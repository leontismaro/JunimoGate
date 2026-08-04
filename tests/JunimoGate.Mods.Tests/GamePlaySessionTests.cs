using JunimoGate.Mods;
using JunimoGate.Tests;

internal static class GamePlaySessionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 4, 8, 0, 0, TimeSpan.Zero);

    public static void DoesNotCountStartupTime()
    {
        using var fixture = new Fixture();
        var session = fixture.Repository.BeginAsync(Metadata(), Epoch).AsTask().GetAwaiter().GetResult();
        var summary = fixture.Repository.ReadSummaryAsync(
            gameProcessActive: true,
            Epoch.AddMinutes(20)).AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(TimeSpan.Zero, summary.TotalPlayTime);
        TestHarness.Equal(session.SessionId, summary.CurrentSession?.SessionId);
        fixture.Repository.EndAsync(
            session.SessionId,
            GamePlaySessionOutcomes.Completed,
            now: Epoch.AddMinutes(20)).AsTask().GetAwaiter().GetResult();
    }

    public static void CountsOnlyRunningForegroundIntervals()
    {
        using var fixture = new Fixture();
        var session = fixture.Repository.BeginAsync(Metadata(), Epoch).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.MarkRunningAsync(
            session.SessionId,
            foreground: false,
            Epoch.AddMinutes(1)).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.MarkForegroundAsync(
            session.SessionId,
            Epoch.AddMinutes(2)).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.CheckpointAsync(
            session.SessionId,
            Epoch.AddMinutes(7)).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.CheckpointAsync(
            session.SessionId,
            Epoch.AddMinutes(8)).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.MarkBackgroundAsync(
            session.SessionId,
            Epoch.AddMinutes(10)).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.MarkForegroundAsync(
            session.SessionId,
            Epoch.AddMinutes(20)).AsTask().GetAwaiter().GetResult();

        var summary = fixture.Repository.ReadSummaryAsync(
            gameProcessActive: true,
            Epoch.AddMinutes(23)).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(TimeSpan.FromMinutes(11), summary.TotalPlayTime);

        fixture.Repository.EndAsync(
            session.SessionId,
            GamePlaySessionOutcomes.Completed,
            now: Epoch.AddMinutes(25)).AsTask().GetAwaiter().GetResult();
        summary = fixture.Repository.ReadSummaryAsync(
            gameProcessActive: false,
            Epoch.AddMinutes(30)).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(TimeSpan.FromMinutes(13), summary.TotalPlayTime);
        TestHarness.Equal(1, summary.CompletedSessions);
        TestHarness.Equal<GamePlaySession?>(null, summary.CurrentSession);
    }

    public static void CapsAnInactiveUncheckpointedSession()
    {
        using var fixture = new Fixture();
        var session = fixture.Repository.BeginAsync(Metadata(), Epoch).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.MarkRunningAsync(
            session.SessionId,
            foreground: true,
            Epoch.AddMinutes(1)).AsTask().GetAwaiter().GetResult();

        var inactive = fixture.Repository.ReadSummaryAsync(
            gameProcessActive: false,
            Epoch.AddHours(2)).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(GamePlaySessionRepository.CheckpointInterval, inactive.TotalPlayTime);
        TestHarness.Equal<DateTimeOffset?>(null, inactive.CurrentSession?.ForegroundSinceUtc);
    }

    public static void ArchivesAStaleSessionBeforeBeginningAnother()
    {
        using var fixture = new Fixture();
        var stale = fixture.Repository.BeginAsync(Metadata(), Epoch).AsTask().GetAwaiter().GetResult();
        _ = fixture.Repository.MarkRunningAsync(
            stale.SessionId,
            foreground: true,
            Epoch.AddMinutes(1)).AsTask().GetAwaiter().GetResult();

        var current = fixture.Repository.BeginAsync(
            Metadata(ProfileId.Parse("farm-2")),
            Epoch.AddHours(1)).AsTask().GetAwaiter().GetResult();
        var summary = fixture.Repository.ReadSummaryAsync(
            gameProcessActive: true,
            Epoch.AddHours(1)).AsTask().GetAwaiter().GetResult();

        TestHarness.Equal(1, summary.CompletedSessions);
        TestHarness.Equal(GamePlaySessionRepository.CheckpointInterval, summary.TotalPlayTime);
        TestHarness.Equal(current.SessionId, summary.CurrentSession?.SessionId);
        var history = File.ReadAllText(Directory.EnumerateFiles(fixture.HistoryRoot).Single());
        TestHarness.True(history.Contains("\"outcome\":\"interrupted\"", StringComparison.Ordinal));
    }

    public static void RecordsFailureAndRemovesCurrentSession()
    {
        using var fixture = new Fixture();
        var session = fixture.Repository.BeginAsync(Metadata(), Epoch).AsTask().GetAwaiter().GetResult();
        fixture.Repository.EndAsync(
            session.SessionId,
            GamePlaySessionOutcomes.Failed,
            "mod_loading_failed",
            Epoch.AddMinutes(1)).AsTask().GetAwaiter().GetResult();

        TestHarness.False(File.Exists(fixture.CurrentPath));
        var history = File.ReadAllText(Directory.EnumerateFiles(fixture.HistoryRoot).Single());
        TestHarness.True(history.Contains("\"outcome\":\"failed\"", StringComparison.Ordinal));
        TestHarness.True(history.Contains("\"failureCode\":\"mod_loading_failed\"", StringComparison.Ordinal));
    }

    public static void RejectsMalformedSessionJson()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CurrentPath)!);
        File.WriteAllText(fixture.CurrentPath, "{\"schema\":\"wrong\"}");

        TestHarness.Throws<InvalidDataException>(() => fixture.Repository.ReadSummaryAsync(
            gameProcessActive: false,
            Epoch).AsTask().GetAwaiter().GetResult());
    }

    private static GamePlaySessionMetadata Metadata(ProfileId? profileId = null) => new(
        profileId ?? ProfileId.Parse("default"),
        ProfileRevision: 7,
        EnabledModCount: 12,
        GameVersion: "1.6.15.3",
        SmapiBuildCode: "smapi-test",
        BundleId: "smapi-bundle-test");

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-sessions-{Guid.NewGuid():N}"));
            Repository = new GamePlaySessionRepository(Root);
        }

        public string Root { get; }
        public string CurrentPath => Path.Combine(Root, "current.json");
        public string HistoryRoot => Path.Combine(Root, "history");
        public GamePlaySessionRepository Repository { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
