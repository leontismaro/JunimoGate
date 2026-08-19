using System.Text.Json;
using Android.Content;
using JunimoGate.Mods;
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.GameHost;

public static partial class GameLaunchRegistry
{
    private const int MaximumOutcomeBytes = 64 * 1024;
    private static readonly TimeSpan PendingLaunchStartupGrace = TimeSpan.FromMinutes(2);

    public static async ValueTask RecordOutcomeAsync(
        Context context,
        string attemptId,
        GameLaunchOutcomeStatus status,
        GameStartupStage stage,
        string code,
        CancellationToken cancellationToken)
    {
        if (!IsSnapshotId(attemptId))
            throw new ArgumentException("The launch outcome identity is invalid.");
        code = IsCode(code) ? code : "startup_failed";
        var outcome = new StoredLaunchOutcome(
            GameLaunchSchema.Outcome,
            attemptId,
            status,
            stage,
            code,
            DateTimeOffset.UtcNow);
        await WriteJsonAsync(
                Path.Combine(GetRoot(context), $"outcome-{attemptId}.json"),
                outcome,
                cancellationToken)
            .ConfigureAwait(false);
        Log.Info(
            "JunimoGate.Launch",
            $"outcome-recorded attempt={attemptId[..8]} status={status} stage={stage} code={code}");
    }

    public static async ValueTask<PendingGameLaunchOutcome?> TryReadPendingOutcomeAsync(
        Context context,
        CancellationToken cancellationToken)
    {
        var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        var pending = state.Pending;
        if (pending is null)
            return null;
        if (!IsSnapshotId(pending.AttemptId) || !IsSnapshotId(pending.SnapshotId) ||
            pending.RecoveryLevel is < 0 or > 2 || pending.Profile is null ||
            pending.ModSelectionId is null || !IsSnapshotId(pending.ModSelectionId))
        {
            await ClearInvalidPendingAsync(context, state, pending, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var snapshot = await TryReadSnapshotAsync(context, pending.SnapshotId, cancellationToken).ConfigureAwait(false);
        var modSelection = await TryReadModSelectionAsync(context, pending.ModSelectionId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null || modSelection is null || ProfileLaunchSelection.From(modSelection) != pending.Profile)
        {
            await ClearInvalidPendingAsync(context, state, pending, cancellationToken).ConfigureAwait(false);
            return null;
        }
        var outcomePath = Path.Combine(GetRoot(context), $"outcome-{pending.AttemptId}.json");
        if (!File.Exists(outcomePath))
        {
            if (GameSessionRegistry.IsGameProcessActive(context))
                return null;
            var pendingAge = DateTimeOffset.UtcNow - pending.IssuedAtUtc;
            if (pendingAge <= PendingLaunchStartupGrace)
            {
                Log.Info(
                    "JunimoGate.Launch",
                    $"launch-still-starting attempt={pending.AttemptId[..8]} level={pending.RecoveryLevel}");
                return null;
            }
            Log.Warn(
                "JunimoGate.Launch",
                $"launch-interrupted attempt={pending.AttemptId[..8]} level={pending.RecoveryLevel}");
            return new PendingGameLaunchOutcome(
                pending.AttemptId,
                pending.SnapshotId,
                pending.RecoveryLevel,
                snapshot,
                pending.Profile,
                modSelection,
                GameLaunchOutcomeStatus.Failed,
                GameStartupStage.LaunchRequest,
                "launch_interrupted");
        }

        try
        {
            var outcome = await ReadJsonAsync<StoredLaunchOutcome>(
                    outcomePath,
                    MaximumOutcomeBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (outcome is null || outcome.Schema != GameLaunchSchema.Outcome ||
                outcome.AttemptId != pending.AttemptId || !Enum.IsDefined(outcome.Status) ||
                !Enum.IsDefined(outcome.Stage) || !IsCode(outcome.Code))
            {
                throw new InvalidDataException("The launch outcome is invalid.");
            }
            return new PendingGameLaunchOutcome(
                pending.AttemptId,
                pending.SnapshotId,
                pending.RecoveryLevel,
                snapshot,
                pending.Profile,
                modSelection,
                outcome.Status,
                outcome.Stage,
                outcome.Code);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.Warn(
                "JunimoGate.Launch",
                $"launch-outcome-invalid attempt={pending.AttemptId[..8]} level={pending.RecoveryLevel}",
                exception);
            return new PendingGameLaunchOutcome(
                pending.AttemptId,
                pending.SnapshotId,
                pending.RecoveryLevel,
                snapshot,
                pending.Profile,
                modSelection,
                GameLaunchOutcomeStatus.Failed,
                GameStartupStage.LaunchRequest,
                "launch_outcome_invalid");
        }
    }

    public static async ValueTask CompletePendingRunningAsync(
        Context context,
        PendingGameLaunchOutcome pending,
        CancellationToken cancellationToken)
    {
        GameActivationState? completed = null;
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (state.Pending?.AttemptId != pending.AttemptId)
                return;
            var confirmsActive = state.ActiveSnapshotId == pending.SnapshotId;
            state = state with
            {
                ActiveConfirmed = confirmsActive,
                PreviousSnapshotId = confirmsActive ? null : state.PreviousSnapshotId,
                FailedSnapshotId = null,
                Pending = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
            completed = state;
        }
        finally
        {
            StateLock.Release();
        }
        if (completed is null)
            return;
        Log.Info(
            "JunimoGate.Launch",
            $"running-confirmed attempt={pending.AttemptId[..8]} active={(completed.ActiveConfirmed ? 1 : 0)}");
        CleanupAttemptFiles(context, pending.AttemptId, pending.ModSelection?.SelectionId);
        await CleanupRegistryAsync(context, completed, cancellationToken).ConfigureAwait(false);
        await RuntimeCacheMaintenance.PruneAsync(context, completed, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask CompletePendingFailureAsync(
        Context context,
        PendingGameLaunchOutcome pending,
        CancellationToken cancellationToken)
    {
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (state.Pending?.AttemptId != pending.AttemptId)
                return;
            var failedIsActive = state.ActiveSnapshotId == pending.SnapshotId;
            state = state with
            {
                ActiveSnapshotId = failedIsActive ? null : state.ActiveSnapshotId,
                ActiveConfirmed = failedIsActive ? false : state.ActiveConfirmed,
                FailedSnapshotId = pending.SnapshotId,
                Pending = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
            CleanupAttemptFiles(context, pending.AttemptId, pending.ModSelection?.SelectionId);
            Log.Warn(
                "JunimoGate.Launch",
                $"failure-completed attempt={pending.AttemptId[..8]} stage={pending.Stage} code={pending.Code} level={pending.RecoveryLevel}");
        }
        finally
        {
            StateLock.Release();
        }
    }

    private static async ValueTask ClearInvalidPendingAsync(
        Context context,
        GameActivationState observed,
        PendingLaunchAttempt pending,
        CancellationToken cancellationToken)
    {
        await StateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await TryReadStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (state.Pending?.AttemptId != observed.Pending?.AttemptId)
                return;
            var pendingWasActive = IsSnapshotId(pending.SnapshotId) && state.ActiveSnapshotId == pending.SnapshotId;
            state = state with
            {
                ActiveSnapshotId = pendingWasActive ? null : state.ActiveSnapshotId,
                ActiveConfirmed = pendingWasActive ? false : state.ActiveConfirmed,
                FailedSnapshotId = IsSnapshotId(pending.SnapshotId) ? pending.SnapshotId : state.FailedSnapshotId,
                Pending = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteStateAsync(context, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateLock.Release();
        }
        if (IsSnapshotId(pending.AttemptId))
            CleanupAttemptFiles(context, pending.AttemptId, pending.ModSelectionId);
    }

    private static void CleanupAttemptFiles(Context context, string attemptId, string? modSelectionId)
    {
        var root = GetRoot(context);
        TryDeleteFile(Path.Combine(root, $"outcome-{attemptId}.json"));
        TryDeleteFile(Path.Combine(root, $"descriptor-{attemptId}.json"));
        TryDeleteFile(Path.Combine(root, $"descriptor-{attemptId}.json.consuming"));
        if (IsSnapshotId(modSelectionId))
            TryDeleteFile(GetModSelectionPath(context, modSelectionId!));
    }

    private static bool IsCode(string value) =>
        value is { Length: > 0 and <= 128 } && value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private sealed record StoredLaunchOutcome(
        string Schema,
        string AttemptId,
        GameLaunchOutcomeStatus Status,
        GameStartupStage Stage,
        string Code,
        DateTimeOffset RecordedAtUtc);
}
