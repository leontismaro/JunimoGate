using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.ViewPager2.Adapter;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.Tabs;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace JunimoGate.App;

[Register("org.junimogate.app.PrimaryPagerFragment")]
public sealed class PrimaryPagerFragment : Fragment
{
    internal const int HomePage = 0;
    internal const int ModsPage = 1;
    internal const int ModGroupsPage = 2;

    private ViewPager2? pager;
    private TabLayout? modTabs;
    private MainShellFragment? mainShell;
    private MainPageChangedCallback? pageChangedCallback;
    private bool syncingTabs;

    internal int? CurrentPage => pager?.CurrentItem;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_primary_pager, container, false)
        ?? throw new InvalidOperationException("The primary pager layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        pager = view.FindViewById<ViewPager2>(Resource.Id.main_pager)
            ?? throw new InvalidOperationException("The primary pager is unavailable.");
        modTabs = view.FindViewById<TabLayout>(Resource.Id.main_mod_tabs)
            ?? throw new InvalidOperationException("The Mod tabs are unavailable.");
        mainShell = FindMainShell()
            ?? throw new InvalidOperationException("The primary pager shell is unavailable.");
        var requestedPage = mainShell.RequestedPrimaryPage;
        pager.Adapter = new PrimaryPagerAdapter(this);
        pager.SetCurrentItem(requestedPage, smoothScroll: false);
        pager.OffscreenPageLimit = 1;
        modTabs.AddTab(modTabs.NewTab().SetText(Resource.String.mods_tab_library));
        modTabs.AddTab(modTabs.NewTab().SetText(Resource.String.mods_tab_groups));
        if (requestedPage >= ModsPage)
        {
            SetModTabsVisible(1f);
            SelectModTab(requestedPage - ModsPage);
        }
        else
            HideModTabs();
        modTabs.TabSelected += OnModTabSelected;
        pageChangedCallback = new MainPageChangedCallback(OnPageSelected, OnPageScrolled, OnPageScrollStateChanged);
        pager.RegisterOnPageChangeCallback(pageChangedCallback);
        mainShell.OnPrimaryPagerReady(this);
    }

    private MainShellFragment? FindMainShell()
    {
        for (var fragment = ParentFragment; fragment is not null; fragment = fragment.ParentFragment)
        {
            if (fragment is MainShellFragment shell)
                return shell;
        }
        return null;
    }

    internal void ApplyPageRequest(int page, bool animate)
    {
        if (page is < HomePage or > ModGroupsPage || pager is null)
            return;
        pager.SetCurrentItem(page, animate);
    }

    public override void OnDestroyView()
    {
        if (pager is not null && pageChangedCallback is not null)
            pager.UnregisterOnPageChangeCallback(pageChangedCallback);
        if (modTabs is not null)
            modTabs.TabSelected -= OnModTabSelected;
        pageChangedCallback?.Dispose();
        pageChangedCallback = null;
        if (pager is not null)
        {
            pager.Adapter = null;
            pager = null;
        }
        modTabs = null;
        mainShell = null;
        base.OnDestroyView();
    }

    private void OnPageSelected(int page)
    {
        mainShell?.OnPrimaryPageChanged(page);
        if (page >= ModsPage)
        {
            SetModTabsVisible(1f);
            SelectModTab(page - ModsPage);
        }
        else if (pager?.ScrollState == ViewPager2.ScrollStateIdle)
            HideModTabs();
    }

    private void OnPageScrolled(int position, float offset)
    {
        mainShell?.OnPrimaryPageScrolled(position, offset);
        if (position == HomePage)
        {
            if (offset <= 0f) HideModTabs(); else SetModTabsVisible(offset);
        }
        else SetModTabsVisible(1f);
    }

    private void OnPageScrollStateChanged(int state)
    {
        if (state == ViewPager2.ScrollStateIdle && pager?.CurrentItem == HomePage)
            HideModTabs();
        if (pager is { } currentPager)
            mainShell?.OnPrimaryPageScrollStateChanged(state, currentPager.CurrentItem);
    }

    private void OnModTabSelected(object? sender, TabLayout.TabSelectedEventArgs args)
    {
        if (!syncingTabs && args.Tab is { } tab)
            mainShell?.RequestPrimaryPage(tab.Position + ModsPage, animate: true);
    }

    private void SelectModTab(int position)
    {
        if (modTabs?.SelectedTabPosition == position)
            return;
        syncingTabs = true;
        modTabs?.GetTabAt(position)?.Select();
        syncingTabs = false;
    }

    private void SetModTabsVisible(float alpha)
    {
        if (modTabs is null) return;
        modTabs.Visibility = ViewStates.Visible;
        modTabs.Alpha = Math.Clamp(alpha, 0f, 1f);
    }

    private void HideModTabs()
    {
        if (modTabs is null) return;
        modTabs.Alpha = 0f;
        modTabs.Visibility = ViewStates.Gone;
    }

    private sealed class PrimaryPagerAdapter(Fragment fragment) : FragmentStateAdapter(fragment)
    {
        public override int ItemCount => 3;
        public override Fragment CreateFragment(int position) => position switch
        {
            HomePage => new HomeFragment(),
            ModsPage => new ModsFragment(),
            ModGroupsPage => new ModGroupsFragment(),
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };
    }

    private sealed class MainPageChangedCallback(Action<int> selected, Action<int, float> scrolled, Action<int> stateChanged) : ViewPager2.OnPageChangeCallback
    {
        public override void OnPageSelected(int position) => selected(position);
        public override void OnPageScrolled(int position, float offset, int pixels) => scrolled(position, offset);
        public override void OnPageScrollStateChanged(int state) => stateChanged(state);
    }
}
