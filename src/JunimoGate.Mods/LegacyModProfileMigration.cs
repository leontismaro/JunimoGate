using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace JunimoGate.Mods;

public sealed record LegacyModProfileMigrationResult(
    ModProfileV2 Profile,
    int ImportedItems,
    int ReusedItems,
    int EnabledMembers,
    int DisabledMembers,
    bool AlreadyMigrated);

public sealed class LegacyModProfileMigrator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MigrationLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string profilesRoot;
    private readonly ModLibraryRepository library;
    private readonly ModProfileV2Repository profiles;
    private readonly ModArchiveImportLimits limits;

    public LegacyModProfileMigrator(
        string profilesRoot,
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        ModArchiveImportLimits? limits = null)
    {
        if (string.IsNullOrWhiteSpace(profilesRoot) || !Path.IsPathFullyQualified(profilesRoot))
            throw new ArgumentException("The profiles root must be absolute.", nameof(profilesRoot));
        this.profilesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilesRoot));
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.limits = limits ?? ModArchiveImportLimits.Default;
        this.limits.Validate();
    }

    public async ValueTask<LegacyModProfileMigrationResult> MigrateAsync(
        ProfileId profileId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var migrationLock = MigrationLocks.GetOrAdd(profilesRoot, static _ => new SemaphoreSlim(1, 1));
        await migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MigrateUnlockedAsync(profileId, displayName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            migrationLock.Release();
        }
    }

    private async ValueTask<LegacyModProfileMigrationResult> MigrateUnlockedAsync(
        ProfileId profileId,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            var migrated = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
            var migratedLayout = new ProfileLayout(profilesRoot, profileId);
            await ValidateMigratedProfileAsync(migrated, cancellationToken).ConfigureAwait(false);
            await DeleteLegacyDirectoriesAsync(migratedLayout, cancellationToken).ConfigureAwait(false);
            return new LegacyModProfileMigrationResult(
                migrated,
                ImportedItems: 0,
                ReusedItems: 0,
                migrated.Members.Count(member => member.Enabled),
                migrated.Members.Count(member => !member.Enabled),
                AlreadyMigrated: true);
        }
        catch (InvalidDataException)
        {
            // A v1 document is the expected migration source.
        }

        var legacy = await new ModProfileRepository(profilesRoot)
            .ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var layout = new ProfileLayout(profilesRoot, profileId);
        var candidates = new List<LegacyModDirectoryCandidate>();
        candidates.AddRange(await DiscoverAsync(layout.EnabledDirectory, enabled: true, cancellationToken)
            .ConfigureAwait(false));
        candidates.AddRange(await DiscoverAsync(layout.DisabledDirectory, enabled: false, cancellationToken)
            .ConfigureAwait(false));

        var duplicateEnabled = candidates
            .Where(candidate => candidate.Enabled)
            .GroupBy(candidate => candidate.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEnabled is not null)
            throw new InvalidDataException($"The legacy Profile enables multiple copies of {duplicateEnabled.Key}.");

        var transactionRoot = Path.Combine(library.Layout.StagingDirectory, $"legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transactionRoot);
        var prepared = new List<PreparedLegacyMod>();
        try
        {
            foreach (var candidate in candidates.OrderBy(candidate => candidate.RelativeSourcePath, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                prepared.Add(await StageAsync(profileId, candidate, transactionRoot, cancellationToken)
                    .ConfigureAwait(false));
            }

            var import = await library.CommitAsync(prepared.Select(item => item.Prepared).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            var members = candidates
                .GroupBy(candidate => candidate.Manifest.UniqueId, StringComparer.OrdinalIgnoreCase)
                .Select(group => SelectMember(group, prepared, import.ResolvedItemsByPreparedId))
                .OrderBy(member => member.ExpectedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.UniqueId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var now = DateTimeOffset.UtcNow;
            var migratedProfile = new ModProfileV2(
                ModProfileV2.CurrentSchema,
                profileId.Value,
                NormalizeDisplayName(displayName),
                legacy.Revision,
                legacy.AssemblyBindingPolicy,
                members,
                legacy.UpdatedAtUtc,
                now < legacy.UpdatedAtUtc ? legacy.UpdatedAtUtc : now,
                Description: null);
            var stored = await profiles.WriteMigratedAsync(migratedProfile, cancellationToken).ConfigureAwait(false);
            await ValidateMigratedProfileAsync(stored, cancellationToken).ConfigureAwait(false);
            await DeleteLegacyDirectoriesAsync(layout, cancellationToken).ConfigureAwait(false);
            return new LegacyModProfileMigrationResult(
                stored,
                import.AddedItems.Count,
                import.ReusedItems.Count,
                stored.Members.Count(member => member.Enabled),
                stored.Members.Count(member => !member.Enabled),
                AlreadyMigrated: false);
        }
        finally
        {
            ModLibraryRepository.TryDeleteDirectory(transactionRoot);
        }
    }

    private async ValueTask ValidateMigratedProfileAsync(
        ModProfileV2 migrated,
        CancellationToken cancellationToken)
    {
        var verified = await profiles.ReadAsync(ProfileId.Parse(migrated.Id), cancellationToken)
            .ConfigureAwait(false);
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        var known = index.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        foreach (var member in verified.Members)
        {
            if (member.LibraryItemId is not { } itemId)
                continue;
            if (!known.ContainsKey(itemId) || !Directory.Exists(library.Layout.GetItemFilesDirectory(itemId)))
            {
                throw new InvalidDataException("The migrated Profile references missing Mod content.");
            }
        }
    }

    public async ValueTask<IReadOnlyList<LegacyModProfileMigrationResult>> MigrateAllAsync(
        CancellationToken cancellationToken = default)
    {
        var migrationLock = MigrationLocks.GetOrAdd(profilesRoot, static _ => new SemaphoreSlim(1, 1));
        await migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(profilesRoot))
                return Array.Empty<LegacyModProfileMigrationResult>();
            var results = new List<LegacyModProfileMigrationResult>();
            foreach (var directory in Directory.EnumerateDirectories(profilesRoot)
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!ProfileId.TryParse(name, out var profileId) || profileId.Value == ModProfileV2.NoModsId ||
                    !File.Exists(Path.Combine(directory, "profile.json")))
                {
                    continue;
                }
                results.Add(await MigrateUnlockedAsync(
                        profileId,
                        profileId.Value == "default" ? "Default" : profileId.Value,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
            return results;
        }
        finally
        {
            migrationLock.Release();
        }
    }

    private static ValueTask DeleteLegacyDirectoriesAsync(ProfileLayout layout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var path in new[]
                 {
                     layout.ModsDirectory,
                     layout.DownloadsDirectory,
                     layout.StagingDirectory,
                 })
        {
            ModLibraryRepository.TryDeleteDirectory(path);
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask<IReadOnlyList<LegacyModDirectoryCandidate>> DiscoverAsync(
        string modsDirectory,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modsDirectory))
            return Array.Empty<LegacyModDirectoryCandidate>();
        var files = EnumerateSafeFiles(modsDirectory, cancellationToken);
        var manifests = files
            .Where(file => string.Equals(Path.GetFileName(file.FullPath), "manifest.json", StringComparison.Ordinal))
            .OrderBy(file => file.RelativePath.Value, StringComparer.Ordinal)
            .ToArray();
        if (manifests.Length > limits.MaximumMods)
            throw new InvalidDataException("The legacy Profile contains too many Mods.");
        var roots = manifests.Select(manifest => Path.GetDirectoryName(manifest.FullPath)!).ToArray();
        for (var first = 0; first < roots.Length; first++)
        {
            for (var second = first + 1; second < roots.Length; second++)
            {
                if (IsSameOrChild(roots[first], roots[second]) || IsSameOrChild(roots[second], roots[first]))
                    throw new InvalidDataException("The legacy Profile contains overlapping Mod roots.");
            }
        }

        var results = new List<LegacyModDirectoryCandidate>();
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manifest.Length is < 2 or > int.MaxValue || manifest.Length > limits.MaximumManifestBytes)
                throw new InvalidDataException("A legacy Mod manifest has an invalid size.");
            var bytes = await File.ReadAllBytesAsync(manifest.FullPath, cancellationToken).ConfigureAwait(false);
            var root = Path.GetDirectoryName(manifest.FullPath)!;
            results.Add(new LegacyModDirectoryCandidate(
                root,
                enabled,
                ModImportUtilities.ParseManifest(bytes),
                Path.GetRelativePath(modsDirectory, root).Replace(Path.DirectorySeparatorChar, '/')));
        }
        return results;
    }

    private async ValueTask<PreparedLegacyMod> StageAsync(
        ProfileId profileId,
        LegacyModDirectoryCandidate candidate,
        string transactionRoot,
        CancellationToken cancellationToken)
    {
        var files = EnumerateSafeFiles(candidate.RootDirectory, cancellationToken)
            .OrderBy(file => file.RelativePath.Value, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0 || files.Length > limits.MaximumEntries)
            throw new InvalidDataException("A legacy Mod contains an invalid file count.");
        var candidateDirectory = Path.Combine(transactionRoot, $"item-{Guid.NewGuid():N}");
        var filesDirectory = Path.Combine(candidateDirectory, "files");
        Directory.CreateDirectory(filesDirectory);
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long totalBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length > limits.MaximumSingleFileBytes)
                throw new InvalidDataException("A legacy Mod file exceeds the configured size limit.");
            totalBytes = checked(totalBytes + file.Length);
            if (totalBytes > limits.MaximumExpandedBytes)
                throw new InvalidDataException("A legacy Mod exceeds the configured expanded size limit.");
            var destination = Path.GetFullPath(Path.Combine(
                filesDirectory,
                file.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!ModImportUtilities.IsContained(filesDirectory, destination))
                throw new InvalidDataException("A legacy Mod path escaped staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            ModImportUtilities.AppendPathHeader(contentHash, file.RelativePath.Value, file.Length);
            await using var source = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
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
                if (written > file.Length)
                    throw new InvalidDataException("A legacy Mod file changed while being copied.");
                contentHash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (written != file.Length)
                throw new InvalidDataException("A legacy Mod file changed while being copied.");
        }

        var importedContentId = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
        var libraryItemId = ModLibraryItemId.Create();
        var item = new ModLibraryItem(
            ModLibraryItem.CurrentSchema,
            libraryItemId,
            importedContentId,
            candidate.Manifest,
            $"library/{libraryItemId}/files",
            DateTimeOffset.UtcNow,
            $"legacy-{profileId.Value}-{(candidate.Enabled ? "enabled" : "disabled")}",
            files.Length,
            totalBytes)
        {
            OriginalRootPath = candidate.RelativeSourcePath,
        };
        item.Validate();
        await using (var metadata = new FileStream(
                         Path.Combine(candidateDirectory, "library-item.json"),
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
        return new PreparedLegacyMod(candidate, new PreparedModLibraryItem(item, candidateDirectory, candidate.RelativeSourcePath));
    }

    private static ModProfileMember SelectMember(
        IGrouping<string, LegacyModDirectoryCandidate> group,
        IReadOnlyList<PreparedLegacyMod> prepared,
        IReadOnlyDictionary<string, ModLibraryItem> committed)
    {
        var selected = group.FirstOrDefault(candidate => candidate.Enabled)
                       ?? group.OrderBy(candidate => candidate.RelativeSourcePath, StringComparer.Ordinal).First();
        var staged = prepared.Single(item => ReferenceEquals(item.Candidate, selected)).Prepared.Item;
        var item = committed[staged.LibraryItemId];
        return ModProfileMember.FromLibraryItem(item, selected.Enabled);
    }

    private IReadOnlyList<LegacyFile> EnumerateSafeFiles(
        string root,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var directories = new Stack<string>();
        directories.Push(normalizedRoot);
        var files = new List<LegacyFile>();
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                if (!ModImportUtilities.IsContained(normalizedRoot, fullPath))
                    throw new InvalidDataException("A legacy Mod path escaped its root.");
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Legacy Mod symbolic links are not supported.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(fullPath);
                    continue;
                }
                var relative = Path.GetRelativePath(normalizedRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
                if (!SafeArchivePath.TryParse(relative, out var safePath))
                    throw new InvalidDataException("A legacy Mod contains an unsafe relative path.");
                var length = new FileInfo(fullPath).Length;
                files.Add(new LegacyFile(fullPath, safePath, length));
                if (files.Count > limits.MaximumEntries)
                    throw new InvalidDataException("A legacy Mod contains too many files.");
            }
        }
        return files;
    }

    private static bool IsSameOrChild(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return normalizedPath == normalizedRoot ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string NormalizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The migrated Profile display name is required.", nameof(value));
        var normalized = value.Trim();
        if (normalized.Length > 80)
            throw new ArgumentException("The migrated Profile display name is too long.", nameof(value));
        return normalized;
    }

    private sealed record LegacyModDirectoryCandidate(
        string RootDirectory,
        bool Enabled,
        ModManifestSummary Manifest,
        string RelativeSourcePath);

    private sealed record LegacyFile(string FullPath, SafeArchivePath RelativePath, long Length);
    private sealed record PreparedLegacyMod(
        LegacyModDirectoryCandidate Candidate,
        PreparedModLibraryItem Prepared);
}
