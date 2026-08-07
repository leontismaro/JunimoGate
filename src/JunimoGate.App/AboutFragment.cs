using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using JunimoGate.Android;
using JunimoGate.Mods;
using Fragment = AndroidX.Fragment.App.Fragment;
using Log = JunimoGate.Android.JunimoGateLog;
using JObject = Java.Lang.Object;
using JString = Java.Lang.String;
using OperationCanceledException = System.OperationCanceledException;

namespace JunimoGate.App;

[Register("org.junimogate.app.AboutFragment")]
public sealed class AboutFragment : Fragment
{
    private const string RepositoryUrl = "https://github.com/leontismaro/JunimoGate";
    private static readonly LicenseDocument[] LicenseDocuments =
    [
        new(Resource.String.about_license_junimogate, "licenses/JunimoGate-GPL-3.0-only.txt"),
        new(Resource.String.about_license_exception, "licenses/JunimoGate-MonoGame-Linking-Exception.txt"),
        new(Resource.String.about_license_smapi, "licenses/JunimoGate-SMAPI-LGPL-3.0-only.txt"),
        new(Resource.String.about_license_third_party, "THIRD-PARTY-NOTICES.md"),
        new(Resource.String.about_license_markdig, "licenses/Markdig-BSD-2-Clause.txt"),
        new(Resource.String.about_license_monogame, "licenses/MonoGame-f5d8bf.txt"),
        new(Resource.String.about_license_openal, "licenses/OpenAL-Soft-1.24.3-COPYING.txt"),
        new(Resource.String.about_license_openal_bsd, "licenses/OpenAL-Soft-1.24.3-BSD-3-Clause.txt"),
        new(Resource.String.about_license_stb, "licenses/StbSharp-PUBLIC-DOMAIN.txt"),
        new(Resource.String.about_license_dotnet, "licenses/DotNet-Runtime-MIT.txt"),
        new(Resource.String.about_license_dotnet_third_party, "licenses/DotNet-Runtime-THIRD-PARTY-NOTICES.txt"),
        new(Resource.String.about_license_skiasharp, "licenses/SkiaSharp-MIT.txt"),
        new(Resource.String.about_license_skiasharp_third_party, "licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt"),
        new(Resource.String.about_license_android, "licenses/AndroidX-Bindings-MIT.txt"),
        new(Resource.String.about_license_android_third_party, "licenses/AndroidX-Apache-2.0.txt"),
    ];
    private TextView? versions;
    private MaterialButton? updateButton;
    private MaterialButton? repositoryButton;
    private MaterialButton? noticesButton;
    private LinearProgressIndicator? progress;
    private CancellationTokenSource? cancellation;
    private LauncherSettingsRepository? settings;
    private string appVersion = "0.0.0-dev";

    public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) =>
        inflater.Inflate(Resource.Layout.fragment_about, container, false)
        ?? throw new InvalidOperationException("The About layout could not be created.");

    public override void OnViewCreated(View view, Bundle? savedInstanceState)
    {
        base.OnViewCreated(view, savedInstanceState);
        versions = view.FindViewById<TextView>(Resource.Id.about_versions)
            ?? throw new InvalidOperationException("The About version view is unavailable.");
        progress = view.FindViewById<LinearProgressIndicator>(Resource.Id.about_progress)
            ?? throw new InvalidOperationException("The About progress view is unavailable.");
        updateButton = view.FindViewById<MaterialButton>(Resource.Id.about_check_update)
            ?? throw new InvalidOperationException("The update action is unavailable.");
        repositoryButton = view.FindViewById<MaterialButton>(Resource.Id.about_repository)
            ?? throw new InvalidOperationException("The repository action is unavailable.");
        noticesButton = view.FindViewById<MaterialButton>(Resource.Id.about_notices)
            ?? throw new InvalidOperationException("The notices action is unavailable.");
        updateButton.Click += OnUpdateClicked;
        repositoryButton.Click += OnRepositoryClicked;
        noticesButton.Click += OnNoticesClicked;
    }

    public override void OnStart()
    {
        base.OnStart();
        cancellation = new CancellationTokenSource();
        settings = new LauncherSettingsRepository(Path.Combine(
            AndroidPrivateStorage.GetUserDataRoot(RequireContext()),
            "settings"));
        LoadVersion();
    }

    public override void OnStop()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        settings = null;
        base.OnStop();
    }

    public override void OnDestroyView()
    {
        if (updateButton is not null)
            updateButton.Click -= OnUpdateClicked;
        if (repositoryButton is not null)
            repositoryButton.Click -= OnRepositoryClicked;
        if (noticesButton is not null)
            noticesButton.Click -= OnNoticesClicked;
        versions = null;
        updateButton = null;
        repositoryButton = null;
        noticesButton = null;
        progress = null;
        base.OnDestroyView();
    }

    private void LoadVersion()
    {
        try
        {
            var context = RequireContext();
            var package = context.PackageManager?.GetPackageInfo(
                              context.PackageName!,
                              (global::Android.Content.PM.PackageInfoFlags)0)
                ?? throw new InvalidOperationException("JunimoGate package metadata is unavailable.");
            appVersion = package.VersionName ?? "—";
            if (versions is not null)
                versions.Text = FormatString(Resource.String.about_version_value, new JString(appVersion));
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          global::Android.Content.PM.PackageManager.NameNotFoundException)
        {
            Log.Error("JunimoGate.About", "about-read-failed", exception);
            versions?.SetText(Resource.String.about_read_failed);
        }
    }

    private void OnUpdateClicked(object? sender, EventArgs eventArgs)
    {
        if (cancellation is { IsCancellationRequested: false } lifetime)
            _ = CheckForUpdatesAsync(lifetime.Token);
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        SetChecking(true);
        try
        {
            var result = await new GitHubUpdateService().CheckAsync(appVersion, cancellationToken).ConfigureAwait(false);
            await RecordUpdateCheckAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAdded || cancellationToken.IsCancellationRequested)
                return;
            Activity?.RunOnUiThread(() => ShowUpdateResult(result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          IOException or InvalidDataException or UnauthorizedAccessException or
                                          InvalidOperationException)
        {
            Log.Warn("JunimoGate.Update", "manual-update-check-failed", exception);
            try
            {
                await RecordUpdateCheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception recordException) when (recordException is IOException or InvalidDataException or
                                                    UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Warn("JunimoGate.Update", "update-check-time-save-failed", recordException);
            }
            if (IsAdded)
                Activity?.RunOnUiThread(() => Toast.MakeText(RequireContext(), Resource.String.about_update_failed, ToastLength.Long)?.Show());
        }
        finally
        {
            if (IsAdded)
                Activity?.RunOnUiThread(() => SetChecking(false));
        }
    }

    private async ValueTask RecordUpdateCheckAsync(CancellationToken cancellationToken)
    {
        if (settings is null)
            return;
        var current = await settings.ReadAsync(cancellationToken).ConfigureAwait(false);
        _ = await settings.UpdateAsync(
                current.Revision,
                value => value with { LastUpdateCheckUtc = DateTimeOffset.UtcNow },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void ShowUpdateResult(UpdateCheckResult result)
    {
        if (result.Status == UpdateCheckStatus.UpdateAvailable && result.ReleaseUrl is not null)
        {
            var dialog = new MaterialAlertDialogBuilder(RequireContext());
            dialog.SetTitle(Resource.String.about_update_available_title);
            dialog.SetMessage(FormatString(
                Resource.String.about_update_available_message,
                new JString(result.ReleaseName ?? result.LatestVersion ?? "—")));
            dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
            dialog.SetPositiveButton(Resource.String.about_open_release, (_, _) => OpenUrl(result.ReleaseUrl));
            dialog.Show();
            return;
        }
        Toast.MakeText(
            RequireContext(),
            result.Status == UpdateCheckStatus.NoStableRelease
                ? Resource.String.about_no_stable_release
                : Resource.String.about_up_to_date,
            ToastLength.Long)?.Show();
    }

    private void OnRepositoryClicked(object? sender, EventArgs eventArgs) => OpenUrl(RepositoryUrl);

    private void OnNoticesClicked(object? sender, EventArgs eventArgs)
    {
        var labels = LicenseDocuments
            .Select(document => GetString(document.TitleResourceId) ?? document.AssetPath)
            .ToArray();
        var dialog = new MaterialAlertDialogBuilder(RequireContext());
        dialog.SetTitle(Resource.String.about_notices);
        dialog.SetItems(labels, (_, eventArgs) => ShowLicense(LicenseDocuments[eventArgs.Which]));
        dialog.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        dialog.Show();
    }

    private void ShowLicense(LicenseDocument document)
    {
        try
        {
            using var stream = RequireContext().Assets?.Open(document.AssetPath)
                ?? throw new IOException("The selected license asset is missing.");
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var dialog = new MaterialAlertDialogBuilder(RequireContext());
            dialog.SetTitle(document.TitleResourceId);
            dialog.SetMessage(text);
            dialog.SetPositiveButton(global::Android.Resource.String.Ok, (_, _) => { });
            dialog.Show();
        }
        catch (IOException exception)
        {
            Log.Error("JunimoGate.About", "notices-read-failed", exception);
            Toast.MakeText(RequireContext(), Resource.String.about_notices_failed, ToastLength.Long)?.Show();
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)));
        }
        catch (ActivityNotFoundException exception)
        {
            Log.Warn("JunimoGate.About", "browser-unavailable", exception);
            Toast.MakeText(RequireContext(), Resource.String.about_browser_unavailable, ToastLength.Long)?.Show();
        }
    }

    private void SetChecking(bool value)
    {
        if (progress is not null)
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        if (updateButton is not null)
            updateButton.Enabled = !value;
    }

    private string FormatString(int resourceId, params JObject[] arguments) =>
        Resources?.GetString(resourceId, arguments)
        ?? throw new InvalidOperationException("The About string resource is unavailable.");

    private sealed record LicenseDocument(int TitleResourceId, string AssetPath);
}
