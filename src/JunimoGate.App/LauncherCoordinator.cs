using Android.Content;
using Android.Util;
using JunimoGate.GameHost;

namespace JunimoGate.App;

internal enum LauncherStatus
{
    Checking,
    Preparing,
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
    bool CanLaunch);

internal sealed class LauncherCoordinator : IDisposable
{
    private readonly Context context;
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private PreparedGameHandle? preparedGame;
    private bool disposed;

    public LauncherCoordinator(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
        CurrentState = new LauncherState(
            LauncherStatus.Checking,
            "Checking the installed game…",
            ShowProgress: true,
            CanLaunch: false);
    }

    public event Action<LauncherState>? StateChanged;

    public LauncherState CurrentState { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (preparedGame is not null)
            {
                Publish(ReadyState(preparedGame));
                return;
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            preparedGame = null;
            Publish(FailedState());
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
            if (CurrentState.Status != LauncherStatus.Ready || preparedGame is null)
                return null;

            PublishLaunching();
            var issue = await GameLaunchRegistry
                .TryIssueLaunchAsync(context, preparedGame, cancellationToken)
                .ConfigureAwait(false);
            if (issue.IsIssued)
                return issue.Launch;

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
                .TryIssueLaunchAsync(context, preparedGame, cancellationToken)
                .ConfigureAwait(false);
            if (issue.IsIssued)
                return issue.Launch;

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
            preparedGame = null;
            Publish(FailedState());
        }
        finally
        {
            operationLock.Release();
        }

        return null;
    }

    public void ReportLaunchFailure()
    {
        if (!disposed)
        {
            preparedGame = null;
            Publish(FailedState());
        }
    }

    public void Dispose()
    {
        disposed = true;
        StateChanged = null;
    }

    private void Publish(LauncherState state)
    {
        if (disposed)
            return;
        CurrentState = state;
        Log.Info("JunimoGate.Launcher", $"state:{state.Status}");
        StateChanged?.Invoke(state);
    }

    private void PublishPreparationResult(GamePreparationResult result)
    {
        preparedGame = result.IsReady ? result.PreparedGame : null;
        Publish(Map(result));
    }

    private static LauncherState Map(GamePreparationResult result) => result.Status switch
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
        $"JunimoGate could not prepare the game. Reopen the app to retry.\n\nCode: {code}",
        ShowProgress: false,
        CanLaunch: false);

    private static LauncherState ReadyState(PreparedGameHandle handle) => new(
        LauncherStatus.Ready,
        $"Stardew Valley {handle.VersionName}\n\nReady to launch through SMAPI.",
        ShowProgress: false,
        CanLaunch: true);

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
