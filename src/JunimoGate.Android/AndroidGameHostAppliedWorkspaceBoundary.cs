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
    int ManagedProbeCount,
    int NativeInventoryCount,
    int RecipeEvaluationCount,
    int RewriteCount);

/// <summary>
/// Non-serializable in-process capability consumed only by GameHost after this boundary rebuilt all
/// source, support-catalog and applied-workspace trust. It is never accepted from an Intent or file.
/// </summary>
internal sealed record AndroidGameHostAppliedWorkspaceCapability(
    GameInstallationCandidate Candidate,
    ValidatedExecutionPlan SourceExecutionPlan,
    ValidatedGameHostAppliedWorkspacePlan AppliedExecutionPlan,
    GameHostRecipeDecision Decision);

/// <summary>Path-redacted result for one exact applied-workspace transaction.</summary>
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
/// Rebuilds the complete trusted-installed-source capability and creates/revalidates the exact
/// immutable applied workspace. It never loads or executes managed/native game code.
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
        if (!packageName.Equals(GameHostRecipeCatalog.TestedPlayPackageName, StringComparison.Ordinal) ||
            !WorkspaceExecutionValidator.MatchesGate0Identity(sourcePlan, candidate))
        {
            throw new ArgumentException("The prepared source session is not supported by the applied-workspace boundary.", nameof(preparationSession));
        }

        var safeContext = context.ApplicationContext ?? context;
        await AndroidPrivateStorage.EnsureMigratedAsync(safeContext, cancellationToken).ConfigureAwait(false);
        var runtimeRoot = AndroidPrivateStorage.GetRuntimeRoot(safeContext);
        var diagnostics = new List<AndroidGameHostAppliedWorkspaceDiagnostic>();
        var appliedRoot = Path.Combine(runtimeRoot, "gamehost-applied");
        var revalidator = new AndroidPackageWorkspaceCandidateRevalidator(safeContext, packageName);
        var stopwatch = Stopwatch.StartNew();
        var managedProbeCount = 0;
        var nativeInventoryCount = 0;
        var recipeEvaluationCount = 0;

        AndroidGameHostAppliedWorkspaceResult ProductResult(
            AndroidGameHostAppliedWorkspaceStatus status,
            string? appliedWorkspaceKey,
            AndroidGameHostAppliedWorkspaceCapability? capability = null,
            int rewriteCount = 0) =>
            Result(
                status,
                packageName,
                sourcePlan.WorkspaceKey,
                appliedWorkspaceKey,
                diagnostics,
                capability,
                new AndroidGameHostAppliedWorkspaceMetrics(
                    Math.Max(1, stopwatch.ElapsedMilliseconds),
                    managedProbeCount,
                    nativeInventoryCount,
                    recipeEvaluationCount,
                    rewriteCount));

        try
        {
            var paths = ResolveAssemblyPaths(sourcePlan);
            var assemblyRoot = Path.Combine(sourcePlan.WorkspacePath, "assemblies");
            var target = paths.Single(path =>
                Path.GetFileName(path).Equals("StardewValley.dll", StringComparison.OrdinalIgnoreCase));
            managedProbeCount++;
            var managed = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(assemblyRoot, target, paths),
                cancellationToken);
            AddDiagnostics(diagnostics, managed.Diagnostics);
            if (!managed.IsSuccess || managed.Evidence is null || managed.ManagedEvidenceKey is null)
            {
                return ProductResult(
                    managed.Status == GameHostProbeStatus.Cancelled
                        ? AndroidGameHostAppliedWorkspaceStatus.Cancelled
                        : AndroidGameHostAppliedWorkspaceStatus.Rejected,
                    null);
            }

            nativeInventoryCount++;
            var native = await new NativeEntryInventoryProbe()
                .ProbeAsync(sourcePlan, preparationSession, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AddDiagnostics(diagnostics, native.Diagnostics);
            if (!native.IsSuccess)
            {
                return ProductResult(
                    native.Status == NativeEntryInventoryStatus.Cancelled
                        ? AndroidGameHostAppliedWorkspaceStatus.Cancelled
                        : AndroidGameHostAppliedWorkspaceStatus.Rejected,
                    null);
            }

            var nativeEvidence = native.Entries.Select(static entry => new GameHostNativeEvidence(
                entry.SourceLabel,
                entry.EntryPath,
                entry.Size,
                entry.Sha256,
                entry.Elf.ElfClass,
                entry.Elf.DataEncoding,
                entry.Elf.IdentVersion,
                entry.Elf.OsAbi,
                entry.Elf.AbiVersion,
                entry.Elf.ObjectType,
                entry.Elf.Machine,
                entry.Elf.Flags)).ToArray();
            var supportKey = GameHostSupportKey.Create(managed.Evidence, native.SelectedAbi, nativeEvidence);
            recipeEvaluationCount++;
            var decision = GameHostRecipeCatalog.Evaluate(
                supportKey,
                managed.ManagedEvidenceKey,
                managed.Evidence,
                native.SelectedAbi,
                nativeEvidence);
            if (!decision.CanRewrite)
            {
                diagnostics.Add(Diagnostic(
                    decision.DecisionCode,
                    DiagnosticSeverity.Error,
                    "The support catalog did not authorize the exact bridge recipe."));
                return ProductResult(
                    AndroidGameHostAppliedWorkspaceStatus.Rejected,
                    null);
            }

            var prepared = await new GameHostAppliedWorkspacePreparer(revalidator)
                .PrepareAsync(
                    new GameHostAppliedWorkspacePreparationRequest(
                        appliedRoot,
                        candidate,
                        sourcePlan,
                        decision,
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
                ? new AndroidGameHostAppliedWorkspaceCapability(candidate, sourcePlan, prepared.Plan, decision)
                : null;
            return ProductResult(
                status,
                prepared.Plan?.AppliedWorkspaceKey,
                capability,
                prepared.Metrics.RewriteCount);
        }
        catch (OperationCanceledException)
        {
            return ProductResult(AndroidGameHostAppliedWorkspaceStatus.Cancelled, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Diagnostic(
                "gamehost_applied_failed_safely",
                DiagnosticSeverity.Error,
                "The applied workspace transaction failed without loading game code."));
            return ProductResult(AndroidGameHostAppliedWorkspaceStatus.Failed, null);
        }
    }

    public static async ValueTask<AndroidGameHostAppliedWorkspaceResult> PrepareAsync(
        Context context,
        GameInstallationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);
        var packageName = candidate.Installation.PackageName;
        if (!packageName.Equals(GameHostRecipeCatalog.TestedPlayPackageName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The candidate package is not supported by the applied-workspace boundary.", nameof(candidate));
        }

        var safeContext = context.ApplicationContext ?? context;
        await AndroidPrivateStorage.EnsureMigratedAsync(safeContext, cancellationToken).ConfigureAwait(false);
        var runtimeRoot = AndroidPrivateStorage.GetRuntimeRoot(safeContext);

        var diagnostics = new List<AndroidGameHostAppliedWorkspaceDiagnostic>();
        var appliedRoot = Path.Combine(runtimeRoot, "gamehost-applied");
        var revalidator = new AndroidPackageWorkspaceCandidateRevalidator(safeContext, packageName);
        var trustValidator = new WorkspaceExecutionValidator(revalidator);

        try
        {
            var before = await ValidateTrustAsync(trustValidator, candidate, runtimeRoot, cancellationToken)
                .ConfigureAwait(false);
            AddDiagnostics(diagnostics, before.Diagnostics);
            if (before.Status == WorkspaceExecutionValidationStatus.Cancelled)
            {
                return Result(AndroidGameHostAppliedWorkspaceStatus.Cancelled, packageName, null, null, diagnostics);
            }

            if (before.Status != WorkspaceExecutionValidationStatus.Validated || before.Plan is null)
            {
                return Result(AndroidGameHostAppliedWorkspaceStatus.Rejected, packageName, null, null, diagnostics);
            }

            var firstPlan = before.Plan;
            GameHostCompatibilityProbeResult managed;
            NativeEntryInventoryResult native;
            try
            {
                var paths = ResolveAssemblyPaths(firstPlan);
                var assemblyRoot = Path.Combine(firstPlan.WorkspacePath, "assemblies");
                var target = paths.Single(path =>
                    Path.GetFileName(path).Equals("StardewValley.dll", StringComparison.OrdinalIgnoreCase));
                managed = new GameHostCompatibilityProbe().Probe(
                    new GameHostCompatibilityProbeOptions(assemblyRoot, target, paths),
                    cancellationToken);
                if (!managed.IsSuccess || managed.Evidence is null || managed.ManagedEvidenceKey is null)
                {
                    AddDiagnostics(diagnostics, managed.Diagnostics);
                    return Result(
                        managed.Status == GameHostProbeStatus.Cancelled
                            ? AndroidGameHostAppliedWorkspaceStatus.Cancelled
                            : AndroidGameHostAppliedWorkspaceStatus.Rejected,
                        packageName,
                        firstPlan.WorkspaceKey,
                        null,
                        diagnostics);
                }

                AddDiagnostics(diagnostics, managed.Diagnostics);
                native = await new NativeEntryInventoryProbe()
                    .ProbeAsync(firstPlan, candidate, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                AddDiagnostics(diagnostics, native.Diagnostics);
                if (!native.IsSuccess)
                {
                    return Result(
                        native.Status == NativeEntryInventoryStatus.Cancelled
                            ? AndroidGameHostAppliedWorkspaceStatus.Cancelled
                            : AndroidGameHostAppliedWorkspaceStatus.Rejected,
                        packageName,
                        firstPlan.WorkspaceKey,
                        null,
                        diagnostics);
                }
            }
            catch (OperationCanceledException)
            {
                return Result(
                    AndroidGameHostAppliedWorkspaceStatus.Cancelled,
                    packageName,
                    firstPlan.WorkspaceKey,
                    null,
                    diagnostics);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                diagnostics.Add(Diagnostic(
                    "gamehost_applied_inputs_invalid",
                    DiagnosticSeverity.Error,
                    "The validated managed/native inputs cannot select an exact bridge recipe."));
                return Result(
                    AndroidGameHostAppliedWorkspaceStatus.Rejected,
                    packageName,
                    firstPlan.WorkspaceKey,
                    null,
                    diagnostics);
            }

            var nativeEvidence = native.Entries.Select(static entry => new GameHostNativeEvidence(
                entry.SourceLabel,
                entry.EntryPath,
                entry.Size,
                entry.Sha256,
                entry.Elf.ElfClass,
                entry.Elf.DataEncoding,
                entry.Elf.IdentVersion,
                entry.Elf.OsAbi,
                entry.Elf.AbiVersion,
                entry.Elf.ObjectType,
                entry.Elf.Machine,
                entry.Elf.Flags)).ToArray();
            var supportKey = GameHostSupportKey.Create(managed.Evidence!, native.SelectedAbi, nativeEvidence);
            var decision = GameHostRecipeCatalog.Evaluate(
                supportKey,
                managed.ManagedEvidenceKey!,
                managed.Evidence!,
                native.SelectedAbi,
                nativeEvidence);
            if (!decision.CanRewrite)
            {
                diagnostics.Add(Diagnostic(
                    decision.DecisionCode,
                    DiagnosticSeverity.Error,
                    "The support catalog did not authorize the exact bridge recipe."));
                return Result(
                    AndroidGameHostAppliedWorkspaceStatus.Rejected,
                    packageName,
                    firstPlan.WorkspaceKey,
                    null,
                    diagnostics);
            }

            var after = await ValidateTrustAsync(trustValidator, candidate, runtimeRoot, cancellationToken)
                .ConfigureAwait(false);
            AddDiagnostics(diagnostics, after.Diagnostics);
            if (after.Status == WorkspaceExecutionValidationStatus.Cancelled)
            {
                return Result(
                    AndroidGameHostAppliedWorkspaceStatus.Cancelled,
                    packageName,
                    firstPlan.WorkspaceKey,
                    null,
                    diagnostics);
            }

            if (after.Status != WorkspaceExecutionValidationStatus.Validated || after.Plan is null ||
                !after.Plan.WorkspaceKey.Equals(firstPlan.WorkspaceKey, StringComparison.Ordinal) ||
                !after.Plan.IdentityDigest.Equals(firstPlan.IdentityDigest, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(
                    "gamehost_applied_trust_changed",
                    DiagnosticSeverity.Error,
                    "The trusted source identity changed while selecting the bridge recipe."));
                return Result(
                    AndroidGameHostAppliedWorkspaceStatus.Rejected,
                    packageName,
                    firstPlan.WorkspaceKey,
                    null,
                    diagnostics);
            }

            var prepared = await new GameHostAppliedWorkspacePreparer(revalidator)
                .PrepareAsync(
                    new GameHostAppliedWorkspacePreparationRequest(
                        appliedRoot,
                        candidate,
                        after.Plan,
                        decision),
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
                ? new AndroidGameHostAppliedWorkspaceCapability(candidate, after.Plan, prepared.Plan, decision)
                : null;
            return Result(
                status,
                packageName,
                after.Plan.WorkspaceKey,
                prepared.Plan?.AppliedWorkspaceKey,
                diagnostics,
                capability);
        }
        catch (OperationCanceledException)
        {
            return Result(AndroidGameHostAppliedWorkspaceStatus.Cancelled, packageName, null, null, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            diagnostics.Add(Diagnostic(
                "gamehost_applied_failed_safely",
                DiagnosticSeverity.Error,
                "The applied workspace transaction failed without loading game code."));
            return Result(AndroidGameHostAppliedWorkspaceStatus.Failed, packageName, null, null, diagnostics);
        }
    }

    private static ValueTask<WorkspaceExecutionValidationResult> ValidateTrustAsync(
        WorkspaceExecutionValidator validator,
        GameInstallationCandidate candidate,
        string runtimeRoot,
        CancellationToken cancellationToken) =>
        validator.ValidateAsync(
            candidate,
            runtimeRoot,
            WorkspacePreparationOptions.DefaultExtractorSchema,
            WorkspacePreparationOptions.DefaultManifestSchema,
            WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
            WorkspaceExecutionTrustDefaults.Gate0RewriteStatus,
            cancellationToken);

    private static string[] ResolveAssemblyPaths(ValidatedExecutionPlan plan)
    {
        var assemblyRoot = Path.GetFullPath(Path.Combine(plan.WorkspacePath, "assemblies"));
        var prefix = assemblyRoot + Path.DirectorySeparatorChar;
        var paths = plan.Payloads
            .Where(static payload => payload.Kind == "assembly")
            .Select(payload => Path.GetFullPath(Path.Combine(
                plan.WorkspacePath,
                payload.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        if (paths.Length == 0 || paths.Any(path => !path.StartsWith(prefix, StringComparison.Ordinal)) ||
            paths.Count(path => Path.GetFileName(path).Equals("StardewValley.dll", StringComparison.OrdinalIgnoreCase)) != 1)
        {
            throw new InvalidDataException("The validated assembly set is not safe or complete.");
        }

        return paths.Order(StringComparer.Ordinal).ToArray();
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

    private static void AddDiagnostics(
        ICollection<AndroidGameHostAppliedWorkspaceDiagnostic> target,
        IEnumerable<GameHostProbeDiagnostic> source)
    {
        foreach (var diagnostic in source)
        {
            target.Add(Diagnostic(diagnostic.Code, diagnostic.Severity, diagnostic.Message));
        }
    }

    private static AndroidGameHostAppliedWorkspaceDiagnostic Diagnostic(
        string code,
        GameHostProbeDiagnosticSeverity severity,
        string message) =>
        new(DateTimeOffset.UtcNow, code, severity.ToString(), message);

    private static AndroidGameHostAppliedWorkspaceDiagnostic Diagnostic(
        string code,
        DiagnosticSeverity severity,
        string message) =>
        new(DateTimeOffset.UtcNow, code, severity.ToString(), message);

    private static AndroidGameHostAppliedWorkspaceResult Result(
        AndroidGameHostAppliedWorkspaceStatus status,
        string packageName,
        string? sourceWorkspaceKey,
        string? appliedWorkspaceKey,
        IEnumerable<AndroidGameHostAppliedWorkspaceDiagnostic> diagnostics,
        AndroidGameHostAppliedWorkspaceCapability? capability = null,
        AndroidGameHostAppliedWorkspaceMetrics? metrics = null) =>
        new(status, packageName, sourceWorkspaceKey, appliedWorkspaceKey, diagnostics, capability, metrics);
}
