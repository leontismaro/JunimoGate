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

[Register("org.junimogate.app.ModGroupEditorFragment")]
public sealed class ModGroupEditorFragment : Fragment
{
    private CancellationTokenSource? cancellation;
    private ModProfileV2Repository? profiles;
    private ModLibraryRepository? library;
    private ModProfileMemberMutationService? mutations;
    private ModManagementUiSession? modManagement;
    private ProfileId profileId;
    private ModProfileV2? profile;
    private IReadOnlyDictionary<string, ModLibraryItem> libraryItems =
        new Dictionary<string, ModLibraryItem>();
    private ModLibraryIndex? librarySnapshot;
    private ModGroupMemberAdapter? adapter;
    private SearchView? search;
    private MaterialButton? infoButton;
    private MaterialButton? addButton;
    private MaterialButton? batchButton;
    private View? batchBar;
    private TextView? selectedCount;
    private MaterialButton? selectAllButton;
    private MaterialButton? enableButton;
    private MaterialButton? disableButton;
    private MaterialButton? removeButton;
    private MaterialButton? batchDoneButton;
    private LinearProgressIndicator? progress;
    private LinearLayout? empty;
    private TextView? emptyText;
    private MaterialButton? emptyAddButton;
    private MainShellFragment? mainShell;
    private RecyclerView? list;
    private bool busy;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mod_group_editor, container, false)
        ?? throw new InvalidOperationException("The Mod group editor layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        search = view.FindViewById<SearchView>(Resource.Id.mod_group_member_search)!;
        infoButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_info)!;
        addButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_add)!;
        batchButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_batch)!;
        batchBar = view.FindViewById<View>(Resource.Id.mod_group_batch_bar)!;
        selectedCount = view.FindViewById<TextView>(Resource.Id.mod_group_selected_count)!;
        selectAllButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_select_all)!;
        enableButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_enable_selected)!;
        disableButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_disable_selected)!;
        removeButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_remove_selected)!;
        batchDoneButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_batch_done)!;
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mod_group_editor_progress)!;
        empty = view.FindViewById<LinearLayout>(Resource.Id.mod_group_editor_empty)!;
        emptyText = view.FindViewById<TextView>(Resource.Id.mod_group_empty_text)!;
        emptyAddButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_empty_add)!;
        list = view.FindViewById<RecyclerView>(Resource.Id.mod_group_members)!;
        adapter = new ModGroupMemberAdapter(
            FormatMemberSummary,
            FormatMemberComponents,
            RequestSetEnabled,
            RequestRemove,
            ShowDependencies,
            UpdateSelectionState);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);

        search.QueryTextChange += OnSearchChanged;
        infoButton.Click += OnInfoClicked;
        addButton.Click += OnAddClicked;
        emptyAddButton.Click += OnAddClicked;
        batchButton.Click += OnBatchClicked;
        selectAllButton.Click += OnSelectAllClicked;
        enableButton.Click += OnEnableSelectedClicked;
        disableButton.Click += OnDisableSelectedClicked;
        removeButton.Click += OnRemoveSelectedClicked;
        batchDoneButton.Click += OnBatchDoneClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        var id = Arguments?.GetString("profileId");
        if (!ProfileId.TryParse(id, out profileId))
            throw new InvalidDataException("The Mod group editor profile ID is invalid.");
        cancellation = new CancellationTokenSource();
        modManagement = ((MainActivity)RequireActivity()).ModManagement;
        modManagement.Changed += OnModManagementChanged;
        profiles = modManagement.Profiles;
        library = modManagement.Library;
        mutations = modManagement.Commands.ProfileMembers;
        AttachToolbar();
        _ = RefreshAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        if (modManagement is not null)
            modManagement.Changed -= OnModManagementChanged;
        modManagement = null;
        profiles = null;
        library = null;
        mutations = null;
        profile = null;
        libraryItems = new Dictionary<string, ModLibraryItem>();
        librarySnapshot = null;
        busy = false;
        DetachToolbar();
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (search is not null)
            search.QueryTextChange -= OnSearchChanged;
        if (infoButton is not null)
            infoButton.Click -= OnInfoClicked;
        if (addButton is not null)
            addButton.Click -= OnAddClicked;
        if (emptyAddButton is not null)
            emptyAddButton.Click -= OnAddClicked;
        if (batchButton is not null)
            batchButton.Click -= OnBatchClicked;
        if (selectAllButton is not null)
            selectAllButton.Click -= OnSelectAllClicked;
        if (enableButton is not null)
            enableButton.Click -= OnEnableSelectedClicked;
        if (disableButton is not null)
            disableButton.Click -= OnDisableSelectedClicked;
        if (removeButton is not null)
            removeButton.Click -= OnRemoveSelectedClicked;
        if (batchDoneButton is not null)
            batchDoneButton.Click -= OnBatchDoneClicked;
        list?.SetAdapter(null);
        search = null;
        infoButton = null;
        addButton = null;
        emptyAddButton = null;
        batchButton = null;
        batchBar = null;
        selectedCount = null;
        selectAllButton = null;
        enableButton = null;
        disableButton = null;
        removeButton = null;
        batchDoneButton = null;
        progress = null;
        empty = null;
        emptyText = null;
        list = null;
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
            var index = modManagement is null
                ? await library.ReadAsync(cancellationToken).ConfigureAwait(false)
                : await modManagement.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
            var byId = index.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                libraryItems = byId;
                librarySnapshot = index;
                RenderProfile(loadedProfile);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the editor cancels its initial read.
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

    private void RenderProfile(ModProfileV2 value)
    {
        profile = value;
        mainShell?.SetEditorTitle(GetDisplayName(value));
        var rows = BuildRows(value);
        var locked = value.Id == ModProfileV2.NoModsId;
        adapter?.SetRows(rows, locked);
        if (infoButton is not null)
            infoButton.Visibility = locked ? ViewStates.Gone : ViewStates.Visible;
        if (addButton is not null)
            addButton.Visibility = locked ? ViewStates.Gone : ViewStates.Visible;
        if (batchButton is not null)
            batchButton.Visibility = locked || rows.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
        ExitBatchMode();
        UpdateEmptyState();
    }

    private void OnSearchChanged(object? sender, SearchView.QueryTextChangeEventArgs eventArgs)
    {
        adapter?.SetQuery(eventArgs.NewText);
        UpdateSelectionState();
        UpdateEmptyState();
    }

    private void OnInfoClicked(object? sender, EventArgs eventArgs)
    {
        if (busy || profile is null || profile.Id == ModProfileV2.NoModsId)
            return;
        var container = new LinearLayout(RequireContext())
        {
            Orientation = Orientation.Vertical,
        };
        var padding = Dp(24);
        container.SetPadding(padding, 0, padding, 0);
        var name = new EditText(RequireContext())
        {
            Hint = GetString(Resource.String.mod_groups_name_hint),
            Text = profile.DisplayName,
        };
        name.SetSingleLine(true);
        var description = new EditText(RequireContext())
        {
            Hint = GetString(Resource.String.mod_group_description_hint),
            Text = profile.Description ?? string.Empty,
        };
        description.SetSingleLine(false);
        description.SetMaxLines(3);
        var policy = new Spinner(RequireContext());
        policy.Adapter = new ArrayAdapter<string>(
            RequireContext(),
            global::Android.Resource.Layout.SimpleSpinnerDropDownItem,
            new[]
            {
                GetString(Resource.String.mod_group_policy_global),
                GetString(Resource.String.binding_highest_compatible),
                GetString(Resource.String.binding_strict),
                GetString(Resource.String.binding_first_loaded),
            });
        policy.SetSelection(profile.AssemblyBindingPolicyOverride switch
        {
            null => 0,
            ModAssemblyBindingPolicy.HighestCompatible => 1,
            ModAssemblyBindingPolicy.Strict => 2,
            ModAssemblyBindingPolicy.FirstLoaded => 3,
            _ => 0,
        });
        container.AddView(name);
        container.AddView(description);
        container.AddView(policy);

        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_group_info);
        dialog.SetView(container);
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.action_done, (_, _) =>
        {
            var selectedPolicy = policy.SelectedItemPosition switch
            {
                1 => ModAssemblyBindingPolicy.HighestCompatible,
                2 => ModAssemblyBindingPolicy.Strict,
                3 => ModAssemblyBindingPolicy.FirstLoaded,
                _ => (ModAssemblyBindingPolicy?)null,
            };
            _ = SaveMetadataAsync(name.Text ?? string.Empty, description.Text, selectedPolicy);
        });
        dialog.Show();
    }

    private void OnAddClicked(object? sender, EventArgs eventArgs)
    {
        if (busy || profile?.Id == ModProfileV2.NoModsId)
            return;
        ModLibraryPickerBottomSheet.New(profileId.Value)
            .Show(ChildFragmentManager, ModLibraryPickerBottomSheet.FragmentTag);
    }

    private void OnBatchClicked(object? sender, EventArgs eventArgs)
    {
        if (busy || adapter is null)
            return;
        if (adapter.IsSelectionMode)
        {
            ExitBatchMode();
            return;
        }
        adapter.EnterSelectionMode();
        if (batchBar is not null)
            batchBar.Visibility = ViewStates.Visible;
        RenderBatchToggle();
        UpdateSelectionState();
    }

    private void OnBatchDoneClicked(object? sender, EventArgs eventArgs) => ExitBatchMode();

    private void OnSelectAllClicked(object? sender, EventArgs eventArgs) => adapter?.SelectAllFiltered();

    private void OnEnableSelectedClicked(object? sender, EventArgs eventArgs) =>
        RequestSetEnabled(adapter?.SelectedUniqueIds ?? Array.Empty<string>(), enabled: true);

    private void OnDisableSelectedClicked(object? sender, EventArgs eventArgs) =>
        RequestSetEnabled(adapter?.SelectedUniqueIds ?? Array.Empty<string>(), enabled: false);

    private void OnRemoveSelectedClicked(object? sender, EventArgs eventArgs)
    {
        var ids = adapter?.SelectedUniqueIds ?? Array.Empty<string>();
        if (ids.Count == 0)
            return;
        ConfirmRemove(ids);
    }

    private void RequestSetEnabled(ModGroupMemberRow row, bool enabled) =>
        RequestSetEnabled(row.Members.Select(member => member.UniqueId).ToArray(), enabled);

    private void RequestSetEnabled(IReadOnlyCollection<string> uniqueIds, bool enabled)
    {
        if (!busy && uniqueIds.Count > 0)
            _ = SetEnabledAsync(uniqueIds, enabled);
    }

    private void RequestRemove(ModGroupMemberRow row) =>
        ConfirmRemove(row.Members.Select(member => member.UniqueId).ToArray());

    private void ConfirmRemove(IReadOnlyCollection<string> uniqueIds)
    {
        if (busy || uniqueIds.Count == 0)
            return;
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_group_remove_title);
        dialog.SetMessage(GetString(Resource.String.mod_group_remove_message, uniqueIds.Count));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mod_group_remove_selected, (_, _) => _ = RemoveAsync(uniqueIds));
        dialog.Show();
    }

    private async Task SetEnabledAsync(IReadOnlyCollection<string> uniqueIds, bool enabled)
    {
        if (mutations is null)
            return;
        SetBusy(true, showProgress: false);
        try
        {
            var token = cancellation?.Token ?? CancellationToken.None;
            var result = await mutations.SetEnabledAsync(profileId, uniqueIds, enabled, token).ConfigureAwait(false);
            if (result.ChangedMembers > 0)
                modManagement?.PublishMutation(ModManagementChangeKind.Profiles, this);
            if (IsAdded)
                Activity?.RunOnUiThread(() => RenderProfile(result.Profile));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Error("JunimoGate.Mods", "profile-member-enable-failed", exception);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    ShowMessage(Resource.String.mod_group_update_failed);
                    if (profile is not null)
                        RenderProfile(profile);
                });
            }
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false, showProgress: false));
        }
    }

    private async Task RemoveAsync(IReadOnlyCollection<string> uniqueIds)
    {
        if (mutations is null)
            return;
        SetBusy(true);
        try
        {
            var token = cancellation?.Token ?? CancellationToken.None;
            var result = await mutations.RemoveAsync(profileId, uniqueIds, token).ConfigureAwait(false);
            if (result.ChangedMembers > 0)
                modManagement?.PublishMutation(ModManagementChangeKind.Profiles, this);
            if (IsAdded)
                Activity?.RunOnUiThread(() => RenderProfile(result.Profile));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Error("JunimoGate.Mods", "profile-member-remove-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_group_update_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private async Task SaveMetadataAsync(
        string displayName,
        string? description,
        ModAssemblyBindingPolicy? policy)
    {
        if (mutations is null)
            return;
        SetBusy(true);
        try
        {
            var token = cancellation?.Token ?? CancellationToken.None;
            var updated = await mutations.UpdateMetadataAsync(profileId, displayName, description, policy, token)
                .ConfigureAwait(false);
            modManagement?.PublishMutation(ModManagementChangeKind.Profiles, this);
            if (IsAdded)
                Activity?.RunOnUiThread(() => RenderProfile(updated));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-metadata-save-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_group_update_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void UpdateSelectionState()
    {
        var count = adapter?.SelectedUniqueIds.Count ?? 0;
        if (selectedCount is not null)
            selectedCount.Text = GetString(Resource.String.mod_group_selected_count, count);
        if (enableButton is not null)
            enableButton.Enabled = count > 0 && !busy;
        if (disableButton is not null)
            disableButton.Enabled = count > 0 && !busy;
        if (removeButton is not null)
            removeButton.Enabled = count > 0 && !busy;
        if (selectAllButton is not null)
        {
            var description = adapter?.AreAllFilteredSelected == true
                ? Resource.String.mod_group_clear_visible_selection
                : Resource.String.mod_group_select_all;
            selectAllButton.ContentDescription = GetString(description);
            selectAllButton.TooltipText = GetString(description);
        }
    }

    private void ExitBatchMode()
    {
        adapter?.ExitSelectionMode();
        if (batchBar is not null)
            batchBar.Visibility = ViewStates.Gone;
        RenderBatchToggle();
        UpdateSelectionState();
    }

    private void RenderBatchToggle()
    {
        if (batchButton is null)
            return;
        var selectionMode = adapter?.IsSelectionMode == true;
        var description = selectionMode ? Resource.String.action_done : Resource.String.mod_group_batch;
        batchButton.SetIconResource(selectionMode ? Resource.Drawable.ic_close_24 : Resource.Drawable.ic_checklist_24);
        batchButton.ContentDescription = GetString(description);
        batchButton.TooltipText = GetString(description);
    }

    private void UpdateEmptyState()
    {
        if (adapter is null || empty is null || emptyText is null || emptyAddButton is null)
            return;
        var emptyResult = adapter.ItemCount == 0;
        empty.Visibility = emptyResult ? ViewStates.Visible : ViewStates.Gone;
        emptyText.SetText(adapter.TotalCount == 0
            ? Resource.String.mod_group_members_empty
            : Resource.String.mod_group_members_search_empty);
        emptyAddButton.Visibility = adapter.TotalCount == 0 && profile?.Id != ModProfileV2.NoModsId
            ? ViewStates.Visible
            : ViewStates.Gone;
    }

    private void SetBusy(bool value, bool showProgress = true)
    {
        busy = value;
        if (progress is not null)
            progress.Visibility = value && showProgress ? ViewStates.Visible : ViewStates.Gone;
        if (infoButton is not null)
            infoButton.Enabled = !value;
        if (addButton is not null)
            addButton.Enabled = !value;
        if (batchButton is not null)
            batchButton.Enabled = !value;
        if (selectAllButton is not null)
            selectAllButton.Enabled = !value;
        if (batchDoneButton is not null)
            batchDoneButton.Enabled = !value;
        adapter?.SetInteractionEnabled(!value, list);
        UpdateSelectionState();
    }

    private void AttachToolbar()
    {
        mainShell = FindMainShell()
            ?? throw new InvalidOperationException("The Mod group editor shell is unavailable.");
        mainShell.SetEditorToolbar(GetString(Resource.String.mod_group_editor_title), OnNavigateUp);
    }

    private void DetachToolbar()
    {
        if (mainShell is null)
            return;
        mainShell.ClearEditorToolbar(OnNavigateUp);
        mainShell = null;
    }

    private MainShellFragment? FindMainShell()
    {
        for (var fragment = ParentFragment; fragment is not null; fragment = fragment.ParentFragment)
        {
            if (fragment is MainShellFragment shell)
                return shell;
        }
        return null;
    }

    private void OnNavigateUp(
        object? sender,
        AndroidX.AppCompat.Widget.Toolbar.NavigationClickEventArgs eventArgs) =>
        NavHostFragment.FindNavController(this).PopBackStack();

    private string GetDisplayName(ModProfileV2 value) => value.Id switch
    {
        ModProfileV2.NoModsId => GetString(Resource.String.mods_no_mods_group),
        "default" => GetString(Resource.String.mods_default_group),
        _ => value.DisplayName,
    };

    private IReadOnlyList<ModGroupMemberRow> BuildRows(ModProfileV2 value)
    {
        if (librarySnapshot is null)
        {
            return value.Members.Select(member => new ModGroupMemberRow(
                    $"missing:{member.UniqueId}",
                    member.ExpectedName,
                    [member],
                    Array.Empty<ModLibraryItem>(),
                    null,
                    Array.Empty<ModDependencyDiagnostic>()))
                .ToArray();
        }

        var projection = ModManagementProjection.Create(librarySnapshot);
        var displayByLibraryItem = projection.Items
            .SelectMany(display => display.Members.Select(item => (item.LibraryItemId, Display: display)))
            .ToDictionary(value => value.LibraryItemId, value => value.Display, StringComparer.Ordinal);
        var builders = new Dictionary<string, (ModManagementItem? Display, List<ModProfileMember> Members, List<ModLibraryItem> Items)>(StringComparer.Ordinal);
        foreach (var member in value.Members)
        {
            var installed = member.LibraryItemId is { } id && libraryItems.TryGetValue(id, out var item)
                ? item
                : null;
            var display = installed is not null && displayByLibraryItem.TryGetValue(installed.LibraryItemId, out var found)
                ? found
                : null;
            var key = display?.ItemId ?? $"missing:{member.UniqueId.ToLowerInvariant()}";
            if (!builders.TryGetValue(key, out var builder))
                builder = (display, new List<ModProfileMember>(), new List<ModLibraryItem>());
            builder.Members.Add(member);
            if (installed is not null)
                builder.Items.Add(installed);
            builders[key] = builder;
        }

        return builders.Select(pair =>
            {
                var builder = pair.Value;
                var displayName = builder.Display?.DisplayName ?? builder.Members[0].ExpectedName;
                var dependencies = builder.Display is null
                    ? Array.Empty<ModDependencyDiagnostic>()
                    : ModDependencyAnalyzer.Analyze(builder.Display, librarySnapshot, value).ToArray();
                return new ModGroupMemberRow(
                    pair.Key,
                    displayName,
                    builder.Members.ToArray(),
                    builder.Items.ToArray(),
                    builder.Display,
                    dependencies);
            })
            .OrderBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.RowId, StringComparer.Ordinal)
            .ToArray();
    }

    private int Dp(int value) => (int)Math.Round(value * Resources!.DisplayMetrics!.Density);

    private string FormatMemberSummary(ModGroupMemberRow row)
    {
        if (row.DisplayItem?.IsBundle == true)
        {
            var state = row.Members.All(member => member.Enabled)
                ? Resource.String.mod_group_state_enabled
                : row.Members.All(member => !member.Enabled)
                    ? Resource.String.mod_group_state_disabled
                    : Resource.String.mod_group_state_partial;
            return FormatString(
                Resource.String.mod_group_bundle_summary,
                Java.Lang.Integer.ValueOf(row.Members.Count),
                new JString(GetString(state)));
        }
        var member = row.Members[0];
        return row.Items.FirstOrDefault() is { } item
            ? $"{member.UniqueId} · {item.Manifest.Author}"
            : $"⚠ {member.UniqueId} · {GetString(Resource.String.mod_group_member_missing)}";
    }

    private string FormatMemberComponents(ModGroupMemberRow row)
    {
        if (row.DisplayItem?.IsBundle != true)
            return string.Empty;
        return FormatString(
            Resource.String.mod_group_components,
            new JString(string.Join(", ", row.Members.Select(member => member.ExpectedName))));
    }

    private void ShowDependencies(ModGroupMemberRow row)
    {
        if (busy || row.Dependencies.Count == 0)
            return;
        var labels = row.Dependencies.Select(FormatDependency).ToArray();
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_group_dependencies);
        dialog.SetItems(labels, (_, args) => HandleDependency(row.Dependencies[args.Which]));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.Show();
    }

    private string FormatDependency(ModDependencyDiagnostic dependency)
    {
        var requirement = GetString(dependency.IsRequired
            ? Resource.String.mod_group_dependency_required
            : Resource.String.mod_group_dependency_optional);
        var state = dependency.State switch
        {
            ModDependencyState.NotInstalled => GetString(Resource.String.mod_group_dependency_not_installed),
            ModDependencyState.AvailableSingleVersion or ModDependencyState.AvailableMultipleVersions =>
                GetString(Resource.String.mod_group_dependency_available),
            ModDependencyState.DisabledInProfile => GetString(Resource.String.mod_group_dependency_disabled),
            ModDependencyState.Satisfied => GetString(Resource.String.mod_group_dependency_satisfied),
            ModDependencyState.VersionMismatch => FormatString(
                Resource.String.mod_group_dependency_version_mismatch,
                new JString(dependency.MinimumVersion ?? "—")),
            _ => dependency.State.ToString(),
        };
        return $"{dependency.UniqueId} · {requirement} · {state}";
    }

    private void HandleDependency(ModDependencyDiagnostic dependency)
    {
        switch (dependency.State)
        {
            case ModDependencyState.AvailableSingleVersion:
                _ = AddDependencyAsync(dependency.Candidates[0]);
                break;
            case ModDependencyState.AvailableMultipleVersions:
            case ModDependencyState.VersionMismatch:
                ShowDependencyVersionPicker(dependency);
                break;
            case ModDependencyState.DisabledInProfile:
                RequestSetEnabled(new[] { dependency.UniqueId }, enabled: true);
                break;
        }
    }

    private void ShowDependencyVersionPicker(ModDependencyDiagnostic dependency)
    {
        if (dependency.Candidates.Count == 0)
            return;
        var labels = dependency.Candidates.Select(candidate =>
            $"{candidate.Manifest.Version} · {candidate.Manifest.Author}").ToArray();
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_group_dependency_choose_version);
        dialog.SetItems(labels, (_, args) => _ = AddDependencyAsync(dependency.Candidates[args.Which]));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.Show();
    }

    private async Task AddDependencyAsync(ModLibraryItem item)
    {
        if (mutations is null)
            return;
        SetBusy(true, showProgress: false);
        try
        {
            var token = cancellation?.Token ?? CancellationToken.None;
            var result = await mutations.AddOrReplaceAsync(profileId, [item], enabled: true, token)
                .ConfigureAwait(false);
            if (result.ChangedMembers > 0)
                modManagement?.PublishMutation(ModManagementChangeKind.Profiles, this);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    RenderProfile(result.Profile);
                    ShowMessage(Resource.String.mod_group_dependency_updated);
                });
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Error("JunimoGate.Mods", "profile-dependency-add-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_group_dependency_update_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false, showProgress: false));
        }
    }

    private void OnModManagementChanged(object? sender, ModManagementChangedEventArgs eventArgs)
    {
        if (eventArgs.Kind is not (ModManagementChangeKind.Library or ModManagementChangeKind.Profiles))
            return;
        if (ReferenceEquals(eventArgs.Origin, this))
            return;
        Activity?.RunOnUiThread(() =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = RefreshAsync(lifetime.Token);
        });
    }

    private void ShowMessage(int resourceId) =>
        Toast.MakeText(RequireContext(), resourceId, ToastLength.Long)?.Show();

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The Mod group string resource is unavailable.");
}

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
