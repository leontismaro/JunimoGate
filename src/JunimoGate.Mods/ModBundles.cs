using System.Security.Cryptography;
using System.Text;

namespace JunimoGate.Mods;

public enum ModBundleOrigin
{
    Detected,
    Transfer,
}

public sealed record ModBundleMember(
    string UniqueId,
    string LibraryItemId,
    string OriginalRootPath)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UniqueId) || UniqueId.Length > 256 ||
            !ModLibraryItemId.IsValid(LibraryItemId) || OriginalRootPath.Length > 4096 ||
            OriginalRootPath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException("A Mod bundle member is malformed.");
        }
        if (OriginalRootPath.Length > 0 && !SafeArchivePath.TryParse(OriginalRootPath, out _))
            throw new InvalidDataException("A Mod bundle member path is unsafe.");
    }
}

public sealed record ModBundleDefinition(
    string BundleId,
    string FamilyKey,
    string DisplayName,
    ModBundleOrigin Origin,
    string? SourceArchiveName,
    string? ProductDirectory,
    IReadOnlyList<ModBundleMember> Members,
    DateTimeOffset CreatedAtUtc)
{
    public void Validate(IReadOnlyDictionary<string, ModLibraryItem> items)
    {
        if (!ModContentId.IsValid(BundleId) || !ModContentId.IsValid(FamilyKey) ||
            string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 256 ||
            !Enum.IsDefined(Origin) || SourceArchiveName?.Length > 255 ||
            ProductDirectory?.Length > 4096 || Members is null || Members.Count < 2 || CreatedAtUtc == default)
        {
            throw new InvalidDataException("A Mod bundle definition is malformed.");
        }

        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraryItemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in Members)
        {
            member?.Validate();
            if (member is null || !uniqueIds.Add(member.UniqueId) || !libraryItemIds.Add(member.LibraryItemId) ||
                !items.TryGetValue(member.LibraryItemId, out var item) ||
                !item.Manifest.UniqueId.Equals(member.UniqueId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A Mod bundle contains a duplicate, missing, or mismatched member.");
            }
        }
    }
}

public sealed record ModBundleUnlockOverride(string FamilyKey, string UniqueId)
{
    public void Validate()
    {
        if (!ModContentId.IsValid(FamilyKey) || string.IsNullOrWhiteSpace(UniqueId) || UniqueId.Length > 256)
            throw new InvalidDataException("A Mod bundle unlock override is malformed.");
    }
}

public sealed record ModBundleCatalog(
    string Schema,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ModBundleDefinition> Bundles,
    IReadOnlyList<ModBundleUnlockOverride> UnlockOverrides)
{
    public const string CurrentSchema = "junimogate-mod-bundles/v1";

    public static ModBundleCatalog CreateEmpty() => new(
        CurrentSchema,
        Revision: 1,
        DateTimeOffset.UtcNow,
        Array.Empty<ModBundleDefinition>(),
        Array.Empty<ModBundleUnlockOverride>());

    public void Validate(IReadOnlyList<ModLibraryItem> libraryItems)
    {
        if (Schema != CurrentSchema || Revision < 1 || UpdatedAtUtc == default ||
            Bundles is null || UnlockOverrides is null)
        {
            throw new InvalidDataException("The Mod bundle catalog is malformed.");
        }

        var items = libraryItems.ToDictionary(item => item.LibraryItemId, StringComparer.Ordinal);
        var bundleIds = new HashSet<string>(StringComparer.Ordinal);
        var assignedItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bundle in Bundles)
        {
            bundle?.Validate(items);
            if (bundle is null || !bundleIds.Add(bundle.BundleId) ||
                bundle.Members.Any(member => !assignedItems.Add(member.LibraryItemId)))
            {
                throw new InvalidDataException("The Mod bundle catalog contains duplicate bundle membership.");
            }
        }

        var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in UnlockOverrides)
        {
            value?.Validate();
            if (value is null || !overrides.Add($"{value.FamilyKey}:{value.UniqueId}"))
                throw new InvalidDataException("The Mod bundle catalog contains duplicate unlock overrides.");
        }
    }
}

public sealed record ModBundleMutationResult(
    ModLibraryIndex Library,
    bool Changed,
    bool BundleRemainsVisible);

internal static class ModBundleFactory
{
    public static ModBundleDefinition Create(
        DetectedModBundle detected,
        IReadOnlyDictionary<string, PreparedModLibraryItem> preparedByRoot,
        IReadOnlySet<string> occupiedLibraryItemIds,
        ModBundleOrigin origin = ModBundleOrigin.Detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        var members = detected.Members
            .Select(candidate => preparedByRoot.TryGetValue(candidate.RootPath, out var prepared)
                ? new ModBundleMember(candidate.Manifest.UniqueId, prepared.Item.LibraryItemId, candidate.RootPath)
                : null)
            .Where(member => member is not null && !occupiedLibraryItemIds.Contains(member.LibraryItemId))
            .Cast<ModBundleMember>()
            .GroupBy(member => member.UniqueId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(member => member.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (members.Length < 2)
            throw new InvalidOperationException("A detected Mod bundle has fewer than two available members.");

        var identity = detected.FamilyKey + "|" + string.Join('|', members.Select(member => member.LibraryItemId));
        var bundleId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var sourceArchiveName = detected.Members
            .Select(member => preparedByRoot[member.RootPath].Item.SourceArchiveName)
            .FirstOrDefault(value => value is not null);
        return new ModBundleDefinition(
            bundleId,
            detected.FamilyKey,
            detected.DisplayName,
            origin,
            sourceArchiveName,
            detected.ProductDirectory,
            members,
            DateTimeOffset.UtcNow);
    }
}
