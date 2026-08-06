using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Navigation.Fragment;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.RadioButton;
using Google.Android.Material.TextField;
using JunimoGate.Android;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;
using Stopwatch = System.Diagnostics.Stopwatch;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;

namespace JunimoGate.App;

[Register("org.junimogate.app.ModGroupsFragment")]
public sealed class ModGroupsFragment : Fragment
{
    private static readonly TimeSpan MinimumExportProgressDuration = TimeSpan.FromMilliseconds(500);
    private const int ImportGroupRequestCode = 4710;
    private const int ExportManifestRequestCode = 4711;
    private const int ExportPackageRequestCode = 4712;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? viewCancellation;
    private ModProfileV2Repository? profiles;
    private ActiveModProfileSelectionRepository? selection;
    private ModLibraryRepository? library;
    private ModManagementUiSession? modManagement;
    private ModGroupAdapter? adapter;
    private RecyclerView? list;
    private MaterialButton? createButton;
    private MaterialButton? importButton;
    private LinearProgressIndicator? progress;
    private TextView? progressLabel;
    private TextView? empty;
    private int exportProgressText;
    private ModProfileV2? pendingExportProfile;
    private ModProfilePackageImportTransaction? pendingPackageImport;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mod_groups, container, false)
        ?? throw new InvalidOperationException("The Mod groups layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        viewCancellation = new CancellationTokenSource();
        createButton = view.FindViewById<MaterialButton>(Resource.Id.mod_groups_create)
            ?? throw new InvalidOperationException("The create-group action is unavailable.");
        importButton = view.FindViewById<MaterialButton>(Resource.Id.mod_groups_import)
            ?? throw new InvalidOperationException("The import-group action is unavailable.");
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mod_groups_progress)
            ?? throw new InvalidOperationException("The group progress indicator is unavailable.");
        progressLabel = view.FindViewById<TextView>(Resource.Id.mod_groups_progress_label)
            ?? throw new InvalidOperationException("The group progress label is unavailable.");
        empty = view.FindViewById<TextView>(Resource.Id.mod_groups_empty)
            ?? throw new InvalidOperationException("The group empty state is unavailable.");
        list = view.FindViewById<RecyclerView>(Resource.Id.mod_groups_list)
            ?? throw new InvalidOperationException("The group list is unavailable.");
        adapter = new ModGroupAdapter(FormatSummary, OpenProfile, SelectProfile, RequestShare, RequestDelete);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        createButton.Click += OnCreateClicked;
        importButton.Click += OnImportClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        cancellation = new CancellationTokenSource();
        modManagement = ((MainActivity)RequireActivity()).ModManagement;
        modManagement.Changed += OnModManagementChanged;
        profiles = modManagement.Profiles;
        selection = modManagement.ActiveProfile;
        library = modManagement.Library;
    }

    public override void OnResume()
    {
        base.OnResume();
        if (cancellation is { IsCancellationRequested: false } lifetime)
            _ = RefreshAsync(lifetime.Token);
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
        selection = null;
        library = null;
        exportProgressText = 0;
        SetBusy(false);
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        viewCancellation?.Cancel();
        viewCancellation?.Dispose();
        viewCancellation = null;
        pendingExportProfile = null;
        var pendingImport = Interlocked.Exchange(ref pendingPackageImport, null);
        if (pendingImport is not null)
            _ = pendingImport.DisposeAsync();
        if (createButton is not null)
            createButton.Click -= OnCreateClicked;
        if (importButton is not null)
            importButton.Click -= OnImportClicked;
        list?.SetAdapter(null);
        createButton = null;
        importButton = null;
        progress = null;
        progressLabel = null;
        empty = null;
        list = null;
        adapter = null;
        base.OnDestroyView();
    }

#pragma warning disable CS0618, CS0672 // Fragment activity-result API is scoped to this lifecycle and SAF grants one document.
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode is ExportManifestRequestCode or ExportPackageRequestCode && resultCode != (int)Result.Ok)
            pendingExportProfile = null;
        if (resultCode != (int)Result.Ok || data?.Data is not { } uri ||
            viewCancellation is not { IsCancellationRequested: false } lifetime)
        {
            return;
        }

        if (requestCode == ImportGroupRequestCode)
            _ = ImportGroupAsync(uri, lifetime.Token);
        else if (requestCode is ExportManifestRequestCode or ExportPackageRequestCode && pendingExportProfile is { } profile)
        {
            pendingExportProfile = null;
            _ = ExportGroupAsync(
                uri,
                profile,
                requestCode == ExportPackageRequestCode ? ModProfileTransferKind.Complete : ModProfileTransferKind.Manifest,
                lifetime.Token);
        }
    }
#pragma warning restore CS0618, CS0672

    private void OnCreateClicked(object? sender, EventArgs eventArgs)
    {
        var content = LayoutInflater.From(RequireContext())?.Inflate(
            Resource.Layout.dialog_mod_group_name,
            null,
            false)
            ?? throw new InvalidOperationException("The create-group dialog layout could not be created.");
        var input = content.FindViewById<TextInputEditText>(Resource.Id.mod_group_name_input)
            ?? throw new InvalidOperationException("The create-group name input is unavailable.");
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_groups_create_title);
        dialog.SetView(content);
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mod_groups_create_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = CreateAsync(input.Text ?? string.Empty, lifetime.Token);
        });
        var shown = dialog.Show()
            ?? throw new InvalidOperationException("The create-group dialog could not be shown.");
        var create = shown.GetButton((int)DialogButtonType.Positive)
            ?? throw new InvalidOperationException("The create-group action is unavailable.");
        void UpdateCreateState() => create.Enabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => UpdateCreateState();
        UpdateCreateState();
        input.RequestFocus();
        shown.Window?.SetSoftInputMode(SoftInput.StateAlwaysVisible);
    }

    private void OnImportClicked(object? sender, EventArgs eventArgs)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/octet-stream");
        intent.PutExtra(Intent.ExtraMimeTypes, new[]
        {
            "application/json",
            "application/zip",
            "application/x-zip-compressed",
            "application/octet-stream",
        });
#pragma warning disable CS0618
        StartActivityForResult(intent, ImportGroupRequestCode);
#pragma warning restore CS0618
    }

    private void RequestShare(ModProfileV2 profile)
    {
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(FormatString(Resource.String.mod_groups_share_title, new JString(GetDisplayName(profile))));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetNeutralButton(Resource.String.mod_groups_export_manifest, (_, _) => StartExport(profile, ModProfileTransferKind.Manifest));
        dialog.SetPositiveButton(Resource.String.mod_groups_export_package, (_, _) => StartExport(profile, ModProfileTransferKind.Complete));
        dialog.Show();
    }

    private void StartExport(ModProfileV2 profile, ModProfileTransferKind kind)
    {
        pendingExportProfile = profile;
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(kind == ModProfileTransferKind.Complete ? "application/zip" : "application/json");
        intent.PutExtra(Intent.ExtraTitle, $"JunimoGate-{profile.Id}.{(kind == ModProfileTransferKind.Complete ? "zip" : "json")}");
#pragma warning disable CS0618
        StartActivityForResult(intent, kind == ModProfileTransferKind.Complete ? ExportPackageRequestCode : ExportManifestRequestCode);
#pragma warning restore CS0618
    }

    private async Task ExportGroupAsync(
        global::Android.Net.Uri uri,
        ModProfileV2 profile,
        ModProfileTransferKind kind,
        CancellationToken cancellationToken)
    {
        exportProgressText = kind == ModProfileTransferKind.Complete
            ? Resource.String.mod_groups_exporting_package
            : Resource.String.mod_groups_exporting_manifest;
        SetBusy(true);
        var progressDuration = Stopwatch.StartNew();
        try
        {
            var service = CreateTransferService();
            await using var output = RequireContext().ContentResolver?.OpenOutputStream(uri, "w")
                ?? throw new IOException("The selected export document could not be opened.");
            var result = kind == ModProfileTransferKind.Complete
                ? await service.ExportPackageAsync(ProfileId.Parse(profile.Id), output, cancellationToken).ConfigureAwait(false)
                : await service.ExportManifestAsync(ProfileId.Parse(profile.Id), output, cancellationToken).ConfigureAwait(false);
            var remainingProgressDuration = MinimumExportProgressDuration - progressDuration.Elapsed;
            if (remainingProgressDuration > TimeSpan.Zero)
                await Task.Delay(remainingProgressDuration, cancellationToken).ConfigureAwait(false);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() => ShowMessage(FormatString(
                    Resource.String.mod_groups_export_result,
                    Java.Lang.Integer.ValueOf(result.PackagedItems),
                    Java.Lang.Integer.ValueOf(result.ExcludedConfigFiles),
                    Java.Lang.Integer.ValueOf(result.MissingItems))));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDocument(uri);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-export-failed", exception);
            TryDeleteDocument(uri);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_export_failed));
        }
        finally
        {
            exportProgressText = 0;
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private async Task ImportGroupAsync(global::Android.Net.Uri uri, CancellationToken cancellationToken)
    {
        SetBusy(true);
        try
        {
            var service = CreateTransferService();
            var displayName = ReadDisplayName(uri);
            var isManifest = displayName?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true ||
                             RequireContext().ContentResolver?.GetType(uri)?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true;
            await using var input = RequireContext().ContentResolver?.OpenInputStream(uri)
                ?? throw new IOException("The selected shared group could not be opened.");
            if (isManifest)
            {
                var result = await service.ImportManifestAsync(input, cancellationToken).ConfigureAwait(false);
                modManagement?.NotifyProfilesChanged();
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
                ShowImportResult(result);
                return;
            }

            var transaction = service.CreatePackageImportTransaction(displayName);
            pendingPackageImport = transaction;
            await transaction.ScanAsync(input, cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => ShowPackageConfirmation(transaction));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposePackageImportAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-import-scan-failed", exception);
            await DisposePackageImportAsync().ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_import_failed));
        }
        finally
        {
            if (IsAdded && pendingPackageImport is null)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void ShowPackageConfirmation(ModProfilePackageImportTransaction transaction)
    {
        if (!ReferenceEquals(transaction, pendingPackageImport) || transaction.Document is not { } document)
            return;
        var packaged = document.Members.Count(member => member.PackagedContentId is not null);
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_groups_package_confirm_title);
        dialog.SetMessage(FormatString(
            Resource.String.mod_groups_package_confirm_message,
            new JString(document.DisplayName),
            Java.Lang.Integer.ValueOf(document.Members.Count),
            Java.Lang.Integer.ValueOf(packaged)));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) =>
        {
            _ = DisposePackageImportAsync();
            SetBusy(false);
        });
        dialog.SetOnCancelListener(new DialogCancelListener(() =>
        {
            _ = DisposePackageImportAsync();
            SetBusy(false);
        }));
        dialog.SetPositiveButton(Resource.String.mod_groups_import_action, (_, _) =>
        {
            if (viewCancellation is { IsCancellationRequested: false } lifetime)
                _ = CommitPackageImportAsync(transaction, lifetime.Token);
        });
        dialog.Show();
    }

    private async Task CommitPackageImportAsync(
        ModProfilePackageImportTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var result = transaction.ImportResult
                ?? throw new InvalidDataException("The shared group import result is missing.");
            Interlocked.CompareExchange(ref pendingPackageImport, null, transaction);
            await transaction.DisposeAsync().ConfigureAwait(false);
            modManagement?.NotifyLibraryChanged();
            modManagement?.NotifyProfilesChanged();
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            ShowImportResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposePackageImportAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-import-failed", exception);
            await DisposePackageImportAsync().ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_import_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void ShowImportResult(ModProfileImportResult result)
    {
        if (!IsAdded)
            return;
        Activity?.RunOnUiThread(() => ShowMessage(FormatString(
            Resource.String.mod_groups_import_result,
            new JString(result.Profile.DisplayName),
            Java.Lang.Integer.ValueOf(result.AddedItems.Count),
            Java.Lang.Integer.ValueOf(result.ReusedItems.Count),
            Java.Lang.Integer.ValueOf(result.MissingMembers),
            Java.Lang.Integer.ValueOf(result.DistinctContentCandidates))));
    }

    private async ValueTask DisposePackageImportAsync()
    {
        var pending = Interlocked.Exchange(ref pendingPackageImport, null);
        if (pending is not null)
            await pending.DisposeAsync().ConfigureAwait(false);
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
        finally
        {
            cursor?.Close();
            cursor?.Dispose();
        }
        return null;
    }

    private ModProfileTransferService CreateTransferService() => modManagement?.Transfers
        ?? throw new InvalidOperationException("The Mod management UI session is unavailable.");

    private void TryDeleteDocument(global::Android.Net.Uri uri)
    {
        try
        {
            RequireContext().ContentResolver?.Delete(uri, null, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Java.Lang.SecurityException)
        {
            Log.Warn("JunimoGate.Mods", "profile-export-cleanup-failed", exception);
        }
    }

    private async Task CreateAsync(string displayName, CancellationToken cancellationToken)
    {
        if (profiles is null)
            return;
        SetBusy(true);
        try
        {
            var profile = await profiles.CreateAsync(displayName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            modManagement?.NotifyProfilesChanged();
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() => OpenProfile(profile));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels creation.
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                          InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-create-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_create_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void OpenProfile(ModProfileV2 profile)
    {
        var arguments = new Bundle();
        arguments.PutString("profileId", profile.Id);
        NavHostFragment.FindNavController(this).Navigate(Resource.Id.navigation_mod_group_editor, arguments);
    }

    private void SelectProfile(ModProfileV2 profile)
    {
        if (cancellation is { IsCancellationRequested: false } lifetime)
            _ = SelectAsync(profile, lifetime.Token);
    }

    private async Task SelectAsync(ModProfileV2 profile, CancellationToken cancellationToken)
    {
        if (selection is null)
            return;
        SetBusy(true);
        try
        {
            var current = modManagement is null
                ? await selection.OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken).ConfigureAwait(false)
                : await modManagement.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
            await selection.SetAsync(current.Revision, ProfileId.Parse(profile.Id), cancellationToken)
                .ConfigureAwait(false);
            modManagement?.NotifyActiveProfileChanged();
            await ((MainActivity)RequireActivity())
                .RefreshLauncherProfileAsync(cancellationToken)
                .ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => adapter?.SetActiveProfile(profile.Id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels selection.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-select-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_select_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void RequestDelete(ModGroupListItem item)
    {
        if (!item.CanDelete)
            return;
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.mod_groups_delete_title);
        dialog.SetMessage(FormatString(
            Resource.String.mod_groups_delete_message,
            new JString(item.DisplayName)));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.mods_delete_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = DeleteAsync(item.Profile, lifetime.Token);
        });
        dialog.Show();
    }

    private async Task DeleteAsync(ModProfileV2 profile, CancellationToken cancellationToken)
    {
        if (profiles is null)
            return;
        SetBusy(true);
        try
        {
            await profiles.DeleteAsync(ProfileId.Parse(profile.Id), cancellationToken).ConfigureAwait(false);
            modManagement?.NotifyProfilesChanged();
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels deletion.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "profile-delete-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_delete_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (profiles is null || selection is null || library is null)
            return;
        SetBusy(true);
        try
        {
            var groups = modManagement is null
                ? await profiles.ListAsync(cancellationToken).ConfigureAwait(false)
                : await modManagement.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
            var active = modManagement is null
                ? await selection.OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken).ConfigureAwait(false)
                : await modManagement.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
            var index = modManagement is null
                ? await library.ReadAsync(cancellationToken).ConfigureAwait(false)
                : await modManagement.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
            var ids = index.Items.Select(item => item.LibraryItemId).ToHashSet(StringComparer.Ordinal);
            var rows = groups.Select(profile => new ModGroupListItem(
                    profile,
                    GetDisplayName(profile),
                    profile.Id == active.ActiveProfileId,
                    profile.Members.Count(member => member.LibraryItemId is null || !ids.Contains(member.LibraryItemId)),
                    profile.Id is not (ModProfileV2.NoModsId or "default") && profile.Id != active.ActiveProfileId))
                .ToArray();
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                adapter?.SetItems(rows);
                if (empty is not null)
                    empty.Visibility = rows.Length == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels refresh.
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Error("JunimoGate.Mods", "profile-list-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mod_groups_read_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private string GetDisplayName(ModProfileV2 profile) => profile.Id switch
    {
        ModProfileV2.NoModsId => GetString(Resource.String.mods_no_mods_group),
        "default" => GetString(Resource.String.mods_default_group),
        _ => profile.DisplayName,
    };

    private string FormatSummary(ModGroupListItem item)
    {
        return FormatString(
            Resource.String.mod_groups_item_summary,
            Java.Lang.Integer.ValueOf(item.Profile.Members.Count(member => member.Enabled)),
            Java.Lang.Integer.ValueOf(item.Profile.Members.Count(member => !member.Enabled)),
            Java.Lang.Integer.ValueOf(item.MissingCount));
    }

    private void OnModManagementChanged(object? sender, ModManagementChangedEventArgs eventArgs)
    {
        if (eventArgs.Kind is ModManagementChangeKind.Library or ModManagementChangeKind.Profiles or
            ModManagementChangeKind.ActiveProfile)
        {
            Activity?.RunOnUiThread(() =>
            {
                if (cancellation is { IsCancellationRequested: false } lifetime)
                    _ = RefreshAsync(lifetime.Token);
            });
        }
    }

    private void SetBusy(bool value)
    {
        var isExporting = exportProgressText != 0;
        if (createButton is not null)
            createButton.Enabled = !value && !isExporting;
        if (importButton is not null)
            importButton.Enabled = !value && !isExporting;
        if (progress is not null)
            progress.Visibility = isExporting || value && (adapter?.ItemCount ?? 0) == 0
                ? ViewStates.Visible
                : ViewStates.Gone;
        if (progressLabel is not null)
        {
            progressLabel.Visibility = isExporting
                ? ViewStates.Visible
                : ViewStates.Gone;
            if (isExporting)
                progressLabel.SetText(exportProgressText);
        }
    }

    private void ShowMessage(int resourceId) =>
        Toast.MakeText(RequireContext(), resourceId, ToastLength.Long)?.Show();

    private void ShowMessage(string message) =>
        Toast.MakeText(RequireContext(), message, ToastLength.Long)?.Show();

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The Mod group string resource is unavailable.");

    private sealed class DialogCancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => onCancel();
    }
}

internal sealed record ModGroupListItem(
    ModProfileV2 Profile,
    string DisplayName,
    bool IsActive,
    int MissingCount,
    bool CanDelete);

internal sealed class ModGroupAdapter(
    Func<ModGroupListItem, string> formatSummary,
    Action<ModProfileV2> open,
    Action<ModProfileV2> select,
    Action<ModProfileV2> share,
    Action<ModGroupListItem> delete) : RecyclerView.Adapter
{
    private List<ModGroupListItem> items = [];

    public override int ItemCount => items.Count;

    public void SetItems(IReadOnlyList<ModGroupListItem> value)
    {
        items = [.. value];
        NotifyDataSetChanged();
    }

    public void SetActiveProfile(string profileId)
    {
        var activeIndex = items.FindIndex(item => item.IsActive);
        var selectedIndex = items.FindIndex(item => item.Profile.Id == profileId);
        if (selectedIndex < 0 || activeIndex == selectedIndex)
            return;

        if (activeIndex >= 0)
        {
            var previous = items[activeIndex];
            items[activeIndex] = previous with
            {
                IsActive = false,
                CanDelete = previous.Profile.Id is not (ModProfileV2.NoModsId or "default"),
            };
            NotifyItemChanged(activeIndex);
        }

        items[selectedIndex] = items[selectedIndex] with
        {
            IsActive = true,
            CanDelete = false,
        };
        NotifyItemChanged(selectedIndex);
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_group, parent, false)
            ?? throw new InvalidOperationException("The Mod group item layout could not be created.");
        return new Holder(view, open, select, share, delete);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = items[position];
        ((Holder)holder).Bind(item, formatSummary(item));
    }

    private sealed class Holder : RecyclerView.ViewHolder
    {
        private readonly TextView title;
        private readonly TextView summary;
        private readonly MaterialButton openButton;
        private readonly MaterialRadioButton activeButton;
        private readonly MaterialButton shareButton;
        private readonly MaterialButton deleteButton;
        private readonly Action<ModProfileV2> open;
        private readonly Action<ModProfileV2> select;
        private readonly Action<ModProfileV2> share;
        private readonly Action<ModGroupListItem> delete;
        private ModGroupListItem? item;

        public Holder(
            View view,
            Action<ModProfileV2> open,
            Action<ModProfileV2> select,
            Action<ModProfileV2> share,
            Action<ModGroupListItem> delete) : base(view)
        {
            this.open = open;
            this.select = select;
            this.share = share;
            this.delete = delete;
            title = view.FindViewById<TextView>(Resource.Id.mod_group_title)!;
            summary = view.FindViewById<TextView>(Resource.Id.mod_group_summary)!;
            openButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_open)!;
            activeButton = view.FindViewById<MaterialRadioButton>(Resource.Id.mod_group_active)!;
            shareButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_share)!;
            deleteButton = view.FindViewById<MaterialButton>(Resource.Id.mod_group_delete)!;
            openButton.Click += (_, _) => { if (item is not null) this.open(item.Profile); };
            activeButton.Click += (_, _) => SelectCurrent();
            ItemView.Click += (_, _) => SelectCurrent();
            shareButton.Click += (_, _) => { if (item is not null) this.share(item.Profile); };
            deleteButton.Click += (_, _) => { if (item is not null) this.delete(item); };
        }

        public void Bind(ModGroupListItem value, string detail)
        {
            item = value;
            title.Text = value.DisplayName;
            summary.Text = detail;
            activeButton.Checked = value.IsActive;
            var selectLabel = ItemView.Context?.GetString(
                value.IsActive ? Resource.String.mod_groups_selected : Resource.String.mod_groups_select);
            activeButton.ContentDescription = selectLabel;
            activeButton.TooltipText = selectLabel;
            deleteButton.Visibility = value.CanDelete ? ViewStates.Visible : ViewStates.Gone;
        }

        private void SelectCurrent()
        {
            if (item is { IsActive: false } value)
                select(value.Profile);
        }
    }
}
