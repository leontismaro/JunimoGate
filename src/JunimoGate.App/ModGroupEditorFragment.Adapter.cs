using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Navigation.Fragment;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Android;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;

namespace JunimoGate.App;

internal sealed record ModGroupMemberRow(
    string RowId,
    string DisplayName,
    IReadOnlyList<ModProfileMember> Members,
    IReadOnlyList<ModLibraryItem> Items,
    ModManagementItem? DisplayItem,
    IReadOnlyList<ModDependencyDiagnostic> Dependencies);

internal sealed class ModGroupMemberAdapter(
    Func<ModGroupMemberRow, string> formatSummary,
    Func<ModGroupMemberRow, string> formatComponents,
    Action<ModGroupMemberRow, bool> setEnabled,
    Action<ModGroupMemberRow> remove,
    Action<ModGroupMemberRow> showDependencies,
    Action selectionChanged) : RecyclerView.Adapter
{
    private IReadOnlyList<ModGroupMemberRow> allRows = Array.Empty<ModGroupMemberRow>();
    private IReadOnlyList<ModGroupMemberRow> rows = Array.Empty<ModGroupMemberRow>();
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private bool locked;
    private bool interactionEnabled = true;
    private bool selectionMode;
    private string query = string.Empty;

    public override int ItemCount => rows.Count;
    public int TotalCount => allRows.Count;
    public bool IsSelectionMode => selectionMode;
    public IReadOnlyCollection<string> SelectedUniqueIds => allRows
        .Where(row => selected.Contains(row.RowId))
        .SelectMany(row => row.Members)
        .Select(member => member.UniqueId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    public bool AreAllFilteredSelected => rows.Count > 0 &&
        rows.All(row => selected.Contains(row.RowId));

    public void SetRows(IReadOnlyList<ModGroupMemberRow> value, bool isLocked)
    {
        var presentationChanged = selectionMode || locked != isLocked;
        allRows = value;
        locked = isLocked;
        selected.Clear();
        selectionMode = false;
        ApplyFilter();
        if (presentationChanged && ItemCount > 0)
            NotifyItemRangeChanged(0, ItemCount);
    }

    public void SetQuery(string? value)
    {
        query = value?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    public void EnterSelectionMode()
    {
        selectionMode = true;
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
            foreach (var row in rows)
                selected.Remove(row.RowId);
        }
        else
        {
            foreach (var row in rows)
                selected.Add(row.RowId);
        }
        NotifyDataSetChanged();
        selectionChanged();
    }

    public void SetInteractionEnabled(bool value, RecyclerView? recyclerView)
    {
        if (interactionEnabled == value)
            return;
        interactionEnabled = value;
        if (recyclerView is null)
            return;
        for (var index = 0; index < recyclerView.ChildCount; index++)
        {
            var child = recyclerView.GetChildAt(index);
            if (child is not null && recyclerView.GetChildViewHolder(child) is Holder holder)
                holder.SetInteractionEnabled(!locked && value);
        }
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrEmpty(query)
            ? allRows
            : allRows.Where(row =>
                    Contains(row.DisplayName, query) ||
                    row.Members.Any(member =>
                        Contains(member.ExpectedName, query) ||
                        Contains(member.UniqueId, query) ||
                        Contains(member.ExpectedVersion, query) ||
                        Contains(member.ExpectedAuthor, query)))
                .ToArray();
        selected.RemoveWhere(id => allRows.All(row =>
            row.RowId != id));
        var previous = rows;
        rows = filtered;
        DiffUtil.CalculateDiff(new RowDiff(previous, rows)).DispatchUpdatesTo(this);
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private sealed class RowDiff(
        IReadOnlyList<ModGroupMemberRow> oldRows,
        IReadOnlyList<ModGroupMemberRow> newRows) : DiffUtil.Callback
    {
        public override int OldListSize => oldRows.Count;
        public override int NewListSize => newRows.Count;
        public override bool AreItemsTheSame(int oldItemPosition, int newItemPosition) =>
            oldRows[oldItemPosition].RowId == newRows[newItemPosition].RowId;
        public override bool AreContentsTheSame(int oldItemPosition, int newItemPosition) =>
            oldRows[oldItemPosition] == newRows[newItemPosition];
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_group_member, parent, false)
            ?? throw new InvalidOperationException("The Mod group member layout could not be created.");
        return new Holder(
            view,
            formatSummary,
            formatComponents,
            setEnabled,
            remove,
            showDependencies,
            ToggleSelection);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var row = rows[position];
        ((Holder)holder).Bind(
            row,
            selected.Contains(row.RowId),
            selectionMode,
            locked || !interactionEnabled);
    }

    private void ToggleSelection(ModGroupMemberRow row, bool value)
    {
        if (!selectionMode || locked || !interactionEnabled)
            return;
        if (value)
            selected.Add(row.RowId);
        else
            selected.Remove(row.RowId);
        NotifyDataSetChanged();
        selectionChanged();
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly CheckBox selected;
        private readonly TextView title;
        private readonly TextView summary;
        private readonly TextView components;
        private readonly MaterialSwitch enabled;
        private readonly MaterialButton removeButton;
        private readonly MaterialButton dependenciesButton;
        private readonly Func<ModGroupMemberRow, string> formatSummary;
        private readonly Func<ModGroupMemberRow, string> formatComponents;
        private readonly Action<ModGroupMemberRow, bool> setEnabled;
        private readonly Action<ModGroupMemberRow> remove;
        private readonly Action<ModGroupMemberRow> showDependencies;
        private readonly Action<ModGroupMemberRow, bool> toggleSelection;
        private readonly global::Android.Content.Res.ColorStateList? normalSummaryColors;
        private readonly int missingSummaryColor;
        private ModGroupMemberRow? row;
        private bool selectionMode;
        private bool suppress;

        public Holder(
            View view,
            Func<ModGroupMemberRow, string> formatSummary,
            Func<ModGroupMemberRow, string> formatComponents,
            Action<ModGroupMemberRow, bool> setEnabled,
            Action<ModGroupMemberRow> remove,
            Action<ModGroupMemberRow> showDependencies,
            Action<ModGroupMemberRow, bool> toggleSelection) : base(view)
        {
            this.formatSummary = formatSummary;
            this.formatComponents = formatComponents;
            this.setEnabled = setEnabled;
            this.remove = remove;
            this.showDependencies = showDependencies;
            this.toggleSelection = toggleSelection;
            selected = view.FindViewById<CheckBox>(Resource.Id.mod_group_member_selected)!;
            selected.Clickable = false;
            selected.Focusable = false;
            title = view.FindViewById<TextView>(Resource.Id.mod_group_member_title)!;
            summary = view.FindViewById<TextView>(Resource.Id.mod_group_member_summary)!;
            components = view.FindViewById<TextView>(Resource.Id.mod_group_member_components)!;
            normalSummaryColors = summary.TextColors;
            missingSummaryColor = Google.Android.Material.Color.MaterialColors.GetColor(
                view,
                Resource.Attribute.colorError);
            enabled = view.FindViewById<MaterialSwitch>(Resource.Id.mod_group_member_enabled)!;
            removeButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_member_remove)!;
            dependenciesButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_member_dependencies)!;
            enabled.CheckedChange += (_, eventArgs) =>
            {
                if (!suppress && row is not null)
                    this.setEnabled(row, eventArgs.IsChecked);
            };
            removeButton.Click += (_, _) =>
            {
                if (row is not null)
                    this.remove(row);
            };
            dependenciesButton.Click += (_, _) =>
            {
                if (row is not null)
                    this.showDependencies(row);
            };
            ItemView.Click += (_, _) =>
            {
                if (selectionMode && row is not null)
                    this.toggleSelection(row, !selected.Checked);
            };
        }

        public void Bind(ModGroupMemberRow value, bool isSelected, bool isSelectionMode, bool locked)
        {
            row = value;
            selectionMode = isSelectionMode;
            suppress = true;
            selected.Visibility = isSelectionMode ? ViewStates.Visible : ViewStates.Gone;
            selected.Checked = isSelected;
            selected.Enabled = !locked;
            enabled.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            removeButton.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            dependenciesButton.Visibility = !isSelectionMode && value.Dependencies.Count > 0
                ? ViewStates.Visible
                : ViewStates.Gone;
            enabled.Checked = value.Members.All(member => member.Enabled);
            enabled.Enabled = !locked;
            removeButton.Enabled = !locked;
            dependenciesButton.Enabled = !locked;
            title.Text = value.DisplayItem?.IsBundle == true
                ? value.DisplayName
                : $"{value.Members[0].ExpectedName} {value.Members[0].ExpectedVersion}";
            summary.Text = formatSummary(value);
            var componentText = formatComponents(value);
            components.Text = componentText;
            components.Visibility = componentText.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
            if (value.Items.Count != value.Members.Count)
                summary.SetTextColor(new global::Android.Graphics.Color(missingSummaryColor));
            else if (normalSummaryColors is not null)
                summary.SetTextColor(normalSummaryColors);
            var alpha = value.Items.Count != value.Members.Count ? 0.58f : 1f;
            title.Alpha = alpha;
            summary.Alpha = alpha;
            suppress = false;
        }

        public void SetInteractionEnabled(bool value)
        {
            selected.Enabled = value;
            enabled.Enabled = value;
            removeButton.Enabled = value;
            dependenciesButton.Enabled = value;
        }
    }
}
