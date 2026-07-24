using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Widget;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.Extraction;
using OperationCanceledException = System.OperationCanceledException;
using Path = System.IO.Path;

namespace JunimoGate.App;

/// <summary>Displays M3 game discovery and M4 private workspace diagnostics.</summary>
[Activity(Name = "org.junimogate.app.MainActivity", Label = "JunimoGate", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private const int DiscoveryReportFormatVersion = 2;
    private const int WorkspaceReportFormatVersion = 2;
    private const string DiscoveryReportFileName = "game-discovery-latest.json";
    private const string WorkspaceReportFileName = "game-workspace-latest.json";

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private CancellationTokenSource? discoveryCancellation;
    private TextView? diagnosticText;
    private volatile bool destroyed;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        diagnosticText = new TextView(this)
        {
            Text = "JunimoGate M3 game discovery diagnostics\n\nScanning the two supported package candidates…",
            TextSize = 14,
            Typeface = Typeface.Monospace,
        };
        var padding = (int)(16 * Resources!.DisplayMetrics!.Density);
        diagnosticText.SetPadding(padding, padding, padding, padding);
        diagnosticText.SetTextIsSelectable(true);

        var scrollView = new ScrollView(this);
        scrollView.AddView(diagnosticText);
        SetContentView(scrollView);

        discoveryCancellation = new CancellationTokenSource();
        _ = DiscoverAndReportAsync(discoveryCancellation.Token);
    }

    protected override void OnDestroy()
    {
        destroyed = true;
        var cancellation = Interlocked.Exchange(ref discoveryCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        diagnosticText = null;
        base.OnDestroy();
    }

    private async Task DiscoverAndReportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var context = ApplicationContext ?? this;
            var discovery = await AndroidPlatformBoundary
                .DiscoverGamesAsync(context, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var discoveryDocument = CreateDiscoveryReportDocument(discovery);
            var discoveryReportSaved = await TryWriteReportAtomicallyAsync(
                discoveryDocument,
                DiscoveryReportFileName,
                cancellationToken).ConfigureAwait(false);
            var discoveryText = RenderDiscoveryDiagnostics(discoveryDocument, discoveryReportSaved);

            UpdateUi(
                discoveryText + "\nJunimoGate M4 game workspace\nStatus: preparing…\n",
                cancellationToken);

            var playCandidate = discovery.Candidates.FirstOrDefault(candidate =>
                candidate.Installation.PackageName.Equals(
                    AndroidPlatformBoundary.PlayPackageName,
                    StringComparison.Ordinal));
            var workspaceDocument = await CreateWorkspaceReportDocumentAsync(
                context,
                playCandidate,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var workspaceReportSaved = await TryWriteReportAtomicallyAsync(
                workspaceDocument,
                WorkspaceReportFileName,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            UpdateUi(
                discoveryText + "\n" + RenderWorkspaceDiagnostics(workspaceDocument, workspaceReportSaved),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Activity destruction cancellation intentionally produces no report or UI completion update.
        }
        catch (Exception)
        {
            UpdateUi(
                "JunimoGate startup diagnostics\n\nStartup failed safely. No package paths or exception details were displayed. Try reopening the app.",
                cancellationToken);
        }
    }

    private async Task<bool> TryWriteReportAtomicallyAsync<T>(
        T document,
        string reportFileName,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteReportAtomicallyAsync(document, reportFileName, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task WriteReportAtomicallyAsync<T>(
        T document,
        string reportFileName,
        CancellationToken cancellationToken)
    {
        if (reportFileName is not DiscoveryReportFileName and not WorkspaceReportFileName)
        {
            throw new ArgumentException("The private report file name is not allowed.", nameof(reportFileName));
        }

        var filesPath = FilesDir?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(filesPath))
        {
            throw new IOException("The private files directory is unavailable.");
        }

        var reportDirectory = Path.Combine(filesPath, "reports");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, reportFileName);
        var temporaryPath = Path.Combine(
            reportDirectory,
            $".{Path.GetFileNameWithoutExtension(reportFileName)}-{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(document, ReportJsonOptions) + "\n";
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, reportPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stale private temporary file is safer than exposing cleanup details in the UI.
            }
        }
    }

    private static async Task<WorkspaceReportDocument> CreateWorkspaceReportDocumentAsync(
        Context context,
        GameInstallationCandidate? playCandidate,
        CancellationToken cancellationToken)
    {
        if (playCandidate is null)
        {
            return new WorkspaceReportDocument(
                WorkspaceReportFormatVersion,
                DateTimeOffset.UtcNow,
                null,
                WorkspaceAppStatus.NotAvailable.ToString(),
                null,
                null,
                null,
                [],
                [new WorkspaceDiagnosticDocument(
                    DateTimeOffset.UtcNow,
                    StartupStage.Discovery.ToString(),
                    DiagnosticSeverity.Information.ToString(),
                    "play_candidate_not_available",
                    "The Google Play game candidate is not available for workspace preparation.")]);
        }

        var progress = new WorkspaceProgressCollector();
        try
        {
            var result = await AndroidPlatformBoundary
                .PrepareGameWorkspaceAsync(context, playCandidate, progress, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new WorkspaceReportDocument(
                WorkspaceReportFormatVersion,
                DateTimeOffset.UtcNow,
                playCandidate.Installation.PackageName,
                result.Status.ToString(),
                result.WorkspaceKey,
                result.Statistics is null
                    ? null
                    : new WorkspaceStatisticsDocument(
                        result.Statistics.ContentFileCount,
                        result.Statistics.ContentBytes,
                        result.Statistics.AssemblyFileCount,
                        result.Statistics.AssemblyBytes),
                result.Metrics is null
                    ? null
                    : new WorkspaceMetricsDocument(
                        result.Metrics.DurationMilliseconds,
                        result.Metrics.PeakTemporaryBytes,
                        result.Metrics.FinalWorkspaceBytes),
                progress.Stages,
                result.Diagnostics.Select(CreateWorkspaceDiagnosticDocument).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new WorkspaceReportDocument(
                WorkspaceReportFormatVersion,
                DateTimeOffset.UtcNow,
                playCandidate.Installation.PackageName,
                WorkspaceAppStatus.Failed.ToString(),
                null,
                null,
                null,
                progress.Stages,
                [new WorkspaceDiagnosticDocument(
                    DateTimeOffset.UtcNow,
                    StartupStage.Extraction.ToString(),
                    DiagnosticSeverity.Error.ToString(),
                    "workspace_preparation_failed",
                    "Workspace preparation failed safely.")]);
        }
    }

    private void UpdateUi(string text, CancellationToken cancellationToken)
    {
        if (destroyed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (destroyed ||
                cancellationToken.IsCancellationRequested ||
                IsFinishing ||
                IsDestroyed)
            {
                return;
            }

            if (diagnosticText is not null)
            {
                diagnosticText.Text = text;
            }
        });
    }

    private static DiscoveryReportDocument CreateDiscoveryReportDocument(GameDiscoveryReport discovery)
    {
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var packageReports = discovery.Packages
            .Select(package => new PackageReportDocument(
                package.PackageName,
                package.IsSuccess,
                package.Candidate is null ? null : CreateCandidateDocument(package.Candidate),
                package.Diagnostics.Select(diagnostic => CreateDiscoveryDiagnosticDocument(package.PackageName, diagnostic)).ToArray()))
            .ToArray();
        var candidates = discovery.Candidates.Select(CreateCandidateDocument).ToArray();
        var diagnostics = discovery.Packages
            .SelectMany(package => package.Diagnostics.Select(
                diagnostic => CreateDiscoveryDiagnosticDocument(package.PackageName, diagnostic)))
            .ToArray();
        return new DiscoveryReportDocument(
            DiscoveryReportFormatVersion,
            generatedAtUtc,
            packageReports,
            candidates,
            diagnostics);
    }

    private static CandidateDocument CreateCandidateDocument(GameInstallationCandidate candidate)
    {
        var installation = candidate.Installation;
        var inventories = candidate.SourceInventories.ToDictionary(
            static inventory => inventory.SourceLabel,
            StringComparer.Ordinal);
        var sources = installation.ApkSources.Select(source =>
        {
            var inventory = inventories[source.Label];
            return new ApkSourceDocument(
                source.Label,
                source.Digest.Value,
                source.Size,
                inventory.Roles.ToArray(),
                inventory.NativeAbis.ToArray(),
                inventory.AssemblyStoreAbis.ToArray());
        }).ToArray();
        var certificateVerification = candidate.CertificateVerification;
        return new CandidateDocument(
            installation.PackageName,
            installation.VersionName,
            installation.LongVersionCode,
            installation.SelectedAbi,
            certificateVerification.Status.ToString(),
            certificateVerification.AllowsCodeExecution,
            certificateVerification.MatchedKnownCertificate?.Value,
            new SigningDocument(
                installation.SigningIdentity.CurrentSignerDigests.Select(static digest => digest.Value).ToArray(),
                installation.SigningIdentity.RotationHistory.Select(static digest => digest.Value).ToArray()),
            sources);
    }

    private static DiscoveryDiagnosticDocument CreateDiscoveryDiagnosticDocument(
        string packageName,
        DiagnosticRecord diagnostic) =>
        new(
            packageName,
            diagnostic.Timestamp,
            diagnostic.Stage.ToString(),
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message);

    private static WorkspaceDiagnosticDocument CreateWorkspaceDiagnosticDocument(DiagnosticRecord diagnostic) =>
        new(
            diagnostic.Timestamp,
            diagnostic.Stage.ToString(),
            diagnostic.Severity.ToString(),
            diagnostic.Code,
            diagnostic.Message);

    private static string RenderDiscoveryDiagnostics(
        DiscoveryReportDocument report,
        bool reportSaved)
    {
        var text = new StringBuilder();
        text.AppendLine("JunimoGate M3 game discovery diagnostics");
        text.Append("Generated (UTC): ").AppendLine(report.GeneratedAtUtc.ToString("O"));
        text.Append("Private JSON report: ").AppendLine(reportSaved ? "saved" : "could not be saved");

        foreach (var package in report.PackageReports)
        {
            text.AppendLine();
            text.Append("Package: ").AppendLine(package.PackageName);
            text.Append("Status: ").AppendLine(package.IsSuccess ? "candidate available" : "no candidate");
            if (package.Candidate is not null)
            {
                AppendCandidate(text, package.Candidate);
            }

            if (package.Diagnostics.Count == 0)
            {
                text.AppendLine("Diagnostics: none");
            }
            else
            {
                text.AppendLine("Diagnostics:");
                foreach (var diagnostic in package.Diagnostics)
                {
                    text.Append("  [")
                        .Append(diagnostic.Severity)
                        .Append("] ")
                        .Append(diagnostic.Code)
                        .Append(": ")
                        .AppendLine(diagnostic.Message);
                }
            }
        }

        return text.ToString();
    }

    private static string RenderWorkspaceDiagnostics(
        WorkspaceReportDocument report,
        bool reportSaved)
    {
        var text = new StringBuilder();
        text.AppendLine("JunimoGate M4 game workspace");
        text.Append("Generated (UTC): ").AppendLine(report.GeneratedAtUtc.ToString("O"));
        text.Append("Private JSON report: ").AppendLine(reportSaved ? "saved" : "could not be saved");
        text.Append("Package: ").AppendLine(report.PackageName ?? "not available");
        text.Append("Status: ").AppendLine(report.Status);
        text.Append("Workspace key: ").AppendLine(report.WorkspaceKey ?? "not available");
        if (report.Statistics is null)
        {
            text.AppendLine("Statistics: not available");
        }
        else
        {
            text.Append("Content: ")
                .Append(report.Statistics.ContentFileCount)
                .Append(" files, ")
                .Append(report.Statistics.ContentBytes)
                .AppendLine(" bytes");
            text.Append("Assemblies: ")
                .Append(report.Statistics.AssemblyFileCount)
                .Append(" files, ")
                .Append(report.Statistics.AssemblyBytes)
                .AppendLine(" bytes");
        }

        if (report.Metrics is null)
        {
            text.AppendLine("Metrics: not available");
        }
        else
        {
            text.Append("Duration: ")
                .Append(report.Metrics.DurationMilliseconds)
                .AppendLine(" ms");
            text.Append("Peak temporary: ")
                .Append(report.Metrics.PeakTemporaryBytes)
                .AppendLine(" bytes");
            text.Append("Final workspace: ")
                .Append(report.Metrics.FinalWorkspaceBytes)
                .AppendLine(" bytes");
        }

        text.Append("Progress stages: ")
            .AppendLine(report.ProgressStages.Count == 0 ? "none" : string.Join(", ", report.ProgressStages));

        if (report.Diagnostics.Count == 0)
        {
            text.AppendLine("Diagnostics: none");
        }
        else
        {
            text.AppendLine("Diagnostics:");
            foreach (var diagnostic in report.Diagnostics)
            {
                text.Append("  [")
                    .Append(diagnostic.Severity)
                    .Append("] ")
                    .Append(diagnostic.Code)
                    .Append(": ")
                    .AppendLine(diagnostic.Message);
            }
        }

        return text.ToString();
    }

    private static void AppendCandidate(StringBuilder text, CandidateDocument candidate)
    {
        text.Append("Version: ")
            .Append(candidate.VersionName)
            .Append(" (")
            .Append(candidate.LongVersionCode)
            .AppendLine(")");
        text.Append("Selected ABI: ").AppendLine(candidate.SelectedAbi);
        text.Append("Game certificate: ").AppendLine(candidate.GameCertificateStatus);
        text.Append("Code execution allowed: ").AppendLine(candidate.AllowsCodeExecution ? "yes" : "no");
        if (candidate.MatchedKnownCertificateSha256 is not null)
        {
            text.Append("Matched tested certificate SHA-256: ")
                .AppendLine(candidate.MatchedKnownCertificateSha256);
        }

        text.AppendLine("Current signer SHA-256:");
        foreach (var digest in candidate.Signing.CurrentSignerDigests)
        {
            text.Append("  ").AppendLine(digest);
        }

        text.AppendLine("Signer rotation history (oldest to current):");
        if (candidate.Signing.RotationHistory.Count == 0)
        {
            text.AppendLine("  (none)");
        }
        else
        {
            foreach (var digest in candidate.Signing.RotationHistory)
            {
                text.Append("  ").AppendLine(digest);
            }
        }

        text.AppendLine("APK sources:");
        foreach (var source in candidate.ApkSources)
        {
            text.Append("  ").AppendLine(source.Label);
            text.Append("    SHA-256: ").AppendLine(source.Sha256);
            text.Append("    Size: ").Append(source.SizeBytes).AppendLine(" bytes");
            text.Append("    Roles: ").AppendLine(JoinOrNone(source.Roles));
            text.Append("    Native ABIs: ").AppendLine(JoinOrNone(source.NativeAbis));
            text.Append("    AssemblyStore ABIs: ").AppendLine(JoinOrNone(source.AssemblyStoreAbis));
        }
    }

    private static string JoinOrNone(IReadOnlyList<string> values) =>
        values.Count == 0 ? "(none)" : string.Join(", ", values);

    private enum WorkspaceAppStatus
    {
        Failed,
        NotAvailable,
    }

    private sealed record DiscoveryReportDocument(
        int FormatVersion,
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyList<PackageReportDocument> PackageReports,
        IReadOnlyList<CandidateDocument> Candidates,
        IReadOnlyList<DiscoveryDiagnosticDocument> Diagnostics);

    private sealed record PackageReportDocument(
        string PackageName,
        bool IsSuccess,
        CandidateDocument? Candidate,
        IReadOnlyList<DiscoveryDiagnosticDocument> Diagnostics);

    private sealed record CandidateDocument(
        string PackageName,
        string VersionName,
        long LongVersionCode,
        string SelectedAbi,
        string GameCertificateStatus,
        bool AllowsCodeExecution,
        string? MatchedKnownCertificateSha256,
        SigningDocument Signing,
        IReadOnlyList<ApkSourceDocument> ApkSources);

    private sealed record SigningDocument(
        IReadOnlyList<string> CurrentSignerDigests,
        IReadOnlyList<string> RotationHistory);

    private sealed record ApkSourceDocument(
        string Label,
        string Sha256,
        long SizeBytes,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> NativeAbis,
        IReadOnlyList<string> AssemblyStoreAbis);

    private sealed record DiscoveryDiagnosticDocument(
        string PackageName,
        DateTimeOffset TimestampUtc,
        string Stage,
        string Severity,
        string Code,
        string Message);

    private sealed record WorkspaceReportDocument(
        int FormatVersion,
        DateTimeOffset GeneratedAtUtc,
        string? PackageName,
        string Status,
        string? WorkspaceKey,
        WorkspaceStatisticsDocument? Statistics,
        WorkspaceMetricsDocument? Metrics,
        IReadOnlyList<string> ProgressStages,
        IReadOnlyList<WorkspaceDiagnosticDocument> Diagnostics);

    private sealed record WorkspaceStatisticsDocument(
        int ContentFileCount,
        long ContentBytes,
        int AssemblyFileCount,
        long AssemblyBytes);

    private sealed record WorkspaceMetricsDocument(
        long DurationMilliseconds,
        long PeakTemporaryBytes,
        long FinalWorkspaceBytes);

    private sealed record WorkspaceDiagnosticDocument(
        DateTimeOffset Timestamp,
        string Stage,
        string Severity,
        string Code,
        string Message);

    private sealed class WorkspaceProgressCollector : IProgress<WorkspaceProgressEvent>
    {
        private readonly object gate = new();
        private readonly HashSet<WorkspaceProgressStage> seen = [];
        private readonly List<string> stages = [];

        public IReadOnlyList<string> Stages
        {
            get
            {
                lock (gate)
                {
                    return stages.ToArray();
                }
            }
        }

        public void Report(WorkspaceProgressEvent value)
        {
            lock (gate)
            {
                if (seen.Add(value.Stage))
                {
                    stages.Add(value.Stage.ToString());
                }
            }
        }
    }
}
