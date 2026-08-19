using System.Collections.Concurrent;
using System.Security.Cryptography;
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
    IReadOnlyList<ModDependencySummary> Dependencies)
{
    public IReadOnlyList<string> UpdateKeys { get; init; } = Array.Empty<string>();
}

public sealed record ModLibraryItem(
    string Schema,
    string LibraryItemId,
    [property: JsonPropertyName("contentId")] string ImportedContentId,
    ModManifestSummary Manifest,
    string RelativeStoragePath,
    DateTimeOffset ImportedAtUtc,
    string? SourceArchiveName,
    int FileCount,
    long TotalBytes)
{
    public const string CurrentSchema = "junimogate-mod-library-item/v1";
    public string? OriginalRootPath { get; init; }
    public long ContentGeneration { get; init; } = 1;
    public int CurrentFileCount { get; init; } = FileCount;
    public long CurrentTotalBytes { get; init; } = TotalBytes;

    public void Validate()
    {
        if (Schema != CurrentSchema || !ModLibraryItemId.IsValid(LibraryItemId) || !ModContentId.IsValid(ImportedContentId) ||
            Manifest is null || string.IsNullOrWhiteSpace(Manifest.Name) || string.IsNullOrWhiteSpace(Manifest.Author) ||
            string.IsNullOrWhiteSpace(Manifest.Version) || string.IsNullOrWhiteSpace(Manifest.UniqueId) ||
            ImportedAtUtc == default || FileCount < 1 || TotalBytes < 1 || ContentGeneration < 1 ||
            CurrentFileCount < 1 || CurrentTotalBytes < 1 ||
            RelativeStoragePath != $"library/{LibraryItemId}/files")
        {
            throw new InvalidDataException("The Mod library item is malformed.");
        }

        if (OriginalRootPath is { Length: > 0 } rootPath &&
            (!SafeArchivePath.TryParse(rootPath, out var parsedRoot) || parsedRoot.Value != rootPath))
        {
            throw new InvalidDataException("The Mod library item source root is malformed.");
        }

        if (Manifest.Dependencies is null || Manifest.UpdateKeys is null)
            throw new InvalidDataException("The Mod manifest collection metadata is missing.");
        foreach (var dependency in Manifest.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.UniqueId))
                throw new InvalidDataException("The Mod dependency metadata is malformed.");
        }
        if (Manifest.UpdateKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 4096))
            throw new InvalidDataException("The Mod update-key metadata is malformed.");
    }
}

public sealed record ModLibraryIndex(
    string Schema,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ModLibraryItem> Items)
{
    public const string CurrentSchema = "junimogate-mod-library/v1";
    public ModBundleCatalog BundleCatalog { get; init; } = ModBundleCatalog.CreateEmpty();

    public void Validate()
    {
        if (Schema != CurrentSchema || Revision < 1 || UpdatedAtUtc == default || Items is null || BundleCatalog is null)
            throw new InvalidDataException("The Mod library index is malformed.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            item?.Validate();
            if (item is null || !ids.Add(item.LibraryItemId))
                throw new InvalidDataException("The Mod library index contains a duplicate or null item.");
        }
        BundleCatalog.Validate(Items);
    }
}

public sealed record ModLibraryDeleteResult(
    IReadOnlyList<ModLibraryItem> DeletedItems,
    IReadOnlyList<string> MissingItemIds,
    long Revision);

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
        TranslationsDirectory = Path.Combine(Root, "translations");
    }

    public string Root { get; }
    public string IndexPath { get; }
    public string LibraryDirectory { get; }
    public string StagingDirectory { get; }
    public string QuarantineDirectory { get; }
    public string ExportsDirectory { get; }
    public string TranslationsDirectory { get; }

    public string GetItemDirectory(string libraryItemId) => Path.Combine(LibraryDirectory, libraryItemId);
    public string GetItemFilesDirectory(string libraryItemId) => Path.Combine(GetItemDirectory(libraryItemId), "files");
    public string GetItemMetadataPath(string libraryItemId) => Path.Combine(GetItemDirectory(libraryItemId), "library-item.json");
}

public sealed partial class ModLibraryRepository : IModInstallRepository
{
    private const int MaximumIndexBytes = 8 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> OperationLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly SemaphoreSlim operationLock;

    public ModLibraryRepository(string root)
    {
        Layout = new ModLibraryLayout(root);
        operationLock = OperationLocks.GetOrAdd(Layout.Root, static _ => new SemaphoreSlim(1, 1));
    }

    public ModLibraryLayout Layout { get; }
    public event Action? Changed;
    public event Action? BundleChanged;

    public IModArchiveInstallTransaction CreateInstallTransaction(
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        new ModArchiveInstallTransaction(this, sourceArchiveName, limits ?? ModArchiveImportLimits.Default);

    public ModTranslationInstallTransaction CreateTranslationTransaction(
        IReadOnlyList<ModTranslationTarget> targets,
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        new(this, targets, sourceArchiveName, limits ?? ModArchiveImportLimits.Default);

    public async ValueTask<ModLibraryIndex> ReadAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
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
        var result = await DeleteManyAsync(new[] { libraryItemId }, cancellationToken).ConfigureAwait(false);
        return result.DeletedItems.Count == 1;
    }

    public async ValueTask<ModLibraryIndex> RecordContentMutationAsync(
        IReadOnlyCollection<string> libraryItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryItemIds);
        var requested = libraryItemIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Any(id => !ModLibraryItemId.IsValid(id)))
            throw new ArgumentException("A Mod library item ID is invalid.", nameof(libraryItemIds));
        if (requested.Count == 0)
            return await ReadAsync(cancellationToken).ConfigureAwait(false);

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var updated = await UpdateContentStatisticsUnlockedAsync(current, requested, cancellationToken)
                .ConfigureAwait(false);
            await WriteIndexAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
            return updated;
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async ValueTask CommitFileMutationAsync(
        string libraryItemId,
        string relativePath,
        string stagedFile,
        bool requireExisting,
        long? expectedLength,
        DateTimeOffset? expectedLastWriteTimeUtc,
        CancellationToken cancellationToken)
    {
        if (!ModLibraryItemId.IsValid(libraryItemId))
            throw new ArgumentException("The Mod library item ID is invalid.", nameof(libraryItemId));
        var safePath = SafeArchivePath.Parse(relativePath);
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current.Items.All(item => item.LibraryItemId != libraryItemId))
                throw new KeyNotFoundException("The Mod library item does not exist.");
            var root = Layout.GetItemFilesDirectory(libraryItemId);
            var destination = ResolveContained(root, safePath.Value);
            var destinationInfo = new FileInfo(destination);
            if (requireExisting)
            {
                if (!destinationInfo.Exists || expectedLength is null || expectedLastWriteTimeUtc is null ||
                    destinationInfo.Length != expectedLength.Value ||
                    destinationInfo.LastWriteTimeUtc != expectedLastWriteTimeUtc.Value.UtcDateTime)
                {
                    throw new InvalidOperationException("The Mod file changed after it was opened.");
                }
            }
            else if (destinationInfo.Exists || Directory.Exists(destination))
            {
                throw new IOException("A Mod file or directory with that name already exists.");
            }

            var transactionId = Guid.NewGuid().ToString("N");
            var transactionDirectory = Path.Combine(Layout.StagingDirectory, $"edit-{transactionId}");
            Directory.CreateDirectory(transactionDirectory);
            var newFile = Path.Combine(transactionDirectory, "new");
            File.Move(stagedFile, newFile);
            CopyIndexForRollback(transactionDirectory);
            var journalPath = Path.Combine(transactionDirectory, "transaction.json");
            await WriteJsonDurableAsync(
                    journalPath,
                    new ModFileMutationJournal(
                        ModFileMutationJournal.CurrentSchema,
                        transactionId,
                        "prepared",
                        libraryItemId,
                        safePath.Value,
                        requireExisting),
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (requireExisting)
                    File.Move(destination, Path.Combine(transactionDirectory, "old"));
                File.Move(newFile, destination);
                var updated = await UpdateContentStatisticsUnlockedAsync(
                        current,
                        new HashSet<string>(StringComparer.Ordinal) { libraryItemId },
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteIndexAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
                await WriteJsonDurableAsync(
                        journalPath,
                        new ModFileMutationJournal(
                            ModFileMutationJournal.CurrentSchema,
                            transactionId,
                            "committed",
                            libraryItemId,
                            safePath.Value,
                            requireExisting),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                Changed?.Invoke();
            }
            catch
            {
                RecoverFileMutation(transactionDirectory);
                throw;
            }
            TryDeleteDirectory(transactionDirectory);
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ModLibraryDeleteResult> DeleteManyAsync(
        IReadOnlyCollection<string> libraryItemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(libraryItemIds);
        var requestedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var libraryItemId in libraryItemIds)
        {
            if (!ModLibraryItemId.IsValid(libraryItemId))
                throw new ArgumentException("A Mod library item ID is invalid.", nameof(libraryItemIds));
            requestedIds.Add(libraryItemId);
        }

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (requestedIds.Count == 0)
                return new ModLibraryDeleteResult(Array.Empty<ModLibraryItem>(), Array.Empty<string>(), current.Revision);

            var itemsById = current.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
            var deletedItems = requestedIds
                .Where(itemsById.ContainsKey)
                .Select(id => itemsById[id])
                .ToArray();
            var missingItemIds = requestedIds
                .Where(id => !itemsById.ContainsKey(id))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (deletedItems.Length == 0)
                return new ModLibraryDeleteResult(Array.Empty<ModLibraryItem>(), missingItemIds, current.Revision);

            var moved = new List<(string Source, string Trash)>(deletedItems.Length);
            var translationChanges = new List<(string Source, string Trash, string? Staged)>();
            var appliedTranslationChanges = new List<(string Source, string Trash, string? Staged)>();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(Layout.TranslationsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = await ReadInstallationRecordAsync(directory, cancellationToken).ConfigureAwait(false);
                    var removedFiles = record.Files.Where(file => requestedIds.Contains(file.LibraryItemId)).ToArray();
                    if (removedFiles.Length == 0)
                        continue;

                    var retainedFiles = record.Files.Where(file => !requestedIds.Contains(file.LibraryItemId)).ToArray();
                    string? staged = null;
                    if (retainedFiles.Length > 0)
                    {
                        staged = Path.Combine(Layout.StagingDirectory, $"delete-translation-new-{Guid.NewGuid():N}");
                        translationChanges.Add((
                            directory,
                            Path.Combine(Layout.StagingDirectory, $"delete-translation-old-{Guid.NewGuid():N}"),
                            staged));
                        CopyDirectory(directory, staged, cancellationToken);
                        foreach (var file in removedFiles.Where(file => file.BackupRelativePath is not null))
                        {
                            var backup = ResolveContained(staged, file.BackupRelativePath!);
                            TryDeleteFile(backup);
                            RemoveEmptyParents(Path.GetDirectoryName(backup)!, staged);
                        }
                        await WriteJsonDurableAsync(
                                Path.Combine(staged, "installation.json"),
                                record with { Files = retainedFiles },
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        translationChanges.Add((
                            directory,
                            Path.Combine(Layout.StagingDirectory, $"delete-translation-old-{Guid.NewGuid():N}"),
                            null));
                    }
                }

                foreach (var item in deletedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = Layout.GetItemDirectory(item.LibraryItemId);
                    if (!Directory.Exists(source))
                        throw new InvalidDataException("A Mod library item directory is missing.");
                    var trash = Path.Combine(Layout.StagingDirectory, $"delete-{Guid.NewGuid():N}");
                    Directory.Move(source, trash);
                    moved.Add((source, trash));
                }

                foreach (var change in translationChanges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Move(change.Source, change.Trash);
                    appliedTranslationChanges.Add(change);
                    if (change.Staged is not null)
                        Directory.Move(change.Staged, change.Source);
                }

                var updatedCatalog = RemoveBundleMembers(current.BundleCatalog, requestedIds);
                var updated = current with
                {
                    Revision = checked(current.Revision + 1),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Items = current.Items.Where(item => !requestedIds.Contains(item.LibraryItemId)).ToArray(),
                    BundleCatalog = updatedCatalog,
                };
                await WriteIndexAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
                Changed?.Invoke();
            }
            catch
            {
                for (var index = appliedTranslationChanges.Count - 1; index >= 0; index--)
                {
                    var (source, trash, _) = appliedTranslationChanges[index];
                    if (Directory.Exists(source))
                        TryDeleteDirectory(source);
                    if (Directory.Exists(trash))
                        Directory.Move(trash, source);
                }
                for (var index = moved.Count - 1; index >= 0; index--)
                {
                    var (source, trash) = moved[index];
                    if (!Directory.Exists(source) && Directory.Exists(trash))
                        Directory.Move(trash, source);
                }
                foreach (var (_, _, staged) in translationChanges)
                {
                    if (staged is not null)
                        TryDeleteDirectory(staged);
                }
                throw;
            }

            foreach (var (_, trash) in moved)
                TryDeleteDirectory(trash);
            foreach (var (_, trash, staged) in translationChanges)
            {
                TryDeleteDirectory(trash);
                if (staged is not null)
                    TryDeleteDirectory(staged);
            }
            return new ModLibraryDeleteResult(deletedItems, missingItemIds, checked(current.Revision + 1));
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async ValueTask<ModArchiveImportResult> CommitAsync(
        IReadOnlyList<PreparedModLibraryItem> preparedItems,
        CancellationToken cancellationToken,
        IReadOnlyList<DetectedModBundle>? detectedBundles = null,
        ModBundleOrigin bundleOrigin = ModBundleOrigin.Detected)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var known = current.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
            var knownByImportedContent = current.Items
                .GroupBy(item => item.ImportedContentId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var added = new List<ModLibraryItem>();
            var reused = new List<ModLibraryItem>();
            var recoveredItems = new List<ModLibraryItem>();
            var movedDirectories = new List<string>();
            var repairedDirectories = new List<(string Destination, string? Backup)>();
            var resolvedPreparedByRoot = new Dictionary<string, PreparedModLibraryItem>(StringComparer.Ordinal);
            var resolvedItemsByPreparedId = new Dictionary<string, ModLibraryItem>(StringComparer.Ordinal);
            try
            {
                foreach (var prepared in preparedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    prepared.Item.Validate();
                    knownByImportedContent.TryGetValue(prepared.Item.ImportedContentId, out var matchingItems);
                    ModLibraryItem? existing = null;
                    var needsRepair = false;
                    if (matchingItems is not null)
                    {
                        foreach (var candidate in matchingItems)
                        {
                            var itemDirectory = Layout.GetItemDirectory(candidate.LibraryItemId);
                            var filesDirectory = Layout.GetItemFilesDirectory(candidate.LibraryItemId);
                            if (!Directory.Exists(itemDirectory) || !Directory.Exists(filesDirectory))
                            {
                                existing = candidate;
                                needsRepair = true;
                                break;
                            }
                            string currentDigest;
                            try
                            {
                                currentDigest = await ModImportUtilities.ComputeDirectoryContentDigestAsync(
                                        filesDirectory,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                            {
                                continue;
                            }
                            if (currentDigest == candidate.ImportedContentId)
                            {
                                existing = candidate;
                                break;
                            }
                        }
                    }
                    if (existing is not null)
                    {
                        var existingDirectory = Layout.GetItemDirectory(existing.LibraryItemId);
                        if (needsRepair)
                        {
                            var repaired = existing with
                            {
                                ContentGeneration = checked(existing.ContentGeneration + 1),
                                CurrentFileCount = prepared.Item.CurrentFileCount,
                                CurrentTotalBytes = prepared.Item.CurrentTotalBytes,
                            };
                            await WriteItemMetadataAsync(
                                    Path.Combine(prepared.Directory, "library-item.json"),
                                    repaired,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            string? backup = null;
                            if (Directory.Exists(existingDirectory))
                            {
                                backup = Path.Combine(Layout.StagingDirectory, $"repair-{Guid.NewGuid():N}");
                                Directory.Move(existingDirectory, backup);
                            }
                            Directory.Move(prepared.Directory, existingDirectory);
                            repairedDirectories.Add((existingDirectory, backup));
                            known[repaired.LibraryItemId] = repaired;
                            var candidateIndex = matchingItems!.FindIndex(candidate =>
                                candidate.LibraryItemId == repaired.LibraryItemId);
                            matchingItems[candidateIndex] = repaired;
                            recoveredItems.Add(repaired);
                            existing = repaired;
                        }
                        reused.Add(existing);
                        resolvedPreparedByRoot.Add(prepared.RootPath, prepared with { Item = existing });
                        resolvedItemsByPreparedId.Add(prepared.Item.LibraryItemId, existing);
                        continue;
                    }

                    var orphaned = await FindOrphanByImportedContentAsync(
                            prepared.Item.ImportedContentId,
                            known.Keys,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (orphaned is not null)
                    {
                        known.Add(orphaned.LibraryItemId, orphaned);
                        AddImportedContentCandidate(knownByImportedContent, orphaned);
                        recoveredItems.Add(orphaned);
                        reused.Add(orphaned);
                        resolvedPreparedByRoot.Add(prepared.RootPath, prepared with { Item = orphaned });
                        resolvedItemsByPreparedId.Add(prepared.Item.LibraryItemId, orphaned);
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
                        resolvedPreparedByRoot.Add(prepared.RootPath, prepared with { Item = recoveredItem });
                        resolvedItemsByPreparedId.Add(prepared.Item.LibraryItemId, recoveredItem);
                        continue;
                    }

                    Directory.Move(prepared.Directory, destination);
                    movedDirectories.Add(destination);
                    known.Add(prepared.Item.LibraryItemId, prepared.Item);
                    AddImportedContentCandidate(knownByImportedContent, prepared.Item);
                    added.Add(prepared.Item);
                    resolvedPreparedByRoot.Add(prepared.RootPath, prepared);
                    resolvedItemsByPreparedId.Add(prepared.Item.LibraryItemId, prepared.Item);
                }

                var importedBundles = CreateImportedBundles(
                    current.BundleCatalog,
                    detectedBundles ?? Array.Empty<DetectedModBundle>(),
                    resolvedPreparedByRoot,
                    bundleOrigin);
                var catalogChanged = importedBundles.Any(bundle =>
                    current.BundleCatalog.Bundles.All(existing => existing.BundleId != bundle.BundleId));
                if (added.Count > 0 || recoveredItems.Count > 0 || catalogChanged)
                {
                    var now = DateTimeOffset.UtcNow;
                    var catalog = catalogChanged
                        ? current.BundleCatalog with
                        {
                            Revision = checked(current.BundleCatalog.Revision + 1),
                            UpdatedAtUtc = now,
                            Bundles = current.BundleCatalog.Bundles
                                .Concat(importedBundles)
                                .DistinctBy(bundle => bundle.BundleId, StringComparer.Ordinal)
                                .OrderBy(bundle => bundle.DisplayName, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(bundle => bundle.BundleId, StringComparer.Ordinal)
                                .ToArray(),
                        }
                        : current.BundleCatalog;
                    var updated = current with
                    {
                        Revision = checked(current.Revision + 1),
                        UpdatedAtUtc = now,
                        Items = known.Values
                            .OrderBy(item => item.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.Manifest.Version, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(item => item.LibraryItemId, StringComparer.Ordinal)
                            .ToArray(),
                        BundleCatalog = catalog,
                    };
                    await WriteIndexAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
                    Changed?.Invoke();
                }

                var result = new ModArchiveImportResult(added, reused)
                {
                    Bundles = importedBundles,
                    ResolvedItemsByPreparedId = resolvedItemsByPreparedId,
                };
                foreach (var (_, backup) in repairedDirectories)
                {
                    if (backup is not null)
                        TryDeleteDirectory(backup);
                }
                return result;
            }
            catch
            {
                foreach (var directory in movedDirectories)
                    TryDeleteDirectory(directory);
                for (var index = repairedDirectories.Count - 1; index >= 0; index--)
                {
                    var (destination, backup) = repairedDirectories[index];
                    TryDeleteDirectory(destination);
                    if (backup is not null && Directory.Exists(backup))
                        Directory.Move(backup, destination);
                }
                throw;
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async ValueTask RollbackImportAsync(
        IReadOnlyCollection<string> addedItemIds,
        ModLibraryIndex previousIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addedItemIds);
        ArgumentNullException.ThrowIfNull(previousIndex);
        previousIndex.Validate();
        if (addedItemIds.Any(id => !ModContentId.IsValid(id)) || addedItemIds.Distinct(StringComparer.Ordinal).Count() != addedItemIds.Count)
            throw new InvalidDataException("The Mod import rollback identities are invalid.");

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (current.Revision == previousIndex.Revision)
                return;
            var remove = addedItemIds.ToHashSet(StringComparer.Ordinal);
            if (!remove.IsSubsetOf(current.Items.Select(item => item.LibraryItemId)))
                throw new InvalidDataException("The Mod import rollback no longer matches the library.");
            var retained = current.Items.Where(item => !remove.Contains(item.LibraryItemId)).ToArray();
            if (current.Revision != checked(previousIndex.Revision + 1) ||
                !LibraryItemsEqual(retained, previousIndex.Items))
            {
                throw new InvalidOperationException("The Mod library changed after the import and cannot be rolled back safely.");
            }

            var moved = new List<(string Source, string Staging)>();
            try
            {
                foreach (var id in remove)
                {
                    var source = Layout.GetItemDirectory(id);
                    if (!Directory.Exists(source))
                        throw new InvalidDataException("A Mod import rollback directory is missing.");
                    var staging = Path.Combine(Layout.StagingDirectory, $"rollback-{Guid.NewGuid():N}");
                    Directory.Move(source, staging);
                    moved.Add((source, staging));
                }

                await WriteIndexAtomicAsync(previousIndex, cancellationToken).ConfigureAwait(false);
                Changed?.Invoke();
            }
            catch
            {
                foreach (var (source, staging) in moved.AsEnumerable().Reverse())
                {
                    if (!Directory.Exists(source) && Directory.Exists(staging))
                        Directory.Move(staging, source);
                }
                throw;
            }

            foreach (var (_, staging) in moved)
                TryDeleteDirectory(staging);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private static bool LibraryItemsEqual(
        IReadOnlyList<ModLibraryItem> left,
        IReadOnlyList<ModLibraryItem> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Schema != second.Schema ||
                first.LibraryItemId != second.LibraryItemId ||
                first.ImportedContentId != second.ImportedContentId ||
                first.RelativeStoragePath != second.RelativeStoragePath ||
                first.ImportedAtUtc != second.ImportedAtUtc ||
                first.SourceArchiveName != second.SourceArchiveName ||
                first.OriginalRootPath != second.OriginalRootPath ||
                first.FileCount != second.FileCount ||
                first.TotalBytes != second.TotalBytes ||
                first.ContentGeneration != second.ContentGeneration ||
                first.CurrentFileCount != second.CurrentFileCount ||
                first.CurrentTotalBytes != second.CurrentTotalBytes ||
                !ManifestsEqual(first.Manifest, second.Manifest))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ManifestsEqual(ModManifestSummary left, ModManifestSummary right) =>
        left.Name == right.Name &&
        left.Author == right.Author &&
        left.Version == right.Version &&
        left.UniqueId == right.UniqueId &&
        left.Description == right.Description &&
        left.EntryDll == right.EntryDll &&
        left.ContentPackForUniqueId == right.ContentPackForUniqueId &&
        left.Dependencies.Count == right.Dependencies.Count &&
        left.Dependencies.Zip(right.Dependencies).All(pair =>
            pair.First.UniqueId == pair.Second.UniqueId &&
            pair.First.IsRequired == pair.Second.IsRequired &&
            pair.First.MinimumVersion == pair.Second.MinimumVersion) &&
        left.UpdateKeys.SequenceEqual(right.UpdateKeys, StringComparer.Ordinal);

    private static async ValueTask<(int FileCount, long TotalBytes)> ComputeCurrentStatisticsAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var fileCount = 0;
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            fileCount = checked(fileCount + 1);
            totalBytes = checked(totalBytes + info.Length);
        }
        if (fileCount < 1 || totalBytes < 1)
            throw new InvalidDataException("A Mod library item has no current content.");
        await Task.CompletedTask.ConfigureAwait(false);
        return (fileCount, totalBytes);
    }

    private async ValueTask<ModLibraryIndex> UpdateContentStatisticsUnlockedAsync(
        ModLibraryIndex current,
        IReadOnlySet<string> requested,
        CancellationToken cancellationToken)
    {
        var items = current.Items.ToArray();
        var changed = false;
        for (var index = 0; index < items.Length; index++)
        {
            if (!requested.Contains(items[index].LibraryItemId))
                continue;
            var filesRoot = Layout.GetItemFilesDirectory(items[index].LibraryItemId);
            if (!Directory.Exists(filesRoot))
                throw new DirectoryNotFoundException("The Mod library item directory is missing.");
            var (fileCount, totalBytes) = await ComputeCurrentStatisticsAsync(filesRoot, cancellationToken)
                .ConfigureAwait(false);
            items[index] = items[index] with
            {
                ContentGeneration = checked(items[index].ContentGeneration + 1),
                CurrentFileCount = fileCount,
                CurrentTotalBytes = totalBytes,
            };
            changed = true;
        }
        if (!changed)
            throw new KeyNotFoundException("A Mod library item does not exist.");
        return current with
        {
            Revision = checked(current.Revision + 1),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Items = items,
        };
    }

    internal static JsonSerializerOptions SerializerOptions => JsonOptions;

    public async ValueTask<ModBundleMutationResult> SetBundleMemberUnlockedAsync(
        string bundleId,
        string uniqueId,
        bool unlocked,
        CancellationToken cancellationToken = default)
    {
        if (!ModContentId.IsValid(bundleId))
            throw new ArgumentException("The Mod bundle ID is invalid.", nameof(bundleId));
        if (string.IsNullOrWhiteSpace(uniqueId) || uniqueId.Length > 256)
            throw new ArgumentException("The Mod UniqueID is invalid.", nameof(uniqueId));

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var bundle = current.BundleCatalog.Bundles.FirstOrDefault(candidate => candidate.BundleId == bundleId)
                         ?? throw new KeyNotFoundException("The Mod bundle does not exist.");
            if (!bundle.Members.Any(member => member.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase)))
                throw new KeyNotFoundException("The Mod is not a member of the selected bundle.");

            var overrides = current.BundleCatalog.UnlockOverrides.ToList();
            var index = overrides.FindIndex(value =>
                value.FamilyKey == bundle.FamilyKey && value.UniqueId.Equals(uniqueId, StringComparison.OrdinalIgnoreCase));
            if (unlocked == index >= 0)
            {
                return new ModBundleMutationResult(
                    current,
                    Changed: false,
                    BundleRemainsVisible: CountActiveMembers(bundle, overrides) >= 2);
            }
            if (unlocked)
                overrides.Add(new ModBundleUnlockOverride(bundle.FamilyKey, uniqueId));
            else
                overrides.RemoveAt(index);

            var now = DateTimeOffset.UtcNow;
            var catalog = current.BundleCatalog with
            {
                Revision = checked(current.BundleCatalog.Revision + 1),
                UpdatedAtUtc = now,
                UnlockOverrides = overrides
                    .OrderBy(value => value.FamilyKey, StringComparer.Ordinal)
                    .ThenBy(value => value.UniqueId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
            var updated = current with
            {
                BundleCatalog = catalog,
            };
            await WriteIndexAtomicAsync(updated, cancellationToken).ConfigureAwait(false);
            BundleChanged?.Invoke();
            return new ModBundleMutationResult(
                updated,
                Changed: true,
                BundleRemainsVisible: CountActiveMembers(bundle, overrides) >= 2);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(Layout.Root);
        Directory.CreateDirectory(Layout.LibraryDirectory);
        Directory.CreateDirectory(Layout.StagingDirectory);
        Directory.CreateDirectory(Layout.QuarantineDirectory);
        Directory.CreateDirectory(Layout.ExportsDirectory);
        Directory.CreateDirectory(Layout.TranslationsDirectory);
        RecoverFileMutations();
        RecoverTranslationTransactions();
    }

    private void RecoverFileMutations()
    {
        if (!Directory.Exists(Layout.StagingDirectory))
            return;
        foreach (var directory in Directory.EnumerateDirectories(Layout.StagingDirectory, "edit-*"))
        {
            if (File.Exists(Path.Combine(directory, "transaction.json")))
                RecoverFileMutation(directory);
        }
    }

    private void RecoverFileMutation(string transactionDirectory)
    {
        ModFileMutationJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<ModFileMutationJournal>(
                          File.ReadAllBytes(Path.Combine(transactionDirectory, "transaction.json")),
                          SerializerOptions)
                      ?? throw new InvalidDataException("A Mod file mutation journal is empty.");
            journal.Validate();
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return;
        }
        if (journal.Phase == "committed")
        {
            TryDeleteDirectory(transactionDirectory);
            return;
        }

        var live = ResolveContained(Layout.GetItemFilesDirectory(journal.LibraryItemId), journal.RelativePath);
        var old = Path.Combine(transactionDirectory, "old");
        if (journal.HadOriginal)
        {
            if (File.Exists(old))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(live)!);
                File.Copy(old, live, overwrite: true);
            }
        }
        else if (File.Exists(live))
            TryDeleteFile(live);
        RestoreIndexRollbackCopy(transactionDirectory);
        TryDeleteDirectory(transactionDirectory);
    }

    private async ValueTask<FileStream> AcquireProcessOperationLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Layout.Root);
        var lockPath = Path.Combine(Layout.Root, ".library-operation.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
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

    private static async ValueTask WriteItemMetadataAsync(
        string path,
        ModLibraryItem item,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, item, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async ValueTask<ModLibraryItem?> FindOrphanByImportedContentAsync(
        string importedContentId,
        IEnumerable<string> indexedItemIds,
        CancellationToken cancellationToken)
    {
        var indexed = indexedItemIds.ToHashSet(StringComparer.Ordinal);
        ModLibraryItem? match = null;
        foreach (var directory in Directory.EnumerateDirectories(Layout.LibraryDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !ModLibraryItemId.IsValid(info.Name) || indexed.Contains(info.Name))
            {
                continue;
            }

            ModLibraryItem candidate;
            try
            {
                candidate = await ReadItemMetadataAsync(
                        Path.Combine(directory, "library-item.json"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }
            if (candidate.LibraryItemId != info.Name || candidate.ImportedContentId != importedContentId)
                continue;
            var filesDirectory = Path.Combine(directory, "files");
            if (!Directory.Exists(filesDirectory))
                throw new InvalidDataException("An orphaned Mod library directory is incomplete.");
            var currentDigest = await ModImportUtilities.ComputeDirectoryContentDigestAsync(
                    filesDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (currentDigest != importedContentId)
                continue;
            if (match is not null)
                throw new InvalidDataException("Multiple orphaned Mod library items have the same imported content identity.");
            match = candidate;
        }
        return match;
    }

    private static void AddImportedContentCandidate(
        IDictionary<string, List<ModLibraryItem>> candidates,
        ModLibraryItem item)
    {
        if (!candidates.TryGetValue(item.ImportedContentId, out var matches))
        {
            matches = new List<ModLibraryItem>();
            candidates.Add(item.ImportedContentId, matches);
        }
        matches.Add(item);
    }

    private static IReadOnlyList<ModBundleDefinition> CreateImportedBundles(
        ModBundleCatalog current,
        IReadOnlyList<DetectedModBundle> detected,
        IReadOnlyDictionary<string, PreparedModLibraryItem> preparedByRoot,
        ModBundleOrigin origin)
    {
        var occupied = current.Bundles
            .SelectMany(bundle => bundle.Members)
            .Select(member => member.LibraryItemId)
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<ModBundleDefinition>();
        foreach (var bundle in detected)
        {
            var available = bundle.Members.Count(member =>
                preparedByRoot.TryGetValue(member.RootPath, out var prepared) &&
                !occupied.Contains(prepared.Item.LibraryItemId));
            if (available < 2)
                continue;
            var created = ModBundleFactory.Create(bundle, preparedByRoot, occupied, origin);
            result.Add(created);
            foreach (var member in created.Members)
                occupied.Add(member.LibraryItemId);
        }
        return result;
    }

    private static ModBundleCatalog RemoveBundleMembers(
        ModBundleCatalog catalog,
        IReadOnlySet<string> removedLibraryItemIds)
    {
        var changed = false;
        var bundles = new List<ModBundleDefinition>();
        foreach (var bundle in catalog.Bundles)
        {
            var members = bundle.Members
                .Where(member => !removedLibraryItemIds.Contains(member.LibraryItemId))
                .ToArray();
            if (members.Length != bundle.Members.Count)
                changed = true;
            if (members.Length >= 2)
                bundles.Add(bundle with { Members = members });
        }
        if (!changed)
            return catalog;
        return catalog with
        {
            Revision = checked(catalog.Revision + 1),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Bundles = bundles,
        };
    }

    private static int CountActiveMembers(
        ModBundleDefinition bundle,
        IReadOnlyList<ModBundleUnlockOverride> overrides)
    {
        var unlocked = overrides
            .Where(value => value.FamilyKey == bundle.FamilyKey)
            .Select(value => value.UniqueId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return bundle.Members.Count(member => !unlocked.Contains(member.UniqueId));
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

internal sealed record PreparedModLibraryItem(ModLibraryItem Item, string Directory, string RootPath = "");

internal sealed record ModFileMutationJournal(
    string Schema,
    string TransactionId,
    string Phase,
    string LibraryItemId,
    string RelativePath,
    bool HadOriginal)
{
    public const string CurrentSchema = "junimogate-mod-file-mutation/v1";

    public void Validate()
    {
        if (Schema != CurrentSchema || !Guid.TryParseExact(TransactionId, "N", out _) ||
            Phase is not ("prepared" or "committed") || !ModLibraryItemId.IsValid(LibraryItemId) ||
            !SafeArchivePath.TryParse(RelativePath, out var parsed) || parsed.Value != RelativePath)
        {
            throw new InvalidDataException("A Mod file mutation journal is malformed.");
        }
    }
}

internal static class ModContentId
{
    public static bool IsValid(string? value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class ModLibraryItemId
{
    public static string Create() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static bool IsValid(string? value) => ModContentId.IsValid(value);
}
