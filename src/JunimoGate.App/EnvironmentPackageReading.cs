using System.Diagnostics;
using JunimoGate.Core;

namespace JunimoGate.App;

internal enum InstalledPackageSummaryStatus
{
    Found,
    NotFoundOrNotVisible,
    Failed,
}

internal enum EnvironmentPackageReadStatus
{
    Complete,
    Partial,
    Failed,
}

internal sealed record InstalledPackageSummary(
    string PackageName,
    string DisplayName,
    string? VersionName,
    long VersionCode,
    SigningIdentity? SigningIdentity);

internal sealed record InstalledPackageSummaryResult(
    string StoreId,
    InstalledPackageSummaryStatus Status,
    InstalledPackageSummary? Summary,
    string? FailureStage,
    string? ExceptionType,
    long ElapsedMilliseconds);

internal sealed record EnvironmentPackageReadResult(
    IReadOnlyList<InstalledPackageSummaryResult> Packages,
    EnvironmentPackageReadStatus Status,
    long ElapsedMilliseconds);

internal interface IInstalledPackageSummaryReader
{
    InstalledPackageSummary? Read(string packageName, CancellationToken cancellationToken);
}

internal interface IEnvironmentPackageReadLog
{
    void Info(string message);

    void Warn(string message);
}

internal sealed class EnvironmentPackageReadService
{
    private readonly IInstalledPackageSummaryReader reader;
    private readonly IEnvironmentPackageReadLog log;
    private readonly IReadOnlyList<(string StoreId, string PackageName)> packages;

    public EnvironmentPackageReadService(
        IInstalledPackageSummaryReader reader,
        IEnvironmentPackageReadLog log,
        IReadOnlyList<(string StoreId, string PackageName)> packages)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count == 0)
            throw new ArgumentException("At least one package must be configured.", nameof(packages));
        if (packages.Any(static package =>
                string.IsNullOrWhiteSpace(package.StoreId) || string.IsNullOrWhiteSpace(package.PackageName)))
        {
            throw new ArgumentException("Every package requires a store ID and package name.", nameof(packages));
        }

        this.reader = reader;
        this.log = log;
        this.packages = packages;
    }

    public EnvironmentPackageReadResult Read(long generation, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        log.Info($"environment-read-start generation={generation}");
        try
        {
            var results = new List<InstalledPackageSummaryResult>(packages.Count);
            foreach (var (storeId, packageName) in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = Stopwatch.StartNew();
                log.Info($"package-summary-start store={storeId}");
                try
                {
                    var summary = reader.Read(packageName, cancellationToken);
                    var packageStatus = summary is null
                        ? InstalledPackageSummaryStatus.NotFoundOrNotVisible
                        : InstalledPackageSummaryStatus.Found;
                    results.Add(new InstalledPackageSummaryResult(
                        storeId,
                        packageStatus,
                        summary,
                        null,
                        null,
                        elapsed.ElapsedMilliseconds));
                    log.Info(
                        $"package-summary-complete store={storeId} result={Format(packageStatus)} elapsedMs={elapsed.ElapsedMilliseconds}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    results.Add(new InstalledPackageSummaryResult(
                        storeId,
                        InstalledPackageSummaryStatus.Failed,
                        null,
                        "package-info",
                        exception.GetType().Name,
                        elapsed.ElapsedMilliseconds));
                    log.Warn(
                        $"package-summary-failed store={storeId} stage=package-info exception={exception.GetType().Name} elapsedMs={elapsed.ElapsedMilliseconds}");
                }
            }

            var status = ResolveStatus(results);
            log.Info($"environment-read-complete result={Format(status)} elapsedMs={total.ElapsedMilliseconds}");
            return new EnvironmentPackageReadResult(results, status, total.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Info($"environment-read-cancelled generation={generation} elapsedMs={total.ElapsedMilliseconds}");
            throw;
        }
    }

    private static EnvironmentPackageReadStatus ResolveStatus(
        IReadOnlyCollection<InstalledPackageSummaryResult> results)
    {
        var failures = results.Count(static result => result.Status == InstalledPackageSummaryStatus.Failed);
        return failures == 0
            ? EnvironmentPackageReadStatus.Complete
            : failures == results.Count
                ? EnvironmentPackageReadStatus.Failed
                : EnvironmentPackageReadStatus.Partial;
    }

    private static bool IsRecoverable(Exception exception) => exception is not (
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException);

    private static string Format(InstalledPackageSummaryStatus status) => status switch
    {
        InstalledPackageSummaryStatus.Found => "found",
        InstalledPackageSummaryStatus.NotFoundOrNotVisible => "missing",
        InstalledPackageSummaryStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string Format(EnvironmentPackageReadStatus status) => status switch
    {
        EnvironmentPackageReadStatus.Complete => "complete",
        EnvironmentPackageReadStatus.Partial => "partial",
        EnvironmentPackageReadStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

// Synchronous Android Binder calls cannot be forcibly interrupted. A timed-out
// operation is retired from coordination, while workerSlots bounds abandoned
// calls so repeated refreshes cannot create unbounded blocked workers.
internal sealed class BoundedRetirableTaskGate
{
    private readonly SemaphoreSlim workerSlots;
    private readonly SemaphoreSlim activeSlot = new(1, 1);

    public BoundedRetirableTaskGate(int maximumConcurrentWorkers)
    {
        if (maximumConcurrentWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentWorkers));
        workerSlots = new SemaphoreSlim(maximumConcurrentWorkers, maximumConcurrentWorkers);
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, T> action, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lease = new Lease(workerSlots, activeSlot);
        var worker = Task.Run(() => RunWorker(action, operationCancellation, lease), CancellationToken.None);
        try
        {
            return await worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Retire(operationCancellation, lease, worker);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Retire(operationCancellation, lease, worker);
            throw;
        }
    }

    private static T RunWorker<T>(Func<CancellationToken, T> action, CancellationTokenSource cancellation, Lease lease)
    {
        try
        {
            lease.Enter(cancellation.Token);
            return action(cancellation.Token);
        }
        finally
        {
            lease.Dispose();
            cancellation.Dispose();
        }
    }

    private static void Retire<T>(CancellationTokenSource cancellation, Lease lease, Task<T> worker)
    {
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        lease.Retire();
        _ = ObserveAsync(worker);
    }

    private static async Task ObserveAsync(Task worker)
    {
        try { await worker.ConfigureAwait(false); } catch { }
    }

    private sealed class Lease(SemaphoreSlim workerSlots, SemaphoreSlim activeSlot) : IDisposable
    {
        private readonly object sync = new();
        private bool workerAcquired;
        private bool activeAcquired;
        private bool retired;
        private bool disposed;

        public void Enter(CancellationToken cancellationToken)
        {
            workerSlots.Wait(cancellationToken);
            lock (sync) workerAcquired = true;
            try
            {
                activeSlot.Wait(cancellationToken);
                var release = false;
                lock (sync)
                {
                    if (retired || disposed) release = true;
                    else activeAcquired = true;
                }
                if (release)
                {
                    activeSlot.Release();
                    throw new OperationCanceledException("The operation was retired.", cancellationToken);
                }
            }
            catch
            {
                ReleaseWorker();
                throw;
            }
        }

        public void Retire()
        {
            var release = false;
            lock (sync)
            {
                retired = true;
                if (activeAcquired) { activeAcquired = false; release = true; }
            }
            if (release) activeSlot.Release();
        }

        public void Dispose()
        {
            var release = false;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (activeAcquired) { activeAcquired = false; release = true; }
            }
            if (release) activeSlot.Release();
            ReleaseWorker();
        }

        private void ReleaseWorker()
        {
            var release = false;
            lock (sync)
            {
                if (workerAcquired) { workerAcquired = false; release = true; }
            }
            if (release) workerSlots.Release();
        }
    }
}

internal sealed class EnvironmentReadGeneration
{
    private long current;

    public long Begin() => Interlocked.Increment(ref current);

    public void Invalidate() => Interlocked.Increment(ref current);

    public bool IsCurrent(long generation) => Interlocked.Read(ref current) == generation;
}
