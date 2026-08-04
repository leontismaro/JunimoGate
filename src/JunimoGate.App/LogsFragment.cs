using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;
using Log = JunimoGate.Android.JunimoGateLog;
using Fragment = AndroidX.Fragment.App.Fragment;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.LogsFragment")]
public sealed class LogsFragment : Fragment
{
    private const int ExportDiagnosticsRequestCode = 4601;
    private MaterialAutoCompleteTextView? sourcePicker;
    private MaterialAutoCompleteTextView? generationPicker;
    private TextView? summary;
    private TextView? content;
    private MaterialButton? copyButton;
    private MaterialButton? shareButton;
    private MaterialButton? diagnosticsButton;
    private CancellationTokenSource? cancellation;
    private ProductLogKind selectedKind;
    private ProductLogGeneration selectedGeneration;
    private string currentText = string.Empty;
    private bool pendingDiagnosticExport;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_logs, container, false)
        ?? throw new InvalidOperationException("The Logs layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        sourcePicker = view.FindViewById<MaterialAutoCompleteTextView>(Resource.Id.logs_source);
        generationPicker = view.FindViewById<MaterialAutoCompleteTextView>(Resource.Id.logs_generation);
        summary = view.FindViewById<TextView>(Resource.Id.logs_summary);
        content = view.FindViewById<TextView>(Resource.Id.logs_content);
        copyButton = view.FindViewById<MaterialButton>(Resource.Id.logs_copy);
        shareButton = view.FindViewById<MaterialButton>(Resource.Id.logs_share);
        diagnosticsButton = view.FindViewById<MaterialButton>(Resource.Id.logs_export_diagnostics);

        sourcePicker!.Adapter = new ArrayAdapter<string>(
            RequireContext(),
            global::Android.Resource.Layout.SimpleListItem1,
            [
                GetString(Resource.String.logs_source_launcher),
                GetString(Resource.String.logs_source_game),
                GetString(Resource.String.logs_source_smapi),
            ]);
        generationPicker!.Adapter = new ArrayAdapter<string>(
            RequireContext(),
            global::Android.Resource.Layout.SimpleListItem1,
            [GetString(Resource.String.logs_current), GetString(Resource.String.logs_previous)]);
        sourcePicker.SetText(GetString(Resource.String.logs_source_launcher), filter: false);
        generationPicker.SetText(GetString(Resource.String.logs_current), filter: false);
        sourcePicker.ItemClick += OnSourceSelected;
        generationPicker.ItemClick += OnGenerationSelected;
        copyButton!.Click += OnCopyClicked;
        shareButton!.Click += OnShareClicked;
        diagnosticsButton!.Click += OnDiagnosticsClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
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
        if (sourcePicker is not null)
            sourcePicker.ItemClick -= OnSourceSelected;
        if (generationPicker is not null)
            generationPicker.ItemClick -= OnGenerationSelected;
        if (copyButton is not null)
            copyButton.Click -= OnCopyClicked;
        if (shareButton is not null)
            shareButton.Click -= OnShareClicked;
        if (diagnosticsButton is not null)
            diagnosticsButton.Click -= OnDiagnosticsClicked;
        sourcePicker = null;
        generationPicker = null;
        summary = null;
        content = null;
        copyButton = null;
        shareButton = null;
        diagnosticsButton = null;
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

    private void OnSourceSelected(object? sender, AdapterView.ItemClickEventArgs eventArgs)
    {
        selectedKind = eventArgs.Position switch
        {
            0 => ProductLogKind.Launcher,
            1 => ProductLogKind.GameHost,
            2 => ProductLogKind.Smapi,
            _ => throw new InvalidOperationException("The selected log source is invalid."),
        };
        Reload();
    }

    private void OnGenerationSelected(object? sender, AdapterView.ItemClickEventArgs eventArgs)
    {
        selectedGeneration = eventArgs.Position switch
        {
            0 => ProductLogGeneration.Current,
            1 => ProductLogGeneration.Previous,
            _ => throw new InvalidOperationException("The selected log generation is invalid."),
        };
        Reload();
    }

    private void Reload()
    {
        if (cancellation is not { IsCancellationRequested: false } lifetime)
            return;
        _ = LoadAsync(lifetime.Token);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var document = await new ProductLogService(RequireContext())
                .ReadAsync(selectedKind, selectedGeneration, cancellationToken);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() =>
            {
                currentText = document.Text;
                if (content is not null)
                    content.Text = string.IsNullOrEmpty(document.Text) ? GetString(Resource.String.logs_empty) : document.Text;
                if (summary is not null)
                {
                    summary.Text = document.AvailableBytes == 0
                        ? GetString(Resource.String.logs_file_unavailable)
                        : FormatString(
                            document.IsTruncated ? Resource.String.logs_summary_truncated : Resource.String.logs_summary,
                            new Java.Lang.String(FormatFileSize(document.DisplayedBytes)),
                            Java.Lang.Integer.ValueOf(document.ErrorCount),
                            Java.Lang.Integer.ValueOf(document.WarningCount));
                }
                if (copyButton is not null)
                    copyButton.Enabled = document.Text.Length > 0;
                if (shareButton is not null)
                    shareButton.Enabled = document.Text.Length > 0;
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

    private void OnCopyClicked(object? sender, EventArgs eventArgs)
    {
        if (string.IsNullOrEmpty(currentText))
            return;
        var clipboard = RequireContext().GetSystemService(Context.ClipboardService) as ClipboardManager;
        if (clipboard is not null)
            clipboard.PrimaryClip = ClipData.NewPlainText(GetString(Resource.String.logs_title), currentText);
        ShowMessage(Resource.String.logs_copied);
    }

    private void OnShareClicked(object? sender, EventArgs eventArgs)
    {
        if (string.IsNullOrEmpty(currentText))
            return;
        var intent = new Intent(Intent.ActionSend);
        intent.SetType("text/plain");
        intent.PutExtra(Intent.ExtraText, currentText);
        StartActivity(Intent.CreateChooser(intent, GetString(Resource.String.logs_share))
            ?? throw new InvalidOperationException("The log share chooser is unavailable."));
    }

    private void OnDiagnosticsClicked(object? sender, EventArgs eventArgs)
    {
        var preview = new ProductLogService(RequireContext()).PreviewDiagnosticBundle();
        var files = preview.Sources.Where(static source => source.AvailableBytes > 0).ToArray();
        var list = files.Length == 0
            ? GetString(Resource.String.logs_diagnostic_no_logs)
            : string.Join('\n', files.Select(source => $"• {source.EntryName} ({FormatFileSize(source.IncludedBytes)})"));
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.logs_diagnostic_title);
        dialog.SetMessage(FormatString(
            Resource.String.logs_diagnostic_preview,
            new Java.Lang.String(list),
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
                .ExportDiagnosticBundleAsync(output, cancellationToken);
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
}
