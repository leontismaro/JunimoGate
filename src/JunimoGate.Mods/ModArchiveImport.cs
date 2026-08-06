using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JunimoGate.Mods;

public sealed record ModArchiveImportLimits(
    long MaximumArchiveBytes,
    int MaximumEntries,
    long MaximumExpandedBytes,
    long MaximumSingleFileBytes,
    double MaximumCompressionRatio,
    int MaximumManifestBytes,
    int MaximumMods)
{
    public static ModArchiveImportLimits Default { get; } = new(
        MaximumArchiveBytes: 2L * 1024 * 1024 * 1024,
        MaximumEntries: 100_000,
        MaximumExpandedBytes: 8L * 1024 * 1024 * 1024,
        MaximumSingleFileBytes: 2L * 1024 * 1024 * 1024,
        MaximumCompressionRatio: 1_000,
        MaximumManifestBytes: 1024 * 1024,
        MaximumMods: 2_048);

    public void Validate()
    {
        if (MaximumArchiveBytes < 1 || MaximumEntries < 1 || MaximumExpandedBytes < 1 ||
            MaximumSingleFileBytes < 1 || MaximumCompressionRatio < 1 || MaximumManifestBytes < 1 ||
            MaximumMods < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ModArchiveImportLimits));
        }
    }
}

public enum ModArchiveIssueSeverity
{
    Warning,
    Error,
}

public sealed record ModArchiveIssue(
    ModArchiveIssueSeverity Severity,
    string Code,
    string? Path = null,
    string? Detail = null);

public sealed record ModArchiveCandidate(
    string RootPath,
    ModManifestSummary Manifest,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> EntryPaths);

public sealed record ModArchiveScanResult(
    IReadOnlyList<ModArchiveCandidate> Candidates,
    IReadOnlyList<ModArchiveIssue> Issues,
    int IgnoredFileCount,
    long ArchiveBytes,
    long ExpandedBytes)
{
    public bool CanCommit => Candidates.Count > 0 && Issues.All(issue => issue.Severity != ModArchiveIssueSeverity.Error);
}

public sealed record ModArchiveImportResult(
    IReadOnlyList<ModLibraryItem> AddedItems,
    IReadOnlyList<ModLibraryItem> ReusedItems)
{
    public IReadOnlyList<ModLibraryItem> AllItems => AddedItems.Concat(ReusedItems).ToArray();
    public IReadOnlyList<ModBundleDefinition> Bundles { get; init; } = Array.Empty<ModBundleDefinition>();
}

public sealed class ModArchiveInstallTransaction : IModArchiveInstallTransaction
{
    private readonly ModLibraryRepository repository;
    private readonly string? sourceArchiveName;
    private readonly ModArchiveImportLimits limits;
    private readonly string transactionDirectory;
    private readonly string archivePath;
    private bool disposed;

    internal ModArchiveInstallTransaction(
        ModLibraryRepository repository,
        string? sourceArchiveName,
        ModArchiveImportLimits limits)
    {
        this.repository = repository;
        this.sourceArchiveName = NormalizeArchiveName(sourceArchiveName);
        this.limits = limits;
        limits.Validate();
        transactionDirectory = Path.Combine(repository.Layout.StagingDirectory, $"import-{Guid.NewGuid():N}");
        archivePath = Path.Combine(transactionDirectory, "archive.zip");
        State = ModInstallTransactionState.Created;
    }

    public ModInstallTransactionState State { get; private set; }
    public ModArchiveScanResult? ScanResult { get; private set; }
    public ModArchiveImportResult? ImportResult { get; private set; }
    internal string StoredArchivePath => archivePath;

    public async ValueTask ScanAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(archive);
        if (State != ModInstallTransactionState.Created)
            throw new InvalidOperationException("The Mod archive transaction has already been scanned.");

        State = ModInstallTransactionState.Scanning;
        try
        {
            Directory.CreateDirectory(transactionDirectory);
            var archiveBytes = await CopyArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
            ScanResult = await ScanStoredArchiveAsync(archiveBytes, cancellationToken).ConfigureAwait(false);
            State = ModInstallTransactionState.AwaitingConfirmation;
        }
        catch
        {
            State = ModInstallTransactionState.Failed;
            ModLibraryRepository.TryDeleteDirectory(transactionDirectory);
            throw;
        }
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        => await CommitCoreAsync(explicitBundles: null, ModBundleOrigin.Detected, cancellationToken)
            .ConfigureAwait(false);

    internal async ValueTask CommitAsync(
        IReadOnlyList<DetectedModBundle> explicitBundles,
        ModBundleOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(explicitBundles);
        await CommitCoreAsync(explicitBundles, origin, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CommitCoreAsync(
        IReadOnlyList<DetectedModBundle>? explicitBundles,
        ModBundleOrigin origin,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != ModInstallTransactionState.AwaitingConfirmation || ScanResult is null)
            throw new InvalidOperationException("The Mod archive has not been scanned.");
        if (!ScanResult.CanCommit)
            throw new InvalidOperationException("The Mod archive scan contains blocking errors.");

        State = ModInstallTransactionState.ExtractingToStaging;
        var prepared = new List<PreparedModLibraryItem>();
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = BuildEntryMap(archive);
            foreach (var candidate in ScanResult.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prepared.Add(await ExtractCandidateAsync(candidate, entries, cancellationToken).ConfigureAwait(false));
            }

            State = ModInstallTransactionState.Validated;
            var bundles = explicitBundles ?? ModBundleDetector.Detect(ScanResult.Candidates).Bundles;
            ImportResult = await repository.CommitAsync(prepared, cancellationToken, bundles, origin)
                .ConfigureAwait(false);
            State = ModInstallTransactionState.Committed;
            ModLibraryRepository.TryDeleteDirectory(transactionDirectory);
        }
        catch
        {
            foreach (var item in prepared)
                ModLibraryRepository.TryDeleteDirectory(item.Directory);
            State = ModInstallTransactionState.Failed;
            ModLibraryRepository.TryDeleteDirectory(transactionDirectory);
            throw;
        }
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ModInstallTransactionState.Committed)
            throw new InvalidOperationException("A committed Mod archive transaction cannot be rolled back.");
        ModLibraryRepository.TryDeleteDirectory(transactionDirectory);
        State = ModInstallTransactionState.RolledBack;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            if (State != ModInstallTransactionState.Committed)
                ModLibraryRepository.TryDeleteDirectory(transactionDirectory);
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask<long> CopyArchiveAsync(Stream source, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > limits.MaximumArchiveBytes)
                throw new InvalidDataException("The Mod archive exceeds the configured size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return total;
    }

    private async ValueTask<ModArchiveScanResult> ScanStoredArchiveAsync(
        long archiveBytes,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > limits.MaximumEntries)
            throw new InvalidDataException("The Mod archive contains too many entries.");

        var issues = new List<ModArchiveIssue>();
        var normalizedEntries = new List<ScannedArchiveEntry>();
        var pathKinds = new Dictionary<string, bool>(StringComparer.Ordinal);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeArchivePath.TryParse(entry.FullName, out var safePath, out var error))
            {
                issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "unsafe_path", entry.FullName, error));
                continue;
            }
            var isDirectory = IsDirectory(entry);
            if (pathKinds.TryGetValue(safePath.Value, out var existingIsDirectory))
            {
                if (isDirectory && existingIsDirectory)
                    continue;
                issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "duplicate_path", safePath.Value));
                continue;
            }
            pathKinds.Add(safePath.Value, isDirectory);
            if (IsSpecialEntry(entry))
            {
                issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "special_entry", safePath.Value));
                continue;
            }

            if (!isDirectory)
            {
                if (entry.Length > limits.MaximumSingleFileBytes)
                    issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "single_file_too_large", safePath.Value));
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > limits.MaximumExpandedBytes)
                    issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "expanded_archive_too_large"));
                if (entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio))
                    issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "compression_ratio_too_high", safePath.Value));
            }

            normalizedEntries.Add(new ScannedArchiveEntry(entry, safePath.Value, isDirectory));
        }

        var manifestEntries = normalizedEntries
            .Where(entry => !entry.IsDirectory && string.Equals(GetFileName(entry.Path), "manifest.json", StringComparison.Ordinal))
            .ToArray();
        if (manifestEntries.Length == 0)
            issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "manifest_not_found"));
        if (manifestEntries.Length > limits.MaximumMods)
            issues.Add(new ModArchiveIssue(ModArchiveIssueSeverity.Error, "too_many_mods"));

        var roots = manifestEntries.Select(entry => GetParentPath(entry.Path)).ToArray();
        for (var first = 0; first < roots.Length; first++)
        {
            for (var second = first + 1; second < roots.Length; second++)
            {
                if (IsSameOrChild(roots[first], roots[second]) || IsSameOrChild(roots[second], roots[first]))
                {
                    issues.Add(new ModArchiveIssue(
                        ModArchiveIssueSeverity.Error,
                        "overlapping_mod_roots",
                        roots[first],
                        roots[second]));
                }
            }
        }

        var candidates = new List<ModArchiveCandidate>();
        if (issues.All(issue => issue.Code != "overlapping_mod_roots"))
        {
            foreach (var manifestEntry in manifestEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = GetParentPath(manifestEntry.Path);
                try
                {
                    var manifest = await ReadManifestAsync(manifestEntry.Entry, cancellationToken).ConfigureAwait(false);
                    var files = normalizedEntries
                        .Where(entry => !entry.IsDirectory && IsSameOrChild(root, entry.Path))
                        .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                        .ToArray();
                    candidates.Add(new ModArchiveCandidate(
                        root,
                        manifest,
                        files.Length,
                        files.Sum(entry => entry.Entry.Length),
                        files.Select(entry => entry.Path).ToArray()));
                    if (manifest.EntryDll is null && manifest.ContentPackForUniqueId is null)
                    {
                        issues.Add(new ModArchiveIssue(
                            ModArchiveIssueSeverity.Warning,
                            "manifest_has_no_entrypoint",
                            manifestEntry.Path,
                            manifest.UniqueId));
                    }
                }
                catch (InvalidDataException exception)
                {
                    issues.Add(new ModArchiveIssue(
                        ModArchiveIssueSeverity.Error,
                        "invalid_manifest",
                        manifestEntry.Path,
                        exception.Message));
                }
            }
        }

        var ownedPaths = candidates.SelectMany(candidate => candidate.EntryPaths).ToHashSet(StringComparer.Ordinal);
        var ignored = normalizedEntries.Count(entry => !entry.IsDirectory && !ownedPaths.Contains(entry.Path));
        return new ModArchiveScanResult(candidates, issues, ignored, archiveBytes, expandedBytes);
    }

    private async ValueTask<PreparedModLibraryItem> ExtractCandidateAsync(
        ModArchiveCandidate candidate,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        var candidateDirectory = Path.Combine(transactionDirectory, $"item-{Guid.NewGuid():N}");
        var filesDirectory = Path.Combine(candidateDirectory, "files");
        Directory.CreateDirectory(filesDirectory);
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var archiveEntryPath in candidate.EntryPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(archiveEntryPath, out var entry))
                throw new InvalidDataException("The Mod archive changed after scanning.");
            var relative = GetRelativePath(candidate.RootPath, archiveEntryPath);
            if (!SafeArchivePath.TryParse(relative, out var safeRelative))
                throw new InvalidDataException("The Mod archive produced an unsafe relative path.");
            var destination = Path.GetFullPath(Path.Combine(filesDirectory, safeRelative.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsContained(filesDirectory, destination))
                throw new InvalidDataException("The Mod archive extraction path escaped staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            ModImportUtilities.AppendPathHeader(contentHash, safeRelative.Value, entry.Length);
            await using var source = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                written = checked(written + read);
                totalBytes = checked(totalBytes + read);
                if (written > limits.MaximumSingleFileBytes || totalBytes > limits.MaximumExpandedBytes)
                    throw new InvalidDataException("The Mod archive exceeded its extraction limits.");
                contentHash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (written != entry.Length)
                throw new InvalidDataException("A Mod archive entry length changed during extraction.");
            fileCount++;
        }

        var contentId = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
        var item = new ModLibraryItem(
            ModLibraryItem.CurrentSchema,
            contentId,
            contentId,
            candidate.Manifest,
            $"library/{contentId}/files",
            DateTimeOffset.UtcNow,
            sourceArchiveName,
            fileCount,
            totalBytes);
        item.Validate();
        var metadataPath = Path.Combine(candidateDirectory, "library-item.json");
        await using (var metadata = new FileStream(
                         metadataPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(
                    metadata,
                    item,
                    ModLibraryRepository.SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await metadata.FlushAsync(cancellationToken).ConfigureAwait(false);
            metadata.Flush(flushToDisk: true);
        }
        return new PreparedModLibraryItem(item, candidateDirectory, candidate.RootPath);
    }

    private async ValueTask<ModManifestSummary> ReadManifestAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length is < 2 || entry.Length > int.MaxValue || entry.Length > limits.MaximumManifestBytes)
            throw new InvalidDataException("The Mod manifest has an invalid size.");
        await using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        if (memory.Length != entry.Length || memory.Length > limits.MaximumManifestBytes)
            throw new InvalidDataException("The Mod manifest length changed while reading.");
        return ModImportUtilities.ParseManifest(memory.ToArray());
    }

    private static IReadOnlyDictionary<string, ZipArchiveEntry> BuildEntryMap(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!SafeArchivePath.TryParse(entry.FullName, out var path) || IsDirectory(entry))
                continue;
            if (!entries.TryAdd(path.Value, entry))
                throw new InvalidDataException("The Mod archive contains duplicate normalized paths.");
        }
        return entries;
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0x4000;

    private static bool IsSpecialEntry(ZipArchiveEntry entry)
    {
        var type = (entry.ExternalAttributes >> 16) & 0xF000;
        return type is not (0 or 0x4000 or 0x8000);
    }

    private static string GetFileName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string GetParentPath(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static bool IsSameOrChild(string root, string path) =>
        root.Length == 0 || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string GetRelativePath(string root, string path) =>
        root.Length == 0 ? path : path[(root.Length + 1)..];

    private static bool IsContained(string root, string path) => ModImportUtilities.IsContained(root, path);

    private static string? NormalizeArchiveName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var fileName = Path.GetFileName(value.Trim());
        return fileName.Length <= 255 ? fileName : fileName[..255];
    }

    private sealed record ScannedArchiveEntry(ZipArchiveEntry Entry, string Path, bool IsDirectory);
}
