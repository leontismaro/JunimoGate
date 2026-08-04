using System.Text.Json;

namespace JunimoGate.Mods;

public static class GamePlaySessionOutcomes
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static bool IsValid(string? value) => value is Completed or Failed or Interrupted;
}

public sealed record GamePlaySessionMetadata(
    ProfileId ProfileId,
    long ProfileRevision,
    int EnabledModCount,
    string GameVersion,
    string SmapiBuildCode,
    string BundleId)
{
    public void Validate()
    {
        if (ProfileRevision < 1 || EnabledModCount < 0 ||
            !IsBoundedValue(GameVersion, 128) || !IsBoundedValue(SmapiBuildCode, 128) ||
            !IsBoundedValue(BundleId, 128))
        {
            throw new InvalidDataException("The game play session metadata is malformed.");
        }
    }

    private static bool IsBoundedValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength;
}

public sealed record GamePlaySession(
    string Schema,
    string SessionId,
    string ProfileId,
    long ProfileRevision,
    int EnabledModCount,
    string GameVersion,
    string SmapiBuildCode,
    string BundleId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? RunningAtUtc,
    DateTimeOffset? ForegroundSinceUtc,
    long AccumulatedTicks,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string? Outcome,
    string? FailureCode)
{
    public const string CurrentSchema = "junimogate-game-play-session/v1";

    public void Validate()
    {
        if (Schema != CurrentSchema || !IsSessionId(SessionId) ||
            !JunimoGate.Mods.ProfileId.TryParse(ProfileId, out var profileId) ||
            StartedAtUtc == default || AccumulatedTicks < 0 || UpdatedAtUtc < StartedAtUtc)
        {
            throw new InvalidDataException("The game play session is malformed.");
        }

        new GamePlaySessionMetadata(
            profileId,
            ProfileRevision,
            EnabledModCount,
            GameVersion,
            SmapiBuildCode,
            BundleId).Validate();

        if (RunningAtUtc is { } running && (running < StartedAtUtc || running > UpdatedAtUtc) ||
            ForegroundSinceUtc is { } foreground &&
            (RunningAtUtc is null || foreground < RunningAtUtc.Value || foreground > UpdatedAtUtc) ||
            EndedAtUtc is { } ended && (ended < StartedAtUtc || ended < UpdatedAtUtc) ||
            EndedAtUtc is null != (Outcome is null) ||
            Outcome is not null && !GamePlaySessionOutcomes.IsValid(Outcome) ||
            EndedAtUtc is not null && ForegroundSinceUtc is not null ||
            FailureCode is { Length: > 128 } ||
            FailureCode is not null && Outcome != GamePlaySessionOutcomes.Failed)
        {
            throw new InvalidDataException("The game play session state is malformed.");
        }
    }

    private static bool IsSessionId(string? value) =>
        value is { Length: 32 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record GamePlaySummary(
    TimeSpan TotalPlayTime,
    int CompletedSessions,
    GamePlaySession? CurrentSession);

public sealed class GamePlaySessionRepository
{
    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(5);
    private const int MaximumSessionBytes = 128 * 1024;
    private const int MaximumHistory = 512;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly string root;
    private readonly string currentPath;
    private readonly string historyRoot;

    public GamePlaySessionRepository(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("The game session root must be absolute.", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        currentPath = Path.Combine(this.root, "current.json");
        historyRoot = Path.Combine(this.root, "history");
    }

    public async ValueTask<GamePlaySession> BeginAsync(
        GamePlaySessionMetadata metadata,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectories();
            var timestamp = now ?? DateTimeOffset.UtcNow;
            var stale = await TryReadAsync(currentPath, cancellationToken).ConfigureAwait(false);
            if (stale is not null)
            {
                await CompleteUnlockedAsync(
                        Settle(stale, timestamp, processActive: false),
                        GamePlaySessionOutcomes.Interrupted,
                        failureCode: null,
                        timestamp,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var session = new GamePlaySession(
                GamePlaySession.CurrentSchema,
                Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
                metadata.ProfileId.Value,
                metadata.ProfileRevision,
                metadata.EnabledModCount,
                metadata.GameVersion,
                metadata.SmapiBuildCode,
                metadata.BundleId,
                timestamp,
                null,
                null,
                0,
                timestamp,
                null,
                null,
                null);
            await WriteAtomicAsync(currentPath, session, cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally
        {
            Gate.Release();
        }
    }

    public ValueTask<GamePlaySession> MarkRunningAsync(
        string sessionId,
        bool foreground,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        UpdateCurrentAsync(sessionId, (session, timestamp) => session with
        {
            RunningAtUtc = session.RunningAtUtc ?? timestamp,
            ForegroundSinceUtc = session.RunningAtUtc is null && foreground
                ? timestamp
                : session.ForegroundSinceUtc,
            UpdatedAtUtc = timestamp,
        }, now, cancellationToken);

    public ValueTask<GamePlaySession> MarkForegroundAsync(
        string sessionId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        UpdateCurrentAsync(sessionId, (session, timestamp) =>
            session.RunningAtUtc is null || session.ForegroundSinceUtc is not null
                ? session
                : session with { ForegroundSinceUtc = timestamp, UpdatedAtUtc = timestamp }, now, cancellationToken);

    public ValueTask<GamePlaySession> CheckpointAsync(
        string sessionId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        UpdateCurrentAsync(
            sessionId,
            (session, timestamp) => Accumulate(session, timestamp, remainForeground: true),
            now,
            cancellationToken);

    public ValueTask<GamePlaySession> MarkBackgroundAsync(
        string sessionId,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default) =>
        UpdateCurrentAsync(
            sessionId,
            (session, timestamp) => Accumulate(session, timestamp, remainForeground: false),
            now,
            cancellationToken);

    public async ValueTask EndAsync(
        string sessionId,
        string outcome,
        string? failureCode = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        if (!GamePlaySessionOutcomes.IsValid(outcome) ||
            failureCode is not null && outcome != GamePlaySessionOutcomes.Failed)
        {
            throw new ArgumentException("The game play session outcome is invalid.", nameof(outcome));
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timestamp = now ?? DateTimeOffset.UtcNow;
            var session = await ReadRequiredAsync(currentPath, cancellationToken).ConfigureAwait(false);
            EnsureSessionId(session, sessionId);
            await CompleteUnlockedAsync(
                    Accumulate(session, timestamp, remainForeground: false),
                    outcome,
                    failureCode,
                    timestamp,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async ValueTask<GamePlaySummary> ReadSummaryAsync(
        bool gameProcessActive,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectories();
            long ticks = 0;
            var completed = 0;
            foreach (var path in Directory.EnumerateFiles(historyRoot, "session-*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await ReadRequiredAsync(path, cancellationToken).ConfigureAwait(false);
                ticks = checked(ticks + session.AccumulatedTicks);
                completed++;
            }

            var current = await TryReadAsync(currentPath, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                current = Settle(current, now ?? DateTimeOffset.UtcNow, gameProcessActive);
                ticks = checked(ticks + current.AccumulatedTicks);
            }

            return new GamePlaySummary(TimeSpan.FromTicks(ticks), completed, current);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async ValueTask<GamePlaySession> UpdateCurrentAsync(
        string sessionId,
        Func<GamePlaySession, DateTimeOffset, GamePlaySession> update,
        DateTimeOffset? now,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadRequiredAsync(currentPath, cancellationToken).ConfigureAwait(false);
            EnsureSessionId(current, sessionId);
            var updated = update(current, now ?? DateTimeOffset.UtcNow);
            updated.Validate();
            if (updated != current)
                await WriteAtomicAsync(currentPath, updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async ValueTask CompleteUnlockedAsync(
        GamePlaySession session,
        string outcome,
        string? failureCode,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        var ended = session with
        {
            ForegroundSinceUtc = null,
            UpdatedAtUtc = endedAtUtc,
            EndedAtUtc = endedAtUtc,
            Outcome = outcome,
            FailureCode = failureCode,
        };
        ended.Validate();
        await WriteAtomicAsync(Path.Combine(historyRoot, $"session-{ended.SessionId}.json"), ended, cancellationToken)
            .ConfigureAwait(false);
        File.Delete(currentPath);
        foreach (var stale in Directory.EnumerateFiles(historyRoot, "session-*.json")
                     .Select(static path => new FileInfo(path))
                     .OrderByDescending(static file => file.LastWriteTimeUtc)
                     .Skip(MaximumHistory))
        {
            stale.Delete();
        }
    }

    private static GamePlaySession Accumulate(GamePlaySession session, DateTimeOffset now, bool remainForeground)
    {
        if (session.ForegroundSinceUtc is not { } foreground)
            return session with { UpdatedAtUtc = now };
        var elapsed = now > foreground ? now - foreground : TimeSpan.Zero;
        return session with
        {
            ForegroundSinceUtc = remainForeground ? now : null,
            AccumulatedTicks = checked(session.AccumulatedTicks + elapsed.Ticks),
            UpdatedAtUtc = now,
        };
    }

    private static GamePlaySession Settle(GamePlaySession session, DateTimeOffset now, bool processActive)
    {
        if (session.ForegroundSinceUtc is not { } foreground)
            return session;
        var elapsed = now > foreground ? now - foreground : TimeSpan.Zero;
        if (!processActive && elapsed > CheckpointInterval)
            elapsed = CheckpointInterval;
        return session with
        {
            ForegroundSinceUtc = processActive ? now : null,
            AccumulatedTicks = checked(session.AccumulatedTicks + elapsed.Ticks),
            UpdatedAtUtc = now,
        };
    }

    private async ValueTask<GamePlaySession?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        return await ReadRequiredAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<GamePlaySession> ReadRequiredAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > MaximumSessionBytes)
            throw new InvalidDataException("The game play session file has an invalid size.");
        try
        {
            var session = JsonSerializer.Deserialize<GamePlaySession>(
                await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
                JsonOptions) ?? throw new InvalidDataException("The game play session file is empty.");
            session.Validate();
            return session;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The game play session JSON is malformed.", exception);
        }
    }

    private static async ValueTask WriteAtomicAsync(
        string path,
        GamePlaySession session,
        CancellationToken cancellationToken)
    {
        session.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             8 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the committed session is authoritative.
            }
        }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(historyRoot);
    }

    private static void EnsureSessionId(GamePlaySession session, string sessionId)
    {
        if (session.SessionId != sessionId)
            throw new InvalidOperationException("The active game play session changed.");
    }
}
