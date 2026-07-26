using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.GameHost;
using OperationCanceledException = System.OperationCanceledException;
using Path = System.IO.Path;

namespace JunimoGate.App;

/// <summary>Displays M3 discovery, M4 workspace, and M5 Gate 0 metadata-only diagnostics.</summary>
[Activity(Name = "org.junimogate.app.MainActivity", Label = "JunimoGate", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private const int DiscoveryReportFormatVersion = 2;
    private const int WorkspaceReportFormatVersion = 2;
    private const int GameHostProbeReportFormatVersion = 5;
    private const int MaximumPrivateReportBytes = 64 * 1024 * 1024;
    private const string GameHostProbeReportFormat = "junimogate-gamehost-probe-report";
    private const string DiscoveryReportFileName = "game-discovery-latest.json";
    private const string WorkspaceReportFileName = "game-workspace-latest.json";
    private const string GameHostProbeReportFileName = "gamehost-probe-latest.json";

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private CancellationTokenSource? discoveryCancellation;
    private TextView? diagnosticText;
    private Button? launchGameHostButton;
    private string? pendingSmapiLaunchKey;
    private volatile bool destroyed;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        diagnosticText = new TextView(this)
        {
            Text = "JunimoGate SMAPI launcher\n\nChecking prepared game state…",
            TextSize = 14,
            Typeface = Typeface.Monospace,
        };
        var padding = (int)(16 * Resources!.DisplayMetrics!.Density);
        diagnosticText.SetPadding(padding, padding, padding, padding);
        diagnosticText.SetTextIsSelectable(true);

        var scrollView = new ScrollView(this);
        scrollView.AddView(diagnosticText);

        launchGameHostButton = new Button(this)
        {
            Text = "Launch through SMAPI",
            Enabled = false,
        };
        launchGameHostButton.Click += (_, _) =>
        {
            if (!destroyed && launchGameHostButton?.Enabled == true)
            {
                var key = Interlocked.Exchange(ref pendingSmapiLaunchKey, null);
                if (key is not null)
                {
                    var intent = new Intent(this, typeof(SmapiGameActivity));
                    intent.PutExtra(SmapiGameActivity.LaunchKeyExtra, key);
                    StartActivity(intent);
                    launchGameHostButton.Enabled = false;
                }
            }
        };

        var layout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        layout.AddView(scrollView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1));
        layout.AddView(launchGameHostButton, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        SetContentView(layout);

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
        launchGameHostButton = null;
        base.OnDestroy();
    }

    private async Task DiscoverAndReportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var context = ApplicationContext ?? this;
            UpdateUi("JunimoGate\n\nSMAPI launch\nStatus: checking the prepared snapshot…\n", cancellationToken);
            var handle = await GameDeepPrepareCoordinator.PrepareOrReuseAsync(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            pendingSmapiLaunchKey = handle.Key;
            UpdateUi($"JunimoGate\n\nSMAPI launch\nStatus: ready\nCapability expires: {handle.ExpiresAtUtc:O}\nFast launch checks PackageManager marker, active snapshot schema and file existence only.\n", cancellationToken);
            SetGameHostLaunchEnabled(true, cancellationToken);
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
        if (reportFileName is not DiscoveryReportFileName and
            not WorkspaceReportFileName and
            not GameHostProbeReportFileName)
        {
            throw new ArgumentException("The private report file name is not allowed.", nameof(reportFileName));
        }

        await AndroidPrivateStorage.EnsureMigratedAsync(this, cancellationToken).ConfigureAwait(false);
        var reportDirectory = AndroidPrivateStorage.GetReportsRoot(this);
        var reportPath = Path.Combine(reportDirectory, reportFileName);
        var temporaryPath = Path.Combine(
            reportDirectory,
            $".{Path.GetFileNameWithoutExtension(reportFileName)}-{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(document, ReportJsonOptions);
            if (json.LongLength + 1 > MaximumPrivateReportBytes)
            {
                throw new IOException("The private report exceeds its bounded size.");
            }

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

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

    private static async Task<GameHostProbeReportDocument> CreateGameHostProbeReportDocumentAsync(
        Context context,
        GameInstallationCandidate? playCandidate,
        WorkspaceReportDocument workspace,
        CancellationToken cancellationToken)
    {
        if (playCandidate is null)
        {
            return GameHostProbeReportDocument.NotAvailable(
                null,
                null,
                "play_candidate_not_available",
                "The Google Play game candidate is not available for Gate 0 inspection.");
        }

        var installation = playCandidate.Installation;
        if (workspace.Status is not nameof(WorkspacePreparationStatus.Built) and
            not nameof(WorkspacePreparationStatus.CacheHit))
        {
            return GameHostProbeReportDocument.NotAvailable(
                installation.PackageName,
                installation.SelectedAbi,
                "gamehost_probe_workspace_not_ready",
                "A validated active M4 workspace is not available for Gate 0 inspection.");
        }

        try
        {
            var result = await AndroidGameHostProbeBoundary
                .ProbeAsync(context, playCandidate, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new GameHostProbeReportDocument(
                GameHostProbeReportFormat,
                GameHostProbeReportFormatVersion,
                DateTimeOffset.UtcNow,
                "metadata-only",
                result.PackageName,
                installation.SelectedAbi,
                result.Status.ToString(),
                result.WorkspaceKey,
                result.ManagedEvidenceKey,
                result.SupportKey,
                result.ActivityBridgeEvidenceKey,
                result.ManagedEvidence,
                result.ActivityBridgeEvidence,
                new GameHostNativeInventoryDocument(
                    installation.SelectedAbi,
                    result.NativeEntries.Count,
                    result.NativeEntries.Sum(static entry => entry.Size),
                    result.NativeEntries),
                result.Diagnostics.Select(static diagnostic => new GameHostProbeDiagnosticDocument(
                    diagnostic.TimestampUtc,
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Message)).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GameHostProbeReportDocument(
                GameHostProbeReportFormat,
                GameHostProbeReportFormatVersion,
                DateTimeOffset.UtcNow,
                "metadata-only",
                installation.PackageName,
                installation.SelectedAbi,
                GameHostProbeAppStatus.Failed.ToString(),
                null,
                null,
                null,
                null,
                null,
                null,
                new GameHostNativeInventoryDocument(installation.SelectedAbi, 0, 0, []),
                [new GameHostProbeDiagnosticDocument(
                    DateTimeOffset.UtcNow,
                    "gamehost_probe_app_failed_safely",
                    DiagnosticSeverity.Error.ToString(),
                    "Gate 0 compatibility inspection failed safely without loading game code.")]);
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

    private void SetGameHostLaunchEnabled(bool enabled, CancellationToken cancellationToken)
    {
        if (destroyed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!destroyed &&
                !cancellationToken.IsCancellationRequested &&
                !IsFinishing &&
                !IsDestroyed &&
                launchGameHostButton is not null)
            {
                launchGameHostButton.Enabled = enabled;
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

    private static string RenderAppliedWorkspaceDiagnostics(AndroidGameHostAppliedWorkspaceResult result)
    {
        var text = new StringBuilder();
        text.AppendLine("JunimoGate M5 exact applied workspace");
        text.Append("Status: ").AppendLine(result.Status.ToString());
        text.Append("Package: ").AppendLine(result.PackageName);
        text.Append("Source workspace key: ").AppendLine(result.SourceWorkspaceKey ?? "not available");
        text.Append("Applied workspace key: ").AppendLine(result.AppliedWorkspaceKey ?? "not available");
        text.AppendLine("Game code loaded: no");
        text.AppendLine("GameRunner started: no");
        text.AppendLine("Diagnostics:");
        foreach (var diagnostic in result.Diagnostics)
        {
            text.Append("- [")
                .Append(diagnostic.Severity)
                .Append("] ")
                .Append(diagnostic.Code)
                .Append(": ")
                .AppendLine(diagnostic.Message);
        }

        return text.ToString();
    }

    private static string RenderGameHostProbeDiagnostics(
        GameHostProbeReportDocument report,
        bool reportSaved)
    {
        var text = new StringBuilder();
        text.AppendLine("JunimoGate M5 Gate 0 metadata-only compatibility probe");
        text.Append("Generated (UTC): ").AppendLine(report.GeneratedAtUtc.ToString("O"));
        text.Append("Private JSON report: ").AppendLine(reportSaved ? "saved" : "could not be saved");
        text.Append("Operation: ").AppendLine(report.Operation);
        text.Append("Package: ").AppendLine(report.PackageName ?? "not available");
        text.Append("Selected ABI: ").AppendLine(report.SelectedAbi ?? "not available");
        text.Append("Status: ").AppendLine(report.Status);
        text.Append("Workspace key: ").AppendLine(report.WorkspaceKey ?? "not available");
        text.Append("Managed evidence key: ").AppendLine(report.ManagedEvidenceKey ?? "not available");
        text.Append("Composite support key: ").AppendLine(report.SupportKey ?? "not available");
        text.Append("Activity bridge evidence key: ").AppendLine(report.ActivityBridgeEvidenceKey ?? "not available");

        if (report.ManagedEvidence is null)
        {
            text.AppendLine("Managed evidence: not available");
        }
        else
        {
            var managed = report.ManagedEvidence;
            text.Append("Target assembly: ").AppendLine(managed.TargetAssemblyIdentity);
            text.Append("Target MVID: ").AppendLine(managed.TargetModuleVersionId);
            text.Append("Target framework: ").AppendLine(managed.TargetFramework ?? "not available");
            text.Append("MainActivity base: ").AppendLine(managed.MainActivityBaseType);
            text.Append("MainActivity.instance: ").AppendLine(managed.MainActivityInstanceFieldSignature);
            text.Append("Managed evidence counts: references=")
                .Append(managed.AssemblyReferences.Count)
                .Append(", methods=").Append(managed.MainActivityMethodSignatures.Count)
                .Append(", field uses=").Append(managed.FieldUseCounts.Total)
                .Append(", call sites=").Append(managed.CallSiteCount)
                .Append(", P/Invokes=").Append(managed.PInvokes.Count)
                .Append(", interop attributes=").Append(managed.InteropAttributes.Count)
                .AppendLine();
        }

        if (report.ActivityBridgeEvidence is null)
        {
            text.AppendLine("Activity bridge evidence: not available");
        }
        else
        {
            var bridge = report.ActivityBridgeEvidence;
            text.Append("MonoGame assembly: ").AppendLine(bridge.MonoGameAssembly.Identity);
            text.Append("MonoGame MVID: ").AppendLine(bridge.MonoGameAssembly.ModuleVersionId);
            text.Append("MonoGame public API key: ").AppendLine(bridge.MonoGameAssembly.PublicApiSurface.SurfaceKey);
            text.Append("MonoGame public API surface: ")
                .Append(bridge.MonoGameAssembly.PublicApiSurface.TypeCount)
                .Append(" types, ")
                .Append(bridge.MonoGameAssembly.PublicApiSurface.MemberCount)
                .AppendLine(" members");
            text.Append("MonoGame requirements key: ").AppendLine(bridge.MonoGameRequirements.RequirementsKey);
            text.Append("MonoGame requirements: ")
                .Append(bridge.MonoGameRequirements.ConsumerAssemblyCount)
                .Append(" consumers, ")
                .Append(bridge.MonoGameRequirements.TypeRequirementHashes.Count)
                .Append(" types, ")
                .Append(bridge.MonoGameRequirements.MemberRequirementHashes.Count)
                .AppendLine(" members");
            text.Append("AndroidGameActivity base: ").AppendLine(bridge.MonoGame.AndroidGameActivity.BaseType);
            text.Append("GameRunner type: ").AppendLine(bridge.GameRunner.Type.Signature);
            text.Append("Bridge evidence counts: AndroidGameActivity ctors=")
                .Append(bridge.MonoGame.AndroidGameActivity.ConstructorSignatures.Count)
                .Append(", GameRunner ctors=").Append(bridge.GameRunner.Type.ConstructorSignatures.Count)
                .Append(", lifecycle bodies=").Append(bridge.MainActivity.LifecycleBodies.Count)
                .AppendLine();
        }

        if (report.NativeInventory is null)
        {
            text.AppendLine("Native inventory: not available");
        }
        else
        {
            text.Append("Native inventory: ")
                .Append(report.NativeInventory.EntryCount)
                .Append(" entries, ")
                .Append(report.NativeInventory.TotalBytes)
                .AppendLine(" bytes");
        }

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

    private enum GameHostProbeAppStatus
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

    private sealed record GameHostProbeReportDocument(
        string Format,
        int FormatVersion,
        DateTimeOffset GeneratedAtUtc,
        string Operation,
        string? PackageName,
        string? SelectedAbi,
        string Status,
        string? WorkspaceKey,
        string? ManagedEvidenceKey,
        string? SupportKey,
        string? ActivityBridgeEvidenceKey,
        AndroidGameHostManagedEvidence? ManagedEvidence,
        AndroidActivityBridgeCompatibilityEvidence? ActivityBridgeEvidence,
        GameHostNativeInventoryDocument? NativeInventory,
        IReadOnlyList<GameHostProbeDiagnosticDocument> Diagnostics)
    {
        public static GameHostProbeReportDocument NotAvailable(
            string? packageName,
            string? selectedAbi,
            string code,
            string message) =>
            new(
                GameHostProbeReportFormat,
                GameHostProbeReportFormatVersion,
                DateTimeOffset.UtcNow,
                "metadata-only",
                packageName,
                selectedAbi,
                GameHostProbeAppStatus.NotAvailable.ToString(),
                null,
                null,
                null,
                null,
                null,
                null,
                selectedAbi is null ? null : new GameHostNativeInventoryDocument(selectedAbi, 0, 0, []),
                [new GameHostProbeDiagnosticDocument(
                    DateTimeOffset.UtcNow,
                    code,
                    DiagnosticSeverity.Information.ToString(),
                    message)]);
    }

    private sealed record GameHostNativeInventoryDocument(
        string SelectedAbi,
        int EntryCount,
        long TotalBytes,
        IReadOnlyList<AndroidGameHostNativeEntryEvidence> Entries);

    private sealed record GameHostProbeDiagnosticDocument(
        DateTimeOffset TimestampUtc,
        string Code,
        string Severity,
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
