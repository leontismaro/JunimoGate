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
/// Reads the Bundle presentation projection without exposing install mutations.
/// The Bundle catalog keeps its own revision inside the existing library index.
/// </summary>
public interface IModBundleCatalogRepository
{
    ValueTask<ModBundleCatalog> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class ModBundleCatalogRepository(IModInstallRepository installs) : IModBundleCatalogRepository
{
    public async ValueTask<ModBundleCatalog> ReadAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installs);
        var library = await installs.ReadAsync(cancellationToken).ConfigureAwait(false);
        return library.BundleCatalog;
    }
}

/// <summary>
/// Translation history has a separate ownership boundary even though the current
/// on-disk transaction implementation still shares the library process lock.
/// </summary>
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

public sealed class ModTranslationHistoryRepository(ModLibraryRepository library) : IModTranslationHistoryRepository
{
    public ModTranslationInstallTransaction CreateInstallTransaction(
        IReadOnlyList<ModTranslationTarget> targets,
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        library.CreateTranslationTransaction(targets, sourceArchiveName, limits);

    public ValueTask<IReadOnlyList<ModTranslationInstallationSummary>> ListAsync(
        IReadOnlyCollection<string>? libraryItemIds = null,
        CancellationToken cancellationToken = default) =>
        library.ListTranslationInstallationsAsync(libraryItemIds, cancellationToken);

    public ValueTask<ModTranslationRestoreResult> RestoreAsync(
        string installationId,
        CancellationToken cancellationToken = default) =>
        library.RestoreTranslationAsync(installationId, cancellationToken);
}
