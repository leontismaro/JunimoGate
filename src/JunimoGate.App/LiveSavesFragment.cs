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

[Register("org.junimogate.app.LiveSavesFragment")]
public sealed class LiveSavesFragment : Fragment
{
    private const int ImportRequestCode = 4801;
    private const int ExportRequestCode = 4802;
    private TextView? count;
    private TextView? empty;
    private LinearProgressIndicator? progress;
    private MaterialButton? importButton;
    private SaveAdapter? adapter;
    private CancellationTokenSource? cancellation;
    private SaveManagementUiSession? session;
    private LiveSaveGameEntry? pendingExport;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_live_saves, container, false)
        ?? throw new InvalidOperationException("The live saves layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        count = view.FindViewById<TextView>(Resource.Id.live_saves_count);
        empty = view.FindViewById<TextView>(Resource.Id.live_saves_empty);
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.live_saves_progress);
        importButton = view.FindViewById<MaterialButton>(Resource.Id.live_saves_import);
        var list = view.FindViewById<RecyclerView>(Resource.Id.live_saves_list)
            ?? throw new InvalidOperationException("The live save list is unavailable.");
        adapter = new SaveAdapter(new SaveUiFormatter(RequireContext()), Export);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        importButton!.Click += OnImportClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        session = ((SaveBackupsFragment?)ParentFragment)?.Session
            ?? throw new InvalidOperationException("The save management session is unavailable.");
        session.Changed += OnChanged;
        cancellation = new CancellationTokenSource();
        importButton!.Enabled = !session.IsGameRunning;
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
        if (importButton is not null)
            importButton.Click -= OnImportClicked;
        count = null;
        empty = null;
        progress = null;
        importButton = null;
        adapter = null;
        base.OnDestroyView();
    }

#pragma warning disable CS0618, CS0672
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == ImportRequestCode && resultCode == (int)Result.Ok && data?.Data is { } importUri)
        {
            _ = StageImportAsync(importUri);
            return;
        }
        if (requestCode == ExportRequestCode)
        {
            var save = pendingExport;
            pendingExport = null;
            if (resultCode == (int)Result.Ok && data?.Data is { } exportUri && save is not null)
                _ = ExportAsync(save, exportUri);
        }
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
                adapter?.Submit(overview.Saves);
                if (count is not null)
                    count.Text = GetString(Resource.String.saves_live_count, overview.Saves.Count);
                if (empty is not null)
                    empty.Visibility = overview.Saves.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("JunimoGate.Saves", "live-save-list-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.save_backups_read_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void OnImportClicked(object? sender, EventArgs eventArgs)
    {
        if (session?.IsGameRunning != false)
        {
            Toast.MakeText(RequireContext(), Resource.String.saves_close_game_first, ToastLength.Long)?.Show();
            return;
        }
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/zip");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/zip", "application/x-zip-compressed", "application/octet-stream" });
#pragma warning disable CS0618
        StartActivityForResult(intent, ImportRequestCode);
#pragma warning restore CS0618
    }

    private async Task StageImportAsync(global::Android.Net.Uri uri)
    {
        if (session is null || cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        SetBusy(true);
        try
        {
            var path = await session.StageDocumentAsync(uri, null, lifetime.Token).ConfigureAwait(false);
            if (IsAdded && !lifetime.IsCancellationRequested)
                Activity?.RunOnUiThread(() => SaveImportBottomSheet.ShowForArchive(ParentFragmentManager, path));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("JunimoGate.Saves", "save-import-stage-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.saves_import_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false));
        }
    }

    private void Export(LiveSaveGameEntry save)
    {
        pendingExport = save;
        var title = new SaveUiFormatter(RequireContext()).SaveExportFileName(save);
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/zip");
        intent.PutExtra(Intent.ExtraTitle, title);
#pragma warning disable CS0618
        StartActivityForResult(intent, ExportRequestCode);
#pragma warning restore CS0618
    }

    private async Task ExportAsync(LiveSaveGameEntry save, global::Android.Net.Uri uri)
    {
        if (session is null || cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        SetBusy(true);
        try
        {
            await using var output = RequireContext().ContentResolver?.OpenOutputStream(uri, "w")
                ?? throw new IOException("The selected save document could not be opened.");
            await session.ExportSaveAsync(save, output, null, lifetime.Token).ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.saves_export_complete, ToastLength.Long)?.Show());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            TryDeleteDocument(uri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Saves", "live-save-export-failed", exception);
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
            _ = Context?.ContentResolver?.Delete(uri, null, null);
        }
        catch (Exception exception) when (exception is Java.Lang.SecurityException or InvalidOperationException)
        {
            Log.Warn("JunimoGate.Saves", "live-save-document-cleanup-failed", exception);
        }
    }

    private void SetBusy(bool value)
    {
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        if (importButton is not null)
            importButton.Enabled = !value && session?.IsGameRunning == false;
    }

    private sealed class SaveAdapter(SaveUiFormatter formatter, Action<LiveSaveGameEntry> export) : RecyclerView.Adapter
    {
        private IReadOnlyList<LiveSaveGameEntry> items = [];
        private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);

        public override int ItemCount => items.Count;

        public void Submit(IReadOnlyList<LiveSaveGameEntry> value)
        {
            items = value;
            expanded.RemoveWhere(name => value.All(item => !item.DirectoryName.Equals(name, StringComparison.OrdinalIgnoreCase)));
            NotifyDataSetChanged();
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_live_save, parent, false)
                ?? throw new InvalidOperationException("The live save row could not be created.");
            return new Holder(view);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
        {
            var item = items[position];
            var view = (Holder)holder;
            view.Title.Text = formatter.Title(item.Metadata);
            view.Summary.Text = formatter.Summary(item.Metadata);
            view.Details.Text = formatter.Details(item.Metadata, item.LastWriteTimeUtc);
            view.Warning.Visibility = item.Status == SaveGameEntryStatus.Ready ? ViewStates.Gone : ViewStates.Visible;
            view.Warning.SetText(item.Status == SaveGameEntryStatus.Incomplete
                ? Resource.String.saves_incomplete
                : Resource.String.saves_metadata_unreadable);
            var isExpanded = expanded.Contains(item.DirectoryName);
            view.Actions.Visibility = isExpanded ? ViewStates.Visible : ViewStates.Gone;
            view.ItemView.Click -= view.ItemClick;
            view.ItemClick = (_, _) =>
            {
                if (!expanded.Add(item.DirectoryName))
                    expanded.Remove(item.DirectoryName);
                NotifyItemChanged(position);
            };
            view.ItemView.Click += view.ItemClick;
            view.Export.Click -= view.ExportClick;
            view.ExportClick = (_, _) => export(item);
            view.Export.Click += view.ExportClick;
        }

        private sealed class Holder(View itemView) : RecyclerView.ViewHolder(itemView)
        {
            public TextView Title { get; } = itemView.FindViewById<TextView>(Resource.Id.live_save_title)!;
            public TextView Summary { get; } = itemView.FindViewById<TextView>(Resource.Id.live_save_summary)!;
            public TextView Details { get; } = itemView.FindViewById<TextView>(Resource.Id.live_save_details)!;
            public TextView Warning { get; } = itemView.FindViewById<TextView>(Resource.Id.live_save_warning)!;
            public View Actions { get; } = itemView.FindViewById<View>(Resource.Id.live_save_actions)!;
            public MaterialButton Export { get; } = itemView.FindViewById<MaterialButton>(Resource.Id.live_save_export)!;
            public EventHandler? ItemClick { get; set; }
            public EventHandler? ExportClick { get; set; }
        }
    }
}
