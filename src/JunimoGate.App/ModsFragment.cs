using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Text.Format;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AndroidX.Navigation.Fragment;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Android;
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
    private const int ImportArchiveRequestCode = 4701;
    private MaterialButton? importButton;
    private SearchView? search;
    private LinearProgressIndicator? progress;
    private TextView? empty;
    private TextView? groupSummary;
    private MaterialButton? manageGroupsButton;
    private ModLibraryAdapter? adapter;
    private ModLibraryRepository? repository;
    private ModProfileV2Repository? profiles;
    private ActiveModProfileSelectionRepository? activeProfile;
    private CancellationTokenSource? cancellation;
    private IModArchiveInstallTransaction? pendingTransaction;
    private bool busy;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mods, container, false)
        ?? throw new InvalidOperationException("The Mods layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        importButton = view.FindViewById<MaterialButton>(Resource.Id.mods_import)
            ?? throw new InvalidOperationException("The Mod import button is unavailable.");
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.mods_progress)
            ?? throw new InvalidOperationException("The Mod progress indicator is unavailable.");
        search = view.FindViewById<SearchView>(Resource.Id.mods_search)
            ?? throw new InvalidOperationException("The Mod search input is unavailable.");
        empty = view.FindViewById<TextView>(Resource.Id.mods_empty)
            ?? throw new InvalidOperationException("The Mod empty state is unavailable.");
        groupSummary = view.FindViewById<TextView>(Resource.Id.mods_group_summary)
            ?? throw new InvalidOperationException("The Mod group summary is unavailable.");
        manageGroupsButton = view.FindViewById<MaterialButton>(Resource.Id.mods_manage_groups)
            ?? throw new InvalidOperationException("The Mod group action is unavailable.");
        var list = view.FindViewById<RecyclerView>(Resource.Id.mods_list)
            ?? throw new InvalidOperationException("The Mod library list is unavailable.");
        adapter = new ModLibraryAdapter(FormatItemSummary, ShowDetails, RequestDelete);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        importButton.Click += OnImportClicked;
        manageGroupsButton.Click += OnManageGroupsClicked;
        search.QueryTextChange += OnSearchChanged;
    }

    public override void OnStart()
    {
        base.OnStart();
        cancellation = new CancellationTokenSource();
        repository = CreateRepository();
        var profilesRoot = Path.Combine(AndroidPrivateStorage.GetUserDataRoot(RequireContext()), "profiles");
        profiles = new ModProfileV2Repository(profilesRoot);
        activeProfile = new ActiveModProfileSelectionRepository(profilesRoot);
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
        repository = null;
        profiles = null;
        activeProfile = null;
        SetBusy(false);
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (importButton is not null)
            importButton.Click -= OnImportClicked;
        if (search is not null)
            search.QueryTextChange -= OnSearchChanged;
        if (manageGroupsButton is not null)
            manageGroupsButton.Click -= OnManageGroupsClicked;
        importButton = null;
        search = null;
        progress = null;
        empty = null;
        groupSummary = null;
        manageGroupsButton = null;
        adapter = null;
        base.OnDestroyView();
    }

#pragma warning disable CS0618, CS0672 // Fragment activity-result API is scoped to this lifecycle and retains no external path.
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ImportArchiveRequestCode || resultCode != (int)Result.Ok || data?.Data is not { } uri)
            return;
        if (cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        _ = ScanArchiveAsync(uri, lifetime.Token);
    }
#pragma warning restore CS0618, CS0672

    private void OnImportClicked(object? sender, EventArgs eventArgs)
    {
        if (busy)
            return;
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/zip");
        intent.PutExtra(Intent.ExtraMimeTypes, new[]
        {
            "application/zip",
            "application/x-zip-compressed",
            "application/octet-stream",
        });
#pragma warning disable CS0618 // See OnActivityResult; SAF grants only the selected document stream.
        StartActivityForResult(intent, ImportArchiveRequestCode);
#pragma warning restore CS0618
    }

    private void OnSearchChanged(object? sender, SearchView.QueryTextChangeEventArgs eventArgs)
    {
        adapter?.SetQuery(eventArgs.NewText);
        UpdateEmptyState();
    }

    private void OnManageGroupsClicked(object? sender, EventArgs eventArgs) =>
        NavHostFragment.FindNavController(this).Navigate(Resource.Id.navigation_mod_groups);

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
                Log.Info(
                    "JunimoGate.Mods",
                    $"profile-migration already={(migration.AlreadyMigrated ? 1 : 0)} imported={migration.ImportedItems} reused={migration.ReusedItems} enabled={migration.EnabledMembers} disabled={migration.DisabledMembers}");
            }
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
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Mods", "archive-scan-failed", exception);
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
            await RefreshAllAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded)
            {
                Activity?.RunOnUiThread(() =>
                {
                    SetBusy(false);
                    var count = result.AllItems.Count;
                    ShowMessage(Resources?.GetQuantityString(
                        Resource.Plurals.mods_import_completed,
                        count,
                        [Java.Lang.Integer.ValueOf(count)]) ?? GetString(Resource.String.mods_import_failed));
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
            var index = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => Render(index.Items));
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
        var index = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
        ModProfileV2? selectedProfile = null;
        if (profiles is not null && activeProfile is not null)
        {
            var selection = await activeProfile
                .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                selectedProfile = await profiles
                    .ReadAsync(ProfileId.Parse(selection.ActiveProfileId), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                selectedProfile = await profiles
                    .ReadAsync(ProfileId.Parse(ModProfileV2.NoModsId), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        if (!IsAdded || cancellationToken.IsCancellationRequested)
            return;
        Activity?.RunOnUiThread(() =>
        {
            Render(index.Items);
            RenderGroupSummary(selectedProfile, index);
        });
    }

    private void RenderGroupSummary(ModProfileV2? profile, ModLibraryIndex library)
    {
        if (groupSummary is null)
            return;
        if (profile is null)
        {
            groupSummary.SetText(Resource.String.mods_group_unavailable);
            return;
        }
        var ids = library.Items.Select(item => item.LibraryItemId).ToHashSet(StringComparer.Ordinal);
        var enabledCount = profile.Members.Count(member => member.Enabled);
        var missingCount = profile.Members.Count(member => member.LibraryItemId is null || !ids.Contains(member.LibraryItemId));
        var name = profile.Id == ModProfileV2.NoModsId
            ? GetString(Resource.String.mods_no_mods_group)
            : profile.Id == "default"
                ? GetString(Resource.String.mods_default_group)
                : profile.DisplayName;
        groupSummary.Text = FormatString(
            Resource.String.mods_group_summary,
            new JString(name),
            Java.Lang.Integer.ValueOf(enabledCount),
            Java.Lang.Integer.ValueOf(missingCount));
    }

    private void Render(IReadOnlyList<ModLibraryItem> items)
    {
        adapter?.SetItems(items);
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        if (empty is null || adapter is null)
            return;
        empty.Visibility = adapter.ItemCount == 0 ? ViewStates.Visible : ViewStates.Gone;
        empty.SetText(adapter.TotalCount == 0 ? Resource.String.mods_empty : Resource.String.mods_search_empty);
    }

    private void ShowDetails(ModLibraryItem item)
    {
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

    private void RequestDelete(ModLibraryItem item)
    {
        if (busy || repository is null)
            return;
        var deleteDialog = new MaterialAlertDialogBuilder(RequireContext());
        deleteDialog.SetTitle(Resource.String.mods_delete_title);
        deleteDialog.SetMessage(FormatString(
            Resource.String.mods_delete_message,
            new JString(item.Manifest.Name),
            new JString(item.Manifest.Version)));
        deleteDialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        deleteDialog.SetPositiveButton(Resource.String.mods_delete_action, (_, _) =>
        {
            if (cancellation is { IsCancellationRequested: false } lifetime)
                _ = DeleteAsync(item, lifetime.Token);
        });
        deleteDialog.Show();
    }

    private async Task DeleteAsync(ModLibraryItem item, CancellationToken cancellationToken)
    {
        if (repository is null)
            return;
        SetBusy(true);
        try
        {
            await repository.DeleteAsync(item.LibraryItemId, cancellationToken).ConfigureAwait(false);
            await RefreshAllAsync(cancellationToken).ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.mods_delete_completed));
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

    private string FormatItemSummary(ModLibraryItem item, int installedVersions)
    {
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
            new JString(item.Manifest.Author),
            new JString(item.Manifest.UniqueId),
            new JString(versions),
            new JString(files),
            new JString(size));
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
        finally
        {
            cursor?.Close();
            cursor?.Dispose();
        }
        return null;
    }

    private ModLibraryRepository CreateRepository() => new(
        Path.Combine(AndroidPrivateStorage.GetUserDataRoot(RequireContext()), "mods"));

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
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
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
    Func<ModLibraryItem, int, string> formatSummary,
    Action<ModLibraryItem> showDetails,
    Action<ModLibraryItem> delete) : RecyclerView.Adapter
{
    private IReadOnlyList<ModLibraryItem> allItems = Array.Empty<ModLibraryItem>();
    private IReadOnlyList<ModLibraryItem> items = Array.Empty<ModLibraryItem>();
    private IReadOnlyDictionary<string, int> versionCounts = new Dictionary<string, int>();
    private string query = string.Empty;

    public override int ItemCount => items.Count;
    public int TotalCount => allItems.Count;

    public void SetItems(IReadOnlyList<ModLibraryItem> value)
    {
        allItems = value;
        versionCounts = value
            .GroupBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        ApplyFilter();
    }

    public void SetQuery(string? value)
    {
        query = value?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        items = string.IsNullOrEmpty(query)
            ? allItems
            : allItems.Where(item =>
                    Contains(item.Manifest.Name, query) ||
                    Contains(item.Manifest.Author, query) ||
                    Contains(item.Manifest.UniqueId, query) ||
                    Contains(item.Manifest.Version, query))
                .ToArray();
        NotifyDataSetChanged();
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_library, parent, false)
            ?? throw new InvalidOperationException("The Mod library item layout could not be created.");
        return new ModLibraryViewHolder(view, showDetails, delete);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var item = items[position];
        ((ModLibraryViewHolder)holder).Bind(item, formatSummary(item, versionCounts[item.Manifest.UniqueId]));
    }

    private sealed class ModLibraryViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView title;
        private readonly TextView summary;
        private readonly MaterialButton deleteButton;
        private readonly MaterialButton detailsButton;
        private readonly Action<ModLibraryItem> showDetails;
        private readonly Action<ModLibraryItem> delete;
        private ModLibraryItem? item;

        public ModLibraryViewHolder(
            View view,
            Action<ModLibraryItem> showDetails,
            Action<ModLibraryItem> delete) : base(view)
        {
            this.showDetails = showDetails;
            this.delete = delete;
            title = view.FindViewById<TextView>(Resource.Id.mod_item_title)
                ?? throw new InvalidOperationException("The Mod item title is unavailable.");
            summary = view.FindViewById<TextView>(Resource.Id.mod_item_summary)
                ?? throw new InvalidOperationException("The Mod item summary is unavailable.");
            deleteButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_delete)
                ?? throw new InvalidOperationException("The Mod item delete button is unavailable.");
            detailsButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_details)
                ?? throw new InvalidOperationException("The Mod item details button is unavailable.");
            detailsButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.showDetails(item);
            };
            deleteButton.Click += (_, _) =>
            {
                if (item is not null)
                    this.delete(item);
            };
        }

        public void Bind(ModLibraryItem value, string detail)
        {
            item = value;
            title.Text = $"{value.Manifest.Name} {value.Manifest.Version}";
            summary.Text = detail;
        }
    }
}
