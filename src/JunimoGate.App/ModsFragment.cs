using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.Fragment.App;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace JunimoGate.App;

[Register("org.junimogate.app.ModsFragment")]
public sealed class ModsFragment : Fragment
{
    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_mods, container, false)
        ?? throw new InvalidOperationException("The Mods layout could not be created.");
}
