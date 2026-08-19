namespace JunimoGate.Mods;

public interface IModContentMutationGate
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        IReadOnlyCollection<string> affectedLibraryItemIds,
        CancellationToken cancellationToken = default);
}

public sealed class ModContentInUseException(IReadOnlyCollection<string> libraryItemIds)
    : InvalidOperationException("One or more Mod library items are in use.")
{
    public IReadOnlyCollection<string> LibraryItemIds { get; } = libraryItemIds.ToArray();
}

/// <summary>
/// Application-facing Mod management commands. Android UI code may still use
/// transaction factories for preview/confirmation, but all committed mutations
/// and Profile coordination are exposed from this single service boundary.
/// </summary>
public sealed class ModManagementCommandService
{
    private readonly ModLibraryRepository library;
    private readonly ModTranslationHistoryRepository translations;
    private readonly ModProfileV2Repository profiles;
    private readonly ActiveModProfileSelectionRepository activeProfiles;
    private readonly ModFileService files;
    private readonly IModContentMutationGate mutationGate;

    public ModManagementCommandService(
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        ActiveModProfileSelectionRepository activeProfiles,
        IModContentMutationGate mutationGate)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.activeProfiles = activeProfiles ?? throw new ArgumentNullException(nameof(activeProfiles));
        this.mutationGate = mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));
        translations = new ModTranslationHistoryRepository(library);
        files = new ModFileService(library);
        ProfileMembers = new ModProfileMemberMutationService(profiles);
    }

    public ModProfileMemberMutationService ProfileMembers { get; }

    public IModArchiveInstallTransaction CreateImportTransaction(
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        library.CreateInstallTransaction(sourceArchiveName, limits);

    public ModTranslationInstallTransaction CreateTranslationTransaction(
        IReadOnlyList<ModTranslationTarget> targets,
        string? sourceArchiveName = null,
        ModArchiveImportLimits? limits = null) =>
        translations.CreateInstallTransaction(targets, sourceArchiveName, limits);

    public async ValueTask<ModArchiveImportResult> ImportModsAsync(
        Stream archive,
        string? sourceArchiveName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        await using var transaction = CreateImportTransaction(sourceArchiveName);
        await transaction.ScanAsync(archive, cancellationToken).ConfigureAwait(false);
        if (transaction.ScanResult is null || !transaction.ScanResult.CanCommit)
            throw new InvalidDataException("The Mod archive scan contains blocking errors.");
        await CommitImportAsync(transaction, cancellationToken).ConfigureAwait(false);
        return transaction.ImportResult
            ?? throw new InvalidDataException("The Mod archive import did not produce a result.");
    }

    public async ValueTask CommitImportAsync(
        IModArchiveInstallTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await using var lease = await mutationGate.AcquireAsync(Array.Empty<string>(), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ModLibraryDeleteResult> DeleteInstalledModsAsync(
        IReadOnlyCollection<string> libraryItemIds,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await mutationGate.AcquireAsync(libraryItemIds, cancellationToken)
            .ConfigureAwait(false);
        return await library.DeleteManyAsync(libraryItemIds, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ModTextFile> EditModFileAsync(
        string libraryItemId,
        ModTextFile original,
        string text,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await mutationGate.AcquireAsync(new[] { libraryItemId }, cancellationToken)
            .ConfigureAwait(false);
        return await files.SaveTextAsync(libraryItemId, original, text, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ModTextFile> CreateModFileAsync(
        string libraryItemId,
        string relativeDirectory,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await mutationGate.AcquireAsync(new[] { libraryItemId }, cancellationToken)
            .ConfigureAwait(false);
        return await files.CreateTextAsync(libraryItemId, relativeDirectory, fileName, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ModTranslationInstallResult> InstallTranslationAsync(
        Stream archive,
        IReadOnlyList<ModTranslationTarget> targets,
        string? sourceArchiveName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        await using var transaction = CreateTranslationTransaction(targets, sourceArchiveName);
        await transaction.ScanAsync(archive, cancellationToken).ConfigureAwait(false);
        if (transaction.ScanResult is null || !transaction.ScanResult.CanCommit)
            throw new InvalidDataException("The translation archive scan contains blocking errors.");
        await CommitTranslationAsync(transaction, cancellationToken).ConfigureAwait(false);
        return transaction.InstallResult
            ?? throw new InvalidDataException("The translation install did not produce a result.");
    }

    public async ValueTask CommitTranslationAsync(
        ModTranslationInstallTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var affected = transaction.ScanResult?.Files
            .Select(file => file.LibraryItemId)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? throw new InvalidOperationException("The translation archive has not been scanned.");
        await using var lease = await mutationGate.AcquireAsync(affected, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ModTranslationRestoreResult> RestoreTranslationAsync(
        string installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = (await translations.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.InstallationId == installationId)
            ?? throw new KeyNotFoundException("The translation installation does not exist.");
        await using var lease = await mutationGate.AcquireAsync(
                installation.AffectedLibraryItemIds,
                cancellationToken)
            .ConfigureAwait(false);
        return await translations.RestoreAsync(installationId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ModProfileV2> CreateProfileAsync(
        string displayName,
        string? description = null,
        ModAssemblyBindingPolicy? bindingPolicyOverride = null,
        CancellationToken cancellationToken = default) =>
        profiles.CreateAsync(displayName, description, bindingPolicyOverride, cancellationToken);

    public ValueTask<bool> DeleteProfileAsync(
        ProfileId profileId,
        CancellationToken cancellationToken = default) =>
        profiles.DeleteAsync(profileId, cancellationToken);

    public async ValueTask<ActiveModProfileSelection> SelectProfileAsync(
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        _ = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var current = await activeProfiles
            .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
            .ConfigureAwait(false);
        return await activeProfiles.SetAsync(current.Revision, profileId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ModLaunchSelectionSnapshot> BuildLaunchSelectionAsync(
        ProfileId profileId,
        ModAssemblyBindingPolicy defaultBindingPolicy,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        var index = await library.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ModLaunchSelectionBuilder.Build(profile, index, defaultBindingPolicy);
    }
}
