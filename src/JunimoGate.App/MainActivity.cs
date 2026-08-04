using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using AndroidX.AppCompat.App;
using AndroidX.Navigation;
using AndroidX.Navigation.Fragment;
using AndroidX.Navigation.UI;
using Google.Android.Material.BottomNavigation;
using JunimoGate.GameHost;
using JunimoGate.Mods;
using Log = JunimoGate.Android.JunimoGateLog;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Activity(
    Name = "org.junimogate.app.MainActivity",
    Label = "@string/app_name",
    Theme = "@style/Theme.JunimoGate",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop)]
public sealed class MainActivity : AppCompatActivity, ILauncherUiHost
{
    private CancellationTokenSource? lifetimeCancellation;
    private LauncherCoordinator? coordinator;
    private NavController? navigation;
    private LauncherState currentState = new(
        LauncherStatus.Checking,
        "Checking the installed game…",
        ShowProgress: true,
        CanLaunch: false);
    private bool returningFromGame;
    private bool destroyed;

    LauncherState ILauncherUiHost.CurrentState => currentState;

    private event Action<LauncherState>? launcherStateChanged;

    event Action<LauncherState>? ILauncherUiHost.LauncherStateChanged
    {
        add => launcherStateChanged += value;
        remove => launcherStateChanged -= value;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Log.Initialize(this, "launcher", GameHostRuntimeIdentity.BuildId);
        if (TryRouteToActiveGame())
            return;

        SetContentView(Resource.Layout.activity_main);
        SetSupportActionBar(FindViewById<Google.Android.Material.AppBar.MaterialToolbar>(Resource.Id.top_app_bar));
        SupportActionBar?.SetDisplayShowTitleEnabled(true);

        var navHost = SupportFragmentManager.FindFragmentById(Resource.Id.nav_host_fragment) as NavHostFragment
            ?? throw new InvalidOperationException("The launcher navigation host is unavailable.");
        var bottomNavigation = FindViewById<BottomNavigationView>(Resource.Id.bottom_navigation)
            ?? throw new InvalidOperationException("The launcher bottom navigation is unavailable.");
        navigation = navHost.NavController;
        NavigationUI.SetupWithNavController(bottomNavigation, navigation);

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
        launcherStateChanged = null;
        navigation = null;
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

        base.OnDestroy();
    }

    void ILauncherUiHost.RequestLaunch() => _ = LaunchAsync();

    void ILauncherUiHost.UpdateBindingPolicy(ModAssemblyBindingPolicy policy) =>
        _ = UpdateBindingPolicyAsync(policy);

    void ILauncherUiHost.OpenEnvironment() => OpenEnvironment();

    private async Task LaunchAsync()
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

    private async Task UpdateBindingPolicyAsync(ModAssemblyBindingPolicy policy)
    {
        if (destroyed || coordinator is null || lifetimeCancellation is not { IsCancellationRequested: false } cancellation)
            return;

        try
        {
            await coordinator.UpdateBindingPolicyAsync(policy, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Activity destruction cancels pending Profile writes.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Launcher", "profile-policy-update-failed", exception);
            _ = InitializeAsync(cancellation.Token);
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
            currentState = state;
            launcherStateChanged?.Invoke(state);
            if (state.Status is LauncherStatus.GameNotInstalled or LauncherStatus.Unsupported)
                OpenEnvironment();
        });
    }

    private void OpenEnvironment()
    {
        if (destroyed || navigation?.CurrentDestination?.Id == Resource.Id.navigation_environment)
            return;
        navigation?.Navigate(Resource.Id.navigation_environment);
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

internal interface ILauncherUiHost
{
    LauncherState CurrentState { get; }

    event Action<LauncherState>? LauncherStateChanged;

    void RequestLaunch();

    void UpdateBindingPolicy(ModAssemblyBindingPolicy policy);

    void OpenEnvironment();
}
