using System.IO.Compression;
using System.Xml;

namespace JunimoGate.Core;

public enum SaveGameEntryStatus
{
    Ready,
    Incomplete,
    MetadataUnreadable,
}

public sealed record SaveGameMetadata(
    string? PlayerName,
    string? FarmName,
    string? GameVersion,
    int? Day,
    int? Season,
    int? Year,
    TimeSpan? PlayTime);

public sealed record LiveSaveGameEntry(
    string DirectoryName,
    SaveGameMetadata Metadata,
    DateTimeOffset LastWriteTimeUtc,
    long Size,
    SaveGameEntryStatus Status);

public sealed record SaveArchiveCandidate(
    string CandidateId,
    string EntryPrefix,
    string DirectoryName,
    SaveGameMetadata Metadata,
    long ExpandedSize,
    int FileCount,
    SaveGameEntryStatus Status)
{
    public bool CanImport => Status != SaveGameEntryStatus.Incomplete;
}

public sealed record SaveArchiveInspection(
    IReadOnlyList<SaveArchiveCandidate> Candidates,
    long ExpandedSize,
    int FileCount);

public sealed record SaveTransferProgress(long ProcessedBytes, long TotalBytes);

public enum SaveImportConflictResolution
{
    Skip,
    Replace,
}

public sealed record SaveImportSelection(string CandidateId, SaveImportConflictResolution ConflictResolution);

public sealed record SaveImportResult(
    IReadOnlyList<string> ImportedDirectoryNames,
    string? SafetyBackupName);

public static class LiveSaveGameCatalog
{
    public static IReadOnlyList<LiveSaveGameEntry> Read(string savesRoot)
    {
        var root = ValidateRoot(savesRoot);
        if (!Directory.Exists(root))
            return [];
        var entries = new List<LiveSaveGameEntry>();
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var directory = new DirectoryInfo(path);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.Name.StartsWith(".", StringComparison.Ordinal))
                continue;
            var infoPath = Path.Combine(path, "SaveGameInfo");
            var primaryPath = Path.Combine(path, directory.Name);
            var status = File.Exists(infoPath) && File.Exists(primaryPath)
                ? SaveGameEntryStatus.Ready
                : SaveGameEntryStatus.Incomplete;
            var metadata = SaveGameMetadataReader.Empty;
            if (File.Exists(infoPath))
            {
                try
                {
                    using var stream = new FileStream(infoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    metadata = SaveGameMetadataReader.Read(stream);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
                {
                    if (status == SaveGameEntryStatus.Ready)
                        status = SaveGameEntryStatus.MetadataUnreadable;
                }
            }
            entries.Add(new LiveSaveGameEntry(
                directory.Name,
                metadata,
                GetLatestWriteTime(directory),
                GetDirectorySize(directory),
                status));
        }
        return entries
            .OrderByDescending(static entry => entry.LastWriteTimeUtc)
            .ThenBy(static entry => entry.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTimeOffset GetLatestWriteTime(DirectoryInfo directory)
    {
        var latest = directory.LastWriteTimeUtc;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && file.LastWriteTimeUtc > latest)
                    latest = file.LastWriteTimeUtc;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return new DateTimeOffset(latest, TimeSpan.Zero);
    }

    private static long GetDirectorySize(DirectoryInfo directory)
    {
        long total = 0;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                    total = checked(total + file.Length);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return 0;
        }
        return total;
    }

    internal static string ValidateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified save root is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

public static class SaveGameMetadataReader
{
    internal static readonly SaveGameMetadata Empty = new(null, null, null, null, null, null, null);

    public static SaveGameMetadata Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The save metadata source must be readable.", nameof(source));
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };
        string? player = null;
        string? farm = null;
        string? version = null;
        int? day = null;
        int? season = null;
        int? year = null;
        long? milliseconds = null;
        using var reader = XmlReader.Create(source, settings);
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
            {
                reader.Read();
                continue;
            }
            var name = reader.LocalName;
            if (name is not ("name" or "farmName" or "gameVersion" or "dayOfMonthForSaveGame" or
                "seasonForSaveGame" or "yearForSaveGame" or "millisecondsPlayed"))
            {
                reader.Read();
                continue;
            }
            var value = reader.ReadElementContentAsString();
            switch (name)
            {
                case "name": player ??= Normalize(value); break;
                case "farmName": farm = Normalize(value); break;
                case "gameVersion": version = Normalize(value); break;
                case "dayOfMonthForSaveGame": day = ParseInt(value); break;
                case "seasonForSaveGame": season = ParseInt(value); break;
                case "yearForSaveGame": year = ParseInt(value); break;
                case "millisecondsPlayed":
                    if (long.TryParse(value, out var parsed) && parsed >= 0)
                        milliseconds = parsed;
                    break;
            }
        }
        return new SaveGameMetadata(
            player,
            farm,
            version,
            day,
            season,
            year,
            milliseconds is null ? null : TimeSpan.FromMilliseconds(milliseconds.Value));
    }

    private static string? Normalize(string value)
    {
        var result = value.Trim();
        return result.Length == 0 ? null : result;
    }

    private static int? ParseInt(string value) => int.TryParse(value, out var result) ? result : null;
}

public static class SaveArchiveInspector
{
    public const int MaximumEntries = 100_000;
    public const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    public const long MaximumSingleFileBytes = 2L * 1024 * 1024 * 1024;

    public static SaveArchiveInspection InspectZip(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !Path.IsPathFullyQualified(archivePath))
            throw new ArgumentException("A fully qualified save archive path is required.", nameof(archivePath));
        using var archive = ZipFile.OpenRead(Path.GetFullPath(archivePath));
        return Inspect(archive);
    }

    public static SaveArchiveInspection InspectDirectory(string directoryPath)
    {
        var root = LiveSaveGameCatalog.ValidateRoot(directoryPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The save backup directory is missing.");
        var candidates = new List<SaveArchiveCandidate>();
        long total = 0;
        var files = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var saveInfo = Path.Combine(directory, "SaveGameInfo");
            var primary = Path.Combine(directory, info.Name);
            var status = File.Exists(saveInfo) && File.Exists(primary)
                ? SaveGameEntryStatus.Ready
                : SaveGameEntryStatus.Incomplete;
            var metadata = SaveGameMetadataReader.Empty;
            if (File.Exists(saveInfo))
            {
                try
                {
                    using var stream = File.OpenRead(saveInfo);
                    metadata = SaveGameMetadataReader.Read(stream);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
                {
                    if (status == SaveGameEntryStatus.Ready)
                        status = SaveGameEntryStatus.MetadataUnreadable;
                }
            }
            long size = 0;
            var count = 0;
            foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                size = checked(size + file.Length);
                count++;
            }
            total = checked(total + size);
            files = checked(files + count);
            candidates.Add(new SaveArchiveCandidate(
                info.Name,
                info.Name + "/",
                info.Name,
                metadata,
                size,
                count,
                status));
        }
        return new SaveArchiveInspection(candidates, total, files);
    }

    internal static SaveArchiveInspection Inspect(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("The save archive contains too many entries.");
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var path = NormalizeEntryPath(entry.FullName);
            if (path.Length == 0 || IsDirectory(entry))
                continue;
            if (IsSpecialEntry(entry) || entry.Length < 0 || entry.Length > MaximumSingleFileBytes)
                throw new InvalidDataException("The save archive contains an unsupported entry.");
            total = checked(total + entry.Length);
            if (total > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded save archive is too large.");
            if (!files.TryAdd(path, entry))
                throw new InvalidDataException("The save archive contains duplicate paths.");
        }
        var candidates = new List<SaveArchiveCandidate>();
        foreach (var pair in files.Where(static pair => GetLeaf(pair.Key).Equals("SaveGameInfo", StringComparison.OrdinalIgnoreCase)))
        {
            var prefix = GetParent(pair.Key);
            var directoryName = prefix.Length == 0
                ? InferRootDirectoryName(files.Keys)
                : GetLeaf(prefix.TrimEnd('/'));
            var primaryPath = prefix + directoryName;
            var candidateFiles = files
                .Where(item => prefix.Length == 0 || item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var status = files.ContainsKey(primaryPath)
                ? SaveGameEntryStatus.Ready
                : SaveGameEntryStatus.Incomplete;
            var metadata = SaveGameMetadataReader.Empty;
            try
            {
                using var stream = pair.Value.Open();
                metadata = SaveGameMetadataReader.Read(stream);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or XmlException)
            {
                if (status == SaveGameEntryStatus.Ready)
                    status = SaveGameEntryStatus.MetadataUnreadable;
            }
            var candidateId = prefix.Length == 0 ? directoryName : prefix.TrimEnd('/');
            if (candidates.Any(candidate => candidate.CandidateId.Equals(candidateId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The save archive contains ambiguous save roots.");
            candidates.Add(new SaveArchiveCandidate(
                candidateId,
                prefix,
                directoryName,
                metadata,
                candidateFiles.Sum(static item => item.Value.Length),
                candidateFiles.Length,
                status));
        }
        return new SaveArchiveInspection(
            candidates.OrderBy(static candidate => candidate.DirectoryName, StringComparer.OrdinalIgnoreCase).ToArray(),
            total,
            files.Count);
    }

    internal static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            throw new InvalidDataException("The save archive contains an invalid path.");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(path) || parts.Any(static part => part is "." or ".." || part.Contains(':')))
            throw new InvalidDataException("The save archive contains a path traversal entry.");
        return string.Join('/', parts) + (path.EndsWith('/') ? "/" : string.Empty);
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name);

    private static bool IsSpecialEntry(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode != 0 && unixMode != 0x8000 && unixMode != 0x4000;
    }

    private static string InferRootDirectoryName(IEnumerable<string> paths)
    {
        var names = paths
            .Where(static path => !path.Contains('/'))
            .Select(GetLeaf)
            .Where(static name => !name.Equals("SaveGameInfo", StringComparison.OrdinalIgnoreCase) &&
                                  !name.EndsWith("_old", StringComparison.OrdinalIgnoreCase) &&
                                  !name.EndsWith("_SVBAK", StringComparison.OrdinalIgnoreCase) &&
                                  !name.EndsWith("_SVEMERG", StringComparison.OrdinalIgnoreCase) &&
                                  !name.Contains('.', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 1
            ? names[0]
            : throw new InvalidDataException("A root-level save archive has no unambiguous primary save file.");
    }

    private static string GetParent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..(index + 1)];
    }

    private static string GetLeaf(string path)
    {
        var normalized = path.TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }
}

public static class SaveArchiveWriter
{
    public static Task ExportSaveAsync(
        string savesRoot,
        string directoryName,
        Stream destination,
        IProgress<SaveTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        WriteDirectoriesAsync(
            LiveSaveGameCatalog.ValidateRoot(savesRoot),
            [directoryName],
            destination,
            progress,
            cancellationToken);

    internal static async Task WriteDirectoriesAsync(
        string sourceRoot,
        IReadOnlyList<string> directoryNames,
        Stream destination,
        IProgress<SaveTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The save archive destination must be writable.", nameof(destination));
        var files = new List<(string Path, string Entry, long Size)>();
        foreach (var name in directoryNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateDirectoryName(name);
            var directory = Path.GetFullPath(Path.Combine(sourceRoot, name));
            if (!directory.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("A selected save directory is missing.");
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A save directory contains an unsupported link.");
                var relative = Path.GetRelativePath(sourceRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                files.Add((path, relative, file.Length));
            }
        }
        var total = files.Sum(static file => file.Size);
        long processed = 0;
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(file.Entry, CompressionLevel.Fastest);
            await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await using var output = entry.Open();
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                processed += read;
                progress?.Report(new SaveTransferProgress(processed, total));
            }
        }
    }

    internal static void ValidateDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
            name.Any(static character => char.IsControl(character) || character is '/' or '\\'))
        {
            throw new InvalidDataException("A save directory name is invalid.");
        }
    }
}

public sealed class SaveImportTransaction
{
    private readonly string savesRoot;
    private readonly string stagingRoot;
    private readonly string backupRoot;

    public SaveImportTransaction(string savesRoot, string stagingRoot, string backupRoot)
    {
        this.savesRoot = LiveSaveGameCatalog.ValidateRoot(savesRoot);
        this.stagingRoot = LiveSaveGameCatalog.ValidateRoot(stagingRoot);
        this.backupRoot = LiveSaveGameCatalog.ValidateRoot(backupRoot);
    }

    public async ValueTask<SaveImportResult> ImportAsync(
        string archivePath,
        IReadOnlyList<SaveImportSelection> selections,
        IProgress<SaveTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var inspection = SaveArchiveInspector.InspectZip(archivePath);
        var selected = selections
            .Select(selection => (
                Candidate: inspection.Candidates.SingleOrDefault(candidate => candidate.CandidateId == selection.CandidateId)
                    ?? throw new InvalidDataException("A selected save candidate is no longer present."),
                selection.ConflictResolution))
            .Where(static item => item.Candidate.CanImport)
            .GroupBy(static item => item.Candidate.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.Single())
            .ToArray();
        if (selected.Length == 0)
            return new SaveImportResult([], null);

        Directory.CreateDirectory(savesRoot);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        var transactionRoot = Path.Combine(stagingRoot, $"import-{Guid.NewGuid():N}");
        var preparedRoot = Path.Combine(transactionRoot, "prepared");
        var rollbackRoot = Path.Combine(transactionRoot, "rollback");
        Directory.CreateDirectory(preparedRoot);
        Directory.CreateDirectory(rollbackRoot);
        string? safetyBackup = null;
        var committed = new List<(string Target, string? Rollback)>();
        try
        {
            await ExtractAsync(archivePath, selected.Select(static item => item.Candidate).ToArray(), preparedRoot, progress, cancellationToken)
                .ConfigureAwait(false);
            var replacements = selected
                .Where(item => item.ConflictResolution == SaveImportConflictResolution.Replace &&
                               Directory.Exists(Path.Combine(savesRoot, item.Candidate.DirectoryName)))
                .Select(static item => item.Candidate.DirectoryName)
                .ToArray();
            if (replacements.Length > 0)
            {
                safetyBackup = $"before-import-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip";
                var temporary = Path.Combine(backupRoot, safetyBackup + ".tmp");
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                        await SaveArchiveWriter.WriteDirectoriesAsync(savesRoot, replacements, output, null, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, Path.Combine(backupRoot, safetyBackup));
                }
                catch
                {
                    try
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                    throw;
                }
            }

            foreach (var item in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = item.Candidate.DirectoryName;
                var target = Path.Combine(savesRoot, name);
                var prepared = Path.Combine(preparedRoot, name);
                if (Directory.Exists(target))
                {
                    if (item.ConflictResolution == SaveImportConflictResolution.Skip)
                        continue;
                    var rollback = Path.Combine(rollbackRoot, name);
                    Directory.Move(target, rollback);
                    try
                    {
                        Directory.Move(prepared, target);
                        committed.Add((target, rollback));
                    }
                    catch
                    {
                        Directory.Move(rollback, target);
                        throw;
                    }
                }
                else
                {
                    Directory.Move(prepared, target);
                    committed.Add((target, null));
                }
            }
            Directory.Delete(transactionRoot, recursive: true);
            return new SaveImportResult(
                committed.Select(item => Path.GetFileName(item.Target)).ToArray(),
                safetyBackup);
        }
        catch
        {
            foreach (var item in committed.AsEnumerable().Reverse())
            {
                if (Directory.Exists(item.Target))
                    Directory.Delete(item.Target, recursive: true);
                if (item.Rollback is not null && Directory.Exists(item.Rollback))
                    Directory.Move(item.Rollback, item.Target);
            }
            if (Directory.Exists(transactionRoot))
                Directory.Delete(transactionRoot, recursive: true);
            throw;
        }
    }

    private static async Task ExtractAsync(
        string archivePath,
        IReadOnlyList<SaveArchiveCandidate> selected,
        string preparedRoot,
        IProgress<SaveTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(static entry => !string.IsNullOrEmpty(entry.Name))
            .ToDictionary(entry => SaveArchiveInspector.NormalizeEntryPath(entry.FullName), StringComparer.OrdinalIgnoreCase);
        var total = selected.Sum(static candidate => candidate.ExpandedSize);
        long processed = 0;
        foreach (var candidate in selected)
        {
            SaveArchiveWriter.ValidateDirectoryName(candidate.DirectoryName);
            var destinationRoot = Path.Combine(preparedRoot, candidate.DirectoryName);
            Directory.CreateDirectory(destinationRoot);
            foreach (var pair in entries.Where(pair => candidate.EntryPrefix.Length == 0 ||
                         pair.Key.StartsWith(candidate.EntryPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = candidate.EntryPrefix.Length == 0
                    ? pair.Key
                    : pair.Key[candidate.EntryPrefix.Length..];
                if (relative.Length == 0)
                    continue;
                var target = Path.GetFullPath(Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("A save archive entry escapes its candidate root.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = pair.Value.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                var buffer = new byte[128 * 1024];
                int read;
                long written = 0;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > pair.Value.Length || written > SaveArchiveInspector.MaximumSingleFileBytes)
                        throw new InvalidDataException("A save archive entry expanded beyond its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    processed += read;
                    progress?.Report(new SaveTransferProgress(processed, total));
                }
                if (written != pair.Value.Length)
                    throw new InvalidDataException("A save archive entry did not match its declared size.");
            }
            if (!File.Exists(Path.Combine(destinationRoot, "SaveGameInfo")) ||
                !File.Exists(Path.Combine(destinationRoot, candidate.DirectoryName)))
            {
                throw new InvalidDataException("An extracted save is incomplete.");
            }
        }
    }
}
