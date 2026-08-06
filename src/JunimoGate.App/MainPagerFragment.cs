using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.ViewPager2.Adapter;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.Tabs;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace JunimoGate.App;

[Register("org.junimogate.app.MainPagerFragment")]
public sealed class MainPagerFragment : Fragment
{
    internal const int HomePage = 0;
    internal const int ModsPage = 1;
    internal const int ModGroupsPage = 2;
    internal const string InitialPageArgument = "initialPage";

    private ViewPager2? pager;
    private TabLayout? modTabs;
    private MainPageChangedCallback? pageChangedCallback;
    private bool syncingTabs;

    internal int CurrentPage => pager?.CurrentItem ?? HomePage;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_main_pager, container, false)
        ?? throw new InvalidOperationException("The main pager layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        pager = view.FindViewById<ViewPager2>(Resource.Id.main_pager)
            ?? throw new InvalidOperationException("The main pager is unavailable.");
        modTabs = view.FindViewById<TabLayout>(Resource.Id.main_mod_tabs)
            ?? throw new InvalidOperationException("The Mod tabs are unavailable.");
        pager.Adapter = new MainPagerAdapter(this);
        pager.OffscreenPageLimit = 1;
        modTabs.AddTab(modTabs.NewTab().SetText(Resource.String.mods_tab_library));
        modTabs.AddTab(modTabs.NewTab().SetText(Resource.String.mods_tab_groups));
        modTabs.TabSelected += OnModTabSelected;
        pageChangedCallback = new MainPageChangedCallback(
            OnPageSelected,
            OnPageScrolled,
            OnPageScrollStateChanged);
        pager.RegisterOnPageChangeCallback(pageChangedCallback);

        var initialPage = Arguments?.GetInt(InitialPageArgument, HomePage) ?? HomePage;
        ShowPage(initialPage is >= HomePage and <= ModGroupsPage ? initialPage : HomePage, animate: false);
    }

    internal void ShowPage(int page, bool animate)
    {
        if (page is < HomePage or > ModGroupsPage || pager is null)
            return;
        pager.SetCurrentItem(page, animate);
        OnPageSelected(page);
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
        base.OnDestroyView();
    }

    private void OnPageSelected(int page)
    {
        (Activity as MainActivity)?.OnMainPageChanged(page);
        if (page >= ModsPage)
        {
            SetModTabsVisible(1f);
            SelectModTab(page - ModsPage);
        }
        else if (pager?.ScrollState == ViewPager2.ScrollStateIdle)
        {
            HideModTabs();
        }
    }

    private void OnPageScrolled(int position, float offset)
    {
        (Activity as MainActivity)?.OnMainPageScrolled(position, offset);
        if (position == HomePage)
        {
            if (offset <= 0f)
                HideModTabs();
            else
                SetModTabsVisible(offset);
        }
        else
        {
            SetModTabsVisible(1f);
        }
    }

    private void OnPageScrollStateChanged(int state)
    {
        (Activity as MainActivity)?.OnMainPageScrollStateChanged(
            state,
            pager?.CurrentItem ?? HomePage);
        if (state == ViewPager2.ScrollStateIdle && pager?.CurrentItem == HomePage)
            HideModTabs();
    }

    private void OnModTabSelected(object? sender, TabLayout.TabSelectedEventArgs eventArgs)
    {
        if (!syncingTabs && eventArgs.Tab is { } tab)
            ShowPage(tab.Position + ModsPage, animate: true);
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
        if (modTabs is null)
            return;
        modTabs.Visibility = ViewStates.Visible;
        modTabs.Alpha = Math.Clamp(alpha, 0f, 1f);
    }

    private void HideModTabs()
    {
        if (modTabs is null)
            return;
        modTabs.Alpha = 0f;
        modTabs.Visibility = ViewStates.Gone;
    }

    private sealed class MainPagerAdapter(Fragment fragment) : FragmentStateAdapter(fragment)
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

    private sealed class MainPageChangedCallback(
        Action<int> selected,
        Action<int, float> scrolled,
        Action<int> scrollStateChanged) : ViewPager2.OnPageChangeCallback
    {
        public override void OnPageSelected(int position) => selected(position);

        public override void OnPageScrolled(int position, float positionOffset, int positionOffsetPixels) =>
            scrolled(position, positionOffset);

        public override void OnPageScrollStateChanged(int state) => scrollStateChanged(state);
    }

}
