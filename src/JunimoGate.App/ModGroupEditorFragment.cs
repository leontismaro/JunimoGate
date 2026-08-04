using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.SwitchMaterial;
using JunimoGate.Android;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.ModGroupEditorFragment")]
public sealed class ModGroupEditorFragment : Fragment
{
    private CancellationTokenSource? cancellation;
    private ModProfileV2Repository? profiles;
    private ModLibraryRepository? library;
    private ProfileId profileId;
    private ModProfileV2? profile;
    private IReadOnlyDictionary<string, ModLibraryItem> libraryItems = new Dictionary<string, ModLibraryItem>();
    private readonly Dictionary<string, EditorSelection> selections = new(StringComparer.OrdinalIgnoreCase);
    private ModGroupMemberAdapter? adapter;
    private EditText? nameInput;
    private EditText? descriptionInput;
    private Spinner? policyInput;
    private SearchView? search;
    private MaterialButton? selectAllButton;
    private MaterialButton? clearButton;
    private MaterialButton? saveButton;
    private LinearProgressIndicator? progress;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mod_group_editor, container, false)
        ?? throw new InvalidOperationException("The Mod group editor layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        nameInput = view.FindViewById<EditText>(Resource.Id.mod_group_name)!;
        descriptionInput = view.FindViewById<EditText>(Resource.Id.mod_group_description)!;
        policyInput = view.FindViewById<Spinner>(Resource.Id.mod_group_policy)!;
        search = view.FindViewById<SearchView>(Resource.Id.mod_group_member_search)!;
        selectAllButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_select_all)!;
        clearButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_clear)!;
        saveButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_save)!;
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mod_group_editor_progress)!;
        var list = view.FindViewById<RecyclerView>(Resource.Id.mod_group_members)!;
        adapter = new ModGroupMemberAdapter(ToggleIncluded, ToggleEnabled);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        policyInput.Adapter = new ArrayAdapter<string>(
            RequireContext(),
            global::Android.Resource.Layout.SimpleSpinnerDropDownItem,
            new[]
            {
                GetString(Resource.String.mod_group_policy_global),
                GetString(Resource.String.binding_highest_compatible),
                GetString(Resource.String.binding_strict),
                GetString(Resource.String.binding_first_loaded),
            });
        search.QueryTextChange += OnSearchChanged;
        selectAllButton.Click += OnSelectAllClicked;
        clearButton.Click += OnClearClicked;
        saveButton.Click += OnSaveClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        var id = Arguments?.GetString("profileId");
        if (!ProfileId.TryParse(id, out profileId))
            throw new InvalidDataException("The Mod group editor profile ID is invalid.");
        cancellation = new CancellationTokenSource();
        var userData = AndroidPrivateStorage.GetUserDataRoot(RequireContext());
        profiles = new ModProfileV2Repository(Path.Combine(userData, "profiles"));
        library = new ModLibraryRepository(Path.Combine(userData, "mods"));
        _ = RefreshAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        profiles = null;
        library = null;
        profile = null;
        libraryItems = new Dictionary<string, ModLibraryItem>();
        selections.Clear();
        SetBusy(false);
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (search is not null)
            search.QueryTextChange -= OnSearchChanged;
        if (selectAllButton is not null)
            selectAllButton.Click -= OnSelectAllClicked;
        if (clearButton is not null)
            clearButton.Click -= OnClearClicked;
        if (saveButton is not null)
            saveButton.Click -= OnSaveClicked;
        nameInput = null;
        descriptionInput = null;
        policyInput = null;
        search = null;
        selectAllButton = null;
        clearButton = null;
        saveButton = null;
        progress = null;
        adapter = null;
        base.OnDestroyView();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (profiles is null || library is null)
            return;
        SetBusy(true);
        try
        {
            var loadedProfile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
            var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
            var byId = index.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
            var rows = BuildRows(loadedProfile, index.Items, byId);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => Render(loadedProfile, byId, rows));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels loading.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Mods", "profile-editor-read-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_group_read_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private IReadOnlyList<ModGroupMemberRow> BuildRows(
        ModProfileV2 loadedProfile,
        IReadOnlyList<ModLibraryItem> items,
        IReadOnlyDictionary<string, ModLibraryItem> byId)
    {
        selections.Clear();
        foreach (var member in loadedProfile.Members)
        {
            selections[member.UniqueId] = new EditorSelection(
                member.UniqueId,
                member.LibraryItemId,
                member.Enabled,
                member.ExpectedName,
                member.ExpectedVersion,
                member.ExpectedAuthor,
                member.AddedAtUtc);
        }
        var rows = items
            .OrderBy(item => item.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Manifest.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LibraryItemId, StringComparer.Ordinal)
            .Select(item => new ModGroupMemberRow(item.Manifest.UniqueId, item, null))
            .ToList();
        foreach (var member in loadedProfile.Members.Where(member =>
                     member.LibraryItemId is null || !byId.ContainsKey(member.LibraryItemId)))
        {
            rows.Add(new ModGroupMemberRow(member.UniqueId, null, member));
        }
        return rows;
    }

    private void Render(
        ModProfileV2 loadedProfile,
        IReadOnlyDictionary<string, ModLibraryItem> byId,
        IReadOnlyList<ModGroupMemberRow> rows)
    {
        profile = loadedProfile;
        libraryItems = byId;
        var locked = loadedProfile.Id == ModProfileV2.NoModsId;
        if (nameInput is not null)
        {
            nameInput.Text = loadedProfile.Id switch
            {
                ModProfileV2.NoModsId => GetString(Resource.String.mods_no_mods_group),
                "default" => GetString(Resource.String.mods_default_group),
                _ => loadedProfile.DisplayName,
            };
            nameInput.Enabled = !locked;
        }
        if (descriptionInput is not null)
        {
            descriptionInput.Text = loadedProfile.Description ?? string.Empty;
            descriptionInput.Enabled = !locked;
        }
        if (policyInput is not null)
        {
            policyInput.SetSelection(loadedProfile.AssemblyBindingPolicyOverride switch
            {
                null => 0,
                ModAssemblyBindingPolicy.HighestCompatible => 1,
                ModAssemblyBindingPolicy.Strict => 2,
                ModAssemblyBindingPolicy.FirstLoaded => 3,
                _ => 0,
            });
            policyInput.Enabled = !locked;
        }
        adapter?.SetRows(rows, selections, locked);
        if (selectAllButton is not null)
            selectAllButton.Enabled = !locked;
        if (clearButton is not null)
            clearButton.Enabled = !locked;
        if (saveButton is not null)
            saveButton.Visibility = locked ? ViewStates.Gone : ViewStates.Visible;
    }

    private void OnSearchChanged(object? sender, SearchView.QueryTextChangeEventArgs eventArgs) =>
        adapter?.SetQuery(eventArgs.NewText);

    private void ToggleIncluded(ModGroupMemberRow row, bool included)
    {
        if (profile?.Id == ModProfileV2.NoModsId)
            return;
        if (included)
        {
            var existing = selections.GetValueOrDefault(row.UniqueId);
            selections[row.UniqueId] = row.Item is { } item
                ? new EditorSelection(
                    row.UniqueId,
                    item.LibraryItemId,
                    existing?.Enabled ?? true,
                    item.Manifest.Name,
                    item.Manifest.Version,
                    item.Manifest.Author,
                    existing?.AddedAtUtc ?? DateTimeOffset.UtcNow)
                : new EditorSelection(
                    row.UniqueId,
                    null,
                    existing?.Enabled ?? row.MissingMember!.Enabled,
                    row.MissingMember!.ExpectedName,
                    row.MissingMember.ExpectedVersion,
                    row.MissingMember.ExpectedAuthor,
                    existing?.AddedAtUtc ?? row.MissingMember.AddedAtUtc);
        }
        else if (selections.TryGetValue(row.UniqueId, out var selected) && RowMatches(row, selected))
        {
            selections.Remove(row.UniqueId);
        }
        adapter?.RefreshSelections();
    }

    private void ToggleEnabled(ModGroupMemberRow row, bool enabled)
    {
        if (selections.TryGetValue(row.UniqueId, out var selected) && RowMatches(row, selected))
        {
            selections[row.UniqueId] = selected with { Enabled = enabled };
            adapter?.RefreshSelections();
        }
    }

    private void OnSelectAllClicked(object? sender, EventArgs eventArgs)
    {
        foreach (var row in adapter?.FilteredRows
                     .GroupBy(row => row.UniqueId, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.FirstOrDefault(row =>
                         selections.TryGetValue(group.Key, out var selected) && RowMatches(row, selected)) ?? group.First())
                 ?? Array.Empty<ModGroupMemberRow>())
        {
            ToggleIncluded(row, included: true);
        }
    }

    private void OnClearClicked(object? sender, EventArgs eventArgs)
    {
        var ids = adapter?.FilteredRows.Select(row => row.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                  ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
            selections.Remove(id);
        adapter?.RefreshSelections();
    }

    private void OnSaveClicked(object? sender, EventArgs eventArgs)
    {
        if (cancellation is { IsCancellationRequested: false } lifetime)
            _ = SaveAsync(lifetime.Token);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (profiles is null || profile is null || nameInput is null || descriptionInput is null || policyInput is null)
            return;
        SetBusy(true);
        try
        {
            var members = selections.Values
                .Select(selection => new ModProfileMember(
                    selection.UniqueId,
                    selection.LibraryItemId,
                    selection.Enabled,
                    selection.ExpectedName,
                    selection.ExpectedVersion,
                    selection.ExpectedAuthor,
                    selection.AddedAtUtc))
                .OrderBy(member => member.ExpectedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.UniqueId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var policy = policyInput.SelectedItemPosition switch
            {
                0 => (ModAssemblyBindingPolicy?)null,
                1 => ModAssemblyBindingPolicy.HighestCompatible,
                2 => ModAssemblyBindingPolicy.Strict,
                3 => ModAssemblyBindingPolicy.FirstLoaded,
                _ => null,
            };
            profile = await profiles.UpdateAsync(
                    profileId,
                    profile.Revision,
                    nameInput.Text ?? string.Empty,
                    descriptionInput.Text,
                    policy,
                    members,
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_group_saved));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels saving.
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-editor-save-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_group_save_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private static bool RowMatches(ModGroupMemberRow row, EditorSelection selection) =>
        row.Item?.LibraryItemId == selection.LibraryItemId &&
        (row.Item is not null || row.MissingMember is not null && selection.LibraryItemId is null);

    private void SetBusy(bool value)
    {
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        if (saveButton is not null)
            saveButton.Enabled = !value;
    }

    private void ShowMessage(int resourceId) =>
        Toast.MakeText(RequireContext(), resourceId, ToastLength.Long)?.Show();
}

internal sealed record ModGroupMemberRow(
    string UniqueId,
    ModLibraryItem? Item,
    ModProfileMember? MissingMember);

internal sealed record EditorSelection(
    string UniqueId,
    string? LibraryItemId,
    bool Enabled,
    string ExpectedName,
    string ExpectedVersion,
    string? ExpectedAuthor,
    DateTimeOffset AddedAtUtc);

internal sealed class ModGroupMemberAdapter(
    Action<ModGroupMemberRow, bool> toggleIncluded,
    Action<ModGroupMemberRow, bool> toggleEnabled) : RecyclerView.Adapter
{
    private IReadOnlyList<ModGroupMemberRow> allRows = Array.Empty<ModGroupMemberRow>();
    private IReadOnlyList<ModGroupMemberRow> rows = Array.Empty<ModGroupMemberRow>();
    private IReadOnlyDictionary<string, EditorSelection> selections = new Dictionary<string, EditorSelection>();
    private bool locked;
    private string query = string.Empty;

    public override int ItemCount => rows.Count;
    public IReadOnlyList<ModGroupMemberRow> FilteredRows => rows;

    public void SetRows(
        IReadOnlyList<ModGroupMemberRow> value,
        IReadOnlyDictionary<string, EditorSelection> selected,
        bool isLocked)
    {
        allRows = value;
        selections = selected;
        locked = isLocked;
        ApplyFilter();
    }

    public void SetQuery(string? value)
    {
        query = value?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    public void RefreshSelections() => NotifyDataSetChanged();

    private void ApplyFilter()
    {
        rows = string.IsNullOrEmpty(query)
            ? allRows
            : allRows.Where(row =>
            {
                var name = row.Item?.Manifest.Name ?? row.MissingMember!.ExpectedName;
                var version = row.Item?.Manifest.Version ?? row.MissingMember!.ExpectedVersion;
                return name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       row.UniqueId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       version.Contains(query, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_group_member, parent, false)
            ?? throw new InvalidOperationException("The Mod group member layout could not be created.");
        return new Holder(view, toggleIncluded, toggleEnabled);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var row = rows[position];
        selections.TryGetValue(row.UniqueId, out var selected);
        ((Holder)holder).Bind(row, selected, locked);
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly CheckBox included;
        private readonly TextView title;
        private readonly TextView summary;
        private readonly SwitchMaterial enabled;
        private readonly Action<ModGroupMemberRow, bool> toggleIncluded;
        private readonly Action<ModGroupMemberRow, bool> toggleEnabled;
        private ModGroupMemberRow? row;
        private bool suppress;

        public Holder(
            View view,
            Action<ModGroupMemberRow, bool> toggleIncluded,
            Action<ModGroupMemberRow, bool> toggleEnabled) : base(view)
        {
            this.toggleIncluded = toggleIncluded;
            this.toggleEnabled = toggleEnabled;
            included = view.FindViewById<CheckBox>(Resource.Id.mod_group_member_included)!;
            title = view.FindViewById<TextView>(Resource.Id.mod_group_member_title)!;
            summary = view.FindViewById<TextView>(Resource.Id.mod_group_member_summary)!;
            enabled = view.FindViewById<SwitchMaterial>(Resource.Id.mod_group_member_enabled)!;
            included.CheckedChange += (_, eventArgs) =>
            {
                if (!suppress && row is not null)
                    this.toggleIncluded(row, eventArgs.IsChecked);
            };
            enabled.CheckedChange += (_, eventArgs) =>
            {
                if (!suppress && row is not null)
                    this.toggleEnabled(row, eventArgs.IsChecked);
            };
        }

        public void Bind(ModGroupMemberRow value, EditorSelection? selected, bool locked)
        {
            row = value;
            var matches = selected is not null &&
                          value.Item?.LibraryItemId == selected.LibraryItemId &&
                          (value.Item is not null || value.MissingMember is not null && selected.LibraryItemId is null);
            suppress = true;
            included.Checked = matches;
            included.Enabled = !locked;
            enabled.Checked = matches && selected!.Enabled;
            enabled.Enabled = !locked && matches;
            title.Text = value.Item is { } item
                ? $"{item.Manifest.Name} {item.Manifest.Version}"
                : $"{value.MissingMember!.ExpectedName} {value.MissingMember.ExpectedVersion}";
            summary.Text = value.Item is { } available
                ? $"{available.Manifest.UniqueId} · {available.Manifest.Author}"
                : $"{value.UniqueId} · missing";
            suppress = false;
        }
    }
}
