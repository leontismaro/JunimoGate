using System.IO.Compression;

namespace JunimoGate.Core;

public sealed record SaveBackupEntry(
    string FileName,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    int SaveEntryCount);

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
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
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
        unavailable += Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Count();
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
        var path = ResolveFileName(fileName);
        if (!TryCreateEntry(path, out _))
            throw new InvalidDataException("The selected save backup is missing or incomplete.");
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
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 22 ||
                !file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                ResolveFileName(file.Name) != file.FullName)
            {
                return false;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var saveRoots = archive.Entries
                .Select(static item => item.FullName.Replace('\\', '/').Split('/', 2)[0])
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (archive.Entries.Count == 0 || saveRoots == 0)
                return false;
            entry = new SaveBackupEntry(
                file.Name,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                saveRoots);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private string ResolveFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 240 ||
            fileName != Path.GetFileName(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
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
