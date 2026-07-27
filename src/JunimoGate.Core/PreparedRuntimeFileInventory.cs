using System.Collections.ObjectModel;

namespace JunimoGate.Core;

internal sealed record PreparedRuntimeFileSpec(string Key, string RelativePath, long Size);

internal static class PreparedRuntimeFileInventoryBuilder
{
    public static IReadOnlyDictionary<string, string> BuildAndValidate(
        string root,
        IEnumerable<PreparedRuntimeFileSpec> files,
        StringComparer keyComparer,
        string description,
        string? requiredPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(keyComparer);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var resolved = new Dictionary<string, string>(keyComparer);
        var resolvedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Key) || file.Size < 0 ||
                !IsCanonicalRelativePath(file.RelativePath) ||
                requiredPrefix is not null && !file.RelativePath.StartsWith(requiredPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"A prepared {description} entry is invalid.");
            }

            var path = Path.GetFullPath(Path.Combine(
                canonicalRoot,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"A prepared {description} path escaped its workspace.");
            if (!resolved.TryAdd(file.Key, path) || !resolvedPaths.Add(path))
                throw new InvalidDataException($"The prepared {description} inventory contains a duplicate entry.");

            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException($"A prepared {description} file is missing.", path);
            if (info.Length != file.Size)
                throw new InvalidDataException($"A prepared {description} file size changed.");
        }

        if (resolved.Count == 0)
            throw new InvalidDataException($"The prepared {description} inventory is empty.");
        return new ReadOnlyDictionary<string, string>(resolved);
    }

    private static bool IsCanonicalRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Contains('\\') || path.StartsWith("/", StringComparison.Ordinal) ||
            path.EndsWith("/", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not "..");
    }
}
