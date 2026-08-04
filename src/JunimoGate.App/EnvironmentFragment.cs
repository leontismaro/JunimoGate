using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Fragment = AndroidX.Fragment.App.Fragment;
using JInteger = Java.Lang.Integer;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.EnvironmentFragment")]
public sealed class EnvironmentFragment : Fragment
{
    private TextView? appVersion;
    private TextView? playGame;
    private TextView? samsungGame;
    private TextView? smapi;
    private CircularProgressIndicator? progress;
    private MaterialButton? refresh;
    private CancellationTokenSource? cancellation;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_environment, container, false)
        ?? throw new InvalidOperationException("The environment layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        appVersion = view.FindViewById<TextView>(Resource.Id.environment_app_version);
        playGame = view.FindViewById<TextView>(Resource.Id.environment_play_game);
        samsungGame = view.FindViewById<TextView>(Resource.Id.environment_samsung_game);
        smapi = view.FindViewById<TextView>(Resource.Id.environment_smapi);
        progress = view.FindViewById<CircularProgressIndicator>(Resource.Id.environment_progress);
        refresh = view.FindViewById<MaterialButton>(Resource.Id.environment_refresh);
        refresh!.Click += OnRefreshClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        Refresh();
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (refresh is not null)
            refresh.Click -= OnRefreshClicked;
        appVersion = null;
        playGame = null;
        samsungGame = null;
        smapi = null;
        progress = null;
        refresh = null;
        base.OnDestroyView();
    }

    private void OnRefreshClicked(object? sender, EventArgs eventArgs) => Refresh();

    private void Refresh()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        _ = LoadAsync(cancellation.Token);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (progress is not null)
            progress.Visibility = ViewStates.Visible;
        if (refresh is not null)
            refresh.Enabled = false;
        try
        {
            var info = await new ProductInformationService(RequireContext())
                .ReadEnvironmentAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => Render(info));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving or refreshing cancels the previous read.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            global::Android.Util.Log.Warn("JunimoGate.Environment", $"environment-unavailable:{exception.GetType().Name}");
            Activity?.RunOnUiThread(() =>
            {
                if (playGame is not null)
                    playGame.Text = GetString(Resource.String.environment_read_failed);
            });
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Activity?.RunOnUiThread(() =>
                {
                    if (progress is not null)
                        progress.Visibility = ViewStates.Gone;
                    if (refresh is not null)
                        refresh.Enabled = true;
                });
            }
        }
    }

    private void Render(EnvironmentDisplayInfo info)
    {
        if (appVersion is null || playGame is null || samsungGame is null || smapi is null)
            return;
        var play = info.Games.Single(game => game.PackageName == JunimoGate.Android.AndroidPlatformBoundary.PlayPackageName);
        var samsung = info.Games.Single(game => game.PackageName == JunimoGate.Android.AndroidPlatformBoundary.SamsungPackageName);
        appVersion.Text = FormatString(
            Resource.String.environment_app_version_value,
            new JString(info.AppVersion));
        playGame.Text = FormatGame(play);
        samsungGame.Text = FormatGame(samsung);
        smapi.Text = FormatString(
            Resource.String.environment_smapi_value,
            new JString(info.Smapi.SmapiApiVersion),
            new JString(info.Smapi.SmapiImplementationVersion),
            new JString(info.Smapi.BuildId),
            new JString(info.Smapi.BundleId),
            JInteger.ValueOf(info.Smapi.BundleFileCount));
    }

    private string FormatGame(InstalledGameDisplayInfo game)
    {
        if (!game.IsInstalled)
        {
            return FormatString(
                Resource.String.environment_game_not_installed,
                new JString(game.StoreName),
                new JString(game.PackageName));
        }
        var status = game.Status switch
        {
            "supported" => GetString(Resource.String.environment_status_supported),
            "unrecognized" => GetString(Resource.String.environment_status_unrecognized),
            _ => GetString(Resource.String.environment_status_unsupported),
        };
        return FormatString(
            Resource.String.environment_game_value,
            new JString(game.StoreName),
            new JString(game.VersionName ?? "—"),
            new JString(game.VersionCode?.ToString() ?? "—"),
            new JString(status),
            new JString(game.PackageName));
    }

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The environment string resource is unavailable.");
}
