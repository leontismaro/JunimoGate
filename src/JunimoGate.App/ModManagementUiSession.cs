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
        Transfers = new ModProfileTransferService(Library, Profiles);
        Installations = Library;
        Bundles = new ModBundleCatalogRepository(Installations);
        Translations = new ModTranslationHistoryRepository(Library);
        Commands = new ModManagementCommandService(
            Library,
            Profiles,
            ActiveProfile,
            new AndroidModContentMutationGate(context));
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
        lock (cacheGate)
        {
            if (librarySnapshot is { } cached)
                return cached;
        }

        await libraryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (cacheGate)
            {
                if (librarySnapshot is { } cached)
                    return cached;
            }

            while (true)
            {
                long generation;
                lock (cacheGate)
                    generation = libraryGeneration;
                var loaded = await Library.ReadAsync(cancellationToken).ConfigureAwait(false);
                lock (cacheGate)
                {
                    if (generation != libraryGeneration)
                        continue;
                    librarySnapshot ??= loaded;
                    return librarySnapshot;
                }
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
        lock (cacheGate)
        {
            if (profileSnapshot is { } cached)
                return cached;
        }
        await profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (cacheGate)
            {
                if (profileSnapshot is { } cached)
                    return cached;
            }

            while (true)
            {
                long generation;
                lock (cacheGate)
                    generation = profileGeneration;
                var loaded = await Profiles.ListAsync(cancellationToken).ConfigureAwait(false);
                lock (cacheGate)
                {
                    if (generation != profileGeneration)
                        continue;
                    profileSnapshot ??= loaded;
                    return profileSnapshot;
                }
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
        lock (cacheGate)
        {
            if (activeProfileSnapshot is { } cached)
                return cached;
        }
        await activeProfileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (cacheGate)
            {
                if (activeProfileSnapshot is { } cached)
                    return cached;
            }

            while (true)
            {
                long generation;
                lock (cacheGate)
                    generation = activeProfileGeneration;
                var loaded = await ActiveProfile
                    .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
                    .ConfigureAwait(false);
                lock (cacheGate)
                {
                    if (generation != activeProfileGeneration)
                        continue;
                    activeProfileSnapshot ??= loaded;
                    return activeProfileSnapshot;
                }
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
