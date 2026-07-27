using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Android.Content;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;

namespace JunimoGate.GameHost;

public static class GameLaunchSchema
{
    public const string Snapshot = "junimogate-prepared-game-snapshot/v2";
    public const string Descriptor = "junimogate-game-launch-descriptor/v2";
    public const string BuildId = "smapi-4.3.2.5-junimogate.2";
}

public sealed record PreparedManagedAssembly(string SimpleName, string RelativePath, long Size);

public sealed record PreparedContentFile(string RelativePath, long Size);

public sealed record PreparedGameSnapshot(
    string Schema,
    string BuildId,
    string PackageName,
    string VersionName,
    long VersionCode,
    string Abi,
    string PackageMarker,
    string SourceWorkspacePath,
    string AppliedWorkspacePath,
    string OverlayAssemblyPath,
    string ModsDirectory,
    string InternalDirectory,
    string ConfigDirectory,
    string LogDirectory,
    string SaveDirectory,
    IReadOnlyList<PreparedManagedAssembly> ManagedAssemblies,
    IReadOnlyList<PreparedContentFile> ContentFiles,
    DateTimeOffset PreparedAtUtc)
{
    public void ValidateFast(Context context)
    {
        if (Schema != GameLaunchSchema.Snapshot || BuildId != GameLaunchSchema.BuildId ||
            string.IsNullOrWhiteSpace(PackageName) || VersionCode <= 0 ||
            !Sha256Digest.TryParse(PackageMarker, out _) ||
            string.IsNullOrWhiteSpace(SourceWorkspacePath) || string.IsNullOrWhiteSpace(AppliedWorkspacePath) ||
            string.IsNullOrWhiteSpace(OverlayAssemblyPath) || ManagedAssemblies.Count == 0 || ContentFiles.Count == 0)
        {
            throw new InvalidDataException("The prepared game snapshot is malformed.");
        }

        var runtimeRoot = Path.GetFullPath(AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context));
        foreach (var path in new[]
                 {
                     SourceWorkspacePath, AppliedWorkspacePath, OverlayAssemblyPath, ModsDirectory,
                     InternalDirectory, ConfigDirectory, LogDirectory, SaveDirectory,
                 })
        {
            if (!Path.IsPathFullyQualified(path) || !IsContained(path, runtimeRoot) ||
                (!File.Exists(path) && !Directory.Exists(path)))
            {
                throw new FileNotFoundException("A prepared game snapshot path is missing.");
            }
        }

        if (!File.Exists(OverlayAssemblyPath))
            throw new FileNotFoundException("The rewritten game assembly is missing.");
        if (!Directory.Exists(Path.Combine(SourceWorkspacePath, "Content")))
            throw new DirectoryNotFoundException("The prepared Content root is missing.");

        foreach (var assembly in ManagedAssemblies)
        {
            ValidateRelativePath(assembly.RelativePath);
            if (assembly.Size < 0)
                throw new InvalidDataException("The prepared managed assembly size is invalid.");
            var path = ResolveContainedRelativePath(SourceWorkspacePath, assembly.RelativePath);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != assembly.Size)
                throw new FileNotFoundException("A required prepared managed assembly is missing or changed.");
        }

        foreach (var entry in ContentFiles.Select(static item => item.RelativePath))
            ValidateRelativePath(entry);
    }

    private static void ValidateRelativePath(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) ||
            entry.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(static part => part == ".."))
        {
            throw new InvalidDataException("The prepared snapshot contains an escaping relative path.");
        }
    }

    private static string ResolveContainedRelativePath(string root, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!IsContained(path, root))
            throw new InvalidDataException("The prepared snapshot contains an escaping relative path.");
        return path;
    }

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

public sealed record PreparedGameHandle(
    string SnapshotId,
    string VersionName,
    long VersionCode,
    bool Reused);

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
    DateTimeOffset IssuedAtUtc);

public sealed record GameLaunchHandle(string Key);

public static class GameDeepPrepareCoordinator
{
    public static async ValueTask<GamePreparationResult> PrepareOrReuseAsync(
        Context context,
        IProgress<GamePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var safe = context.ApplicationContext ?? context;
        DeepPrepareMetricsBuilder? deepPrepare = null;
        try
        {
            await AndroidPrivateStorage.EnsureMigratedAsync(safe, cancellationToken).ConfigureAwait(false);
            progress?.Report(new GamePreparationProgress(
                GamePreparationStage.Checking,
                "Checking the installed game and prepared workspace…"));
            var fast = await GameLaunchRegistry.TryGetReusableActiveAsync(safe, cancellationToken).ConfigureAwait(false);
            if (fast is not null)
                return Ready(fast with { Reused = true });

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
        var runtimeRoot = AndroidPrivateStorage.GetRuntimeRoot(context);
        var smapiRoot = Path.Combine(runtimeRoot, "smapi");
        var snapshot = new PreparedGameSnapshot(
            GameLaunchSchema.Snapshot,
            GameLaunchSchema.BuildId,
            source.PackageName,
            source.VersionName,
            source.LongVersionCode,
            source.SelectedAbi,
            PackageUpdateMarker.Create(package),
            source.WorkspacePath,
            capability.AppliedExecutionPlan.AppliedWorkspacePath,
            capability.AppliedExecutionPlan.OverlayAssemblyPath,
            Path.Combine(smapiRoot, "profiles", "default", "Mods", "enabled"),
            Path.Combine(smapiRoot, "runtime", "smapi-internal"),
            Path.Combine(smapiRoot, "config"),
            Path.Combine(smapiRoot, "logs"),
            Path.Combine(smapiRoot, "saves"),
            managed,
            content,
            DateTimeOffset.UtcNow);
        foreach (var directory in new[]
                 {
                     snapshot.ModsDirectory, snapshot.InternalDirectory, snapshot.ConfigDirectory,
                     snapshot.LogDirectory, snapshot.SaveDirectory,
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
        private int managedProbeCount;
        private int nativeInventoryCount;
        private int recipeEvaluationCount;
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
            managedProbeCount = result.Metrics.ManagedProbeCount;
            nativeInventoryCount = result.Metrics.NativeInventoryCount;
            recipeEvaluationCount = result.Metrics.RecipeEvaluationCount;
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
            managedProbeCount,
            nativeInventoryCount,
            recipeEvaluationCount,
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
    private static readonly TimeSpan StaleDescriptorAge = TimeSpan.FromDays(1);
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
        snapshot.ValidateFast(context);
        var root = GetRoot(context);
        var snapshotId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await WriteJsonAsync(Path.Combine(root, $"snapshot-{snapshotId}.json"), snapshot, cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(Path.Combine(root, "active-snapshot.json"), snapshotId, cancellationToken)
            .ConfigureAwait(false);
        return CreateHandle(snapshotId, snapshot, reused: false);
    }

    public static async ValueTask<PreparedGameHandle?> TryGetReusableActiveAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        var active = await TryReadActiveAsync(context, cancellationToken).ConfigureAwait(false);
        if (active is null || !await IsCurrentPackageAsync(context, active.Value.Snapshot, cancellationToken).ConfigureAwait(false))
            return null;
        return CreateHandle(active.Value.SnapshotId, active.Value.Snapshot, reused: true);
    }

    public static async ValueTask<GameLaunchHandle?> TryIssueActiveLaunchAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        var active = await TryReadActiveAsync(context, cancellationToken).ConfigureAwait(false);
        if (active is null || !await IsCurrentPackageAsync(context, active.Value.Snapshot, cancellationToken).ConfigureAwait(false))
            return null;

        var root = GetRoot(context);
        CleanupStaleDescriptors(root);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var descriptor = new GameLaunchDescriptor(
            GameLaunchSchema.Descriptor,
            active.Value.SnapshotId,
            key,
            DateTimeOffset.UtcNow);
        await WriteJsonAsync(Path.Combine(root, $"descriptor-{key}.json"), descriptor, cancellationToken)
            .ConfigureAwait(false);
        return new GameLaunchHandle(key);
    }

    public static async ValueTask<PreparedGameSnapshot> ConsumeAsync(
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
            if (descriptor.Schema != GameLaunchSchema.Descriptor || descriptor.CapabilityKey != key ||
                descriptor.SnapshotId.Length != 32 || descriptor.SnapshotId.Any(static c => !Uri.IsHexDigit(c)))
            {
                throw new InvalidDataException("The launch request is invalid.");
            }

            var snapshotPath = Path.Combine(root, $"snapshot-{descriptor.SnapshotId}.json");
            var snapshot = await ReadJsonAsync<PreparedGameSnapshot>(
                    snapshotPath,
                    MaximumSnapshotBytes,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The prepared game snapshot is invalid.");
            snapshot.ValidateFast(context);
            return snapshot;
        }
        finally
        {
            TryDeleteFile(consumingPath);
        }
    }

    private static async ValueTask<(string SnapshotId, PreparedGameSnapshot Snapshot)?> TryReadActiveAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = GetRoot(context);
            var activePath = Path.Combine(root, "active-snapshot.json");
            if (!File.Exists(activePath))
                return null;
            var snapshotId = (await File.ReadAllTextAsync(activePath, cancellationToken).ConfigureAwait(false)).Trim();
            if (snapshotId.Length != 32 || snapshotId.Any(static c => !Uri.IsHexDigit(c)))
                return null;
            var snapshotPath = Path.Combine(root, $"snapshot-{snapshotId}.json");
            if (!File.Exists(snapshotPath))
                return null;
            var snapshot = await ReadJsonAsync<PreparedGameSnapshot>(
                    snapshotPath,
                    MaximumSnapshotBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
                return null;
            snapshot.ValidateFast(context);
            return (snapshotId, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidDataException or CryptographicException or ArgumentException)
        {
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
            var package = await new AndroidPackageInstallationSnapshotProvider(context)
                .GetSnapshotAsync(snapshot.PackageName, cancellationToken)
                .ConfigureAwait(false);
            return package is not null && package.VersionName == snapshot.VersionName &&
                package.LongVersionCode == snapshot.VersionCode && package.SigningIdentity is not null &&
                KnownGameCertificate.Verify(snapshot.PackageName, package.SigningIdentity).AllowsCodeExecution &&
                PackageUpdateMarker.Create(package) == snapshot.PackageMarker;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          InvalidOperationException or CryptographicException)
        {
            return false;
        }
    }

    private static PreparedGameHandle CreateHandle(string snapshotId, PreparedGameSnapshot snapshot, bool reused) =>
        new(snapshotId, snapshot.VersionName, snapshot.VersionCode, reused);

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

    private static async Task WriteTextAsync(string path, string value, CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, value, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
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
}
