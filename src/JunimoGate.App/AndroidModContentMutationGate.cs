using Android.Content;
using JunimoGate.GameHost;
using JunimoGate.Mods;

namespace JunimoGate.App;

internal sealed class AndroidModContentMutationGate(Context context) : IModContentMutationGate
{
    private readonly Context context = context.ApplicationContext ?? context;

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        IReadOnlyCollection<string> affectedLibraryItemIds,
        CancellationToken cancellationToken = default)
    {
        var lease = await GameLaunchRegistry.AcquireModLibraryCoordinationAsync(context, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var inUse = await GameLaunchRegistry.FindLibraryItemsInUseAsync(
                    context,
                    affectedLibraryItemIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inUse.Count != 0)
                throw new ModContentInUseException(inUse);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
