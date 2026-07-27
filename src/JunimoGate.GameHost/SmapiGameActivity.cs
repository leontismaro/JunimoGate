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

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
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

        try
        {
            var key = Intent?.GetStringExtra(LaunchKeyExtra) ?? throw new InvalidDataException("The launch capability is missing.");
            var snapshot = await GameLaunchRegistry.ConsumeAsync(this, key, CancellationToken.None);
            await BundledSmapiAssets.ProvisionAndValidateAsync(this, snapshot.InternalDirectory, CancellationToken.None);
            var runtimeFiles = PreparedRuntimeFiles.BuildAndValidate(snapshot);
            loader = new SmapiDefaultAssemblyLoader(snapshot, runtimeFiles);
            loader.Install();
            SmapiContentBridge.Install(runtimeFiles);
            GameHostBridge.Attach(this, snapshot);
            _ = loader.LoadGameAssembly();
            GameSessionRegistry.MarkActive(this);
            Log.Info("JunimoGate.SMAPI", $"session-starting:build={GameLaunchSchema.BuildId}:smapi=4.3.2.5");
            CreateAndRunSession(snapshot, loader);
        }
        catch (Exception ex)
        {
            Log.Error("JunimoGate.SMAPI", $"startup-failed:{ex.GetType().Name}");
            FailStartup();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CreateAndRunSession(PreparedGameSnapshot snapshot, SmapiDefaultAssemblyLoader assemblyLoader)
    {
        var runtime = new SmapiRuntime(new SmapiRuntimeOptions
        {
            Activity = this,
            GameAssemblyDirectory = snapshot.SourceWorkspacePath,
            ContentDirectory = Path.Combine(snapshot.SourceWorkspacePath, "Content"),
            ModsDirectory = snapshot.ModsDirectory,
            InternalDirectory = snapshot.InternalDirectory,
            ConfigDirectory = snapshot.ConfigDirectory,
            LogDirectory = snapshot.LogDirectory,
            SaveDirectory = snapshot.SaveDirectory,
            BackupDirectory = snapshot.BackupDirectory,
            MainThread = new ActivityDispatcher(this),
            AssemblyLoader = assemblyLoader,
            AttachGameView = view => RunOnUiThread(() => SetContentView(view)),
            ReportFailure = failure => Log.Error("JunimoGate.SMAPI", $"{failure.Code}:{failure.Exception?.GetType().Name ?? "none"}"),
        });
        session = runtime.CreateSession();
        session.Run();
    }

    protected override void OnResume() { base.OnResume(); session?.OnResume(); SetImmersive(); }
    protected override void OnPause() { session?.OnPause(); base.OnPause(); }
    protected override void OnNewIntent(global::Android.Content.Intent? intent) { base.OnNewIntent(intent); Log.Info("JunimoGate.SMAPI", "session-routed-to-front"); }
    public override void OnWindowFocusChanged(bool hasFocus) { base.OnWindowFocusChanged(hasFocus); session?.OnWindowFocusChanged(hasFocus); if (hasFocus) SetImmersive(); }
#pragma warning disable CS0672
    public override void OnBackPressed() => HandleSystemBack();
#pragma warning restore CS0672

    protected override void OnDestroy()
    {
        var terminateGameProcess = IsFinishing;
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
                "SMAPI startup failed. Return to JunimoGate and prepare the game again.",
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
