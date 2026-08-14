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
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.App;

[Register("org.junimogate.app.EnvironmentFragment")]
public sealed class EnvironmentFragment : Fragment
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
    private TextView? appVersion;
    private LinearLayout? games;
    private TextView? gamesEmpty;
    private TextView? smapi;
    private LinearProgressIndicator? progress;
    private MaterialButton? refresh;
    private CancellationTokenSource? cancellation;
    private readonly EnvironmentReadGeneration readGeneration = new();

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
        readGeneration.Invalidate();
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
        var generation = readGeneration.Begin();
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        _ = LoadAsync(generation, cancellation.Token);
    }

    private async Task LoadAsync(long generation, CancellationToken cancellationToken)
    {
        if (progress is not null)
            progress.Visibility = ViewStates.Visible;
        if (refresh is not null)
            refresh.Enabled = false;
        try
        {
            var service = new ProductInformationService(RequireContext());
            var local = await service.ReadLocalEnvironmentAsync(cancellationToken);
            PostIfCurrent(generation, cancellationToken, () => RenderLocal(local));
            var packageRead = service
                .ReadEnvironmentAsync(generation, local, ReadTimeout, cancellationToken)
                .AsTask();
            var info = await packageRead;
            if (!readGeneration.IsCurrent(generation) || cancellationToken.IsCancellationRequested)
                return;
            PostIfCurrent(generation, cancellationToken, () => Render(info));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving or refreshing cancels the previous read.
        }
        catch (TimeoutException)
        {
            Log.Warn("JunimoGate.Environment", $"environment-read-timeout generation={generation} elapsedMs={(long)ReadTimeout.TotalMilliseconds}");
            PostIfCurrent(generation, cancellationToken, RenderReadFailure);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            Log.Warn(
                "JunimoGate.Environment",
                $"environment-read-failed generation={generation} exception={exception.GetType().Name}");
            PostIfCurrent(generation, cancellationToken, RenderReadFailure);
        }
        finally
        {
            PostIfCurrent(generation, cancellationToken, FinishLoading);
        }
    }

    private void PostIfCurrent(long generation, CancellationToken cancellationToken, Action action)
    {
        if (!readGeneration.IsCurrent(generation) || cancellationToken.IsCancellationRequested)
            return;
        Activity?.RunOnUiThread(() =>
        {
            if (readGeneration.IsCurrent(generation) && !cancellationToken.IsCancellationRequested && IsAdded)
                action();
        });
    }

    private void FinishLoading()
    {
        if (progress is not null)
            progress.Visibility = ViewStates.Gone;
        if (refresh is not null)
            refresh.Enabled = true;
    }

    private void RenderLocal(LocalEnvironmentDisplayInfo info)
    {
        if (appVersion is null || smapi is null)
            return;
        appVersion.Text = FormatString(
            Resource.String.environment_app_version_value,
            new JString(info.AppVersion));
        smapi.Text = FormatString(
            Resource.String.environment_smapi_value,
            new JString(info.Smapi.SmapiApiVersion));
    }

    private void Render(EnvironmentDisplayInfo info)
    {
        if (appVersion is null || games is null || gamesEmpty is null || smapi is null)
            return;
        RenderLocal(new LocalEnvironmentDisplayInfo(info.AppVersion, info.Smapi));
        games.RemoveAllViews();
        foreach (var game in info.Games)
            games.AddView(CreateGameView(game));
        gamesEmpty.Text = GetString(info.PackageReadStatus switch
        {
            EnvironmentPackageReadStatus.Complete => Resource.String.environment_no_installed_game,
            EnvironmentPackageReadStatus.Partial => Resource.String.environment_read_partial,
            EnvironmentPackageReadStatus.Failed => Resource.String.environment_read_failed,
            _ => throw new InvalidOperationException("The package read status is invalid."),
        });
        gamesEmpty.Visibility = info.PackageReadStatus == EnvironmentPackageReadStatus.Complete && info.Games.Count > 0
            ? ViewStates.Gone
            : ViewStates.Visible;
    }

    private void RenderReadFailure()
    {
        if (appVersion is not null && appVersion.Text == GetString(Resource.String.environment_loading))
        {
            appVersion.Text = FormatString(
                Resource.String.environment_app_version_value,
                new JString("—"));
        }
        if (smapi is not null && smapi.Text == GetString(Resource.String.environment_loading))
        {
            smapi.Text = FormatString(
                Resource.String.environment_smapi_value,
                new JString("—"));
        }
        games?.RemoveAllViews();
        if (gamesEmpty is not null)
        {
            gamesEmpty.Text = GetString(Resource.String.environment_read_failed);
            gamesEmpty.Visibility = ViewStates.Visible;
        }
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
        if (game.Icon is not null)
            icon?.SetImageDrawable(game.Icon);
        return view;
    }

    private static bool IsRecoverable(Exception exception) => exception is not (
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException);

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The environment string resource is unavailable.");
}
