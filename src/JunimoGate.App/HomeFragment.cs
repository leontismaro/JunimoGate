using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace JunimoGate.App;

[Register("org.junimogate.app.HomeFragment")]
public sealed class HomeFragment : Fragment
{
    private ILauncherUiHost? host;
    private TextView? statusText;
    private LinearProgressIndicator? progress;
    private MaterialButton? launchButton;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_home, container, false)
        ?? throw new InvalidOperationException("The Home layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        statusText = view.FindViewById<TextView>(Resource.Id.home_status);
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.home_progress);
        launchButton = view.FindViewById<MaterialButton>(Resource.Id.home_launch_button);
        launchButton!.Click += OnLaunchClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        host = Activity as ILauncherUiHost
            ?? throw new InvalidOperationException("The Home screen requires a launcher host.");
        host.LauncherStateChanged += OnStateChanged;
        Render(host.CurrentState);
    }

    public override void OnStop()
    {
        if (host is not null)
            host.LauncherStateChanged -= OnStateChanged;
        host = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (launchButton is not null)
            launchButton.Click -= OnLaunchClicked;
        statusText = null;
        progress = null;
        launchButton = null;
        base.OnDestroyView();
    }

    private void OnLaunchClicked(object? sender, EventArgs eventArgs) => host?.RequestLaunch();

    private void OnStateChanged(LauncherState state) => Render(state);

    private void Render(LauncherState state)
    {
        if (statusText is null || progress is null || launchButton is null)
            return;
        statusText.Text = state.Message;
        progress.Visibility = state.ShowProgress ? ViewStates.Visible : ViewStates.Gone;
        launchButton.Enabled = state.CanLaunch;
        launchButton.Text = GetString(state.Status == LauncherStatus.Launching
            ? Resource.String.launching_game
            : Resource.String.launch_game);
    }
}
