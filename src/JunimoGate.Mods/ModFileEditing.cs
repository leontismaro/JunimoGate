using System.Text;

namespace JunimoGate.Mods;

public sealed record ModFileEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long Length,
    bool CanEdit);

public sealed record ModTextFile(
    string RelativePath,
    string Text,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed class ModFileService(ModLibraryRepository library)
{
    public const long MaximumEditableBytes = 1024 * 1024;
    private const int TextProbeBytes = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ProtectedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".apk", ".dll", ".dex", ".exe", ".pdb", ".so", ".zip",
    };

    public async ValueTask<IReadOnlyList<ModFileEntry>> ListAsync(
        string libraryItemId,
        string? relativeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveItemRootAsync(libraryItemId, cancellationToken).ConfigureAwait(false);
        var relative = NormalizeDirectory(relativeDirectory);
        var directory = ResolveContained(root, relative, allowRoot: true);
        var info = new DirectoryInfo(directory);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The Mod directory is missing or unsupported.");

        var entries = new List<ModFileEntry>();
        foreach (var child in info.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var childRelative = CombineRelative(relative, child.Name);
            if (child is DirectoryInfo)
            {
                entries.Add(new ModFileEntry(child.Name, childRelative, true, 0, false));
                continue;
            }
            if (child is not FileInfo file)
                continue;
            entries.Add(new ModFileEntry(
                child.Name,
                childRelative,
                false,
                file.Length,
                await CanEditAsync(file, childRelative, cancellationToken).ConfigureAwait(false)));
        }
        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<ModTextFile> ReadTextAsync(
        string libraryItemId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var (path, info) = await ResolveEditableFileAsync(libraryItemId, relativePath, cancellationToken)
            .ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(path, info.Length, cancellationToken).ConfigureAwait(false);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The selected Mod file is not UTF-8 text.", exception);
        }
        if (text.Contains('\0'))
            throw new InvalidDataException("The selected Mod file contains binary data.");
        return new ModTextFile(relativePath.Replace('\\', '/'), text, info.Length, info.LastWriteTimeUtc);
    }

    public async ValueTask<ModTextFile> CreateTextAsync(
        string libraryItemId,
        string? relativeDirectory,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !SafeArchivePath.TryParse(fileName, out var safeName) ||
            safeName.Value != fileName || fileName.Contains('/') || fileName.Contains('\\') ||
            IsProtected(safeName.Value))
        {
            throw new InvalidDataException("The new Mod file name is invalid or protected.");
        }

        var root = await ResolveItemRootAsync(libraryItemId, cancellationToken).ConfigureAwait(false);
        var relative = NormalizeDirectory(relativeDirectory);
        var directory = ResolveContained(root, relative, allowRoot: true);
        var directoryInfo = new DirectoryInfo(directory);
        if (!directoryInfo.Exists || (directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The Mod directory is missing or unsupported.");

        var relativePath = CombineRelative(relative, safeName.Value);
        var path = ResolveContained(root, relativePath, allowRoot: false);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("A Mod file or directory with that name already exists.");

        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1,
                             FileOptions.Asynchronous))
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }

        var created = new FileInfo(path);
        await library.RecordContentMutationAsync(new[] { libraryItemId }, cancellationToken)
            .ConfigureAwait(false);
        return new ModTextFile(relativePath, string.Empty, created.Length, created.LastWriteTimeUtc);
    }

    public async ValueTask<ModTextFile> SaveTextAsync(
        string libraryItemId,
        ModTextFile original,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(text);
        var bytes = StrictUtf8.GetBytes(text);
        if (bytes.LongLength > MaximumEditableBytes)
            throw new InvalidDataException("The edited Mod file exceeds the size limit.");

        var (path, info) = await ResolveEditableFileAsync(libraryItemId, original.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (info.Length != original.Length || info.LastWriteTimeUtc != original.LastWriteTimeUtc.UtcDateTime)
            throw new InvalidOperationException("The Mod file changed after it was opened.");

        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
        var saved = new FileInfo(path);
        await library.RecordContentMutationAsync(new[] { libraryItemId }, cancellationToken)
            .ConfigureAwait(false);
        return new ModTextFile(original.RelativePath, text, saved.Length, saved.LastWriteTimeUtc);
    }

    private async ValueTask<(string Path, FileInfo Info)> ResolveEditableFileAsync(
        string libraryItemId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (!SafeArchivePath.TryParse(relativePath, out var safePath) || IsProtected(safePath.Value))
            throw new InvalidDataException("The selected Mod file cannot be edited.");
        var root = await ResolveItemRootAsync(libraryItemId, cancellationToken).ConfigureAwait(false);
        var path = ResolveContained(root, safePath.Value, allowRoot: false);
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 0 or > MaximumEditableBytes)
        {
            throw new InvalidDataException("The selected Mod file is missing, unsupported, or too large.");
        }
        return (path, info);
    }

    private async ValueTask<string> ResolveItemRootAsync(
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        if (!ModLibraryItemId.IsValid(libraryItemId))
            throw new ArgumentException("The Mod library item ID is invalid.", nameof(libraryItemId));
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!index.Items.Any(item => item.LibraryItemId == libraryItemId))
            throw new KeyNotFoundException("The Mod library item does not exist.");
        var root = Path.GetFullPath(library.Layout.GetItemFilesDirectory(libraryItemId));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The Mod library item directory is missing.");
        return root;
    }

    private static async ValueTask<bool> CanEditAsync(
        FileInfo file,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (file.Length is < 0 or > MaximumEditableBytes || IsProtected(relativePath))
            return false;
        var length = (int)Math.Min(file.Length, TextProbeBytes);
        var buffer = new byte[length];
        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            TextProbeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAtLeastAsync(buffer, length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (buffer.AsSpan(0, read).Contains((byte)0))
                return false;
            var decoder = StrictUtf8.GetDecoder();
            var characters = new char[StrictUtf8.GetMaxCharCount(read)];
            _ = decoder.GetChars(buffer, 0, read, characters, 0, flush: file.Length <= read);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength > MaximumEditableBytes)
            throw new InvalidDataException("The selected Mod file exceeds the size limit.");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var memory = new MemoryStream((int)expectedLength);
        var buffer = new byte[32 * 1024];
        while (memory.Length <= MaximumEditableBytes)
        {
            var remaining = MaximumEditableBytes + 1 - memory.Length;
            var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (memory.Length > MaximumEditableBytes)
            throw new InvalidDataException("The selected Mod file exceeds the size limit.");
        if (memory.Length != expectedLength)
            throw new InvalidOperationException("The Mod file changed while it was being opened.");
        return memory.ToArray();
    }

    private static string NormalizeDirectory(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return SafeArchivePath.Parse(value).Value;
    }

    private static string CombineRelative(string parent, string name) =>
        parent.Length == 0 ? SafeArchivePath.Parse(name).Value : SafeArchivePath.Parse($"{parent}/{name}").Value;

    private static string ResolveContained(string root, string relative, bool allowRoot)
    {
        var path = relative.Length == 0
            ? root
            : Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !allowRoot && path == root)
        {
            throw new InvalidDataException("The selected Mod path escaped its library item.");
        }
        return path;
    }

    private static bool IsProtected(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
               ProtectedExtensions.Contains(Path.GetExtension(name));
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The committed destination is authoritative.
        }
    }
}
