using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Core;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.SaveImportBottomSheet")]
public sealed class SaveImportBottomSheet : BottomSheetDialogFragment
{
    private const string FragmentTag = "save-import";
    private const string ArchivePathArgument = "archivePath";
    private CancellationTokenSource? cancellation;
    private SaveManagementUiSession? session;
    private SaveImportAdapter? adapter;
    private TextView? summary;
    private TextView? empty;
    private LinearProgressIndicator? progress;
    private MaterialButton? importButton;
    private RecyclerView? list;
    private string? archivePath;
    private bool completed;

    internal static void ShowForArchive(AndroidX.Fragment.App.FragmentManager manager, string archivePath)
    {
        if (manager.FindFragmentByTag(FragmentTag) is not null)
            return;
        var fragment = new SaveImportBottomSheet();
        var arguments = new Bundle();
        arguments.PutString(ArchivePathArgument, archivePath);
        fragment.Arguments = arguments;
        fragment.Show(manager, FragmentTag);
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_save_import, container, false)
        ?? throw new InvalidOperationException("The save import layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        summary = view.FindViewById<TextView>(Resource.Id.save_import_summary);
        empty = view.FindViewById<TextView>(Resource.Id.save_import_empty);
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.save_import_progress);
        importButton = view.FindViewById<MaterialButton>(Resource.Id.save_import_confirm);
        var cancelButton = view.FindViewById<MaterialButton>(Resource.Id.save_import_cancel)
            ?? throw new InvalidOperationException("The save import cancel button is unavailable.");
        list = view.FindViewById<RecyclerView>(Resource.Id.save_import_list)
            ?? throw new InvalidOperationException("The save import list is unavailable.");
        adapter = new SaveImportAdapter(new SaveUiFormatter(RequireContext()), UpdateAction);
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.SetAdapter(adapter);
        importButton!.Click += OnImportClicked;
        cancelButton.Click += OnCancelClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        session = ((SaveBackupsFragment?)ParentFragment)?.Session
            ?? throw new InvalidOperationException("The save management session is unavailable.");
        archivePath = Arguments?.GetString(ArchivePathArgument)
            ?? throw new InvalidDataException("The staged save archive path is missing.");
        cancellation = new CancellationTokenSource();
        _ = LoadAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (importButton is not null)
            importButton.Click -= OnImportClicked;
        list?.SetAdapter(null);
        list = null;
        summary = null;
        empty = null;
        progress = null;
        importButton = null;
        adapter = null;
        base.OnDestroyView();
    }

    public override void OnDestroy()
    {
        session = null;
        base.OnDestroy();
    }

    public override void OnDismiss(IDialogInterface dialog)
    {
        if (!completed && Activity?.IsChangingConfigurations != true && session is not null && archivePath is not null)
            session.DeleteStagedArchive(archivePath);
        base.OnDismiss(dialog);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (session is not { } activeSession || archivePath is not { } activePath)
            return;
        SetBusy(true, indeterminate: true);
        try
        {
            var inspection = await Task.Run(() => activeSession.InspectArchive(activePath), cancellationToken).ConfigureAwait(false);
            var overview = await activeSession.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
            var existing = overview.Saves.Select(save => save.DirectoryName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = inspection.Candidates
                .Select(candidate => new SaveImportRow(candidate, existing.Contains(candidate.DirectoryName)))
                .ToArray();
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                adapter?.Submit(rows);
                if (summary is not null)
                    summary.Text = GetString(Resource.String.saves_import_summary, rows.Length, rows.Count(row => row.Conflicts));
                if (empty is not null)
                    empty.Visibility = rows.Length == 0 ? ViewStates.Visible : ViewStates.Gone;
                UpdateAction();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Saves", "save-import-inspection-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() =>
                {
                    Toast.MakeText(RequireContext(), Resource.String.saves_import_invalid, ToastLength.Long)?.Show();
                    DismissAllowingStateLoss();
                });
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false, indeterminate: true));
        }
    }

    private void OnImportClicked(object? sender, EventArgs eventArgs)
    {
        if (session?.IsGameRunning != false)
        {
            Toast.MakeText(RequireContext(), Resource.String.saves_close_game_first, ToastLength.Long)?.Show();
            return;
        }
        _ = ImportAsync();
    }

    private void OnCancelClicked(object? sender, EventArgs eventArgs) => Dismiss();

    private async Task ImportAsync()
    {
        if (session is not { } activeSession || archivePath is not { } activePath || adapter is null ||
            cancellation is not { IsCancellationRequested: false } lifetime)
        {
            return;
        }
        var selections = adapter.Selections;
        if (selections.Count == 0)
            return;
        SetBusy(true, indeterminate: false);
        var transferProgress = new Progress<SaveTransferProgress>(value =>
        {
            if (progress is null)
                return;
            progress.Indeterminate = value.TotalBytes <= 0;
            if (value.TotalBytes > 0)
                progress.Progress = (int)Math.Clamp(value.ProcessedBytes * 100 / value.TotalBytes, 0, 100);
        });
        try
        {
            var result = await activeSession.ImportAsync(activePath, selections, transferProgress, lifetime.Token).ConfigureAwait(false);
            completed = true;
            activeSession.DeleteStagedArchive(activePath);
            if (!IsAdded)
                return;
            Activity?.RunOnUiThread(() =>
            {
                var message = result.SafetyBackupName is null
                    ? GetString(Resource.String.saves_import_complete, result.ImportedDirectoryNames.Count)
                    : GetString(Resource.String.saves_import_complete_with_backup, result.ImportedDirectoryNames.Count);
                Toast.MakeText(RequireContext(), message, ToastLength.Long)?.Show();
                DismissAllowingStateLoss();
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Saves", "save-import-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.saves_import_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetBusy(false, indeterminate: false));
        }
    }

    private void UpdateAction()
    {
        if (importButton is null)
            return;
        var selected = adapter?.Selections.Count ?? 0;
        importButton.Enabled = selected > 0 && session?.IsGameRunning == false;
        importButton.Text = GetString(Resource.String.saves_import_selected, selected);
    }

    private void SetBusy(bool value, bool indeterminate)
    {
        if (progress is not null)
        {
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
            progress.Indeterminate = indeterminate;
            if (!indeterminate)
                progress.Progress = 0;
        }
        adapter?.SetEnabled(!value);
        if (importButton is not null)
            importButton.Enabled = !value && (adapter?.Selections.Count ?? 0) > 0 && session?.IsGameRunning == false;
    }

    private sealed record SaveImportRow(SaveArchiveCandidate Candidate, bool Conflicts);

    private sealed class SaveImportAdapter(SaveUiFormatter formatter, Action changed) : RecyclerView.Adapter
    {
        private IReadOnlyList<SaveImportRow> rows = [];
        private readonly HashSet<string> selected = new(StringComparer.Ordinal);
        private bool enabled = true;

        public override int ItemCount => rows.Count;
        public IReadOnlyList<SaveImportSelection> Selections => rows
            .Where(row => selected.Contains(row.Candidate.CandidateId))
            .Select(row => new SaveImportSelection(
                row.Candidate.CandidateId,
                row.Conflicts ? SaveImportConflictResolution.Replace : SaveImportConflictResolution.Skip))
            .ToArray();

        public void Submit(IReadOnlyList<SaveImportRow> value)
        {
            rows = value;
            selected.Clear();
            foreach (var row in value.Where(row => row.Candidate.CanImport && !row.Conflicts))
                selected.Add(row.Candidate.CandidateId);
            NotifyDataSetChanged();
        }

        public void SetEnabled(bool value)
        {
            enabled = value;
            NotifyDataSetChanged();
        }

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
        {
            var view = LayoutInflater.From(parent.Context)?.Inflate(Resource.Layout.item_save_import, parent, false)
                ?? throw new InvalidOperationException("The save import row could not be created.");
            return new Holder(view, Toggle);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) =>
            ((Holder)holder).Bind(rows[position], selected.Contains(rows[position].Candidate.CandidateId), enabled, formatter);

        private void Toggle(SaveImportRow row, bool value)
        {
            if (!enabled || !row.Candidate.CanImport)
                return;
            if (value)
                selected.Add(row.Candidate.CandidateId);
            else
                selected.Remove(row.Candidate.CandidateId);
            NotifyDataSetChanged();
            changed();
        }

        private sealed class Holder : RecyclerView.ViewHolder
        {
            private readonly CheckBox check;
            private readonly TextView title;
            private readonly TextView summary;
            private readonly TextView status;
            private readonly Action<SaveImportRow, bool> toggle;
            private SaveImportRow? row;
            private bool suppress;

            public Holder(View view, Action<SaveImportRow, bool> toggle) : base(view)
            {
                this.toggle = toggle;
                check = view.FindViewById<CheckBox>(Resource.Id.save_import_check)!;
                title = view.FindViewById<TextView>(Resource.Id.save_import_title)!;
                summary = view.FindViewById<TextView>(Resource.Id.save_import_item_summary)!;
                status = view.FindViewById<TextView>(Resource.Id.save_import_status)!;
                check.CheckedChange += (_, eventArgs) =>
                {
                    if (!suppress && row is not null)
                        this.toggle(row, eventArgs.IsChecked);
                };
                view.Click += (_, _) =>
                {
                    if (row is { } current && check.Enabled)
                        this.toggle(current, !check.Checked);
                };
            }

            public void Bind(SaveImportRow value, bool isSelected, bool enabled, SaveUiFormatter formatter)
            {
                row = value;
                suppress = true;
                title.Text = formatter.Title(value.Candidate.Metadata);
                summary.Text = formatter.Summary(value.Candidate.Metadata);
                status.SetText(value.Candidate.Status == SaveGameEntryStatus.Incomplete
                    ? Resource.String.saves_incomplete
                    : value.Conflicts
                        ? Resource.String.saves_import_conflict
                        : Resource.String.saves_import_new);
                check.Text = value.Conflicts
                    ? ItemView.Context?.GetString(Resource.String.saves_import_replace)
                    : ItemView.Context?.GetString(Resource.String.saves_import_add);
                check.Enabled = enabled && value.Candidate.CanImport;
                check.Checked = isSelected;
                suppress = false;
                ItemView.Alpha = value.Candidate.CanImport ? 1f : 0.55f;
            }
        }
    }
}
