using System.Text.Json;
using Android.Content;
using Android.Util;
using JunimoGate.Android;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.GameHost;

internal static class RuntimeCacheMaintenance
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async ValueTask PrepareRecoveryAsync(
        Context context,
        PreparedGameSnapshot failed,
        GameStartupStage stage,
        int recoveryLevel,
        CancellationToken cancellationToken)
    {
        if (GameSessionRegistry.IsGameProcessActive(context))
            throw new InvalidOperationException("Runtime caches cannot be changed while the game process is active.");
        if (recoveryLevel is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(recoveryLevel));

        BundledSmapiAssets.DiscardCurrentBundle(context);
        await RemoveAppliedAsync(context, failed.AppliedWorkspaceKey, cancellationToken).ConfigureAwait(false);
        if (recoveryLevel == 2)
        {
            await GameLaunchRegistry.DropPreviousIfUsesAsync(context, failed, cancellationToken).ConfigureAwait(false);
            await RemoveSourceAsync(context, failed.SourceWorkspaceKey, cancellationToken).ConfigureAwait(false);
        }

        Log.Info(
            "JunimoGate.Recovery",
            $"cache-reset level={recoveryLevel} stage={stage} source={(recoveryLevel == 2 ? 1 : 0)} applied=1 bundle=1");
    }

    public static async ValueTask PruneAsync(
        Context context,
        GameLaunchRegistry.GameActivationState state,
        CancellationToken cancellationToken)
    {
        if (GameSessionRegistry.IsGameProcessActive(context))
            return;

        try
        {
            var snapshots = await GameLaunchRegistry.ReadRetainedSnapshotsAsync(context, state, cancellationToken)
                .ConfigureAwait(false);
            var sourceKeys = snapshots.Select(static snapshot => snapshot.SourceWorkspaceKey)
                .ToHashSet(StringComparer.Ordinal);
            var appliedKeys = snapshots.Select(static snapshot => snapshot.AppliedWorkspaceKey)
                .ToHashSet(StringComparer.Ordinal);
            var runtimeRoot = AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context);
            long reclaimed = 0;
            var removed = 0;

            removed += PruneKeyDirectories(Path.Combine(runtimeRoot, "workspaces"), sourceKeys, ref reclaimed);
            var appliedRoot = Path.Combine(runtimeRoot, "gamehost-applied-v2");
            removed += PruneKeyDirectories(Path.Combine(appliedRoot, "committed"), appliedKeys, ref reclaimed);
            removed += PruneDeletionTombstones(Path.Combine(runtimeRoot, "workspaces"), ref reclaimed);
            removed += PruneDeletionTombstones(Path.Combine(appliedRoot, "committed"), ref reclaimed);
            PruneAppliedIndexes(Path.Combine(appliedRoot, "cache-index"), appliedKeys);
            await ReconcileSourceStateAsync(runtimeRoot, snapshots, cancellationToken).ConfigureAwait(false);
            await ReconcileAppliedStateAsync(appliedRoot, snapshots, cancellationToken).ConfigureAwait(false);

            removed += PruneOwnedTransientDirectories(Path.Combine(runtimeRoot, "quarantine"), IsSourceQuarantine, ref reclaimed);
            removed += PruneOwnedTransientDirectories(Path.Combine(runtimeRoot, "staging"), IsSourceStaging, ref reclaimed);
            removed += PruneOwnedTransientDirectories(Path.Combine(appliedRoot, "quarantine"), IsAppliedTransient, ref reclaimed);
            removed += PruneOwnedTransientDirectories(Path.Combine(appliedRoot, "staging"), IsAppliedTransient, ref reclaimed);
            removed += PruneLegacyRuntimeTree(Path.Combine(runtimeRoot, "gamehost-applied"), ref reclaimed);
            var legacySmapiRoot = Path.Combine(runtimeRoot, "smapi");
            foreach (var legacyCacheName in new[] { "managed", "mod-rewrite-cache", "runtime", "smapi-internal" })
                removed += PruneLegacyRuntimeTree(Path.Combine(legacySmapiRoot, legacyCacheName), ref reclaimed);
            removed += BundledSmapiAssets.PruneOldBundles(context, ref reclaimed);

            Log.Info("JunimoGate.Cache", $"pruned entries={removed} reclaimedBytes={reclaimed}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.Warn("JunimoGate.Cache", $"prune-skipped:{exception.GetType().Name}");
        }
    }

    private static async ValueTask RemoveSourceAsync(
        Context context,
        string key,
        CancellationToken cancellationToken)
    {
        var root = AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context);
        MoveAndDelete(Path.Combine(root, "workspaces", key));
        var statePath = Path.Combine(root, "workspace-state.json");
        var state = await TryReadAsync<WorkspaceState>(statePath, cancellationToken).ConfigureAwait(false);
        if (state is null || state.ActiveKey != key && state.PreviousKey != key)
            return;
        var active = state.ActiveKey == key ? state.PreviousKey : state.ActiveKey;
        var previous = state.PreviousKey == key || active == state.PreviousKey ? null : state.PreviousKey;
        await WriteAtomicAsync(
                statePath,
                new WorkspaceState("junimogate-workspace-state", "v1", active, previous),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask RemoveAppliedAsync(
        Context context,
        string key,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context),
            "gamehost-applied-v2");
        MoveAndDelete(Path.Combine(root, "committed", key));
        RemoveAppliedIndexesForKey(Path.Combine(root, "cache-index"), key);
        var statePath = Path.Combine(root, GameHostAppliedWorkspaceContract.StateFileName);
        var state = await TryReadAsync<GameHostAppliedWorkspaceState>(statePath, cancellationToken).ConfigureAwait(false);
        if (state is null || state.ActiveKey != key && state.PreviousKey != key)
            return;
        var active = state.ActiveKey == key ? state.PreviousKey : state.ActiveKey;
        var previous = state.PreviousKey == key || active == state.PreviousKey ? null : state.PreviousKey;
        await WriteAtomicAsync(
                statePath,
                new GameHostAppliedWorkspaceState(
                    GameHostAppliedWorkspaceContract.StateFormat,
                    GameHostAppliedWorkspaceContract.StateSchema,
                    active,
                    previous),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static int PruneKeyDirectories(string root, HashSet<string> retained, ref long reclaimed)
    {
        if (!Directory.Exists(root))
            return 0;
        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var key = Path.GetFileName(path);
            if (!IsCacheKey(key) || retained.Contains(key))
                continue;
            reclaimed += GetDirectoryBytes(path);
            MoveAndDelete(path);
            removed++;
        }
        return removed;
    }

    private static int PruneOwnedTransientDirectories(
        string root,
        Func<string, bool> owns,
        ref long reclaimed)
    {
        if (!Directory.Exists(root))
            return 0;
        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (!owns(Path.GetFileName(path)))
                continue;
            reclaimed += GetDirectoryBytes(path);
            MoveAndDelete(path);
            removed++;
        }
        return removed;
    }

    private static int PruneDeletionTombstones(string root, ref long reclaimed)
    {
        if (!Directory.Exists(root))
            return 0;
        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(root, "*.deleting-*", SearchOption.TopDirectoryOnly))
        {
            reclaimed += GetDirectoryBytes(path);
            TryDeleteDirectory(path);
            if (!Directory.Exists(path))
                removed++;
        }
        return removed;
    }

    private static int PruneLegacyRuntimeTree(string path, ref long reclaimed)
    {
        if (!Directory.Exists(path))
            return 0;
        reclaimed += GetDirectoryBytes(path);
        MoveAndDelete(path);
        return 1;
    }

    private static void PruneAppliedIndexes(string root, HashSet<string> retained)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var key = document.RootElement.TryGetProperty("appliedWorkspaceKey", out var element)
                    ? element.GetString()
                    : null;
                if (!IsCacheKey(key) || !retained.Contains(key!))
                    File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                TryDelete(path);
            }
        }
    }

    private static void RemoveAppliedIndexesForKey(string root, string removedKey)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var key = document.RootElement.TryGetProperty("appliedWorkspaceKey", out var element)
                    ? element.GetString()
                    : null;
                if (!IsCacheKey(key) || key == removedKey)
                    File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                TryDelete(path);
            }
        }
    }

    private static async ValueTask ReconcileSourceStateAsync(
        string runtimeRoot,
        IReadOnlyList<PreparedGameSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var keys = snapshots.Select(static snapshot => snapshot.SourceWorkspaceKey)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        await WriteAtomicAsync(
                Path.Combine(runtimeRoot, "workspace-state.json"),
                new WorkspaceState(
                    "junimogate-workspace-state",
                    "v1",
                    keys.ElementAtOrDefault(0),
                    keys.ElementAtOrDefault(1)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask ReconcileAppliedStateAsync(
        string appliedRoot,
        IReadOnlyList<PreparedGameSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var keys = snapshots.Select(static snapshot => snapshot.AppliedWorkspaceKey)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        Directory.CreateDirectory(appliedRoot);
        await WriteAtomicAsync(
                Path.Combine(appliedRoot, GameHostAppliedWorkspaceContract.StateFileName),
                new GameHostAppliedWorkspaceState(
                    GameHostAppliedWorkspaceContract.StateFormat,
                    GameHostAppliedWorkspaceContract.StateSchema,
                    keys.ElementAtOrDefault(0),
                    keys.ElementAtOrDefault(1)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is < 1 or > 64 * 1024)
                return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    private static async ValueTask WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void MoveAndDelete(string path)
    {
        if (!Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A runtime cache directory is a reparse point.");
        var moved = path + $".deleting-{Guid.NewGuid():N}";
        Directory.Move(path, moved);
        TryDeleteDirectory(moved);
    }

    private static long GetDirectoryBytes(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(static file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The directory was already detached from the active cache and can be reclaimed later.
        }
    }

    private static bool IsCacheKey(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSourceQuarantine(string name) =>
        name.Length > 64 && IsCacheKey(name[..64]) && name[64] == '-';

    private static bool IsSourceStaging(string name) =>
        name is { Length: 97 } && IsCacheKey(name[..64]) && name[64] == '-';

    private static bool IsAppliedTransient(string name) =>
        name.StartsWith("pending-", StringComparison.Ordinal) ||
        name.Length > 64 && IsCacheKey(name[..64]) && name[64] == '-';
}
