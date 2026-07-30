using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using JunimoGate.GameHost;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Activity(
    Name = "org.junimogate.app.MainActivity",
    Label = "JunimoGate",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop)]
public sealed class MainActivity : Activity
{
    private CancellationTokenSource? lifetimeCancellation;
    private LauncherCoordinator? coordinator;
    private TextView? statusText;
    private ProgressBar? progressBar;
    private Button? launchButton;
    private bool returningFromGame;
    private bool destroyed;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Log.Initialize(this, "launcher", GameLaunchSchema.BuildId);
        if (TryRouteToActiveGame())
            return;

        var density = Resources!.DisplayMetrics!.Density;
        var padding = (int)(24 * density);
        statusText = new TextView(this)
        {
            Text = "Checking the installed game…",
            TextSize = 18,
            Gravity = GravityFlags.Center,
            Typeface = Typeface.Default,
        };
        statusText.SetPadding(padding, padding, padding, padding);

        progressBar = new ProgressBar(this)
        {
            Indeterminate = true,
        };

        launchButton = new Button(this)
        {
            Text = "Launch game",
            Enabled = false,
        };
        launchButton.Click += OnLaunchClicked;

        var layout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        layout.SetGravity(GravityFlags.Center);
        layout.SetPadding(padding, padding, padding, padding);
        layout.AddView(statusText, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1));
        layout.AddView(progressBar, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent));
        layout.AddView(launchButton, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        SetContentView(layout);

        lifetimeCancellation = new CancellationTokenSource();
        coordinator = new LauncherCoordinator(ApplicationContext ?? this);
        coordinator.StateChanged += OnLauncherStateChanged;
        OnLauncherStateChanged(coordinator.CurrentState);
        _ = InitializeAsync(lifetimeCancellation.Token);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        _ = !destroyed && TryRouteToActiveGame();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Log.Info("JunimoGate.Launcher", $"activity-resumed returningFromGame={(returningFromGame ? 1 : 0)}");
        if (returningFromGame && !destroyed && lifetimeCancellation is { IsCancellationRequested: false } cancellation)
        {
            returningFromGame = false;
            _ = InitializeAsync(cancellation.Token);
        }
    }

    protected override void OnDestroy()
    {
        Log.Info(
            "JunimoGate.Launcher",
            $"activity-destroyed finishing={(IsFinishing ? 1 : 0)} changingConfiguration={(IsChangingConfigurations ? 1 : 0)}");
        destroyed = true;
        if (launchButton is not null)
            launchButton.Click -= OnLaunchClicked;
        if (coordinator is not null)
        {
            coordinator.StateChanged -= OnLauncherStateChanged;
            coordinator.Dispose();
            coordinator = null;
        }

        var cancellation = Interlocked.Exchange(ref lifetimeCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        statusText = null;
        progressBar = null;
        launchButton = null;
        base.OnDestroy();
    }

    private async void OnLaunchClicked(object? sender, EventArgs eventArgs)
    {
        if (destroyed || coordinator is null || lifetimeCancellation is not { IsCancellationRequested: false } cancellation)
            return;

        try
        {
            var handle = await coordinator.TryCreateLaunchAsync(cancellation.Token);
            if (handle is null || destroyed)
                return;

            await StartWithRecoveryAsync(handle, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Activity destruction cancels pending launcher work.
        }
        catch (Exception exception) when (exception is ActivityNotFoundException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Launcher", "launch-dispatch-failed", exception);
            returningFromGame = false;
            coordinator.ReportLaunchFailure();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (coordinator is null)
            return;
        try
        {
            var handle = await coordinator.InitializeAsync(cancellationToken);
            if (handle is not null && !destroyed)
                await StartWithRecoveryAsync(handle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Activity destruction cancels pending launcher work.
        }
    }

    private async Task StartWithRecoveryAsync(GameLaunchHandle handle, CancellationToken cancellationToken)
    {
        while (!destroyed && coordinator is not null)
        {
            try
            {
                var intent = new Intent(this, typeof(SmapiGameActivity));
                intent.PutExtra(SmapiGameActivity.LaunchKeyExtra, handle.Key);
                returningFromGame = true;
                Log.Info("JunimoGate.Launcher", $"activity-dispatch attempt={handle.Key[..8]}");
                StartActivity(intent);
                return;
            }
            catch (Exception exception) when (exception is ActivityNotFoundException or InvalidOperationException)
            {
                Log.Error("JunimoGate.Launcher", "launch-dispatch-failed", exception);
                returningFromGame = false;
                var recovered = await coordinator
                    .RecoverLaunchDispatchFailureAsync(handle, cancellationToken);
                if (recovered is null)
                    return;
                handle = recovered;
            }
        }
    }

    private void OnLauncherStateChanged(LauncherState state)
    {
        RunOnUiThread(() =>
        {
            if (destroyed)
                return;
            if (statusText is not null)
                statusText.Text = state.Message;
            if (progressBar is not null)
                progressBar.Visibility = state.ShowProgress ? ViewStates.Visible : ViewStates.Gone;
            if (launchButton is not null)
                launchButton.Enabled = state.CanLaunch;
        });
    }

    private bool TryRouteToActiveGame()
    {
        if (!GameSessionRegistry.TryRouteActiveGame(this))
            return false;

        Log.Info("JunimoGate.Launcher", "active-game-session-routed");
        Finish();
        return true;
    }
}
