using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Android.Window;
using JunimoGate.Android;
using JunimoGate.Rewriter;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.GameHost;

[Activity(
    Name = "org.junimogate.gamehost.GameHostActivity",
    Label = "JunimoGate Game Host",
    Exported = false,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Keyboard |
        ConfigChanges.KeyboardHidden |
        ConfigChanges.Orientation |
        ConfigChanges.ScreenLayout |
        ConfigChanges.ScreenSize |
        ConfigChanges.UiMode)]
public sealed class GameHostActivity : AndroidGameActivity
{
    private CancellationTokenSource? preparationCancellation;
    private TextView? statusText;
    private GameHostManagedAssemblyLoadContext? managedLoadContext;
    private Game? gameRunner;
    private MonoGameAndroidGameView? gameView;
    private GameLifecycleForwarder? gameLifecycle;
    private BackInvokedCallback? backInvokedCallback;
    private volatile bool destroyed;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidEnvironment.UnhandledExceptionRaiser += OnUnhandledExceptionRaiser;
        statusText = new TextView(this)
        {
            Text = "JunimoGate GameHost\n\nRebuilding trusted source and applied-workspace capability…",
            TextSize = 16,
        };
        var padding = (int)(20 * Resources!.DisplayMetrics!.Density);
        statusText.SetPadding(padding, padding, padding, padding);
        SetContentView(statusText);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            RegisterBackInvokedCallback();
        }

        preparationCancellation = new CancellationTokenSource();
        _ = PrepareBridgeSessionAsync(preparationCancellation.Token);
    }

    protected override void OnResume()
    {
        base.OnResume();
        ForwardGameResume();
        RequestedOrientation = ScreenOrientation.SensorLandscape;
        SetImmersive();
    }

    protected override void OnPause()
    {
        ForwardGamePauseAndBackup();
        base.OnPause();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
        {
            RequestedOrientation = ScreenOrientation.SensorLandscape;
            SetImmersive();
        }
    }

#pragma warning disable CS0672 // API26 compatibility override; Android invokes this for system Back.
    public override void OnBackPressed()
#pragma warning restore CS0672
    {
        RequestSafeFinish();
    }

    protected override void OnDestroy()
    {
        destroyed = true;
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            UnregisterBackInvokedCallback();
        }
        var cancellation = Interlocked.Exchange(ref preparationCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        AndroidEnvironment.UnhandledExceptionRaiser -= OnUnhandledExceptionRaiser;
        try
        {
            gameView?.Stop();
            if (gameView is not null)
            {
                Log.Info("JunimoGate.GameHost", "game-view-stop-requested");
            }
        }
        catch (ObjectDisposedException)
        {
            // A framework-owned View may already have completed disposal.
        }

        GameHostContentBridge.Detach(managedLoadContext);
        GameHostBridge.Detach(this);
        gameLifecycle = null;
        gameView = null;
        gameRunner = null;
        managedLoadContext = null;
        statusText = null;
        base.OnDestroy();
    }

    private async Task PrepareBridgeSessionAsync(CancellationToken cancellationToken)
    {
        var runAttempted = false;
        try
        {
            var context = ApplicationContext ?? this;
            var discovery = await AndroidPlatformBoundary
                .DiscoverGamesAsync(context, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = discovery.Candidates.SingleOrDefault(item =>
                item.Installation.PackageName.Equals(
                    GameHostRecipeCatalog.TestedPlayPackageName,
                    StringComparison.Ordinal));
            if (candidate is null)
            {
                UpdateStatus("GameHost stopped safely.\n\nThe exact tested Play installation is not available.", cancellationToken);
                return;
            }

            var prepared = await AndroidGameHostAppliedWorkspaceBoundary
                .PrepareAsync(context, candidate, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!prepared.IsSuccess || prepared.Capability is null)
            {
                var code = prepared.Diagnostics.LastOrDefault()?.Code ?? "gamehost_capability_unavailable";
                UpdateStatus(
                    $"GameHost stopped safely.\n\nTrusted launch capability was rejected.\nCode: {code}",
                    cancellationToken);
                return;
            }

            var capability = prepared.Capability;
            GameHostBridge.Attach(this, capability.SourceExecutionPlan, capability.Decision);
            var loadContext = new GameHostManagedAssemblyLoadContext(capability.AppliedExecutionPlan);
            managedLoadContext = loadContext;
            GameHostContentBridge.Install(capability.SourceExecutionPlan, loadContext);
            Log.Info("JunimoGate.GameHost", "trusted-content-bridge-installed");

            var gameAssembly = loadContext.LoadGameAssembly();
            var gameRunnerType = gameAssembly.GetType("StardewValley.GameRunner", throwOnError: true, ignoreCase: false)
                ?? throw new TypeLoadException("The exact game assembly does not define GameRunner.");
            if (gameRunnerType.BaseType is null ||
                !gameRunnerType.BaseType.FullName!.Equals("Microsoft.Xna.Framework.Game", StringComparison.Ordinal))
            {
                throw new TypeLoadException("The exact GameRunner base type does not match the approved MonoGame contract.");
            }

            var constructed = await RunOnUiThreadAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mobileDisplayType = gameAssembly.GetType(
                    "StardewValley.Mobile.MobileDisplay",
                    throwOnError: true,
                    ignoreCase: false)
                    ?? throw new TypeLoadException("The exact game assembly does not define MobileDisplay.");
                var setupDisplay = mobileDisplayType.GetMethod(
                    "SetupDisplaySettings",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null)
                    ?? throw new MissingMethodException(mobileDisplayType.FullName, "SetupDisplaySettings");
                if (!setupDisplay.ReturnType.Equals(typeof(void)))
                {
                    throw new TypeLoadException("MobileDisplay.SetupDisplaySettings does not match the exact contract.");
                }

                setupDisplay.Invoke(null, null);
                GameHostMonoGameAudioResetBridge.PrepareForNewGame();
                Log.Info("JunimoGate.GameHost", "monogame-audio-static-state-ready");
                var instance = Activator.CreateInstance(gameRunnerType, nonPublic: true)
                    ?? throw new InvalidOperationException("GameRunner construction returned null.");
                if (instance is not Game game)
                {
                    throw new TypeLoadException("GameRunner did not bind to the approved public MonoGame provider.");
                }

                var view = game.Services.GetService(typeof(View)) as MonoGameAndroidGameView
                    ?? throw new InvalidOperationException("GameRunner services did not expose the MonoGame Android View.");
                var staticInstance = gameRunnerType.GetField(
                    "instance",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(gameRunnerType.FullName, "instance");
                if (!staticInstance.FieldType.Equals(gameRunnerType))
                {
                    throw new TypeLoadException("GameRunner.instance does not have the exact GameRunner type.");
                }

                if (view.Parent is not null)
                {
                    throw new InvalidOperationException("The MonoGame View is already attached to another parent.");
                }

                var gamePtrField = gameRunnerType.GetField(
                    "gamePtr",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public)
                    ?? throw new MissingFieldException(gameRunnerType.FullName, "gamePtr");
                if (!string.Equals(gamePtrField.FieldType.FullName, "StardewValley.Game1", StringComparison.Ordinal))
                {
                    throw new TypeLoadException("GameRunner.gamePtr does not have the exact Game1 type.");
                }

                var resume = RequireLifecycleMethod(gamePtrField.FieldType, "OnAppResume", isStatic: false);
                var pause = RequireLifecycleMethod(gamePtrField.FieldType, "OnAppPause", isStatic: false);
                var emergencyBackup = RequireLifecycleMethod(gamePtrField.FieldType, "emergencyBackup", isStatic: true);

                staticInstance.SetValue(null, instance);
                SetContentView(view);
                return (
                    Game: game,
                    View: view,
                    Lifecycle: new GameLifecycleForwarder(instance, gamePtrField, resume, pause, emergencyBackup));
            }, cancellationToken).ConfigureAwait(false);

            gameRunner = constructed.Game;
            gameView = constructed.View;
            gameLifecycle = constructed.Lifecycle;
            Log.Info("JunimoGate.GameHost", "mobile-display-configured:view-mounted:run-entering");
            runAttempted = true;
            await RunOnUiThreadAsync(() =>
            {
                constructed.Game.Run();
                Log.Info("JunimoGate.GameHost", "game-runner-run-returned:platform-activate-entering");
                GameHostMonoGameLifecycleBridge.ActivateDelayedGame(constructed.Game);
                Log.Info("JunimoGate.GameHost", "game-platform-activated:view-resume-entering");
                constructed.View.Resume();
                constructed.View.RequestFocus();
                ForwardGameResume(constructed.Lifecycle, "game1-resume-initial");
                return true;
            }, cancellationToken).ConfigureAwait(false);
            Log.Info("JunimoGate.GameHost", "game-runner-view-resumed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Activity destruction cancellation intentionally produces no completion update.
        }
        catch (Exception exception)
        {
            Log.Error("JunimoGate.GameHost", $"managed-load-or-attach-failed:{DescribeExceptionTypes(exception)}");
            GameHostContentBridge.Detach(managedLoadContext);
            GameHostBridge.Detach(this);
            gameLifecycle = null;
            gameView = null;
            gameRunner = null;
            managedLoadContext = null;
            RestoreStatusView();
            UpdateStatus(
                "GameHost stopped safely.\n\n" +
                "Capability attachment, sealed managed load, construction, or GameRunner.Run failed.\n" +
                $"GameRunner.Run attempted: {(runAttempted ? "yes" : "no")}",
                cancellationToken);
        }
    }

    private void RestoreStatusView()
    {
        RunOnUiThread(() =>
        {
            if (!destroyed && statusText is not null)
            {
                SetContentView(statusText);
            }
        });
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() =>
        {
            if (destroyed || cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private static System.Reflection.MethodInfo RequireLifecycleMethod(
        Type game1Type,
        string methodName,
        bool isStatic)
    {
        var flags = System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            (isStatic ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance);
        var method = game1Type.GetMethod(
            methodName,
            flags,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(game1Type.FullName, methodName);
        if (!method.ReturnType.Equals(typeof(void)) || method.IsStatic != isStatic)
        {
            throw new TypeLoadException($"Game1.{methodName} does not match the exact lifecycle contract.");
        }

        return method;
    }

    private void ForwardGameResume()
    {
        var lifecycle = gameLifecycle;
        if (lifecycle is not null)
        {
            ForwardGameResume(lifecycle, "game1-resume-forwarded");
        }
    }

    private void ForwardGamePauseAndBackup()
    {
        var lifecycle = gameLifecycle;
        if (lifecycle is null)
        {
            return;
        }

        var game1 = lifecycle.GamePtrField.GetValue(lifecycle.GameRunner);
        if (game1 is null)
        {
            Log.Info("JunimoGate.GameHost", "game1-pause-skipped:not-initialized");
            return;
        }

        InvokeLifecycle(lifecycle.Pause, game1, "game1-pause-forwarded");
        InvokeLifecycle(lifecycle.EmergencyBackup, target: null, "game1-emergency-backup-forwarded");
    }

    private static void ForwardGameResume(GameLifecycleForwarder lifecycle, string successCode)
    {
        var game1 = lifecycle.GamePtrField.GetValue(lifecycle.GameRunner);
        if (game1 is null)
        {
            Log.Info("JunimoGate.GameHost", "game1-resume-skipped:not-initialized");
            return;
        }

        InvokeLifecycle(lifecycle.Resume, game1, successCode);
    }

    private static void InvokeLifecycle(
        System.Reflection.MethodInfo method,
        object? target,
        string successCode)
    {
        try
        {
            method.Invoke(target, null);
            Log.Info("JunimoGate.GameHost", successCode);
        }
        catch (Exception exception)
        {
            Log.Error(
                "JunimoGate.GameHost",
                $"game-lifecycle-forward-failed:{DescribeExceptionTypes(exception)}");
        }
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
        {
            return;
        }

        OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(backInvokedCallback);
        backInvokedCallback.Dispose();
        backInvokedCallback = null;
    }

    private void RequestSafeFinish()
    {
        Log.Info("JunimoGate.GameHost", "gamehost-back-finish-requested");
        Finish();
    }

    private void SetImmersive()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Kitkat)
        {
#pragma warning disable CA1422 // Required for the supported API26-29 immersive compatibility path.
            Window!.DecorView!.SystemUiFlags = (SystemUiFlags)5894;
#pragma warning restore CA1422
        }
    }

    private static void OnUnhandledExceptionRaiser(object? sender, RaiseThrowableEventArgs args)
    {
        try
        {
            Log.Error(
                "JunimoGate.GameHost",
                $"unhandled-managed-exception:{DescribeExceptionTypes(args.Exception)}");
        }
        catch
        {
            // Diagnostics must never alter the original unhandled-exception behavior.
        }
    }

    private static string DescribeExceptionTypes(Exception exception)
    {
        var types = new List<string>();
        for (Exception? current = exception; current is not null && types.Count < 8; current = current.InnerException)
        {
            types.Add(current switch
            {
                TypeInitializationException initialization =>
                    $"TypeInitializationException({initialization.TypeName ?? "unknown"})",
                TypeLoadException load =>
                    $"TypeLoadException({load.TypeName ?? "unknown"})",
                MissingMethodException missingMethod =>
                    $"MissingMethodException({missingMethod.Message})",
                MissingFieldException missingField =>
                    $"MissingFieldException({BoundedExceptionMessage(missingField.Message)})",
                FileNotFoundException missingFile =>
                    $"FileNotFoundException({BoundedExceptionMessage(missingFile.Message)})",
                ContentLoadException contentLoad =>
                    $"ContentLoadException({BoundedExceptionMessage(contentLoad.Message)})",
                _ => current.GetType().Name,
            });
        }

        return string.Join('>', types);
    }

    private static string BoundedExceptionMessage(string message)
    {
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Contains("/data/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/storage/", StringComparison.OrdinalIgnoreCase))
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return $"message-sha256:{Convert.ToHexStringLower(digest)}";
        }

        const int maximumLength = 320;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "…";
    }

    private sealed record GameLifecycleForwarder(
        object GameRunner,
        System.Reflection.FieldInfo GamePtrField,
        System.Reflection.MethodInfo Resume,
        System.Reflection.MethodInfo Pause,
        System.Reflection.MethodInfo EmergencyBackup);

    private sealed class BackInvokedCallback : Java.Lang.Object, IOnBackInvokedCallback
    {
        private readonly WeakReference<GameHostActivity> activity;

        public BackInvokedCallback(GameHostActivity activity)
        {
            this.activity = new WeakReference<GameHostActivity>(activity);
        }

        public void OnBackInvoked()
        {
            if (activity.TryGetTarget(out var target) && !target.destroyed)
            {
                target.RequestSafeFinish();
            }
        }
    }

    private void UpdateStatus(string text, CancellationToken cancellationToken)
    {
        if (destroyed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!destroyed && !cancellationToken.IsCancellationRequested && statusText is not null)
            {
                statusText.Text = text;
            }
        });
    }
}
