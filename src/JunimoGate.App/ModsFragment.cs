using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
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
    private LinearProgressIndicator? progress;
    private TextView? empty;
    private ModLibraryAdapter? adapter;
    private ModLibraryRepository? repository;
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
        empty = view.FindViewById<TextView>(Resource.Id.mods_empty)
            ?? throw new InvalidOperationException("The Mod empty state is unavailable.");
        var list = view.FindViewById<RecyclerView>(Resource.Id.mods_list)
            ?? throw new InvalidOperationException("The Mod library list is unavailable.");
        adapter = new ModLibraryAdapter(FormatItemSummary, RequestDelete);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        importButton.Click += OnImportClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        cancellation = new CancellationTokenSource();
        repository = CreateRepository();
        _ = RefreshAsync(cancellation.Token);
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
        SetBusy(false);
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (importButton is not null)
            importButton.Click -= OnImportClicked;
        importButton = null;
        progress = null;
        empty = null;
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
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
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

    private void Render(IReadOnlyList<ModLibraryItem> items)
    {
        adapter?.SetItems(items);
        if (empty is not null)
            empty.Visibility = items.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
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
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
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

    private string FormatItemSummary(ModLibraryItem item)
    {
        var size = global::Android.Text.Format.Formatter.FormatFileSize(RequireContext(), item.TotalBytes) ?? "—";
        var files = Resources?.GetQuantityString(
            Resource.Plurals.environment_file_count,
            item.FileCount,
            [Java.Lang.Integer.ValueOf(item.FileCount)]) ?? item.FileCount.ToString();
        return FormatString(
            Resource.String.mods_item_summary,
            new JString(item.Manifest.Author),
            new JString(item.Manifest.UniqueId),
            new JString(files),
            new JString(size));
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
    Func<ModLibraryItem, string> formatSummary,
    Action<ModLibraryItem> delete) : RecyclerView.Adapter
{
    private IReadOnlyList<ModLibraryItem> items = Array.Empty<ModLibraryItem>();

    public override int ItemCount => items.Count;

    public void SetItems(IReadOnlyList<ModLibraryItem> value)
    {
        items = value;
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_mod_library, parent, false)
            ?? throw new InvalidOperationException("The Mod library item layout could not be created.");
        return new ModLibraryViewHolder(view, delete);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
        ((ModLibraryViewHolder)holder).Bind(items[position], formatSummary(items[position]));

    private sealed class ModLibraryViewHolder : RecyclerView.ViewHolder
    {
        private readonly TextView title;
        private readonly TextView summary;
        private readonly MaterialButton deleteButton;
        private readonly Action<ModLibraryItem> delete;
        private ModLibraryItem? item;

        public ModLibraryViewHolder(View view, Action<ModLibraryItem> delete) : base(view)
        {
            this.delete = delete;
            title = view.FindViewById<TextView>(Resource.Id.mod_item_title)
                ?? throw new InvalidOperationException("The Mod item title is unavailable.");
            summary = view.FindViewById<TextView>(Resource.Id.mod_item_summary)
                ?? throw new InvalidOperationException("The Mod item summary is unavailable.");
            deleteButton = view.FindViewById<MaterialButton>(Resource.Id.mod_item_delete)
                ?? throw new InvalidOperationException("The Mod item delete button is unavailable.");
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
