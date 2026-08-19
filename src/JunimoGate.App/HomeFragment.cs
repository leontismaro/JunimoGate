using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using AndroidX.Navigation.Fragment;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Android;
using AndroidDateUtils = Android.Text.Format.DateUtils;
using AndroidFormatStyleFlags = Android.Text.Format.FormatStyleFlags;
using Fragment = AndroidX.Fragment.App.Fragment;
using JInteger = Java.Lang.Integer;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.HomeFragment")]
public sealed class HomeFragment : Fragment
{
    private ILauncherUiHost? host;
    private TextView? statusText;
    private LinearProgressIndicator? progress;
    private ImageView? gameIcon;
    private TextView? playTimeText;
    private View? lastSaveCard;
    private TextView? lastSaveText;
    private TextView? lastSaveTimeText;
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
        gameIcon = view.FindViewById<ImageView>(Resource.Id.home_game_icon);
        playTimeText = view.FindViewById<TextView>(Resource.Id.home_play_time);
        lastSaveCard = view.FindViewById(Resource.Id.home_last_save_card);
        lastSaveText = view.FindViewById<TextView>(Resource.Id.home_last_save);
        lastSaveTimeText = view.FindViewById<TextView>(Resource.Id.home_last_save_time);
        modCountText = view.FindViewById<TextView>(Resource.Id.home_mod_count);
        if (lastSaveCard is not null)
            lastSaveCard.Click += OnLastSaveClicked;
        TrySetInstalledGameIcon();
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
        if (lastSaveCard is not null)
            lastSaveCard.Click -= OnLastSaveClicked;
        statusText = null;
        progress = null;
        gameIcon = null;
        playTimeText = null;
        lastSaveCard = null;
        lastSaveText = null;
        lastSaveTimeText = null;
        modCountText = null;
        base.OnDestroyView();
    }

    private void OnStateChanged(LauncherState state) => Render(state);

    private async Task LoadSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ((MainActivity)RequireActivity()).EnsureModProfilesReadyAsync(cancellationToken)
                .ConfigureAwait(false);
            var summary = await Task.Run(
                () => new ProductInformationService(RequireContext()).ReadHomeSummary(cancellationToken),
                cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Activity?.RunOnUiThread(() =>
                {
                    if (playTimeText is not null)
                        playTimeText.Text = FormatPlayTime(summary.TotalPlayTime);
                    if (lastSaveText is not null)
                        lastSaveText.Text = summary.LatestSaveName ?? GetString(Resource.String.last_save_empty);
                    if (lastSaveTimeText is not null)
                        lastSaveTimeText.Text = summary.LatestSaveTimeUtc is null
                            ? string.Empty
                            : FormatRelativeTime(summary.LatestSaveTimeUtc.Value);
                    if (modCountText is not null)
                    {
                        modCountText.Text = FormatQuantity(
                            Resource.Plurals.enabled_mods_count,
                            summary.EnabledModCount);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leaving the screen cancels the low-cost summary refresh.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            global::Android.Util.Log.Warn("JunimoGate.Home", $"summary-unavailable:{exception.GetType().Name}");
        }
    }

    private void Render(LauncherState state)
    {
        if (statusText is null || progress is null)
            return;
        statusText.Text = LauncherTextFormatter.Format(RequireContext(), state);
        progress.Visibility = state.ShowProgress ? ViewStates.Visible : ViewStates.Gone;
    }

    private void OnLastSaveClicked(object? sender, EventArgs eventArgs) =>
        NavHostFragment.FindNavController(this).Navigate(Resource.Id.navigation_save_backups);

    private void TrySetInstalledGameIcon()
    {
        if (gameIcon is null)
            return;
        var manager = RequireContext().PackageManager;
        if (manager is null)
            return;
        foreach (var packageName in new[]
                 {
                     AndroidPlatformBoundary.PlayPackageName,
                     AndroidPlatformBoundary.SamsungPackageName,
                 })
        {
            try
            {
                var application = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? manager.GetApplicationInfo(
                        packageName,
                        PackageManager.ApplicationInfoFlags.Of(0L))
                    : manager.GetApplicationInfo(packageName, (PackageInfoFlags)0);
                var drawable = application?.LoadIcon(manager);
                if (drawable is null)
                    continue;
                gameIcon.SetImageDrawable(drawable);
                return;
            }
            catch (PackageManager.NameNotFoundException)
            {
                // Try the other supported store package; the fallback game icon remains otherwise.
            }
        }
    }

    private static string FormatRelativeTime(DateTimeOffset value) =>
        AndroidDateUtils.GetRelativeTimeSpanString(
                value.ToUnixTimeMilliseconds(),
                DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                AndroidDateUtils.MinuteInMillis,
                AndroidFormatStyleFlags.AbbrevRelative)
            ?.ToString()
        ?? "—";

    private string FormatQuantity(int resourceId, int quantity) =>
        Resources?.GetQuantityString(resourceId, quantity, [JInteger.ValueOf(quantity)])
        ?? throw new InvalidOperationException("The home quantity resource is unavailable.");

    private string FormatPlayTime(TimeSpan duration)
    {
        var totalMinutes = Math.Max(0, (int)Math.Floor(duration.TotalMinutes));
        if (totalMinutes < 60)
            return FormatQuantity(Resource.Plurals.play_time_minutes, totalMinutes);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return Resources?.GetQuantityString(
            Resource.Plurals.play_time_hours,
            hours,
            [JInteger.ValueOf(hours), JInteger.ValueOf(minutes)])
            ?? throw new InvalidOperationException("The play-time resource is unavailable.");
    }

}
