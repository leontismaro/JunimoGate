using System.Text.Json;
using Android.Content;
using Android.Util;
using JunimoGate.Android;
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.GameHost;

internal sealed record PreparedSmapiBundleFile(string RelativePath, long Size);

internal sealed record PreparedSmapiBundle(
    string BundleId,
    string RootPath,
    string InternalDirectory,
    IReadOnlyList<PreparedSmapiBundleFile> Files);

internal static class BundledSmapiAssets
{
    private const string ManifestSchema = "junimogate-smapi-bundle/v2";
    private const string PackagedManifestAssetPath = "smapi-bundle-manifest.json";
    private const string ManifestFileName = "bundle-manifest.json";
    private const string BundleIdPrefix = "smapi-bundle-";
    private const int BundleIdDigestLength = 24;
    private const int MaximumManifestBytes = 128 * 1024;
    private const int MaximumBundleFiles = 256;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void DiscardCurrentBundle(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safe = context.ApplicationContext ?? context;
        var expected = ReadPackagedManifest(safe);
        MoveAndDeleteDirectory(GetBundleRoot(safe, expected.BundleId));
    }

    public static void DiscardCurrentRuntimeCaches(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safe = context.ApplicationContext ?? context;
        var expected = ReadPackagedManifest(safe);
        string smapiRoot = Path.Combine(AndroidPrivateStorage.GetRuntimeRoot(safe), "smapi");
        foreach (string cache in new[] { "assembly-load-cache-v1", "mod-rewrite-cache-v2" })
            MoveAndDeleteDirectory(Path.Combine(smapiRoot, cache, expected.BundleId));
    }

    public static int PruneOldBundles(Context context, ref long reclaimedBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safe = context.ApplicationContext ?? context;
        var expected = ReadPackagedManifest(safe);
        var bundlesRoot = Path.Combine(AndroidPrivateStorage.GetRuntimeRoot(safe), "smapi", "bundles");
        if (!Directory.Exists(bundlesRoot))
            return 0;
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(bundlesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(directory).Equals(expected.BundleId, StringComparison.Ordinal))
                continue;
            reclaimedBytes += GetDirectoryBytes(directory);
            MoveAndDeleteDirectory(directory);
            removed++;
        }
        return removed;
    }

    public static int PruneOldRuntimeCaches(Context context, ref long reclaimedBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safe = context.ApplicationContext ?? context;
        var expected = ReadPackagedManifest(safe);
        string smapiRoot = Path.Combine(AndroidPrivateStorage.GetRuntimeRoot(safe), "smapi");
        int removed = 0;
        foreach (string cache in new[] { "assembly-load-cache-v1", "mod-rewrite-cache-v2" })
        {
            string root = Path.Combine(smapiRoot, cache);
            if (!Directory.Exists(root))
                continue;
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Equals(expected.BundleId, StringComparison.Ordinal))
                    continue;
                reclaimedBytes += GetDirectoryBytes(directory);
                MoveAndDeleteDirectory(directory);
                removed++;
            }
        }
        return removed;
    }

    public static async Task<PreparedSmapiBundle> ProvisionAndValidateAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safe = context.ApplicationContext ?? context;
        var expected = ReadPackagedManifest(safe);
        var bundleRoot = GetBundleRoot(safe, expected.BundleId);
        if (TryReadCurrentBundle(bundleRoot, expected, validateFiles: false, out var current))
        {
            Log.Info(
                "JunimoGate.LaunchTrace",
                $"game smapiBundle=cache-hit id={expected.BundleId} files={expected.Files.Count}");
            return current;
        }

        var parent = Path.GetDirectoryName(bundleRoot)
            ?? throw new InvalidDataException("The SMAPI bundle root is invalid.");
        Directory.CreateDirectory(parent);
        var staging = bundleRoot + $".{Guid.NewGuid():N}.staging";
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var asset in expected.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResolveTarget(staging, asset.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = safe.Assets!.Open(asset.AssetPath);
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.SequentialScan);
                await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                if (output.Length != asset.Size)
                    throw new InvalidDataException("A bundled SMAPI asset size does not match its build manifest.");
            }

            await WriteManifestAsync(Path.Combine(staging, ManifestFileName), expected, cancellationToken)
                .ConfigureAwait(false);
            CommitDirectory(staging, bundleRoot);
            Log.Info(
                "JunimoGate.LaunchTrace",
                $"game smapiBundle=deployed id={expected.BundleId} files={expected.Files.Count}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        if (!TryReadCurrentBundle(bundleRoot, expected, validateFiles: true, out current))
            throw new InvalidDataException("The bundled SMAPI asset deployment did not validate.");
        return current;
    }

    private static string GetBundleRoot(Context context, string bundleId) => Path.GetFullPath(Path.Combine(
        AndroidPrivateStorage.GetRuntimeRoot(context),
        "smapi",
        "bundles",
        bundleId));

    private static BundledAssetManifest ReadPackagedManifest(Context context)
    {
        try
        {
            using var input = context.Assets?.Open(PackagedManifestAssetPath)
                ?? throw new InvalidDataException("The packaged SMAPI bundle manifest is missing.");
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (output.Length + read > MaximumManifestBytes)
                    throw new InvalidDataException("The packaged SMAPI bundle manifest is too large.");
                output.Write(buffer, 0, read);
            }

            if (output.Length == 0)
                throw new InvalidDataException("The packaged SMAPI bundle manifest is empty.");
            var manifest = JsonSerializer.Deserialize<BundledAssetManifest>(output.ToArray(), JsonOptions)
                ?? throw new InvalidDataException("The packaged SMAPI bundle manifest is invalid.");
            ValidatePackagedManifest(manifest);
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The packaged SMAPI bundle manifest could not be read.", exception);
        }
    }

    private static void ValidatePackagedManifest(BundledAssetManifest manifest)
    {
        if (manifest.Schema != ManifestSchema || !IsCanonicalSha256(manifest.ContentSha256) ||
            manifest.BundleId != BundleIdPrefix + manifest.ContentSha256[..BundleIdDigestLength] ||
            manifest.Files is null || manifest.Files.Count is < 1 or > MaximumBundleFiles)
        {
            throw new InvalidDataException("The packaged SMAPI bundle manifest header is invalid.");
        }

        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousAssetPath = null;
        foreach (var entry in manifest.Files)
        {
            if (entry is null || !IsSafeRelativePath(entry.AssetPath) || !IsSafeRelativePath(entry.RelativePath) ||
                entry.Size <= 0 || !IsCanonicalSha256(entry.Sha256) ||
                !assetPaths.Add(entry.AssetPath) || !relativePaths.Add(entry.RelativePath) ||
                previousAssetPath is not null && StringComparer.Ordinal.Compare(previousAssetPath, entry.AssetPath) >= 0 ||
                !entry.AssetPath.StartsWith("smapi-managed/", StringComparison.Ordinal) &&
                !entry.AssetPath.StartsWith("smapi-internal/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("A packaged SMAPI bundle entry is invalid.");
            }
            previousAssetPath = entry.AssetPath;
        }

        foreach (var required in new[]
                 {
                     "managed/StardewModdingAPI.dll",
                     "managed/SMAPI.Toolkit.dll",
                     "managed/SMAPI.Toolkit.CoreInterfaces.dll",
                 })
        {
            if (!relativePaths.Contains(required))
                throw new InvalidDataException("The packaged SMAPI managed asset set is incomplete.");
        }
    }

    private static bool TryReadCurrentBundle(
        string bundleRoot,
        BundledAssetManifest expected,
        bool validateFiles,
        out PreparedSmapiBundle bundle)
    {
        bundle = null!;
        try
        {
            var manifestPath = Path.Combine(bundleRoot, ManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length is < 1 or > MaximumManifestBytes)
                return false;
            var manifest = JsonSerializer.Deserialize<BundledAssetManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            if (manifest is null || manifest.Schema != expected.Schema || manifest.BundleId != expected.BundleId ||
                manifest.ContentSha256 != expected.ContentSha256 || manifest.Files is null ||
                manifest.Files.Count != expected.Files.Count)
            {
                return false;
            }

            for (var index = 0; index < expected.Files.Count; index++)
            {
                var entry = manifest.Files[index];
                var expectedEntry = expected.Files[index];
                if (entry is null || entry != expectedEntry)
                    return false;
                if (validateFiles)
                {
                    var info = new FileInfo(ResolveTarget(bundleRoot, entry.RelativePath));
                    if (!info.Exists || info.Length != entry.Size)
                        return false;
                }
            }

            bundle = new PreparedSmapiBundle(
                expected.BundleId,
                Path.GetFullPath(bundleRoot),
                Path.GetFullPath(Path.Combine(bundleRoot, "smapi-internal")),
                manifest.Files.Select(static entry => new PreparedSmapiBundleFile(entry.RelativePath, entry.Size)).ToArray());
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    private static string ResolveTarget(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
            throw new InvalidDataException("A bundled SMAPI asset path is invalid.");

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A bundled SMAPI asset escaped its bundle root.");
        return target;
    }

    private static bool IsSafeRelativePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains('\\') &&
        value.Split('/').All(static segment => segment is not ("" or "." or ".."));

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task WriteManifestAsync(
        string path,
        BundledAssetManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static void CommitDirectory(string staging, string target)
    {
        var replaced = target + $".{Guid.NewGuid():N}.replaced";
        var movedExisting = false;
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Move(target, replaced);
                movedExisting = true;
            }

            Directory.Move(staging, target);
        }
        catch
        {
            if (movedExisting && !Directory.Exists(target) && Directory.Exists(replaced))
                Directory.Move(replaced, target);
            throw;
        }

        TryDeleteDirectory(replaced);
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
            // A stale private staging directory is harmless and can be replaced on the next launch.
        }
    }

    private static void MoveAndDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A bundled SMAPI directory is a reparse point.");
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

    private sealed record BundledAssetEntry(string AssetPath, string RelativePath, long Size, string Sha256);
    private sealed record BundledAssetManifest(
        string Schema,
        string BundleId,
        string ContentSha256,
        IReadOnlyList<BundledAssetEntry> Files);
}
