using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoGate.Mods;

public sealed record ModDependencySummary(
    string UniqueId,
    bool IsRequired,
    string? MinimumVersion);

public sealed record ModManifestSummary(
    string Name,
    string Author,
    string Version,
    string UniqueId,
    string? Description,
    string? EntryDll,
    string? ContentPackForUniqueId,
    IReadOnlyList<ModDependencySummary> Dependencies);

public sealed record ModLibraryItem(
    string Schema,
    string LibraryItemId,
    string ContentId,
    ModManifestSummary Manifest,
    string RelativeStoragePath,
    DateTimeOffset ImportedAtUtc,
    string? SourceArchiveName,
    int FileCount,
    long TotalBytes)
{
    public const string CurrentSchema = "junimogate-mod-library-item/v1";

    public void Validate()
    {
        if (Schema != CurrentSchema || !ModContentId.IsValid(LibraryItemId) || ContentId != LibraryItemId ||
            Manifest is null || string.IsNullOrWhiteSpace(Manifest.Name) || string.IsNullOrWhiteSpace(Manifest.Author) ||
            string.IsNullOrWhiteSpace(Manifest.Version) || string.IsNullOrWhiteSpace(Manifest.UniqueId) ||
            ImportedAtUtc == default || FileCount < 1 || TotalBytes < 1 ||
            RelativeStoragePath != $"library/{LibraryItemId}/files")
        {
            throw new InvalidDataException("The Mod library item is malformed.");
        }

        if (Manifest.Dependencies is null)
            throw new InvalidDataException("The Mod dependency metadata is missing.");
        foreach (var dependency in Manifest.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.UniqueId))
                throw new InvalidDataException("The Mod dependency metadata is malformed.");
        }
    }
}

public sealed record ModLibraryIndex(
    string Schema,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ModLibraryItem> Items)
{
    public const string CurrentSchema = "junimogate-mod-library/v1";

    public void Validate()
    {
        if (Schema != CurrentSchema || Revision < 1 || UpdatedAtUtc == default || Items is null)
            throw new InvalidDataException("The Mod library index is malformed.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            item?.Validate();
            if (item is null || !ids.Add(item.LibraryItemId))
                throw new InvalidDataException("The Mod library index contains a duplicate or null item.");
        }
    }
}

public sealed class ModLibraryLayout
{
    public ModLibraryLayout(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("The Mod library root must be absolute.", nameof(root));

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        IndexPath = Path.Combine(Root, "library-index.json");
        LibraryDirectory = Path.Combine(Root, "library");
        StagingDirectory = Path.Combine(Root, "staging");
        QuarantineDirectory = Path.Combine(Root, "quarantine");
        ExportsDirectory = Path.Combine(Root, "exports");
    }

    public string Root { get; }
    public string IndexPath { get; }
    public string LibraryDirectory { get; }
    public string StagingDirectory { get; }
    public string QuarantineDirectory { get; }
    public string ExportsDirectory { get; }

    public string GetItemDirectory(string libraryItemId) => Path.Combine(LibraryDirectory, libraryItemId);
    public string GetItemFilesDirectory(string libraryItemId) => Path.Combine(GetItemDirectory(libraryItemId), "files");
    public string GetItemMetadataPath(string libraryItemId) => Path.Combine(GetItemDirectory(libraryItemId), "library-item.json");
}

public sealed class ModLibraryRepository
{
    private const int MaximumIndexBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly SemaphoreSlim operationLock = new(1, 1);

    public ModLibraryRepository(string root)
    {
        Layout = new ModLibraryLayout(root);
    }

    public ModLibraryLayout Layout { get; }

    public IModArchiveInstallTransaction CreateInstallTransaction(
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        new ModArchiveInstallTransaction(this, sourceArchiveName, limits ?? ModArchiveImportLimits.Default);

    public async ValueTask<ModLibraryIndex> ReadAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectories();
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(
        string libraryItemId,
        CancellationToken cancellationToken = default)
    {
        if (!ModContentId.IsValid(libraryItemId))
            throw new ArgumentException("The Mod library item ID is invalid.", nameof(libraryItemId));

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var item = current.Items.FirstOrDefault(candidate => candidate.LibraryItemId == libraryItemId);
            if (item is null)
                return false;

            var source = Layout.GetItemDirectory(libraryItemId);
            var trash = Path.Combine(Layout.StagingDirectory, $"delete-{Guid.NewGuid():N}");
            if (!Directory.Exists(source))
                throw new InvalidDataException("The Mod library item directory is missing.");

            Directory.Move(source, trash);
            try
            {
                var updated = current with
                {
                    Revision = checked(current.Revision + 1),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Items = current.Items.Where(candidate => candidate.LibraryItemId != libraryItemId).ToArray(),
                };
                await WriteIndexAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!Directory.Exists(source) && Directory.Exists(trash))
                    Directory.Move(trash, source);
                throw;
            }

            TryDeleteDirectory(trash);
            return true;
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async ValueTask<ModArchiveImportResult> CommitAsync(
        IReadOnlyList<PreparedModLibraryItem> preparedItems,
        CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var known = current.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
            var added = new List<ModLibraryItem>();
            var reused = new List<ModLibraryItem>();
            var recoveredItems = new List<ModLibraryItem>();
            var movedDirectories = new List<string>();
            try
            {
                foreach (var prepared in preparedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    prepared.Item.Validate();
                    if (known.TryGetValue(prepared.Item.LibraryItemId, out var existing))
                    {
                        var existingDirectory = Layout.GetItemDirectory(existing.LibraryItemId);
                        if (!Directory.Exists(existingDirectory))
                        {
                            Directory.Move(prepared.Directory, existingDirectory);
                            movedDirectories.Add(existingDirectory);
                        }
                        reused.Add(existing);
                        continue;
                    }

                    var destination = Layout.GetItemDirectory(prepared.Item.LibraryItemId);
                    if (Directory.Exists(destination))
                    {
                        var recoveredItem = await ReadItemMetadataAsync(
                                Path.Combine(destination, "library-item.json"),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (recoveredItem.LibraryItemId != prepared.Item.LibraryItemId)
                            throw new InvalidDataException("An orphaned Mod library directory has the wrong identity.");
                        if (!Directory.Exists(Path.Combine(destination, "files")))
                            throw new InvalidDataException("An orphaned Mod library directory is incomplete.");
                        known.Add(recoveredItem.LibraryItemId, recoveredItem);
                        recoveredItems.Add(recoveredItem);
                        reused.Add(recoveredItem);
                        continue;
                    }

                    Directory.Move(prepared.Directory, destination);
                    movedDirectories.Add(destination);
                    known.Add(prepared.Item.LibraryItemId, prepared.Item);
                    added.Add(prepared.Item);
                }

                if (added.Count > 0 || recoveredItems.Count > 0)
                {
                    var updated = current with
                    {
                        Revision = checked(current.Revision + 1),
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Items = known.Values
                            .OrderBy(item => item.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.Manifest.Version, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.LibraryItemId, StringComparer.Ordinal)
                            .ToArray(),
                    };
                    await WriteIndexAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
                }

                return new ModArchiveImportResult(added, reused);
            }
            catch
            {
                foreach (var directory in movedDirectories)
                    TryDeleteDirectory(directory);
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal static JsonSerializerOptions SerializerOptions => JsonOptions;

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(Layout.Root);
        Directory.CreateDirectory(Layout.LibraryDirectory);
        Directory.CreateDirectory(Layout.StagingDirectory);
        Directory.CreateDirectory(Layout.QuarantineDirectory);
        Directory.CreateDirectory(Layout.ExportsDirectory);
    }

    private async ValueTask<ModLibraryIndex> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Layout.IndexPath))
        {
            return new ModLibraryIndex(
                ModLibraryIndex.CurrentSchema,
                Revision: 1,
                DateTimeOffset.UtcNow,
                Array.Empty<ModLibraryItem>());
        }

        var file = new FileInfo(Layout.IndexPath);
        if (file.Length is < 1 or > MaximumIndexBytes)
            throw new InvalidDataException("The Mod library index has an invalid size.");

        await using var stream = new FileStream(
            Layout.IndexPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var index = await JsonSerializer.DeserializeAsync<ModLibraryIndex>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The Mod library index is empty.");
            index.Validate();
            return index;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Mod library index JSON is malformed.", exception);
        }
    }

    private async ValueTask WriteIndexAtomicAsync(
        ModLibraryIndex index,
        CancellationToken cancellationToken)
    {
        index.Validate();
        var temporary = Layout.IndexPath + $".{Guid.NewGuid():N}.tmp";
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
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Layout.IndexPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async ValueTask<ModLibraryItem> ReadItemMetadataAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var item = await JsonSerializer.DeserializeAsync<ModLibraryItem>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The Mod library item metadata is empty.");
        item.Validate();
        return item;
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; stale staging is recoverable and never authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; stale staging is recoverable and never authoritative.
        }
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the committed file is authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; the committed file is authoritative.
        }
    }
}

internal sealed record PreparedModLibraryItem(ModLibraryItem Item, string Directory);

internal static class ModContentId
{
    public static bool IsValid(string? value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
