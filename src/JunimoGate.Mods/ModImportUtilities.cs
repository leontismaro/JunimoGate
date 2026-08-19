using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JunimoGate.Mods;

internal static class ModImportUtilities
{
    public static ModManifestSummary ParseManifest(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Span.StartsWith(Encoding.UTF8.Preamble))
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The Mod manifest must be a JSON object.");
            var contentPackFor = TryGetProperty(root, "ContentPackFor", out var contentPack) &&
                                 contentPack.ValueKind != JsonValueKind.Null
                ? ReadContentPackFor(contentPack)
                : null;
            var dependencies = new List<ModDependencySummary>();
            if (TryGetProperty(root, "Dependencies", out var dependencyArray) &&
                dependencyArray.ValueKind != JsonValueKind.Null)
            {
                if (dependencyArray.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Dependencies must be an array.");
                foreach (var dependency in dependencyArray.EnumerateArray())
                {
                    if (dependency.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Each dependency must be an object.");
                    dependencies.Add(new ModDependencySummary(
                        ReadRequiredString(dependency, "UniqueID"),
                        ReadOptionalBoolean(dependency, "IsRequired") ?? true,
                        ReadOptionalString(dependency, "MinimumVersion")));
                }
            }

            var updateKeys = new List<string>();
            if (TryGetProperty(root, "UpdateKeys", out var updateKeyArray) &&
                updateKeyArray.ValueKind != JsonValueKind.Null)
            {
                if (updateKeyArray.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("UpdateKeys must be an array.");
                foreach (var updateKey in updateKeyArray.EnumerateArray())
                {
                    if (updateKey.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException("Each UpdateKeys entry must be a string.");
                    if (string.IsNullOrWhiteSpace(updateKey.GetString()))
                        continue;
                    var value = updateKey.GetString()!.Trim();
                    if (value.Length > 4096)
                        throw new InvalidDataException("A Mod UpdateKeys entry is too long.");
                    updateKeys.Add(value);
                }
            }

            return new ModManifestSummary(
                ReadRequiredString(root, "Name"),
                ReadRequiredString(root, "Author"),
                ReadRequiredString(root, "Version"),
                ReadRequiredString(root, "UniqueID"),
                ReadOptionalString(root, "Description"),
                ReadOptionalString(root, "EntryDll"),
                contentPackFor,
                dependencies)
            {
                UpdateKeys = updateKeys,
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Mod manifest JSON is malformed.", exception);
        }
    }

    public static void AppendPathHeader(IncrementalHash hash, string path, long length)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], pathBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(header[4..], length);
        hash.AppendData(header);
        hash.AppendData(pathBytes);
    }

    public static async ValueTask<string> ComputeDirectoryContentDigestAsync(
        string filesRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filesRoot) || !Path.IsPathFullyQualified(filesRoot))
            throw new ArgumentException("The Mod files root must be absolute.", nameof(filesRoot));
        if (!Directory.Exists(filesRoot))
            throw new DirectoryNotFoundException("The Mod files root is missing.");

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesRoot));
        var files = Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(normalizedRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return (Path: path, Relative: SafeArchivePath.Parse(relative).Value);
            })
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToArray();
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.Path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A Mod content file is unavailable or unsupported.");
            AppendPathHeader(contentHash, file.Relative, info.Length);
            await using var input = new FileStream(
                file.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long readBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                contentHash.AppendData(buffer, 0, read);
                readBytes = checked(readBytes + read);
            }
            if (readBytes != info.Length)
                throw new InvalidDataException("A Mod content file changed while its digest was calculated.");
        }
        return Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool IsContained(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ReadContentPackFor(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("ContentPackFor must be an object.");
        return ReadRequiredString(value, "UniqueID");
    }

    private static string ReadRequiredString(JsonElement element, string name) =>
        ReadOptionalString(element, name) is { } value
            ? value
            : throw new InvalidDataException($"The Mod manifest is missing {name}.");

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"The Mod manifest field {name} must be a non-empty string.");
        var result = value.GetString()!.Trim();
        if (result.Length > 4096)
            throw new InvalidDataException($"The Mod manifest field {name} is too long.");
        return result;
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"The Mod manifest field {name} must be a boolean.");
        return value.GetBoolean();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
