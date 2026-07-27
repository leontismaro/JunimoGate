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
using JunimoGate.Android;
using JunimoGate.Core;
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
            await VerifyPackageMarkerAsync(snapshot);
            GameSessionRegistry.MarkActive(this);
            await ProvisionInternalFilesAsync(snapshot.InternalDirectory);
            loader = new SmapiDefaultAssemblyLoader(snapshot);
            loader.Install();
            SmapiContentBridge.Install(snapshot);
            GameHostBridge.Attach(this, snapshot);
            _ = loader.LoadGameAssembly();
            Log.Info("JunimoGate.SMAPI", $"session-starting:build={GameLaunchSchema.BuildId}:smapi=4.3.2.5");
            CreateAndRunSession(snapshot, loader);
        }
        catch (Exception ex)
        {
            Log.Error("JunimoGate.SMAPI", $"startup-failed:{ex.GetType().Name}");
            ShowFailure("SMAPI startup failed safely. Return to JunimoGate and run Deep Prepare again.");
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
        SmapiContentBridge.Detach();
        GameHostBridge.Detach(this);
        loader?.Dispose();
        loader = null;
        base.OnDestroy();
        if (terminateGameProcess)
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    private async Task VerifyPackageMarkerAsync(PreparedGameSnapshot snapshot)
    {
        var package = await new AndroidPackageInstallationSnapshotProvider(this).GetSnapshotAsync(snapshot.PackageName, CancellationToken.None)
            ?? throw new InvalidOperationException("The prepared game package is no longer installed.");
        if (package.VersionName != snapshot.VersionName || package.LongVersionCode != snapshot.VersionCode ||
            package.SigningIdentity is null ||
            !KnownGameCertificate.Verify(snapshot.PackageName, package.SigningIdentity).AllowsCodeExecution ||
            PackageUpdateMarker.Create(package) != snapshot.PackageMarker)
            throw new InvalidOperationException("The game identity changed after Deep Prepare.");
    }

    private async Task ProvisionInternalFilesAsync(string target)
    {
        foreach (var name in new[] { "config.json", "metadata.json", "blacklist.json" })
            await CopyAssetAsync($"smapi-internal/{name}", Path.Combine(target, name));
        var i18n = Path.Combine(target, "i18n"); Directory.CreateDirectory(i18n);
        foreach (var name in Assets?.List("smapi-internal/i18n") ?? [])
            await CopyAssetAsync($"smapi-internal/i18n/{name}", Path.Combine(i18n, name));

        var managed = Path.Combine(Path.GetDirectoryName(target)!, "managed");
        Directory.CreateDirectory(managed);
        foreach (var name in new[]
        {
            "StardewModdingAPI.dll",
            "StardewModdingAPI.Toolkit.dll",
            "StardewModdingAPI.Toolkit.CoreInterfaces.dll",
        })
            await CopyAssetAsync($"smapi-managed/{name}", Path.Combine(managed, name));
    }

    private async Task CopyAssetAsync(string asset, string target)
    {
        if (File.Exists(target)) return;
        await using var input = Assets!.Open(asset);
        await using var output = new FileStream(target + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output);
        output.Flush(true);
        File.Move(target + ".tmp", target, overwrite: false);
    }
    private void ShowFailure(string message) => RunOnUiThread(() => { if (!destroyed && status is not null) status.Text = message; });
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
