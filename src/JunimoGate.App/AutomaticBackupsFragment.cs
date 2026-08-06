using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Core;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.AutomaticBackupsFragment")]
public sealed class AutomaticBackupsFragment : Fragment
{
    private const int ExportRequestCode = 4811;
    private TextView? count;
    private TextView? empty;
    private LinearProgressIndicator? progress;
    private RecyclerView? list;
    private BackupAdapter? adapter;
    private CancellationTokenSource? cancellation;
    private SaveManagementUiSession? session;
    private SaveBackupEntry? pendingExport;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_automatic_backups, container, false)
        ?? throw new InvalidOperationException("The automatic backup layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        count = view.FindViewById<TextView>(Resource.Id.automatic_backups_count);
        empty = view.FindViewById<TextView>(Resource.Id.automatic_backups_empty);
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.automatic_backups_progress);
        list = view.FindViewById<RecyclerView>(Resource.Id.automatic_backups_list)
            ?? throw new InvalidOperationException("The automatic backup list is unavailable.");
        adapter = new BackupAdapter(new SaveUiFormatter(RequireContext()), Restore, Export);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
    }

    public override void OnStart()
    {
        base.OnStart();
        session = ((SaveBackupsFragment?)ParentFragment)?.Session
            ?? throw new InvalidOperationException("The save management session is unavailable.");
        session.Changed += OnChanged;
        cancellation = new CancellationTokenSource();
        _ = LoadAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        if (session is not null)
            session.Changed -= OnChanged;
        session = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        pendingExport = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        list?.SetAdapter(null);
        list = null;
        count = null;
        empty = null;
        progress = null;
        adapter = null;
        base.OnDestroyView();
    }

#pragma warning disable CS0618, CS0672
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ExportRequestCode)
            return;
        var backup = pendingExport;
        pendingExport = null;
        if (resultCode == (int)Result.Ok && data?.Data is { } uri && backup is not null)
            _ = ExportAsync(backup, uri);
    }
#pragma warning restore CS0618, CS0672

    private void OnChanged() => Activity?.RunOnUiThread(() =>
    {
        if (cancellation is { IsCancellationRequested: false } lifetime)
            _ = LoadAsync(lifetime.Token);
    });

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (session is null)
            return;
        SetBusy(true);
        try
        {
            var overview = await session.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                adapter?.Submit(overview.Backups.Entries);
                if (count is not null)
                    count.Text = GetString(Resource.String.saves_backup_count, overview.Backups.Entries.Count);
                if (empty is not null)
                    empty.Visibility = overview.Backups.Entries.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("JunimoGate.Saves", "automatic-backup-list-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.save_backups_read_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void Restore(SaveBackupEntry backup)
    {
        if (session?.IsGameRunning != false)
        {
            Toast.MakeText(RequireContext(), Resource.String.saves_close_game_first, ToastLength.Long)?.Show();
            return;
        }
        _ = StageRestoreAsync(backup);
    }

    private async Task StageRestoreAsync(SaveBackupEntry backup)
    {
        if (session is null || cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        SetBusy(true);
        try
        {
            var path = await session.StageBackupAsync(backup, null, lifetime.Token).ConfigureAwait(false);
            if (IsAdded && !lifetime.IsCancellationRequested)
                Activity?.RunOnUiThread(() => SaveImportBottomSheet.ShowForArchive(ParentFragmentManager, path));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("JunimoGate.Saves", "save-backup-restore-stage-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.saves_restore_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void Export(SaveBackupEntry backup)
    {
        pendingExport = backup;
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/zip");
        intent.PutExtra(Intent.ExtraTitle, new SaveUiFormatter(RequireContext()).BackupExportFileName(backup));
#pragma warning disable CS0618
        StartActivityForResult(intent, ExportRequestCode);
#pragma warning restore CS0618
    }

    private async Task ExportAsync(SaveBackupEntry backup, global::Android.Net.Uri uri)
    {
        if (session is null || cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        SetBusy(true);
        try
        {
            await using var output = RequireContext().ContentResolver?.OpenOutputStream(uri, "w")
                ?? throw new IOException("The selected backup document could not be opened.");
            await session.ExportBackupAsync(backup, output, lifetime.Token).ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.save_backups_export_complete, ToastLength.Long)?.Show());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            TryDeleteDocument(uri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Saves", "automatic-backup-export-failed", exception);
            TryDeleteDocument(uri);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.save_backups_export_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void TryDeleteDocument(global::Android.Net.Uri uri)
    {
        try
        {
            _ = RequireContext().ContentResolver?.Delete(uri, null, null);
        }
        catch (Exception exception) when (exception is Java.Lang.SecurityException or InvalidOperationException)
        {
            Log.Warn("JunimoGate.Saves", "save-backup-document-cleanup-failed", exception);
        }
    }

    private void SetBusy(bool value)
    {
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        adapter?.SetEnabled(!value);
    }

    private sealed class BackupAdapter(
        SaveUiFormatter formatter,
        Action<SaveBackupEntry> restore,
        Action<SaveBackupEntry> export) : RecyclerView.Adapter
    {
        private IReadOnlyList<SaveBackupEntry> items = [];
        private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
        private bool enabled = true;

        public override int ItemCount => items.Count;

        public void Submit(IReadOnlyList<SaveBackupEntry> value)
        {
            items = value;
            expanded.RemoveWhere(name => value.All(item => !item.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)));
            NotifyDataSetChanged();
        }

        public void SetEnabled(bool value)
        {
            enabled = value;
            NotifyDataSetChanged();
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_save_backup, parent, false)
                ?? throw new InvalidOperationException("The automatic backup row could not be created.");
            return new Holder(view);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var item = items[position];
            var view = (Holder)holder;
            view.Title.Text = formatter.FormatDateTime(item.LastWriteTimeUtc);
            view.Summary.Text = formatter.BackupDetails(item);
            view.Contents.Text = formatter.BackupContents(item);
            var isExpanded = expanded.Contains(item.FileName);
            view.Expanded.Visibility = isExpanded ? ViewStates.Visible : ViewStates.Gone;
            view.Restore.Enabled = enabled;
            view.Export.Enabled = enabled;

            view.ItemView.Click -= view.ItemClick;
            view.ItemClick = (_, _) =>
            {
                if (!expanded.Add(item.FileName))
                    expanded.Remove(item.FileName);
                NotifyItemChanged(position);
            };
            view.ItemView.Click += view.ItemClick;

            view.Restore.Click -= view.RestoreClick;
            view.RestoreClick = (_, _) => restore(item);
            view.Restore.Click += view.RestoreClick;
            view.Export.Click -= view.ExportClick;
            view.ExportClick = (_, _) => export(item);
            view.Export.Click += view.ExportClick;
        }

        private sealed class Holder(View itemView) : RecyclerView.ViewHolder(itemView)
        {
            public TextView Title { get; } = itemView.FindViewById<TextView>(Resource.Id.save_backup_title)!;
            public TextView Summary { get; } = itemView.FindViewById<TextView>(Resource.Id.save_backup_summary)!;
            public TextView Contents { get; } = itemView.FindViewById<TextView>(Resource.Id.save_backup_contents)!;
            public View Expanded { get; } = itemView.FindViewById<View>(Resource.Id.save_backup_expanded)!;
            public MaterialButton Restore { get; } = itemView.FindViewById<MaterialButton>(Resource.Id.save_backup_restore)!;
            public MaterialButton Export { get; } = itemView.FindViewById<MaterialButton>(Resource.Id.save_backup_export)!;
            public EventHandler? ItemClick { get; set; }
            public EventHandler? RestoreClick { get; set; }
            public EventHandler? ExportClick { get; set; }
        }
    }
}
