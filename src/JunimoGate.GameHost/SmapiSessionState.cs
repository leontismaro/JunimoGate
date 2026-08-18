namespace JunimoGate.GameHost;

internal enum SmapiSessionStatus
{
    Starting,
    Running,
    Failed,
    Disposed,
}

internal sealed record SmapiSessionFailure(
    string Code,
    string Message,
    Exception? Exception);

internal sealed record SmapiSessionSnapshot(
    SmapiSessionStatus Status,
    SmapiSessionFailure? Failure);

internal readonly record struct SmapiSessionStateChange(
    bool Accepted,
    SmapiSessionStatus PreviousStatus,
    SmapiSessionSnapshot Snapshot);

internal sealed class SmapiSessionState : IDisposable
{
    private readonly object syncRoot = new();
    private readonly TaskCompletionSource<SmapiSessionSnapshot> startupCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private SmapiSessionStatus status = SmapiSessionStatus.Starting;
    private SmapiSessionFailure? failure;

    public Task<SmapiSessionSnapshot> StartupCompletion => startupCompletion.Task;

    public SmapiSessionSnapshot Snapshot
    {
        get
        {
            lock (syncRoot)
                return new SmapiSessionSnapshot(status, failure);
        }
    }

    public SmapiSessionStateChange Apply(
        SmapiSessionStatus nextStatus,
        SmapiSessionFailure? nextFailure = null)
    {
        lock (syncRoot)
        {
            var previousStatus = status;
            bool accepted = nextStatus switch
            {
                SmapiSessionStatus.Running => previousStatus == SmapiSessionStatus.Starting,
                SmapiSessionStatus.Failed =>
                    previousStatus is SmapiSessionStatus.Starting or SmapiSessionStatus.Running
                    && nextFailure is not null,
                SmapiSessionStatus.Disposed =>
                    previousStatus is SmapiSessionStatus.Starting or SmapiSessionStatus.Running,
                _ => false,
            };
            if (!accepted)
                return new SmapiSessionStateChange(false, previousStatus, new SmapiSessionSnapshot(status, failure));

            status = nextStatus;
            if (nextStatus == SmapiSessionStatus.Failed)
                failure = nextFailure;

            var snapshot = new SmapiSessionSnapshot(status, failure);
            if (previousStatus == SmapiSessionStatus.Starting)
                startupCompletion.TrySetResult(snapshot);
            return new SmapiSessionStateChange(true, previousStatus, snapshot);
        }
    }

    public void Dispose() => Apply(SmapiSessionStatus.Disposed);
}
