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
            SourceLibraryItemId is not null && !ModContentId.IsValid(SourceLibraryItemId) ||
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

public sealed record ModProfileTransferDocument(
    string Schema,
    ModProfileTransferKind Kind,
    string? SourceProfileId,
    string DisplayName,
    string? Description,
    ModAssemblyBindingPolicy? AssemblyBindingPolicyOverride,
    IReadOnlyList<ModProfileTransferMember> Members,
    DateTimeOffset ExportedAtUtc)
{
    public const string CurrentSchema = "junimogate-mod-profile-transfer/v1";
    public const string PackageEntryName = "junimogate-profile.json";
    public const int MaximumDocumentBytes = 2 * 1024 * 1024;

    public void Validate()
    {
        if (Schema != CurrentSchema || !Enum.IsDefined(Kind) ||
            SourceProfileId is not null && !ProfileId.TryParse(SourceProfileId, out _) ||
            string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80 ||
            Description?.Length > 1_024 ||
            AssemblyBindingPolicyOverride is { } policy && !Enum.IsDefined(policy) ||
            Members is null || Members.Count > ModProfileV2.MaximumMembers || ExportedAtUtc == default)
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
    }
}

public sealed record ModProfileExportResult(
    ModProfileTransferDocument Document,
    int PackagedItems,
    int MissingItems,
    int ExcludedConfigFiles,
    long PackagedBytes);

public sealed record ModProfileImportResult(
    ModProfileV2 Profile,
    IReadOnlyList<ModLibraryItem> AddedItems,
    IReadOnlyList<ModLibraryItem> ReusedItems,
    int MissingMembers);

public sealed class ModProfileTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ModLibraryRepository library;
    private readonly ModProfileV2Repository profiles;

    public ModProfileTransferService(ModLibraryRepository library, ModProfileV2Repository profiles)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public async ValueTask<ModProfileExportResult> ExportManifestAsync(
        ProfileId profileId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var document = CreateDocument(profile, ModProfileTransferKind.Manifest, packagedIds: null);
        await JsonSerializer.SerializeAsync(destination, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        var missing = profile.Members.Count(member => member.LibraryItemId is null);
        return new ModProfileExportResult(document, 0, missing, 0, 0);
    }

    public async ValueTask<ModProfileExportResult> ExportPackageAsync(
        ProfileId profileId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        var indexed = index.Items.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        var packagedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packagedItems = 0;
        var missingItems = 0;
        var excludedConfigs = 0;
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
                    if (file.Relative.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                    {
                        excludedConfigs++;
                        continue;
                    }

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

            var document = CreateDocument(profile, ModProfileTransferKind.Complete, packagedIds);
            var metadataEntry = archive.CreateEntry(ModProfileTransferDocument.PackageEntryName, CompressionLevel.Fastest);
            await using var metadata = metadataEntry.Open();
            await JsonSerializer.SerializeAsync(metadata, document, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        var resultDocument = CreateDocument(profile, ModProfileTransferKind.Complete, packagedIds);
        return new ModProfileExportResult(
            resultDocument,
            packagedItems,
            missingItems,
            excludedConfigs,
            packagedBytes);
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
            members.Count(member => member.LibraryItemId is null));
    }

    public ModProfilePackageImportTransaction CreatePackageImportTransaction(
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        new(library, profiles, sourceArchiveName, limits ?? ModArchiveImportLimits.Default, JsonOptions);

    private static ModProfileTransferDocument CreateDocument(
        ModProfileV2 profile,
        ModProfileTransferKind kind,
        IReadOnlyDictionary<string, string>? packagedIds)
    {
        profile.Validate();
        var document = new ModProfileTransferDocument(
            ModProfileTransferDocument.CurrentSchema,
            kind,
            profile.Id,
            profile.DisplayName,
            profile.Description,
            profile.AssemblyBindingPolicyOverride,
            profile.Members.Select(member => new ModProfileTransferMember(
                member.UniqueId,
                member.LibraryItemId,
                packagedIds is not null && packagedIds.TryGetValue(member.UniqueId, out var packagedId) ? packagedId : null,
                member.Enabled,
                member.ExpectedName,
                member.ExpectedVersion,
                member.ExpectedAuthor,
                member.AddedAtUtc)).ToArray(),
            DateTimeOffset.UtcNow);
        document.Validate();
        return document;
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
            var document = JsonSerializer.Deserialize<ModProfileTransferDocument>(memory.ToArray(), JsonOptions)
                ?? throw new InvalidDataException("The shared Mod Profile document is empty.");
            document.Validate();
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The shared Mod Profile JSON is malformed.", exception);
        }
    }

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
            if (bound is null && member.SourceLibraryItemId is not null)
                indexed.TryGetValue(member.SourceLibraryItemId, out bound);
            if (bound is not null && !bound.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
                bound = null;
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

    private static string GetSafeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return SafeArchivePath.Parse(relative).Value;
    }
}

public sealed class ModProfilePackageImportTransaction : IAsyncDisposable
{
    private readonly ModLibraryRepository library;
    private readonly ModProfileV2Repository profiles;
    private readonly ModArchiveInstallTransaction mods;
    private readonly JsonSerializerOptions jsonOptions;
    private bool hasPackagedMods;
    private bool disposed;

    internal ModProfilePackageImportTransaction(
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        string? sourceArchiveName,
        ModArchiveImportLimits limits,
        JsonSerializerOptions jsonOptions)
    {
        this.library = library;
        this.profiles = profiles;
        this.jsonOptions = jsonOptions;
        mods = new ModArchiveInstallTransaction(library, sourceArchiveName, limits);
    }

    public ModProfileTransferDocument? Document { get; private set; }
    public ModArchiveScanResult? ModScanResult => mods.ScanResult;
    public ModProfileImportResult? ImportResult { get; private set; }

    public async ValueTask ScanAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Document is not null)
            throw new InvalidOperationException("The Mod Profile package was already scanned.");
        await mods.ScanAsync(source, cancellationToken).ConfigureAwait(false);
        using var archive = ZipFile.OpenRead(mods.StoredArchivePath);
        var metadataEntries = archive.Entries
            .Where(entry => entry.FullName.Equals(ModProfileTransferDocument.PackageEntryName, StringComparison.Ordinal))
            .ToArray();
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
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Document is null || ImportResult is not null)
            throw new InvalidOperationException("The Mod Profile package is not ready to commit.");

        var previousIndex = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (hasPackagedMods)
            await mods.CommitAsync(cancellationToken).ConfigureAwait(false);
        var imported = hasPackagedMods
            ? mods.ImportResult ?? throw new InvalidDataException("The Mod package import result is missing.")
            : new ModArchiveImportResult(Array.Empty<ModLibraryItem>(), Array.Empty<ModLibraryItem>());
        var addedIds = imported.AddedItems.Select(item => item.LibraryItemId).ToArray();
        try
        {
            var packaged = imported.AllItems.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
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
                members.Count(member => member.LibraryItemId is null));
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

    private async ValueTask<ModProfileTransferDocument> ReadPackageDocumentAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await JsonSerializer.DeserializeAsync<ModProfileTransferDocument>(
                    stream,
                    jsonOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The Mod Profile package metadata is empty.");
            document.Validate();
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Mod Profile package metadata is malformed.", exception);
        }
    }
}
