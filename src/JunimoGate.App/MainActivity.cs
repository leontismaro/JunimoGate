using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.Core.View;
using AndroidX.DrawerLayout.Widget;
using AndroidX.Navigation;
using AndroidX.Navigation.Fragment;
using AndroidX.Navigation.UI;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.Navigation;
using Google.Android.Material.ProgressIndicator;
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
    private CancellationTokenSource? lifetimeCancellation;
    private LauncherCoordinator? coordinator;
    private NavController? navigation;
    private InteractiveDrawerLayout? drawer;
    private NavigationView? drawerNavigation;
    private FloatingActionButton? drawerOpen;
    private AppCompatImageButton? drawerClose;
    private FloatingActionButton? launchAction;
    private CircularProgressIndicator? launchProgress;
    private View? bottomHome;
    private View? bottomMods;
    private View? bottomHomeIndicator;
    private View? bottomModsIndicator;
    private View? homeBackdrop;
    private NavDestinationListener? destinationListener;
    private int selectedBottomDomain = Resource.Id.navigation_home;
    private int? pendingMainPage;
    private bool drawerNavigationSelectionPending;
    private bool currentDestinationUsesDrawerFade;
    private bool drawerHomeTransitionInProgress;
    private int homeBackdropAnimationGeneration;
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
        var toolbar = FindViewById<Google.Android.Material.AppBar.MaterialToolbar>(Resource.Id.top_app_bar)
            ?? throw new InvalidOperationException("The launcher toolbar is unavailable.");
        SetSupportActionBar(toolbar);
        SupportActionBar?.SetDisplayShowTitleEnabled(true);

        var navHost = SupportFragmentManager.FindFragmentById(Resource.Id.nav_host_fragment) as NavHostFragment
            ?? throw new InvalidOperationException("The launcher navigation host is unavailable.");
        var bottomNavigation = FindViewById<View>(Resource.Id.bottom_navigation)
            ?? throw new InvalidOperationException("The launcher bottom navigation is unavailable.");
        bottomHome = FindViewById<View>(Resource.Id.bottom_navigation_home)
            ?? throw new InvalidOperationException("The Home navigation area is unavailable.");
        bottomMods = FindViewById<View>(Resource.Id.bottom_navigation_mods)
            ?? throw new InvalidOperationException("The Mod navigation area is unavailable.");
        bottomHomeIndicator = FindViewById<View>(Resource.Id.bottom_navigation_home_indicator)
            ?? throw new InvalidOperationException("The Home navigation indicator is unavailable.");
        bottomModsIndicator = FindViewById<View>(Resource.Id.bottom_navigation_mods_indicator)
            ?? throw new InvalidOperationException("The Mod navigation indicator is unavailable.");
        homeBackdrop = FindViewById<View>(Resource.Id.home_backdrop)
            ?? throw new InvalidOperationException("The Home backdrop is unavailable.");
        drawerNavigation = FindViewById<NavigationView>(Resource.Id.drawer_navigation)
            ?? throw new InvalidOperationException("The launcher drawer navigation is unavailable.");
        drawer = FindViewById<InteractiveDrawerLayout>(Resource.Id.main_drawer)
            ?? throw new InvalidOperationException("The launcher drawer is unavailable.");
        drawerOpen = FindViewById<FloatingActionButton>(Resource.Id.drawer_open)
            ?? throw new InvalidOperationException("The drawer open action is unavailable.");
        drawerClose = FindViewById<AppCompatImageButton>(Resource.Id.drawer_close)
            ?? throw new InvalidOperationException("The drawer close action is unavailable.");
        launchAction = FindViewById<FloatingActionButton>(Resource.Id.launch_action)
            ?? throw new InvalidOperationException("The game launch action is unavailable.");
        launchProgress = FindViewById<CircularProgressIndicator>(Resource.Id.launch_progress)
            ?? throw new InvalidOperationException("The game launch progress is unavailable.");
        var bottomNavigationContainer = FindViewById<View>(Resource.Id.bottom_navigation_container)
            ?? throw new InvalidOperationException("The bottom navigation container is unavailable.");
        var drawerContent = FindViewById<View>(Resource.Id.drawer_content)
            ?? throw new InvalidOperationException("The drawer content is unavailable.");
        ViewCompat.SetOnApplyWindowInsetsListener(
            drawer,
            new SystemBarInsetsListener(
                toolbar,
                bottomNavigationContainer,
                bottomNavigation,
                launchAction,
                launchProgress,
                drawerContent,
                drawerOpen));
        ViewCompat.RequestApplyInsets(drawer);
        navigation = navHost.NavController;
        destinationListener = new NavDestinationListener(OnDestinationChanged);
        navigation.AddOnDestinationChangedListener(destinationListener);
        bottomHome.Click += OnBottomHomeClicked;
        bottomMods.Click += OnBottomModsClicked;
        RenderBottomNavigation();
        drawerNavigation.NavigationItemSelected += OnDrawerNavigationItemSelected;
        drawerOpen.Click += OnDrawerOpenClicked;
        drawerClose.Click += OnDrawerCloseClicked;
        launchAction.Click += OnLaunchActionClicked;

        lifetimeCancellation = new CancellationTokenSource();
        modManagement = new ModManagementUiSession(AndroidPrivateStorage.GetUserDataRoot(this));
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
        if (drawerOpen is not null)
            drawerOpen.Click -= OnDrawerOpenClicked;
        if (drawerClose is not null)
            drawerClose.Click -= OnDrawerCloseClicked;
        if (drawerNavigation is not null)
            drawerNavigation.NavigationItemSelected -= OnDrawerNavigationItemSelected;
        if (launchAction is not null)
            launchAction.Click -= OnLaunchActionClicked;
        if (bottomHome is not null)
            bottomHome.Click -= OnBottomHomeClicked;
        if (bottomMods is not null)
            bottomMods.Click -= OnBottomModsClicked;
        if (navigation is not null && destinationListener is not null)
            navigation.RemoveOnDestinationChangedListener(destinationListener);
        destinationListener?.Dispose();
        destinationListener = null;
        navigation = null;
        bottomHome = null;
        bottomMods = null;
        bottomHomeIndicator = null;
        bottomModsIndicator = null;
        homeBackdrop = null;
        drawer = null;
        drawerNavigation = null;
        drawerOpen = null;
        drawerClose = null;
        launchAction = null;
        launchProgress = null;
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
            modManagement?.NotifyProfilesChanged();
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
                session.NotifyLibraryChanged();
                session.NotifyProfilesChanged();
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
            RenderLaunchAction(state);
            launcherStateChanged?.Invoke(state);
            if (state.Status is LauncherStatus.NeedsPreparation or LauncherStatus.GameNotInstalled or LauncherStatus.Unsupported)
                OpenEnvironment();
        });
    }

    private void RenderLaunchAction(LauncherState state)
    {
        if (launchAction is null || launchProgress is null)
            return;
        launchAction.Enabled = state.CanLaunch;
        launchAction.ContentDescription = GetString(LauncherTextFormatter.GetActionTextResource(state));
        launchProgress.Visibility = state.ShowProgress ? ViewStates.Visible : ViewStates.Gone;
    }

    private void OnLaunchActionClicked(object? sender, EventArgs eventArgs) => _ = LaunchAsync();

    internal void OnMainPageChanged(int page)
    {
        if (destroyed || page is < MainPagerFragment.HomePage or > MainPagerFragment.ModGroupsPage)
            return;
        selectedBottomDomain = page == MainPagerFragment.HomePage
            ? Resource.Id.navigation_home
            : Resource.Id.navigation_mods;
        UpdateDrawerSwipeState();
        RenderBottomNavigation(animate: true);
    }

    internal void OnMainPageScrolled(int position, float offset)
    {
        if (drawerHomeTransitionInProgress ||
            navigation?.CurrentDestination?.Id is not
            (Resource.Id.navigation_home or Resource.Id.navigation_mods))
            return;
        RenderHomeBackdrop(position == MainPagerFragment.HomePage ? 1f - offset : 0f);
    }

    internal void OnMainPageScrollStateChanged(int state, int page)
    {
        if (drawerHomeTransitionInProgress || state != ViewPager2.ScrollStateIdle ||
            navigation?.CurrentDestination?.Id is not
                (Resource.Id.navigation_home or Resource.Id.navigation_mods))
            return;
        RenderHomeBackdrop(page == MainPagerFragment.HomePage ? 1f : 0f);
    }

    private void OnBottomHomeClicked(object? sender, EventArgs eventArgs) =>
        ShowMainPage(MainPagerFragment.HomePage);

    private void OnBottomModsClicked(object? sender, EventArgs eventArgs) =>
        ShowMainPage(MainPagerFragment.ModsPage);

    private void ShowMainPage(int page)
    {
        if (destroyed || navigation is null)
            return;

        if (navigation.CurrentDestination?.Id is Resource.Id.navigation_home or Resource.Id.navigation_mods &&
            GetMainPagerFragment() is { } mainPager)
        {
            mainPager.ShowPage(page, animate: true);
            return;
        }

        pendingMainPage = page;
        if (navigation.CurrentDestination?.Id is Resource.Id.navigation_home or Resource.Id.navigation_mods)
        {
            drawer?.Post(ApplyPendingMainPage);
            return;
        }

        if (!navigation.PopBackStack(Resource.Id.navigation_home, inclusive: false))
            navigation.Navigate(page == MainPagerFragment.HomePage
                ? Resource.Id.navigation_home
                : Resource.Id.navigation_mods);
    }

    private MainPagerFragment? GetMainPagerFragment()
    {
        var navHost = SupportFragmentManager.FindFragmentById(Resource.Id.nav_host_fragment) as NavHostFragment;
        if (navHost?.ChildFragmentManager.PrimaryNavigationFragment is MainPagerFragment primary)
            return primary;
        foreach (var fragment in navHost?.ChildFragmentManager.Fragments ?? [])
        {
            if (fragment is MainPagerFragment pager && pager.IsAdded)
                return pager;
        }
        return null;
    }

    private void ApplyPendingMainPage()
    {
        if (pendingMainPage is not { } page || GetMainPagerFragment() is not { } mainPager)
            return;
        pendingMainPage = null;
        mainPager.ShowPage(page, animate: false);
    }

    private void OnDestinationChanged(int destinationId)
    {
        var previousDestinationUsedDrawerFade = currentDestinationUsesDrawerFade;
        var enteredWithDrawerFade = drawerNavigationSelectionPending && IsDrawerDestination(destinationId);
        drawerNavigationSelectionPending = false;
        currentDestinationUsesDrawerFade = enteredWithDrawerFade;
        if (destinationId == Resource.Id.navigation_mods)
            selectedBottomDomain = Resource.Id.navigation_mods;
        else if (destinationId == Resource.Id.navigation_mod_group_editor)
            selectedBottomDomain = Resource.Id.navigation_mods;
        if (destinationId is Resource.Id.navigation_home or Resource.Id.navigation_mods)
            drawer?.Post(ApplyPendingMainPage);
        var backdropAlpha = destinationId == Resource.Id.navigation_home &&
            (GetMainPagerFragment()?.CurrentPage ?? MainPagerFragment.HomePage) == MainPagerFragment.HomePage
                ? 1f
                : 0f;
        if (enteredWithDrawerFade ||
            backdropAlpha > 0f && previousDestinationUsedDrawerFade)
        {
            AnimateHomeBackdrop(
                backdropAlpha,
                blockPagerUpdates: backdropAlpha > 0f && previousDestinationUsedDrawerFade);
        }
        else
        {
            RenderHomeBackdrop(backdropAlpha);
        }
        UpdateDrawerSwipeState();
        RenderDrawerSelection(destinationId);
        RenderBottomNavigation(animate: true);
    }

    private void RenderDrawerSelection(int destinationId)
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

    private void UpdateDrawerSwipeState()
    {
        if (drawer is null)
            return;
        var enabled = navigation?.CurrentDestination?.Id is
                Resource.Id.navigation_home or Resource.Id.navigation_mods &&
            selectedBottomDomain == Resource.Id.navigation_home;
        drawer.ContentSwipeEnabled = enabled;
        drawer.SetDrawerLockMode(
            enabled ? DrawerLayout.LockModeUnlocked : DrawerLayout.LockModeLockedClosed,
            GravityCompat.Start);
    }

    private void RenderBottomNavigation(bool animate = false)
    {
        if (bottomHome is not null)
            bottomHome.Selected = selectedBottomDomain == Resource.Id.navigation_home;
        if (bottomMods is not null)
            bottomMods.Selected = selectedBottomDomain == Resource.Id.navigation_mods;
        if (bottomHomeIndicator is not null)
            RenderBottomIndicator(
                bottomHomeIndicator,
                selectedBottomDomain == Resource.Id.navigation_home,
                animate);
        if (bottomModsIndicator is not null)
            RenderBottomIndicator(
                bottomModsIndicator,
                selectedBottomDomain == Resource.Id.navigation_mods,
                animate);
    }

    private void RenderHomeBackdrop(float alpha)
    {
        if (homeBackdrop is null)
            return;
        homeBackdropAnimationGeneration++;
        drawerHomeTransitionInProgress = false;
        homeBackdrop.Animate()?.Cancel();
        homeBackdrop.Alpha = Math.Clamp(alpha, 0f, 1f);
    }

    private void AnimateHomeBackdrop(float alpha, bool blockPagerUpdates)
    {
        if (homeBackdrop is null)
            return;
        var targetAlpha = Math.Clamp(alpha, 0f, 1f);
        var navigationDuration = Resources?.GetInteger(Resource.Integer.config_navAnimTime) ?? 150;
        var revealDuration = blockPagerUpdates ? Math.Min(100, navigationDuration) : navigationDuration;
        var revealDelay = blockPagerUpdates ? navigationDuration : 0;
        var generation = ++homeBackdropAnimationGeneration;
        drawerHomeTransitionInProgress = blockPagerUpdates;
        homeBackdrop.Animate()
            ?.Cancel();
        homeBackdrop.Animate()
            ?.Alpha(targetAlpha)
            .SetStartDelay(revealDelay)
            .SetDuration(revealDuration)
            .Start();
        if (!blockPagerUpdates)
            return;
        homeBackdrop.PostDelayed(
            () =>
            {
                if (generation != homeBackdropAnimationGeneration)
                    return;
                drawerHomeTransitionInProgress = false;
                homeBackdrop.Alpha = targetAlpha;
            },
            revealDelay + revealDuration);
    }

    private static bool IsDrawerDestination(int destinationId) =>
        destinationId is Resource.Id.navigation_environment or
            Resource.Id.navigation_save_backups or Resource.Id.navigation_logs or
            Resource.Id.navigation_settings or Resource.Id.navigation_about;

    private static void RenderBottomIndicator(View indicator, bool selected, bool animate)
    {
        var becameSelected = selected && !indicator.Selected;
        indicator.Animate()?.Cancel();
        indicator.Selected = selected;
        indicator.ScaleX = 1f;
        indicator.Alpha = 1f;
        if (!animate || !becameSelected)
            return;

        indicator.ScaleX = 0.12f;
        indicator.Alpha = 0.7f;
        indicator.Animate()
            ?.ScaleX(1f)
            .Alpha(1f)
            .SetDuration(280L)
            .Start();
    }

    private void OnDrawerOpenClicked(object? sender, EventArgs eventArgs) =>
        drawer?.OpenDrawer(GravityCompat.Start, animate: true);

    private void OnDrawerCloseClicked(object? sender, EventArgs eventArgs) =>
        drawer?.CloseDrawer(GravityCompat.Start);

    private void OnDrawerNavigationItemSelected(
        object? sender,
        NavigationView.NavigationItemSelectedEventArgs eventArgs)
    {
        drawerNavigationSelectionPending = !destroyed && navigation is not null;
        if (drawerNavigationSelectionPending &&
            NavigationUI.OnNavDestinationSelected(eventArgs.MenuItem, navigation!))
        {
            RenderDrawerSelection(eventArgs.MenuItem.ItemId);
        }
        else
        {
            drawerNavigationSelectionPending = false;
        }
        drawer?.CloseDrawer(GravityCompat.Start);
    }

    private void OpenEnvironment()
    {
        if (destroyed || navigation?.CurrentDestination?.Id == Resource.Id.navigation_environment)
            return;
        navigation?.Navigate(Resource.Id.navigation_environment);
    }

    private sealed class SystemBarInsetsListener(
        View toolbar,
        View bottomNavigationContainer,
        View bottomNavigation,
        View launchAction,
        View launchProgress,
        View drawerContent,
        View drawerOpen) :
        Java.Lang.Object,
        IOnApplyWindowInsetsListener
    {
        private readonly int toolbarHeight = toolbar.LayoutParameters?.Height ?? 0;
        private readonly int toolbarPaddingTop = toolbar.PaddingTop;
        private readonly int bottomNavigationContainerHeight =
            bottomNavigationContainer.LayoutParameters?.Height ?? 0;
        private readonly int bottomNavigationMarginBottom =
            (bottomNavigation.LayoutParameters as ViewGroup.MarginLayoutParams)?.BottomMargin ?? 0;
        private readonly int launchActionMarginBottom =
            (launchAction.LayoutParameters as ViewGroup.MarginLayoutParams)?.BottomMargin ?? 0;
        private readonly int launchProgressMarginBottom =
            (launchProgress.LayoutParameters as ViewGroup.MarginLayoutParams)?.BottomMargin ?? 0;
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
            SetHeight(toolbar, toolbarHeight + systemBars.Top);
            toolbar.SetPadding(
                toolbar.PaddingLeft,
                toolbarPaddingTop + systemBars.Top,
                toolbar.PaddingRight,
                toolbar.PaddingBottom);

            SetHeight(
                bottomNavigationContainer,
                bottomNavigationContainerHeight + systemBars.Bottom);
            SetBottomMargin(bottomNavigation, bottomNavigationMarginBottom + systemBars.Bottom);
            SetBottomMargin(launchAction, launchActionMarginBottom + systemBars.Bottom);
            SetBottomMargin(launchProgress, launchProgressMarginBottom + systemBars.Bottom);

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

        private static void SetHeight(View view, int height)
        {
            if (view.LayoutParameters is not { } layout)
                return;
            layout.Height = height;
            view.LayoutParameters = layout;
        }

        private static void SetBottomMargin(View view, int bottomMargin)
        {
            if (view.LayoutParameters is not ViewGroup.MarginLayoutParams margins)
                return;
            margins.BottomMargin = bottomMargin;
            view.LayoutParameters = margins;
        }
    }

    private sealed class NavDestinationListener(Action<int> changed) :
        Java.Lang.Object,
        NavController.IOnDestinationChangedListener
    {
        public void OnDestinationChanged(
            NavController controller,
            NavDestination destination,
            Bundle? arguments) => changed(destination.Id);
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
