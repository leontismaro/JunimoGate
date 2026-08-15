using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using Android.Window;
using Microsoft.Xna.Framework;
using StardewModdingAPI.AndroidHost;
using JunimoGate.Mods;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.GameHost;

[Activity(
    Name = ActivityName,
    Label = "JunimoGate SMAPI",
    Exported = false,
    Process = ":game",
    LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Orientation |
        ConfigChanges.ScreenLayout | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class SmapiGameActivity : AndroidGameActivity
{
    public const string ActivityName = "org.junimogate.gamehost.SmapiGameActivity";
    public const string LaunchKeyExtra = "org.junimogate.extra.LAUNCH_KEY";
    private SmapiDefaultAssemblyLoader? loader;
    private SmapiSession? session;
    private FrameLayout? gameViewContainer;
    private View? loadingOverlay;
    private BackInvokedCallback? backInvokedCallback;
    private long lastBackHandledUptime;
    private bool destroyed;
    private bool activityResumed;
    private bool playSessionRunning;
    private string? lastSmapiFailureCode;
    private GamePlaySessionRepository? playSessions;
    private string? playSessionId;
    private CancellationTokenSource? checkpointCancellation;
    private Task? checkpointTask;
    private CancellationTokenSource? backgroundPersistenceCancellation;
    private Task? backgroundPersistenceTask;
    private static readonly TimeSpan BackgroundPersistenceDelay = TimeSpan.FromMilliseconds(250);
    private const long LoadingFadeDurationMilliseconds = 180L;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture; // per StardewValley.Program.Main
        base.OnCreate(savedInstanceState);
        Log.Initialize(this, "game", GameHostRuntimeIdentity.BuildId);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterBackInvokedCallback();
        SetContentView(Resource.Layout.activity_smapi_game);
        gameViewContainer = FindViewById<FrameLayout>(Resource.Id.smapi_game_view_container)
            ?? throw new InvalidOperationException("The SMAPI game view container is unavailable.");
        loadingOverlay = FindViewById(Resource.Id.smapi_loading_overlay)
            ?? throw new InvalidOperationException("The SMAPI loading overlay is unavailable.");
        if (savedInstanceState is not null)
        {
            Log.Warn("JunimoGate.SMAPI", "session-recreation-returning-to-launcher");
            Finish();
            return;
        }

        var attemptId = Intent?.GetStringExtra(LaunchKeyExtra);
        var stage = GameStartupStage.LaunchRequest;
        try
        {
            var key = attemptId ?? throw new InvalidDataException("The launch capability is missing.");
            GameSessionRegistry.MarkActive(this);
            var launch = await GameLaunchRegistry.ConsumeAsync(this, key, CancellationToken.None);
            var snapshot = launch.Snapshot;
            stage = GameStartupStage.SmapiBundle;
            var smapiBundle = await BundledSmapiAssets.ProvisionAndValidateAsync(
                this,
                CancellationToken.None);
            await TryBeginPlaySessionAsync(launch, snapshot, smapiBundle);
            stage = GameStartupStage.RuntimeInventory;
            var runtimeFiles = PreparedRuntimeFiles.BuildAndValidate(snapshot, smapiBundle);
            stage = GameStartupStage.LoaderInstallation;
            var runtimeRoot = JunimoGate.Android.AndroidPrivateStorage.GetRuntimeRoot(ApplicationContext ?? this);
            loader = new SmapiDefaultAssemblyLoader(
                runtimeFiles,
                Path.Combine(runtimeRoot, "smapi", "assembly-load-cache-v1", smapiBundle.BundleId),
                launch.ModsRoot);
            loader.Install();
            SmapiContentBridge.Install(runtimeFiles);
            GameHostBridge.Attach(this, snapshot);
            stage = GameStartupStage.GameAssembly;
            _ = loader.LoadGameAssembly();
            Log.Info(
                "JunimoGate.SMAPI",
                $"session-starting:build={GameHostRuntimeIdentity.BuildId}:bundle={smapiBundle.BundleId}:smapi=4.5.2");
            stage = GameStartupStage.SmapiSession;
            PreservePreviousSmapiLog(snapshot.LogDirectory);
            var startupCompletion = new TaskCompletionSource<SmapiFailure?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CreateAndRunSession(snapshot, launch, smapiBundle, loader, startupCompletion);
            var reportedFailure = await startupCompletion.Task;
            if (reportedFailure is not null)
            {
                throw new InvalidOperationException(
                    $"SMAPI reported startup failure '{reportedFailure.Code}'.",
                    reportedFailure.Exception);
            }
            await TryMarkPlaySessionRunningAsync();
            stage = GameStartupStage.Running;
            try
            {
                await GameLaunchRegistry.RecordOutcomeAsync(
                    this,
                    launch.AttemptId,
                    GameLaunchOutcomeStatus.Running,
                    stage,
                    "session_running",
                    CancellationToken.None);
            }
            catch (Exception outcomeException) when (outcomeException is IOException or UnauthorizedAccessException)
            {
                Log.Error("JunimoGate.SMAPI", "startup-outcome-failed", outcomeException);
            }
        }
        catch (Exception ex)
        {
            Log.Error(
                "JunimoGate.SMAPI",
                $"startup-failed stage={stage} code={lastSmapiFailureCode ?? "unclassified"}",
                ex);
            if (attemptId is not null)
            {
                try
                {
                    await GameLaunchRegistry.RecordOutcomeAsync(
                        this,
                        attemptId,
                        GameLaunchOutcomeStatus.Failed,
                        stage,
                        lastSmapiFailureCode ?? $"startup_{stage.ToString().ToLowerInvariant()}",
                        CancellationToken.None);
                }
                catch (Exception outcomeException)
                {
                    Log.Error("JunimoGate.SMAPI", "startup-outcome-failed", outcomeException);
                }
            }
            FailStartup();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateAndRunSession(
        PreparedGameSnapshot snapshot,
        ConsumedGameLaunch launch,
        PreparedSmapiBundle smapiBundle,
        SmapiDefaultAssemblyLoader assemblyLoader,
        TaskCompletionSource<SmapiFailure?> startupCompletion)
    {
        var runtime = new SmapiRuntime(new SmapiRuntimeOptions
        {
            Activity = this,
            GameAssemblyDirectory = snapshot.SourceWorkspacePath,
            ContentDirectory = Path.Combine(snapshot.SourceWorkspacePath, "Content"),
            ModsDirectory = launch.ModsRoot,
            ModDirectories = launch.ModDirectories,
            InternalDirectory = smapiBundle.InternalDirectory,
            ConfigDirectory = snapshot.ConfigDirectory,
            LogDirectory = snapshot.LogDirectory,
            SaveDirectory = snapshot.SaveDirectory,
            BackupDirectory = snapshot.BackupDirectory,
            ModRewriteCacheDirectory = Path.Combine(
                JunimoGate.Android.AndroidPrivateStorage.GetRuntimeRoot(ApplicationContext ?? this),
                "smapi",
                "mod-rewrite-cache-v2",
                smapiBundle.BundleId),
            ModRewriteCacheIdentity = string.Join(
                '|',
                smapiBundle.BundleId,
                snapshot.SourceWorkspaceKey,
                snapshot.AppliedWorkspaceKey),
            MainThread = new ActivityDispatcher(this),
            AssemblyLoader = assemblyLoader,
            ShowLoadingLogsOnScreen = false,
            AssemblyBindingPolicy = launch.Profile.AssemblyBindingPolicy switch
            {
                JunimoGate.Mods.ModAssemblyBindingPolicy.Strict => StardewModdingAPI.AndroidHost.ModAssemblyBindingPolicy.Strict,
                JunimoGate.Mods.ModAssemblyBindingPolicy.FirstLoaded => StardewModdingAPI.AndroidHost.ModAssemblyBindingPolicy.FirstLoaded,
                JunimoGate.Mods.ModAssemblyBindingPolicy.HighestCompatible => StardewModdingAPI.AndroidHost.ModAssemblyBindingPolicy.HighestCompatible,
                _ => throw new InvalidDataException("The launch Profile binding policy is invalid."),
            },
            AttachGameView = view => RunOnUiThread(() => AttachGameView(view)),
            ReportModLoadingReady = () =>
            {
                Log.Info("JunimoGate.SMAPI", "mod-loading-ready");
                startupCompletion.TrySetResult(null);
            },
            ReportGameViewReady = () => RunOnUiThread(RevealGameView),
            ReportFailure = failure =>
            {
                lastSmapiFailureCode = failure.Code;
                if (failure.Exception is null)
                    Log.Error("JunimoGate.SMAPI", $"smapi-failure code={failure.Code} message={failure.Message}");
                else
                    Log.Error("JunimoGate.SMAPI", $"smapi-failure code={failure.Code}", failure.Exception);
                startupCompletion.TrySetResult(failure);
            },
        });
        session = runtime.CreateSession();
        session.Run();
    }

    private void AttachGameView(View gameView)
    {
        if (destroyed || IsFinishing)
            return;
        var container = gameViewContainer
            ?? throw new InvalidOperationException("The SMAPI game view container is unavailable.");
        if (ReferenceEquals(gameView.Parent, container))
            return;
        if (gameView.Parent is ViewGroup parent)
            parent.RemoveView(gameView);
        container.RemoveAllViews();
        container.AddView(
            gameView,
            new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent));
    }

    private void RevealGameView()
    {
        if (destroyed || IsFinishing || loadingOverlay is not { } overlay)
            return;
        overlay.Animate()?.Cancel();
        overlay.Animate()
            ?.Alpha(0f)
            .SetDuration(LoadingFadeDurationMilliseconds)
            .Start();
        overlay.PostDelayed(
            () =>
            {
                if (!ReferenceEquals(loadingOverlay, overlay))
                    return;
                overlay.Visibility = ViewStates.Gone;
                loadingOverlay = null;
            },
            LoadingFadeDurationMilliseconds);
        Log.Info("JunimoGate.SMAPI", "game-view-ready");
    }

    protected override void OnResume()
    {
        base.OnResume();
        activityResumed = true;
        Log.Info("JunimoGate.SMAPI", $"activity-resumed sessionCreated={(session is null ? 0 : 1)}");
        CancelPendingBackgroundPersistence();
        TryMarkPlaySessionForeground();
        session?.OnResume();
        SetImmersive();
    }

    protected override void OnPause()
    {
        activityResumed = false;
        var finishing = IsFinishing;
        Log.Info(
            "JunimoGate.SMAPI",
            $"activity-paused sessionCreated={(session is null ? 0 : 1)} finishing={(finishing ? 1 : 0)}");
        session?.OnPause();
        QueuePlaySessionBackgroundPersistence();
        base.OnPause();
    }
    protected override void OnNewIntent(global::Android.Content.Intent? intent) { base.OnNewIntent(intent); Log.Info("JunimoGate.SMAPI", "session-routed-to-front"); }
    public override void OnWindowFocusChanged(bool hasFocus) { base.OnWindowFocusChanged(hasFocus); session?.OnWindowFocusChanged(hasFocus); if (hasFocus) SetImmersive(); }
#pragma warning disable CS0672
    public override void OnBackPressed() => HandleSystemBack();
#pragma warning restore CS0672

    protected override void OnDestroy()
    {
        var terminateGameProcess = IsFinishing;
        Log.Info(
            "JunimoGate.SMAPI",
            $"activity-destroyed finishing={(IsFinishing ? 1 : 0)} changingConfiguration={(IsChangingConfigurations ? 1 : 0)} terminateProcess={(terminateGameProcess ? 1 : 0)}");
        destroyed = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            UnregisterBackInvokedCallback();
        CompletePlaySession(GamePlaySessionOutcomes.Completed, failureCode: null);
        session?.Dispose();
        session = null;
        loadingOverlay = null;
        gameViewContainer = null;
        GameSessionRegistry.ClearCurrentProcess(this);
        ReleaseRuntimeHooks();
        base.OnDestroy();
        if (terminateGameProcess)
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    private void FailStartup()
    {
        CompletePlaySession(GamePlaySessionOutcomes.Failed, lastSmapiFailureCode);
        GameSessionRegistry.ClearCurrentProcess(this);
        session?.Dispose();
        session = null;
        ReleaseRuntimeHooks();
        RunOnUiThread(() =>
        {
            if (destroyed || IsFinishing)
                return;
            Toast.MakeText(
                this,
                "Stardew Valley could not start. Returning to JunimoGate.",
                ToastLength.Long)?.Show();
            Finish();
        });
    }

    private void ReleaseRuntimeHooks()
    {
        SmapiContentBridge.Detach();
        GameHostBridge.Detach(this);
        loader?.Dispose();
        loader = null;
    }

    private void PreservePreviousSmapiLog(string logDirectory)
    {
        var current = Path.Combine(logDirectory, "SMAPI-latest.txt");
        if (!File.Exists(current))
            return;
        try
        {
            var destination = Path.Combine(
                JunimoGate.Android.AndroidPrivateStorage.GetProductLogsRoot(ApplicationContext ?? this),
                "smapi-previous.txt");
            File.Move(current, destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn("JunimoGate.SMAPI", "smapi-previous-log-retention-failed", exception);
        }
    }

    private async ValueTask TryBeginPlaySessionAsync(
        ConsumedGameLaunch launch,
        PreparedGameSnapshot snapshot,
        PreparedSmapiBundle smapiBundle)
    {
        try
        {
            var repository = new GamePlaySessionRepository(Path.Combine(
                JunimoGate.Android.AndroidPrivateStorage.GetUserDataRoot(ApplicationContext ?? this),
                "sessions"));
            var profileId = ProfileId.Parse(launch.ModSelection?.ProfileId ?? launch.Profile.ProfileId);
            var sessionRecord = await repository.BeginAsync(
                new GamePlaySessionMetadata(
                    profileId,
                    launch.ModSelection?.ProfileRevision ?? launch.Profile.Revision,
                    launch.ModSelection?.Items.Count ?? launch.ModDirectories?.Count ?? 0,
                    snapshot.VersionName,
                    GameHostRuntimeIdentity.BuildId,
                    smapiBundle.BundleId),
                cancellationToken: CancellationToken.None);
            playSessions = repository;
            playSessionId = sessionRecord.SessionId;
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.Sessions", "session-begin-failed", exception);
            playSessions = null;
            playSessionId = null;
        }
    }

    private async ValueTask TryMarkPlaySessionRunningAsync()
    {
        if (playSessions is not { } repository || playSessionId is not { } sessionId)
            return;
        try
        {
            await repository.MarkRunningAsync(
                sessionId,
                activityResumed,
                cancellationToken: CancellationToken.None);
            playSessionRunning = true;
            StartCheckpointLoop();
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.Sessions", "session-running-failed", exception);
        }
    }

    private void TryMarkPlaySessionForeground()
    {
        if (!playSessionRunning || playSessions is not { } repository || playSessionId is not { } sessionId)
            return;
        TryPersistPlaySession(
            "session-foreground-failed",
            () => repository.MarkForegroundAsync(sessionId, cancellationToken: CancellationToken.None));
    }

    private void QueuePlaySessionBackgroundPersistence()
    {
        if (!playSessionRunning || playSessions is not { } repository || playSessionId is not { } sessionId)
            return;
        CancelPendingBackgroundPersistence();
        var cancellation = new CancellationTokenSource();
        backgroundPersistenceCancellation = cancellation;
        backgroundPersistenceTask = PersistPlaySessionBackgroundAsync(
            repository,
            sessionId,
            DateTimeOffset.UtcNow,
            cancellation.Token);
    }

    private static async Task PersistPlaySessionBackgroundAsync(
        GamePlaySessionRepository repository,
        string sessionId,
        DateTimeOffset pausedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(BackgroundPersistenceDelay, cancellationToken).ConfigureAwait(false);
            await repository.MarkBackgroundAsync(
                    sessionId,
                    pausedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A quick resume or final Activity destruction supersedes this intermediate checkpoint.
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.Sessions", "session-background-failed", exception);
        }
    }

    private void StartCheckpointLoop()
    {
        if (checkpointTask is not null || playSessions is null || playSessionId is null)
            return;
        checkpointCancellation = new CancellationTokenSource();
        checkpointTask = RunCheckpointLoopAsync(
            playSessions,
            playSessionId,
            checkpointCancellation.Token);
    }

    private static async Task RunCheckpointLoopAsync(
        GamePlaySessionRepository repository,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(GamePlaySessionRepository.CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await repository.CheckpointAsync(sessionId, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session completion owns the final foreground accumulation.
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.Sessions", "session-checkpoint-failed", exception);
        }
    }

    private void CompletePlaySession(string outcome, string? failureCode)
    {
        StopCheckpointLoop();
        WaitForBackgroundPersistence();
        var repository = playSessions;
        var sessionId = playSessionId;
        playSessions = null;
        playSessionId = null;
        playSessionRunning = false;
        if (repository is null || sessionId is null)
            return;
        TryPersistPlaySession(
            "session-end-failed",
            () => repository.EndAsync(
                sessionId,
                outcome,
                failureCode,
                cancellationToken: CancellationToken.None));
    }

    private void WaitForBackgroundPersistence()
    {
        backgroundPersistenceCancellation?.Cancel();
        try
        {
            backgroundPersistenceTask?.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.Sessions", "session-background-await-failed", exception);
        }
        finally
        {
            backgroundPersistenceCancellation?.Dispose();
            backgroundPersistenceCancellation = null;
            backgroundPersistenceTask = null;
        }
    }

    private void CancelPendingBackgroundPersistence()
    {
        if (backgroundPersistenceTask is null)
            return;
        WaitForBackgroundPersistence();
    }

    private void StopCheckpointLoop()
    {
        checkpointCancellation?.Cancel();
        try
        {
            checkpointTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The checkpoint loop treats cancellation as normal completion.
        }
        checkpointCancellation?.Dispose();
        checkpointCancellation = null;
        checkpointTask = null;
    }

    private static void TryPersistPlaySession(string failureEvent, Func<ValueTask> operation)
    {
        try
        {
            operation().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.Sessions", failureEvent, exception);
        }
    }

    private static void TryPersistPlaySession<T>(string failureEvent, Func<ValueTask<T>> operation)
    {
        try
        {
            _ = operation().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.Sessions", failureEvent, exception);
        }
    }

    private void HandleSystemBack()
    {
        if (destroyed || IsFinishing)
            return;
        var now = SystemClock.UptimeMillis();
        if (now - lastBackHandledUptime < 250)
            return;
        lastBackHandledUptime = now;
        if (session?.TryHandleBack() == true)
        {
            Log.Info("JunimoGate.SMAPI", "back-forwarded-to-game");
            return;
        }

        Log.Info("JunimoGate.SMAPI", "back-backgrounded-before-session-ready");
        MoveTaskToBack(nonRoot: true);
    }

    [SupportedOSPlatform("android33.0")]
    private void RegisterBackInvokedCallback()
    {
        backInvokedCallback = new BackInvokedCallback(this);
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(
            IOnBackInvokedDispatcher.PriorityDefault,
            backInvokedCallback);
    }

    [SupportedOSPlatform("android33.0")]
    private void UnregisterBackInvokedCallback()
    {
        if (backInvokedCallback is null)
            return;
        OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(backInvokedCallback);
        backInvokedCallback.Dispose();
        backInvokedCallback = null;
    }
    private void SetImmersive()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var controller = Window?.InsetsController;
            if (controller is not null)
            {
                controller.Hide(WindowInsets.Type.SystemBars());
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            return;
        }

        var decor = Window?.DecorView;
        if (decor is not null)
            decor.SystemUiFlags = SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen |
                SystemUiFlags.HideNavigation | SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutStable;
    }

    private sealed class ActivityDispatcher(Activity activity) : IMainThreadDispatcher
    {
        public bool IsMainThread => Looper.MyLooper() == Looper.MainLooper;
        public void Invoke(Action callback)
        {
            if (IsMainThread) { callback(); return; }
            Exception? error = null; using var done = new ManualResetEventSlim();
            activity.RunOnUiThread(() => { try { callback(); } catch (Exception ex) { error = ex; } finally { done.Set(); } });
            done.Wait(); if (error is not null) throw error;
        }
    }

    private sealed class BackInvokedCallback(SmapiGameActivity activity)
        : Java.Lang.Object, IOnBackInvokedCallback
    {
        private readonly WeakReference<SmapiGameActivity> target = new(activity);

        public void OnBackInvoked()
        {
            if (target.TryGetTarget(out var activity))
                activity.HandleSystemBack();
        }
    }
}
