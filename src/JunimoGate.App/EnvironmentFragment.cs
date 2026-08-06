using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Fragment = AndroidX.Fragment.App.Fragment;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.EnvironmentFragment")]
public sealed class EnvironmentFragment : Fragment
{
    private TextView? appVersion;
    private LinearLayout? games;
    private TextView? gamesEmpty;
    private TextView? smapi;
    private LinearProgressIndicator? progress;
    private MaterialButton? refresh;
    private CancellationTokenSource? cancellation;

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_environment, container, false)
        ?? throw new InvalidOperationException("The environment layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        appVersion = view.FindViewById<TextView>(Resource.Id.environment_app_version);
        games = view.FindViewById<LinearLayout>(Resource.Id.environment_games);
        gamesEmpty = view.FindViewById<TextView>(Resource.Id.environment_games_empty);
        smapi = view.FindViewById<TextView>(Resource.Id.environment_smapi);
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.environment_progress);
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
        games = null;
        gamesEmpty = null;
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
                if (gamesEmpty is not null)
                {
                    gamesEmpty.Text = GetString(Resource.String.environment_read_failed);
                    gamesEmpty.Visibility = ViewStates.Visible;
                }
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
        if (appVersion is null || games is null || gamesEmpty is null || smapi is null)
            return;
        appVersion.Text = FormatString(
            Resource.String.environment_app_version_value,
            new JString(info.AppVersion));
        games.RemoveAllViews();
        foreach (var game in info.Games)
            games.AddView(CreateGameView(game));
        gamesEmpty.Visibility = info.Games.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        smapi.Text = FormatString(
            Resource.String.environment_smapi_value,
            new JString(info.Smapi.SmapiApiVersion));
    }

    private View CreateGameView(InstalledGameDisplayInfo game)
    {
        var view = LayoutInflater.From(RequireContext())?.Inflate(Resource.Layout.item_installed_game, games, false)
            ?? throw new InvalidOperationException("The installed game item could not be created.");
        var icon = view.FindViewById<ImageView>(Resource.Id.installed_game_icon);
        var name = view.FindViewById<TextView>(Resource.Id.installed_game_name);
        var version = view.FindViewById<TextView>(Resource.Id.installed_game_version);
        var status = view.FindViewById<TextView>(Resource.Id.installed_game_status);
        name!.Text = game.DisplayName;
        version!.Text = FormatString(
            Resource.String.environment_game_version,
            new JString(game.VersionName ?? "—"),
            new JString(game.VersionCode?.ToString() ?? "—"));
        var statusText = game.Status switch
        {
            InstalledGameStatus.Supported => GetString(Resource.String.environment_status_supported),
            InstalledGameStatus.Unrecognized => GetString(Resource.String.environment_status_unrecognized),
            InstalledGameStatus.DetectedUnsupported => GetString(Resource.String.environment_status_unsupported),
            _ => throw new InvalidOperationException("The installed game status is invalid."),
        };
        status!.Text = statusText;
        TrySetGameIcon(icon, game.PackageName);
        return view;
    }

    private void TrySetGameIcon(ImageView? icon, string packageName)
    {
        if (icon is null)
            return;
        try
        {
            var manager = RequireContext().PackageManager;
            var application = manager is null
                ? null
                : OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? manager.GetApplicationInfo(
                        packageName,
                        PackageManager.ApplicationInfoFlags.Of(0L))
                    : manager.GetApplicationInfo(packageName, (PackageInfoFlags)0);
            var drawable = application?.LoadIcon(manager);
            if (drawable is not null)
                icon.SetImageDrawable(drawable);
        }
        catch (PackageManager.NameNotFoundException)
        {
            // The package can disappear between the snapshot and UI render; the fallback icon remains.
        }
    }

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The environment string resource is unavailable.");
}
