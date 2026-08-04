using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Fragment = AndroidX.Fragment.App.Fragment;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.HomeFragment")]
public sealed class HomeFragment : Fragment
{
    private ILauncherUiHost? host;
    private TextView? statusText;
    private LinearProgressIndicator? progress;
    private MaterialButton? launchButton;
    private TextView? playTimeText;
    private TextView? lastSaveText;
    private TextView? modCountText;
    private CancellationTokenSource? summaryCancellation;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_home, container, false)
        ?? throw new InvalidOperationException("The Home layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        statusText = view.FindViewById<TextView>(Resource.Id.home_status);
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.home_progress);
        launchButton = view.FindViewById<MaterialButton>(Resource.Id.home_launch_button);
        playTimeText = view.FindViewById<TextView>(Resource.Id.home_play_time);
        lastSaveText = view.FindViewById<TextView>(Resource.Id.home_last_save);
        modCountText = view.FindViewById<TextView>(Resource.Id.home_mod_count);
        launchButton!.Click += OnLaunchClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        host = Activity as ILauncherUiHost
            ?? throw new InvalidOperationException("The Home screen requires a launcher host.");
        host.LauncherStateChanged += OnStateChanged;
        Render(host.CurrentState);
        summaryCancellation = new CancellationTokenSource();
        _ = LoadSummaryAsync(summaryCancellation.Token);
    }

    public override void OnStop()
    {
        if (host is not null)
            host.LauncherStateChanged -= OnStateChanged;
        host = null;
        summaryCancellation?.Cancel();
        summaryCancellation?.Dispose();
        summaryCancellation = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (launchButton is not null)
            launchButton.Click -= OnLaunchClicked;
        statusText = null;
        progress = null;
        launchButton = null;
        playTimeText = null;
        lastSaveText = null;
        modCountText = null;
        base.OnDestroyView();
    }

    private void OnLaunchClicked(object? sender, EventArgs eventArgs) => host?.RequestLaunch();

    private void OnStateChanged(LauncherState state) => Render(state);

    private async Task LoadSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var summary = await Task.Run(
                () => new ProductInformationService(RequireContext()).ReadHomeSummary(),
                cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Activity?.RunOnUiThread(() =>
                {
                    if (playTimeText is not null)
                        playTimeText.Text = $"{GetString(Resource.String.play_time_label)}\n—";
                    if (lastSaveText is not null)
                    {
                        lastSaveText.Text = summary.LatestSaveTimeUtc is null
                            ? $"{GetString(Resource.String.last_save_label)}\n—"
                            : $"{GetString(Resource.String.last_save_label)}\n{summary.LatestSaveName}\n{summary.LatestSaveTimeUtc.Value.ToLocalTime():g}";
                    }
                    if (modCountText is not null)
                        modCountText.Text = $"{GetString(Resource.String.enabled_mods_label)}\n{summary.EnabledModCount}";
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels the low-cost summary refresh.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            global::Android.Util.Log.Warn("JunimoGate.Home", $"summary-unavailable:{exception.GetType().Name}");
        }
    }

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
