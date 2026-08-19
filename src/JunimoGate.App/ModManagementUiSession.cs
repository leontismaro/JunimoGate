using Android.Content;
using JunimoGate.Mods;

namespace JunimoGate.App;

internal enum ModManagementChangeKind
{
    Library,
    Profiles,
    ActiveProfile,
    Bundle,
}

internal sealed class ModManagementChangedEventArgs(
    ModManagementChangeKind kind,
    long generation,
    object? origin = null) : EventArgs
{
    public ModManagementChangeKind Kind { get; } = kind;
    public long Generation { get; } = generation;
    public object? Origin { get; } = origin;
}

internal class ModManagementStore : IDisposable
{
    private readonly SemaphoreSlim libraryLock = new(1, 1);
    private readonly SemaphoreSlim profileLock = new(1, 1);
    private readonly SemaphoreSlim activeProfileLock = new(1, 1);
    private ModLibraryIndex? librarySnapshot;
    private IReadOnlyList<ModProfileV2>? profileSnapshot;
    private ActiveModProfileSelection? activeProfileSnapshot;
    private long libraryGeneration;
    private long profileGeneration;
    private long activeProfileGeneration;
    private readonly object cacheGate = new();
    private bool disposed;

    public ModManagementStore(Context context, string userDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        var profilesRoot = Path.Combine(userDataRoot, "profiles");
        Library = new ModLibraryRepository(Path.Combine(userDataRoot, "mods"));
        Profiles = new ModProfileV2Repository(profilesRoot);
        ActiveProfile = new ActiveModProfileSelectionRepository(profilesRoot);
        var mutationGate = new AndroidModContentMutationGate(context);
        Transfers = new ModProfileTransferService(Library, Profiles, mutationGate);
        Installations = Library;
        Bundles = new ModBundleCatalogRepository(Installations);
        Translations = new ModTranslationHistoryRepository(Library);
        Commands = new ModManagementCommandService(
            Library,
            Profiles,
            ActiveProfile,
            mutationGate);
        Library.Changed += OnLibraryChanged;
        Library.BundleChanged += OnBundleChanged;
        Profiles.Changed += OnProfilesChanged;
        ActiveProfile.Changed += OnActiveProfileChanged;
    }

    public ModLibraryRepository Library { get; }
    public IModInstallRepository Installations { get; }
    public IModBundleCatalogRepository Bundles { get; }
    public IModTranslationHistoryRepository Translations { get; }
    public ModManagementCommandService Commands { get; }
    public ModProfileV2Repository Profiles { get; }
    public ActiveModProfileSelectionRepository ActiveProfile { get; }
    public ModProfileTransferService Transfers { get; }
    public long LibraryGeneration => Interlocked.Read(ref libraryGeneration);
    public long ProfileGeneration => Interlocked.Read(ref profileGeneration);
    public long ActiveProfileGeneration => Interlocked.Read(ref activeProfileGeneration);

    public event EventHandler<ModManagementChangedEventArgs>? Changed;

    public async ValueTask<ModLibraryIndex> GetLibraryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await libraryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var loaded = await Library.ReadAsync(cancellationToken).ConfigureAwait(false);
            lock (cacheGate)
            {
                if (librarySnapshot is { } cached &&
                    cached.Revision == loaded.Revision &&
                    cached.BundleCatalog.Revision == loaded.BundleCatalog.Revision)
                    return cached;
                librarySnapshot = loaded;
                if (librarySnapshot is not null && loaded.Revision > 0)
                    libraryGeneration++;
                return loaded;
            }
        }
        finally
        {
            libraryLock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ModProfileV2>> GetProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var loaded = await Profiles.ListAsync(cancellationToken).ConfigureAwait(false);
            lock (cacheGate)
            {
                if (profileSnapshot is { } cached && ProfilesEquivalent(cached, loaded))
                    return cached;
                profileSnapshot = loaded;
                profileGeneration++;
                return loaded;
            }
        }
        finally
        {
            profileLock.Release();
        }
    }

    public async ValueTask<ActiveModProfileSelection> GetActiveProfileAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await activeProfileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var loaded = await ActiveProfile
                .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
                .ConfigureAwait(false);
            lock (cacheGate)
            {
                if (activeProfileSnapshot is { } cached &&
                    cached.Revision == loaded.Revision &&
                    cached.ActiveProfileId == loaded.ActiveProfileId)
                    return cached;
                activeProfileSnapshot = loaded;
                activeProfileGeneration++;
                return loaded;
            }
        }
        finally
        {
            activeProfileLock.Release();
        }
    }

    public void ResetSnapshots()
    {
        InvalidateLibrary(null);
        InvalidateProfiles(null);
        InvalidateActiveProfile(null);
    }

    private void OnLibraryChanged() => InvalidateLibrary(null);
    private void OnBundleChanged() => InvalidateBundle(null);
    private void OnProfilesChanged() => InvalidateProfiles(null);
    private void OnActiveProfileChanged() => InvalidateActiveProfile(null);

    private static bool ProfilesEquivalent(
        IReadOnlyList<ModProfileV2> first,
        IReadOnlyList<ModProfileV2> second)
    {
        if (first.Count != second.Count)
            return false;
        var indexed = first.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        foreach (var profile in second)
        {
            if (!indexed.TryGetValue(profile.Id, out var existing) ||
                existing.Revision != profile.Revision ||
                existing.UpdatedAtUtc != profile.UpdatedAtUtc)
                return false;
        }
        return true;
    }

    private void InvalidateLibrary(object? origin)
    {
        if (disposed)
            return;
        long generation;
        lock (cacheGate)
        {
            librarySnapshot = null;
            generation = ++libraryGeneration;
        }
        Changed?.Invoke(this, new ModManagementChangedEventArgs(ModManagementChangeKind.Library, generation, origin));
    }

    private void InvalidateBundle(object? origin)
    {
        if (disposed)
            return;
        lock (cacheGate)
            librarySnapshot = null;
        Changed?.Invoke(this, new ModManagementChangedEventArgs(ModManagementChangeKind.Bundle, LibraryGeneration, origin));
    }

    private void InvalidateProfiles(object? origin)
    {
        if (disposed)
            return;
        long generation;
        lock (cacheGate)
        {
            profileSnapshot = null;
            generation = ++profileGeneration;
        }
        Changed?.Invoke(this, new ModManagementChangedEventArgs(
            ModManagementChangeKind.Profiles,
            generation,
            origin));
    }

    private void InvalidateActiveProfile(object? origin)
    {
        if (disposed)
            return;
        long generation;
        lock (cacheGate)
        {
            activeProfileSnapshot = null;
            generation = ++activeProfileGeneration;
        }
        Changed?.Invoke(this, new ModManagementChangedEventArgs(ModManagementChangeKind.ActiveProfile, generation, origin));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Library.Changed -= OnLibraryChanged;
        Library.BundleChanged -= OnBundleChanged;
        Profiles.Changed -= OnProfilesChanged;
        ActiveProfile.Changed -= OnActiveProfileChanged;
        Changed = null;
        lock (cacheGate)
        {
            librarySnapshot = null;
            profileSnapshot = null;
            activeProfileSnapshot = null;
        }
    }
}

internal sealed class ModManagementUiSession(Context context, string userDataRoot)
    : ModManagementStore(context, userDataRoot)
{
}
