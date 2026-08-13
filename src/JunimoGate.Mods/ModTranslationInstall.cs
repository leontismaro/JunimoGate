using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JunimoGate.Mods;

public sealed record ModTranslationTarget(
    string LibraryItemId,
    string UniqueId,
    string DisplayName,
    string? OriginalRootPath)
{
    public static ModTranslationTarget FromLibraryItem(ModLibraryItem item, string? originalRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ModTranslationTarget(
            item.LibraryItemId,
            item.Manifest.UniqueId,
            item.Manifest.Name,
            originalRootPath ?? item.OriginalRootPath);
    }

    public void Validate()
    {
        if (!ModLibraryItemId.IsValid(LibraryItemId) || string.IsNullOrWhiteSpace(UniqueId) ||
            string.IsNullOrWhiteSpace(DisplayName) ||
            OriginalRootPath is { Length: > 0 } root && !SafeArchivePath.TryParse(root, out _))
        {
            throw new InvalidDataException("A Mod translation target is malformed.");
        }
    }
}

public enum ModTranslationFileAction
{
    Add,
    Replace,
}

public sealed record ModTranslationFilePlan(
    string SourcePath,
    string LibraryItemId,
    string TargetPath,
    ModTranslationFileAction Action,
    long Length,
    string? ExpectedTargetSha256 = null);

public sealed record ModTranslationIssue(
    ModArchiveIssueSeverity Severity,
    string Code,
    string? Path = null,
    string? Detail = null);

public sealed record ModTranslationLocaleDiagnostic(
    string LibraryItemId,
    string TargetPath,
    int MatchingKeys,
    int MissingKeys,
    int UnknownKeys);

public sealed record ModTranslationScanResult(
    IReadOnlyList<ModTranslationFilePlan> Files,
    IReadOnlyList<string> UnmappedFiles,
    IReadOnlyList<ModTranslationIssue> Issues,
    IReadOnlyList<ModTranslationLocaleDiagnostic> LocaleDiagnostics,
    long ArchiveBytes,
    long ExpandedBytes)
{
    public bool CanCommit => Files.Count > 0 && Issues.All(issue => issue.Severity != ModArchiveIssueSeverity.Error);
    public int AddedFiles => Files.Count(file => file.Action == ModTranslationFileAction.Add);
    public int ReplacedFiles => Files.Count(file => file.Action == ModTranslationFileAction.Replace);
}

public sealed record ModTranslationManualMapping(
    string SourcePrefix,
    string LibraryItemId,
    string TargetDirectory,
    IReadOnlyList<string> SourcePaths);

public sealed record ModTranslationInstallResult(
    string InstallationId,
    int AddedFiles,
    int ReplacedFiles,
    IReadOnlyList<string> AffectedLibraryItemIds);

public sealed record ModTranslationInstallationSummary(
    string InstallationId,
    DateTimeOffset InstalledAtUtc,
    string? SourceArchiveName,
    int FileCount,
    IReadOnlyList<string> AffectedLibraryItemIds);

public sealed record ModTranslationRestoreResult(
    string InstallationId,
    int RestoredFiles,
    IReadOnlyList<string> AffectedLibraryItemIds);

public sealed class ModTranslationInstallTransaction : IAsyncDisposable
{
    private readonly ModLibraryRepository repository;
    private readonly IReadOnlyList<ModTranslationTarget> targets;
    private readonly string? sourceArchiveName;
    private readonly ModArchiveImportLimits limits;
    private readonly string transactionId = Guid.NewGuid().ToString("N");
    private readonly string transactionDirectory;
    private readonly string archivePath;
    private readonly List<ModTranslationManualMapping> manualMappings = new();
    private bool disposed;

    internal ModTranslationInstallTransaction(
        ModLibraryRepository repository,
        IReadOnlyList<ModTranslationTarget> targets,
        string? sourceArchiveName,
        ModArchiveImportLimits limits)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || targets.Select(target => target.LibraryItemId).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new ArgumentException("At least one unique Mod translation target is required.", nameof(targets));
        foreach (var target in targets)
            target.Validate();
        this.targets = targets.ToArray();
        this.sourceArchiveName = NormalizeArchiveName(sourceArchiveName);
        this.limits = limits;
        limits.Validate();
        transactionDirectory = Path.Combine(repository.Layout.StagingDirectory, $"translation-{transactionId}");
        archivePath = Path.Combine(transactionDirectory, "archive.zip");
        State = ModInstallTransactionState.Created;
    }

    public ModInstallTransactionState State { get; private set; }
    public ModTranslationScanResult? ScanResult { get; private set; }
    public ModTranslationInstallResult? InstallResult { get; private set; }

    public async ValueTask ScanAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(archive);
        if (State != ModInstallTransactionState.Created)
            throw new InvalidOperationException("The translation archive transaction has already been scanned.");

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
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != ModInstallTransactionState.AwaitingConfirmation || ScanResult is null)
            throw new InvalidOperationException("The translation archive has not been scanned.");
        if (!ScanResult.CanCommit)
            throw new InvalidOperationException("The translation archive scan contains blocking errors.");

        State = ModInstallTransactionState.ExtractingToStaging;
        try
        {
            InstallResult = await repository.CommitTranslationAsync(
                    transactionId,
                    transactionDirectory,
                    archivePath,
                    sourceArchiveName,
                    ScanResult,
                    cancellationToken)
                .ConfigureAwait(false);
            State = ModInstallTransactionState.Committed;
        }
        catch
        {
            State = ModInstallTransactionState.Failed;
            throw;
        }
    }

    public async ValueTask MapUnmappedAsync(
        string? sourcePrefix,
        string libraryItemId,
        string? targetDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != ModInstallTransactionState.AwaitingConfirmation || ScanResult is null)
            throw new InvalidOperationException("The translation archive has not been scanned.");
        if (targets.All(target => target.LibraryItemId != libraryItemId))
            throw new ArgumentException("The translation mapping target is outside the selected Mod scope.", nameof(libraryItemId));

        var normalizedSource = NormalizeOptionalPath(sourcePrefix);
        var normalizedTarget = NormalizeOptionalPath(targetDirectory);
        var sources = ScanResult.UnmappedFiles
            .Where(path => normalizedSource.Length == 0 || IsUnderPrefix(normalizedSource, path))
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidOperationException("The selected translation source directory has no unmapped files.");

        manualMappings.Add(new ModTranslationManualMapping(
            normalizedSource,
            libraryItemId,
            normalizedTarget,
            sources));
        try
        {
            ScanResult = await ScanStoredArchiveAsync(ScanResult.ArchiveBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            manualMappings.RemoveAt(manualMappings.Count - 1);
            throw;
        }
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ModInstallTransactionState.Committed)
            throw new InvalidOperationException("A committed translation transaction cannot be rolled back.");
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
            archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
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
                throw new InvalidDataException("The translation archive exceeds the configured size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        return total;
    }

    private async ValueTask<ModTranslationScanResult> ScanStoredArchiveAsync(
        long archiveBytes,
        CancellationToken cancellationToken)
    {
        var inventories = await LoadTargetInventoriesAsync(cancellationToken).ConfigureAwait(false);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > limits.MaximumEntries)
            throw new InvalidDataException("The translation archive contains too many entries.");

        var issues = new List<ModTranslationIssue>();
        var entries = new List<TranslationArchiveEntry>();
        var pathKinds = new Dictionary<string, bool>(StringComparer.Ordinal);
        var manifests = new List<TranslationManifest>();
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeArchivePath.TryParse(entry.FullName, out var safePath, out var error))
            {
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "unsafe_path", entry.FullName, error));
                continue;
            }
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (pathKinds.TryGetValue(safePath.Value, out var existingDirectory))
            {
                if (isDirectory && existingDirectory)
                    continue;
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "duplicate_path", safePath.Value));
                continue;
            }
            pathKinds.Add(safePath.Value, isDirectory);
            if (IsSpecialEntry(entry))
            {
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "special_entry", safePath.Value));
                continue;
            }
            if (isDirectory)
                continue;
            if (Path.GetFileName(safePath.Value).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    manifests.Add(new TranslationManifest(
                        GetParent(safePath.Value),
                        await ReadManifestAsync(entry, cancellationToken).ConfigureAwait(false)));
                    issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Warning, "manifest_ignored", safePath.Value));
                }
                catch (InvalidDataException exception)
                {
                    issues.Add(new ModTranslationIssue(
                        ModArchiveIssueSeverity.Warning,
                        "invalid_manifest_ignored",
                        safePath.Value,
                        exception.Message));
                }
                continue;
            }
            if (IsProtected(safePath.Value))
            {
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Warning, "protected_file_ignored", safePath.Value));
                continue;
            }
            if (entry.Length > limits.MaximumSingleFileBytes)
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "single_file_too_large", safePath.Value));
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > limits.MaximumExpandedBytes)
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "expanded_archive_too_large"));
            if (entry.Length > 0 && (entry.CompressedLength == 0 ||
                                     entry.Length / (double)entry.CompressedLength > limits.MaximumCompressionRatio))
            {
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "compression_ratio_too_high", safePath.Value));
            }
            entries.Add(new TranslationArchiveEntry(entry, safePath.Value));
        }

        foreach (var manifest in manifests)
        {
            if (inventories.All(target => !target.Target.UniqueId.Equals(
                    manifest.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new ModTranslationIssue(
                    ModArchiveIssueSeverity.Error,
                    "manifest_target_mismatch",
                    manifest.RootPath,
                    manifest.Manifest.UniqueId));
            }
        }
        var mappings = DiscoverMappings(entries, inventories, manifests);
        var plans = new List<ModTranslationFilePlan>();
        var mappedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            foreach (var entry in entries.Where(entry => IsUnderPrefix(mapping.SourcePrefix, entry.Path)))
            {
                var relative = GetRelative(mapping.SourcePrefix, entry.Path);
                if (relative.Length == 0)
                    continue;
                var targetPath = CombineRelative(mapping.TargetDirectory, relative);
                if (!mappedSources.Add(entry.Path))
                {
                    issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "ambiguous_mapping", entry.Path));
                    continue;
                }
                var action = mapping.Target.Files.Contains(targetPath)
                    ? ModTranslationFileAction.Replace
                    : ModTranslationFileAction.Add;
                plans.Add(new ModTranslationFilePlan(
                    entry.Path,
                    mapping.Target.Target.LibraryItemId,
                    targetPath,
                    action,
                    entry.Entry.Length));
            }
        }

        AddFlatLocaleMappings(entries, inventories, mappedSources, plans, issues);
        ApplyManualMappings(entries, inventories, mappedSources, plans, issues);
        var duplicateTarget = plans
            .GroupBy(plan => $"{plan.LibraryItemId}\0{plan.TargetPath}", StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTarget is not null)
            issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "duplicate_target", duplicateTarget.First().TargetPath));

        var unmapped = entries.Where(entry => !mappedSources.Contains(entry.Path)).Select(entry => entry.Path).ToArray();
        if (unmapped.Length > 0)
            issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Warning, "unmapped_files", Detail: unmapped.Length.ToString()));
        var versionedPlans = await BindTargetVersionsAsync(plans, inventories, cancellationToken).ConfigureAwait(false);
        var diagnostics = await DiagnoseLocalesAsync(archive, versionedPlans, inventories, cancellationToken).ConfigureAwait(false);
        return new ModTranslationScanResult(
            versionedPlans.OrderBy(plan => plan.LibraryItemId, StringComparer.Ordinal)
                .ThenBy(plan => plan.TargetPath, StringComparer.Ordinal).ToArray(),
            unmapped,
            issues,
            diagnostics,
            archiveBytes,
            expandedBytes);
    }

    private async ValueTask<IReadOnlyList<TargetInventory>> LoadTargetInventoriesAsync(CancellationToken cancellationToken)
    {
        var index = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
        var known = index.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        var result = new List<TargetInventory>();
        foreach (var target in targets)
        {
            if (!known.TryGetValue(target.LibraryItemId, out var item) ||
                !item.Manifest.UniqueId.Equals(target.UniqueId, StringComparison.OrdinalIgnoreCase))
            {
                throw new KeyNotFoundException("A selected Mod translation target no longer exists.");
            }
            var root = repository.Layout.GetItemFilesDirectory(target.LibraryItemId);
            var files = EnumerateSafeRelativeFiles(root, cancellationToken);
            var directories = files.Select(GetParent).Where(path => path.Length > 0).ToHashSet(StringComparer.Ordinal);
            result.Add(new TargetInventory(target, root, files.ToHashSet(StringComparer.Ordinal), directories));
        }
        return result;
    }

    private static IReadOnlyList<DiscoveredMapping> DiscoverMappings(
        IReadOnlyList<TranslationArchiveEntry> entries,
        IReadOnlyList<TargetInventory> inventories,
        IReadOnlyList<TranslationManifest> manifests)
    {
        var candidates = new List<MappingCandidate>();
        var prefixes = entries.SelectMany(entry => GetDirectoryPrefixes(entry.Path))
            .Append(string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var target in inventories)
        {
            foreach (var prefix in prefixes)
            {
                var files = entries.Where(entry => IsUnderPrefix(prefix, entry.Path)).ToArray();
                if (files.Length == 0)
                    continue;
                var overlap = files.Count(entry => target.Files.Contains(GetRelative(prefix, entry.Path)));
                var directoryOverlap = files
                    .Select(entry => GetParent(GetRelative(prefix, entry.Path)))
                    .Where(path => path.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Count(target.Directories.Contains);
                var rootStrength = MatchRootStrength(prefix, target.Target.OriginalRootPath);
                if (manifests.Any(manifest => manifest.RootPath == prefix &&
                    manifest.Manifest.UniqueId.Equals(target.Target.UniqueId, StringComparison.OrdinalIgnoreCase)))
                {
                    rootStrength = 4;
                }
                if (rootStrength == 0 && overlap < 2 && directoryOverlap == 0)
                    continue;
                candidates.Add(new MappingCandidate(
                    prefix, target, string.Empty, rootStrength, overlap, directoryOverlap, files.Length));
            }
        }

        var selected = new List<DiscoveredMapping>();
        foreach (var sourceRoot in SelectIndependentSourceRoots(candidates))
        {
            var ranked = candidates.Where(candidate => candidate.SourcePrefix == sourceRoot)
                .OrderByDescending(candidate => candidate.RootStrength)
                .ThenByDescending(candidate => candidate.Overlap)
                .ThenByDescending(candidate => candidate.DirectoryOverlap)
                .ThenByDescending(candidate => candidate.SourcePrefix.Count(character => character == '/'))
                .ToArray();
            if (ranked.Length == 0)
                continue;
            var best = ranked[0];
            if (ranked.Skip(1).Any(candidate => candidate.RootStrength == best.RootStrength &&
                                                candidate.Overlap == best.Overlap &&
                                                candidate.DirectoryOverlap == best.DirectoryOverlap &&
                                                candidate.Target.Target.LibraryItemId != best.Target.Target.LibraryItemId))
            {
                continue;
            }
            selected.Add(new DiscoveredMapping(best.SourcePrefix, best.Target, best.TargetDirectory));
        }
        return selected;
    }

    private static IReadOnlyList<string> SelectIndependentSourceRoots(IReadOnlyList<MappingCandidate> candidates)
    {
        var strong = candidates.Where(candidate => candidate.RootStrength > 0)
            .Select(candidate => candidate.SourcePrefix)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(prefix => prefix.Length)
            .ToList();
        foreach (var structural in candidates.Where(candidate => candidate.RootStrength == 0)
                     .OrderByDescending(candidate => candidate.Overlap)
                     .ThenByDescending(candidate => candidate.DirectoryOverlap)
                     .ThenByDescending(candidate => candidate.SourcePrefix.Length))
        {
            if (strong.Any(prefix => prefix == structural.SourcePrefix ||
                                     IsUnderPrefix(prefix, structural.SourcePrefix) ||
                                     IsUnderPrefix(structural.SourcePrefix, prefix)))
                continue;
            strong.Add(structural.SourcePrefix);
        }
        return strong;
    }

    private static void AddFlatLocaleMappings(
        IReadOnlyList<TranslationArchiveEntry> entries,
        IReadOnlyList<TargetInventory> inventories,
        ISet<string> mappedSources,
        ICollection<ModTranslationFilePlan> plans,
        ICollection<ModTranslationIssue> issues)
    {
        foreach (var entry in entries.Where(entry => !mappedSources.Contains(entry.Path) && !entry.Path.Contains('/')))
        {
            if (!IsLocaleFileName(entry.Path))
                continue;
            var targets = inventories.Where(target => target.Directories.Contains("i18n")).ToArray();
            if (targets.Length != 1)
            {
                if (targets.Length > 1)
                    issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Warning, "ambiguous_locale_target", entry.Path));
                continue;
            }
            var targetPath = $"i18n/{entry.Path}";
            var target = targets[0];
            mappedSources.Add(entry.Path);
            plans.Add(new ModTranslationFilePlan(
                entry.Path,
                target.Target.LibraryItemId,
                targetPath,
                target.Files.Contains(targetPath) ? ModTranslationFileAction.Replace : ModTranslationFileAction.Add,
                entry.Entry.Length));
        }
    }

    private void ApplyManualMappings(
        IReadOnlyList<TranslationArchiveEntry> entries,
        IReadOnlyList<TargetInventory> inventories,
        ISet<string> mappedSources,
        ICollection<ModTranslationFilePlan> plans,
        ICollection<ModTranslationIssue> issues)
    {
        var entryMap = entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        foreach (var mapping in manualMappings)
        {
            var target = inventories.SingleOrDefault(value => value.Target.LibraryItemId == mapping.LibraryItemId);
            if (target is null)
            {
                issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "manual_target_missing", mapping.LibraryItemId));
                continue;
            }
            foreach (var sourcePath in mapping.SourcePaths)
            {
                if (!entryMap.TryGetValue(sourcePath, out var entry) || mappedSources.Contains(sourcePath))
                    continue;
                var relative = mapping.SourcePrefix.Length == 0
                    ? sourcePath
                    : GetRelative(mapping.SourcePrefix, sourcePath);
                var targetPath = CombineRelative(mapping.TargetDirectory, relative);
                if (IsProtected(targetPath))
                {
                    issues.Add(new ModTranslationIssue(ModArchiveIssueSeverity.Error, "protected_target", targetPath));
                    continue;
                }
                mappedSources.Add(sourcePath);
                plans.Add(new ModTranslationFilePlan(
                    sourcePath,
                    mapping.LibraryItemId,
                    targetPath,
                    target.Files.Contains(targetPath) ? ModTranslationFileAction.Replace : ModTranslationFileAction.Add,
                    entry.Entry.Length));
            }
        }
    }

    private static async ValueTask<IReadOnlyList<ModTranslationLocaleDiagnostic>> DiagnoseLocalesAsync(
        ZipArchive archive,
        IReadOnlyList<ModTranslationFilePlan> plans,
        IReadOnlyList<TargetInventory> inventories,
        CancellationToken cancellationToken)
    {
        var result = new List<ModTranslationLocaleDiagnostic>();
        var entries = BuildArchiveEntryMap(archive);
        foreach (var plan in plans.Where(plan => GetParent(plan.TargetPath).Equals("i18n", StringComparison.OrdinalIgnoreCase) &&
                                                 IsLocaleFileName(Path.GetFileName(plan.TargetPath))))
        {
            var target = inventories.Single(value => value.Target.LibraryItemId == plan.LibraryItemId);
            var baselinePath = Path.Combine(target.Root, "i18n", "default.json");
            if (!File.Exists(baselinePath) || !entries.TryGetValue(plan.SourcePath, out var entry))
                continue;
            try
            {
                var baseline = await ReadJsonKeysAsync(baselinePath, cancellationToken).ConfigureAwait(false);
                var translated = await ReadJsonKeysAsync(entry, cancellationToken).ConfigureAwait(false);
                result.Add(new ModTranslationLocaleDiagnostic(
                    plan.LibraryItemId,
                    plan.TargetPath,
                    baseline.Intersect(translated, StringComparer.Ordinal).Count(),
                    baseline.Except(translated, StringComparer.Ordinal).Count(),
                    translated.Except(baseline, StringComparer.Ordinal).Count()));
            }
            catch (InvalidDataException)
            {
                // Some Mods use non-JSON locale formats despite the extension; mapping remains valid.
            }
        }
        return result;
    }

    private static async ValueTask<IReadOnlyList<ModTranslationFilePlan>> BindTargetVersionsAsync(
        IReadOnlyList<ModTranslationFilePlan> plans,
        IReadOnlyList<TargetInventory> inventories,
        CancellationToken cancellationToken)
    {
        var result = new List<ModTranslationFilePlan>(plans.Count);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Action == ModTranslationFileAction.Add)
            {
                result.Add(plan);
                continue;
            }
            var target = inventories.Single(value => value.Target.LibraryItemId == plan.LibraryItemId);
            var path = ModLibraryRepository.ResolveContained(target.Root, plan.TargetPath);
            if (!File.Exists(path))
                throw new InvalidDataException("A translation target changed while scanning.");
            result.Add(plan with
            {
                ExpectedTargetSha256 = await ModLibraryRepository.HashFileAsync(path, cancellationToken).ConfigureAwait(false),
            });
        }
        return result;
    }

    private static async ValueTask<HashSet<string>> ReadJsonKeysAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseJsonKeys(bytes);
    }

    private static async ValueTask<HashSet<string>> ReadJsonKeysAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("A locale file is too large for diagnostics.");
        await using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return ParseJsonKeys(memory.ToArray());
    }

    private static HashSet<string> ParseJsonKeys(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A locale JSON file is not an object.");
            return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A locale JSON file is malformed.", exception);
        }
    }

    private static IReadOnlyList<string> EnumerateSafeRelativeFiles(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(normalizedRoot))
            throw new DirectoryNotFoundException("A selected Mod directory is missing.");
        var directories = new Stack<string>();
        directories.Push(normalizedRoot);
        var files = new List<string>();
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A selected Mod contains an unsupported symbolic link.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(path);
                    continue;
                }
                var relative = Path.GetRelativePath(normalizedRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                files.Add(SafeArchivePath.Parse(relative).Value);
            }
        }
        return files;
    }

    private static int MatchRootStrength(string sourcePrefix, string? originalRootPath)
    {
        if (string.IsNullOrEmpty(originalRootPath))
            return 0;
        if (sourcePrefix.Equals(originalRootPath, StringComparison.OrdinalIgnoreCase))
            return 3;
        var sourceSegments = sourcePrefix.Split('/');
        var rootSegments = originalRootPath.Split('/');
        if (sourceSegments.Length >= rootSegments.Length &&
            sourceSegments[^rootSegments.Length..].SequenceEqual(rootSegments, StringComparer.OrdinalIgnoreCase))
            return 2;
        return sourceSegments[^1].Equals(rootSegments[^1], StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static IEnumerable<string> GetDirectoryPrefixes(string path)
    {
        for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
            yield return path[..index];
    }

    private static bool IsUnderPrefix(string prefix, string path) =>
        prefix.Length == 0 || path.Length > prefix.Length && path.StartsWith(prefix + '/', StringComparison.Ordinal);

    private static string GetRelative(string prefix, string path) => prefix.Length == 0 ? path : path[(prefix.Length + 1)..];
    private static string GetParent(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : string.Empty;

    private static string CombineRelative(string directory, string path) =>
        directory.Length == 0 ? SafeArchivePath.Parse(path).Value : SafeArchivePath.Parse($"{directory}/{path}").Value;

    private static bool IsLocaleFileName(string name)
    {
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Equals("default", StringComparison.OrdinalIgnoreCase) ||
               stem.Length is >= 2 and <= 16 && stem.All(character => char.IsLetter(character) || character is '-' or '_');
    }

    private static bool IsProtected(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            return true;
        return Path.GetExtension(name).ToLowerInvariant() is ".apk" or ".dll" or ".dex" or ".exe" or ".pdb" or ".so" or ".zip";
    }

    private static bool IsSpecialEntry(ZipArchiveEntry entry)
    {
        const int fileTypeMask = 0xF000;
        const int regularFile = 0x8000;
        const int directory = 0x4000;
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var fileType = unixMode & fileTypeMask;
        return fileType != 0 && fileType is not regularFile and not directory;
    }

    private async ValueTask<ModManifestSummary> ReadManifestAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length is < 2 or > int.MaxValue || entry.Length > limits.MaximumManifestBytes)
            throw new InvalidDataException("A translation manifest has an invalid size.");
        await using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        if (memory.Length != entry.Length)
            throw new InvalidDataException("A translation manifest changed while reading.");
        return ModImportUtilities.ParseManifest(memory.ToArray());
    }

    private static IReadOnlyDictionary<string, ZipArchiveEntry> BuildArchiveEntryMap(ZipArchive archive)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;
            if (!SafeArchivePath.TryParse(entry.FullName, out var path) || !result.TryAdd(path.Value, entry))
                throw new InvalidDataException("The translation archive contains an invalid or duplicate path.");
        }
        return result;
    }

    private static string? NormalizeArchiveName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return normalized.Length <= 255 ? normalized : normalized[..255];
    }

    private static string NormalizeOptionalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "/")
            return string.Empty;
        var normalized = value.Trim().TrimEnd('/', '\\');
        return SafeArchivePath.Parse(normalized).Value;
    }

    private sealed record TranslationArchiveEntry(ZipArchiveEntry Entry, string Path);
    private sealed record TranslationManifest(string RootPath, ModManifestSummary Manifest);
    private sealed record TargetInventory(
        ModTranslationTarget Target,
        string Root,
        IReadOnlySet<string> Files,
        IReadOnlySet<string> Directories);
    private sealed record MappingCandidate(
        string SourcePrefix,
        TargetInventory Target,
        string TargetDirectory,
        int RootStrength,
        int Overlap,
        int DirectoryOverlap,
        int FileCount);
    private sealed record DiscoveredMapping(string SourcePrefix, TargetInventory Target, string TargetDirectory);
}

internal sealed record ModTranslationTransactionJournal(
    string Schema,
    string TransactionId,
    string Phase,
    IReadOnlyList<string> LibraryItemIds,
    string? RemovedInstallationId = null)
{
    public const string CurrentSchema = "junimogate-mod-translation-transaction/v1";
}

internal sealed record ModTranslationInstallationRecord(
    string Schema,
    string InstallationId,
    DateTimeOffset InstalledAtUtc,
    string? SourceArchiveName,
    IReadOnlyList<ModTranslationInstalledFile> Files)
{
    public const string CurrentSchema = "junimogate-mod-translation/v1";
}

internal sealed record ModTranslationInstalledFile(
    string LibraryItemId,
    string RelativePath,
    bool Replaced,
    string InstalledSha256,
    string? BackupRelativePath);

public sealed partial class ModLibraryRepository
{
    public async ValueTask<IReadOnlyList<ModTranslationInstallationSummary>> ListTranslationInstallationsAsync(
        IReadOnlyCollection<string>? libraryItemIds = null,
        CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var filter = libraryItemIds?.ToHashSet(StringComparer.Ordinal);
            var result = new List<ModTranslationInstallationSummary>();
            foreach (var directory in Directory.EnumerateDirectories(Layout.TranslationsDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await ReadInstallationRecordAsync(directory, cancellationToken).ConfigureAwait(false);
                var affected = record.Files.Select(file => file.LibraryItemId).Distinct(StringComparer.Ordinal).ToArray();
                if (filter is not null && !affected.Any(filter.Contains))
                    continue;
                result.Add(new ModTranslationInstallationSummary(
                    record.InstallationId,
                    record.InstalledAtUtc,
                    record.SourceArchiveName,
                    record.Files.Count,
                    affected));
            }
            return result.OrderByDescending(value => value.InstalledAtUtc).ToArray();
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<ModTranslationRestoreResult> RestoreTranslationAsync(
        string installationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(installationId, "N", out _))
            throw new ArgumentException("The translation installation ID is invalid.", nameof(installationId));

        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var installationDirectory = Path.Combine(Layout.TranslationsDirectory, installationId);
            if (!Directory.Exists(installationDirectory))
                throw new KeyNotFoundException("The translation installation does not exist.");
            var record = await ReadInstallationRecordAsync(installationDirectory, cancellationToken).ConfigureAwait(false);
            var affected = record.Files.Select(file => file.LibraryItemId).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var file in record.Files)
            {
                var live = ResolveContained(Layout.GetItemFilesDirectory(file.LibraryItemId), file.RelativePath);
                if (!File.Exists(live) ||
                    await HashFileAsync(live, cancellationToken).ConfigureAwait(false) != file.InstalledSha256)
                {
                    throw new InvalidOperationException("A translated Mod file changed after installation.");
                }
            }

            var transactionId = Guid.NewGuid().ToString("N");
            var transactionDirectory = Path.Combine(Layout.StagingDirectory, $"translation-{transactionId}");
            Directory.CreateDirectory(transactionDirectory);
            foreach (var itemId in affected)
            {
                var live = Layout.GetItemFilesDirectory(itemId);
                var staged = Path.Combine(transactionDirectory, $"{itemId}-new");
                CopyDirectory(live, staged, cancellationToken);
                foreach (var file in record.Files.Where(value => value.LibraryItemId == itemId))
                {
                    var destination = ResolveContained(staged, file.RelativePath);
                    if (file.Replaced)
                    {
                        if (file.BackupRelativePath is null)
                            throw new InvalidDataException("A translated file backup reference is missing.");
                        var backup = ResolveContained(installationDirectory, file.BackupRelativePath);
                        if (!File.Exists(backup))
                            throw new InvalidDataException("A translated file backup is missing.");
                        File.Copy(backup, destination, overwrite: true);
                    }
                    else
                    {
                        File.Delete(destination);
                        RemoveEmptyParents(Path.GetDirectoryName(destination)!, staged);
                    }
                }
            }

            var journalPath = Path.Combine(transactionDirectory, "transaction.json");
            await WriteJsonDurableAsync(
                    journalPath,
                    new ModTranslationTransactionJournal(
                        ModTranslationTransactionJournal.CurrentSchema,
                        transactionId,
                        "prepared",
                        affected,
                        installationId),
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                foreach (var itemId in affected)
                {
                    var live = Layout.GetItemFilesDirectory(itemId);
                    Directory.Move(live, Path.Combine(transactionDirectory, $"{itemId}-old"));
                    Directory.Move(Path.Combine(transactionDirectory, $"{itemId}-new"), live);
                }
                Directory.Move(installationDirectory, Path.Combine(transactionDirectory, "removed-record"));
                await WriteJsonDurableAsync(
                        journalPath,
                        new ModTranslationTransactionJournal(
                            ModTranslationTransactionJournal.CurrentSchema,
                            transactionId,
                            "committed",
                            affected,
                            installationId),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                RecoverTranslationTransaction(transactionDirectory);
                throw;
            }
            TryDeleteDirectory(transactionDirectory);
            return new ModTranslationRestoreResult(installationId, record.Files.Count, affected);
        }
        finally
        {
            operationLock.Release();
        }
    }

    internal async ValueTask<ModTranslationInstallResult> CommitTranslationAsync(
        string transactionId,
        string transactionDirectory,
        string archivePath,
        string? sourceArchiveName,
        ModTranslationScanResult scan,
        CancellationToken cancellationToken)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireProcessOperationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureDirectories();
            var current = await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var known = current.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
            var affected = scan.Files.Select(file => file.LibraryItemId).Distinct(StringComparer.Ordinal).ToArray();
            if (affected.Any(id => !known.ContainsKey(id)))
                throw new KeyNotFoundException("A translation target no longer exists.");

            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/'))
                .ToDictionary(entry => SafeArchivePath.Parse(entry.FullName).Value, StringComparer.Ordinal);
            var recordDirectory = Path.Combine(transactionDirectory, "record");
            var recordBackups = Path.Combine(recordDirectory, "backups");
            Directory.CreateDirectory(recordBackups);
            var installedFiles = new List<ModTranslationInstalledFile>();
            foreach (var itemId in affected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var live = Layout.GetItemFilesDirectory(itemId);
                var staged = Path.Combine(transactionDirectory, $"{itemId}-new");
                CopyDirectory(live, staged, cancellationToken);
                foreach (var plan in scan.Files.Where(file => file.LibraryItemId == itemId))
                {
                    var destination = ResolveContained(staged, plan.TargetPath);
                    if (plan.Action == ModTranslationFileAction.Add && File.Exists(destination) ||
                        plan.Action == ModTranslationFileAction.Replace &&
                        (!File.Exists(destination) || plan.ExpectedTargetSha256 is null ||
                         await HashFileAsync(destination, cancellationToken).ConfigureAwait(false) != plan.ExpectedTargetSha256))
                    {
                        throw new InvalidOperationException("A translation target changed after scanning.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    string? backupRelative = null;
                    if (plan.Action == ModTranslationFileAction.Replace)
                    {
                        backupRelative = $"backups/{itemId}/{plan.TargetPath}";
                        var backup = ResolveContained(recordDirectory, backupRelative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(destination, backup, overwrite: false);
                    }
                    if (!entries.TryGetValue(plan.SourcePath, out var entry))
                        throw new InvalidDataException("The translation archive changed after scanning.");
                    await ExtractEntryAsync(entry, destination, plan.Length, cancellationToken).ConfigureAwait(false);
                    installedFiles.Add(new ModTranslationInstalledFile(
                        itemId,
                        plan.TargetPath,
                        plan.Action == ModTranslationFileAction.Replace,
                        await HashFileAsync(destination, cancellationToken).ConfigureAwait(false),
                        backupRelative));
                }
            }

            var record = new ModTranslationInstallationRecord(
                ModTranslationInstallationRecord.CurrentSchema,
                transactionId,
                DateTimeOffset.UtcNow,
                sourceArchiveName,
                installedFiles);
            await WriteJsonDurableAsync(Path.Combine(recordDirectory, "installation.json"), record, cancellationToken)
                .ConfigureAwait(false);
            var journalPath = Path.Combine(transactionDirectory, "transaction.json");
            await WriteJsonDurableAsync(
                    journalPath,
                    new ModTranslationTransactionJournal(
                        ModTranslationTransactionJournal.CurrentSchema, transactionId, "prepared", affected),
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                foreach (var itemId in affected)
                {
                    var live = Layout.GetItemFilesDirectory(itemId);
                    var old = Path.Combine(transactionDirectory, $"{itemId}-old");
                    var staged = Path.Combine(transactionDirectory, $"{itemId}-new");
                    Directory.Move(live, old);
                    Directory.Move(staged, live);
                }
                await WriteJsonDurableAsync(
                        journalPath,
                        new ModTranslationTransactionJournal(
                            ModTranslationTransactionJournal.CurrentSchema, transactionId, "committed", affected),
                        cancellationToken)
                    .ConfigureAwait(false);
                Directory.Move(recordDirectory, Path.Combine(Layout.TranslationsDirectory, transactionId));
            }
            catch
            {
                RecoverTranslationTransaction(transactionDirectory);
                throw;
            }

            ModLibraryRepository.TryDeleteDirectory(transactionDirectory);
            return new ModTranslationInstallResult(
                transactionId,
                scan.AddedFiles,
                scan.ReplacedFiles,
                affected);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private void RecoverTranslationTransactions()
    {
        if (!Directory.Exists(Layout.StagingDirectory))
            return;
        foreach (var directory in Directory.EnumerateDirectories(Layout.StagingDirectory, "translation-*"))
        {
            if (File.Exists(Path.Combine(directory, "transaction.json")))
                RecoverTranslationTransaction(directory);
        }
    }

    private void RecoverTranslationTransaction(string transactionDirectory)
    {
        ModTranslationTransactionJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<ModTranslationTransactionJournal>(
                          File.ReadAllBytes(Path.Combine(transactionDirectory, "transaction.json")),
                          SerializerOptions)
                      ?? throw new InvalidDataException("A translation transaction journal is empty.");
            if (journal.Schema != ModTranslationTransactionJournal.CurrentSchema)
                throw new InvalidDataException("A translation transaction journal is unsupported.");
            if (journal.RemovedInstallationId is { } installationId &&
                !Guid.TryParseExact(installationId, "N", out _))
            {
                throw new InvalidDataException("A translation transaction journal is malformed.");
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return;
        }

        if (journal.Phase == "committed")
        {
            var record = Path.Combine(transactionDirectory, "record");
            var destination = Path.Combine(Layout.TranslationsDirectory, journal.TransactionId);
            if (Directory.Exists(record) && !Directory.Exists(destination))
                Directory.Move(record, destination);
            TryDeleteDirectory(transactionDirectory);
            return;
        }

        foreach (var itemId in journal.LibraryItemIds.Reverse())
        {
            var live = Layout.GetItemFilesDirectory(itemId);
            var old = Path.Combine(transactionDirectory, $"{itemId}-old");
            if (!Directory.Exists(old))
                continue;
            if (Directory.Exists(live))
                TryDeleteDirectory(live);
            if (!Directory.Exists(live))
                Directory.Move(old, live);
        }
        if (journal.RemovedInstallationId is { } removedInstallationId)
        {
            var removedRecord = Path.Combine(transactionDirectory, "removed-record");
            var installation = Path.Combine(Layout.TranslationsDirectory, removedInstallationId);
            if (Directory.Exists(removedRecord) && !Directory.Exists(installation))
                Directory.Move(removedRecord, installation);
        }
        TryDeleteDirectory(transactionDirectory);
    }

    private static async ValueTask ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            written = checked(written + read);
            if (written > expectedLength)
                throw new InvalidDataException("A translation entry changed while extracting.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (written != expectedLength)
            throw new InvalidDataException("A translation entry changed while extracting.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        var sourceInfo = new DirectoryInfo(source);
        if (!sourceInfo.Exists)
            throw new DirectoryNotFoundException("A Mod directory is missing.");
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A Mod directory contains an unsupported symbolic link.");

        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            Directory.CreateDirectory(current.Destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A Mod directory contains an unsupported symbolic link.");
                var target = Path.Combine(current.Destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push((entry, target));
                else
                    File.Copy(entry, target, overwrite: false);
            }
        }
    }

    internal static string ResolveContained(string root, string relative)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, SafeArchivePath.Parse(relative).Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A translation path escaped its target directory.");
        return path;
    }

    internal static async ValueTask<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask WriteJsonDurableAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async ValueTask<ModTranslationInstallationRecord> ReadInstallationRecordAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "installation.json");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 2 or > 8 * 1024 * 1024)
            throw new InvalidDataException("A translation installation record is missing or invalid.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var record = await JsonSerializer.DeserializeAsync<ModTranslationInstallationRecord>(
                             stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                         ?? throw new InvalidDataException("A translation installation record is empty.");
            if (record.Schema != ModTranslationInstallationRecord.CurrentSchema ||
                record.InstallationId != Path.GetFileName(directory) || record.InstalledAtUtc == default ||
                record.Files is null || record.Files.Count == 0 || record.Files.Any(file =>
                    !ModLibraryItemId.IsValid(file.LibraryItemId) ||
                    !SafeArchivePath.TryParse(file.RelativePath, out _) ||
                    !ModContentId.IsValid(file.InstalledSha256) ||
                    file.Replaced != (file.BackupRelativePath is not null)))
            {
                throw new InvalidDataException("A translation installation record is malformed.");
            }
            return record;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A translation installation record is malformed.", exception);
        }
    }

    private static void RemoveEmptyParents(string directory, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = Path.GetFullPath(directory);
             current != normalizedRoot && current.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
             current = Path.GetDirectoryName(current)!)
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
                break;
            Directory.Delete(current);
        }
    }
}
