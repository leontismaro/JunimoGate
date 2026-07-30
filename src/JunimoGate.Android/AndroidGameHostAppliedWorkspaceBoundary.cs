using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Android.Content;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;

namespace JunimoGate.Android;

public enum AndroidGameHostAppliedWorkspaceStatus
{
    Built,
    CacheHit,
    Rejected,
    Failed,
    Cancelled,
}

public sealed record AndroidGameHostAppliedWorkspaceDiagnostic(
    DateTimeOffset TimestampUtc,
    string Code,
    string Severity,
    string Message);

public sealed record AndroidGameHostAppliedWorkspaceMetrics(
    long DurationMilliseconds,
    int CompatibilityAnalysisCount,
    int RewriteCount);

/// <summary>In-process result of preparing the semantic bridge overlay.</summary>
internal sealed record AndroidGameHostAppliedWorkspaceCapability(
    GameInstallationCandidate Candidate,
    ValidatedExecutionPlan SourceExecutionPlan,
    ValidatedGameHostAppliedWorkspacePlan AppliedExecutionPlan);

public sealed class AndroidGameHostAppliedWorkspaceResult
{
    internal AndroidGameHostAppliedWorkspaceResult(
        AndroidGameHostAppliedWorkspaceStatus status,
        string packageName,
        string? sourceWorkspaceKey,
        string? appliedWorkspaceKey,
        IEnumerable<AndroidGameHostAppliedWorkspaceDiagnostic> diagnostics,
        AndroidGameHostAppliedWorkspaceCapability? capability = null,
        AndroidGameHostAppliedWorkspaceMetrics? metrics = null)
    {
        Status = status;
        PackageName = packageName;
        SourceWorkspaceKey = sourceWorkspaceKey;
        AppliedWorkspaceKey = appliedWorkspaceKey;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        Capability = capability;
        Metrics = metrics;
    }

    public AndroidGameHostAppliedWorkspaceStatus Status { get; }
    public string PackageName { get; }
    public string? SourceWorkspaceKey { get; }
    public string? AppliedWorkspaceKey { get; }
    public ReadOnlyCollection<AndroidGameHostAppliedWorkspaceDiagnostic> Diagnostics { get; }
    public AndroidGameHostAppliedWorkspaceMetrics? Metrics { get; }
    internal AndroidGameHostAppliedWorkspaceCapability? Capability { get; }
    public bool IsSuccess => Status is AndroidGameHostAppliedWorkspaceStatus.Built or AndroidGameHostAppliedWorkspaceStatus.CacheHit;
}

/// <summary>
/// Builds or reuses the MainActivity bridge overlay. Compatibility is decided by the local rewrite
/// rules on cache miss; no managed-wide probe, native inventory, or version catalog is consulted.
/// </summary>
public static class AndroidGameHostAppliedWorkspaceBoundary
{
    public static async ValueTask<AndroidGameHostAppliedWorkspaceResult> PrepareAsync(
        Context context,
        GameInstallationPreparationSession preparationSession,
        ValidatedExecutionPlan sourcePlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparationSession);
        ArgumentNullException.ThrowIfNull(sourcePlan);
        var candidate = preparationSession.Candidate;
        var packageName = candidate.Installation.PackageName;
        if (packageName != KnownGameCertificate.PlayPackageName ||
            sourcePlan.SelectedAbi != GameInstallationDiscoveryCoordinator.SupportedAbi ||
            !WorkspaceExecutionValidator.MatchesGate0Identity(sourcePlan, candidate))
        {
            throw new ArgumentException(
                "The prepared source session is not a validated Play ARM64 workspace.",
                nameof(preparationSession));
        }

        var safeContext = context.ApplicationContext ?? context;
        await AndroidPrivateStorage.EnsureMigratedAsync(safeContext, cancellationToken).ConfigureAwait(false);
        var appliedRoot = Path.Combine(AndroidPrivateStorage.GetRuntimeRoot(safeContext), "gamehost-applied-v2");
        var diagnostics = new List<AndroidGameHostAppliedWorkspaceDiagnostic>();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var prepared = await new GameHostAppliedWorkspacePreparer(
                    new AndroidPackageWorkspaceCandidateRevalidator(safeContext, packageName))
                .PrepareAsync(
                    new GameHostAppliedWorkspacePreparationRequest(
                        appliedRoot,
                        candidate,
                        sourcePlan,
                        verifySourcePayloadHashes: false,
                        revalidateInstallation: false,
                        validateCommittedAfterBuild: false),
                    cancellationToken)
                .ConfigureAwait(false);
            AddDiagnostics(diagnostics, prepared.Diagnostics);
            var status = prepared.Status switch
            {
                GameHostAppliedWorkspacePreparationStatus.Built => AndroidGameHostAppliedWorkspaceStatus.Built,
                GameHostAppliedWorkspacePreparationStatus.CacheHit => AndroidGameHostAppliedWorkspaceStatus.CacheHit,
                GameHostAppliedWorkspacePreparationStatus.Cancelled => AndroidGameHostAppliedWorkspaceStatus.Cancelled,
                GameHostAppliedWorkspacePreparationStatus.Rejected => AndroidGameHostAppliedWorkspaceStatus.Rejected,
                _ => AndroidGameHostAppliedWorkspaceStatus.Failed,
            };
            var capability = prepared.Plan is not null &&
                status is AndroidGameHostAppliedWorkspaceStatus.Built or AndroidGameHostAppliedWorkspaceStatus.CacheHit
                ? new AndroidGameHostAppliedWorkspaceCapability(candidate, sourcePlan, prepared.Plan)
                : null;
            return new AndroidGameHostAppliedWorkspaceResult(
                status,
                packageName,
                sourcePlan.WorkspaceKey,
                prepared.Plan?.AppliedWorkspaceKey,
                diagnostics,
                capability,
                new AndroidGameHostAppliedWorkspaceMetrics(
                    Math.Max(1, stopwatch.ElapsedMilliseconds),
                    prepared.Metrics.RewriteCount,
                    prepared.Metrics.RewriteCount));
        }
        catch (OperationCanceledException)
        {
            return Failed(AndroidGameHostAppliedWorkspaceStatus.Cancelled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or CryptographicException or ArgumentException or
                                          InvalidOperationException)
        {
            diagnostics.Add(new AndroidGameHostAppliedWorkspaceDiagnostic(
                DateTimeOffset.UtcNow,
                "gamehost_applied_failed_safely",
                DiagnosticSeverity.Error.ToString(),
                "The applied workspace transaction failed without loading game code."));
            return Failed(AndroidGameHostAppliedWorkspaceStatus.Failed);
        }

        AndroidGameHostAppliedWorkspaceResult Failed(AndroidGameHostAppliedWorkspaceStatus status) =>
            new(
                status,
                packageName,
                sourcePlan.WorkspaceKey,
                null,
                diagnostics,
                metrics: new AndroidGameHostAppliedWorkspaceMetrics(
                    Math.Max(1, stopwatch.ElapsedMilliseconds),
                    CompatibilityAnalysisCount: 0,
                    RewriteCount: 0));
    }

    private static void AddDiagnostics(
        ICollection<AndroidGameHostAppliedWorkspaceDiagnostic> target,
        IEnumerable<DiagnosticRecord> source)
    {
        foreach (var diagnostic in source)
        {
            target.Add(new AndroidGameHostAppliedWorkspaceDiagnostic(
                diagnostic.Timestamp,
                diagnostic.Code,
                diagnostic.Severity.ToString(),
                diagnostic.Message));
        }
    }
}
