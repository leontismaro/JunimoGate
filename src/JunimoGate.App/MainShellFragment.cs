using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.Core.View;
using AndroidX.Navigation;
using AndroidX.Navigation.Fragment;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.FloatingActionButton;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Android;
using Fragment = AndroidX.Fragment.App.Fragment;
using FragmentManager = AndroidX.Fragment.App.FragmentManager;

namespace JunimoGate.App;

[Register("org.junimogate.app.MainShellFragment")]
public sealed class MainShellFragment : AndroidX.Fragment.App.Fragment
{
    private const string PrimaryPageStateKey = "main-shell-primary-page";

    private NavController? navigation;
    private NavDestinationListener? destinationListener;
    private FragmentManager? navHostFragmentManager;
    private DestinationViewLifecycleCallbacks? destinationViewCallbacks;
    private MaterialToolbar? toolbar;
    private View? bottomHome;
    private View? bottomMods;
    private View? bottomHomeIndicator;
    private View? bottomModsIndicator;
    private View? homeBackdrop;
    private FloatingActionButton? launchAction;
    private CircularProgressIndicator? launchProgress;
    private int currentPrimaryPage = PrimaryPagerFragment.HomePage;
    private int? requestedPrimaryPage;
    private int? renderedDestinationId;
    private float primaryHomeBackdropAlpha = 1f;
    private bool primaryPagerIdle = true;

    internal int RequestedPrimaryPage => requestedPrimaryPage ?? currentPrimaryPage;

    public override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var restoredPage = savedInstanceState?.GetInt(PrimaryPageStateKey, PrimaryPagerFragment.HomePage)
            ?? PrimaryPagerFragment.HomePage;
        if (restoredPage is >= PrimaryPagerFragment.HomePage and <= PrimaryPagerFragment.ModGroupsPage)
            currentPrimaryPage = restoredPage;
    }

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_main_shell, container, false)
        ?? throw new InvalidOperationException("The main shell layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        var host = ChildFragmentManager.FindFragmentById(Resource.Id.shell_nav_host) as NavHostFragment
            ?? throw new InvalidOperationException("The main shell navigation host is unavailable.");
        navigation = host.NavController;
        navHostFragmentManager = host.ChildFragmentManager;

        toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.top_app_bar)
            ?? throw new InvalidOperationException("The launcher toolbar is unavailable.");
        bottomHome = view.FindViewById<View>(Resource.Id.bottom_navigation_home)
            ?? throw new InvalidOperationException("The Home navigation area is unavailable.");
        bottomMods = view.FindViewById<View>(Resource.Id.bottom_navigation_mods)
            ?? throw new InvalidOperationException("The Mod navigation area is unavailable.");
        bottomHomeIndicator = view.FindViewById<View>(Resource.Id.bottom_navigation_home_indicator)
            ?? throw new InvalidOperationException("The Home navigation indicator is unavailable.");
        bottomModsIndicator = view.FindViewById<View>(Resource.Id.bottom_navigation_mods_indicator)
            ?? throw new InvalidOperationException("The Mod navigation indicator is unavailable.");
        homeBackdrop = view.FindViewById<View>(Resource.Id.home_backdrop)
            ?? throw new InvalidOperationException("The Home backdrop is unavailable.");
        launchAction = view.FindViewById<FloatingActionButton>(Resource.Id.launch_action)
            ?? throw new InvalidOperationException("The game launch action is unavailable.");
        launchProgress = view.FindViewById<CircularProgressIndicator>(Resource.Id.launch_progress)
            ?? throw new InvalidOperationException("The game launch progress is unavailable.");

        bottomHome.Click += OnBottomHomeClicked;
        bottomMods.Click += OnBottomModsClicked;
        launchAction.Click += OnLaunchClicked;
        ViewCompat.SetOnApplyWindowInsetsListener(
            view,
            new ShellInsetsListener(
                toolbar,
                view.FindViewById<View>(Resource.Id.bottom_navigation_container)!,
                view.FindViewById<View>(Resource.Id.bottom_navigation)!,
                launchAction,
                launchProgress));
        ViewCompat.RequestApplyInsets(view);

        if (Activity is MainActivity activity)
        {
            activity.SetShellToolbar(toolbar);
            ((ILauncherUiHost)activity).LauncherStateChanged += OnLauncherStateChanged;
            RenderLauncherState(((ILauncherUiHost)activity).CurrentState);
        }
        destinationViewCallbacks = new DestinationViewLifecycleCallbacks(OnDestinationViewCreated);
        navHostFragmentManager.RegisterFragmentLifecycleCallbacks(destinationViewCallbacks, recursive: false);
        destinationListener = new NavDestinationListener(OnDestinationChanged);
        navigation.AddOnDestinationChangedListener(destinationListener);
        TryCommitCurrentDestination();
        if (Activity is MainActivity readyActivity &&
            ((ILauncherUiHost)readyActivity).CurrentState.Status is
                LauncherStatus.NeedsPreparation or LauncherStatus.GameNotInstalled or LauncherStatus.Unsupported)
            OpenEnvironment();
    }

    public override void OnDestroyView()
    {
        if (Activity is MainActivity activity)
            ((ILauncherUiHost)activity).LauncherStateChanged -= OnLauncherStateChanged;
        if (navigation is not null && destinationListener is not null)
            navigation.RemoveOnDestinationChangedListener(destinationListener);
        if (navHostFragmentManager is not null && destinationViewCallbacks is not null)
            navHostFragmentManager.UnregisterFragmentLifecycleCallbacks(destinationViewCallbacks);
        destinationListener?.Dispose();
        destinationListener = null;
        destinationViewCallbacks?.Dispose();
        destinationViewCallbacks = null;
        navHostFragmentManager = null;
        navigation = null;
        renderedDestinationId = null;
        if (bottomHome is not null)
            bottomHome.Click -= OnBottomHomeClicked;
        if (bottomMods is not null)
            bottomMods.Click -= OnBottomModsClicked;
        if (launchAction is not null)
            launchAction.Click -= OnLaunchClicked;
        toolbar = null;
        bottomHome = null;
        bottomMods = null;
        bottomHomeIndicator = null;
        bottomModsIndicator = null;
        homeBackdrop = null;
        launchAction = null;
        launchProgress = null;
        base.OnDestroyView();
    }

    public override void OnSaveInstanceState(Bundle outState)
    {
        outState.PutInt(PrimaryPageStateKey, RequestedPrimaryPage);
        base.OnSaveInstanceState(outState);
    }

    internal void NavigateDrawerDestination(int destinationId)
    {
        if (navigation is null)
            return;
        if (navigation.CurrentDestination?.Id == destinationId && renderedDestinationId == destinationId)
        {
            (Activity as MainActivity)?.CloseNavigationDrawer();
            return;
        }
        NavigateFromMainRoot(destinationId);
    }

    internal bool HandleBack()
    {
        if (navigation is null)
            return false;
        if (navigation.CurrentDestination?.Id != Resource.Id.navigation_main)
        {
            NavigateFromMainRoot(Resource.Id.navigation_main);
            return true;
        }
        if (GetPrimaryPager()?.CurrentPage is { } page && page != PrimaryPagerFragment.HomePage)
        {
            RequestPrimaryPage(PrimaryPagerFragment.HomePage, animate: true);
            return true;
        }
        return false;
    }

    internal void OnPrimaryPagerReady(PrimaryPagerFragment pager)
    {
        if (navigation?.CurrentDestination?.Id != Resource.Id.navigation_main ||
            pager.CurrentPage is not { } page)
            return;

        currentPrimaryPage = page;
        if (requestedPrimaryPage == page)
            requestedPrimaryPage = null;
        primaryHomeBackdropAlpha = page == PrimaryPagerFragment.HomePage ? 1f : 0f;
        primaryPagerIdle = true;
        if (renderedDestinationId == Resource.Id.navigation_main)
            SyncVisualState();
    }

    internal void RequestPrimaryPage(int page, bool animate = true)
    {
        if (page is < PrimaryPagerFragment.HomePage or > PrimaryPagerFragment.ModGroupsPage)
            return;
        requestedPrimaryPage = page;
        if (navigation?.CurrentDestination?.Id != Resource.Id.navigation_main)
        {
            NavigateFromMainRoot(Resource.Id.navigation_main);
            return;
        }

        if (GetPrimaryPager() is { } pager)
            ApplyPrimaryPageRequest(pager, page, animate);
    }

    internal void OpenEnvironment()
    {
        if (navigation is not null && navigation.CurrentDestination?.Id != Resource.Id.navigation_environment)
            NavigateFromMainRoot(Resource.Id.navigation_environment);
    }

    internal void SetEditorToolbar(
        string title,
        EventHandler<AndroidX.AppCompat.Widget.Toolbar.NavigationClickEventArgs> handler)
    {
        if (toolbar is null)
            return;
        toolbar.Title = title;
        toolbar.SetNavigationIcon(Resource.Drawable.ic_chevron_left_24);
        toolbar.SetNavigationContentDescription(Resource.String.action_back);
        toolbar.NavigationClick += handler;
    }

    internal void ClearEditorToolbar(
        EventHandler<AndroidX.AppCompat.Widget.Toolbar.NavigationClickEventArgs> handler)
    {
        if (toolbar is null)
            return;
        toolbar.NavigationClick -= handler;
        toolbar.NavigationIcon = null;
        toolbar.Title = GetString(Resource.String.app_name);
    }

    internal void SetEditorTitle(string title)
    {
        if (toolbar is not null && renderedDestinationId == Resource.Id.navigation_mod_group_editor)
            toolbar.Title = title;
    }

    internal void RenderLauncherState(LauncherState state)
    {
        if (launchAction is null || launchProgress is null)
            return;
        launchAction.Enabled = state.CanLaunch;
        launchAction.ContentDescription = GetString(LauncherTextFormatter.GetActionTextResource(state));
        launchProgress.Visibility = state.ShowProgress ? ViewStates.Visible : ViewStates.Gone;
    }

    private void OnLauncherStateChanged(LauncherState state) =>
        Activity?.RunOnUiThread(() => RenderLauncherState(state));

    private void OnLaunchClicked(object? sender, EventArgs args) =>
        ((ILauncherUiHost?)Activity)?.RequestLaunch();

    private void OnBottomHomeClicked(object? sender, EventArgs args)
        => RequestPrimaryPage(PrimaryPagerFragment.HomePage);

    private void OnBottomModsClicked(object? sender, EventArgs args)
        => RequestPrimaryPage(PrimaryPagerFragment.ModsPage);

    private PrimaryPagerFragment? GetPrimaryPager()
        => navHostFragmentManager?.PrimaryNavigationFragment as PrimaryPagerFragment;

    internal void OnPrimaryPageChanged(int page)
    {
        currentPrimaryPage = page;
        if (renderedDestinationId != Resource.Id.navigation_main)
            return;
        if (requestedPrimaryPage == page)
            requestedPrimaryPage = null;
        SyncVisualState(animate: true);
    }

    internal void OnPrimaryPageScrolled(int position, float offset)
    {
        if (renderedDestinationId != Resource.Id.navigation_main)
            return;

        primaryHomeBackdropAlpha = position == PrimaryPagerFragment.HomePage
            ? 1f - Math.Clamp(offset, 0f, 1f)
            : 0f;
        RenderHomeBackdrop(primaryHomeBackdropAlpha);

        if (Activity is MainActivity activity && primaryHomeBackdropAlpha < 0.999f)
            activity.SetDrawerSwipeEnabled(false);
    }

    internal void OnPrimaryPageScrollStateChanged(int state, int page)
    {
        if (renderedDestinationId != Resource.Id.navigation_main)
            return;
        primaryPagerIdle = state == ViewPager2.ScrollStateIdle;
        if (primaryPagerIdle)
            primaryHomeBackdropAlpha = page == PrimaryPagerFragment.HomePage ? 1f : 0f;
        SyncVisualState();
    }

    private void OnDestinationChanged(int _)
    {
        TryCommitCurrentDestination();
    }

    private void OnDestinationViewCreated(Fragment fragment)
    {
        if (!TryGetDestinationId(fragment, out var destinationId) ||
            navigation?.CurrentDestination?.Id != destinationId)
            return;
        CommitDestinationView(destinationId, fragment as PrimaryPagerFragment);
    }

    private void TryCommitCurrentDestination()
    {
        var fragment = navHostFragmentManager?.PrimaryNavigationFragment;
        if (fragment?.View is null || !TryGetDestinationId(fragment, out var destinationId) ||
            navigation?.CurrentDestination?.Id != destinationId)
            return;
        CommitDestinationView(destinationId, fragment as PrimaryPagerFragment);
    }

    private void CommitDestinationView(int destinationId, PrimaryPagerFragment? primaryPager)
    {
        renderedDestinationId = destinationId;
        if (destinationId != Resource.Id.navigation_main)
            primaryPagerIdle = true;
        if (toolbar is not null && destinationId != Resource.Id.navigation_mod_group_editor)
        {
            toolbar.NavigationIcon = null;
            toolbar.Title = destinationId == Resource.Id.navigation_main
                ? GetString(Resource.String.app_name)
                : navigation?.CurrentDestination?.Label;
        }
        if (destinationId == Resource.Id.navigation_main && primaryPager is not null)
            ApplyPrimaryPageRequest(primaryPager, RequestedPrimaryPage, animate: false);
        SyncVisualState(primaryPager);
        if (Activity is MainActivity { IsNavigationDrawerOpen: true } activity)
            activity.CloseNavigationDrawer();
    }

    private void NavigateFromMainRoot(int destinationId)
    {
        if (navigation is null)
            return;

        using var builder = new NavOptions.Builder();
        builder.SetLaunchSingleTop(true);
        builder.SetPopUpTo(Resource.Id.navigation_main, inclusive: false);
        builder.SetEnterAnim(0);
        builder.SetExitAnim(0);
        builder.SetPopEnterAnim(0);
        builder.SetPopExitAnim(0);
        using var options = builder.Build();
        navigation.Navigate(destinationId, null, options);
    }

    private void ApplyPrimaryPageRequest(PrimaryPagerFragment pager, int page, bool animate)
    {
        if (!animate)
            primaryHomeBackdropAlpha = page == PrimaryPagerFragment.HomePage ? 1f : 0f;

        if (pager.CurrentPage == page)
        {
            if (renderedDestinationId == Resource.Id.navigation_main)
                requestedPrimaryPage = null;
            return;
        }

        pager.ApplyPageRequest(page, animate);
    }

    private void SyncVisualState(PrimaryPagerFragment? primaryPager = null, bool animate = false)
    {
        if (renderedDestinationId is not { } destinationId)
            return;
        var isMainDestination = destinationId == Resource.Id.navigation_main;
        var page = (primaryPager ?? GetPrimaryPager())?.CurrentPage;
        RenderHomeBackdrop(isMainDestination ? primaryHomeBackdropAlpha : 0f);
        RenderBottomNavigation(animate);
        if (Activity is MainActivity activity)
        {
            activity.RenderDrawerSelection(destinationId);
            activity.SetDrawerSwipeEnabled(isMainDestination &&
                                           primaryPagerIdle &&
                                           primaryHomeBackdropAlpha >= 0.999f &&
                                           page == PrimaryPagerFragment.HomePage);
        }
    }

    private void RenderBottomNavigation(bool animate = false)
    {
        var effectivePage = currentPrimaryPage;
        var home = effectivePage == PrimaryPagerFragment.HomePage &&
                   renderedDestinationId == Resource.Id.navigation_main;
        var mods = effectivePage >= PrimaryPagerFragment.ModsPage ||
                   renderedDestinationId == Resource.Id.navigation_mod_group_editor;
        if (bottomHome is not null)
            bottomHome.Selected = home;
        if (bottomMods is not null)
            bottomMods.Selected = mods;
        if (bottomHomeIndicator is not null)
            RenderBottomIndicator(bottomHomeIndicator, home, animate);
        if (bottomModsIndicator is not null)
            RenderBottomIndicator(bottomModsIndicator, mods, animate);
    }

    private void RenderHomeBackdrop(float alpha)
    {
        if (homeBackdrop is null)
            return;
        var clampedAlpha = Math.Clamp(alpha, 0f, 1f);
        homeBackdrop.Alpha = clampedAlpha;
        homeBackdrop.Visibility = clampedAlpha > 0.001f ? ViewStates.Visible : ViewStates.Gone;
    }

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
        indicator.Animate()?.ScaleX(1f).Alpha(1f).SetDuration(280L).Start();
    }

    private static bool TryGetDestinationId(Fragment fragment, out int destinationId)
    {
        destinationId = fragment switch
        {
            PrimaryPagerFragment => Resource.Id.navigation_main,
            LogsFragment => Resource.Id.navigation_logs,
            SettingsFragment => Resource.Id.navigation_settings,
            EnvironmentFragment => Resource.Id.navigation_environment,
            SaveBackupsFragment => Resource.Id.navigation_save_backups,
            AboutFragment => Resource.Id.navigation_about,
            ModGroupEditorFragment => Resource.Id.navigation_mod_group_editor,
            _ => 0,
        };
        return destinationId != 0;
    }

    private sealed class NavDestinationListener(Action<int> changed) : Java.Lang.Object, NavController.IOnDestinationChangedListener
    {
        public void OnDestinationChanged(NavController controller, NavDestination destination, Bundle? arguments) => changed(destination.Id);
    }

    private sealed class DestinationViewLifecycleCallbacks(Action<Fragment> created) :
        FragmentManager.FragmentLifecycleCallbacks
    {
        public override void OnFragmentViewCreated(
            FragmentManager fragmentManager,
            Fragment fragment,
            View view,
            Bundle? savedInstanceState) => created(fragment);
    }

    private sealed class ShellInsetsListener(
        View toolbar,
        View bottomContainer,
        View bottomNavigation,
        View launchAction,
        View launchProgress) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private readonly int toolbarHeight = toolbar.LayoutParameters?.Height ?? 0;
        private readonly int toolbarPaddingTop = toolbar.PaddingTop;
        private readonly int bottomContainerHeight = bottomContainer.LayoutParameters?.Height ?? 0;
        private readonly int bottomMargin = (bottomNavigation.LayoutParameters as ViewGroup.MarginLayoutParams)?.BottomMargin ?? 0;
        private readonly int launchMargin = (launchAction.LayoutParameters as ViewGroup.MarginLayoutParams)?.BottomMargin ?? 0;
        private readonly int progressMargin = (launchProgress.LayoutParameters as ViewGroup.MarginLayoutParams)?.BottomMargin ?? 0;

        public WindowInsetsCompat? OnApplyWindowInsets(View? view, WindowInsetsCompat? insets)
        {
            if (insets is null)
                return null;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            if (bars is null)
                return insets;
            SetHeight(toolbar, toolbarHeight + bars.Top);
            toolbar.SetPadding(toolbar.PaddingLeft, toolbarPaddingTop + bars.Top, toolbar.PaddingRight, toolbar.PaddingBottom);
            SetHeight(bottomContainer, bottomContainerHeight + bars.Bottom);
            SetBottomMargin(bottomNavigation, bottomMargin + bars.Bottom);
            SetBottomMargin(launchAction, launchMargin + bars.Bottom);
            SetBottomMargin(launchProgress, progressMargin + bars.Bottom);
            return insets;
        }

        private static void SetHeight(View view, int height)
        {
            if (view.LayoutParameters is { } layout)
            {
                layout.Height = height;
                view.LayoutParameters = layout;
            }
        }

        private static void SetBottomMargin(View view, int margin)
        {
            if (view.LayoutParameters is ViewGroup.MarginLayoutParams layout)
            {
                layout.BottomMargin = margin;
                view.LayoutParameters = layout;
            }
        }
    }
}
