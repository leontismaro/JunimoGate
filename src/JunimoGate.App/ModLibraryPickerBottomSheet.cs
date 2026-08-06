using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.ModLibraryPickerBottomSheet")]
public sealed class ModLibraryPickerBottomSheet : BottomSheetDialogFragment
{
    internal const string FragmentTag = "mod-library-picker";
    private const string ProfileArgument = "profileId";
    private CancellationTokenSource? cancellation;
    private ModPickerAdapter? adapter;
    private RecyclerView? list;
    private SearchView? search;
    private LinearProgressIndicator? progress;
    private TextView? empty;
    private MaterialButton? addButton;
    private ModManagementUiSession? session;
    private ProfileId profileId;
    private ModProfileV2? profile;

    internal static ModLibraryPickerBottomSheet New(string profileId)
    {
        var fragment = new ModLibraryPickerBottomSheet();
        var arguments = new Bundle();
        arguments.PutString(ProfileArgument, profileId);
        fragment.Arguments = arguments;
        return fragment;
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mod_library_picker, container, false)
        ?? throw new InvalidOperationException("The Mod picker layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        search = view.FindViewById<SearchView>(Resource.Id.mod_picker_search)!;
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mod_picker_progress)!;
        empty = view.FindViewById<TextView>(Resource.Id.mod_picker_empty)!;
        addButton = view.FindViewById<MaterialButton>(Resource.Id.mod_picker_add)!;
        list = view.FindViewById<RecyclerView>(Resource.Id.mod_picker_list)!;
        adapter = new ModPickerAdapter(UpdateSelectionState);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        search.QueryTextChange += OnSearchChanged;
        addButton.Click += OnAddClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (!ProfileId.TryParse(Arguments?.GetString(ProfileArgument), out profileId))
            throw new InvalidDataException("The Mod picker profile ID is invalid.");
        cancellation = new CancellationTokenSource();
        session = ((MainActivity)RequireActivity()).ModManagement;
        _ = LoadAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        session = null;
        profile = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (search is not null)
            search.QueryTextChange -= OnSearchChanged;
        if (addButton is not null)
            addButton.Click -= OnAddClicked;
        list?.SetAdapter(null);
        search = null;
        progress = null;
        empty = null;
        addButton = null;
        list = null;
        adapter = null;
        base.OnDestroyView();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (session is null)
            return;
        try
        {
            var loadedProfile = await session.Profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
            var library = await session.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
            var members = loadedProfile.Members.ToDictionary(member => member.UniqueId, StringComparer.OrdinalIgnoreCase);
            var rows = ModManagementProjection.Create(library).Items
                .Select(item => new ModPickerRow(
                    item,
                    item.Members.All(candidate =>
                        members.TryGetValue(candidate.Manifest.UniqueId, out var member) &&
                        member.LibraryItemId == candidate.LibraryItemId)))
                .ToArray();
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                profile = loadedProfile;
                adapter?.SetRows(rows);
                if (progress is not null)
                    progress.Visibility = ViewStates.Gone;
                UpdateSelectionState();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Mods", "mod-picker-load-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.mod_group_read_failed, ToastLength.Long)?.Show());
        }
    }

    private void OnSearchChanged(object? sender, SearchView.QueryTextChangeEventArgs eventArgs)
    {
        adapter?.SetQuery(eventArgs.NewText);
        UpdateSelectionState();
    }

    private void OnAddClicked(object? sender, EventArgs eventArgs)
    {
        var selected = adapter?.SelectedItems ?? Array.Empty<ModLibraryItem>();
        if (selected.Count == 0 || profile is null)
            return;
        var replacements = selected.Count(item => profile.Members.Any(member =>
            member.UniqueId.Equals(item.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase) &&
            member.LibraryItemId != item.LibraryItemId));
        if (replacements == 0)
        {
            _ = AddAsync(selected);
            return;
        }
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mods_replace_versions_title);
        dialog.SetMessage(GetString(Resource.String.mod_picker_replace_message, replacements));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mods_replace_versions_action, (_, _) => _ = AddAsync(selected));
        dialog.Show();
    }

    private async Task AddAsync(IReadOnlyList<ModLibraryItem> selected)
    {
        if (session is null || cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        SetBusy(true);
        try
        {
            await session.MemberMutations.AddOrReplaceAsync(profileId, selected, enabled: true, lifetime.Token)
                .ConfigureAwait(false);
            session.NotifyProfilesChanged();
            if (IsAdded)
                Activity?.RunOnUiThread(DismissAllowingStateLoss);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "mod-picker-add-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.mods_add_to_group_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void UpdateSelectionState()
    {
        var count = adapter?.SelectedEntryCount ?? 0;
        if (addButton is not null)
        {
            addButton.Enabled = count > 0;
            addButton.Text = GetString(Resource.String.mod_picker_add_count, count);
        }
        if (empty is not null && adapter is not null)
            empty.Visibility = adapter.ItemCount == 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private void SetBusy(bool value)
    {
        if (addButton is not null)
            addButton.Enabled = !value && (adapter?.SelectedItems.Count ?? 0) > 0;
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        adapter?.SetEnabled(!value);
    }
}

internal sealed record ModPickerRow(ModManagementItem Item, bool AlreadyAdded);

internal sealed class ModPickerAdapter(Action selectionChanged) : RecyclerView.Adapter
{
    private IReadOnlyList<ModPickerRow> allRows = Array.Empty<ModPickerRow>();
    private IReadOnlyList<ModPickerRow> rows = Array.Empty<ModPickerRow>();
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private string query = string.Empty;
    private bool enabled = true;

    public override int ItemCount => rows.Count;
    public int SelectedEntryCount => selected.Count;
    public IReadOnlyList<ModLibraryItem> SelectedItems => allRows
        .Where(row => selected.Contains(row.Item.ItemId))
        .SelectMany(row => row.Item.Members)
        .ToArray();

    public void SetRows(IReadOnlyList<ModPickerRow> value)
    {
        allRows = value;
        ApplyFilter();
    }

    public void SetQuery(string? value)
    {
        query = value?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    public void SetEnabled(bool value)
    {
        enabled = value;
        NotifyDataSetChanged();
    }

    private void ApplyFilter()
    {
        rows = string.IsNullOrEmpty(query)
            ? allRows
            : allRows.Where(row => row.Item.SearchTerms.Any(term =>
                                       term.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                                   row.Item.Members.Any(item =>
                                       item.Manifest.Version.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_library_picker, parent, false)
            ?? throw new InvalidOperationException("The Mod picker row could not be created.");
        return new Holder(view, Toggle);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var row = rows[position];
        ((Holder)holder).Bind(
            row,
            selected.Contains(row.Item.ItemId),
            enabled);
    }

    private void Toggle(ModPickerRow row, bool value)
    {
        if (!enabled || row.AlreadyAdded)
            return;
        if (value)
        {
            var ids = row.Item.Members
                .Select(item => item.Manifest.UniqueId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected.RemoveWhere(itemId => allRows.Any(candidate =>
                candidate.Item.ItemId == itemId &&
                candidate.Item.Members.Any(item => ids.Contains(item.Manifest.UniqueId))));
            selected.Add(row.Item.ItemId);
        }
        else
            selected.Remove(row.Item.ItemId);
        NotifyDataSetChanged();
        selectionChanged();
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly CheckBox check;
        private readonly Action<ModPickerRow, bool> toggle;
        private ModPickerRow? row;
        private bool suppress;

        public Holder(View view, Action<ModPickerRow, bool> toggle) : base(view)
        {
            this.toggle = toggle;
            check = view.FindViewById<CheckBox>(Resource.Id.mod_picker_item_check)!;
            check.CheckedChange += (_, eventArgs) =>
            {
                if (!suppress && row is not null)
                    this.toggle(row, eventArgs.IsChecked);
            };
        }

        public void Bind(ModPickerRow value, bool selected, bool enabled)
        {
            row = value;
            suppress = true;
            var versions = string.Join(", ", value.Item.Members
                .Select(item => item.Manifest.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var authors = string.Join(", ", value.Item.Members
                .Select(item => item.Manifest.Author)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            check.Text = $"{value.Item.DisplayName} {versions} · {authors}" +
                         (value.AlreadyAdded ? $" · {ItemView.Context?.GetString(Resource.String.mod_picker_already_added)}" : string.Empty);
            check.Checked = value.AlreadyAdded || selected;
            check.Enabled = enabled && !value.AlreadyAdded;
            suppress = false;
        }
    }
}
