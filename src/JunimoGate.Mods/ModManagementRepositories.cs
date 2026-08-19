namespace JunimoGate.Mods;

/// <summary>
/// The installation catalog owns library items and their physical storage.
/// Profile, Bundle and translation callers should depend on this narrow contract
/// instead of taking ownership of the whole repository implementation.
/// </summary>
public interface IModInstallRepository
{
    ModLibraryLayout Layout { get; }

    ValueTask<ModLibraryIndex> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<ModLibraryDeleteResult> DeleteManyAsync(
        IReadOnlyCollection<string> libraryItemIds,
        CancellationToken cancellationToken = default);

    IModArchiveInstallTransaction CreateInstallTransaction(
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null);
}

/// <summary>
/// Owns the independently persisted Bundle presentation catalog.
/// Installation import and deletion coordinate cross-catalog changes atomically.
/// </summary>
public interface IModBundleCatalogRepository
{
    ValueTask<ModBundleCatalog> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<ModBundleMutationResult> SetMemberUnlockedAsync(
        string bundleId,
        string uniqueId,
        bool unlocked,
        CancellationToken cancellationToken = default);
}

public sealed class ModBundleCatalogRepository : IModBundleCatalogRepository
{
    private readonly ModLibraryRepository library;

    public ModBundleCatalogRepository(IModInstallRepository installs)
    {
        library = installs as ModLibraryRepository
                  ?? throw new ArgumentException("The Bundle catalog requires the shared installation repository.", nameof(installs));
    }

    public async ValueTask<ModBundleCatalog> ReadAsync(CancellationToken cancellationToken = default)
    {
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        return index.BundleCatalog;
    }

    public async ValueTask<ModBundleMutationResult> SetMemberUnlockedAsync(
        string bundleId,
        string uniqueId,
        bool unlocked,
        CancellationToken cancellationToken = default)
    {
        if (!ModContentId.IsValid(bundleId))
            throw new ArgumentException("The Mod bundle ID is invalid.", nameof(bundleId));
        if (string.IsNullOrWhiteSpace(uniqueId) || uniqueId.Length > 256)
            throw new ArgumentException("The Mod UniqueID is invalid.", nameof(uniqueId));

        await library.OperationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await library.AcquireProcessOperationLockAsync(cancellationToken)
                .ConfigureAwait(false);
            library.EnsureDirectories();
            var current = await library.ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
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
                    BundleRemainsVisible: ModLibraryRepository.CountActiveMembers(bundle, overrides) >= 2);
            }
            if (unlocked)
                overrides.Add(new ModBundleUnlockOverride(bundle.FamilyKey, uniqueId));
            else
                overrides.RemoveAt(index);

            var catalog = current.BundleCatalog with
            {
                Revision = checked(current.BundleCatalog.Revision + 1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                UnlockOverrides = overrides
                    .OrderBy(value => value.FamilyKey, StringComparer.Ordinal)
                    .ThenBy(value => value.UniqueId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
            var updated = current with { BundleCatalog = catalog };
            await library.WriteBundleCatalogAtomicAsync(catalog, current.Items, cancellationToken).ConfigureAwait(false);
            library.NotifyBundleChanged();
            return new ModBundleMutationResult(
                updated,
                Changed: true,
                BundleRemainsVisible: ModLibraryRepository.CountActiveMembers(bundle, overrides) >= 2);
        }
        finally
        {
            library.OperationLock.Release();
        }
    }
}

public interface IModTranslationHistoryRepository
{
    ModTranslationInstallTransaction CreateInstallTransaction(
        IReadOnlyList<ModTranslationTarget> targets,
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null);

    ValueTask<IReadOnlyList<ModTranslationInstallationSummary>> ListAsync(
        IReadOnlyCollection<string>? libraryItemIds = null,
        CancellationToken cancellationToken = default);

    ValueTask<ModTranslationRestoreResult> RestoreAsync(
        string installationId,
        CancellationToken cancellationToken = default);
}
