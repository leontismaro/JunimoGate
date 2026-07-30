using Android.Content;
using Android.Util;
using JunimoGate.Android;
using JunimoGate.GameHost;
using JunimoGate.Mods;
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.App;

internal enum LauncherStatus
{
    Checking,
    Preparing,
    Recovering,
    Ready,
    Launching,
    GameNotInstalled,
    Unsupported,
    Failed,
}

internal sealed record LauncherState(
    LauncherStatus Status,
    string Message,
    bool ShowProgress,
    bool CanLaunch,
    ModAssemblyBindingPolicy AssemblyBindingPolicy = ModAssemblyBindingPolicy.HighestCompatible,
    bool CanConfigureProfile = false);

internal sealed class LauncherCoordinator : IDisposable
{
    private readonly Context context;
    private readonly ModProfileRepository profiles;
    private readonly ProfileId profileId = ProfileId.Parse("default");
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private PreparedGameHandle? preparedGame;
    private ModProfile? profile;
    private bool disposed;

    public LauncherCoordinator(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
        profiles = new ModProfileRepository(Path.Combine(AndroidPrivateStorage.GetUserDataRoot(this.context), "profiles"));
        CurrentState = new LauncherState(
            LauncherStatus.Checking,
            "Checking the installed game…",
            ShowProgress: true,
            CanLaunch: false);
    }

    public event Action<LauncherState>? StateChanged;

    public LauncherState CurrentState { get; private set; }

    public async Task<GameLaunchHandle?> InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profile = await profiles.OpenOrCreateAsync(profileId, cancellationToken).ConfigureAwait(false);
            var pending = await GameLaunchRegistry.TryReadPendingOutcomeAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (pending is not null)
            {
                preparedGame = null;
                if (pending.Status == GameLaunchOutcomeStatus.Running)
                {
                    await GameLaunchRegistry.CompletePendingRunningAsync(context, pending, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    return await RecoverPendingLaunchAsync(pending, cancellationToken).ConfigureAwait(false);
                }
            }

            if (preparedGame is not null)
            {
                Publish(ReadyState(preparedGame));
                return null;
            }

            Publish(new LauncherState(
                LauncherStatus.Checking,
                "Checking the installed game and prepared workspace…",
                ShowProgress: true,
                CanLaunch: false));
            var progress = new PreparationProgress(this);
            var result = await GameDeepPrepareCoordinator
                .PrepareOrReuseAsync(context, progress, cancellationToken)
                .ConfigureAwait(false);
            PublishPreparationResult(result);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Launcher", "initialize-failed", exception);
            preparedGame = null;
            Publish(FailedState());
            return null;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async ValueTask<GameLaunchHandle?> TryCreateLaunchAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentState.Status == LauncherStatus.Failed)
            {
                Publish(new LauncherState(
                    LauncherStatus.Preparing,
                    "Preparing Stardew Valley for launch…",
                    ShowProgress: true,
                    CanLaunch: false));
                var retried = await GameDeepPrepareCoordinator
                    .PrepareAsync(context, new PreparationProgress(this), cancellationToken)
                    .ConfigureAwait(false);
                PublishPreparationResult(retried);
            }

            if (CurrentState.Status != LauncherStatus.Ready || preparedGame is null || profile is null)
                return null;

            PublishLaunching();
            var issue = await GameLaunchRegistry
                .TryIssueLaunchAsync(context, preparedGame, ProfileLaunchSelection.From(profile), cancellationToken)
                .ConfigureAwait(false);
            if (issue.IsIssued)
                return issue.Launch;

            Log.Warn("JunimoGate.Launcher", $"launch-request-not-issued status={issue.Status}");

            if (issue.Status == GameLaunchIssueStatus.ActiveSnapshotChanged)
            {
                preparedGame = null;
                Publish(new LauncherState(
                    LauncherStatus.Checking,
                    "The prepared game changed. Checking it again…",
                    ShowProgress: true,
                    CanLaunch: false));
                var refreshed = await GameDeepPrepareCoordinator
                    .PrepareOrReuseAsync(context, new PreparationProgress(this), cancellationToken)
                    .ConfigureAwait(false);
                PublishPreparationResult(refreshed);
                return null;
            }

            if (issue.Status == GameLaunchIssueStatus.ProfileChanged)
            {
                profile = await profiles.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
                Publish(ReadyState(preparedGame));
                return null;
            }

            Publish(new LauncherState(
                LauncherStatus.Preparing,
                "The installed game changed. Preparing it again…",
                ShowProgress: true,
                CanLaunch: false));
            var prepared = await GameDeepPrepareCoordinator
                .PrepareAsync(context, new PreparationProgress(this), cancellationToken)
                .ConfigureAwait(false);
            PublishPreparationResult(prepared);
            if (!prepared.IsReady || preparedGame is null)
                return null;

            PublishLaunching();
            issue = await GameLaunchRegistry
                .TryIssueLaunchAsync(context, preparedGame, ProfileLaunchSelection.From(profile), cancellationToken)
                .ConfigureAwait(false);
            if (issue.IsIssued)
                return issue.Launch;

            Log.Error("JunimoGate.Launcher", $"launch-request-rejected-after-prepare status={issue.Status}");
            preparedGame = null;
            Publish(FailedState("launch_state_changed_after_prepare"));
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Launcher", "launch-request-failed", exception);
            preparedGame = null;
            Publish(FailedState());
        }
        finally
        {
            operationLock.Release();
        }

        return null;
    }

    public async ValueTask UpdateBindingPolicyAsync(
        ModAssemblyBindingPolicy policy,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentState.Status != LauncherStatus.Ready || preparedGame is null || profile is null)
                return;
            profile = await profiles.UpdateBindingPolicyAsync(
                    profileId,
                    profile.Revision,
                    policy,
                    cancellationToken)
                .ConfigureAwait(false);
            Publish(ReadyState(preparedGame));
        }
        finally
        {
            operationLock.Release();
        }
    }

    public void ReportLaunchFailure()
    {
        if (!disposed)
        {
            preparedGame = null;
            Publish(FailedState());
        }
    }

    public async ValueTask<GameLaunchHandle?> RecoverLaunchDispatchFailureAsync(
        GameLaunchHandle launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        try
        {
            await GameLaunchRegistry.RecordOutcomeAsync(
                    context,
                    launch.Key,
                    GameLaunchOutcomeStatus.Failed,
                    GameStartupStage.LaunchRequest,
                    "activity_start_failed",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn("JunimoGate.Launcher", "launch-dispatch-outcome-failed", exception);
        }
        return await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GameLaunchHandle?> RecoverPendingLaunchAsync(
        PendingGameLaunchOutcome pending,
        CancellationToken cancellationToken)
    {
        Log.Warn(
            "JunimoGate.Recovery",
            $"startup-failure attempt={pending.AttemptId[..8]} stage={pending.Stage} code={pending.Code} level={pending.RecoveryLevel}");
        await GameLaunchRegistry.CompletePendingFailureAsync(context, pending, cancellationToken)
            .ConfigureAwait(false);
        if (pending.Code == "mod_loading_failed" || pending.RecoveryLevel >= 2)
        {
            Log.Error(
                "JunimoGate.Recovery",
                $"automatic-recovery-stopped stage={pending.Stage} code={pending.Code} level={pending.RecoveryLevel}");
            Publish(FailedState(pending.Code));
            return null;
        }

        for (var level = pending.RecoveryLevel + 1; level <= 2; level++)
        {
            Log.Info(
                "JunimoGate.Recovery",
                $"automatic-recovery-started stage={pending.Stage} code={pending.Code} level={level}");
            Publish(new LauncherState(
                LauncherStatus.Recovering,
                "Preparing Stardew Valley for launch…",
                ShowProgress: true,
                CanLaunch: false));
            var prepared = await GameDeepPrepareCoordinator
                .RecoverAsync(
                    context,
                    pending.Snapshot,
                    pending.Stage,
                    level,
                    new PreparationProgress(this),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!prepared.IsReady)
            {
                Log.Warn(
                    "JunimoGate.Recovery",
                    $"automatic-recovery-prepare-failed status={prepared.Status} code={prepared.Code} level={level}");
                if (prepared.Status is GamePreparationStatus.GameNotInstalled or GamePreparationStatus.Unsupported || level == 2)
                {
                    PublishPreparationResult(prepared);
                    return null;
                }
                continue;
            }

            preparedGame = prepared.PreparedGame;
            PublishLaunching();
            var issue = await GameLaunchRegistry
                .TryIssueLaunchAsync(context, preparedGame!, pending.Profile, level, cancellationToken)
                .ConfigureAwait(false);
            if (issue.IsIssued)
            {
                Log.Info("JunimoGate.Recovery", $"automatic-recovery-launch-issued level={level}");
                return issue.Launch;
            }
            Log.Warn(
                "JunimoGate.Recovery",
                $"automatic-recovery-launch-not-issued status={issue.Status} level={level}");
            if (level == 2)
                break;
        }

        preparedGame = null;
        Log.Error("JunimoGate.Recovery", "automatic-recovery-exhausted");
        Publish(FailedState("startup_recovery_exhausted"));
        return null;
    }

    public void Dispose()
    {
        disposed = true;
        StateChanged = null;
    }

    private void Publish(LauncherState state)
    {
        if (disposed || CurrentState == state)
            return;
        if (profile is not null)
            state = state with { AssemblyBindingPolicy = profile.AssemblyBindingPolicy };
        CurrentState = state;
        Log.Info("JunimoGate.Launcher", $"state:{state.Status}");
        StateChanged?.Invoke(state);
    }

    private void PublishPreparationResult(GamePreparationResult result)
    {
        Log.Info(
            "JunimoGate.Launcher",
            $"preparation-result status={result.Status} code={result.Code} ready={(result.IsReady ? 1 : 0)}");
        preparedGame = result.IsReady ? result.PreparedGame : null;
        Publish(Map(result));
    }

    private LauncherState Map(GamePreparationResult result) => result.Status switch
    {
        GamePreparationStatus.Ready => ReadyState(result.PreparedGame!),
        GamePreparationStatus.GameNotInstalled => new LauncherState(
            LauncherStatus.GameNotInstalled,
            "Stardew Valley is not installed for the current Android user.",
            ShowProgress: false,
            CanLaunch: false),
        GamePreparationStatus.Unsupported => new LauncherState(
            LauncherStatus.Unsupported,
            $"This installed Stardew Valley build is not supported yet.\n\nCode: {result.Code}",
            ShowProgress: false,
            CanLaunch: false),
        _ => FailedState(result.Code),
    };

    private static LauncherState FailedState(string code = "launcher_failed") => new(
        LauncherStatus.Failed,
        $"Stardew Valley could not start.\n\nCode: {code}",
        ShowProgress: false,
        CanLaunch: true);

    private LauncherState ReadyState(PreparedGameHandle handle) => new(
        LauncherStatus.Ready,
        $"Stardew Valley {handle.VersionName}\n\nReady to launch through SMAPI.",
        ShowProgress: false,
        CanLaunch: true,
        AssemblyBindingPolicy: profile?.AssemblyBindingPolicy ?? ModAssemblyBindingPolicy.HighestCompatible,
        CanConfigureProfile: true);

    private void PublishLaunching() => Publish(new LauncherState(
        LauncherStatus.Launching,
        "Starting Stardew Valley through SMAPI…",
        ShowProgress: true,
        CanLaunch: false));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class PreparationProgress(LauncherCoordinator owner) : IProgress<GamePreparationProgress>
    {
        public void Report(GamePreparationProgress value)
        {
            var status = value.Stage == GamePreparationStage.Preparing
                ? LauncherStatus.Preparing
                : LauncherStatus.Checking;
            owner.Publish(new LauncherState(
                status,
                value.Message,
                ShowProgress: true,
                CanLaunch: false));
        }
    }
}
