using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JunimoGate.Core;

public sealed record DiagnosticTextSource(string EntryName, string Path, int MaximumBytes)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EntryName) || EntryName.Length > 96 ||
            EntryName.Any(static character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')) ||
            !EntryName.EndsWith(".txt", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(Path) || !System.IO.Path.IsPathFullyQualified(Path) ||
            MaximumBytes is < 1 or > DiagnosticBundleBuilder.MaximumSourceBytes)
        {
            throw new InvalidDataException("The diagnostic text source is malformed.");
        }
    }
}

public sealed record DiagnosticSourcePreview(string EntryName, long AvailableBytes, int IncludedBytes);

public sealed record DiagnosticBundlePreview(
    IReadOnlyList<DiagnosticSourcePreview> Sources,
    long TotalAvailableBytes,
    int TotalIncludedBytes);

public static partial class DiagnosticTextRedactor
{
    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = UnixPrivatePath().Replace(value, "<private-path>");
        value = WindowsPrivatePath().Replace(value, "<private-path>");
        value = NamedSecret().Replace(value, static match =>
            $"{match.Groups[1].Value}{match.Groups[2].Value}<redacted>");
        return LongHexIdentity().Replace(value, "<redacted-id>");
    }

    [GeneratedRegex("(?<![A-Za-z0-9])/(?:data|storage|sdcard|mnt|home|tmp)/[^\\s\\\"'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPrivatePath();

    [GeneratedRegex("(?<![A-Za-z0-9])[A-Za-z]:\\\\[^\\s\\\"'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPrivatePath();

    [GeneratedRegex("(?i)\\b(capability(?:key)?|launch(?:key|token)|descriptor(?:token)?|token)(\\\"?\\s*[:=]\\s*\\\"?)[A-Za-z0-9._-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32,}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    private static partial Regex LongHexIdentity();
}

public static class DiagnosticBundleBuilder
{
    public const int MaximumSourceBytes = 1024 * 1024;
    public const int MaximumSources = 16;
    private const int MaximumMetadataFields = 64;
    private const int MaximumMetadataValueCharacters = 4096;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static DiagnosticBundlePreview Preview(IReadOnlyList<DiagnosticTextSource> sources)
    {
        ValidateSources(sources);
        var previews = new List<DiagnosticSourcePreview>(sources.Count);
        long available = 0;
        var included = 0;
        foreach (var source in sources)
        {
            var length = TryGetLength(source.Path);
            available = checked(available + length);
            var sourceIncluded = (int)Math.Min(length, source.MaximumBytes);
            included = checked(included + sourceIncluded);
            previews.Add(new DiagnosticSourcePreview(source.EntryName, length, sourceIncluded));
        }
        return new DiagnosticBundlePreview(previews, available, included);
    }

    public static async ValueTask<string> ReadTailTextAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The diagnostic source path must be absolute.", nameof(path));
        if (maximumBytes is < 1 or > MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!File.Exists(path))
            return string.Empty;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var count = (int)Math.Min(stream.Length, maximumBytes);
        var offset = stream.Length - count;
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var text = Utf8WithoutBom.GetString(bytes);
        if (offset > 0)
        {
            var newline = text.IndexOf('\n');
            text = newline < 0 ? string.Empty : text[(newline + 1)..];
        }
        return text;
    }

    public static async ValueTask CreateAsync(
        Stream destination,
        IReadOnlyDictionary<string, string?> metadata,
        IReadOnlyList<DiagnosticTextSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!destination.CanWrite)
            throw new ArgumentException("The diagnostic destination must be writable.", nameof(destination));
        ValidateMetadata(metadata);
        ValidateSources(sources);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true, Utf8WithoutBom);
        var manifest = new DiagnosticManifest(
            "junimogate-diagnostic-bundle/v1",
            DateTimeOffset.UtcNow,
            metadata.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value is null ? null : DiagnosticTextRedactor.Redact(pair.Value),
                StringComparer.Ordinal),
            sources.Select(static source => source.EntryName).ToArray());
        await WriteTextEntryAsync(
            archive,
            "diagnostics.json",
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken).ConfigureAwait(false);

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source.Path))
                continue;
            var text = await ReadTailTextAsync(source.Path, source.MaximumBytes, cancellationToken)
                .ConfigureAwait(false);
            await WriteTextEntryAsync(
                archive,
                $"logs/{source.EntryName}",
                DiagnosticTextRedactor.Redact(text),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteTextEntryAsync(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await using var writer = new StreamWriter(output, Utf8WithoutBom, 16 * 1024, leaveOpen: true);
        await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSources(IReadOnlyList<DiagnosticTextSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count > MaximumSources)
            throw new InvalidDataException("The diagnostic bundle contains too many sources.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            source?.Validate();
            if (source is null || !names.Add(source.EntryName))
                throw new InvalidDataException("The diagnostic bundle contains a duplicate or null source.");
        }
    }

    private static void ValidateMetadata(IReadOnlyDictionary<string, string?> metadata)
    {
        if (metadata.Count > MaximumMetadataFields)
            throw new InvalidDataException("The diagnostic bundle contains too much metadata.");
        foreach (var pair in metadata)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64 ||
                pair.Key.Any(static character =>
                    character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')) ||
                pair.Value is { Length: > MaximumMetadataValueCharacters })
            {
                throw new InvalidDataException("The diagnostic bundle metadata is malformed.");
            }
        }
    }

    private static long TryGetLength(string path)
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

    private sealed record DiagnosticManifest(
        string Schema,
        DateTimeOffset GeneratedAtUtc,
        IReadOnlyDictionary<string, string?> Metadata,
        IReadOnlyList<string> Sources);
}
