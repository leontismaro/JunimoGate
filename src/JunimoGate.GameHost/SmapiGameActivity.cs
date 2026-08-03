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
using Log = JunimoGate.Android.JunimoGateLog;

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
    private TextView? status;
    private BackInvokedCallback? backInvokedCallback;
    private long lastBackHandledUptime;
    private bool destroyed;
    private string? lastSmapiFailureCode;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Log.Initialize(this, "game", GameHostRuntimeIdentity.BuildId);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterBackInvokedCallback();
        status = new TextView(this) { Text = "JunimoGate SMAPI\n\nStarting prepared session…", TextSize = 16 };
        SetContentView(status);
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
            stage = GameStartupStage.RuntimeInventory;
            var runtimeFiles = PreparedRuntimeFiles.BuildAndValidate(snapshot, smapiBundle);
            stage = GameStartupStage.LoaderInstallation;
            var runtimeRoot = JunimoGate.Android.AndroidPrivateStorage.GetRuntimeRoot(ApplicationContext ?? this);
            loader = new SmapiDefaultAssemblyLoader(
                runtimeFiles,
                Path.Combine(runtimeRoot, "smapi", "assembly-load-cache-v1", GameHostRuntimeIdentity.BuildId),
                launch.ModsDirectory);
            loader.Install();
            SmapiContentBridge.Install(runtimeFiles);
            GameHostBridge.Attach(this, snapshot);
            stage = GameStartupStage.GameAssembly;
            _ = loader.LoadGameAssembly();
            Log.Info("JunimoGate.SMAPI", $"session-starting:build={GameHostRuntimeIdentity.BuildId}:smapi=4.5.2");
            stage = GameStartupStage.SmapiSession;
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
            ModsDirectory = launch.ModsDirectory,
            InternalDirectory = smapiBundle.InternalDirectory,
            ConfigDirectory = snapshot.ConfigDirectory,
            LogDirectory = snapshot.LogDirectory,
            SaveDirectory = snapshot.SaveDirectory,
            BackupDirectory = snapshot.BackupDirectory,
            ModRewriteCacheDirectory = Path.Combine(
                JunimoGate.Android.AndroidPrivateStorage.GetRuntimeRoot(ApplicationContext ?? this),
                "smapi",
                "mod-rewrite-cache-v2",
                GameHostRuntimeIdentity.BuildId),
            ModRewriteCacheIdentity = string.Join(
                '|',
                GameHostRuntimeIdentity.BuildId,
                snapshot.SourceWorkspaceKey,
                snapshot.AppliedWorkspaceKey),
            MainThread = new ActivityDispatcher(this),
            AssemblyLoader = assemblyLoader,
            AssemblyBindingPolicy = launch.Profile.AssemblyBindingPolicy switch
            {
                JunimoGate.Mods.ModAssemblyBindingPolicy.Strict => StardewModdingAPI.AndroidHost.ModAssemblyBindingPolicy.Strict,
                JunimoGate.Mods.ModAssemblyBindingPolicy.FirstLoaded => StardewModdingAPI.AndroidHost.ModAssemblyBindingPolicy.FirstLoaded,
                JunimoGate.Mods.ModAssemblyBindingPolicy.HighestCompatible => StardewModdingAPI.AndroidHost.ModAssemblyBindingPolicy.HighestCompatible,
                _ => throw new InvalidDataException("The launch Profile binding policy is invalid."),
            },
            AttachGameView = view => RunOnUiThread(() => SetContentView(view)),
            ReportModLoadingReady = () =>
            {
                Log.Info("JunimoGate.SMAPI", "mod-loading-ready");
                startupCompletion.TrySetResult(null);
            },
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

    protected override void OnResume()
    {
        base.OnResume();
        Log.Info("JunimoGate.SMAPI", $"activity-resumed sessionCreated={(session is null ? 0 : 1)}");
        session?.OnResume();
        SetImmersive();
    }

    protected override void OnPause()
    {
        Log.Info("JunimoGate.SMAPI", $"activity-paused sessionCreated={(session is null ? 0 : 1)}");
        session?.OnPause();
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
        session?.Dispose();
        session = null;
        GameSessionRegistry.ClearCurrentProcess(this);
        ReleaseRuntimeHooks();
        base.OnDestroy();
        if (terminateGameProcess)
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    private void FailStartup()
    {
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
