using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Text.Format;
using Android.Views;
using Android.Widget;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Navigation.Fragment;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Android;
using JunimoGate.GameHost;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;

namespace JunimoGate.App;

internal sealed class ModLibraryAdapter(
    Func<ModManagementItem, int, string> formatSummary,
    Func<ModManagementItem, string> formatMetadata,
    Action<ModManagementItem> showDetails,
    Action<ModManagementItem> showFiles,
    Action<ModManagementItem> installTranslation,
    Action<ModManagementItem> addToGroup,
    Action<ModManagementItem> delete,
    Action<ModManagementItem> export,
    Action<ModManagementItem, ModLibraryItem> unlock,
    Action<ModManagementItem> restore,
    Action selectionChanged) : RecyclerView.Adapter
{
    private IReadOnlyList<ModManagementItem> allItems = Array.Empty<ModManagementItem>();
    private IReadOnlyList<ModManagementItem> items = Array.Empty<ModManagementItem>();
    private IReadOnlyDictionary<string, int> versionCounts = new Dictionary<string, int>();
    private IReadOnlySet<string> translatedItemIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private bool selectionMode;
    private bool interactionEnabled = true;
    private string query = string.Empty;
    private string? expandedItemId;

    public override int ItemCount => items.Count;
    public int TotalCount => allItems.Count;
    public bool IsSelectionMode => selectionMode;
    public int SelectedEntryCount => allItems.Count(item => selected.Contains(item.ItemId));
    public IReadOnlyList<ModLibraryItem> SelectedItems => allItems
        .Where(item => selected.Contains(item.ItemId))
        .SelectMany(item => item.Members)
        .DistinctBy(item => item.LibraryItemId, StringComparer.Ordinal)
        .ToArray();
    public bool AreAllFilteredSelected => items.Count > 0 &&
        items.All(item => selected.Contains(item.ItemId));

    public void SetItems(IReadOnlyList<ModManagementItem> value, IReadOnlySet<string>? translatedIds = null)
    {
        var translationStateChanged = translatedIds is not null &&
            !translatedItemIds.SetEquals(translatedIds);
        if (translatedIds is not null)
            translatedItemIds = new HashSet<string>(translatedIds, StringComparer.Ordinal);
        allItems = value;
        selected.RemoveWhere(id => value.All(item => item.ItemId != id));
        versionCounts = value
            .SelectMany(item => item.Members)
            .GroupBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        ApplyFilter();
        if (translationStateChanged)
            NotifyDataSetChanged();
    }

    public void SetQuery(string? value)
    {
        query = value?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    public void EnterSelectionMode()
    {
        selectionMode = true;
        expandedItemId = null;
        selected.Clear();
        NotifyDataSetChanged();
        selectionChanged();
    }

    public void ExitSelectionMode()
    {
        if (!selectionMode && selected.Count == 0)
            return;
        selectionMode = false;
        selected.Clear();
        NotifyDataSetChanged();
        selectionChanged();
    }

    public void SelectAllFiltered()
    {
        if (!selectionMode)
            return;
        if (AreAllFilteredSelected)
        {
            foreach (var item in items)
                selected.Remove(item.ItemId);
        }
        else
        {
            foreach (var item in items)
                selected.Add(item.ItemId);
        }
        NotifyDataSetChanged();
        selectionChanged();
    }

    public void SetInteractionEnabled(bool value)
    {
        interactionEnabled = value;
        NotifyDataSetChanged();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrEmpty(query)
            ? allItems
            : allItems.Where(item =>
                    item.SearchTerms.Any(term => Contains(term, query)) ||
                    item.Members.Any(member => Contains(member.Manifest.Version, query)))
                .ToArray();
        if (expandedItemId is not null && filtered.All(item => item.ItemId != expandedItemId))
            expandedItemId = null;
        var oldItems = items;
        items = filtered;
        DiffUtil.CalculateDiff(new ItemDiff(oldItems, items)).DispatchUpdatesTo(this);
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_library, parent, false)
            ?? throw new InvalidOperationException("The Mod library item layout could not be created.");
        return new ModLibraryViewHolder(
            view,
            formatMetadata,
            showDetails,
            showFiles,
            installTranslation,
            addToGroup,
            delete,
            export,
            unlock,
            restore,
            ToggleSelection,
            ToggleExpanded);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = items[position];
        ((ModLibraryViewHolder)holder).Bind(
            item,
            formatSummary(item, item.IsBundle ? 1 : versionCounts[item.Members[0].Manifest.UniqueId]),
            selected.Contains(item.ItemId),
            selectionMode,
            interactionEnabled,
            item.ItemId == expandedItemId,
            item.Members.Any(member => translatedItemIds.Contains(member.LibraryItemId)));
    }

    private void ToggleSelection(ModManagementItem item, bool value)
    {
        if (!selectionMode || !interactionEnabled)
            return;
        if (value)
            selected.Add(item.ItemId);
        else
            selected.Remove(item.ItemId);
        NotifyDataSetChanged();
        selectionChanged();
    }

    private void ToggleExpanded(ModManagementItem item)
    {
        if (selectionMode || !interactionEnabled)
            return;
        var previous = expandedItemId;
        expandedItemId = previous == item.ItemId ? null : item.ItemId;
        NotifyItem(previous);
        NotifyItem(expandedItemId);
    }

    private void NotifyItem(string? itemId)
    {
        if (itemId is null)
            return;
        var index = items.ToList().FindIndex(item => item.ItemId == itemId);
        if (index >= 0)
            NotifyItemChanged(index);
    }

    private sealed class ItemDiff(
        IReadOnlyList<ModManagementItem> oldItems,
        IReadOnlyList<ModManagementItem> newItems) : DiffUtil.Callback
    {
        public override int OldListSize => oldItems.Count;
        public override int NewListSize => newItems.Count;
        public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
            oldItems[oldItemPosition].ItemId == newItems[newItemPosition].ItemId;
        public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) =>
            oldItems[oldItemPosition] == newItems[newItemPosition];
    }

    private sealed class ModLibraryViewHolder : RecyclerView.ViewHolder
    {
        private const int MoreActionExport = 1;
        private const int MoreActionRestore = 2;
        private const int MoreActionDelete = 3;
        private readonly TextView title;
        private readonly TextView summary;
        private readonly TextView description;
        private readonly TextView metadata;
        private readonly LinearLayout components;
        private readonly ImageView expand;
        private readonly View expanded;
        private readonly CheckBox selected;
        private readonly MaterialButton addButton;
        private readonly MaterialButton detailsButton;
        private readonly MaterialButton filesButton;
        private readonly MaterialButton translationButton;
        private readonly MaterialButton moreButton;
        private readonly Action<ModManagementItem> showDetails;
        private readonly Action<ModManagementItem> showFiles;
        private readonly Action<ModManagementItem> installTranslation;
        private readonly Action<ModManagementItem> addToGroup;
        private readonly Action<ModManagementItem> delete;
        private readonly Action<ModManagementItem> export;
        private readonly Action<ModManagementItem, ModLibraryItem> unlock;
        private readonly Action<ModManagementItem> restore;
        private readonly Action<ModManagementItem, bool> toggleSelection;
        private readonly Action<ModManagementItem> toggleExpanded;
        private readonly Func<ModManagementItem, string> formatMetadata;
        private ModManagementItem? item;
        private bool selectionMode;

        public ModLibraryViewHolder(
            View view,
            Func<ModManagementItem, string> formatMetadata,
            Action<ModManagementItem> showDetails,
            Action<ModManagementItem> showFiles,
            Action<ModManagementItem> installTranslation,
            Action<ModManagementItem> addToGroup,
            Action<ModManagementItem> delete,
            Action<ModManagementItem> export,
            Action<ModManagementItem, ModLibraryItem> unlock,
            Action<ModManagementItem> restore,
            Action<ModManagementItem, bool> toggleSelection,
            Action<ModManagementItem> toggleExpanded) : base(view)
        {
            this.formatMetadata = formatMetadata;
            this.showDetails = showDetails;
            this.showFiles = showFiles;
            this.installTranslation = installTranslation;
            this.addToGroup = addToGroup;
            this.delete = delete;
            this.export = export;
            this.unlock = unlock;
            this.restore = restore;
            this.toggleSelection = toggleSelection;
            this.toggleExpanded = toggleExpanded;
            title = view.FindViewById<TextView>(Resource.Id.mod_item_title)
                ?? throw new InvalidOperationException("The Mod item title is unavailable.");
            summary = view.FindViewById<TextView>(Resource.Id.mod_item_summary)
                ?? throw new InvalidOperationException("The Mod item summary is unavailable.");
            description = view.FindViewById<TextView>(Resource.Id.mod_item_description)
                ?? throw new InvalidOperationException("The Mod item description is unavailable.");
            metadata = view.FindViewById<TextView>(Resource.Id.mod_item_metadata)
                ?? throw new InvalidOperationException("The Mod item metadata is unavailable.");
            components = view.FindViewById<LinearLayout>(Resource.Id.mod_item_components)
                ?? throw new InvalidOperationException("The Mod item component list is unavailable.");
            expand = view.FindViewById<ImageView>(Resource.Id.mod_item_expand)
                ?? throw new InvalidOperationException("The Mod item expand affordance is unavailable.");
            expanded = view.FindViewById<View>(Resource.Id.mod_item_expanded)
                ?? throw new InvalidOperationException("The Mod item expanded area is unavailable.");
            selected = view.FindViewById<CheckBox>(Resource.Id.mod_item_selected)
                ?? throw new InvalidOperationException("The Mod item selection is unavailable.");
            selected.Clickable = false;
            selected.Focusable = false;
            addButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_add)
                ?? throw new InvalidOperationException("The Mod item add action is unavailable.");
            detailsButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_details)
                ?? throw new InvalidOperationException("The Mod item details button is unavailable.");
            filesButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_files)
                ?? throw new InvalidOperationException("The Mod item files button is unavailable.");
            translationButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_translation)
                ?? throw new InvalidOperationException("The Mod item translation button is unavailable.");
            moreButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_more)
                ?? throw new InvalidOperationException("The Mod item more action is unavailable.");
            detailsButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.showDetails(item);
            };
            filesButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.showFiles(item);
            };
            translationButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.installTranslation(item);
            };
            addButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.addToGroup(item);
            };
            moreButton.Click += (_, _) => ShowMoreActions();
            ItemView.Click += (_, _) =>
            {
                if (selectionMode && item is not null)
                    this.toggleSelection(item, !selected.Checked);
                else if (item is not null)
                    this.toggleExpanded(item);
            };
        }

        public void Bind(
            ModManagementItem value,
            string detail,
            bool isSelected,
            bool isSelectionMode,
            bool interactionEnabled,
            bool isExpanded,
            bool hasTranslation)
        {
            item = value;
            selectionMode = isSelectionMode;
            title.Text = value.DisplayName;
            summary.Text = detail;
            description.Text = value.IsBundle
                ? ItemView.Context?.GetString(Resource.String.mods_bundle_description)
                : value.Members[0].Manifest.Description ?? ItemView.Context?.GetString(Resource.String.mods_no_description);
            metadata.Text = formatMetadata(value);
            BindComponents(value, interactionEnabled);
            selected.Visibility = isSelectionMode ? ViewStates.Visible : ViewStates.Gone;
            selected.Checked = isSelected;
            selected.Enabled = interactionEnabled;
            addButton.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            detailsButton.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            filesButton.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            translationButton.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            moreButton.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            expanded.Visibility = !isSelectionMode && isExpanded ? ViewStates.Visible : ViewStates.Gone;
            expand.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            expand.Rotation = isExpanded ? 180f : 0f;
            addButton.Enabled = interactionEnabled;
            detailsButton.Enabled = interactionEnabled;
            filesButton.Enabled = interactionEnabled;
            translationButton.Enabled = interactionEnabled;
            translationButton.Selected = hasTranslation;
            translationButton.ContentDescription = ItemView.Context?.GetString(hasTranslation
                ? Resource.String.mod_translation_action_installed
                : Resource.String.mod_translation_action);
            moreButton.Enabled = interactionEnabled;
        }

        private void ShowMoreActions()
        {
            if (item is not { } value || !moreButton.Enabled)
                return;
            var context = ItemView.Context
                ?? throw new InvalidOperationException("The Mod item context is unavailable.");
            var menu = new PopupMenu(context, moreButton);
            var popup = menu.Menu
                ?? throw new InvalidOperationException("The Mod item action menu is unavailable.");
            var order = 0;
            if (value.IsBundle)
                _ = popup.Add(0, MoreActionExport, order++, Resource.String.mods_bundle_export);
            if (value.RestorableBundle is not null)
                _ = popup.Add(0, MoreActionRestore, order++, Resource.String.mods_bundle_restore);
            _ = popup.Add(0, MoreActionDelete, order, Resource.String.mods_delete_action);
            menu.MenuItemClick += (_, eventArgs) =>
            {
                eventArgs.Handled = eventArgs.Item?.ItemId switch
                {
                    MoreActionExport => HandleMoreAction(this.export),
                    MoreActionRestore => HandleMoreAction(this.restore),
                    MoreActionDelete => HandleMoreAction(this.delete),
                    _ => false,
                };
            };
            menu.Show();
        }

        private bool HandleMoreAction(Action<ModManagementItem> action)
        {
            if (item is null)
                return false;
            action(item);
            return true;
        }

        private void BindComponents(ModManagementItem value, bool interactionEnabled)
        {
            components.RemoveAllViews();
            components.Visibility = value.IsBundle ? ViewStates.Visible : ViewStates.Gone;
            if (!value.IsBundle)
                return;
            foreach (var member in value.Members)
            {
                var context = ItemView.Context
                    ?? throw new InvalidOperationException("The Mod item context is unavailable.");
                var row = new LinearLayout(context)
                {
                    Orientation = Orientation.Horizontal,
                };
                row.SetGravity(GravityFlags.CenterVertical);
                var text = new TextView(context)
                {
                    Text = $"{member.Manifest.Name} {member.Manifest.Version}",
                };
                row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
                var button = new MaterialButton(context)
                {
                    Text = string.Empty,
                    ContentDescription = context.Resources?.GetString(
                        Resource.String.mods_bundle_unlock_description,
                        [new JString(member.Manifest.Name)]),
                    TooltipText = context.GetString(Resource.String.mods_bundle_unlock),
                    IconPadding = 0,
                    Enabled = interactionEnabled,
                };
                var size = (int)Math.Round(48 * context.Resources!.DisplayMetrics!.Density);
                var padding = (int)Math.Round(12 * context.Resources.DisplayMetrics.Density);
                button.SetIconResource(Resource.Drawable.ic_lock_open_24);
                button.SetMinWidth(0);
                button.SetPadding(padding, padding, padding, padding);
                button.Click += (_, _) => unlock(value, member);
                row.AddView(button, new LinearLayout.LayoutParams(
                    size,
                    size));
                components.AddView(row);
            }
        }
    }
}
