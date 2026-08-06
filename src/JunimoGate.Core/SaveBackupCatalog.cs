using System.IO.Compression;

namespace JunimoGate.Core;

public sealed record SaveBackupEntry(
    string FileName,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    IReadOnlyList<SaveArchiveCandidate> Saves,
    bool IsDirectory)
{
    public int SaveEntryCount => Saves.Count;
}

public sealed record SaveBackupCatalogSnapshot(
    IReadOnlyList<SaveBackupEntry> Entries,
    int UnavailableEntryCount);

public sealed class SaveBackupCatalog
{
    public const int MaximumEntries = 64;
    private readonly string root;

    public SaveBackupCatalog(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("The save backup root must be absolute.", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public SaveBackupCatalogSnapshot Read()
    {
        if (!Directory.Exists(root))
            return new SaveBackupCatalogSnapshot([], 0);
        var entries = new List<SaveBackupEntry>();
        var unavailable = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(static path => File.GetLastWriteTimeUtc(path)))
        {
            if (!TryCreateEntry(path, out var entry))
            {
                unavailable++;
                continue;
            }
            if (entries.Count < MaximumEntries)
                entries.Add(entry);
            else
                unavailable++;
        }
        return new SaveBackupCatalogSnapshot(entries, unavailable);
    }

    public async ValueTask ExportAsync(
        string fileName,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The save backup destination must be writable.", nameof(destination));
        var path = ResolveEntryName(fileName);
        if (!TryCreateEntry(path, out var entry))
            throw new InvalidDataException("The selected save backup is missing or incomplete.");
        if (entry.IsDirectory)
        {
            await SaveArchiveWriter.WriteDirectoriesAsync(
                    path,
                    entry.Saves.Select(static save => save.DirectoryName).ToArray(),
                    destination,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool TryCreateEntry(string path, out SaveBackupEntry entry)
    {
        entry = null!;
        try
        {
            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || ResolveEntryName(directory.Name) != directory.FullName)
                    return false;
                var inspection = SaveArchiveInspector.InspectDirectory(path);
                var saves = inspection.Candidates.Where(static candidate => candidate.CanImport).ToArray();
                if (saves.Length == 0)
                    return false;
                entry = new SaveBackupEntry(
                    directory.Name,
                    inspection.ExpandedSize,
                    new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero),
                    saves,
                    IsDirectory: true);
                return true;
            }
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 22 || !file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0 || ResolveEntryName(file.Name) != file.FullName)
                return false;
            var archiveInspection = SaveArchiveInspector.InspectZip(path);
            var archiveSaves = archiveInspection.Candidates.Where(static candidate => candidate.CanImport).ToArray();
            if (archiveSaves.Length == 0)
                return false;
            entry = new SaveBackupEntry(
                file.Name,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                archiveSaves,
                IsDirectory: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private string ResolveEntryName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 240 ||
            fileName != Path.GetFileName(fileName) ||
            fileName.Any(static character => char.IsControl(character)))
        {
            throw new InvalidDataException("The save backup file name is invalid.");
        }
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The save backup path escapes its root.");
        return path;
    }
}
