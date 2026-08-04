using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Android.Content;
using Android.Util;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Mods;
using JunimoGate.Rewriter;
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.GameHost;

public sealed partial record PreparedGameSnapshot
{
    public void ValidateEnvelope(Context context)
    {
        if (!GameLaunchSchema.IsSupportedSnapshot(Schema) ||
            CompatibilityRuleId != GameCompatibilityIds.StardewAndroidMainActivityBridgeV1 ||
            string.IsNullOrWhiteSpace(PackageName) || VersionCode <= 0 ||
            !Sha256Digest.TryParse(PackageMarker, out _) ||
            !IsCacheKey(SourceWorkspaceKey) || !IsCacheKey(AppliedWorkspaceKey) ||
            string.IsNullOrWhiteSpace(SourceWorkspacePath) || string.IsNullOrWhiteSpace(AppliedWorkspacePath) ||
            string.IsNullOrWhiteSpace(OverlayAssemblyPath) || OverlayAssemblySize <= 0 ||
            ManagedAssemblies is null || ManagedAssemblies.Count == 0 ||
            ContentFiles is null || ContentFiles.Count == 0)
        {
            throw new InvalidDataException("The prepared game snapshot is malformed.");
        }

        var safeContext = context.ApplicationContext ?? context;
        var runtimeRoot = Path.GetFullPath(AndroidPrivateStorage.GetRuntimeRoot(safeContext));
        var userDataRoot = Path.GetFullPath(AndroidPrivateStorage.GetUserDataRoot(safeContext));
        var gameSaveRoot = Path.GetFullPath(AndroidPrivateStorage.GetGameSaveRoot(safeContext));
        var expectedSourceWorkspace = Path.GetFullPath(Path.Combine(runtimeRoot, "workspaces", SourceWorkspaceKey));
        var expectedAppliedWorkspace = Path.GetFullPath(Path.Combine(
            runtimeRoot,
            "gamehost-applied-v2",
            "committed",
            AppliedWorkspaceKey));
        foreach (var path in new[]
                 {
                     SourceWorkspacePath, AppliedWorkspacePath, OverlayAssemblyPath,
                 })
        {
            if (!Path.IsPathFullyQualified(path) || !IsContained(path, runtimeRoot))
                throw new InvalidDataException("A prepared game snapshot path is not host-owned.");
        }

        foreach (var path in new[] { ConfigDirectory, LogDirectory, BackupDirectory })
        {
            if (!Path.IsPathFullyQualified(path) || !IsContained(path, userDataRoot))
                throw new InvalidDataException("A prepared SMAPI user-data path is not host-owned.");
        }

        if (!Path.GetFullPath(SourceWorkspacePath).Equals(expectedSourceWorkspace, StringComparison.Ordinal) ||
            !Path.GetFullPath(AppliedWorkspacePath).Equals(expectedAppliedWorkspace, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A prepared workspace path does not match its cache key.");
        }

        if (!Path.IsPathFullyQualified(SaveDirectory) ||
            !Path.GetFullPath(SaveDirectory).Equals(gameSaveRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The prepared game save path is not host-owned.");
        }
    }

    private static bool IsCacheKey(string value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsContained(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(root);
        return fullPath.Equals(fullRoot, StringComparison.Ordinal) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

public enum GamePreparationStage
{
    Checking,
    Discovering,
    Preparing,
}

public sealed record GamePreparationProgress(GamePreparationStage Stage, string Message);

public enum GamePreparationStatus
{
    Ready,
    GameNotInstalled,
    Unsupported,
    Failed,
}

public sealed class PreparedGameHandle
{
    internal PreparedGameHandle(string snapshotId, PreparedGameSnapshot snapshot, bool reused)
    {
        SnapshotId = snapshotId;
        Snapshot = snapshot;
        Reused = reused;
    }

    public string SnapshotId { get; }
    public string VersionName => Snapshot.VersionName;
    public long VersionCode => Snapshot.VersionCode;
    public bool Reused { get; }
    internal PreparedGameSnapshot Snapshot { get; }
}

public sealed record GamePreparationResult(
    GamePreparationStatus Status,
    PreparedGameHandle? PreparedGame,
    string Code,
    string Message,
    DeepPrepareMetrics? Metrics = null)
{
    public bool IsReady => Status == GamePreparationStatus.Ready && PreparedGame is not null;
}

public sealed record GameLaunchDescriptor(
    string Schema,
    string SnapshotId,
    string CapabilityKey,
    int RecoveryLevel,
    ProfileLaunchSelection Profile,
    string? ModSelectionId,
    DateTimeOffset IssuedAtUtc);

public sealed record ProfileLaunchSelection(
    string ProfileId,
    long Revision,
    ModAssemblyBindingPolicy AssemblyBindingPolicy)
{
    public ProfileId Validate()
    {
        if (!JunimoGate.Mods.ProfileId.TryParse(ProfileId, out var id) || Revision < 1 ||
            !Enum.IsDefined(AssemblyBindingPolicy))
        {
            throw new InvalidDataException("The launch Profile selection is malformed.");
        }
        return id;
    }

    public static ProfileLaunchSelection From(ModProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _ = profile.Validate();
        return new ProfileLaunchSelection(profile.Id, profile.Revision, profile.AssemblyBindingPolicy);
    }

    public static ProfileLaunchSelection From(ModLaunchSelectionSnapshot selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _ = selection.Validate();
        return new ProfileLaunchSelection(
            selection.ProfileId,
            selection.ProfileRevision,
            selection.AssemblyBindingPolicy);
    }
}

public sealed record GameLaunchHandle(string Key);

public enum GameStartupStage
{
    LaunchRequest,
    SmapiBundle,
    RuntimeInventory,
    LoaderInstallation,
    GameAssembly,
    SmapiSession,
    Running,
}

public enum GameLaunchOutcomeStatus
{
    Running,
    Failed,
}

public sealed record ConsumedGameLaunch(
    string AttemptId,
    string SnapshotId,
    int RecoveryLevel,
    PreparedGameSnapshot Snapshot,
    ProfileLaunchSelection Profile,
    ModLaunchSelectionSnapshot? ModSelection,
    string ModsRoot,
    IReadOnlyList<string>? ModDirectories);

public sealed record PendingGameLaunchOutcome(
    string AttemptId,
    string SnapshotId,
    int RecoveryLevel,
    PreparedGameSnapshot Snapshot,
    ProfileLaunchSelection Profile,
    ModLaunchSelectionSnapshot? ModSelection,
    GameLaunchOutcomeStatus Status,
    GameStartupStage Stage,
    string Code);

public enum GameLaunchIssueStatus
{
    Issued,
    PackageChanged,
    ActiveSnapshotChanged,
    ProfileChanged,
}

public sealed record GameLaunchIssueResult(GameLaunchIssueStatus Status, GameLaunchHandle? Launch)
{
    public bool IsIssued => Status == GameLaunchIssueStatus.Issued && Launch is not null;
}

public static class GameDeepPrepareCoordinator
{
    public static async ValueTask<GamePreparationResult> PrepareOrReuseAsync(
        Context context,
        IProgress<GamePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await PrepareCoreAsync(context, allowReuse: true, progress, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<GamePreparationResult> PrepareAsync(
        Context context,
        IProgress<GamePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        await PrepareCoreAsync(context, allowReuse: false, progress, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<GamePreparationResult> RecoverAsync(
        Context context,
        PreparedGameSnapshot failedSnapshot,
        GameStartupStage failedStage,
        int recoveryLevel,
        IProgress<GamePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failedSnapshot);
        if (recoveryLevel is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(recoveryLevel));
        if (recoveryLevel == 1 && failedStage is GameStartupStage.LaunchRequest or GameStartupStage.SmapiBundle)
        {
            if (failedStage == GameStartupStage.SmapiBundle)
                BundledSmapiAssets.DiscardCurrentBundle(context);
            var handle = await GameLaunchRegistry.ActivateAsync(context, failedSnapshot, cancellationToken)
                .ConfigureAwait(false);
            return Ready(handle);
        }

        progress?.Report(new GamePreparationProgress(
            GamePreparationStage.Preparing,
            "Preparing the game runtime for another launch…"));
        await RuntimeCacheMaintenance
            .PrepareRecoveryAsync(context, failedSnapshot, failedStage, recoveryLevel, cancellationToken)
            .ConfigureAwait(false);
        return await PrepareCoreAsync(context, allowReuse: false, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<GamePreparationResult> PrepareCoreAsync(
        Context context,
        bool allowReuse,
        IProgress<GamePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var safe = context.ApplicationContext ?? context;
        DeepPrepareMetricsBuilder? deepPrepare = null;
        try
        {
            await AndroidPrivateStorage.EnsureMigratedAsync(safe, cancellationToken).ConfigureAwait(false);
            progress?.Report(new GamePreparationProgress(
                GamePreparationStage.Checking,
                "Checking the installed game and prepared workspace…"));
            if (allowReuse)
            {
                var fast = await GameLaunchRegistry.TryOpenActiveAsync(safe, cancellationToken).ConfigureAwait(false);
                if (fast is not null)
                    return Ready(fast);
            }

            deepPrepare = new DeepPrepareMetricsBuilder();
            progress?.Report(new GamePreparationProgress(
                GamePreparationStage.Discovering,
                "Inspecting the installed Stardew Valley package…"));
            var provider = new AndroidPackageInstallationSnapshotProvider(safe);
            deepPrepare.PackageManagerSnapshotCount++;
            var initialPackage = await provider
                .GetSnapshotAsync(AndroidPlatformBoundary.PlayPackageName, cancellationToken)
                .ConfigureAwait(false);
            if (initialPackage is null)
            {
                return await CompleteDeepPrepareAsync(safe, new GamePreparationResult(
                    GamePreparationStatus.GameNotInstalled,
                    null,
                    GameDiscoveryErrorCodes.PackageNotFoundOrNotVisible,
                    "Stardew Valley is not installed for the current Android user."), deepPrepare).ConfigureAwait(false);
            }

            progress?.Report(new GamePreparationProgress(
                GamePreparationStage.Preparing,
                "Preparing the game for its first JunimoGate launch…"));
            try
            {
                await using var preparationSession = await GameInstallationPreparationSession
                    .OpenAsync(initialPackage, AndroidPlatformBoundary.PlayPackageName, cancellationToken)
                    .ConfigureAwait(false);
                deepPrepare.Capture(preparationSession);
                if (!preparationSession.Candidate.CertificateVerification.AllowsCodeExecution)
                {
                    return await CompleteDeepPrepareAsync(safe, new GamePreparationResult(
                        GamePreparationStatus.Unsupported,
                        null,
                        GameDiscoveryErrorCodes.GameCertificateUnrecognized,
                        "The installed Stardew Valley signing identity is not supported."), deepPrepare).ConfigureAwait(false);
                }

                var result = await PrepareAsync(
                    safe,
                    preparationSession,
                    deepPrepare,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                return await CompleteDeepPrepareAsync(safe, result, deepPrepare).ConfigureAwait(false);
            }
            catch (GameInstallationPreparationException exception)
            {
                Log.Warn(
                    "JunimoGate.DeepPrepare",
                    $"package-preparation-rejected code={exception.Code}",
                    exception);
                return await CompleteDeepPrepareAsync(safe, new GamePreparationResult(
                    GamePreparationStatus.Unsupported,
                    null,
                    exception.Code,
                    "The installed Stardew Valley package is not supported by this JunimoGate build."), deepPrepare).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidDataException or InvalidOperationException or CryptographicException)
        {
            Log.Error("JunimoGate.DeepPrepare", "deep-prepare-failed", exception);
            var result = new GamePreparationResult(
                GamePreparationStatus.Failed,
                null,
                "game_preparation_failed",
                "JunimoGate could not prepare the installed game. Reopen the app to retry.");
            return deepPrepare is null
                ? result
                : await CompleteDeepPrepareAsync(safe, result, deepPrepare).ConfigureAwait(false);
        }
    }

    private static async ValueTask<GamePreparationResult> PrepareAsync(
        Context context,
        GameInstallationPreparationSession preparationSession,
        DeepPrepareMetricsBuilder deepPrepare,
        IProgress<GamePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceWorkspace = await AndroidPlatformBoundary
            .PrepareGameWorkspaceAsync(
                context,
                preparationSession,
                new SourceWorkspaceProgress(progress),
                cancellationToken)
            .ConfigureAwait(false);
        deepPrepare.Capture(sourceWorkspace);
        if (sourceWorkspace.Status is not WorkspacePreparationStatus.Built and
            not WorkspacePreparationStatus.CacheHit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = sourceWorkspace.Diagnostics.FirstOrDefault(item =>
                item.Severity >= DiagnosticSeverity.Error);
            return sourceWorkspace.Status == WorkspacePreparationStatus.Blocked
                ? new GamePreparationResult(
                    GamePreparationStatus.Unsupported,
                    null,
                    diagnostic?.Code ?? "source_workspace_blocked",
                    "The installed game is not allowed to provide an executable workspace.")
                : new GamePreparationResult(
                    GamePreparationStatus.Failed,
                    null,
                    diagnostic?.Code ?? "source_workspace_failed",
                    "JunimoGate could not prepare the installed game workspace.");
        }

        progress?.Report(new GamePreparationProgress(
            GamePreparationStage.Preparing,
            "Checking compatibility and preparing the SMAPI game host…"));
        if (sourceWorkspace.ExecutionPlan is null)
        {
            return new GamePreparationResult(
                GamePreparationStatus.Failed,
                null,
                "source_workspace_evidence_missing",
                "The source workspace did not produce reusable preparation evidence.");
        }

        var prepared = await AndroidGameHostAppliedWorkspaceBoundary
            .PrepareAsync(context, preparationSession, sourceWorkspace.ExecutionPlan, cancellationToken)
            .ConfigureAwait(false);
        deepPrepare.Capture(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        if (!prepared.IsSuccess || prepared.Capability is null)
        {
            var diagnostic = prepared.Diagnostics.FirstOrDefault(item =>
                item.Severity.Equals(DiagnosticSeverity.Error.ToString(), StringComparison.OrdinalIgnoreCase));
            return prepared.Status == AndroidGameHostAppliedWorkspaceStatus.Rejected
                ? new GamePreparationResult(
                    GamePreparationStatus.Unsupported,
                    null,
                    diagnostic?.Code ?? "game_version_unsupported",
                    "This Stardew Valley build is not supported yet.")
                : new GamePreparationResult(
                    GamePreparationStatus.Failed,
                    null,
                    diagnostic?.Code ?? "deep_prepare_failed",
                    "The game preparation transaction did not complete.");
        }

        var capability = prepared.Capability;
        var source = capability.SourceExecutionPlan;
        deepPrepare.PackageManagerSnapshotCount++;
        var package = await new AndroidPackageInstallationSnapshotProvider(context)
            .GetSnapshotAsync(source.PackageName, cancellationToken)
            .ConfigureAwait(false);
        if (package is null || package.VersionName != source.VersionName ||
            package.LongVersionCode != source.LongVersionCode || package.SigningIdentity is null ||
            !KnownGameCertificate.Verify(source.PackageName, package.SigningIdentity).AllowsCodeExecution ||
            PackageUpdateMarker.Create(package) != PackageUpdateMarker.Create(preparationSession.InitialSnapshot))
        {
            return new GamePreparationResult(
                GamePreparationStatus.Failed,
                null,
                "package_changed_after_prepare",
                "The installed game changed while JunimoGate was preparing it. Reopen the app to retry.");
        }

        var managed = source.Payloads
            .Where(static item => item.Kind == "assembly")
            .Select(item => new PreparedManagedAssembly(
                Path.GetFileNameWithoutExtension(item.RelativePath),
                item.RelativePath,
                item.Size))
            .ToArray();
        var content = source.Payloads
            .Where(static item => item.Kind == "content")
            .Select(item => new PreparedContentFile(item.RelativePath, item.Size))
            .ToArray();
        var userDataRoot = AndroidPrivateStorage.GetUserDataRoot(context);
        var snapshot = new PreparedGameSnapshot(
            GameLaunchSchema.Snapshot,
            GameCompatibilityIds.StardewAndroidMainActivityBridgeV1,
            source.PackageName,
            source.VersionName,
            source.LongVersionCode,
            source.SelectedAbi,
            PackageUpdateMarker.Create(package),
            source.WorkspaceKey,
            source.WorkspacePath,
            capability.AppliedExecutionPlan.AppliedWorkspaceKey,
            capability.AppliedExecutionPlan.AppliedWorkspacePath,
            capability.AppliedExecutionPlan.OverlayAssemblyPath,
            capability.AppliedExecutionPlan.OverlayAssemblySize,
            Path.Combine(userDataRoot, "config"),
            Path.Combine(userDataRoot, "logs"),
            AndroidPrivateStorage.GetGameSaveRoot(context),
            Path.Combine(userDataRoot, "save-backups"),
            managed,
            content,
            DateTimeOffset.UtcNow);
        foreach (var directory in new[]
                 {
                     snapshot.ConfigDirectory, snapshot.LogDirectory,
                     snapshot.SaveDirectory, snapshot.BackupDirectory,
                 })
        {
            Directory.CreateDirectory(directory);
        }

        var handle = await GameLaunchRegistry.ActivateAsync(context, snapshot, cancellationToken).ConfigureAwait(false);
        return Ready(handle);
    }

    private static GamePreparationResult Ready(PreparedGameHandle handle) =>
        new(
            GamePreparationStatus.Ready,
            handle,
            handle.Reused ? "fast_launch_ready" : "deep_prepare_ready",
            handle.Reused
                ? "The prepared game is ready."
                : "The installed game was prepared successfully.");

    private static async ValueTask<GamePreparationResult> CompleteDeepPrepareAsync(
        Context context,
        GamePreparationResult result,
        DeepPrepareMetricsBuilder metrics)
    {
        var completed = result with { Metrics = metrics.Build() };
        await DeepPrepareDiagnostics.RecordAsync(context, completed, CancellationToken.None).ConfigureAwait(false);
        return completed;
    }

    private sealed class DeepPrepareMetricsBuilder
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private int apkSourceOpenCount;
        private int apkFullHashCount;
        private long apkBytesHashed;
        private string sourceWorkspaceStatus = "not-run";
        private long sourceWorkspaceDurationMilliseconds;
        private int workspacePayloadHashPassCount;
        private long workspacePayloadBytesHashed;
        private string appliedWorkspaceStatus = "not-run";
        private long appliedWorkspaceDurationMilliseconds;
        private int compatibilityAnalysisCount;
        private int rewriteCount;

        public int PackageManagerSnapshotCount { get; set; }

        public void Capture(GameInstallationPreparationSession session)
        {
            apkSourceOpenCount = session.ApkSourceCount;
            apkFullHashCount = session.ApkSourceCount;
            apkBytesHashed = session.ApkBytesHashed;
        }

        public void Capture(WorkspacePreparationResult result)
        {
            sourceWorkspaceStatus = result.Status.ToString();
            if (result.Metrics is null)
                return;
            sourceWorkspaceDurationMilliseconds = result.Metrics.DurationMilliseconds;
            workspacePayloadHashPassCount = result.Metrics.WorkspacePayloadHashPassCount;
            workspacePayloadBytesHashed = result.Metrics.WorkspacePayloadBytesHashed;
        }

        public void Capture(AndroidGameHostAppliedWorkspaceResult result)
        {
            appliedWorkspaceStatus = result.Status.ToString();
            if (result.Metrics is null)
                return;
            appliedWorkspaceDurationMilliseconds = result.Metrics.DurationMilliseconds;
            compatibilityAnalysisCount = result.Metrics.CompatibilityAnalysisCount;
            rewriteCount = result.Metrics.RewriteCount;
        }

        public DeepPrepareMetrics Build() => new(
            Math.Max(1, stopwatch.ElapsedMilliseconds),
            PackageManagerSnapshotCount,
            apkSourceOpenCount,
            apkFullHashCount,
            apkBytesHashed,
            sourceWorkspaceStatus,
            sourceWorkspaceDurationMilliseconds,
            workspacePayloadHashPassCount,
            workspacePayloadBytesHashed,
            appliedWorkspaceStatus,
            appliedWorkspaceDurationMilliseconds,
            compatibilityAnalysisCount,
            rewriteCount);
    }

    private sealed class SourceWorkspaceProgress(IProgress<GamePreparationProgress>? progress)
        : IProgress<WorkspaceProgressEvent>
    {
        public void Report(WorkspaceProgressEvent value) =>
            progress?.Report(new GamePreparationProgress(
                GamePreparationStage.Preparing,
                value.Message));
    }
}

public static class GameLaunchRegistry
{
    private const int MaximumSnapshotBytes = 64 * 1024 * 1024;
    private const int MaximumDescriptorBytes = 64 * 1024;
    private const int MaximumModSelectionBytes = 2 * 1024 * 1024;
    private const int MaximumStateBytes = 64 * 1024;
    private const int MaximumOutcomeBytes = 64 * 1024;
    private static readonly TimeSpan StaleDescriptorAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan PendingLaunchStartupGrace = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim StateLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async ValueTask<PreparedGameHandle> ActivateAsync(
        Context context,
        PreparedGameSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.ValidateEnvelope(context);
        var root = GetRoot(context);
        var snapshotId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await WriteJsonAsync(Path.Combine(root, $"snapshot-{snapshotId}.json"), snapshot, cancellationToken)
            .ConfigureAwait(false);
        GameActivationState state;
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            var previous = current.ActiveConfirmed && IsSnapshotId(current.ActiveSnapshotId)
                ? current.ActiveSnapshotId
                : current.PreviousSnapshotId;
            state = new GameActivationState(
                GameLaunchSchema.Activation,
                snapshotId,
                ActiveConfirmed: false,
                previous,
                FailedSnapshotId: null,
                Pending: null,
                DateTimeOffset.UtcNow);
            await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateLock.Release();
        }

        await CleanupRegistryAsync(context, state, cancellationToken).ConfigureAwait(false);
        await RuntimeCacheMaintenance.PruneAsync(context, state, cancellationToken).ConfigureAwait(false);
        return CreateHandle(snapshotId, snapshot, reused: false);
    }

    public static async ValueTask<PreparedGameHandle?> TryOpenActiveAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        var active = await TryReadActiveAsync(context, cancellationToken).ConfigureAwait(false);
        return active is null ? null : CreateHandle(active.Value.SnapshotId, active.Value.Snapshot, reused: true);
    }

    public static async ValueTask<RuntimeCacheCleanupResult> CleanRebuildableCachesAsync(
        Context context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (GameSessionRegistry.IsGameProcessActive(context))
            return new RuntimeCacheCleanupResult(0, 0, BlockedByRunningGame: true);
        var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        return await RuntimeCacheMaintenance
            .PruneAsync(context, state, cancellationToken, throwOnFailure: true)
            .ConfigureAwait(false);
    }

    public static async ValueTask<GameLaunchIssueResult> TryIssueLaunchAsync(
        Context context,
        PreparedGameHandle preparedGame,
        ModLaunchSelectionSnapshot modSelection,
        CancellationToken cancellationToken) =>
        await TryIssueLaunchAsync(context, preparedGame, modSelection, recoveryLevel: 0, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<GameLaunchIssueResult> TryIssueLaunchAsync(
        Context context,
        PreparedGameHandle preparedGame,
        ProfileLaunchSelection legacyProfile,
        CancellationToken cancellationToken) =>
        await TryIssueLegacyLaunchAsync(
            context,
            preparedGame,
            legacyProfile,
            recoveryLevel: 0,
            cancellationToken).ConfigureAwait(false);

    public static async ValueTask<GameLaunchIssueResult> TryIssueLaunchAsync(
        Context context,
        PreparedGameHandle preparedGame,
        ModLaunchSelectionSnapshot modSelection,
        int recoveryLevel,
        CancellationToken cancellationToken)
        => await TryIssueCoreAsync(
            context,
            preparedGame,
            ProfileLaunchSelection.From(modSelection),
            modSelection,
            recoveryLevel,
            cancellationToken).ConfigureAwait(false);

    public static async ValueTask<GameLaunchIssueResult> TryIssueLegacyLaunchAsync(
        Context context,
        PreparedGameHandle preparedGame,
        ProfileLaunchSelection legacyProfile,
        int recoveryLevel,
        CancellationToken cancellationToken) =>
        await TryIssueCoreAsync(
            context,
            preparedGame,
            legacyProfile,
            modSelection: null,
            recoveryLevel,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<GameLaunchIssueResult> TryIssueCoreAsync(
        Context context,
        PreparedGameHandle preparedGame,
        ProfileLaunchSelection profile,
        ModLaunchSelectionSnapshot? modSelection,
        int recoveryLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedGame);
        ArgumentNullException.ThrowIfNull(profile);
        if (recoveryLevel is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(recoveryLevel));
        var isCurrent = modSelection is null
            ? await IsCurrentLegacyProfileAsync(context, profile, cancellationToken).ConfigureAwait(false)
            : await IsCurrentModSelectionAsync(context, modSelection, cancellationToken).ConfigureAwait(false);
        if (!isCurrent || modSelection is not null && ProfileLaunchSelection.From(modSelection) != profile)
            return new GameLaunchIssueResult(GameLaunchIssueStatus.ProfileChanged, null);
        var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (!preparedGame.SnapshotId.Equals(state.ActiveSnapshotId, StringComparison.Ordinal) || state.Pending is not null)
            return new GameLaunchIssueResult(GameLaunchIssueStatus.ActiveSnapshotChanged, null);
        if (!await IsCurrentPackageAsync(context, preparedGame.Snapshot, cancellationToken).ConfigureAwait(false))
        {
            return new GameLaunchIssueResult(GameLaunchIssueStatus.PackageChanged, null);
        }

        var root = GetRoot(context);
        CleanupStaleDescriptors(root);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var selectionPath = modSelection is null ? null : GetModSelectionPath(context, modSelection.SelectionId);
        if (selectionPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(selectionPath)!);
            if (File.Exists(selectionPath))
                throw new InvalidDataException("The Mod launch selection identity already exists.");
            await WriteJsonAsync(selectionPath, modSelection, cancellationToken).ConfigureAwait(false);
        }
        var descriptor = new GameLaunchDescriptor(
            GameLaunchSchema.Descriptor,
            preparedGame.SnapshotId,
            key,
            recoveryLevel,
            profile,
            modSelection?.SelectionId,
            DateTimeOffset.UtcNow);
        try
        {
            await WriteJsonAsync(Path.Combine(root, $"descriptor-{key}.json"), descriptor, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (selectionPath is not null)
                TryDeleteFile(selectionPath);
            throw;
        }
        try
        {
            await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
                if (!preparedGame.SnapshotId.Equals(state.ActiveSnapshotId, StringComparison.Ordinal) || state.Pending is not null)
                {
                    TryDeleteFile(Path.Combine(root, $"descriptor-{key}.json"));
                    if (selectionPath is not null)
                        TryDeleteFile(selectionPath);
                    return new GameLaunchIssueResult(GameLaunchIssueStatus.ActiveSnapshotChanged, null);
                }

                state = state with
                {
                    Pending = new PendingLaunchAttempt(
                        key,
                        preparedGame.SnapshotId,
                        recoveryLevel,
                        profile,
                        modSelection?.SelectionId,
                        DateTimeOffset.UtcNow),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                StateLock.Release();
            }
        }
        catch
        {
            TryDeleteFile(Path.Combine(root, $"descriptor-{key}.json"));
            if (selectionPath is not null)
                TryDeleteFile(selectionPath);
            throw;
        }
        Log.Info(
            "JunimoGate.LaunchTrace",
            $"descriptor-issued attempt={key[..8]} level={recoveryLevel} descriptorSnapshotReads=0");
        return new GameLaunchIssueResult(GameLaunchIssueStatus.Issued, new GameLaunchHandle(key));
    }

    public static async ValueTask<ConsumedGameLaunch> ConsumeAsync(
        Context context,
        string key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length != 32 || key.Any(static c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The launch request key is invalid.");

        var root = GetRoot(context);
        var descriptorPath = Path.Combine(root, $"descriptor-{key}.json");
        var consumingPath = descriptorPath + ".consuming";
        try
        {
            File.Move(descriptorPath, consumingPath, overwrite: false);
        }
        catch (IOException)
        {
            throw new InvalidDataException("The launch request was already consumed or is unavailable.");
        }

        try
        {
            var descriptor = await ReadJsonAsync<GameLaunchDescriptor>(
                    consumingPath,
                    MaximumDescriptorBytes,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The launch request is invalid.");
            var isLegacyDescriptor = descriptor.Schema == GameLaunchSchema.LegacyDescriptorV4;
            if (descriptor.Schema != GameLaunchSchema.Descriptor && !isLegacyDescriptor ||
                isLegacyDescriptor && descriptor.ModSelectionId is not null ||
                descriptor.CapabilityKey != key ||
                !IsSnapshotId(descriptor.SnapshotId) || descriptor.RecoveryLevel is < 0 or > 2 ||
                descriptor.ModSelectionId is not null && !IsSnapshotId(descriptor.ModSelectionId))
            {
                throw new InvalidDataException("The launch request is invalid.");
            }

            ModLaunchSelectionSnapshot? modSelection = null;
            if (descriptor.ModSelectionId is not null)
            {
                modSelection = await ReadJsonAsync<ModLaunchSelectionSnapshot>(
                        GetModSelectionPath(context, descriptor.ModSelectionId),
                        MaximumModSelectionBytes,
                        cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("The Mod launch selection is invalid.");
                if (ProfileLaunchSelection.From(modSelection) != descriptor.Profile ||
                    !await IsCurrentModSelectionAsync(context, modSelection, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The Mod launch selection changed before it was consumed.");
                }
            }
            else if (!await IsCurrentLegacyProfileAsync(context, descriptor.Profile, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The legacy Mod Profile changed before it was consumed.");
            }

            var snapshotPath = Path.Combine(root, $"snapshot-{descriptor.SnapshotId}.json");
            var snapshot = await ReadJsonAsync<PreparedGameSnapshot>(
                    snapshotPath,
                    MaximumSnapshotBytes,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The prepared game snapshot is invalid.");
            snapshot.ValidateEnvelope(context);
            string modsRoot;
            IReadOnlyList<string>? modDirectories;
            if (modSelection is null)
            {
                var profileLayout = new ProfileLayout(GetProfilesRoot(context), descriptor.Profile.Validate());
                Directory.CreateDirectory(profileLayout.EnabledDirectory);
                modsRoot = profileLayout.EnabledDirectory;
                modDirectories = null;
            }
            else
            {
                modsRoot = GetModsRoot(context);
                modDirectories = ModLaunchSelectionPathResolver.ResolveExistingRoots(modsRoot, modSelection);
            }
            Log.Info(
                "JunimoGate.LaunchTrace",
                $"descriptor-consumed attempt={key[..8]} level={descriptor.RecoveryLevel} gameSnapshotReads=1");
            return new ConsumedGameLaunch(
                key,
                descriptor.SnapshotId,
                descriptor.RecoveryLevel,
                snapshot,
                descriptor.Profile,
                modSelection,
                modsRoot,
                modDirectories);
        }
        finally
        {
            TryDeleteFile(consumingPath);
        }
    }

    public static async ValueTask RecordOutcomeAsync(
        Context context,
        string attemptId,
        GameLaunchOutcomeStatus status,
        GameStartupStage stage,
        string code,
        CancellationToken cancellationToken)
    {
        if (!IsSnapshotId(attemptId))
            throw new ArgumentException("The launch outcome identity is invalid.");
        code = IsCode(code) ? code : "startup_failed";
        var outcome = new StoredLaunchOutcome(
            GameLaunchSchema.Outcome,
            attemptId,
            status,
            stage,
            code,
            DateTimeOffset.UtcNow);
        await WriteJsonAsync(
                Path.Combine(GetRoot(context), $"outcome-{attemptId}.json"),
                outcome,
                cancellationToken)
            .ConfigureAwait(false);
        Log.Info(
            "JunimoGate.Launch",
            $"outcome-recorded attempt={attemptId[..8]} status={status} stage={stage} code={code}");
    }

    public static async ValueTask<PendingGameLaunchOutcome?> TryReadPendingOutcomeAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        var pending = state.Pending;
        if (pending is null)
            return null;
        if (!IsSnapshotId(pending.AttemptId) || !IsSnapshotId(pending.SnapshotId) ||
            pending.RecoveryLevel is < 0 or > 2 || pending.Profile is null ||
            pending.ModSelectionId is not null && !IsSnapshotId(pending.ModSelectionId))
        {
            await ClearInvalidPendingAsync(context, state, pending, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var snapshot = await TryReadSnapshotAsync(context, pending.SnapshotId, cancellationToken).ConfigureAwait(false);
        var modSelection = pending.ModSelectionId is null
            ? null
            : await TryReadModSelectionAsync(context, pending.ModSelectionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || pending.ModSelectionId is not null &&
            (modSelection is null || ProfileLaunchSelection.From(modSelection) != pending.Profile))
        {
            await ClearInvalidPendingAsync(context, state, pending, cancellationToken).ConfigureAwait(false);
            return null;
        }
        var outcomePath = Path.Combine(GetRoot(context), $"outcome-{pending.AttemptId}.json");
        if (!File.Exists(outcomePath))
        {
            if (GameSessionRegistry.IsGameProcessActive(context))
                return null;
            var pendingAge = DateTimeOffset.UtcNow - pending.IssuedAtUtc;
            if (pendingAge <= PendingLaunchStartupGrace)
            {
                Log.Info(
                    "JunimoGate.Launch",
                    $"launch-still-starting attempt={pending.AttemptId[..8]} level={pending.RecoveryLevel}");
                return null;
            }
            Log.Warn(
                "JunimoGate.Launch",
                $"launch-interrupted attempt={pending.AttemptId[..8]} level={pending.RecoveryLevel}");
            return new PendingGameLaunchOutcome(
                pending.AttemptId,
                pending.SnapshotId,
                pending.RecoveryLevel,
                snapshot,
                pending.Profile,
                modSelection,
                GameLaunchOutcomeStatus.Failed,
                GameStartupStage.LaunchRequest,
                "launch_interrupted");
        }

        try
        {
            var outcome = await ReadJsonAsync<StoredLaunchOutcome>(
                    outcomePath,
                    MaximumOutcomeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome is null || outcome.Schema != GameLaunchSchema.Outcome ||
                outcome.AttemptId != pending.AttemptId || !Enum.IsDefined(outcome.Status) ||
                !Enum.IsDefined(outcome.Stage) || !IsCode(outcome.Code))
            {
                throw new InvalidDataException("The launch outcome is invalid.");
            }
            return new PendingGameLaunchOutcome(
                pending.AttemptId,
                pending.SnapshotId,
                pending.RecoveryLevel,
                snapshot,
                pending.Profile,
                modSelection,
                outcome.Status,
                outcome.Stage,
                outcome.Code);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.Warn(
                "JunimoGate.Launch",
                $"launch-outcome-invalid attempt={pending.AttemptId[..8]} level={pending.RecoveryLevel}",
                exception);
            return new PendingGameLaunchOutcome(
                pending.AttemptId,
                pending.SnapshotId,
                pending.RecoveryLevel,
                snapshot,
                pending.Profile,
                modSelection,
                GameLaunchOutcomeStatus.Failed,
                GameStartupStage.LaunchRequest,
                "launch_outcome_invalid");
        }
    }

    public static async ValueTask<bool> IsLibraryItemInUseAsync(
        Context context,
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        if (libraryItemId is not { Length: 64 } || libraryItemId.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The Mod library item ID is invalid.", nameof(libraryItemId));
        if (GameSessionRegistry.IsGameProcessActive(context))
            return true;
        var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (state.Pending?.ModSelectionId is not { } selectionId)
            return false;
        var selection = await TryReadModSelectionAsync(context, selectionId, cancellationToken).ConfigureAwait(false);
        return selection?.Items.Any(item => item.LibraryItemId == libraryItemId) == true;
    }

    public static async ValueTask CompletePendingRunningAsync(
        Context context,
        PendingGameLaunchOutcome pending,
        CancellationToken cancellationToken)
    {
        GameActivationState? completed = null;
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (state.Pending?.AttemptId != pending.AttemptId)
                return;
            var confirmsActive = state.ActiveSnapshotId == pending.SnapshotId;
            state = state with
            {
                ActiveConfirmed = confirmsActive,
                PreviousSnapshotId = confirmsActive ? null : state.PreviousSnapshotId,
                FailedSnapshotId = null,
                Pending = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
            completed = state;
        }
        finally
        {
            StateLock.Release();
        }
        if (completed is null)
            return;
        Log.Info(
            "JunimoGate.Launch",
            $"running-confirmed attempt={pending.AttemptId[..8]} active={(completed.ActiveConfirmed ? 1 : 0)}");
        CleanupAttemptFiles(context, pending.AttemptId, pending.ModSelection?.SelectionId);
        await CleanupRegistryAsync(context, completed, cancellationToken).ConfigureAwait(false);
        await RuntimeCacheMaintenance.PruneAsync(context, completed, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask CompletePendingFailureAsync(
        Context context,
        PendingGameLaunchOutcome pending,
        CancellationToken cancellationToken)
    {
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (state.Pending?.AttemptId != pending.AttemptId)
                return;
            var failedIsActive = state.ActiveSnapshotId == pending.SnapshotId;
            state = state with
            {
                ActiveSnapshotId = failedIsActive ? null : state.ActiveSnapshotId,
                ActiveConfirmed = failedIsActive ? false : state.ActiveConfirmed,
                FailedSnapshotId = pending.SnapshotId,
                Pending = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
            CleanupAttemptFiles(context, pending.AttemptId, pending.ModSelection?.SelectionId);
            Log.Warn(
                "JunimoGate.Launch",
                $"failure-completed attempt={pending.AttemptId[..8]} stage={pending.Stage} code={pending.Code} level={pending.RecoveryLevel}");
        }
        finally
        {
            StateLock.Release();
        }
    }

    internal static async ValueTask DropPreviousIfUsesAsync(
        Context context,
        PreparedGameSnapshot failed,
        CancellationToken cancellationToken)
    {
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (!IsSnapshotId(state.PreviousSnapshotId))
                return;
            var previous = await TryReadSnapshotAsync(context, state.PreviousSnapshotId!, cancellationToken).ConfigureAwait(false);
            if (previous is null ||
                previous.SourceWorkspaceKey == failed.SourceWorkspaceKey ||
                previous.AppliedWorkspaceKey == failed.AppliedWorkspaceKey)
            {
                state = state with { PreviousSnapshotId = null, UpdatedAtUtc = DateTimeOffset.UtcNow };
                await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            StateLock.Release();
        }
    }

    internal static async ValueTask<IReadOnlyList<PreparedGameSnapshot>> ReadRetainedSnapshotsAsync(
        Context context,
        GameActivationState state,
        CancellationToken cancellationToken)
    {
        var ids = new[] { state.ActiveSnapshotId, state.PreviousSnapshotId, state.Pending?.SnapshotId }
            .Where(IsSnapshotId)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>();
        var snapshots = new List<PreparedGameSnapshot>();
        foreach (var id in ids)
        {
            var snapshot = await TryReadSnapshotAsync(context, id, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
                snapshots.Add(snapshot);
        }
        return snapshots;
    }

    private static async ValueTask<(string SnapshotId, PreparedGameSnapshot Snapshot)?> TryReadActiveAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            var snapshotId = state.ActiveSnapshotId;
            if (!IsSnapshotId(snapshotId))
                return null;
            var snapshot = await TryReadSnapshotAsync(context, snapshotId!, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
                return null;
            Log.Info(
                "JunimoGate.LaunchTrace",
                $"launcher snapshotReads=1 durationMs={Math.Max(1, stopwatch.ElapsedMilliseconds)}");
            return (snapshotId!, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidDataException or CryptographicException or ArgumentException)
        {
            Log.Warn("JunimoGate.LaunchTrace", "active-snapshot-read-failed", exception);
            return null;
        }
    }

    private static async ValueTask<bool> IsCurrentPackageAsync(
        Context context,
        PreparedGameSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var package = await new AndroidPackageInstallationSnapshotProvider(context)
                .GetSnapshotAsync(snapshot.PackageName, cancellationToken)
                .ConfigureAwait(false);
            var current = package is not null && package.VersionName == snapshot.VersionName &&
                package.LongVersionCode == snapshot.VersionCode && package.SigningIdentity is not null &&
                KnownGameCertificate.Verify(snapshot.PackageName, package.SigningIdentity).AllowsCodeExecution &&
                PackageUpdateMarker.Create(package) == snapshot.PackageMarker;
            Log.Info(
                "JunimoGate.LaunchTrace",
                $"launcher packageSnapshots=1 current={(current ? 1 : 0)} durationMs={Math.Max(1, stopwatch.ElapsedMilliseconds)}");
            return current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          InvalidOperationException or CryptographicException)
        {
            Log.Warn("JunimoGate.LaunchTrace", "package-snapshot-check-failed", exception);
            return false;
        }
    }

    private static PreparedGameHandle CreateHandle(string snapshotId, PreparedGameSnapshot snapshot, bool reused) =>
        new(snapshotId, snapshot, reused);

    private static async ValueTask<GameActivationState> TryReadStateAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        var activePath = Path.Combine(GetRoot(context), "active-snapshot.json");
        if (!File.Exists(activePath))
            return GameActivationState.Empty;
        try
        {
            var state = await ReadJsonAsync<GameActivationState>(activePath, MaximumStateBytes, cancellationToken)
                .ConfigureAwait(false);
            return state is not null && state.Schema == GameLaunchSchema.Activation
                ? state
                : GameActivationState.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return GameActivationState.Empty;
        }
    }

    private static Task WriteStateAsync(Context context, GameActivationState state, CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.Combine(GetRoot(context), "active-snapshot.json"), state, cancellationToken);

    private static async ValueTask<PreparedGameSnapshot?> TryReadSnapshotAsync(
        Context context,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(GetRoot(context), $"snapshot-{snapshotId}.json");
            if (!File.Exists(path))
                return null;
            var snapshot = await ReadJsonAsync<PreparedGameSnapshot>(path, MaximumSnapshotBytes, cancellationToken)
                .ConfigureAwait(false);
            snapshot?.ValidateEnvelope(context);
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidDataException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static string GetRoot(Context context)
    {
        var root = Path.Combine(AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context), "launch-sessions");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 1 || file.Length > maximumBytes)
            throw new InvalidDataException("A launch registry document has an invalid size.");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static ValueTask CleanupRegistryAsync(
        Context context,
        GameActivationState state,
        CancellationToken cancellationToken)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in new[] { state.ActiveSnapshotId, state.PreviousSnapshotId, state.FailedSnapshotId, state.Pending?.SnapshotId })
        {
            if (IsSnapshotId(id))
                retained.Add(id!);
        }
        try
        {
            foreach (var path in Directory.EnumerateFiles(GetRoot(context), "snapshot-*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                var id = name.StartsWith("snapshot-", StringComparison.Ordinal) ? name[9..] : string.Empty;
                if (IsSnapshotId(id) && !retained.Contains(id))
                    TryDeleteFile(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Registry cleanup is best-effort and never changes the selected snapshot.
        }
        CleanupStaleDescriptors(GetRoot(context));
        if (!GameSessionRegistry.IsGameProcessActive(context))
        {
            var retainedSelection = state.Pending?.ModSelectionId;
            try
            {
                var selectionRoot = GetModSelectionRoot(context);
                if (Directory.Exists(selectionRoot))
                {
                    foreach (var path in Directory.EnumerateFiles(selectionRoot, "selection-*.json", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(path);
                        var id = name.StartsWith("selection-", StringComparison.Ordinal) ? name[10..] : string.Empty;
                        if (IsSnapshotId(id) && id != retainedSelection)
                            TryDeleteFile(path);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Selection cleanup is best-effort; descriptors still validate their own private snapshot.
            }
        }
        return ValueTask.CompletedTask;
    }

    private static async ValueTask ClearInvalidPendingAsync(
        Context context,
        GameActivationState observed,
        PendingLaunchAttempt pending,
        CancellationToken cancellationToken)
    {
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (state.Pending?.AttemptId != observed.Pending?.AttemptId)
                return;
            var pendingWasActive = IsSnapshotId(pending.SnapshotId) && state.ActiveSnapshotId == pending.SnapshotId;
            state = state with
            {
                ActiveSnapshotId = pendingWasActive ? null : state.ActiveSnapshotId,
                ActiveConfirmed = pendingWasActive ? false : state.ActiveConfirmed,
                FailedSnapshotId = IsSnapshotId(pending.SnapshotId) ? pending.SnapshotId : state.FailedSnapshotId,
                Pending = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateLock.Release();
        }
        if (IsSnapshotId(pending.AttemptId))
            CleanupAttemptFiles(context, pending.AttemptId, pending.ModSelectionId);
    }

    private static void CleanupAttemptFiles(Context context, string attemptId, string? modSelectionId)
    {
        var root = GetRoot(context);
        TryDeleteFile(Path.Combine(root, $"outcome-{attemptId}.json"));
        TryDeleteFile(Path.Combine(root, $"descriptor-{attemptId}.json"));
        TryDeleteFile(Path.Combine(root, $"descriptor-{attemptId}.json.consuming"));
        if (IsSnapshotId(modSelectionId))
            TryDeleteFile(GetModSelectionPath(context, modSelectionId!));
    }

    private static void CleanupStaleDescriptors(string root)
    {
        var cutoff = DateTime.UtcNow - StaleDescriptorAge;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "descriptor-*.json*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                        File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Stale cleanup is best-effort and never blocks a fresh launch request.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The launch request itself will surface any real registry write failure.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale private temporary file is safer than replacing an unrelated path.
        }
    }

    private static bool IsSnapshotId(string? value) =>
        value is { Length: 32 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCode(string value) =>
        value is { Length: > 0 and <= 128 } && value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static string GetProfilesRoot(Context context) =>
        Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context.ApplicationContext ?? context), "profiles");

    private static string GetModsRoot(Context context) =>
        Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context.ApplicationContext ?? context), "mods");

    private static string GetSettingsRoot(Context context) =>
        Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context.ApplicationContext ?? context), "settings");

    private static string GetModSelectionRoot(Context context) =>
        Path.Combine(
            AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context),
            "mod-selections");

    private static string GetModSelectionPath(Context context, string selectionId) =>
        Path.Combine(GetModSelectionRoot(context), $"selection-{selectionId}.json");

    private static async ValueTask<ModLaunchSelectionSnapshot?> TryReadModSelectionAsync(
        Context context,
        string selectionId,
        CancellationToken cancellationToken)
    {
        if (!IsSnapshotId(selectionId))
            return null;
        try
        {
            var selection = await ReadJsonAsync<ModLaunchSelectionSnapshot>(
                    GetModSelectionPath(context, selectionId),
                    MaximumModSelectionBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            _ = selection?.Validate();
            return selection;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static async ValueTask<bool> IsCurrentModSelectionAsync(
        Context context,
        ModLaunchSelectionSnapshot? selection,
        CancellationToken cancellationToken)
    {
        if (selection is null)
            return false;
        ProfileId profileId;
        try
        {
            profileId = selection.Validate();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        try
        {
            var profile = await new ModProfileV2Repository(GetProfilesRoot(context))
                .ReadAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            var library = await new ModLibraryRepository(GetModsRoot(context))
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var legacyDefault = await new ModProfileRepository(GetProfilesRoot(context))
                .ReadAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
            var globalSettings = await new LauncherSettingsRepository(GetSettingsRoot(context))
                .OpenOrCreateAsync(legacyDefault.AssemblyBindingPolicy, cancellationToken)
                .ConfigureAwait(false);
            return selection.Matches(profile, library, globalSettings.DefaultAssemblyBindingPolicy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static async ValueTask<bool> IsCurrentLegacyProfileAsync(
        Context context,
        ProfileLaunchSelection? selection,
        CancellationToken cancellationToken)
    {
        if (selection is null)
            return false;
        ProfileId profileId;
        try
        {
            profileId = selection.Validate();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        try
        {
            var profile = await new ModProfileRepository(GetProfilesRoot(context))
                .ReadAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            return profile.Revision == selection.Revision &&
                   profile.AssemblyBindingPolicy == selection.AssemblyBindingPolicy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    internal sealed record PendingLaunchAttempt(
        string AttemptId,
        string SnapshotId,
        int RecoveryLevel,
        ProfileLaunchSelection Profile,
        string? ModSelectionId,
        DateTimeOffset IssuedAtUtc);

    internal sealed record GameActivationState(
        string Schema,
        string? ActiveSnapshotId,
        bool ActiveConfirmed,
        string? PreviousSnapshotId,
        string? FailedSnapshotId,
        PendingLaunchAttempt? Pending,
        DateTimeOffset UpdatedAtUtc)
    {
        public static GameActivationState Empty { get; } = new(
            GameLaunchSchema.Activation,
            null,
            false,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch);
    }

    private sealed record StoredLaunchOutcome(
        string Schema,
        string AttemptId,
        GameLaunchOutcomeStatus Status,
        GameStartupStage Stage,
        string Code,
        DateTimeOffset RecordedAtUtc);
}
