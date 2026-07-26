using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using JunimoGate.Core;
using JunimoGate.Extraction;

namespace JunimoGate.Rewriter;

public static class GameHostAppliedWorkspaceDiagnosticCodes
{
    public const string Built = "gamehost_applied_workspace_built";
    public const string CacheHit = "gamehost_applied_workspace_cache_hit";
    public const string Cancelled = "gamehost_applied_workspace_cancelled";
    public const string RequestRejected = "gamehost_applied_workspace_request_rejected";
    public const string SourceRejected = "gamehost_applied_workspace_source_rejected";
    public const string RewriteRejected = "gamehost_applied_workspace_rewrite_rejected";
    public const string ManifestRejected = "gamehost_applied_workspace_manifest_rejected";
    public const string RecoveryRejected = "gamehost_applied_workspace_recovery_rejected";
    public const string LiveIdentityChanged = "gamehost_applied_workspace_live_identity_changed";
    public const string CommitFailed = "gamehost_applied_workspace_commit_failed";
}

public enum GameHostAppliedWorkspacePreparationStatus
{
    Built,
    CacheHit,
    Rejected,
    Failed,
    Cancelled,
}

/// <summary>
/// Binds one applied-workspace transaction to the original candidate, a sealed fresh M4 execution
/// plan, and the catalog-issued exact bridge capability. No caller-selected source path is accepted.
/// </summary>
public sealed class GameHostAppliedWorkspacePreparationRequest
{
    public GameHostAppliedWorkspacePreparationRequest(
        string appliedRootPath,
        GameInstallationCandidate originalCandidate,
        ValidatedExecutionPlan sourceExecutionPlan,
        GameHostRecipeDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appliedRootPath);
        ArgumentNullException.ThrowIfNull(originalCandidate);
        ArgumentNullException.ThrowIfNull(sourceExecutionPlan);
        ArgumentNullException.ThrowIfNull(decision);

        AppliedRootPath = Path.GetFullPath(appliedRootPath);
        OriginalCandidate = originalCandidate;
        SourceExecutionPlan = sourceExecutionPlan;
        Decision = decision;

        if (!originalCandidate.Installation.PackageName.Equals(sourceExecutionPlan.PackageName, StringComparison.Ordinal) ||
            !decision.CanRewrite ||
            decision.EntitlementPolicy != GameHostEntitlementPolicy.TrustedInstalledSource ||
            decision.Recipe != GameHostBridgeRecipe.Identity ||
            !decision.SupportKey.Equals(GameHostRecipeCatalog.TestedPlaySupportKey, StringComparison.Ordinal) ||
            !decision.ApprovedMutations.SequenceEqual(GameHostBridgeRecipe.ApprovedMutations) ||
            PathsOverlap(AppliedRootPath, sourceExecutionPlan.WorkspacePath))
        {
            throw new ArgumentException("The applied workspace request is not bound to the approved trusted source.");
        }
    }

    public string AppliedRootPath { get; }
    public GameInstallationCandidate OriginalCandidate { get; }
    public ValidatedExecutionPlan SourceExecutionPlan { get; }
    public GameHostRecipeDecision Decision { get; }

    private static bool PathsOverlap(string left, string right) =>
        IsContained(left, right) || IsContained(right, left);

    private static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (normalizedRoot.Equals(normalizedCandidate, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// Sealed in-process result for one committed and activated applied workspace. Future load boundaries
/// must still obtain a fresh source execution plan and independently revalidate these manifests.
/// </summary>
public sealed class ValidatedGameHostAppliedWorkspacePlan
{
    internal ValidatedGameHostAppliedWorkspacePlan(
        ValidatedExecutionPlan sourceExecutionPlan,
        string appliedWorkspaceKey,
        string appliedWorkspacePath,
        string overlayAssemblyPath,
        long overlayAssemblySize,
        string overlayAssemblySha256,
        string rewriteManifestSha256,
        string appliedManifestSha256,
        DateTimeOffset validatedAtUtc)
    {
        SourceExecutionPlan = sourceExecutionPlan;
        AppliedWorkspaceKey = appliedWorkspaceKey;
        AppliedWorkspacePath = appliedWorkspacePath;
        OverlayAssemblyPath = overlayAssemblyPath;
        OverlayAssemblySize = overlayAssemblySize;
        OverlayAssemblySha256 = overlayAssemblySha256;
        RewriteManifestSha256 = rewriteManifestSha256;
        AppliedManifestSha256 = appliedManifestSha256;
        ValidatedAtUtc = validatedAtUtc;
    }

    public ValidatedExecutionPlan SourceExecutionPlan { get; }
    public string AppliedWorkspaceKey { get; }
    public string AppliedWorkspacePath { get; }
    public string OverlayAssemblyPath { get; }
    public long OverlayAssemblySize { get; }
    public string OverlayAssemblySha256 { get; }
    public string RewriteManifestSha256 { get; }
    public string AppliedManifestSha256 { get; }
    public DateTimeOffset ValidatedAtUtc { get; }
}

public sealed record GameHostAppliedWorkspacePreparationResult(
    GameHostAppliedWorkspacePreparationStatus Status,
    ValidatedGameHostAppliedWorkspacePlan? Plan,
    ImmutableArray<DiagnosticRecord> Diagnostics)
{
    public bool IsSuccess =>
        Status is GameHostAppliedWorkspacePreparationStatus.Built or GameHostAppliedWorkspacePreparationStatus.CacheHit;
}

internal delegate ValueTask<GameHostBridgeRewriteResult> GameHostBridgeRewriteOperation(
    GameHostRecipeDecision decision,
    ValidatedExecutionPlan plan,
    RewriteRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Builds one content-addressed applied workspace containing only the rewritten overlay and the two
/// frozen applied manifests. Original commercial payloads remain in the immutable M4 workspace.
/// </summary>
public sealed class GameHostAppliedWorkspacePreparer
{
    private const int MaximumStateBytes = 64 * 1024;
    private const string CommittedDirectoryName = "committed";
    private const string StagingDirectoryName = "staging";
    private const string QuarantineDirectoryName = "quarantine";
    private const string LockFileName = ".workspace.lock";
    private const string PendingPrefix = "pending-";
    private const string OverlayRelativePath = "overlay/assemblies/StardewValley.dll";
    private const string ToolBuildId = "junimogate-gamehost-bridge-writer-v1";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks =
        new(StringComparer.Ordinal);

    private readonly IWorkspaceCandidateRevalidator revalidator;
    private readonly GameHostBridgeRewriteOperation rewriteOperation;

    public GameHostAppliedWorkspacePreparer(IWorkspaceCandidateRevalidator revalidator)
        : this(revalidator, RewriteExactAsync)
    {
    }

    internal GameHostAppliedWorkspacePreparer(
        IWorkspaceCandidateRevalidator revalidator,
        GameHostBridgeRewriteOperation rewriteOperation)
    {
        ArgumentNullException.ThrowIfNull(revalidator);
        ArgumentNullException.ThrowIfNull(rewriteOperation);
        this.revalidator = revalidator;
        this.rewriteOperation = rewriteOperation;
    }

    public async ValueTask<GameHostAppliedWorkspacePreparationResult> PrepareAsync(
        GameHostAppliedWorkspacePreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<DiagnosticRecord>();
        string? stagingPath = null;
        var stagingCommitted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(request.AppliedRootPath);
            await using var lease = await AcquireLeaseAsync(request.AppliedRootPath, cancellationToken).ConfigureAwait(false);

            var committedRoot = Path.Combine(request.AppliedRootPath, CommittedDirectoryName);
            var stagingRoot = Path.Combine(request.AppliedRootPath, StagingDirectoryName);
            var quarantineRoot = Path.Combine(request.AppliedRootPath, QuarantineDirectoryName);
            Directory.CreateDirectory(committedRoot);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(quarantineRoot);

            var recovery = await RecoverAsync(
                request.AppliedRootPath,
                committedRoot,
                stagingRoot,
                quarantineRoot,
                cancellationToken).ConfigureAwait(false);
            if (!recovery)
            {
                return Result(
                    GameHostAppliedWorkspacePreparationStatus.Rejected,
                    null,
                    diagnostics,
                    GameHostAppliedWorkspaceDiagnosticCodes.RecoveryRejected,
                    DiagnosticSeverity.Error,
                    "The applied workspace state could not be recovered safely.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sourceSnapshot = await RevalidateSourceAsync(
                request.SourceExecutionPlan,
                request.OriginalCandidate,
                cancellationToken).ConfigureAwait(false);
            if (sourceSnapshot is null)
            {
                return Result(
                    GameHostAppliedWorkspacePreparationStatus.Rejected,
                    null,
                    diagnostics,
                    GameHostAppliedWorkspaceDiagnosticCodes.SourceRejected,
                    DiagnosticSeverity.Error,
                    "The immutable source workspace or its manifests changed before rewrite.");
            }

            stagingPath = Path.Combine(stagingRoot, PendingPrefix + Guid.NewGuid().ToString("N"));
            var stagingOverlayPath = Path.Combine(stagingPath, OverlayRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stagingOverlayPath)!);

            var inputPath = Path.Combine(
                request.SourceExecutionPlan.WorkspacePath,
                GameHostBridgeRecipe.InputRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var rewrite = await rewriteOperation(
                request.Decision,
                request.SourceExecutionPlan,
                new RewriteRequest(inputPath, stagingOverlayPath, GameHostBridgeRecipe.Identity),
                cancellationToken).ConfigureAwait(false);
            if (!rewrite.IsSuccess ||
                rewrite.InputDigest is null ||
                rewrite.InputModuleVersionId is null ||
                rewrite.Rewrite.OutputDigest is null ||
                rewrite.Mutations.IsDefaultOrEmpty)
            {
                diagnostics.AddRange(rewrite.Rewrite.Diagnostics);
                return Result(
                    GameHostAppliedWorkspacePreparationStatus.Rejected,
                    null,
                    diagnostics,
                    GameHostAppliedWorkspaceDiagnosticCodes.RewriteRejected,
                    DiagnosticSeverity.Error,
                    "The exact bridge writer did not produce an approved overlay.");
            }

            var inputPayload = request.SourceExecutionPlan.Payloads.Single(payload =>
                payload.Kind.Equals("assembly", StringComparison.Ordinal) &&
                payload.RelativePath.Equals(GameHostBridgeRecipe.InputRelativePath, StringComparison.Ordinal));
            var inputIdentity = new AppliedRewriteInputIdentity(
                inputPayload.RelativePath,
                GameHostRecipeCatalog.TestedPlayTargetIdentity,
                rewrite.InputModuleVersionId,
                inputPayload.Size,
                rewrite.InputDigest.Value.ToString());
            var outputInfo = new FileInfo(stagingOverlayPath);
            var outputIdentity = new AppliedRewriteOutputIdentity(
                inputPayload.RelativePath,
                OverlayRelativePath,
                GameHostRecipeCatalog.TestedPlayTargetIdentity,
                rewrite.InputModuleVersionId,
                AppliedModuleVersionIdPolicy.Preserve,
                outputInfo.Length,
                rewrite.Rewrite.OutputDigest.Value.ToString());
            var tool = new AppliedRewriterToolIdentity(
                ToolBuildId,
                GameHostAppliedWorkspaceContract.PinnedMonoCecilVersion);
            var sourceBinding = new AppliedSourceWorkspaceBinding(
                request.SourceExecutionPlan.WorkspaceKey,
                sourceSnapshot.SourceManifestSha256,
                sourceSnapshot.ExtractionManifestSha256,
                sourceSnapshot.RewriteManifestV1Sha256,
                sourceSnapshot.OriginalPayloadSet);
            var appliedKey = GameHostAppliedWorkspaceKey.Create(
                sourceBinding,
                request.Decision.SupportKey,
                GameHostBridgeRecipe.Identity,
                tool,
                [inputIdentity],
                rewrite.Mutations,
                [outputIdentity]);
            var rewriteManifest = new GameHostRewriteManifestV2(
                GameHostAppliedWorkspaceContract.RewriteManifestFormat,
                GameHostAppliedWorkspaceContract.RewriteManifestSchema,
                appliedKey,
                sourceBinding,
                request.Decision.SupportKey,
                GameHostBridgeRecipe.Identity,
                GameHostAppliedWorkspaceContract.RewriteStatusApplied,
                tool,
                [inputIdentity],
                rewrite.Mutations,
                [outputIdentity],
                new AppliedRewritePostValidation(
                    GameHostAppliedWorkspaceContract.PostValidationPassed,
                    ReopenedWithIndependentReader: true,
                    InputGuardsPassed: true,
                    PostconditionsPassed: true,
                    AssemblyIdentityPassed: true,
                    ReferenceClosurePassed: true));

            var rewriteManifestPath = Path.Combine(stagingPath, GameHostAppliedWorkspaceContract.RewriteManifestFileName);
            await WriteJsonNewAsync(rewriteManifestPath, rewriteManifest, cancellationToken).ConfigureAwait(false);
            var rewriteManifestSha256 = await HashFileAsync(rewriteManifestPath, cancellationToken).ConfigureAwait(false);
            var appliedManifest = new GameHostAppliedWorkspaceManifest(
                GameHostAppliedWorkspaceContract.AppliedManifestFormat,
                GameHostAppliedWorkspaceContract.AppliedManifestSchema,
                appliedKey,
                request.SourceExecutionPlan.WorkspaceKey,
                request.Decision.SupportKey,
                GameHostBridgeRecipe.Identity,
                rewriteManifestSha256,
                sourceSnapshot.OriginalPayloadSet.Digest,
                [new AppliedOverlayFileIdentity(
                    "managed-assembly",
                    OverlayRelativePath,
                    outputInfo.Length,
                    rewrite.Rewrite.OutputDigest.Value.ToString())]);
            var appliedManifestPath = Path.Combine(stagingPath, GameHostAppliedWorkspaceContract.AppliedManifestFileName);
            await WriteJsonNewAsync(appliedManifestPath, appliedManifest, cancellationToken).ConfigureAwait(false);
            var appliedManifestSha256 = await HashFileAsync(appliedManifestPath, cancellationToken).ConfigureAwait(false);

            var stagedFiles = EnumerateRelativeFiles(stagingPath);
            var shape = GameHostAppliedWorkspaceValidator.ValidateShape(
                rewriteManifest,
                appliedManifest,
                sourceSnapshot.OriginalPayloads,
                stagedFiles);
            var authorization = GameHostAppliedWorkspaceValidator.ValidateAuthorization(rewriteManifest, request.Decision);
            if (!shape.IsValid || !authorization.IsValid)
            {
                return Result(
                    GameHostAppliedWorkspacePreparationStatus.Rejected,
                    null,
                    diagnostics,
                    GameHostAppliedWorkspaceDiagnosticCodes.ManifestRejected,
                    DiagnosticSeverity.Error,
                    "The staged applied workspace failed exact shape or authorization validation.",
                    string.Join(',', shape.ErrorCodes.Concat(authorization.ErrorCodes).Distinct(StringComparer.Ordinal)));
            }

            var committedPath = Path.Combine(committedRoot, appliedKey);
            var cacheHit = Directory.Exists(committedPath);
            if (cacheHit)
            {
                if (!await ValidateCommittedAsync(
                        committedPath,
                        rewriteManifest,
                        appliedManifest,
                        rewriteManifestSha256,
                        appliedManifestSha256,
                        sourceSnapshot.OriginalPayloads,
                        cancellationToken).ConfigureAwait(false))
                {
                    return Result(
                        GameHostAppliedWorkspacePreparationStatus.Rejected,
                        null,
                        diagnostics,
                        GameHostAppliedWorkspaceDiagnosticCodes.ManifestRejected,
                        DiagnosticSeverity.Error,
                        "An existing applied workspace with the same key failed exact validation.");
                }

                DeleteOwnedTree(stagingPath);
                stagingPath = null;
            }
            else
            {
                Directory.Move(stagingPath, committedPath);
                stagingCommitted = true;
                stagingPath = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var freshCandidate = await revalidator.RevalidateAsync(
                request.SourceExecutionPlan.PackageName,
                cancellationToken).ConfigureAwait(false);
            if (freshCandidate is null ||
                !WorkspaceManifestValidator.CandidateIdentityEquals(
                    request.OriginalCandidate,
                    freshCandidate,
                    includeSourcePaths: true) ||
                !WorkspaceExecutionValidator.MatchesGate0Identity(request.SourceExecutionPlan, freshCandidate))
            {
                return Result(
                    GameHostAppliedWorkspacePreparationStatus.Rejected,
                    null,
                    diagnostics,
                    GameHostAppliedWorkspaceDiagnosticCodes.LiveIdentityChanged,
                    DiagnosticSeverity.Error,
                    "The live installed source changed before applied workspace activation.");
            }

            if (!await ValidateCommittedAsync(
                    committedPath,
                    rewriteManifest,
                    appliedManifest,
                    rewriteManifestSha256,
                    appliedManifestSha256,
                    sourceSnapshot.OriginalPayloads,
                    cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    GameHostAppliedWorkspacePreparationStatus.Rejected,
                    null,
                    diagnostics,
                    GameHostAppliedWorkspaceDiagnosticCodes.ManifestRejected,
                    DiagnosticSeverity.Error,
                    "The committed applied workspace failed validation before activation.");
            }

            await ActivateAsync(request.AppliedRootPath, appliedKey, cancellationToken).ConfigureAwait(false);
            var overlayPath = Path.Combine(
                committedPath,
                OverlayRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var plan = new ValidatedGameHostAppliedWorkspacePlan(
                request.SourceExecutionPlan,
                appliedKey,
                committedPath,
                overlayPath,
                outputInfo.Length,
                rewrite.Rewrite.OutputDigest.Value.ToString(),
                rewriteManifestSha256,
                appliedManifestSha256,
                DateTimeOffset.UtcNow);
            return Result(
                cacheHit
                    ? GameHostAppliedWorkspacePreparationStatus.CacheHit
                    : GameHostAppliedWorkspacePreparationStatus.Built,
                plan,
                diagnostics,
                cacheHit ? GameHostAppliedWorkspaceDiagnosticCodes.CacheHit : GameHostAppliedWorkspaceDiagnosticCodes.Built,
                DiagnosticSeverity.Information,
                cacheHit
                    ? "The exact applied workspace was revalidated and activated from cache."
                    : "The exact applied workspace was committed and activated.");
        }
        catch (OperationCanceledException)
        {
            return Result(
                GameHostAppliedWorkspacePreparationStatus.Cancelled,
                null,
                diagnostics,
                GameHostAppliedWorkspaceDiagnosticCodes.Cancelled,
                DiagnosticSeverity.Warning,
                "Applied workspace preparation was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException or OverflowException)
        {
            return Result(
                GameHostAppliedWorkspacePreparationStatus.Failed,
                null,
                diagnostics,
                GameHostAppliedWorkspaceDiagnosticCodes.CommitFailed,
                DiagnosticSeverity.Error,
                "Applied workspace preparation failed without activating partial output.",
                exception.GetType().Name);
        }
        finally
        {
            if (!stagingCommitted && stagingPath is not null)
            {
                DeleteOwnedTree(stagingPath);
            }
        }
    }

    private static ValueTask<GameHostBridgeRewriteResult> RewriteExactAsync(
        GameHostRecipeDecision decision,
        ValidatedExecutionPlan plan,
        RewriteRequest request,
        CancellationToken cancellationToken) =>
        new GameHostBridgeAssemblyRewriter(decision, plan)
            .RewriteWithEvidenceAsync(request, cancellationToken);

    private static async ValueTask<SourceSnapshot?> RevalidateSourceAsync(
        ValidatedExecutionPlan plan,
        GameInstallationCandidate candidate,
        CancellationToken cancellationToken)
    {
        var manifestValidation = await WorkspaceManifestValidator.ValidateAsync(
            plan.WorkspacePath,
            plan.WorkspaceKey,
            candidate,
            new WorkspaceManifestValidationExpectations(
                WorkspacePreparationOptions.DefaultManifestSchema,
                WorkspacePreparationOptions.DefaultExtractorSchema,
                WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
                WorkspaceExecutionTrustDefaults.Gate0RewriteStatus,
                "none"),
            cancellationToken).ConfigureAwait(false);
        if (!manifestValidation.IsValid)
        {
            return null;
        }

        var payloads = new List<OriginalPayloadIdentity>(plan.Payloads.Count);
        foreach (var payload in plan.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveContainedFile(plan.WorkspacePath, payload.RelativePath);
            if (path is null ||
                !File.Exists(path) ||
                new FileInfo(path).Length != payload.Size ||
                !string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), payload.Sha256, StringComparison.Ordinal))
            {
                return null;
            }

            payloads.Add(new OriginalPayloadIdentity(payload.Kind, payload.RelativePath, payload.Size, payload.Sha256));
        }

        var sourceManifest = ResolveContainedFile(plan.WorkspacePath, WorkspaceManifestConstants.SourceManifestFileName);
        var extractionManifest = ResolveContainedFile(plan.WorkspacePath, WorkspaceManifestConstants.ExtractionManifestFileName);
        var rewriteManifest = ResolveContainedFile(plan.WorkspacePath, WorkspaceManifestConstants.RewriteManifestFileName);
        if (sourceManifest is null || extractionManifest is null || rewriteManifest is null ||
            !File.Exists(sourceManifest) || !File.Exists(extractionManifest) || !File.Exists(rewriteManifest))
        {
            return null;
        }

        return new SourceSnapshot(
            payloads.ToImmutableArray(),
            OriginalPayloadSetIdentity.Create(payloads),
            await HashFileAsync(sourceManifest, cancellationToken).ConfigureAwait(false),
            await HashFileAsync(extractionManifest, cancellationToken).ConfigureAwait(false),
            await HashFileAsync(rewriteManifest, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<bool> ValidateCommittedAsync(
        string committedPath,
        GameHostRewriteManifestV2 expectedRewrite,
        GameHostAppliedWorkspaceManifest expectedApplied,
        string expectedRewriteSha256,
        string expectedAppliedSha256,
        IReadOnlyList<OriginalPayloadIdentity> originalPayloads,
        CancellationToken cancellationToken)
    {
        try
        {
            var rewritePath = Path.Combine(committedPath, GameHostAppliedWorkspaceContract.RewriteManifestFileName);
            var appliedPath = Path.Combine(committedPath, GameHostAppliedWorkspaceContract.AppliedManifestFileName);
            if (!File.Exists(rewritePath) || !File.Exists(appliedPath) ||
                !string.Equals(await HashFileAsync(rewritePath, cancellationToken).ConfigureAwait(false), expectedRewriteSha256, StringComparison.Ordinal) ||
                !string.Equals(await HashFileAsync(appliedPath, cancellationToken).ConfigureAwait(false), expectedAppliedSha256, StringComparison.Ordinal))
            {
                return false;
            }

            var rewrite = await WorkspaceJson.ReadBoundedAsync<GameHostRewriteManifestV2>(
                rewritePath,
                1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            var applied = await WorkspaceJson.ReadBoundedAsync<GameHostAppliedWorkspaceManifest>(
                appliedPath,
                1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            if (rewrite is null || applied is null ||
                !string.Equals(rewrite.AppliedWorkspaceKey, expectedRewrite.AppliedWorkspaceKey, StringComparison.Ordinal) ||
                !string.Equals(applied.AppliedWorkspaceKey, expectedApplied.AppliedWorkspaceKey, StringComparison.Ordinal))
            {
                return false;
            }

            var files = EnumerateRelativeFiles(committedPath);
            var shape = GameHostAppliedWorkspaceValidator.ValidateShape(rewrite, applied, originalPayloads, files);
            if (!shape.IsValid)
            {
                return false;
            }

            foreach (var overlay in applied.OverlayFiles)
            {
                var path = ResolveContainedFile(committedPath, overlay.RelativePath);
                if (path is null ||
                    !File.Exists(path) ||
                    new FileInfo(path).Length != overlay.Size ||
                    !string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false), overlay.Sha256, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException or OverflowException)
        {
            return false;
        }
    }

    private static async ValueTask<bool> RecoverAsync(
        string appliedRoot,
        string committedRoot,
        string stagingRoot,
        string quarantineRoot,
        CancellationToken cancellationToken)
    {
        GameHostAppliedWorkspaceState? state = null;
        var statePath = Path.Combine(appliedRoot, GameHostAppliedWorkspaceContract.StateFileName);
        if (File.Exists(statePath))
        {
            try
            {
                state = await WorkspaceJson.ReadBoundedAsync<GameHostAppliedWorkspaceState>(
                    statePath,
                    MaximumStateBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return false;
            }
        }

        var committedKeys = Directory.EnumerateDirectories(committedRoot)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Cast<string>()
            .ToArray();
        var stagingNames = Directory.EnumerateDirectories(stagingRoot)
            .Select(Path.GetFileName)
            .Where(static name => name is not null)
            .Cast<string>()
            .ToArray();
        var recovery = GameHostAppliedWorkspaceRecoveryPlanner.Create(state, committedKeys, stagingNames);
        if (!recovery.IsValid)
        {
            return false;
        }

        foreach (var stagingName in recovery.OwnedStagingDirectoriesToDelete)
        {
            DeleteOwnedTree(Path.Combine(stagingRoot, stagingName));
        }

        foreach (var key in recovery.OrphanedCommittedKeysToQuarantine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(committedRoot, key);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(
                quarantineRoot,
                $"{key}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
            Directory.Move(source, destination);
        }

        return true;
    }

    private static async ValueTask ActivateAsync(
        string appliedRoot,
        string appliedKey,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(appliedRoot, GameHostAppliedWorkspaceContract.StateFileName);
        GameHostAppliedWorkspaceState? current = null;
        if (File.Exists(statePath))
        {
            current = await WorkspaceJson.ReadBoundedAsync<GameHostAppliedWorkspaceState>(
                statePath,
                MaximumStateBytes,
                cancellationToken).ConfigureAwait(false);
        }

        var previous = current?.ActiveKey is { } active && !active.Equals(appliedKey, StringComparison.Ordinal)
            ? active
            : current?.PreviousKey;
        var next = new GameHostAppliedWorkspaceState(
            GameHostAppliedWorkspaceContract.StateFormat,
            GameHostAppliedWorkspaceContract.StateSchema,
            appliedKey,
            previous);
        var temporary = Path.Combine(appliedRoot, $".{GameHostAppliedWorkspaceContract.StateFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteJsonNewAsync(temporary, next, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async ValueTask<WorkspaceLease> AcquireLeaseAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var processLock = ProcessLocks.GetOrAdd(normalizedRoot, static _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lockPath = Path.Combine(normalizedRoot, LockFileName);
            for (var attempt = 0; attempt < 50; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.Asynchronous);
                    return new WorkspaceLease(processLock, stream);
                }
                catch (IOException) when (attempt < 49)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("Could not acquire the applied workspace file lease.");
        }
        catch
        {
            processLock.Release();
            throw;
        }
    }

    private static async ValueTask WriteJsonNewAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, WorkspaceJson.Options, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async ValueTask<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    private static string? ResolveContainedFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathFullyQualified(relativePath) ||
            relativePath.Contains('\\'))
        {
            return null;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var current = candidate;
        while (!current.Equals(normalizedRoot, StringComparison.Ordinal))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
            if (current.Length == 0)
            {
                return null;
            }
        }

        return candidate;
    }

    private static IReadOnlyList<string> EnumerateRelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void DeleteOwnedTree(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static GameHostAppliedWorkspacePreparationResult Result(
        GameHostAppliedWorkspacePreparationStatus status,
        ValidatedGameHostAppliedWorkspacePlan? plan,
        List<DiagnosticRecord> diagnostics,
        string code,
        DiagnosticSeverity severity,
        string message,
        string? detail = null)
    {
        diagnostics.Add(new DiagnosticRecord(
            DateTimeOffset.UtcNow,
            StartupStage.Rewrite,
            severity,
            code,
            message,
            detail));
        return new GameHostAppliedWorkspacePreparationResult(status, plan, diagnostics.ToImmutableArray());
    }

    private sealed record SourceSnapshot(
        ImmutableArray<OriginalPayloadIdentity> OriginalPayloads,
        OriginalPayloadSetSummary OriginalPayloadSet,
        string SourceManifestSha256,
        string ExtractionManifestSha256,
        string RewriteManifestV1Sha256);

    private sealed class WorkspaceLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim processLock;
        private FileStream? fileLock;

        public WorkspaceLease(SemaphoreSlim processLock, FileStream fileLock)
        {
            this.processLock = processLock;
            this.fileLock = fileLock;
        }

        public ValueTask DisposeAsync()
        {
            fileLock?.Dispose();
            fileLock = null;
            processLock.Release();
            return ValueTask.CompletedTask;
        }
    }
}
