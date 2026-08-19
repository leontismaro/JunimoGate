using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.Activity;
using AndroidX.Core.View;
using AndroidX.DrawerLayout.Widget;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.Dialog;
using Google.Android.Material.Navigation;
using JunimoGate.Android;
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
    private const string StartupNoticePreferences = "startup_notice";
    private const string OpenSourceNoticeShownKey = "open_source_notice_shown";
    private const string OfficialReleasesUrl = "https://github.com/leontismaro/JunimoGate/releases";
    private CancellationTokenSource? lifetimeCancellation;
    private LauncherCoordinator? coordinator;
    private MainShellFragment? mainShell;
    private InteractiveDrawerLayout? drawer;
    private NavigationView? drawerNavigation;
    private FloatingActionButton? drawerOpen;
    private AppCompatImageButton? drawerClose;
    private BackPressedCallback? backPressedCallback;
    private ModManagementUiSession? modManagement;
    private LauncherState currentState = new(
        LauncherStatus.Checking,
        LauncherMessageKey.CheckingInstalledGame,
        ShowProgress: true,
        CanLaunch: false);
    private bool returningFromGame;
    private bool destroyed;

    LauncherState ILauncherUiHost.CurrentState => currentState;

    internal ModManagementUiSession ModManagement => modManagement
        ?? throw new InvalidOperationException("The Mod management UI session is unavailable.");

    private event Action<LauncherState>? launcherStateChanged;

    event Action<LauncherState>? ILauncherUiHost.LauncherStateChanged
    {
        add => launcherStateChanged += value;
        remove => launcherStateChanged -= value;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var window = Window ?? throw new InvalidOperationException("The launcher window is unavailable.");
        WindowCompat.SetDecorFitsSystemWindows(window, false);
        Log.Initialize(this, "launcher", GameHostRuntimeIdentity.BuildId);
        if (TryRouteToActiveGame())
            return;

        SetContentView(Resource.Layout.activity_main);
        backPressedCallback = new BackPressedCallback(HandleBackPressed);
        OnBackPressedDispatcher.AddCallback(this, backPressedCallback);
        mainShell = SupportFragmentManager.FindFragmentById(Resource.Id.main_shell_fragment) as MainShellFragment
            ?? throw new InvalidOperationException("The main shell is unavailable.");
        drawerNavigation = FindViewById<NavigationView>(Resource.Id.drawer_navigation)
            ?? throw new InvalidOperationException("The launcher drawer navigation is unavailable.");
        drawer = FindViewById<InteractiveDrawerLayout>(Resource.Id.main_drawer)
            ?? throw new InvalidOperationException("The launcher drawer is unavailable.");
        drawerOpen = FindViewById<FloatingActionButton>(Resource.Id.drawer_open)
            ?? throw new InvalidOperationException("The drawer open action is unavailable.");
        drawerClose = FindViewById<AppCompatImageButton>(Resource.Id.drawer_close)
            ?? throw new InvalidOperationException("The drawer close action is unavailable.");
        var drawerContent = FindViewById<View>(Resource.Id.drawer_content)
            ?? throw new InvalidOperationException("The drawer content is unavailable.");
        ViewCompat.SetOnApplyWindowInsetsListener(
            drawer,
            new SystemBarInsetsListener(
                drawerContent,
                drawerOpen));
        ViewCompat.RequestApplyInsets(drawer);
        drawerNavigation.NavigationItemSelected += OnDrawerNavigationItemSelected;
        drawerOpen.Click += OnDrawerOpenClicked;
        drawerClose.Click += OnDrawerCloseClicked;
        ShowOpenSourceNoticeIfNeeded();

        lifetimeCancellation = new CancellationTokenSource();
        modManagement = new ModManagementUiSession(this, AndroidPrivateStorage.GetUserDataRoot(this));
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

    private void HandleBackPressed()
    {
        if (drawer?.IsDrawerOpen(GravityCompat.Start) == true)
        {
            drawer.CloseDrawer(GravityCompat.Start);
            return;
        }
        if (mainShell?.HandleBack() == true)
            return;
        if (backPressedCallback is not null)
        {
            backPressedCallback.Enabled = false;
            OnBackPressedDispatcher.OnBackPressed();
            backPressedCallback.Enabled = true;
        }
    }

    protected override void OnDestroy()
    {
        Log.Info(
            "JunimoGate.Launcher",
            $"activity-destroyed finishing={(IsFinishing ? 1 : 0)} changingConfiguration={(IsChangingConfigurations ? 1 : 0)}");
        destroyed = true;
        launcherStateChanged = null;
        if (drawerOpen is not null)
            drawerOpen.Click -= OnDrawerOpenClicked;
        if (drawerClose is not null)
            drawerClose.Click -= OnDrawerCloseClicked;
        if (drawerNavigation is not null)
            drawerNavigation.NavigationItemSelected -= OnDrawerNavigationItemSelected;
        mainShell = null;
        backPressedCallback?.Remove();
        backPressedCallback = null;
        drawer = null;
        drawerNavigation = null;
        drawerOpen = null;
        drawerClose = null;
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

        modManagement?.Dispose();
        modManagement = null;

        base.OnDestroy();
    }

    void ILauncherUiHost.RequestLaunch() => _ = LaunchAsync();

    void ILauncherUiHost.UpdateBindingPolicy(ModAssemblyBindingPolicy policy) =>
        _ = UpdateBindingPolicyAsync(policy);

    void ILauncherUiHost.RequestGameEnvironmentRepair() => _ = RepairGameEnvironmentAsync();

    void ILauncherUiHost.RequestCacheCleanup() => _ = CleanRebuildableCachesAsync();

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

    internal ValueTask RefreshLauncherProfileAsync(CancellationToken cancellationToken) =>
        coordinator?.RefreshProfileAsync(cancellationToken) ?? ValueTask.CompletedTask;

    internal ValueTask EnsureModProfilesReadyAsync(CancellationToken cancellationToken) =>
        coordinator?.EnsureModProfilesReadyAsync(cancellationToken) ?? ValueTask.CompletedTask;

    private async Task RepairGameEnvironmentAsync()
    {
        if (destroyed || coordinator is null || lifetimeCancellation is not { IsCancellationRequested: false } cancellation)
            return;
        try
        {
            var result = await coordinator.RepairGameEnvironmentAsync(cancellation.Token);
            if (!destroyed)
            {
                RunOnUiThread(() => Toast.MakeText(
                    this,
                    result.IsReady ? Resource.String.settings_repair_complete : Resource.String.settings_repair_failed,
                    ToastLength.Long)?.Show());
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Launcher", "manual-repair-failed", exception);
            if (!destroyed)
                RunOnUiThread(() => Toast.MakeText(this, Resource.String.settings_repair_failed, ToastLength.Long)?.Show());
            _ = InitializeAsync(cancellation.Token);
        }
    }

    private async Task CleanRebuildableCachesAsync()
    {
        if (destroyed || coordinator is null || lifetimeCancellation is not { IsCancellationRequested: false } cancellation)
            return;
        try
        {
            var result = await coordinator.CleanRebuildableCachesAsync(cancellation.Token);
            if (destroyed)
                return;
            RunOnUiThread(() =>
            {
                var reclaimed = global::Android.Text.Format.Formatter
                    .FormatFileSize(this, result.ReclaimedBytes) ?? "0 B";
                var message = result.BlockedByRunningGame
                    ? GetString(Resource.String.settings_cache_cleanup_blocked)
                    : FormatString(
                        Resource.String.settings_cache_cleanup_complete,
                        Java.Lang.Integer.ValueOf(result.RemovedEntries),
                        new Java.Lang.String(reclaimed));
                Toast.MakeText(this, message, ToastLength.Long)?.Show();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidDataException or InvalidOperationException)
        {
            Log.Error("JunimoGate.Cache", "manual-cache-cleanup-failed", exception);
            if (!destroyed)
                RunOnUiThread(() => Toast.MakeText(this, Resource.String.settings_cache_cleanup_failed, ToastLength.Long)?.Show());
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (coordinator is null)
            return;
        try
        {
            var handle = await coordinator.InitializeAsync(cancellationToken);
            if (modManagement is { } session)
            {
                session.ResetSnapshots();
                _ = PreloadModManagementAsync(session, cancellationToken);
            }
            if (handle is not null && !destroyed)
                await StartWithRecoveryAsync(handle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Activity destruction cancels pending launcher work.
        }
    }

    private static async Task PreloadModManagementAsync(
        ModManagementUiSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("JunimoGate.Mods", "ui-library-preload-failed", exception);
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
            mainShell?.RenderLauncherState(state);
            launcherStateChanged?.Invoke(state);
            if (state.Status is LauncherStatus.NeedsPreparation or LauncherStatus.GameNotInstalled or LauncherStatus.Unsupported)
                OpenEnvironment();
        });
    }

    internal bool IsNavigationDrawerOpen => drawer?.IsDrawerOpen(GravityCompat.Start) == true;

    internal void SetShellToolbar(Google.Android.Material.AppBar.MaterialToolbar toolbar)
    {
        SetSupportActionBar(toolbar);
        SupportActionBar?.SetDisplayShowTitleEnabled(true);
    }

    internal void SetDrawerSwipeEnabled(bool enabled)
    {
        if (drawer is null)
            return;
        drawer.ContentSwipeEnabled = enabled;
        if (!drawer.IsDrawerOpen(GravityCompat.Start))
            ApplyDrawerLockMode();
    }

    internal void SetDrawerOpenVisible(bool visible)
    {
        if (drawerOpen is not null)
            drawerOpen.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
    }

    internal void RenderDrawerSelection(int destinationId)
    {
        if (drawerNavigation?.Menu is not { } menu)
            return;
        var isDrawerDestination = IsDrawerDestination(destinationId);
        for (var index = 0; index < menu.Size(); index++)
        {
            var item = menu.GetItem(index);
            item?.SetChecked(isDrawerDestination && item.ItemId == destinationId);
        }
    }

    private static bool IsDrawerDestination(int destinationId) =>
        destinationId is Resource.Id.navigation_environment or
            Resource.Id.navigation_save_backups or Resource.Id.navigation_logs or
            Resource.Id.navigation_settings or Resource.Id.navigation_about;

    private void OnDrawerOpenClicked(object? sender, EventArgs eventArgs) =>
        drawer?.OpenDrawer(GravityCompat.Start, animate: false);

    private void OnDrawerCloseClicked(object? sender, EventArgs eventArgs) =>
        CloseNavigationDrawer();

    private void OnDrawerNavigationItemSelected(
        object? sender,
        NavigationView.NavigationItemSelectedEventArgs eventArgs)
    {
        if (!destroyed)
            mainShell?.NavigateDrawerDestination(eventArgs.MenuItem.ItemId);
    }

    internal void CloseNavigationDrawer()
    {
        drawer?.CloseDrawer(GravityCompat.Start, animate: false);
        ApplyDrawerLockMode();
    }

    private void ApplyDrawerLockMode()
    {
        if (drawer is null)
            return;
        drawer.SetDrawerLockMode(
            drawer.ContentSwipeEnabled ? DrawerLayout.LockModeUnlocked : DrawerLayout.LockModeLockedClosed,
            GravityCompat.Start);
    }

    private void OpenEnvironment()
    {
        if (!destroyed)
            mainShell?.OpenEnvironment();
    }

    private void ShowOpenSourceNoticeIfNeeded()
    {
        var preferences = GetSharedPreferences(StartupNoticePreferences, FileCreationMode.Private);
        if (preferences?.GetBoolean(OpenSourceNoticeShownKey, false) != false)
            return;

        var dialog = new MaterialAlertDialogBuilder(this);
        dialog.SetTitle(Resource.String.open_source_notice_title);
        dialog.SetMessage(Resource.String.open_source_notice_message);
        dialog.SetNegativeButton(Resource.String.open_source_notice_acknowledge, (_, _) => { });
        dialog.SetPositiveButton(Resource.String.open_source_notice_official_release, (_, _) => OpenOfficialReleases());
        dialog.Show();

        preferences.Edit()?.PutBoolean(OpenSourceNoticeShownKey, true)?.Apply();
    }

    private void OpenOfficialReleases()
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(OfficialReleasesUrl)));
        }
        catch (ActivityNotFoundException exception)
        {
            Log.Warn("JunimoGate.Launcher", "official-release-browser-unavailable", exception);
            Toast.MakeText(this, Resource.String.about_browser_unavailable, ToastLength.Long)?.Show();
        }
    }

    private sealed class SystemBarInsetsListener(View drawerContent, View drawerOpen) :
        Java.Lang.Object,
        IOnApplyWindowInsetsListener
    {
        private readonly int drawerPaddingTop = drawerContent.PaddingTop;
        private readonly int drawerPaddingBottom = drawerContent.PaddingBottom;
        private readonly int drawerOpenMarginBottom =
            (drawerOpen.LayoutParameters as ViewGroup.MarginLayoutParams)?.BottomMargin ?? 0;

        public WindowInsetsCompat? OnApplyWindowInsets(View? view, WindowInsetsCompat? insets)
        {
            if (insets is null)
                return null;
            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            if (systemBars is null)
                return insets;
            drawerContent.SetPadding(
                drawerContent.PaddingLeft,
                drawerPaddingTop + systemBars.Top,
                drawerContent.PaddingRight,
                drawerPaddingBottom + systemBars.Bottom);

            if (drawerOpen.LayoutParameters is ViewGroup.MarginLayoutParams margins)
            {
                margins.BottomMargin = drawerOpenMarginBottom + systemBars.Bottom;
                drawerOpen.LayoutParameters = margins;
            }
            return insets;
        }

    }

    private sealed class BackPressedCallback(Action callback) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => callback();
    }

    private bool TryRouteToActiveGame()
    {
        if (!GameSessionRegistry.TryRouteActiveGame(this))
            return false;

        Log.Info("JunimoGate.Launcher", "active-game-session-routed");
        Finish();
        return true;
    }

    private string FormatString(int resourceId, params Java.Lang.Object[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The Launcher string resource is unavailable.");

}

internal interface ILauncherUiHost
{
    LauncherState CurrentState { get; }

    event Action<LauncherState>? LauncherStateChanged;

    void RequestLaunch();

    void UpdateBindingPolicy(ModAssemblyBindingPolicy policy);

    void RequestGameEnvironmentRepair();

    void RequestCacheCleanup();
}
