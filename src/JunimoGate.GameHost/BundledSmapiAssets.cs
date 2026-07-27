using System.Text.Json;
using Android.Content;
using Android.Util;
using JunimoGate.Android;

namespace JunimoGate.GameHost;

internal static class BundledSmapiAssets
{
    private const string ManifestSchema = "junimogate-smapi-bundle/v1";
    private const string ManifestFileName = "bundle-manifest.json";
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GetInternalDirectory(string runtimeRoot) => Path.Combine(
        runtimeRoot,
        "smapi",
        "bundles",
        GameLaunchSchema.BuildId,
        "smapi-internal");

    public static async Task ProvisionAndValidateAsync(
        Context context,
        string internalDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var safe = context.ApplicationContext ?? context;
        var expectedInternal = Path.GetFullPath(GetInternalDirectory(AndroidPrivateStorage.GetRuntimeRoot(safe)));
        if (!Path.GetFullPath(internalDirectory).Equals(expectedInternal, StringComparison.Ordinal))
            throw new InvalidDataException("The SMAPI bundle path does not match this JunimoGate build.");

        var bundleRoot = Path.GetDirectoryName(expectedInternal)
            ?? throw new InvalidDataException("The SMAPI bundle path is invalid.");
        var assets = GetExpectedAssets(safe);
        if (TryValidateCurrentBundle(bundleRoot, assets))
        {
            Log.Info("JunimoGate.LaunchTrace", $"game smapiBundle=cache-hit files={assets.Count}");
            return;
        }

        var parent = Path.GetDirectoryName(bundleRoot)
            ?? throw new InvalidDataException("The SMAPI bundle root is invalid.");
        Directory.CreateDirectory(parent);
        var staging = bundleRoot + $".{Guid.NewGuid():N}.staging";
        try
        {
            Directory.CreateDirectory(staging);
            var entries = new List<BundledAssetEntry>(assets.Count);
            foreach (var asset in assets)
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
                if (output.Length <= 0)
                    throw new InvalidDataException("A bundled SMAPI asset is empty.");
                entries.Add(new BundledAssetEntry(asset.RelativePath, output.Length));
            }

            var manifest = new BundledAssetManifest(ManifestSchema, GameLaunchSchema.BuildId, entries);
            await WriteManifestAsync(Path.Combine(staging, ManifestFileName), manifest, cancellationToken)
                .ConfigureAwait(false);
            CommitDirectory(staging, bundleRoot);
            Log.Info("JunimoGate.LaunchTrace", $"game smapiBundle=deployed files={assets.Count}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        if (!TryValidateCurrentBundle(bundleRoot, assets))
            throw new InvalidDataException("The bundled SMAPI asset deployment did not validate.");
    }

    private static IReadOnlyList<BundledAssetSpec> GetExpectedAssets(Context context)
    {
        var result = new List<BundledAssetSpec>
        {
            new("smapi-internal/config.json", "smapi-internal/config.json"),
            new("smapi-internal/metadata.json", "smapi-internal/metadata.json"),
            new("smapi-internal/blacklist.json", "smapi-internal/blacklist.json"),
            new("smapi-managed/StardewModdingAPI.dll", "managed/StardewModdingAPI.dll"),
            new("smapi-managed/StardewModdingAPI.Toolkit.dll", "managed/StardewModdingAPI.Toolkit.dll"),
            new("smapi-managed/StardewModdingAPI.Toolkit.CoreInterfaces.dll", "managed/StardewModdingAPI.Toolkit.CoreInterfaces.dll"),
        };
        foreach (var name in (context.Assets?.List("smapi-internal/i18n") ?? []).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(name) != name || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A bundled SMAPI translation asset name is invalid.");
            result.Add(new BundledAssetSpec(
                $"smapi-internal/i18n/{name}",
                $"smapi-internal/i18n/{name}"));
        }

        return result;
    }

    private static bool TryValidateCurrentBundle(
        string bundleRoot,
        IReadOnlyList<BundledAssetSpec> expected)
    {
        try
        {
            var manifestPath = Path.Combine(bundleRoot, ManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length is < 1 or > MaximumManifestBytes)
                return false;
            var manifest = JsonSerializer.Deserialize<BundledAssetManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            if (manifest is null || manifest.Schema != ManifestSchema ||
                manifest.BundleId != GameLaunchSchema.BuildId || manifest.Files is null ||
                manifest.Files.Count != expected.Count)
            {
                return false;
            }

            for (var index = 0; index < expected.Count; index++)
            {
                var entry = manifest.Files[index];
                if (entry.RelativePath != expected[index].RelativePath || entry.Size <= 0)
                    return false;
                var info = new FileInfo(ResolveTarget(bundleRoot, entry.RelativePath));
                if (!info.Exists || info.Length != entry.Size)
                    return false;
            }

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
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\') || relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("A bundled SMAPI asset path is invalid.");
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A bundled SMAPI asset escaped its bundle root.");
        return target;
    }

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

    private sealed record BundledAssetSpec(string AssetPath, string RelativePath);
    private sealed record BundledAssetEntry(string RelativePath, long Size);
    private sealed record BundledAssetManifest(
        string Schema,
        string BundleId,
        IReadOnlyList<BundledAssetEntry> Files);
}
