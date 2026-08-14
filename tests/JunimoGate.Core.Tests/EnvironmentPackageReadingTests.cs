using JunimoGate.App;
using JunimoGate.Core;
using JunimoGate.Tests;

internal static class EnvironmentPackageReadingTests
{
    private static readonly (string StoreId, string PackageName)[] Packages =
    [
        ("play", "play.package"),
        ("galaxy", "galaxy.package"),
    ];

    public static void PreservesIndependentPackageResults()
    {
        foreach (var testCase in new[]
                 {
                     new Case(Result.Found, Result.Missing, EnvironmentPackageReadStatus.Complete),
                     new Case(Result.Missing, Result.Found, EnvironmentPackageReadStatus.Complete),
                     new Case(Result.Found, Result.Found, EnvironmentPackageReadStatus.Complete),
                     new Case(Result.Missing, Result.Missing, EnvironmentPackageReadStatus.Complete),
                     new Case(Result.Found, Result.Failed, EnvironmentPackageReadStatus.Partial),
                     new Case(Result.Failed, Result.Found, EnvironmentPackageReadStatus.Partial),
                     new Case(Result.Failed, Result.Failed, EnvironmentPackageReadStatus.Failed),
                 })
        {
            var reader = new StubSummaryReader(testCase.Play, testCase.Galaxy);
            var result = CreateService(reader).Read(1, CancellationToken.None);

            TestHarness.Equal(testCase.Status, result.Status);
            TestHarness.Equal(ToStatus(testCase.Play), result.Packages[0].Status);
            TestHarness.Equal(ToStatus(testCase.Galaxy), result.Packages[1].Status);
        }
    }

    public static void ReturnsImmediatelyWhileReaderIsBlocked()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var service = CreateService(new BlockingSummaryReader(entered, release));
        var gate = new BoundedRetirableTaskGate(2);

        var call = gate.RunAsync(
            token => service.Read(1, token),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        TestHarness.True(entered.Wait(TimeSpan.FromSeconds(2)));
        TestHarness.False(call.IsCompleted);

        release.Set();
        _ = call.GetAwaiter().GetResult();
    }

    public static void ReplacesATimedOutRead()
    {
        using var release = new ManualResetEventSlim();
        using var firstEntered = new ManualResetEventSlim();
        var gate = new BoundedRetirableTaskGate(2);
        var callCount = 0;
        int Read(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                firstEntered.Set();
                release.Wait();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return call;
        }

        var first = gate.RunAsync(Read, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        TestHarness.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));
        TestHarness.Throws<TimeoutException>(() => first.GetAwaiter().GetResult());

        var second = gate.RunAsync(Read, TimeSpan.FromSeconds(2), CancellationToken.None);
        TestHarness.Equal(2, second.GetAwaiter().GetResult());
        release.Set();
    }

    public static void BoundsBlockedWorkers()
    {
        using var release = new ManualResetEventSlim();
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var gate = new BoundedRetirableTaskGate(2);
        var callCount = 0;
        int Block(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount);
            (call == 1 ? firstEntered : secondEntered).Set();
            release.Wait();
            cancellationToken.ThrowIfCancellationRequested();
            return call;
        }

        var first = gate.RunAsync(Block, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        TestHarness.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));
        TestHarness.Throws<TimeoutException>(() => first.GetAwaiter().GetResult());
        var second = gate.RunAsync(Block, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        TestHarness.True(secondEntered.Wait(TimeSpan.FromSeconds(2)));
        TestHarness.Throws<TimeoutException>(() => second.GetAwaiter().GetResult());

        var third = gate.RunAsync(Block, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        TestHarness.Throws<TimeoutException>(() => third.GetAwaiter().GetResult());
        TestHarness.Equal(2, Volatile.Read(ref callCount));
        release.Set();
    }

    public static void SerializesHealthyReads()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var gate = new BoundedRetirableTaskGate(2);
        var callCount = 0;
        var first = gate.RunAsync(
            _ =>
            {
                Interlocked.Increment(ref callCount);
                entered.Set();
                release.Wait();
                return 1;
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        TestHarness.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var second = gate.RunAsync(
            _ => Interlocked.Increment(ref callCount),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Thread.Sleep(50);
        TestHarness.Equal(1, Volatile.Read(ref callCount));

        release.Set();
        TestHarness.Equal(1, first.GetAwaiter().GetResult());
        TestHarness.Equal(2, second.GetAwaiter().GetResult());
    }

    public static void RejectsStaleGenerations()
    {
        var generation = new EnvironmentReadGeneration();
        var first = generation.Begin();
        TestHarness.True(generation.IsCurrent(first));
        var second = generation.Begin();
        TestHarness.False(generation.IsCurrent(first));
        TestHarness.True(generation.IsCurrent(second));
        generation.Invalidate();
        TestHarness.False(generation.IsCurrent(second));
    }

    public static void WritesRedactedTerminalLogs()
    {
        var log = new CapturingLog();
        var service = CreateService(new StubSummaryReader(Result.Found, Result.Failed), log: log);

        _ = service.Read(42, CancellationToken.None);

        TestHarness.True(log.Messages.Any(static message => message == "environment-read-start generation=42"));
        TestHarness.True(log.Messages.Any(static message =>
            message.StartsWith("package-summary-complete store=play result=found elapsedMs=", StringComparison.Ordinal)));
        TestHarness.True(log.Messages.Any(static message =>
            message.StartsWith("package-summary-failed store=galaxy stage=package-info exception=IOException elapsedMs=", StringComparison.Ordinal)));
        TestHarness.True(log.Messages.Any(static message =>
            message.StartsWith("environment-read-complete result=partial elapsedMs=", StringComparison.Ordinal)));
        TestHarness.False(log.Messages.Any(static message =>
            message.Contains("play.package", StringComparison.Ordinal) ||
            message.Contains("galaxy.package", StringComparison.Ordinal)));
    }

    private static EnvironmentPackageReadService CreateService(
        IInstalledPackageSummaryReader reader,
        IEnvironmentPackageReadLog? log = null) =>
        new(reader, log ?? new CapturingLog(), Packages);

    private static InstalledPackageSummaryStatus ToStatus(Result result) => result switch
    {
        Result.Found => InstalledPackageSummaryStatus.Found,
        Result.Missing => InstalledPackageSummaryStatus.NotFoundOrNotVisible,
        Result.Failed => InstalledPackageSummaryStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private enum Result
    {
        Found,
        Missing,
        Failed,
    }

    private sealed record Case(Result Play, Result Galaxy, EnvironmentPackageReadStatus Status);

    private sealed class StubSummaryReader(Result play, Result galaxy) : IInstalledPackageSummaryReader
    {
        public InstalledPackageSummary? Read(string packageName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = packageName == "play.package" ? play : galaxy;
            return result switch
            {
                Result.Found => new InstalledPackageSummary(packageName, "Game", "1.0", 1, null),
                Result.Missing => null,
                Result.Failed => throw new IOException("private detail"),
                _ => throw new ArgumentOutOfRangeException(),
            };
        }
    }

    private sealed class BlockingSummaryReader(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IInstalledPackageSummaryReader
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public InstalledPackageSummary? Read(string packageName, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            entered.Set();
            release.Wait();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private sealed class CapturingLog : IEnvironmentPackageReadLog
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Warn(string message) => Messages.Add(message);
    }
}
