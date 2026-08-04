using System.Text.Json;
using Android.Content;
using Android.Text.Format;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.GameHost;
using JunimoGate.Mods;

namespace JunimoGate.App;

internal enum ProductLogKind
{
    Launcher,
    GameHost,
    Smapi,
}

internal enum ProductLogGeneration
{
    Current,
    Previous,
}

internal sealed record ProductLogSource(
    ProductLogKind Kind,
    ProductLogGeneration Generation,
    string EntryName,
    string Path);

internal sealed record ProductLogDocument(
    string Text,
    long AvailableBytes,
    int DisplayedBytes,
    bool IsTruncated,
    int WarningCount,
    int ErrorCount);

internal sealed class ProductLogService
{
    public const int MaximumDisplayBytes = 256 * 1024;
    private const int MaximumDiagnosticBytes = 1024 * 1024;
    private readonly Context context;

    public ProductLogService(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
    }

    public ProductLogSource GetSource(ProductLogKind kind, ProductLogGeneration generation)
    {
        var productLogs = AndroidPrivateStorage.GetProductLogsRoot(context);
        var gameLogs = Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context), "logs");
        return (kind, generation) switch
        {
            (ProductLogKind.Launcher, ProductLogGeneration.Current) =>
                new(kind, generation, "launcher-current.txt", Path.Combine(productLogs, "launcher-current.jsonl")),
            (ProductLogKind.Launcher, ProductLogGeneration.Previous) =>
                new(kind, generation, "launcher-previous.txt", Path.Combine(productLogs, "launcher-previous.jsonl")),
            (ProductLogKind.GameHost, ProductLogGeneration.Current) =>
                new(kind, generation, "game-current.txt", Path.Combine(productLogs, "game-current.jsonl")),
            (ProductLogKind.GameHost, ProductLogGeneration.Previous) =>
                new(kind, generation, "game-previous.txt", Path.Combine(productLogs, "game-previous.jsonl")),
            (ProductLogKind.Smapi, ProductLogGeneration.Current) =>
                new(kind, generation, "smapi-current.txt", Path.Combine(gameLogs, "SMAPI-latest.txt")),
            (ProductLogKind.Smapi, ProductLogGeneration.Previous) =>
                new(kind, generation, "smapi-previous.txt", Path.Combine(productLogs, "smapi-previous.txt")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    public async ValueTask<ProductLogDocument> ReadAsync(
        ProductLogKind kind,
        ProductLogGeneration generation,
        CancellationToken cancellationToken)
    {
        var source = GetSource(kind, generation);
        var available = GetLength(source.Path);
        var text = await DiagnosticBundleBuilder.ReadTailTextAsync(
            source.Path,
            MaximumDisplayBytes,
            cancellationToken).ConfigureAwait(false);
        var (warnings, errors) = CountSeverity(text, kind != ProductLogKind.Smapi);
        return new ProductLogDocument(
            text,
            available,
            System.Text.Encoding.UTF8.GetByteCount(text),
            available > MaximumDisplayBytes,
            warnings,
            errors);
    }

    public DiagnosticBundlePreview PreviewDiagnosticBundle() =>
        DiagnosticBundleBuilder.Preview(GetDiagnosticSources());

    public async ValueTask ExportDiagnosticBundleAsync(
        Stream destination,
        CancellationToken cancellationToken)
    {
        var runtime = GameHostRuntimeInformationReader.Read(context);
        var prepared = await GameLaunchRegistry.TryOpenActiveAsync(context, cancellationToken).ConfigureAwait(false);
        var profilesRoot = Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context), "profiles");
        var active = await new ActiveModProfileSelectionRepository(profilesRoot)
            .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken).ConfigureAwait(false);
        ModProfileV2? profile = null;
        var enabledModCount = 0;
        try
        {
            profile = await new ModProfileV2Repository(profilesRoot)
                .ReadAsync(active.Validate(), cancellationToken).ConfigureAwait(false);
            var library = await new ModLibraryRepository(Path.Combine(
                    AndroidPrivateStorage.GetUserDataRoot(context),
                    "mods"))
                .ReadAsync(cancellationToken).ConfigureAwait(false);
            var available = library.Items.Select(static item => item.LibraryItemId).ToHashSet(StringComparer.Ordinal);
            enabledModCount = profile.Members.Count(member =>
                member.Enabled && member.LibraryItemId is not null && available.Contains(member.LibraryItemId));
        }
        catch (InvalidDataException)
        {
            // Legacy or malformed Profile metadata is represented without reading Mod contents.
        }

        var package = context.PackageManager?.GetPackageInfo(
            context.PackageName!,
            (global::Android.Content.PM.PackageInfoFlags)0);
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["junimoGateVersion"] = package?.VersionName,
            ["junimoGateVersionCode"] = package is null ? null : GetLongVersionCode(package).ToString(),
            ["androidSdk"] = ((int)global::Android.OS.Build.VERSION.SdkInt).ToString(),
            ["deviceManufacturer"] = global::Android.OS.Build.Manufacturer,
            ["deviceModel"] = global::Android.OS.Build.Model,
            ["gameVersion"] = prepared?.VersionName,
            ["gameVersionCode"] = prepared?.VersionCode.ToString(),
            ["smapiApiVersion"] = runtime.SmapiApiVersion,
            ["smapiImplementationVersion"] = runtime.SmapiImplementationVersion,
            ["smapiBuildCode"] = runtime.BuildId,
            ["smapiBundleId"] = runtime.BundleId,
            ["activeProfileId"] = active.ActiveProfileId,
            ["activeProfileRevision"] = profile?.Revision.ToString(),
            ["enabledModCount"] = profile is null ? null : enabledModCount.ToString(),
        };
        await DiagnosticBundleBuilder.CreateAsync(
            destination,
            metadata,
            GetDiagnosticSources(),
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<DiagnosticTextSource> GetDiagnosticSources() =>
        Enum.GetValues<ProductLogKind>()
            .SelectMany(kind => Enum.GetValues<ProductLogGeneration>()
                .Select(generation => GetSource(kind, generation)))
            .Select(static source => new DiagnosticTextSource(
                source.EntryName,
                source.Path,
                MaximumDiagnosticBytes))
            .ToArray();

    private static (int Warnings, int Errors) CountSeverity(string text, bool jsonLines)
    {
        var warnings = 0;
        var errors = 0;
        foreach (var line in text.Split('\n'))
        {
            if (jsonLines)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (!json.RootElement.TryGetProperty("level", out var level))
                        continue;
                    if (level.ValueEquals("error"))
                        errors++;
                    else if (level.ValueEquals("warn"))
                        warnings++;
                }
                catch (JsonException)
                {
                    // A bounded tail may start after a partial JSON line; raw display remains authoritative.
                }
            }
            else if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                errors++;
            else if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
                warnings++;
        }
        return (warnings, errors);
    }

    private static long GetLength(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long GetLongVersionCode(global::Android.Content.PM.PackageInfo package)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            return package.LongVersionCode;
#pragma warning disable CA1422
        return package.VersionCode;
#pragma warning restore CA1422
    }
}
