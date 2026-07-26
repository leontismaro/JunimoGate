using System.Collections.ObjectModel;
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
    public const string Snapshot = "junimogate-prepared-game-snapshot/v1";
    public const string Descriptor = "junimogate-game-launch-descriptor/v1";
    public const string BuildId = "smapi-4.3.2.5-junimogate.1";
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
            string.IsNullOrWhiteSpace(SourceWorkspacePath) || string.IsNullOrWhiteSpace(AppliedWorkspacePath) ||
            string.IsNullOrWhiteSpace(OverlayAssemblyPath) || ManagedAssemblies.Count == 0 || ContentFiles.Count == 0)
            throw new InvalidDataException("The prepared game snapshot is malformed.");

        var runtimeRoot = Path.GetFullPath(AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context));
        foreach (var path in new[] { SourceWorkspacePath, AppliedWorkspacePath, OverlayAssemblyPath, ModsDirectory, InternalDirectory, ConfigDirectory, LogDirectory, SaveDirectory })
        {
            if (!Path.IsPathFullyQualified(path) || !IsContained(path, runtimeRoot) || (!File.Exists(path) && !Directory.Exists(path)))
                throw new FileNotFoundException("A prepared game snapshot path is missing.");
        }

        if (!File.Exists(OverlayAssemblyPath))
            throw new FileNotFoundException("The rewritten game assembly is missing.");
        foreach (var entry in ManagedAssemblies.Select(static item => item.RelativePath).Concat(ContentFiles.Select(static item => item.RelativePath)))
        {
            if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry) ||
                entry.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(static part => part == ".."))
                throw new InvalidDataException("The prepared snapshot contains an escaping relative path.");
        }
        _ = context;
    }

    private static bool IsContained(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(root);
        return fullPath.Equals(fullRoot, StringComparison.Ordinal) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

public sealed record GameLaunchDescriptor(
    string Schema,
    string SnapshotId,
    string CapabilityKey,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record GameLaunchHandle(string Key, DateTimeOffset ExpiresAtUtc);

public static class GameDeepPrepareCoordinator
{
    public static async ValueTask<GameLaunchHandle> PrepareOrReuseAsync(Context context, CancellationToken cancellationToken = default)
    {
        var safe = context.ApplicationContext ?? context;
        await AndroidPrivateStorage.EnsureMigratedAsync(safe, cancellationToken).ConfigureAwait(false);
        var fast = await GameLaunchRegistry.TryReuseActiveAsync(safe, cancellationToken).ConfigureAwait(false);
        if (fast is not null) return fast;
        var discovery = await AndroidPlatformBoundary.DiscoverGamesAsync(safe, cancellationToken).ConfigureAwait(false);
        var candidate = discovery.Candidates.SingleOrDefault(item => item.Installation.PackageName == AndroidPlatformBoundary.PlayPackageName)
            ?? throw new InvalidOperationException("The supported Play game installation is unavailable.");
        return await PrepareAndIssueAsync(safe, candidate, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<GameLaunchHandle> PrepareAndIssueAsync(
        Context context,
        GameInstallationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var safe = context.ApplicationContext ?? context;
        await AndroidPrivateStorage.EnsureMigratedAsync(safe, cancellationToken).ConfigureAwait(false);
        var fast = await GameLaunchRegistry.TryReuseActiveAsync(safe, candidate, cancellationToken).ConfigureAwait(false);
        if (fast is not null)
            return fast;

        var prepared = await AndroidGameHostAppliedWorkspaceBoundary.PrepareAsync(context, candidate, cancellationToken).ConfigureAwait(false);
        if (!prepared.IsSuccess || prepared.Capability is null)
            throw new InvalidOperationException("Deep Prepare did not produce an applied workspace.");

        var capability = prepared.Capability;
        var source = capability.SourceExecutionPlan;
        var managed = source.Payloads
            .Where(static item => item.Kind == "assembly")
            .Select(item => new PreparedManagedAssembly(Path.GetFileNameWithoutExtension(item.RelativePath), item.RelativePath, item.Size))
            .ToArray();
        var content = source.Payloads
            .Where(static item => item.Kind == "content")
            .Select(item => new PreparedContentFile(item.RelativePath, item.Size))
            .ToArray();
        var runtimeRoot = AndroidPrivateStorage.GetRuntimeRoot(safe);
        var smapiRoot = Path.Combine(runtimeRoot, "smapi");
        var snapshot = new PreparedGameSnapshot(
            GameLaunchSchema.Snapshot, GameLaunchSchema.BuildId,
            source.PackageName, source.VersionName, source.LongVersionCode, source.SelectedAbi,
            source.WorkspacePath, capability.AppliedExecutionPlan.AppliedWorkspacePath,
            capability.AppliedExecutionPlan.OverlayAssemblyPath,
            Path.Combine(smapiRoot, "profiles", "default", "Mods", "enabled"),
            Path.Combine(smapiRoot, "runtime", "smapi-internal"),
            Path.Combine(smapiRoot, "config"), Path.Combine(smapiRoot, "logs"), Path.Combine(smapiRoot, "saves"),
            managed, content, DateTimeOffset.UtcNow);
        foreach (var directory in new[] { snapshot.ModsDirectory, snapshot.InternalDirectory, snapshot.ConfigDirectory, snapshot.LogDirectory, snapshot.SaveDirectory })
            Directory.CreateDirectory(directory);
        return await GameLaunchRegistry.IssueAsync(safe, snapshot, cancellationToken).ConfigureAwait(false);
    }
}

public static class GameLaunchRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async ValueTask<GameLaunchHandle> IssueAsync(Context context, PreparedGameSnapshot snapshot, CancellationToken cancellationToken)
    {
        var root = GetRoot(context);
        var snapshotId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await WriteSnapshotAndActiveAsync(root, snapshotId, snapshot, cancellationToken).ConfigureAwait(false);
        return await IssueDescriptorAsync(root, snapshotId, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<GameLaunchHandle?> TryReuseActiveAsync(Context context, GameInstallationCandidate candidate, CancellationToken cancellationToken)
    {
        var root = GetRoot(context);
        var activePath = Path.Combine(root, "active-snapshot.json");
        if (!File.Exists(activePath)) return null;
        var snapshotId = (await File.ReadAllTextAsync(activePath, cancellationToken).ConfigureAwait(false)).Trim();
        if (snapshotId.Length != 32 || snapshotId.Any(static c => !Uri.IsHexDigit(c))) return null;
        var snapshotPath = Path.Combine(root, $"snapshot-{snapshotId}.json");
        if (!File.Exists(snapshotPath)) return null;
        var snapshot = JsonSerializer.Deserialize<PreparedGameSnapshot>(await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false), JsonOptions);
        if (snapshot is null || snapshot.PackageName != candidate.Installation.PackageName || snapshot.VersionName != candidate.Installation.VersionName || snapshot.VersionCode != candidate.Installation.LongVersionCode)
            return null;
        snapshot.ValidateFast(context);
        return await IssueDescriptorAsync(root, snapshotId, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<GameLaunchHandle?> TryReuseActiveAsync(Context context, CancellationToken cancellationToken)
    {
        var root = GetRoot(context);
        var activePath = Path.Combine(root, "active-snapshot.json");
        if (!File.Exists(activePath)) return null;
        var snapshotId = (await File.ReadAllTextAsync(activePath, cancellationToken).ConfigureAwait(false)).Trim();
        if (snapshotId.Length != 32 || snapshotId.Any(static c => !Uri.IsHexDigit(c))) return null;
        var snapshotPath = Path.Combine(root, $"snapshot-{snapshotId}.json");
        if (!File.Exists(snapshotPath)) return null;
        var snapshot = JsonSerializer.Deserialize<PreparedGameSnapshot>(await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false), JsonOptions);
        if (snapshot is null) return null;
        var package = await new AndroidPackageInstallationSnapshotProvider(context).GetSnapshotAsync(snapshot.PackageName, cancellationToken).ConfigureAwait(false);
        if (package is null || package.VersionName != snapshot.VersionName || package.LongVersionCode != snapshot.VersionCode ||
            package.SigningIdentity is null || !KnownGameCertificate.Verify(snapshot.PackageName, package.SigningIdentity).AllowsCodeExecution)
            return null;
        snapshot.ValidateFast(context);
        return await IssueDescriptorAsync(root, snapshotId, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<GameLaunchHandle> IssueDescriptorAsync(string root, string snapshotId, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var issued = DateTimeOffset.UtcNow;
        var descriptor = new GameLaunchDescriptor(GameLaunchSchema.Descriptor, snapshotId, key, issued, issued.AddMinutes(10));
        await WriteJsonAsync(Path.Combine(root, $"descriptor-{key}.json"), descriptor, cancellationToken).ConfigureAwait(false);
        return new GameLaunchHandle(key, descriptor.ExpiresAtUtc);
    }

    private static async Task WriteSnapshotAndActiveAsync(string root, string snapshotId, PreparedGameSnapshot snapshot, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(Path.Combine(root, $"snapshot-{snapshotId}.json"), snapshot, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, "active-snapshot.json"), snapshotId, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<PreparedGameSnapshot> ConsumeAsync(Context context, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length != 32 || key.Any(static c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("The launch capability key is invalid.");
        var root = GetRoot(context);
        var descriptorPath = Path.Combine(root, $"descriptor-{key}.json");
        var consumingPath = descriptorPath + ".consuming";
        try
        {
            File.Move(descriptorPath, consumingPath, overwrite: false);
        }
        catch (IOException)
        {
            throw new InvalidDataException("The launch descriptor was already consumed or is unavailable.");
        }
        var descriptor = JsonSerializer.Deserialize<GameLaunchDescriptor>(await File.ReadAllTextAsync(consumingPath, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("The launch descriptor is invalid.");
        File.Delete(consumingPath);
        if (descriptor.Schema != GameLaunchSchema.Descriptor || descriptor.CapabilityKey != key || descriptor.ExpiresAtUtc < DateTimeOffset.UtcNow)
            throw new InvalidDataException("The launch descriptor is expired or invalid.");
        var snapshotPath = Path.Combine(root, $"snapshot-{descriptor.SnapshotId}.json");
        var snapshot = JsonSerializer.Deserialize<PreparedGameSnapshot>(await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("The prepared game snapshot is invalid.");
        snapshot.ValidateFast(context);
        return snapshot;
    }

    private static string GetRoot(Context context)
    {
        var root = Path.Combine(AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context), "launch-sessions");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(value, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }
}
