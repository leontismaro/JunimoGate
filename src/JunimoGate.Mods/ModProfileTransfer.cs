using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoGate.Mods;

public enum ModProfileTransferKind
{
    Manifest,
    Complete,
}

public sealed record ModProfileTransferMember(
    string UniqueId,
    string? SourceLibraryItemId,
    string? PackagedContentId,
    bool Enabled,
    string ExpectedName,
    string ExpectedVersion,
    string? ExpectedAuthor,
    DateTimeOffset AddedAtUtc)
{
    public void Validate(ModProfileTransferKind kind)
    {
        if (string.IsNullOrWhiteSpace(UniqueId) || UniqueId.Length > 256 ||
            SourceLibraryItemId is not null && !ModLibraryItemId.IsValid(SourceLibraryItemId) ||
            PackagedContentId is not null && !ModContentId.IsValid(PackagedContentId) ||
            kind == ModProfileTransferKind.Manifest && PackagedContentId is not null ||
            string.IsNullOrWhiteSpace(ExpectedName) || ExpectedName.Length > 256 ||
            string.IsNullOrWhiteSpace(ExpectedVersion) || ExpectedVersion.Length > 128 ||
            ExpectedAuthor?.Length > 256 || AddedAtUtc == default)
        {
            throw new InvalidDataException("The shared Mod Profile member is malformed.");
        }
    }
}

public sealed record ModProfileTransferBundleMember(
    string UniqueId,
    string? SourceLibraryItemId,
    string? PackagedContentId)
{
    public void Validate(ModProfileTransferKind kind)
    {
        if (string.IsNullOrWhiteSpace(UniqueId) || UniqueId.Length > 256 ||
            SourceLibraryItemId is not null && !ModLibraryItemId.IsValid(SourceLibraryItemId) ||
            PackagedContentId is not null && !ModContentId.IsValid(PackagedContentId) ||
            kind == ModProfileTransferKind.Manifest && PackagedContentId is not null ||
            kind == ModProfileTransferKind.Complete && PackagedContentId is null)
        {
            throw new InvalidDataException("A shared Mod bundle member is malformed.");
        }
    }
}

public sealed record ModProfileTransferBundle(
    string PortableBundleId,
    string FamilyKey,
    string DisplayName,
    IReadOnlyList<ModProfileTransferBundleMember> Members)
{
    public void Validate(
        ModProfileTransferKind kind,
        IReadOnlyDictionary<string, ModProfileTransferMember> documentMembers)
    {
        if (!ModContentId.IsValid(PortableBundleId) || !ModContentId.IsValid(FamilyKey) ||
            string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 256 ||
            Members is null || Members.Count < 2)
        {
            throw new InvalidDataException("A shared Mod bundle is malformed.");
        }
        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in Members)
        {
            member?.Validate(kind);
            if (member is null || !uniqueIds.Add(member.UniqueId) ||
                !documentMembers.TryGetValue(member.UniqueId, out var documentMember) ||
                member.SourceLibraryItemId != documentMember.SourceLibraryItemId ||
                member.PackagedContentId != documentMember.PackagedContentId)
            {
                throw new InvalidDataException("A shared Mod bundle contains a duplicate or unmatched member.");
            }
        }
    }
}

public sealed record ModProfileTransferDocument(
    string Schema,
    ModProfileTransferKind Kind,
    string? SourceProfileId,
    string DisplayName,
    string? Description,
    ModAssemblyBindingPolicy? AssemblyBindingPolicyOverride,
    IReadOnlyList<ModProfileTransferMember> Members,
    IReadOnlyList<ModProfileTransferBundle> Bundles,
    DateTimeOffset ExportedAtUtc)
{
    public const string CurrentSchema = "junimogate-mod-profile-transfer/v1";
    public const string PackageEntryName = "junimogate-profile.json";
    public const int MaximumDocumentBytes = 2 * 1024 * 1024;

    // Optional in v1 so older shared packages remain readable.
    public bool IncludesConfigFiles { get; init; }

    public void Validate()
    {
        if (Schema != CurrentSchema || !Enum.IsDefined(Kind) ||
            SourceProfileId is not null && !ProfileId.TryParse(SourceProfileId, out _) ||
            string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80 ||
            Description?.Length > 1_024 ||
            AssemblyBindingPolicyOverride is { } policy && !Enum.IsDefined(policy) ||
            Members is null || Members.Count > ModProfileV2.MaximumMembers ||
            Bundles is null || Bundles.Count > ModProfileV2.MaximumMembers / 2 || ExportedAtUtc == default)
        {
            throw new InvalidDataException("The shared Mod Profile document is malformed.");
        }

        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in Members)
        {
            member?.Validate(Kind);
            if (member is null || !uniqueIds.Add(member.UniqueId))
                throw new InvalidDataException("The shared Mod Profile contains duplicate or null members.");
        }
        var indexedMembers = Members.ToDictionary(member => member.UniqueId, StringComparer.OrdinalIgnoreCase);
        var bundleIds = new HashSet<string>(StringComparer.Ordinal);
        var assignedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundle in Bundles)
        {
            bundle?.Validate(Kind, indexedMembers);
            if (bundle is null || !bundleIds.Add(bundle.PortableBundleId) ||
                bundle.Members.Any(member => !assignedMembers.Add(member.UniqueId)))
            {
                throw new InvalidDataException("The shared Mod Profile contains duplicate bundle membership.");
            }
        }
    }
}

public sealed record ModProfileExportResult(
    ModProfileTransferDocument Document,
    int PackagedItems,
    int MissingItems,
    int ExcludedConfigFiles,
    long PackagedBytes)
{
    public int IncludedConfigFiles { get; init; }
}

public sealed record ModProfileImportResult(
    ModProfileV2 Profile,
    IReadOnlyList<ModLibraryItem> AddedItems,
    IReadOnlyList<ModLibraryItem> ReusedItems,
    int MissingMembers,
    int DistinctContentCandidates);

public sealed class ModProfileTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ModLibraryRepository library;
    private readonly ModProfileV2Repository profiles;
    private readonly IModContentMutationGate mutationGate;

    public ModProfileTransferService(
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        IModContentMutationGate? mutationGate = null)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.mutationGate = mutationGate ?? PassThroughModContentMutationGate.Instance;
    }

    public async ValueTask<ModProfileExportResult> ExportManifestAsync(
        ProfileId profileId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        var document = CreateDocument(profile, ModProfileTransferKind.Manifest, packagedIds: null, index);
        await JsonSerializer.SerializeAsync(destination, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        var missing = profile.Members.Count(member => member.LibraryItemId is null);
        return new ModProfileExportResult(document, 0, missing, 0, 0);
    }

    public async ValueTask<ModProfileExportResult> ExportPackageAsync(
        ProfileId profileId,
        Stream destination,
        CancellationToken cancellationToken = default,
        bool includeConfig = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        return await ExportPackageCoreAsync(profile, index, destination, cancellationToken, includeConfig).ConfigureAwait(false);
    }

    public async ValueTask<ModProfileExportResult> ExportBundlePackageAsync(
        string bundleId,
        Stream destination,
        CancellationToken cancellationToken = default,
        bool includeConfig = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!ModContentId.IsValid(bundleId))
            throw new ArgumentException("The Mod bundle ID is invalid.", nameof(bundleId));
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        var bundle = ModManagementProjection.Create(index).Items.FirstOrDefault(item =>
            item.Bundle?.BundleId == bundleId)
            ?? throw new KeyNotFoundException("The Mod bundle is not currently visible.");
        var now = DateTimeOffset.UtcNow;
        var profile = new ModProfileV2(
            ModProfileV2.CurrentSchema,
            "default",
            bundle.DisplayName,
            Revision: 1,
            AssemblyBindingPolicyOverride: null,
            bundle.Members.Select(item => ModProfileMember.FromLibraryItem(item, enabled: true)).ToArray(),
            now,
            now,
            Description: null);
        return await ExportPackageCoreAsync(profile, index, destination, cancellationToken, includeConfig).ConfigureAwait(false);
    }

    private async ValueTask<ModProfileExportResult> ExportPackageCoreAsync(
        ModProfileV2 profile,
        ModLibraryIndex index,
        Stream destination,
        CancellationToken cancellationToken,
        bool includeConfig)
    {
        var indexed = index.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        var packagedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packagedItems = 0;
        var missingItems = 0;
        var excludedConfigs = 0;
        var includedConfigs = 0;
        long packagedBytes = 0;

        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var member in profile.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.LibraryItemId is null || !indexed.TryGetValue(member.LibraryItemId, out var item))
                {
                    missingItems++;
                    continue;
                }

                var filesRoot = library.Layout.GetItemFilesDirectory(item.LibraryItemId);
                if (!Directory.Exists(filesRoot))
                {
                    missingItems++;
                    continue;
                }

                using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var files = Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories)
                    .Select(path => (Path: path, Relative: GetSafeRelativePath(filesRoot, path)))
                    .OrderBy(file => file.Relative, StringComparer.Ordinal)
                    .ToArray();
                var included = 0;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.Relative.Equals("config.json", StringComparison.OrdinalIgnoreCase) && !includeConfig)
                    {
                        excludedConfigs++;
                        continue;
                    }
                    if (file.Relative.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                        includedConfigs++;

                    var info = new FileInfo(file.Path);
                    if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("A Mod package source file is unavailable or unsupported.");
                    ModImportUtilities.AppendPathHeader(contentHash, file.Relative, info.Length);
                    var entry = archive.CreateEntry(
                        $"mods/{item.LibraryItemId}/{file.Relative}",
                        CompressionLevel.Fastest);
                    await using var input = new FileStream(
                        file.Path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = entry.Open();
                    var buffer = new byte[128 * 1024];
                    long copied = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                            break;
                        contentHash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        copied = checked(copied + read);
                    }
                    if (copied != info.Length)
                        throw new InvalidDataException("A Mod package source file changed during export.");
                    packagedBytes = checked(packagedBytes + copied);
                    included++;
                }

                if (included == 0)
                    throw new InvalidDataException("A Mod package item has no exportable files.");
                packagedIds.Add(member.UniqueId, Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant());
                packagedItems++;
            }

            var document = CreateDocument(profile, ModProfileTransferKind.Complete, packagedIds, index, includeConfig);
            var metadataEntry = archive.CreateEntry(ModProfileTransferDocument.PackageEntryName, CompressionLevel.Fastest);
            await using var metadata = metadataEntry.Open();
            await JsonSerializer.SerializeAsync(metadata, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        var resultDocument = CreateDocument(profile, ModProfileTransferKind.Complete, packagedIds, index, includeConfig);
        return new ModProfileExportResult(
            resultDocument,
            packagedItems,
            missingItems,
            excludedConfigs,
            packagedBytes)
        {
            IncludedConfigFiles = includedConfigs,
        };
    }

    public async ValueTask<ModProfileImportResult> ImportManifestAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var document = await ReadDocumentAsync(source, cancellationToken).ConfigureAwait(false);
        if (document.Kind != ModProfileTransferKind.Manifest)
            throw new InvalidDataException("The selected document is not a Mod Profile manifest.");
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        var members = BindMembers(document, index, packagedItems: null);
        var profile = await profiles.CreateImportedAsync(
                document.DisplayName,
                document.Description,
                document.AssemblyBindingPolicyOverride,
                members,
                cancellationToken)
            .ConfigureAwait(false);
        return new ModProfileImportResult(
            profile,
            Array.Empty<ModLibraryItem>(),
            Array.Empty<ModLibraryItem>(),
            members.Count(member => member.LibraryItemId is null),
            DistinctContentCandidates: 0);
    }

    public ModProfilePackageImportTransaction CreatePackageImportTransaction(
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        new(
            library,
            profiles,
            mutationGate,
            sourceArchiveName,
            limits ?? ModArchiveImportLimits.Default,
            JsonOptions);

    public ValueTask<ModProfilePackageImportTransaction?> TryPromotePackageImportTransactionAsync(
        IModArchiveInstallTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction is not ModArchiveInstallTransaction scanned)
            throw new ArgumentException("The Mod archive transaction cannot be promoted.", nameof(transaction));
        return ModProfilePackageImportTransaction.TryCreateAsync(
            library,
            profiles,
            mutationGate,
            scanned,
            JsonOptions,
            cancellationToken);
    }

    private static ModProfileTransferDocument CreateDocument(
        ModProfileV2 profile,
        ModProfileTransferKind kind,
        IReadOnlyDictionary<string, string>? packagedIds,
        ModLibraryIndex library,
        bool includesConfigFiles = false)
    {
        profile.Validate();
        library.Validate();
        var transferMembers = profile.Members.Select(member => new ModProfileTransferMember(
            member.UniqueId,
            member.LibraryItemId,
            packagedIds is not null && packagedIds.TryGetValue(member.UniqueId, out var packagedId) ? packagedId : null,
            member.Enabled,
            member.ExpectedName,
            member.ExpectedVersion,
            member.ExpectedAuthor,
            member.AddedAtUtc)).ToArray();
        var document = new ModProfileTransferDocument(
            ModProfileTransferDocument.CurrentSchema,
            kind,
            profile.Id,
            profile.DisplayName,
            profile.Description,
            profile.AssemblyBindingPolicyOverride,
            transferMembers,
            CreateTransferBundles(profile, library, kind, packagedIds),
            DateTimeOffset.UtcNow);
        document = document with { IncludesConfigFiles = kind == ModProfileTransferKind.Complete && includesConfigFiles };
        document.Validate();
        return document;
    }

    private static IReadOnlyList<ModProfileTransferBundle> CreateTransferBundles(
        ModProfileV2 profile,
        ModLibraryIndex library,
        ModProfileTransferKind kind,
        IReadOnlyDictionary<string, string>? packagedIds)
    {
        var profileMembers = profile.Members.ToDictionary(member => member.UniqueId, StringComparer.OrdinalIgnoreCase);
        return ModManagementProjection.Create(library).Items
            .Where(item => item.Bundle is not null)
            .Select(item =>
            {
                var members = item.Members
                    .Where(member => profileMembers.TryGetValue(member.Manifest.UniqueId, out var profileMember) &&
                                     profileMember.LibraryItemId == member.LibraryItemId &&
                                     (kind == ModProfileTransferKind.Manifest ||
                                      packagedIds?.ContainsKey(member.Manifest.UniqueId) == true))
                    .Select(member => new ModProfileTransferBundleMember(
                        member.Manifest.UniqueId,
                        member.LibraryItemId,
                        packagedIds is not null && packagedIds.TryGetValue(member.Manifest.UniqueId, out var packagedId)
                            ? packagedId
                            : null))
                    .ToArray();
                return members.Length < 2
                    ? null
                    : new ModProfileTransferBundle(
                        item.Bundle!.BundleId,
                        item.Bundle.FamilyKey,
                        item.DisplayName,
                        members);
            })
            .Where(bundle => bundle is not null)
            .Cast<ModProfileTransferBundle>()
            .ToArray();
    }

    internal static async ValueTask<ModProfileTransferDocument> ReadDocumentAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (memory.Length + read > ModProfileTransferDocument.MaximumDocumentBytes)
                throw new InvalidDataException("The shared Mod Profile document is too large.");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var document = NormalizeLegacyDocument(
                JsonSerializer.Deserialize<ModProfileTransferDocument>(memory.ToArray(), JsonOptions)
                ?? throw new InvalidDataException("The shared Mod Profile document is empty."));
            document.Validate();
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The shared Mod Profile JSON is malformed.", exception);
        }
    }

    internal static ModProfileTransferDocument NormalizeLegacyDocument(ModProfileTransferDocument document) =>
        document.Bundles is null
            ? document with { Bundles = Array.Empty<ModProfileTransferBundle>() }
            : document;

    internal static IReadOnlyList<ModProfileMember> BindMembers(
        ModProfileTransferDocument document,
        ModLibraryIndex library,
        IReadOnlyDictionary<string, ModLibraryItem>? packagedItems)
    {
        document.Validate();
        library.Validate();
        var indexed = library.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        var result = new List<ModProfileMember>(document.Members.Count);
        foreach (var member in document.Members)
        {
            ModLibraryItem? bound = null;
            if (member.PackagedContentId is not null && packagedItems is not null)
                packagedItems.TryGetValue(member.PackagedContentId, out bound);
            if (bound is not null && !bound.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
                bound = null;
            bound ??= ResolveInstalledMember(member, library.Items, indexed);
            result.Add(new ModProfileMember(
                member.UniqueId,
                bound?.LibraryItemId,
                member.Enabled,
                member.ExpectedName,
                member.ExpectedVersion,
                member.ExpectedAuthor,
                member.AddedAtUtc));
        }
        return result;
    }

    private static ModLibraryItem? ResolveInstalledMember(
        ModProfileTransferMember member,
        IReadOnlyList<ModLibraryItem> items,
        IReadOnlyDictionary<string, ModLibraryItem> indexed)
    {
        if (member.SourceLibraryItemId is not null &&
            indexed.TryGetValue(member.SourceLibraryItemId, out var exact) &&
            exact.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
        {
            return exact;
        }

        var logicalCandidates = items
            .Where(item => item.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (logicalCandidates.Length == 1)
            return logicalCandidates[0];

        var versionCandidates = logicalCandidates
            .Where(item => item.Manifest.Version.Equals(member.ExpectedVersion, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return versionCandidates.Length == 1 ? versionCandidates[0] : null;
    }

    private static string GetSafeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return SafeArchivePath.Parse(relative).Value;
    }
}

public sealed class ModProfilePackageImportTransaction : IModArchiveInstallTransaction
{
    private readonly ModLibraryRepository library;
    private readonly ModProfileV2Repository profiles;
    private readonly IModContentMutationGate mutationGate;
    private readonly ModArchiveInstallTransaction mods;
    private readonly JsonSerializerOptions jsonOptions;
    private bool hasPackagedMods;
    private bool disposed;

    internal ModProfilePackageImportTransaction(
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        IModContentMutationGate mutationGate,
        string? sourceArchiveName,
        ModArchiveImportLimits limits,
        JsonSerializerOptions jsonOptions)
    {
        this.library = library;
        this.profiles = profiles;
        this.mutationGate = mutationGate;
        this.jsonOptions = jsonOptions;
        mods = new ModArchiveInstallTransaction(library, sourceArchiveName, limits);
    }

    private ModProfilePackageImportTransaction(
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        IModContentMutationGate mutationGate,
        ModArchiveInstallTransaction mods,
        JsonSerializerOptions jsonOptions)
    {
        this.library = library;
        this.profiles = profiles;
        this.mutationGate = mutationGate;
        this.mods = mods;
        this.jsonOptions = jsonOptions;
    }

    public ModProfileTransferDocument? Document { get; private set; }
    public ModInstallTransactionState State => mods.State;
    public ModArchiveScanResult? ScanResult => mods.ScanResult;
    public ModArchiveScanResult? ModScanResult => ScanResult;
    public ModProfileImportResult? ImportResult { get; private set; }
    ModArchiveImportResult? IModArchiveInstallTransaction.ImportResult => mods.ImportResult;

    internal static async ValueTask<ModProfilePackageImportTransaction?> TryCreateAsync(
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        IModContentMutationGate mutationGate,
        ModArchiveInstallTransaction mods,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        if (mods.State != ModInstallTransactionState.AwaitingConfirmation || mods.ScanResult is null)
            throw new InvalidOperationException("The Mod archive must be scanned before package detection.");
        var transaction = new ModProfilePackageImportTransaction(
            library,
            profiles,
            mutationGate,
            mods,
            jsonOptions);
        return await transaction.TryLoadPackageAsync(cancellationToken).ConfigureAwait(false)
            ? transaction
            : null;
    }

    public async ValueTask ScanAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Document is not null)
            throw new InvalidOperationException("The Mod Profile package was already scanned.");
        await mods.ScanAsync(source, cancellationToken).ConfigureAwait(false);
        if (!await TryLoadPackageAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The Mod Profile package metadata is missing.");
    }

    private async ValueTask<bool> TryLoadPackageAsync(CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(mods.StoredArchivePath);
        var metadataEntries = archive.Entries
            .Where(entry => entry.FullName.Equals(ModProfileTransferDocument.PackageEntryName, StringComparison.Ordinal))
            .ToArray();
        if (metadataEntries.Length == 0)
            return false;
        if (metadataEntries.Length != 1 || metadataEntries[0].Length is < 2 or > ModProfileTransferDocument.MaximumDocumentBytes)
            throw new InvalidDataException("The Mod Profile package metadata is missing or duplicated.");
        await using (var metadata = metadataEntries[0].Open())
            Document = await ReadPackageDocumentAsync(metadata, cancellationToken).ConfigureAwait(false);
        if (Document.Kind != ModProfileTransferKind.Complete)
            throw new InvalidDataException("The selected archive is not a complete Mod Profile package.");
        var expected = Document.Members
            .Where(member => member.PackagedContentId is not null)
            .Select(member => member.UniqueId)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        hasPackagedMods = expected.Length > 0;
        var scan = mods.ScanResult ?? throw new InvalidDataException("The Mod package scan result is missing.");
        if (hasPackagedMods && !scan.CanCommit || !hasPackagedMods &&
            (scan.Candidates.Count != 0 || scan.Issues.Any(issue => issue.Code != "manifest_not_found")))
        {
            throw new InvalidDataException("The complete Mod Profile package contains invalid Mod files.");
        }
        var actual = scan.Candidates
            .Select(candidate => candidate.Manifest.UniqueId)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The Mod Profile package files do not match its member list.");
        return true;
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Document is null || ImportResult is not null)
            throw new InvalidOperationException("The Mod Profile package is not ready to commit.");

        var candidateIds = (mods.ScanResult?.Candidates ?? Array.Empty<ModArchiveCandidate>())
            .Select(candidate => candidate.Manifest.UniqueId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var beforeLease = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        var affectedItemIds = beforeLease.Items
            .Where(item => candidateIds.Contains(item.Manifest.UniqueId))
            .Select(item => item.LibraryItemId)
            .ToArray();
        await using var lease = await mutationGate.AcquireAsync(affectedItemIds, cancellationToken)
            .ConfigureAwait(false);
        var previousIndex = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (hasPackagedMods)
            await mods.CommitAsync(
                    CreateTransferredBundles(Document, mods.ScanResult!),
                    ModBundleOrigin.Transfer,
                    cancellationToken)
                .ConfigureAwait(false);
        var imported = hasPackagedMods
            ? mods.ImportResult ?? throw new InvalidDataException("The Mod package import result is missing.")
            : new ModArchiveImportResult(Array.Empty<ModLibraryItem>(), Array.Empty<ModLibraryItem>());
        var addedIds = imported.AddedItems.Select(item => item.LibraryItemId).ToArray();
        try
        {
            var packaged = imported.AllItems.ToDictionary(item => item.ImportedContentId, StringComparer.Ordinal);
            foreach (var member in Document.Members.Where(member => member.PackagedContentId is not null))
            {
                if (!packaged.TryGetValue(member.PackagedContentId!, out var item) ||
                    !item.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A packaged Mod content identity does not match its exported metadata.");
                }
            }

            var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
            var members = ModProfileTransferService.BindMembers(Document, index, packaged);
            var profile = await profiles.CreateImportedAsync(
                    Document.DisplayName,
                    Document.Description,
                    Document.AssemblyBindingPolicyOverride,
                    members,
                    cancellationToken)
                .ConfigureAwait(false);
            ImportResult = new ModProfileImportResult(
                profile,
                imported.AddedItems,
                imported.ReusedItems,
                members.Count(member => member.LibraryItemId is null),
                imported.AddedItems.Count(added => previousIndex.Items.Any(existing =>
                    existing.Manifest.UniqueId.Equals(added.Manifest.UniqueId, StringComparison.OrdinalIgnoreCase) &&
                    existing.Manifest.Version.Equals(added.Manifest.Version, StringComparison.OrdinalIgnoreCase))));
        }
        catch
        {
            await library.RollbackImportAsync(addedIds, previousIndex, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            await mods.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask RollbackAsync(CancellationToken cancellationToken = default) =>
        mods.RollbackAsync(cancellationToken);

    private async ValueTask<ModProfileTransferDocument> ReadPackageDocumentAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = ModProfileTransferService.NormalizeLegacyDocument(
                await JsonSerializer.DeserializeAsync<ModProfileTransferDocument>(
                        stream,
                        jsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("The Mod Profile package metadata is empty."));
            document.Validate();
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Mod Profile package metadata is malformed.", exception);
        }
    }

    private static IReadOnlyList<DetectedModBundle> CreateTransferredBundles(
        ModProfileTransferDocument document,
        ModArchiveScanResult scan)
    {
        var candidates = scan.Candidates.ToDictionary(
            candidate => candidate.Manifest.UniqueId,
            StringComparer.OrdinalIgnoreCase);
        return document.Bundles.Select(bundle =>
        {
            var members = bundle.Members.Select(member =>
            {
                if (!candidates.TryGetValue(member.UniqueId, out var candidate))
                    throw new InvalidDataException("A shared Mod bundle file is missing from the package.");
                return candidate;
            }).ToArray();
            return new DetectedModBundle(
                bundle.FamilyKey,
                bundle.DisplayName,
                ProductDirectory: null,
                members);
        }).ToArray();
    }
}

internal sealed class PassThroughModContentMutationGate : IModContentMutationGate
{
    public static PassThroughModContentMutationGate Instance { get; } = new();

    public ValueTask<IAsyncDisposable> AcquireAsync(
        IReadOnlyCollection<string> affectedLibraryItemIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable>(PassThroughLease.Instance);
    }

    private sealed class PassThroughLease : IAsyncDisposable
    {
        public static PassThroughLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
