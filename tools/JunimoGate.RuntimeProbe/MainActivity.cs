using System.Runtime.Versioning;
using System.Text;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using JunimoGate.RuntimeProbe.Core;

namespace JunimoGate.RuntimeProbe;

[Activity(Name = "org.junimogate.runtimeprobe.MainActivity", Label = "Runtime Probe", MainLauncher = true, Exported = true)]
[SupportedOSPlatform("android26.0")]
public sealed class MainActivity : Activity
{
    private const string LogTag = "JunimoGateProbe";
    private const string ReportFileName = "runtime-probe-report.json";

    private TextView? _output;
    private ScrollView? _scrollView;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _output = new TextView(this)
        {
            Text = "JunimoGate Mono runtime probe\n",
        };
        _output.SetTextIsSelectable(true);
        _output.SetPadding(24, 24, 24, 24);

        _scrollView = new ScrollView(this);
        _scrollView.AddView(_output, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        SetContentView(_scrollView);

        _ = RunProbeAsync();
    }

    private async Task RunProbeAsync()
    {
        var reportPath = Path.Combine(FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android app-private files directory is unavailable."), ReportFileName);
        try
        {
            AppendLine($"Report: {reportPath}");
            AppendLine("Running sequential hard cases on a background thread...");
            Log.Info(LogTag, $"probe-start report={reportPath}");

            var input = new RuntimeProbeInput
            {
                PlatformMetadata = CaptureAndroidMetadata(reportPath),
            };
            var report = await Task.Run(() => RuntimeProbeRunner.RunAsync(input, OnProbeProgress));
            var json = RuntimeProbeJson.Serialize(report);
            await WriteReportAtomicallyAsync(reportPath, json);

            Log.Info(LogTag, $"probe-conclusion conclusion={report.Conclusion} report={reportPath}");
            AppendLine(string.Empty);
            AppendLine($"CONCLUSION: {report.Conclusion}");
            AppendLine($"Full report atomically written to {reportPath}");
            AppendLine("ADB collection: adb exec-out run-as org.junimogate.runtimeprobe cat files/runtime-probe-report.json");
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, $"probe-runner-fatal type={ex.GetType().FullName} message={ex.Message}\n{ex.StackTrace}");
            AppendLine(string.Empty);
            AppendLine($"FATAL: {ex.GetType().FullName}: {ex.Message}");
            AppendLine(ex.StackTrace ?? string.Empty);
        }
    }

    private void OnProbeProgress(ProbeProgress progress)
    {
        if (progress.Result is null)
        {
            Log.Info(LogTag, $"case-start index={progress.CaseNumber}/{progress.TotalCases} id={progress.CaseId}");
            AppendLine($"[{progress.CaseNumber}/{progress.TotalCases}] START {progress.CaseId}");
            return;
        }

        var result = progress.Result;
        Log.Info(
            LogTag,
            $"case-complete index={progress.CaseNumber}/{progress.TotalCases} id={result.Id} " +
            $"status={result.Status} durationMs={result.DurationMilliseconds:F3} summary={result.Summary}");
        AppendLine($"[{progress.CaseNumber}/{progress.TotalCases}] {result.Status.ToString().ToUpperInvariant()} {result.Id}");
        AppendLine($"  {result.Summary}");
        if (result.Exception is not null)
        {
            Log.Error(LogTag, $"case-exception id={result.Id} type={result.Exception.Type} message={result.Exception.Message}\n{result.Exception.StackTrace}");
            AppendLine($"  {result.Exception.Type}: {result.Exception.Message}");
        }
    }

    private Dictionary<string, string> CaptureAndroidMetadata(string reportPath)
    {
        var appInfo = ApplicationInfo;
        var isActuallyDebuggable = appInfo is not null &&
            (appInfo.Flags & ApplicationInfoFlags.Debuggable) != 0;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["probePlatform"] = "android",
            ["packageName"] = PackageName ?? "org.junimogate.runtimeprobe",
            ["reportPath"] = reportPath,
            ["reportCollectionCommand"] = "adb exec-out run-as org.junimogate.runtimeprobe cat files/runtime-probe-report.json",
            ["reportStorage"] = "app-private-files-no-storage-permission",
            ["manifestDebuggableRequested"] = "true",
            ["applicationDebuggableActual"] = isActuallyDebuggable.ToString(),
            ["releaseApkIntentionallyDebuggable"] = "true-internal-probe-not-product-apk",
            ["requestedUseInterpreter"] = "false",
            ["requestedAndroidUseInterpreter"] = "false",
            ["requestedRunAotCompilation"] = "false",
            ["executionModeIntent"] = "stock-mono-jit-no-interpreter-no-aot",
            ["androidSdkInt"] = ((int)Build.VERSION.SdkInt).ToString(),
            ["androidRelease"] = Build.VERSION.Release ?? "unknown",
            ["androidIncremental"] = Build.VERSION.Incremental ?? "unknown",
            ["androidSecurityPatch"] = Build.VERSION.SecurityPatch ?? "unknown",
            ["androidManufacturer"] = Build.Manufacturer ?? "unknown",
            ["androidBrand"] = Build.Brand ?? "unknown",
            ["androidModel"] = Build.Model ?? "unknown",
            ["androidDevice"] = Build.Device ?? "unknown",
            ["androidProduct"] = Build.Product ?? "unknown",
            ["androidHardware"] = Build.Hardware ?? "unknown",
            ["androidFingerprint"] = Build.Fingerprint ?? "unknown",
            ["androidSupportedAbis"] = string.Join(",", Build.SupportedAbis ?? []),
            ["androidSupported64BitAbis"] = string.Join(",", Build.Supported64BitAbis ?? []),
            ["applicationTargetSdk"] = appInfo?.TargetSdkVersion.ToString() ?? "unknown",
        };
    }

    private void AppendLine(string text)
    {
        RunOnUiThread(() =>
        {
            _output?.Append(text + System.Environment.NewLine);
            _scrollView?.Post(() => _scrollView.FullScroll(FocusSearchDirection.Down));
        });
    }

    private static async Task WriteReportAtomicallyAsync(string path, string json)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Report path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{ReportFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
