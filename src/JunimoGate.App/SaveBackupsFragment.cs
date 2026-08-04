using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Core;
using Log = JunimoGate.Android.JunimoGateLog;
using AndroidDateFormat = Android.Text.Format.DateFormat;
using Fragment = AndroidX.Fragment.App.Fragment;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.SaveBackupsFragment")]
public sealed class SaveBackupsFragment : Fragment
{
    private const int ExportBackupRequestCode = 4701;
    private TextView? summary;
    private TextView? empty;
    private CircularProgressIndicator? progress;
    private BackupAdapter? adapter;
    private CancellationTokenSource? loadCancellation;
    private CancellationTokenSource? viewCancellation;
    private SaveBackupEntry? pendingExport;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_save_backups, container, false)
        ?? throw new InvalidOperationException("The save backups layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        summary = view.FindViewById<TextView>(Resource.Id.save_backups_summary);
        empty = view.FindViewById<TextView>(Resource.Id.save_backups_empty);
        progress = view.FindViewById<CircularProgressIndicator>(Resource.Id.save_backups_progress);
        var list = view.FindViewById<RecyclerView>(Resource.Id.save_backups_list)
            ?? throw new InvalidOperationException("The save backup list is unavailable.");
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        adapter = new BackupAdapter(FormatBackupSubtitle, ExportBackup);
        list.SetAdapter(adapter);
        viewCancellation = new CancellationTokenSource();
    }

    public override void OnStart()
    {
        base.OnStart();
        loadCancellation = new CancellationTokenSource();
        _ = LoadAsync(loadCancellation.Token);
    }

    public override void OnStop()
    {
        loadCancellation?.Cancel();
        loadCancellation?.Dispose();
        loadCancellation = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        viewCancellation?.Cancel();
        viewCancellation?.Dispose();
        viewCancellation = null;
        pendingExport = null;
        summary = null;
        empty = null;
        progress = null;
        adapter = null;
        base.OnDestroyView();
    }

#pragma warning disable CS0618, CS0672
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ExportBackupRequestCode)
            return;
        if (resultCode != (int)Result.Ok)
            pendingExport = null;
        if (resultCode != (int)Result.Ok || data?.Data is not { } uri || pendingExport is not { } backup ||
            viewCancellation is not { IsCancellationRequested: false } lifetime)
        {
            return;
        }
        pendingExport = null;
        _ = ExportBackupAsync(uri, backup, lifetime.Token);
    }
#pragma warning restore CS0618, CS0672

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (progress is not null)
            progress.Visibility = ViewStates.Visible;
        try
        {
            var overview = await Task.Run(
                () => new SaveBackupService(RequireContext()).Read(),
                cancellationToken);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => Render(overview));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("JunimoGate.Saves", "save-backup-list-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.save_backups_read_failed));
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && IsAdded)
                Activity?.RunOnUiThread(() => { if (progress is not null) progress.Visibility = ViewStates.Gone; });
        }
    }

    private void Render(SaveBackupOverview overview)
    {
        adapter?.Submit(overview.Backups);
        if (empty is not null)
            empty.Visibility = overview.Backups.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        if (summary is null)
            return;
        var latest = overview.LatestSaveTimeUtc is null
            ? GetString(Resource.String.save_backups_no_live_save)
            : FormatString(
                Resource.String.save_backups_latest_save,
                new Java.Lang.String(overview.LatestSaveName ?? "—"),
                new Java.Lang.String(FormatDateTime(overview.LatestSaveTimeUtc.Value)));
        summary.Text = FormatString(
            Resource.String.save_backups_overview,
            Java.Lang.Integer.ValueOf(overview.LiveSaveCount),
            Java.Lang.Integer.ValueOf(overview.Backups.Count),
            new Java.Lang.String(latest),
            Java.Lang.Integer.ValueOf(overview.UnavailableBackupCount));
    }

    private void ExportBackup(SaveBackupEntry backup)
    {
        pendingExport = backup;
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/zip");
        intent.PutExtra(Intent.ExtraTitle, backup.FileName);
#pragma warning disable CS0618
        StartActivityForResult(intent, ExportBackupRequestCode);
#pragma warning restore CS0618
    }

    private async Task ExportBackupAsync(
        global::Android.Net.Uri uri,
        SaveBackupEntry backup,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var output = RequireContext().ContentResolver?.OpenOutputStream(uri, "w")
                ?? throw new IOException("The selected save backup document could not be opened.");
            await new SaveBackupService(RequireContext())
                .ExportAsync(backup.FileName, output, cancellationToken);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.save_backups_export_complete));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDocument(uri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Saves", "save-backup-export-failed", exception);
            TryDeleteDocument(uri);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.save_backups_export_failed));
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

    private string FormatBackupSubtitle(SaveBackupEntry backup) => FormatString(
        Resource.String.save_backups_item_summary,
        new Java.Lang.String(FormatDateTime(backup.LastWriteTimeUtc)),
        new Java.Lang.String(global::Android.Text.Format.Formatter.FormatShortFileSize(RequireContext(), backup.Size) ?? $"{backup.Size} B"),
        Java.Lang.Integer.ValueOf(backup.SaveEntryCount));

    private string FormatDateTime(DateTimeOffset value)
    {
        using var date = new Java.Util.Date(value.ToUnixTimeMilliseconds());
        var dateText = AndroidDateFormat.GetMediumDateFormat(RequireContext())?.Format(date) ?? "—";
        var timeText = AndroidDateFormat.GetTimeFormat(RequireContext())?.Format(date) ?? "—";
        return FormatString(
            Resource.String.date_time_value,
            new Java.Lang.String(dateText),
            new Java.Lang.String(timeText));
    }

    private string FormatString(int resourceId, params Java.Lang.Object[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The save backup string resource is unavailable.");

    private void ShowMessage(int resourceId) =>
        Toast.MakeText(RequireContext(), resourceId, ToastLength.Long)?.Show();

    private sealed class BackupAdapter(
        Func<SaveBackupEntry, string> formatSubtitle,
        Action<SaveBackupEntry> export) : RecyclerView.Adapter
    {
        private IReadOnlyList<SaveBackupEntry> items = [];

        public override int ItemCount => items.Count;

        public void Submit(IReadOnlyList<SaveBackupEntry> value)
        {
            items = value;
            NotifyDataSetChanged();
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_save_backup, parent, false)
                ?? throw new InvalidOperationException("The save backup row could not be created.");
            return new BackupViewHolder(view);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var view = (BackupViewHolder)holder;
            var item = items[position];
            view.Title.Text = item.FileName;
            view.Summary.Text = formatSubtitle(item);
            view.Export.Click -= view.ExportHandler;
            view.ExportHandler = (_, _) => export(item);
            view.Export.Click += view.ExportHandler;
        }

        private sealed class BackupViewHolder : RecyclerView.ViewHolder
        {
            public BackupViewHolder(View itemView) : base(itemView)
            {
                Title = itemView.FindViewById<TextView>(Resource.Id.save_backup_title)!;
                Summary = itemView.FindViewById<TextView>(Resource.Id.save_backup_summary)!;
                Export = itemView.FindViewById<MaterialButton>(Resource.Id.save_backup_export)!;
            }

            public TextView Title { get; }
            public TextView Summary { get; }
            public MaterialButton Export { get; }
            public EventHandler? ExportHandler { get; set; }
        }
    }
}
