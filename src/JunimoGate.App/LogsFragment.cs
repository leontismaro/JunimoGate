using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using JunimoGate.Core;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.LogsFragment")]
public sealed class LogsFragment : Fragment
{
    private const int ExportDiagnosticsRequestCode = 4601;
    private const int MoreActionStructured = 1;
    private const int MoreActionRaw = 2;
    private const int MoreActionCopy = 3;
    private const int MoreActionShare = 4;
    private const int MoreActionUpload = 5;
    private const int MoreActionDiagnostics = 6;
    private const int MoreModeGroup = 1;
    private MaterialButtonToggleGroup? sourceGroup;
    private MaterialButtonToggleGroup? generationGroup;
    private MaterialButtonToggleGroup? filterGroup;
    private MaterialButton? sourceLauncher;
    private MaterialButton? sourceGame;
    private MaterialButton? sourceSmapi;
    private MaterialButton? generationCrash;
    private MaterialButton? generationCurrent;
    private MaterialButton? generationPrevious;
    private MaterialButton? filterImportant;
    private MaterialButton? filterAll;
    private MaterialButton? filterErrors;
    private MaterialButton? filterWarnings;
    private MaterialButton? refreshButton;
    private MaterialButton? searchToggle;
    private MaterialButton? searchClose;
    private MaterialButton? moreButton;
    private View? searchRow;
    private SearchView? search;
    private TextView? summary;
    private TextView? rawContent;
    private TextView? empty;
    private RecyclerView? list;
    private ScrollView? rawScroll;
    private ProductLogAdapter? adapter;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? uploadCancellation;
    private AndroidX.AppCompat.App.AlertDialog? uploadProgressDialog;
    private ProductLogDocument? currentDocument;
    private ProductLogKind selectedKind = ProductLogKind.Launcher;
    private ProductLogGeneration selectedGeneration = ProductLogGeneration.Current;
    private ProductLogFilter selectedFilter = ProductLogFilter.Important;
    private bool rawMode;
    private bool fileOperationInProgress;
    private bool pendingDiagnosticExport;
    private int loadVersion;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_logs, container, false)
        ?? throw new InvalidOperationException("The Logs layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        sourceGroup = view.FindViewById<MaterialButtonToggleGroup>(Resource.Id.logs_source_group);
        generationGroup = view.FindViewById<MaterialButtonToggleGroup>(Resource.Id.logs_generation_group);
        filterGroup = view.FindViewById<MaterialButtonToggleGroup>(Resource.Id.logs_filter_group);
        sourceLauncher = view.FindViewById<MaterialButton>(Resource.Id.logs_source_launcher);
        sourceGame = view.FindViewById<MaterialButton>(Resource.Id.logs_source_game);
        sourceSmapi = view.FindViewById<MaterialButton>(Resource.Id.logs_source_smapi);
        generationCrash = view.FindViewById<MaterialButton>(Resource.Id.logs_crash);
        generationCurrent = view.FindViewById<MaterialButton>(Resource.Id.logs_current);
        generationPrevious = view.FindViewById<MaterialButton>(Resource.Id.logs_previous);
        filterImportant = view.FindViewById<MaterialButton>(Resource.Id.logs_filter_important);
        filterAll = view.FindViewById<MaterialButton>(Resource.Id.logs_filter_all);
        filterErrors = view.FindViewById<MaterialButton>(Resource.Id.logs_filter_errors);
        filterWarnings = view.FindViewById<MaterialButton>(Resource.Id.logs_filter_warnings);
        refreshButton = view.FindViewById<MaterialButton>(Resource.Id.logs_refresh);
        searchToggle = view.FindViewById<MaterialButton>(Resource.Id.logs_search_toggle);
        searchClose = view.FindViewById<MaterialButton>(Resource.Id.logs_search_close);
        moreButton = view.FindViewById<MaterialButton>(Resource.Id.logs_more);
        searchRow = view.FindViewById(Resource.Id.logs_search_row);
        search = view.FindViewById<SearchView>(Resource.Id.logs_search);
        summary = view.FindViewById<TextView>(Resource.Id.logs_summary);
        rawContent = view.FindViewById<TextView>(Resource.Id.logs_raw_content);
        empty = view.FindViewById<TextView>(Resource.Id.logs_empty);
        rawScroll = view.FindViewById<ScrollView>(Resource.Id.logs_raw_scroll);
        list = view.FindViewById<RecyclerView>(Resource.Id.logs_list)
            ?? throw new InvalidOperationException("The Logs list is unavailable.");

        adapter = new ProductLogAdapter();
        list.SetLayoutManager(new LinearLayoutManager(RequireContext()));
        list.AddItemDecoration(new DividerItemDecoration(RequireContext(), LinearLayoutManager.Vertical));
        list.SetAdapter(adapter);

        sourceLauncher!.Click += OnSourceLauncherClicked;
        sourceGame!.Click += OnSourceGameClicked;
        sourceSmapi!.Click += OnSourceSmapiClicked;
        generationCrash!.Click += OnGenerationCrashClicked;
        generationCurrent!.Click += OnGenerationCurrentClicked;
        generationPrevious!.Click += OnGenerationPreviousClicked;
        filterImportant!.Click += OnFilterImportantClicked;
        filterAll!.Click += OnFilterAllClicked;
        filterErrors!.Click += OnFilterErrorsClicked;
        filterWarnings!.Click += OnFilterWarningsClicked;
        refreshButton!.Click += OnRefreshClicked;
        searchToggle!.Click += OnSearchToggleClicked;
        searchClose!.Click += OnSearchCloseClicked;
        moreButton!.Click += OnMoreClicked;
        search!.QueryTextChange += OnSearchChanged;
        UpdateSelectionControls();
        UpdateContentViews();
    }

    public override void OnStart()
    {
        base.OnStart();
        cancellation = new CancellationTokenSource();
        UpdateGenerationControls();
        _ = LoadAsync(cancellation.Token);
    }

    public override void OnStop()
    {
        uploadCancellation?.Cancel();
        uploadProgressDialog?.Dismiss();
        uploadProgressDialog = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (sourceLauncher is not null)
            sourceLauncher.Click -= OnSourceLauncherClicked;
        if (sourceGame is not null)
            sourceGame.Click -= OnSourceGameClicked;
        if (sourceSmapi is not null)
            sourceSmapi.Click -= OnSourceSmapiClicked;
        if (generationCrash is not null)
            generationCrash.Click -= OnGenerationCrashClicked;
        if (generationCurrent is not null)
            generationCurrent.Click -= OnGenerationCurrentClicked;
        if (generationPrevious is not null)
            generationPrevious.Click -= OnGenerationPreviousClicked;
        if (filterImportant is not null)
            filterImportant.Click -= OnFilterImportantClicked;
        if (filterAll is not null)
            filterAll.Click -= OnFilterAllClicked;
        if (filterErrors is not null)
            filterErrors.Click -= OnFilterErrorsClicked;
        if (filterWarnings is not null)
            filterWarnings.Click -= OnFilterWarningsClicked;
        if (refreshButton is not null)
            refreshButton.Click -= OnRefreshClicked;
        if (searchToggle is not null)
            searchToggle.Click -= OnSearchToggleClicked;
        if (searchClose is not null)
            searchClose.Click -= OnSearchCloseClicked;
        if (moreButton is not null)
            moreButton.Click -= OnMoreClicked;
        if (search is not null)
            search.QueryTextChange -= OnSearchChanged;
        list?.SetAdapter(null);
        sourceGroup = null;
        generationGroup = null;
        filterGroup = null;
        sourceLauncher = null;
        sourceGame = null;
        sourceSmapi = null;
        generationCrash = null;
        generationCurrent = null;
        generationPrevious = null;
        filterImportant = null;
        filterAll = null;
        filterErrors = null;
        filterWarnings = null;
        refreshButton = null;
        searchToggle = null;
        searchClose = null;
        moreButton = null;
        searchRow = null;
        search = null;
        summary = null;
        rawContent = null;
        empty = null;
        list = null;
        rawScroll = null;
        adapter = null;
        currentDocument = null;
        fileOperationInProgress = false;
        pendingDiagnosticExport = false;
        base.OnDestroyView();
    }

#pragma warning disable CS0618, CS0672
    public override void OnActivityResult(int requestCode, int resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != ExportDiagnosticsRequestCode)
            return;
        if (resultCode != (int)Result.Ok)
            pendingDiagnosticExport = false;
        if (!pendingDiagnosticExport || resultCode != (int)Result.Ok || data?.Data is not { } uri ||
            cancellation is not { IsCancellationRequested: false } lifetime)
        {
            return;
        }
        pendingDiagnosticExport = false;
        _ = ExportDiagnosticsAsync(uri, lifetime.Token);
    }
#pragma warning restore CS0618, CS0672

    private void OnSourceLauncherClicked(object? sender, EventArgs eventArgs) => SelectSource(ProductLogKind.Launcher);
    private void OnSourceGameClicked(object? sender, EventArgs eventArgs) => SelectSource(ProductLogKind.GameHost);
    private void OnSourceSmapiClicked(object? sender, EventArgs eventArgs) => SelectSource(ProductLogKind.Smapi);
    private void OnGenerationCrashClicked(object? sender, EventArgs eventArgs) => SelectGeneration(ProductLogGeneration.Crash);
    private void OnGenerationCurrentClicked(object? sender, EventArgs eventArgs) => SelectGeneration(ProductLogGeneration.Current);
    private void OnGenerationPreviousClicked(object? sender, EventArgs eventArgs) => SelectGeneration(ProductLogGeneration.Previous);
    private void OnFilterImportantClicked(object? sender, EventArgs eventArgs) => SelectFilter(ProductLogFilter.Important);
    private void OnFilterAllClicked(object? sender, EventArgs eventArgs) => SelectFilter(ProductLogFilter.All);
    private void OnFilterErrorsClicked(object? sender, EventArgs eventArgs) => SelectFilter(ProductLogFilter.Errors);
    private void OnFilterWarningsClicked(object? sender, EventArgs eventArgs) => SelectFilter(ProductLogFilter.Warnings);

    private void OnSearchToggleClicked(object? sender, EventArgs eventArgs)
    {
        if (searchRow is null || search is null)
            return;
        searchRow.Visibility = ViewStates.Visible;
        search.RequestFocus();
        var inputMethod = RequireContext().GetSystemService(Context.InputMethodService) as InputMethodManager;
        inputMethod?.ShowSoftInput(search, ShowFlags.Implicit);
    }

    private void OnSearchCloseClicked(object? sender, EventArgs eventArgs)
    {
        if (search is not null)
        {
            search.SetQuery(string.Empty, false);
            search.ClearFocus();
            var inputMethod = RequireContext().GetSystemService(Context.InputMethodService) as InputMethodManager;
            inputMethod?.HideSoftInputFromWindow(search.WindowToken, HideSoftInputFlags.None);
        }
        if (searchRow is not null)
            searchRow.Visibility = ViewStates.Gone;
        ApplyFilter();
    }

    private void OnMoreClicked(object? sender, EventArgs eventArgs)
    {
        if (moreButton is null)
            return;
        var available = currentDocument?.AvailableBytes > 0;
        var menu = new PopupMenu(RequireContext(), moreButton);
        var popupMenu = menu.Menu ?? throw new InvalidOperationException("The Logs action menu is unavailable.");
        var structured = popupMenu.Add(MoreModeGroup, MoreActionStructured, 0, Resource.String.logs_mode_structured)
            ?? throw new InvalidOperationException("The structured Logs action is unavailable.");
        structured.SetCheckable(true);
        structured.SetChecked(!rawMode);
        var raw = popupMenu.Add(MoreModeGroup, MoreActionRaw, 1, Resource.String.logs_mode_raw)
            ?? throw new InvalidOperationException("The raw Logs action is unavailable.");
        raw.SetCheckable(true);
        raw.SetChecked(rawMode);
        popupMenu.SetGroupCheckable(MoreModeGroup, true, true);
        var copy = popupMenu.Add(0, MoreActionCopy, 2, Resource.String.logs_copy_full)
            ?? throw new InvalidOperationException("The copy Logs action is unavailable.");
        copy.SetEnabled(available && !fileOperationInProgress);
        var share = popupMenu.Add(0, MoreActionShare, 3, Resource.String.logs_share_full)
            ?? throw new InvalidOperationException("The share Logs action is unavailable.");
        share.SetEnabled(available && !fileOperationInProgress);
        var upload = popupMenu.Add(0, MoreActionUpload, 4, Resource.String.logs_upload)
            ?? throw new InvalidOperationException("The upload Logs action is unavailable.");
        upload.SetEnabled(available && selectedKind == ProductLogKind.Smapi);
        upload.SetVisible(selectedKind == ProductLogKind.Smapi);
        _ = popupMenu.Add(0, MoreActionDiagnostics, 5, Resource.String.logs_export_diagnostics)
            ?? throw new InvalidOperationException("The diagnostic Logs action is unavailable.");
        menu.MenuItemClick += (_, args) =>
        {
            args.Handled = args.Item is { } item && HandleMoreAction(item.ItemId);
        };
        menu.Show();
    }

    private bool HandleMoreAction(int action)
    {
        switch (action)
        {
            case MoreActionStructured:
                rawMode = false;
                UpdateContentViews();
                return true;
            case MoreActionRaw:
                rawMode = true;
                UpdateContentViews();
                return true;
            case MoreActionCopy:
                CopyCurrentLog();
                return true;
            case MoreActionShare:
                ShareCurrentLog();
                return true;
            case MoreActionUpload:
                ShowUploadConfirmation();
                return true;
            case MoreActionDiagnostics:
                ShowDiagnosticsConfirmation();
                return true;
            default:
                return false;
        }
    }

    private void OnRefreshClicked(object? sender, EventArgs eventArgs) => Reload();

    private void OnSearchChanged(object? sender, SearchView.QueryTextChangeEventArgs eventArgs)
    {
        ApplyFilter();
        eventArgs.Handled = true;
    }

    private void SelectSource(ProductLogKind kind)
    {
        selectedKind = kind;
        selectedGeneration = kind == ProductLogKind.Smapi
            ? new ProductLogService(RequireContext()).GetPreferredSmapiGeneration()
            : ProductLogGeneration.Current;
        ClearCurrentDocument();
        UpdateSelectionControls();
        Reload();
    }

    private void SelectGeneration(ProductLogGeneration generation)
    {
        selectedGeneration = generation;
        ClearCurrentDocument();
        UpdateGenerationControls();
        Reload();
    }

    private void SelectFilter(ProductLogFilter filter)
    {
        selectedFilter = filter;
        UpdateFilterControls();
        ApplyFilter();
    }

    private void UpdateSelectionControls()
    {
        sourceGroup?.Check(selectedKind switch
        {
            ProductLogKind.Launcher => Resource.Id.logs_source_launcher,
            ProductLogKind.GameHost => Resource.Id.logs_source_game,
            ProductLogKind.Smapi => Resource.Id.logs_source_smapi,
            _ => throw new ArgumentOutOfRangeException(nameof(selectedKind)),
        });
        UpdateGenerationControls();
        UpdateFilterControls();
    }

    private void UpdateGenerationControls()
    {
        if (generationCrash is null)
            return;
        var hasCrash = selectedKind == ProductLogKind.Smapi &&
            new ProductLogService(RequireContext()).IsAvailable(ProductLogKind.Smapi, ProductLogGeneration.Crash);
        generationCrash.Visibility = hasCrash ? ViewStates.Visible : ViewStates.Gone;
        if (selectedKind != ProductLogKind.Smapi && selectedGeneration == ProductLogGeneration.Crash ||
            selectedGeneration == ProductLogGeneration.Crash && !hasCrash)
        {
            selectedGeneration = ProductLogGeneration.Current;
        }
        generationGroup?.Check(selectedGeneration switch
        {
            ProductLogGeneration.Crash => Resource.Id.logs_crash,
            ProductLogGeneration.Current => Resource.Id.logs_current,
            ProductLogGeneration.Previous => Resource.Id.logs_previous,
            _ => throw new ArgumentOutOfRangeException(nameof(selectedGeneration)),
        });
    }

    private void UpdateFilterControls() => filterGroup?.Check(selectedFilter switch
    {
        ProductLogFilter.Important => Resource.Id.logs_filter_important,
        ProductLogFilter.All => Resource.Id.logs_filter_all,
        ProductLogFilter.Errors => Resource.Id.logs_filter_errors,
        ProductLogFilter.Warnings => Resource.Id.logs_filter_warnings,
        _ => throw new ArgumentOutOfRangeException(nameof(selectedFilter)),
    });

    private void Reload()
    {
        if (cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        _ = LoadAsync(lifetime.Token);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref loadVersion);
        var kind = selectedKind;
        var generation = selectedGeneration;
        try
        {
            var document = await new ProductLogService(RequireContext())
                .ReadAsync(kind, generation, cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested || version != loadVersion)
                return;
            Activity?.RunOnUiThread(() =>
            {
                currentDocument = document;
                if (document.Text.Length > 0 && document.Entries.Count == 0)
                    rawMode = true;
                UpdateSummary(document);
                UpdateGenerationControls();
                ApplyFilter();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("JunimoGate.Logs", "log-read-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.logs_read_failed));
        }
    }

    private void UpdateSummary(ProductLogDocument document)
    {
        if (summary is null)
            return;
        summary.Text = document.AvailableBytes == 0
            ? GetString(Resource.String.logs_file_unavailable)
            : FormatString(
                document.IsTruncated ? Resource.String.logs_summary_truncated : Resource.String.logs_summary,
                new Java.Lang.String(FormatFileSize(document.DisplayedBytes)),
                Java.Lang.Integer.ValueOf(document.ErrorCount),
                Java.Lang.Integer.ValueOf(document.WarningCount));
    }

    private void ApplyFilter()
    {
        var query = search?.Query?.Trim() ?? string.Empty;
        var entries = currentDocument?.Entries ?? [];
        var filtered = entries
            .Select(static (entry, index) => new ProductLogDisplayEntry(index, entry))
            .Where(item => MatchesFilter(item.Entry.Level))
            .Where(item => query.Length == 0 ||
                item.Entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        adapter?.Submit(filtered);
        if (rawContent is not null)
        {
            rawContent.Text = entries.Count == 0 || selectedFilter == ProductLogFilter.All && query.Length == 0
                ? currentDocument?.Text ?? string.Empty
                : string.Join('\n', filtered.Select(static item => item.Entry.RawText));
        }
        UpdateContentViews(filtered.Length);
    }

    private bool MatchesFilter(ProductLogLevel level) => selectedFilter switch
    {
        ProductLogFilter.Important => level is ProductLogLevel.Alert or ProductLogLevel.Warn or ProductLogLevel.Error or ProductLogLevel.Critical,
        ProductLogFilter.All => true,
        ProductLogFilter.Errors => level is ProductLogLevel.Error or ProductLogLevel.Critical,
        ProductLogFilter.Warnings => level == ProductLogLevel.Warn,
        _ => false,
    };

    private void UpdateContentViews(int? filteredCount = null)
    {
        var hasText = currentDocument?.Text.Length > 0;
        var hasRawText = rawContent?.Text?.Length > 0;
        var count = filteredCount ?? adapter?.ItemCount ?? 0;
        if (rawScroll is not null)
            rawScroll.Visibility = rawMode && hasRawText ? ViewStates.Visible : ViewStates.Gone;
        if (list is not null)
            list.Visibility = !rawMode && count > 0 ? ViewStates.Visible : ViewStates.Gone;
        if (empty is not null)
        {
            var showEmpty = rawMode ? !hasRawText : count == 0;
            empty.Visibility = showEmpty ? ViewStates.Visible : ViewStates.Gone;
            empty.SetText(hasText ? Resource.String.logs_no_matches : Resource.String.logs_empty);
        }
    }

    private void ClearCurrentDocument()
    {
        currentDocument = null;
        adapter?.Submit([]);
        if (rawContent is not null)
            rawContent.Text = string.Empty;
        if (summary is not null)
            summary.SetText(Resource.String.logs_file_unavailable);
        UpdateContentViews(0);
    }

    private void CopyCurrentLog()
    {
        if (fileOperationInProgress || currentDocument is not { AvailableBytes: > 0 } ||
            cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        fileOperationInProgress = true;
        _ = CopyCurrentLogAsync(selectedKind, selectedGeneration, lifetime.Token);
    }

    private async Task CopyCurrentLogAsync(
        ProductLogKind kind,
        ProductLogGeneration generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await new ProductLogService(RequireContext())
                .ReadFullTextAsync(kind, generation, cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested || text.Length == 0)
                return;
            Activity?.RunOnUiThread(() =>
            {
                var clipboard = RequireContext().GetSystemService(Context.ClipboardService) as ClipboardManager;
                if (clipboard is not null)
                    clipboard.PrimaryClip = ClipData.NewPlainText(GetString(Resource.String.logs_title), text);
                ShowMessage(Resource.String.logs_copied);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn("JunimoGate.Logs", "log-copy-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.logs_copy_failed));
        }
        finally
        {
            fileOperationInProgress = false;
        }
    }

    private void ShareCurrentLog()
    {
        if (fileOperationInProgress || currentDocument is not { AvailableBytes: > 0 } ||
            cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        fileOperationInProgress = true;
        _ = ShareCurrentLogAsync(selectedKind, selectedGeneration, lifetime.Token);
    }

    private async Task ShareCurrentLogAsync(
        ProductLogKind kind,
        ProductLogGeneration generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var service = new ProductLogService(RequireContext());
            var source = service.GetSource(kind, generation);
            var directory = Path.Combine(RequireContext().CacheDir?.AbsolutePath
                ?? throw new IOException("The application cache directory is unavailable."), "shared-logs");
            Directory.CreateDirectory(directory);
            CleanupExpiredShareFiles(directory);
            var stem = Path.GetFileNameWithoutExtension(source.EntryName);
            var path = Path.Combine(directory, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            await service.CopyFullLogAsync(kind, generation, path, cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;

            Activity?.RunOnUiThread(() => ShareFile(path, source.EntryName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or Java.Lang.IllegalArgumentException)
        {
            Log.Error("JunimoGate.Logs", "log-share-failed", exception);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.logs_share_failed));
        }
        finally
        {
            fileOperationInProgress = false;
        }
    }

    private void ShareFile(string path, string entryName)
    {
        var context = RequireContext();
        var authority = $"{context.PackageName}.fileprovider";
        var uri = FileProvider.GetUriForFile(context, authority, new Java.IO.File(path));
        var intent = new Intent(Intent.ActionSend);
        intent.SetType("text/plain");
        intent.PutExtra(Intent.ExtraStream, uri);
        intent.PutExtra(Intent.ExtraSubject, entryName);
        intent.ClipData = ClipData.NewRawUri(entryName, uri);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        StartActivity(Intent.CreateChooser(intent, GetString(Resource.String.logs_share_full))
            ?? throw new InvalidOperationException("The log share chooser is unavailable."));
    }

    private void ShowUploadConfirmation()
    {
        if (selectedKind != ProductLogKind.Smapi || currentDocument is not { AvailableBytes: > 0 } document)
            return;
        if (document.AvailableBytes > SmapiLogUploadClient.MaximumUploadBytes)
        {
            ShowMessage(Resource.String.logs_upload_too_large);
            return;
        }
        var source = new ProductLogService(RequireContext()).GetSource(selectedKind, selectedGeneration);
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.logs_upload_confirm_title);
        dialog.SetMessage(FormatString(
            Resource.String.logs_upload_confirm,
            new Java.Lang.String(source.EntryName),
            new Java.Lang.String(FormatFileSize(document.AvailableBytes))));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.logs_upload_action, (_, _) => StartUpload(source));
        dialog.Show();
    }

    private void StartUpload(ProductLogSource source)
    {
        if (cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        uploadCancellation?.Cancel();
        var uploadLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        uploadCancellation = uploadLifetime;
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.logs_uploading_title);
        dialog.SetMessage(Resource.String.logs_uploading);
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => uploadLifetime.Cancel());
        uploadProgressDialog = dialog.Show();
        _ = UploadAsync(source, uploadLifetime);
    }

    private async Task UploadAsync(ProductLogSource source, CancellationTokenSource uploadLifetime)
    {
        try
        {
            var text = await new ProductLogService(RequireContext())
                .ReadFullTextAsync(source.Kind, source.Generation, uploadLifetime.Token).ConfigureAwait(false);
            using var client = new SmapiLogUploadClient();
            var uri = await client.UploadAsync(text, uploadLifetime.Token).ConfigureAwait(false);
            if (!IsAdded || uploadLifetime.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                uploadProgressDialog?.Dismiss();
                uploadProgressDialog = null;
                ShowUploadSuccess(uri);
            });
        }
        catch (OperationCanceledException) when (uploadLifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or HttpRequestException or TaskCanceledException)
        {
            Log.Warn("JunimoGate.Logs", $"smapi-upload-failed:{exception.GetType().Name}");
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.logs_upload_failed));
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() =>
                {
                    uploadProgressDialog?.Dismiss();
                    uploadProgressDialog = null;
                });
            if (ReferenceEquals(uploadCancellation, uploadLifetime))
                uploadCancellation = null;
            uploadLifetime.Dispose();
        }
    }

    private void ShowUploadSuccess(Uri uri)
    {
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.logs_upload_success_title);
        dialog.SetMessage(FormatString(Resource.String.logs_upload_success, new Java.Lang.String(uri.AbsoluteUri)));
        dialog.SetPositiveButton(Resource.String.logs_open_link, (_, _) => OpenLink(uri));
        dialog.SetNeutralButton(Resource.String.logs_share_link, (_, _) => ShareLink(uri));
        dialog.SetNegativeButton(Resource.String.logs_copy_link, (_, _) => CopyLink(uri));
        dialog.Show();
    }

    private void OpenLink(Uri uri)
    {
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri.AbsoluteUri));
        StartActivity(intent);
    }

    private void ShareLink(Uri uri)
    {
        var intent = new Intent(Intent.ActionSend);
        intent.SetType("text/plain");
        intent.PutExtra(Intent.ExtraText, uri.AbsoluteUri);
        StartActivity(Intent.CreateChooser(intent, GetString(Resource.String.logs_share_link))
            ?? throw new InvalidOperationException("The log link share chooser is unavailable."));
    }

    private void CopyLink(Uri uri)
    {
        var clipboard = RequireContext().GetSystemService(Context.ClipboardService) as ClipboardManager;
        if (clipboard is not null)
            clipboard.PrimaryClip = ClipData.NewPlainText(GetString(Resource.String.logs_title), uri.AbsoluteUri);
        ShowMessage(Resource.String.logs_link_copied);
    }

    private static void CleanupExpiredShareFiles(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn("JunimoGate.Logs", "shared-log-cleanup-failed", exception);
        }
    }

    private void ShowDiagnosticsConfirmation()
    {
        var preview = new ProductLogService(RequireContext()).PreviewDiagnosticBundle();
        var files = preview.Sources.Where(static source => source.AvailableBytes > 0).ToArray();
        var fileList = files.Length == 0
            ? GetString(Resource.String.logs_diagnostic_no_logs)
            : string.Join('\n', files.Select(source => $"• {source.EntryName} ({FormatFileSize(source.IncludedBytes)})"));
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.logs_diagnostic_title);
        dialog.SetMessage(FormatString(
            Resource.String.logs_diagnostic_preview,
            new Java.Lang.String(fileList),
            new Java.Lang.String(FormatFileSize(preview.TotalIncludedBytes))));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.logs_export, (_, _) => StartDiagnosticExport());
        dialog.Show();
    }

    private void StartDiagnosticExport()
    {
        pendingDiagnosticExport = true;
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/zip");
        intent.PutExtra(Intent.ExtraTitle, $"JunimoGate-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
#pragma warning disable CS0618
        StartActivityForResult(intent, ExportDiagnosticsRequestCode);
#pragma warning restore CS0618
    }

    private async Task ExportDiagnosticsAsync(global::Android.Net.Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            await using var output = RequireContext().ContentResolver?.OpenOutputStream(uri, "w")
                ?? throw new IOException("The selected diagnostic document could not be opened.");
            await new ProductLogService(RequireContext())
                .ExportDiagnosticBundleAsync(output, cancellationToken).ConfigureAwait(false);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.logs_export_complete));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDocument(uri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Logs", "diagnostic-export-failed", exception);
            TryDeleteDocument(uri);
            if (IsAdded)
                Activity?.RunOnUiThread(() => ShowMessage(Resource.String.logs_export_failed));
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
            Log.Warn("JunimoGate.Logs", "diagnostic-document-cleanup-failed", exception);
        }
    }

    private string FormatFileSize(long bytes) =>
        global::Android.Text.Format.Formatter.FormatShortFileSize(RequireContext(), bytes) ?? $"{bytes} B";

    private string FormatString(int resourceId, params Java.Lang.Object[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The Logs string resource is unavailable.");

    private void ShowMessage(int resourceId) =>
        Toast.MakeText(RequireContext(), resourceId, ToastLength.Long)?.Show();

    private enum ProductLogFilter
    {
        Important,
        All,
        Errors,
        Warnings,
    }
}
