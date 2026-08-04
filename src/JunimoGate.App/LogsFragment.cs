using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.Fragment.App;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace JunimoGate.App;

[Register("org.junimogate.app.LogsFragment")]
public sealed class LogsFragment : Fragment
{
    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_logs, container, false)
        ?? throw new InvalidOperationException("The Logs layout could not be created.");
}
