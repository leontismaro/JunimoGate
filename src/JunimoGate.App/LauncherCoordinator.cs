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
            Publish(new LauncherState(
                LauncherStatus.Checking,
                "Checking the installed game and prepared workspace…",
                ShowProgress: true,
                CanLaunch: false));
            var progress = new PreparationProgress(this);
            var result = await GameDeepPrepareCoordinator
                .PrepareOrReuseAsync(context, progress, cancellationToken)
                .ConfigureAwait(false);
            Publish(Map(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
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
        var needsRefresh = false;
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentState.Status != LauncherStatus.Ready)
                return null;

            Publish(new LauncherState(
                LauncherStatus.Launching,
                "Starting Stardew Valley through SMAPI…",
                ShowProgress: true,
                CanLaunch: false));
            var handle = await GameLaunchRegistry
                .TryIssueActiveLaunchAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (handle is not null)
                return handle;

            needsRefresh = true;
            Publish(new LauncherState(
                LauncherStatus.Checking,
                "The installed game changed. Checking it again…",
                ShowProgress: true,
                CanLaunch: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Publish(FailedState());
        }
        finally
        {
            operationLock.Release();
        }

        if (needsRefresh)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    public void ReportLaunchFailure()
    {
        if (!disposed)
            Publish(FailedState());
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

    private static LauncherState Map(GamePreparationResult result) => result.Status switch
    {
        GamePreparationStatus.Ready => new LauncherState(
            LauncherStatus.Ready,
            $"Stardew Valley {result.PreparedGame!.VersionName}\n\nReady to launch through SMAPI.",
            ShowProgress: false,
            CanLaunch: true),
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
