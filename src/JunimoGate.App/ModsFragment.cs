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

[Register("org.junimogate.app.ModsFragment")]
public sealed class ModsFragment : Fragment
{
    private const int ExportBundleRequestCode = 4702;
    private ActivityResultLauncher? importArchiveLauncher;
    private MaterialButton? importButton;
    private MaterialButton? batchButton;
    private View? batchBar;
    private TextView? selectedCount;
    private MaterialButton? selectAllButton;
    private MaterialButton? addSelectedButton;
    private MaterialButton? deleteSelectedButton;
    private MaterialButton? batchDoneButton;
    private AppBarLayout? appBar;
    private View? searchTools;
    private SearchView? search;
    private TextView? inventoryCount;
    private LinearProgressIndicator? progress;
    private TextView? empty;
    private ModLibraryAdapter? adapter;
    private RecyclerView? list;
    private ModManagementUiSession? modManagement;
    private ModLibraryRepository? repository;
    private ModProfileV2Repository? profiles;
    private ModProfileMemberMutationService? profileMutations;
    private ActiveModProfileSelectionRepository? activeProfile;
    private LauncherSettingsRepository? settingsRepository;
    private LauncherSettings? launcherSettings;
    private CancellationTokenSource? cancellation;
    private IModArchiveInstallTransaction? pendingTransaction;
    private string? pendingExportBundleId;
    private int actualComponentCount;
    private bool busy;

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        importArchiveLauncher = RegisterForActivityResult(
            new ActivityResultContracts.StartActivityForResult(),
            new ImportArchiveResultCallback(HandleImportArchiveResult));
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mods, container, false)
        ?? throw new InvalidOperationException("The Mods layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        importButton = view.FindViewById<MaterialButton>(Resource.Id.mods_import)
            ?? throw new InvalidOperationException("The Mod import button is unavailable.");
        batchButton = view.FindViewById<MaterialButton>(Resource.Id.mods_batch)
            ?? throw new InvalidOperationException("The Mod batch action is unavailable.");
        appBar = view.FindViewById<AppBarLayout>(Resource.Id.mods_app_bar)
            ?? throw new InvalidOperationException("The Mod app bar is unavailable.");
        searchTools = view.FindViewById<View>(Resource.Id.mods_search_tools)
            ?? throw new InvalidOperationException("The Mod search tools are unavailable.");
        batchBar = view.FindViewById<View>(Resource.Id.mods_batch_bar)
            ?? throw new InvalidOperationException("The Mod batch bar is unavailable.");
        selectedCount = view.FindViewById<TextView>(Resource.Id.mods_selected_count)
            ?? throw new InvalidOperationException("The Mod selection count is unavailable.");
        selectAllButton = view.FindViewById<MaterialButton>(Resource.Id.mods_select_all)
            ?? throw new InvalidOperationException("The Mod select-all action is unavailable.");
        addSelectedButton = view.FindViewById<MaterialButton>(Resource.Id.mods_add_selected)
            ?? throw new InvalidOperationException("The add-selected action is unavailable.");
        deleteSelectedButton = view.FindViewById<MaterialButton>(Resource.Id.mods_delete_selected)
            ?? throw new InvalidOperationException("The delete-selected action is unavailable.");
        batchDoneButton = view.FindViewById<MaterialButton>(Resource.Id.mods_batch_done)
            ?? throw new InvalidOperationException("The Mod batch completion action is unavailable.");
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mods_progress)
            ?? throw new InvalidOperationException("The Mod progress indicator is unavailable.");
        search = view.FindViewById<SearchView>(Resource.Id.mods_search)
            ?? throw new InvalidOperationException("The Mod search input is unavailable.");
        inventoryCount = view.FindViewById<TextView>(Resource.Id.mods_inventory_count)
            ?? throw new InvalidOperationException("The Mod inventory count is unavailable.");
        empty = view.FindViewById<TextView>(Resource.Id.mods_empty)
            ?? throw new InvalidOperationException("The Mod empty state is unavailable.");
        list = view.FindViewById<RecyclerView>(Resource.Id.mods_list)
            ?? throw new InvalidOperationException("The Mod library list is unavailable.");
        adapter = new ModLibraryAdapter(
            FormatItemSummary,
            FormatItemMetadata,
            ShowDetails,
            RequestFiles,
            RequestAddToGroup,
            RequestDelete,
            RequestExport,
            RequestUnlock,
            RequestRestore,
            UpdateBatchState);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        importButton.Click += OnImportClicked;
        batchButton.Click += OnBatchClicked;
        selectAllButton.Click += OnSelectAllClicked;
        addSelectedButton.Click += OnAddSelectedClicked;
        deleteSelectedButton.Click += OnDeleteSelectedClicked;
        batchDoneButton.Click += OnBatchDoneClicked;
        search.QueryTextChange += OnSearchChanged;
    }

    public override void OnStart()
    {
        base.OnStart();
        cancellation = new CancellationTokenSource();
        modManagement = ((MainActivity)RequireActivity()).ModManagement;
        modManagement.Changed += OnModManagementChanged;
        repository = modManagement.Library;
        var profilesRoot = Path.Combine(AndroidPrivateStorage.GetUserDataRoot(RequireContext()), "profiles");
        profiles = modManagement.Profiles;
        profileMutations = modManagement.MemberMutations;
        activeProfile = modManagement.ActiveProfile;
        settingsRepository = new LauncherSettingsRepository(Path.Combine(
            AndroidPrivateStorage.GetUserDataRoot(RequireContext()),
            "settings"));
        _ = InitializeContentAsync(profilesRoot, cancellation.Token);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        var pending = Interlocked.Exchange(ref pendingTransaction, null);
        if (pending is not null)
            _ = pending.DisposeAsync();
        if (modManagement is not null)
            modManagement.Changed -= OnModManagementChanged;
        modManagement = null;
        repository = null;
        profiles = null;
        profileMutations = null;
        activeProfile = null;
        settingsRepository = null;
        launcherSettings = null;
        SetBusy(false);
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (importButton is not null)
            importButton.Click -= OnImportClicked;
        if (batchButton is not null)
            batchButton.Click -= OnBatchClicked;
        if (selectAllButton is not null)
            selectAllButton.Click -= OnSelectAllClicked;
        if (addSelectedButton is not null)
            addSelectedButton.Click -= OnAddSelectedClicked;
        if (deleteSelectedButton is not null)
            deleteSelectedButton.Click -= OnDeleteSelectedClicked;
        if (batchDoneButton is not null)
            batchDoneButton.Click -= OnBatchDoneClicked;
        if (search is not null)
            search.QueryTextChange -= OnSearchChanged;
        list?.SetAdapter(null);
        importButton = null;
        batchButton = null;
        batchBar = null;
        appBar = null;
        searchTools = null;
        selectedCount = null;
        selectAllButton = null;
        addSelectedButton = null;
        deleteSelectedButton = null;
        batchDoneButton = null;
        search = null;
        inventoryCount = null;
        progress = null;
        empty = null;
        list = null;
        adapter = null;
        pendingExportBundleId = null;
        base.OnDestroyView();
    }

#pragma warning disable CS0618, CS0672 // Export still uses the legacy API; Mod import uses Activity Result below.
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == ExportBundleRequestCode && resultCode != (int)Result.Ok)
            pendingExportBundleId = null;
        if (resultCode != (int)Result.Ok || data?.Data is not { } uri)
            return;
        if (requestCode == ExportBundleRequestCode &&
            cancellation is { IsCancellationRequested: false } lifetime &&
            pendingExportBundleId is { } bundleId)
        {
            pendingExportBundleId = null;
            _ = ExportBundleAsync(uri, bundleId, lifetime.Token);
        }
    }
#pragma warning restore CS0618, CS0672

    private void OnImportClicked(object? sender, EventArgs eventArgs)
    {
        if (busy)
            return;
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(ModDocumentPickerPolicy.RequestMimeType);
        intent.PutExtra(Intent.ExtraMimeTypes, ModDocumentPickerPolicy.AcceptedMimeTypes.ToArray());
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        if (importArchiveLauncher is null)
        {
            Log.Error("JunimoGate.Mods", "archive-picker-launcher-unavailable");
            ShowMessage(Resource.String.mods_picker_unavailable);
            return;
        }
        try
        {
            importArchiveLauncher.Launch(intent);
            Log.Info("JunimoGate.Mods", "archive-picker-launched");
        }
        catch (Exception exception) when (exception is ActivityNotFoundException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "archive-picker-launch-failed", exception);
            ShowMessage(Resource.String.mods_picker_unavailable);
        }
    }

    private void HandleImportArchiveResult(ActivityResult result)
    {
        if (result.ResultCode != (int)Result.Ok)
        {
            Log.Info("JunimoGate.Mods", $"archive-picker-cancelled resultCode={result.ResultCode}");
            return;
        }

        var uri = GetSingleDocumentUri(result.Data);
        if (uri is null)
        {
            Log.Warn("JunimoGate.Mods", "archive-picker-empty-result");
            if (IsAdded)
                ShowMessage(Resource.String.mods_picker_empty_result);
            return;
        }

        if (cancellation is not { IsCancellationRequested: false } lifetime)
        {
            Log.Warn("JunimoGate.Mods", "archive-picker-result-ignored lifecycle=inactive");
            return;
        }

        Log.Info("JunimoGate.Mods", "archive-picker-document-received");
        _ = ScanArchiveAsync(uri, lifetime.Token);
    }

    private static global::Android.Net.Uri? GetSingleDocumentUri(Intent? data)
    {
        var clipData = data?.ClipData;
        return ModDocumentPickerPolicy.ResolveSingleDocument(
            data?.Data,
            clipData?.ItemCount == 1 ? clipData.GetItemAt(0)?.Uri : null,
            clipData?.ItemCount ?? 0);
    }

    private void OnSearchChanged(object? sender, SearchView.QueryTextChangeEventArgs eventArgs)
    {
        adapter?.SetQuery(eventArgs.NewText);
        var hasQuery = !string.IsNullOrWhiteSpace(eventArgs.NewText);
        UpdateSearchToolScrolling(hasQuery || batchBar?.Visibility == ViewStates.Visible);
        if (hasQuery)
            appBar?.SetExpanded(true, animate: true);
        UpdateBatchState();
        UpdateInventoryCount();
        UpdateEmptyState();
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
        UpdateSearchToolScrolling(pinned: true);
        appBar?.SetExpanded(true, animate: true);
        UpdateBatchState();
    }

    private void OnSelectAllClicked(object? sender, EventArgs eventArgs) => adapter?.SelectAllFiltered();

    private void OnAddSelectedClicked(object? sender, EventArgs eventArgs)
    {
        var items = adapter?.SelectedItems ?? Array.Empty<ModLibraryItem>();
        if (items.Count > 0)
            _ = ShowGroupPickerAsync(items);
    }

    private void OnDeleteSelectedClicked(object? sender, EventArgs eventArgs)
    {
        var items = adapter?.SelectedItems ?? Array.Empty<ModLibraryItem>();
        if (busy || items.Count == 0)
            return;
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mods_delete_selected_title);
        dialog.SetMessage(GetString(
            Resource.String.mods_delete_selected_message,
            adapter?.SelectedEntryCount ?? items.Count));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mods_delete_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = DeleteManyAsync(items, lifetime.Token);
        });
        dialog.Show();
    }

    private void OnBatchDoneClicked(object? sender, EventArgs eventArgs) => ExitBatchMode();

    private void ExitBatchMode()
    {
        adapter?.ExitSelectionMode();
        if (batchBar is not null)
            batchBar.Visibility = ViewStates.Gone;
        RenderBatchToggle();
        UpdateSearchToolScrolling(!string.IsNullOrWhiteSpace(search?.Query?.ToString()));
        UpdateBatchState();
    }

    private void RenderBatchToggle()
    {
        if (batchButton is null)
            return;
        var selectionMode = adapter?.IsSelectionMode == true;
        var description = selectionMode ? Resource.String.action_done : Resource.String.mods_batch;
        batchButton.SetIconResource(selectionMode ? Resource.Drawable.ic_close_24 : Resource.Drawable.ic_checklist_24);
        batchButton.ContentDescription = GetString(description);
        batchButton.TooltipText = GetString(description);
    }

    private void UpdateSearchToolScrolling(bool pinned)
    {
        if (searchTools?.LayoutParameters is not AppBarLayout.LayoutParams parameters)
            return;
        parameters.ScrollFlags = pinned
            ? AppBarLayout.LayoutParams.ScrollFlagNoScroll
            : AppBarLayout.LayoutParams.ScrollFlagScroll | AppBarLayout.LayoutParams.ScrollFlagEnterAlways;
        searchTools.LayoutParameters = parameters;
    }

    private void UpdateBatchState()
    {
        var count = adapter?.SelectedEntryCount ?? 0;
        if (selectedCount is not null)
            selectedCount.Text = GetString(Resource.String.mods_selected_count, count);
        if (addSelectedButton is not null)
            addSelectedButton.Enabled = count > 0 && !busy;
        if (deleteSelectedButton is not null)
            deleteSelectedButton.Enabled = count > 0 && !busy;
        if (selectAllButton is not null)
        {
            selectAllButton.Enabled = !busy;
            var description = adapter?.AreAllFilteredSelected == true
                ? Resource.String.mods_clear_visible_selection
                : Resource.String.mods_select_all;
            selectAllButton.ContentDescription = GetString(description);
            selectAllButton.TooltipText = GetString(description);
        }
        if (batchDoneButton is not null)
            batchDoneButton.Enabled = !busy;
    }

    private async Task InitializeContentAsync(string profilesRoot, CancellationToken cancellationToken)
    {
        SetBusy(true);
        try
        {
            await AndroidPrivateStorage.EnsureMigratedAsync(RequireContext(), cancellationToken).ConfigureAwait(false);
            if (repository is not null && profiles is not null)
            {
                var migrator = new LegacyModProfileMigrator(profilesRoot, repository, profiles);
                var migration = await migrator
                    .MigrateAsync(ProfileId.Parse("default"), "Default", cancellationToken)
                    .ConfigureAwait(false);
                if (!migration.AlreadyMigrated)
                {
                    modManagement?.NotifyLibraryChanged();
                    modManagement?.NotifyProfilesChanged();
                }
                Log.Info(
                    "JunimoGate.Mods",
                    $"profile-migration already={(migration.AlreadyMigrated ? 1 : 0)} imported={migration.ImportedItems} reused={migration.ReusedItems} enabled={migration.EnabledMembers} disabled={migration.DisabledMembers}");
            }
            if (settingsRepository is not null)
                launcherSettings = await settingsRepository.ReadAsync(cancellationToken).ConfigureAwait(false);
            await RefreshAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels migration and refresh work.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-initialization-failed", exception);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_profile_migration_failed));
            }
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private async Task ScanArchiveAsync(global::Android.Net.Uri uri, CancellationToken cancellationToken)
    {
        if (repository is null)
            return;
        SetBusy(true);
        try
        {
            await AndroidPrivateStorage.EnsureMigratedAsync(RequireContext(), cancellationToken).ConfigureAwait(false);
            var transaction = repository.CreateInstallTransaction(ReadDisplayName(uri));
            pendingTransaction = transaction;
            await using var stream = RequireContext().ContentResolver?.OpenInputStream(uri)
                ?? throw new IOException("The selected Mod archive could not be opened.");
            await transaction.ScanAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => ShowScanResult(transaction));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels the app-private staging transaction.
        }
        catch (Java.Lang.Exception exception)
        {
            Log.Error(
                "JunimoGate.Mods",
                $"archive-provider-access-failed exception={exception.GetType().Name}");
            await FinishArchiveScanFailureAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "archive-scan-failed", exception);
            await FinishArchiveScanFailureAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask FinishArchiveScanFailureAsync()
    {
        await DisposePendingAsync().ConfigureAwait(false);
        if (IsAdded)
        {
            Activity?.RunOnUiThread(() =>
            {
                SetBusy(false);
                ShowMessage(Resource.String.mods_import_failed);
            });
        }
    }

    private void ShowScanResult(IModArchiveInstallTransaction transaction)
    {
        if (!ReferenceEquals(transaction, pendingTransaction) || transaction.ScanResult is not { } scan)
            return;
        if (!scan.CanCommit)
        {
            var detail = FormatIssues(scan.Issues);
            _ = DisposePendingAsync();
            SetBusy(false);
            var rejectedDialog = new MaterialAlertDialogBuilder(RequireContext());
            rejectedDialog.SetTitle(Resource.String.mods_archive_rejected);
            rejectedDialog.SetMessage(detail);
            rejectedDialog.SetPositiveButton(global::Android.Resource.String.Ok, (_, _) => { });
            rejectedDialog.Show();
            return;
        }

        var confirmDialog = new MaterialAlertDialogBuilder(RequireContext());
        confirmDialog.SetTitle(Resource.String.mods_import_confirm_title);
        confirmDialog.SetMessage(FormatScanConfirmation(scan));
        confirmDialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) =>
        {
            _ = DisposePendingAsync();
            SetBusy(false);
        });
        confirmDialog.SetPositiveButton(Resource.String.mods_import_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = CommitArchiveAsync(transaction, lifetime.Token);
        });
        confirmDialog.SetOnCancelListener(new DialogCancelListener(() =>
        {
            _ = DisposePendingAsync();
            SetBusy(false);
        }));
        confirmDialog.Show();
    }

    private async Task CommitArchiveAsync(
        IModArchiveInstallTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var result = transaction.ImportResult
                ?? throw new InvalidDataException("The Mod import result is missing.");
            Interlocked.CompareExchange(ref pendingTransaction, null, transaction);
            await transaction.DisposeAsync().ConfigureAwait(false);
            ModProfileAutoAssignmentResult? assignment = null;
            var assignmentFailed = false;
            if (settingsRepository is not null)
            {
                launcherSettings = await settingsRepository.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (launcherSettings.AddImportedModsToActiveProfile && profiles is not null && activeProfile is not null)
                {
                    try
                    {
                        assignment = await ModProfileAutoAssignment.AddImportedToActiveProfileAsync(
                                activeProfile,
                                profiles,
                                result.AllItems,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                      UnauthorizedAccessException or InvalidOperationException)
                    {
                        assignmentFailed = true;
                        Log.Error("JunimoGate.Mods", "archive-profile-assignment-failed", exception);
                    }
                }
            }
            modManagement?.NotifyLibraryChanged();
            if (assignment?.AddedMembers > 0)
                modManagement?.NotifyProfilesChanged();
            await RefreshAllAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    SetBusy(false);
                    var count = result.AllItems.Count;
                    ShowMessage(FormatImportResult(count, assignment, assignmentFailed));
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposePendingAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "archive-import-failed", exception);
            await DisposePendingAsync().ConfigureAwait(false);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    SetBusy(false);
                    ShowMessage(Resource.String.mods_import_failed);
                });
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (repository is null)
            return;
        try
        {
            await AndroidPrivateStorage.EnsureMigratedAsync(RequireContext(), cancellationToken).ConfigureAwait(false);
            var index = modManagement is null
                ? await repository.ReadAsync(cancellationToken).ConfigureAwait(false)
                : await modManagement.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => Render(index));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels the read.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Mods", "library-read-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_library_read_failed));
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        if (repository is null)
            return;
        var index = modManagement is null
            ? await repository.ReadAsync(cancellationToken).ConfigureAwait(false)
            : await modManagement.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
        if (!IsAdded || cancellationToken.IsCancellationRequested)
            return;
        Activity?.RunOnUiThread(() => Render(index));
    }

    private void Render(ModLibraryIndex index)
    {
        var projection = ModManagementProjection.Create(index);
        actualComponentCount = projection.ActualComponentCount;
        adapter?.SetItems(projection.Items);
        UpdateInventoryCount();
        UpdateEmptyState();
    }

    private void UpdateInventoryCount()
    {
        if (inventoryCount is null || adapter is null)
            return;
        var displayed = adapter.ItemCount;
        var total = adapter.TotalCount;
        inventoryCount.Text = displayed == total
            ? FormatString(
                Resource.String.mods_inventory_count,
                Java.Lang.Integer.ValueOf(total),
                Java.Lang.Integer.ValueOf(actualComponentCount))
            : FormatString(
                Resource.String.mods_inventory_filtered_count,
                Java.Lang.Integer.ValueOf(displayed),
                Java.Lang.Integer.ValueOf(total),
                Java.Lang.Integer.ValueOf(actualComponentCount));
    }

    private void UpdateEmptyState()
    {
        if (empty is null || adapter is null)
            return;
        empty.Visibility = adapter.ItemCount == 0 ? ViewStates.Visible : ViewStates.Gone;
        empty.SetText(adapter.TotalCount == 0 ? Resource.String.mods_empty : Resource.String.mods_search_empty);
    }

    private void ShowDetails(ModManagementItem displayItem)
    {
        if (displayItem.IsBundle)
        {
            var lines = displayItem.Members.Select(member =>
                $"• {member.Manifest.Name} {member.Manifest.Version}\n  {member.Manifest.UniqueId}");
            var bundleDialog = new MaterialAlertDialogBuilder(RequireContext());
            bundleDialog.SetTitle(displayItem.DisplayName);
            bundleDialog.SetMessage(string.Join("\n", lines));
            bundleDialog.SetPositiveButton(global::Android.Resource.String.Ok, (_, _) => { });
            bundleDialog.Show();
            return;
        }
        var item = displayItem.Members.Single();
        var type = item.Manifest.EntryDll is not null && item.Manifest.ContentPackForUniqueId is not null
            ? GetString(Resource.String.mods_type_mixed)
            : item.Manifest.EntryDll is not null
                ? GetString(Resource.String.mods_type_code)
                : GetString(Resource.String.mods_type_content);
        var source = item.SourceArchiveName ?? GetString(Resource.String.value_unavailable);
        var description = item.Manifest.Description ?? GetString(Resource.String.value_unavailable);
        var dependencies = Resources?.GetQuantityString(
            Resource.Plurals.mods_dependency_count,
            item.Manifest.Dependencies.Count,
            [Java.Lang.Integer.ValueOf(item.Manifest.Dependencies.Count)]) ?? item.Manifest.Dependencies.Count.ToString();
        var files = Resources?.GetQuantityString(
            Resource.Plurals.environment_file_count,
            item.FileCount,
            [Java.Lang.Integer.ValueOf(item.FileCount)]) ?? item.FileCount.ToString();
        var size = global::Android.Text.Format.Formatter.FormatFileSize(RequireContext(), item.TotalBytes) ?? "—";
        var detail = FormatString(
            Resource.String.mods_details_message,
            new JString(item.Manifest.UniqueId),
            new JString(item.Manifest.Author),
            new JString(type),
            new JString(dependencies),
            new JString(files),
            new JString(size),
            new JString(FormatDateTime(item.ImportedAtUtc)),
            new JString(source),
            new JString(description));
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle($"{item.Manifest.Name} {item.Manifest.Version}");
        dialog.SetMessage(detail);
        dialog.SetPositiveButton(global::Android.Resource.String.Ok, (_, _) => { });
        dialog.Show();
    }

    private void RequestFiles(ModManagementItem displayItem)
    {
        if (busy)
            return;
        if (!displayItem.IsBundle)
        {
            OpenFiles(displayItem.Members.Single());
            return;
        }

        var members = displayItem.Members
            .OrderBy(member => member.Manifest.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(member => member.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var labels = members.Select(member => $"{member.Manifest.Name} {member.Manifest.Version}").ToArray();
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_files_choose_mod);
        dialog.SetItems(labels, (_, eventArgs) => OpenFiles(members[eventArgs.Which]));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.Show();
    }

    private void OpenFiles(ModLibraryItem item)
    {
        using var arguments = new Bundle();
        arguments.PutString("libraryItemId", item.LibraryItemId);
        arguments.PutString("modName", $"{item.Manifest.Name} {item.Manifest.Version}");
        NavHostFragment.FindNavController(this).Navigate(Resource.Id.navigation_mod_files, arguments);
    }

    private void RequestAddToGroup(ModManagementItem item)
    {
        if (!busy)
            _ = ShowGroupPickerAsync(item.Members);
    }

    private async Task ShowGroupPickerAsync(IReadOnlyList<ModLibraryItem> items)
    {
        if (profiles is null || cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        if (items.GroupBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            ShowMessage(Resource.String.mods_add_duplicate_versions);
            return;
        }

        SetBusy(true);
        try
        {
            var loadedGroups = modManagement is null
                ? await profiles.ListAsync(lifetime.Token).ConfigureAwait(false)
                : await modManagement.GetProfilesAsync(lifetime.Token).ConfigureAwait(false);
            var groups = loadedGroups
                .Where(group => group.Id != ModProfileV2.NoModsId)
                .ToArray();
            if (!IsAdded || lifetime.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => ShowGroupPicker(groups, items));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Mods", "profile-picker-read-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_read_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void ShowGroupPicker(IReadOnlyList<ModProfileV2> groups, IReadOnlyList<ModLibraryItem> items)
    {
        if (groups.Count == 0)
        {
            ShowMessage(Resource.String.mods_no_editable_groups);
            return;
        }
        var labels = groups.Select(group =>
            $"{GetProfileDisplayName(group)} · {group.Members.Count}").ToArray();
        var selectedIndex = 0;
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mods_choose_group);
        dialog.SetSingleChoiceItems(labels, selectedIndex, (_, eventArgs) => selectedIndex = eventArgs.Which);
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mods_add_to_group, (_, _) =>
        {
            var selected = groups[selectedIndex];
            var replacements = items.Count(item => selected.Members.Any(member =>
                member.UniqueId.Equals(item.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase) &&
                member.LibraryItemId != item.LibraryItemId));
            if (replacements > 0)
                ConfirmVersionReplacement(selected, items, replacements);
            else
                _ = AddToGroupAsync(ProfileId.Parse(selected.Id), items);
        });
        dialog.Show();
    }

    private void ConfirmVersionReplacement(
        ModProfileV2 target,
        IReadOnlyList<ModLibraryItem> items,
        int replacements)
    {
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mods_replace_versions_title);
        dialog.SetMessage(FormatString(
            Resource.String.mods_replace_versions_message,
            Java.Lang.Integer.ValueOf(replacements),
            new JString(GetProfileDisplayName(target))));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mods_replace_versions_action, (_, _) =>
            _ = AddToGroupAsync(ProfileId.Parse(target.Id), items));
        dialog.Show();
    }

    private async Task AddToGroupAsync(ProfileId target, IReadOnlyList<ModLibraryItem> items)
    {
        if (profileMutations is null)
            return;
        SetBusy(true);
        try
        {
            var result = await profileMutations.AddOrReplaceAsync(target, items, enabled: true)
                .ConfigureAwait(false);
            if (result.ChangedMembers > 0)
                modManagement?.NotifyProfilesChanged();
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    ExitBatchMode();
                    ShowMessage(GetString(
                        Resource.String.mods_added_to_group,
                        result.AddedMembers,
                        result.ReplacedMembers,
                        result.ChangedMembers));
                });
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-members-add-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_add_to_group_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private string GetProfileDisplayName(ModProfileV2 value) => value.Id switch
    {
        "default" => GetString(Resource.String.mods_default_group),
        _ => value.DisplayName,
    };

    private void RequestDelete(ModManagementItem item)
    {
        if (busy || repository is null)
            return;
        if (launcherSettings?.ConfirmLibraryDeletion == false)
        {
            if (cancellation is { IsCancellationRequested: false } directLifetime)
                _ = DeleteManyAsync(item.Members, directLifetime.Token);
            return;
        }
        var deleteDialog = new MaterialAlertDialogBuilder(RequireContext());
        deleteDialog.SetTitle(Resource.String.mods_delete_title);
        deleteDialog.SetMessage(item.IsBundle
            ? FormatString(
                Resource.String.mods_delete_bundle_message,
                new JString(item.DisplayName),
                Java.Lang.Integer.ValueOf(item.Members.Count))
            : FormatString(
                Resource.String.mods_delete_message,
                new JString(item.Members[0].Manifest.Name),
                new JString(item.Members[0].Manifest.Version)));
        deleteDialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        deleteDialog.SetPositiveButton(Resource.String.mods_delete_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = DeleteManyAsync(item.Members, lifetime.Token);
        });
        deleteDialog.Show();
    }

    private void RequestExport(ModManagementItem item)
    {
        if (busy || item.Bundle is null)
            return;
        pendingExportBundleId = item.Bundle.BundleId;
        var safeName = string.Concat(item.DisplayName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/zip");
        intent.PutExtra(Intent.ExtraTitle, $"JunimoGate-{safeName}.zip");
#pragma warning disable CS0618
        StartActivityForResult(intent, ExportBundleRequestCode);
#pragma warning restore CS0618
    }

    private async Task ExportBundleAsync(
        global::Android.Net.Uri uri,
        string bundleId,
        CancellationToken cancellationToken)
    {
        if (modManagement is null)
            return;
        SetBusy(true);
        try
        {
            await using var output = RequireContext().ContentResolver?.OpenOutputStream(uri, "w")
                ?? throw new IOException("The selected bundle export document could not be opened.");
            await modManagement.Transfers.ExportBundlePackageAsync(bundleId, output, cancellationToken)
                .ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_bundle_exported));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteExportDocument(uri);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          ArgumentException or KeyNotFoundException)
        {
            Log.Error("JunimoGate.Mods", "bundle-export-failed", exception);
            TryDeleteExportDocument(uri);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_bundle_export_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void TryDeleteExportDocument(global::Android.Net.Uri uri)
    {
        try
        {
            _ = Context?.ContentResolver?.Delete(uri, null, null);
        }
        catch (Exception exception) when (exception is Java.Lang.SecurityException or InvalidOperationException)
        {
            Log.Warn("JunimoGate.Mods", "bundle-export-cleanup-failed", exception);
        }
    }

    private void RequestUnlock(ModManagementItem item, ModLibraryItem member)
    {
        if (!busy && item.Bundle is not null)
            _ = SetBundleMemberUnlockedAsync(item.Bundle.BundleId, member.Manifest.UniqueId, unlocked: true);
    }

    private void RequestRestore(ModManagementItem item)
    {
        if (!busy && item.RestorableBundle is not null)
        {
            _ = SetBundleMemberUnlockedAsync(
                item.RestorableBundle.BundleId,
                item.Members[0].Manifest.UniqueId,
                unlocked: false);
        }
    }

    private async Task SetBundleMemberUnlockedAsync(string bundleId, string uniqueId, bool unlocked)
    {
        if (repository is null || cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        SetBusy(true);
        try
        {
            var result = await repository.SetBundleMemberUnlockedAsync(
                    bundleId,
                    uniqueId,
                    unlocked,
                    lifetime.Token)
                .ConfigureAwait(false);
            if (result.Changed)
                modManagement?.NotifyLibraryChanged();
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    Render(result.Library);
                    ShowMessage(Resource.String.mods_bundle_changed);
                });
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException or
                                          ArgumentException or KeyNotFoundException)
        {
            Log.Error("JunimoGate.Mods", "bundle-membership-update-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_bundle_change_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private string FormatImportResult(
        int importedCount,
        ModProfileAutoAssignmentResult? assignment,
        bool assignmentFailed)
    {
        if (assignmentFailed)
            return GetString(Resource.String.mods_import_completed_group_failed, importedCount);
        if (assignment?.BlockedByReadOnlyProfile == true)
            return GetString(Resource.String.mods_import_completed_no_mods_group, importedCount);
        if (assignment is not null)
        {
            return GetString(
                Resource.String.mods_import_completed_auto_group,
                importedCount,
                assignment.AddedMembers,
                assignment.AmbiguousUniqueIds);
        }
        return Resources?.GetQuantityString(
            Resource.Plurals.mods_import_completed,
            importedCount,
            [Java.Lang.Integer.ValueOf(importedCount)]) ?? GetString(Resource.String.mods_import_failed);
    }

    private async Task DeleteAsync(ModLibraryItem item, CancellationToken cancellationToken)
        => await DeleteManyAsync(new[] { item }, cancellationToken).ConfigureAwait(false);

    private async Task DeleteManyAsync(
        IReadOnlyList<ModLibraryItem> items,
        CancellationToken cancellationToken)
    {
        if (repository is null)
            return;
        SetBusy(true);
        try
        {
            var inUse = await GameLaunchRegistry.FindLibraryItemsInUseAsync(
                    RequireContext(),
                    items.Select(item => item.LibraryItemId).ToArray(),
                    cancellationToken).ConfigureAwait(false);
            if (inUse.Count != 0)
            {
                if (IsAdded)
                    Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_delete_in_use));
                return;
            }
            var result = await repository.DeleteManyAsync(
                    items.Select(item => item.LibraryItemId).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            modManagement?.NotifyLibraryChanged();
            await RefreshAllAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    ExitBatchMode();
                    ShowMessage(items.Count == 1
                        ? GetString(Resource.String.mods_delete_completed)
                        : GetString(Resource.String.mods_delete_selected_completed, result.DeletedItems.Count));
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels deletion before its next transaction boundary.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Mods", "library-delete-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_delete_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private string FormatScanConfirmation(ModArchiveScanResult scan)
    {
        var names = scan.Candidates
            .Take(20)
            .Select(candidate => $"• {candidate.Manifest.Name} {candidate.Manifest.Version}")
            .ToList();
        if (scan.Candidates.Count > names.Count)
            names.Add(GetString(Resource.String.mods_import_more, scan.Candidates.Count - names.Count));
        var size = global::Android.Text.Format.Formatter.FormatFileSize(RequireContext(), scan.ExpandedBytes) ?? "—";
        var summary = Resources?.GetQuantityString(
            Resource.Plurals.mods_import_scan_summary,
            scan.Candidates.Count,
            [Java.Lang.Integer.ValueOf(scan.Candidates.Count), new Java.Lang.String(size)]) ?? string.Empty;
        if (scan.Issues.Any(issue => issue.Severity == ModArchiveIssueSeverity.Warning))
            names.Add(GetString(Resource.String.mods_import_has_warnings));
        return summary + "\n\n" + string.Join("\n", names);
    }

    private string FormatIssues(IReadOnlyList<ModArchiveIssue> issues)
    {
        var errors = issues.Where(issue => issue.Severity == ModArchiveIssueSeverity.Error).ToArray();
        var lines = errors
            .Take(12)
            .Select(issue => $"• {issue.Code}{(issue.Path is null ? string.Empty : $": {issue.Path}")}")
            .ToList();
        if (errors.Length > lines.Count)
            lines.Add(GetString(Resource.String.mods_import_more_errors));
        return GetString(Resource.String.mods_archive_rejected_description) + "\n\n" + string.Join("\n", lines);
    }

    private string FormatItemSummary(ModManagementItem displayItem, int installedVersions)
    {
        if (displayItem.IsBundle)
        {
            var bundleVersions = string.Join(", ", displayItem.Members
                .Select(item => item.Manifest.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            return FormatString(
                Resource.String.mods_bundle_summary,
                Java.Lang.Integer.ValueOf(displayItem.Members.Count),
                new JString(bundleVersions));
        }
        var item = displayItem.Members.Single();
        var size = global::Android.Text.Format.Formatter.FormatFileSize(RequireContext(), item.TotalBytes) ?? "—";
        var files = Resources?.GetQuantityString(
            Resource.Plurals.environment_file_count,
            item.FileCount,
            [Java.Lang.Integer.ValueOf(item.FileCount)]) ?? item.FileCount.ToString();
        var versions = Resources?.GetQuantityString(
            Resource.Plurals.mods_version_count,
            installedVersions,
            [Java.Lang.Integer.ValueOf(installedVersions)]) ?? installedVersions.ToString();
        return FormatString(
            Resource.String.mods_item_summary,
            new JString(item.Manifest.Version),
            new JString(item.Manifest.Author),
            new JString(versions),
            new JString(files),
            new JString(size));
    }

    private string FormatItemMetadata(ModManagementItem displayItem)
    {
        if (displayItem.IsBundle)
            return GetString(Resource.String.mods_bundle_metadata, displayItem.Members.Count);
        var item = displayItem.Members.Single();
        var type = item.Manifest.EntryDll is not null && item.Manifest.ContentPackForUniqueId is not null
            ? GetString(Resource.String.mods_type_mixed)
            : item.Manifest.EntryDll is not null
                ? GetString(Resource.String.mods_type_code)
                : GetString(Resource.String.mods_type_content);
        var dependencies = Resources?.GetQuantityString(
            Resource.Plurals.mods_dependency_count,
            item.Manifest.Dependencies.Count,
            [Java.Lang.Integer.ValueOf(item.Manifest.Dependencies.Count)]) ?? item.Manifest.Dependencies.Count.ToString();
        return FormatString(
            Resource.String.mods_item_metadata,
            new JString(type),
            new JString(dependencies));
    }

    private string FormatDateTime(DateTimeOffset value)
    {
        var context = RequireContext();
        using var date = new Java.Util.Date(value.ToUnixTimeMilliseconds());
        var dateFormatter = DateFormat.GetMediumDateFormat(context)
            ?? throw new InvalidOperationException("The localized date formatter is unavailable.");
        var timeFormatter = DateFormat.GetTimeFormat(context)
            ?? throw new InvalidOperationException("The localized time formatter is unavailable.");
        return FormatString(
            Resource.String.date_time_value,
            new JString(dateFormatter.Format(date) ?? "—"),
            new JString(timeFormatter.Format(date) ?? "—"));
    }

    private string? ReadDisplayName(global::Android.Net.Uri uri)
    {
        ICursor? cursor = null;
        try
        {
            cursor = RequireContext().ContentResolver?.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            if (cursor?.MoveToFirst() == true)
            {
                var index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (index >= 0)
                    return cursor.GetString(index);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Java.Lang.Exception)
        {
            Log.Warn(
                "JunimoGate.Mods",
                $"archive-display-name-unavailable exception={exception.GetType().Name}");
        }
        finally
        {
            TryCloseCursor(cursor);
        }
        return null;
    }

    private static void TryCloseCursor(ICursor? cursor)
    {
        if (cursor is null)
            return;
        try
        {
            cursor.Close();
            cursor.Dispose();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Java.Lang.Exception)
        {
            Log.Warn(
                "JunimoGate.Mods",
                $"archive-display-name-cursor-close-failed exception={exception.GetType().Name}");
        }
    }

    private sealed class ImportArchiveResultCallback(Action<ActivityResult> callback) :
        Java.Lang.Object,
        IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result)
        {
            if (result is ActivityResult activityResult)
                callback(activityResult);
        }
    }

    private void OnModManagementChanged(object? sender, ModManagementChangedEventArgs eventArgs)
    {
        if (eventArgs.Kind != ModManagementChangeKind.Library)
            return;
        Activity?.RunOnUiThread(() =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = RefreshAsync(lifetime.Token);
        });
    }

    private async ValueTask DisposePendingAsync()
    {
        var pending = Interlocked.Exchange(ref pendingTransaction, null);
        if (pending is not null)
            await pending.DisposeAsync().ConfigureAwait(false);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        if (importButton is not null)
            importButton.Enabled = !value;
        if (batchButton is not null)
            batchButton.Enabled = !value;
        if (deleteSelectedButton is not null)
            deleteSelectedButton.Enabled = !value;
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        adapter?.SetInteractionEnabled(!value);
        UpdateBatchState();
    }

    private void ShowMessage(int resourceId) =>
        Toast.MakeText(RequireContext(), resourceId, ToastLength.Long)?.Show();

    private void ShowMessage(string message) =>
        Toast.MakeText(RequireContext(), message, ToastLength.Long)?.Show();

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The Mod string resource is unavailable.");

    private sealed class DialogCancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => onCancel();
    }
}

internal sealed class ModLibraryAdapter(
    Func<ModManagementItem, int, string> formatSummary,
    Func<ModManagementItem, string> formatMetadata,
    Action<ModManagementItem> showDetails,
    Action<ModManagementItem> showFiles,
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

    public void SetItems(IReadOnlyList<ModManagementItem> value)
    {
        allItems = value;
        selected.RemoveWhere(id => value.All(item => item.ItemId != id));
        versionCounts = value
            .SelectMany(item => item.Members)
            .GroupBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        ApplyFilter();
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
            item.ItemId == expandedItemId);
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
        private readonly TextView title;
        private readonly TextView summary;
        private readonly TextView description;
        private readonly TextView metadata;
        private readonly LinearLayout components;
        private readonly ImageView expand;
        private readonly View expanded;
        private readonly CheckBox selected;
        private readonly MaterialButton addButton;
        private readonly MaterialButton deleteButton;
        private readonly MaterialButton detailsButton;
        private readonly MaterialButton filesButton;
        private readonly MaterialButton exportButton;
        private readonly MaterialButton restoreButton;
        private readonly Action<ModManagementItem> showDetails;
        private readonly Action<ModManagementItem> showFiles;
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
            deleteButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_delete)
                ?? throw new InvalidOperationException("The Mod item delete button is unavailable.");
            detailsButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_details)
                ?? throw new InvalidOperationException("The Mod item details button is unavailable.");
            filesButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_files)
                ?? throw new InvalidOperationException("The Mod item files button is unavailable.");
            exportButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_export)
                ?? throw new InvalidOperationException("The Mod item export button is unavailable.");
            restoreButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_restore)
                ?? throw new InvalidOperationException("The Mod item restore button is unavailable.");
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
            addButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.addToGroup(item);
            };
            deleteButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.delete(item);
            };
            exportButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.export(item);
            };
            restoreButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.restore(item);
            };
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
            bool isExpanded)
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
            exportButton.Visibility = !isSelectionMode && value.IsBundle ? ViewStates.Visible : ViewStates.Gone;
            restoreButton.Visibility = !isSelectionMode && value.RestorableBundle is not null
                ? ViewStates.Visible
                : ViewStates.Gone;
            deleteButton.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            expanded.Visibility = !isSelectionMode && isExpanded ? ViewStates.Visible : ViewStates.Gone;
            expand.Visibility = isSelectionMode ? ViewStates.Gone : ViewStates.Visible;
            expand.Rotation = isExpanded ? 180f : 0f;
            addButton.Enabled = interactionEnabled;
            detailsButton.Enabled = interactionEnabled;
            filesButton.Enabled = interactionEnabled;
            exportButton.Enabled = interactionEnabled;
            restoreButton.Enabled = interactionEnabled;
            deleteButton.Enabled = interactionEnabled;
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
