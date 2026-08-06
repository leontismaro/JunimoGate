using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.ViewPager2.Adapter;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.Tabs;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace JunimoGate.App;

[Register("org.junimogate.app.SaveBackupsFragment")]
public sealed class SaveBackupsFragment : Fragment
{
    private TabLayoutMediator? tabMediator;
    private ViewPager2? pager;
    private SaveManagementUiSession? session;

    internal SaveManagementUiSession Session => session
        ?? throw new InvalidOperationException("The save management session is unavailable.");

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_save_backups, container, false)
        ?? throw new InvalidOperationException("The save management layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        session = new SaveManagementUiSession(RequireContext());
        var tabs = view.FindViewById<TabLayout>(Resource.Id.save_tabs)
            ?? throw new InvalidOperationException("The save tabs are unavailable.");
        pager = view.FindViewById<ViewPager2>(Resource.Id.save_pager)
            ?? throw new InvalidOperationException("The save pager is unavailable.");
        pager.Adapter = new PagerAdapter(this);
        pager.OffscreenPageLimit = 1;
        tabMediator = new TabLayoutMediator(tabs, pager, new TabConfiguration(this));
        tabMediator.Attach();
    }

    public override void OnDestroyView()
    {
        tabMediator?.Detach();
        tabMediator?.Dispose();
        tabMediator = null;
        if (pager is not null)
        {
            pager.Adapter = null;
            pager = null;
        }
        session?.Dispose();
        session = null;
        base.OnDestroyView();
    }

    private sealed class PagerAdapter(Fragment fragment) : FragmentStateAdapter(fragment)
    {
        public override int ItemCount => 2;

        public override Fragment CreateFragment(int position) => position switch
        {
            0 => new LiveSavesFragment(),
            1 => new AutomaticBackupsFragment(),
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };
    }

    private sealed class TabConfiguration(Fragment fragment) : Java.Lang.Object, TabLayoutMediator.ITabConfigurationStrategy
    {
        public void OnConfigureTab(TabLayout.Tab tab, int position) => tab.SetText(position == 0
            ? fragment.GetString(Resource.String.saves_tab_live)
            : fragment.GetString(Resource.String.saves_tab_backups));
    }
}
