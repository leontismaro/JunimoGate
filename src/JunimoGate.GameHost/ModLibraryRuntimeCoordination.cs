using Android.Content;

namespace JunimoGate.GameHost;

public static partial class GameLaunchRegistry
{
    private static readonly SemaphoreSlim ModLibraryCoordinationLock = new(1, 1);

    public static async ValueTask<IAsyncDisposable> AcquireModLibraryCoordinationAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await ModLibraryCoordinationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = GetRoot(context);
            Directory.CreateDirectory(root);
            var lockPath = Path.Combine(root, "mod-library.lock");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.Asynchronous);
                    return new ModLibraryCoordinationLease(stream);
                }
                catch (IOException)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            ModLibraryCoordinationLock.Release();
            throw;
        }
    }

    public static async ValueTask<bool> IsLibraryItemInUseAsync(
        Context context,
        string libraryItemId,
        CancellationToken cancellationToken)
    {
        var result = await FindLibraryItemsInUseAsync(context, new[] { libraryItemId }, cancellationToken)
            .ConfigureAwait(false);
        return result.Count != 0;
    }

    public static async ValueTask<IReadOnlySet<string>> FindLibraryItemsInUseAsync(
        Context context,
        IReadOnlyCollection<string> libraryItemIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(libraryItemIds);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var libraryItemId in libraryItemIds)
        {
            if (libraryItemId is not { Length: 64 } || libraryItemId.Any(static character =>
                    character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException("A Mod library item ID is invalid.", nameof(libraryItemIds));
            requested.Add(libraryItemId);
        }
        if (requested.Count == 0)
            return requested;
        if (GameSessionRegistry.IsGameProcessActive(context))
            return requested;
        var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (state.Pending?.ModSelectionId is not { } selectionId)
            return new HashSet<string>(StringComparer.Ordinal);
        var selection = await TryReadModSelectionAsync(context, selectionId, cancellationToken).ConfigureAwait(false);
        if (selection is null)
            return new HashSet<string>(StringComparer.Ordinal);
        requested.IntersectWith(selection.Items.Select(item => item.LibraryItemId));
        return requested;
    }

    private sealed class ModLibraryCoordinationLease(FileStream stream) : IAsyncDisposable
    {
        private FileStream? stream = stream;

        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref stream, null);
            if (owned is null)
                return;
            try
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                ModLibraryCoordinationLock.Release();
            }
        }
    }
}
