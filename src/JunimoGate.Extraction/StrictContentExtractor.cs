using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace JunimoGate.Extraction;

public sealed record ContentApkSource(string SourceLabel, ZipArchive Archive);

/// <summary>Strictly validates all Content ZIP entries before streaming any payload bytes to disk.</summary>
public sealed class StrictContentExtractor
{
    private const string ContentPrefix = "assets/Content/";
    private static readonly char[] InvalidPortableCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    public async ValueTask<IReadOnlyList<WorkspaceExtractedFileManifest>> ExtractAsync(
        IEnumerable<ContentApkSource> sources,
        string stagingDirectory,
        WorkspaceExtractionLimits limits,
        IProgress<WorkspaceProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        progress?.Report(new WorkspaceProgressEvent(WorkspaceProgressStage.ScanningContent, "Scanning Content entries."));
        var plan = Prescan(sources, limits, cancellationToken);
        progress?.Report(new WorkspaceProgressEvent(WorkspaceProgressStage.ExtractingContent, "Extracting Content entries.", 0, plan.Count));

        var outputs = new List<WorkspaceExtractedFileManifest>(plan.Count);
        for (var index = 0; index < plan.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = plan[index];
            var destinationPath = Path.Combine(stagingDirectory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            long written = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = item.Entry.Open())
            await using (var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    written = checked(written + count);
                    if (written > item.Entry.Length || written > limits.MaximumContentFileBytes)
                    {
                        throw new WorkspacePreparationException(
                            WorkspaceErrorCodes.ContentLimitsExceeded,
                            "A Content entry exceeded its declared or configured size while streaming.");
                    }

                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (written != item.Entry.Length)
            {
                throw new WorkspacePreparationException(
                    WorkspaceErrorCodes.ManifestInvalid,
                    "A Content entry did not produce its declared uncompressed size.");
            }

            outputs.Add(new WorkspaceExtractedFileManifest(
                "content",
                item.RelativePath,
                written,
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                item.SourceLabel,
                item.LogicalSourceEntry));
            progress?.Report(new WorkspaceProgressEvent(
                WorkspaceProgressStage.ExtractingContent,
                "Extracting Content entries.",
                index + 1,
                plan.Count));
        }

        return outputs;
    }

    private static List<PlannedContentEntry> Prescan(
        IEnumerable<ContentApkSource> sources,
        WorkspaceExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        var sourceArray = sources.ToArray();
        if (sourceArray.Any(static source => source is null || string.IsNullOrWhiteSpace(source.SourceLabel) || source.Archive is null))
        {
            throw new ArgumentException("Content APK sources must have a logical label and archive.", nameof(sources));
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<PlannedContentEntry>();
        long totalBytes = 0;

        foreach (var source in sourceArray)
        {
            foreach (var entry in source.Archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.FullName.StartsWith(ContentPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                ValidateUnixMode(entry);
                var suffix = entry.FullName[ContentPrefix.Length..];
                var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                var relativeSuffix = ValidateRelativePath(suffix, isDirectory, limits);
                if (relativeSuffix is null)
                {
                    continue;
                }

                var relativePath = $"Content/{relativeSuffix}";
                var collisionKey = relativePath.Normalize(NormalizationForm.FormC);
                if (isDirectory)
                {
                    RegisterDirectory(collisionKey, files, directories);
                    continue;
                }

                if (!files.Add(collisionKey) || directories.Contains(collisionKey))
                {
                    throw new WorkspacePreparationException(
                        WorkspaceErrorCodes.DuplicateOutput,
                        "Content entries produce a duplicate output or file-directory collision.");
                }

                RegisterAncestors(collisionKey, files, directories);
                if (entry.Length < 0 || entry.Length > limits.MaximumContentFileBytes)
                {
                    throw new WorkspacePreparationException(
                        WorkspaceErrorCodes.ContentLimitsExceeded,
                        "A Content entry exceeds the per-file uncompressed size limit.");
                }

                totalBytes = checked(totalBytes + entry.Length);
                if (plan.Count + 1 > limits.MaximumContentEntries || totalBytes > limits.MaximumTotalContentBytes)
                {
                    throw new WorkspacePreparationException(
                        WorkspaceErrorCodes.ContentLimitsExceeded,
                        "Content entry count or total uncompressed size exceeds configured limits.");
                }

                if (entry.Length >= limits.CompressionRatioMinimumFileBytes)
                {
                    if (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio)
                    {
                        throw new WorkspacePreparationException(
                            WorkspaceErrorCodes.ContentLimitsExceeded,
                            "A Content entry exceeds the configured compression ratio limit.");
                    }
                }

                plan.Add(new PlannedContentEntry(
                    source.SourceLabel,
                    entry,
                    relativePath,
                    $"{ContentPrefix}{relativeSuffix}"));
            }
        }

        return plan
            .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(static item => item.SourceLabel, StringComparer.Ordinal)
            .ToList();
    }

    private static string? ValidateRelativePath(
        string suffix,
        bool isDirectory,
        WorkspaceExtractionLimits limits)
    {
        if (suffix.Length == 0)
        {
            return isDirectory ? null : throw Unsafe();
        }

        if (suffix.IndexOf('\\') >= 0 || suffix.IndexOf('\0') >= 0 || suffix.Any(char.IsControl))
        {
            throw Unsafe();
        }

        if (suffix.StartsWith("/", StringComparison.Ordinal) || Path.IsPathFullyQualified(suffix))
        {
            throw Unsafe();
        }

        var trimmed = isDirectory ? suffix[..^1] : suffix;
        if (trimmed.Length == 0)
        {
            return null;
        }

        var segments = trimmed.Split('/', StringSplitOptions.None);
        if (segments.Length > limits.MaximumPathDepth || segments.Any(static segment => segment.Length == 0))
        {
            throw Unsafe();
        }

        var normalizedSegments = new string[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment is "." or ".." ||
                segment.Length > limits.MaximumPathSegmentLength ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.IndexOfAny(InvalidPortableCharacters) >= 0 ||
                IsReservedWindowsName(segment))
            {
                throw Unsafe();
            }

            normalizedSegments[index] = segment.Normalize(NormalizationForm.FormC);
        }

        var normalized = string.Join('/', normalizedSegments);
        if (normalized.Length > limits.MaximumRelativePathLength)
        {
            throw Unsafe();
        }

        return normalized;
    }

    private static void ValidateUnixMode(ZipArchiveEntry entry)
    {
        var mode = ((uint)entry.ExternalAttributes >> 16) & 0xF000;
        if (mode != 0 && mode != 0x8000 && mode != 0x4000)
        {
            throw new WorkspacePreparationException(
                WorkspaceErrorCodes.UnsafeContentEntry,
                "Symlink and special Content entries are not allowed.");
        }

        if (mode == 0x4000 && !entry.FullName.EndsWith("/", StringComparison.Ordinal))
        {
            throw new WorkspacePreparationException(
                WorkspaceErrorCodes.UnsafeContentEntry,
                "A Content directory entry has an unsafe archive shape.");
        }
    }

    private static void RegisterDirectory(
        string directory,
        HashSet<string> files,
        HashSet<string> directories)
    {
        if (files.Contains(directory))
        {
            throw new WorkspacePreparationException(
                WorkspaceErrorCodes.DuplicateOutput,
                "Content entries produce a file-directory collision.");
        }

        directories.Add(directory);
        RegisterAncestors(directory, files, directories);
    }

    private static void RegisterAncestors(
        string path,
        HashSet<string> files,
        HashSet<string> directories)
    {
        var slash = path.IndexOf('/');
        while (slash >= 0)
        {
            var ancestor = path[..slash];
            if (files.Contains(ancestor))
            {
                throw new WorkspacePreparationException(
                    WorkspaceErrorCodes.DuplicateOutput,
                    "Content entries produce a file-directory collision.");
            }

            directories.Add(ancestor);
            slash = path.IndexOf('/', slash + 1);
        }
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot >= 0 ? segment[..dot] : segment;
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9';
    }

    private static WorkspacePreparationException Unsafe() =>
        new(WorkspaceErrorCodes.UnsafeContentEntry, "An unsafe Content entry path was rejected.");

    private sealed record PlannedContentEntry(
        string SourceLabel,
        ZipArchiveEntry Entry,
        string RelativePath,
        string LogicalSourceEntry);
}
