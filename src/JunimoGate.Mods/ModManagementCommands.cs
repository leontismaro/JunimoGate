namespace JunimoGate.Mods;

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

    public ModManagementCommandService(
        ModLibraryRepository library,
        ModProfileV2Repository profiles,
        ActiveModProfileSelectionRepository activeProfiles)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.activeProfiles = activeProfiles ?? throw new ArgumentNullException(nameof(activeProfiles));
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return transaction.ImportResult
            ?? throw new InvalidDataException("The Mod archive import did not produce a result.");
    }

    public ValueTask<ModLibraryDeleteResult> DeleteInstalledModsAsync(
        IReadOnlyCollection<string> libraryItemIds,
        CancellationToken cancellationToken = default) =>
        library.DeleteManyAsync(libraryItemIds, cancellationToken);

    public ValueTask<ModTextFile> EditModFileAsync(
        string libraryItemId,
        ModTextFile original,
        string text,
        CancellationToken cancellationToken = default) =>
        files.SaveTextAsync(libraryItemId, original, text, cancellationToken);

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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return transaction.InstallResult
            ?? throw new InvalidDataException("The translation install did not produce a result.");
    }

    public ValueTask<ModTranslationRestoreResult> RestoreTranslationAsync(
        string installationId,
        CancellationToken cancellationToken = default) =>
        translations.RestoreAsync(installationId, cancellationToken);

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
