using Android.Content;
using Android.Content.PM;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.GameHost;
using JunimoGate.Mods;

namespace JunimoGate.App;

internal sealed record InstalledGameDisplayInfo(
    string PackageName,
    string StoreName,
    bool IsInstalled,
    bool IsSelectable,
    string? VersionName,
    long? VersionCode,
    string Status);

internal sealed record EnvironmentDisplayInfo(
    string AppVersion,
    IReadOnlyList<InstalledGameDisplayInfo> Games,
    GameHostRuntimeInformation Smapi);

internal sealed record HomeSummary(
    int EnabledModCount,
    string? LatestSaveName,
    DateTimeOffset? LatestSaveTimeUtc);

internal sealed class ProductInformationService
{
    private readonly Context context;

    public ProductInformationService(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
    }

    public async ValueTask<EnvironmentDisplayInfo> ReadEnvironmentAsync(CancellationToken cancellationToken)
    {
        var provider = new AndroidPackageInstallationSnapshotProvider(context);
        var games = new List<InstalledGameDisplayInfo>(2);
        foreach (var candidate in new[]
                 {
                     (AndroidPlatformBoundary.PlayPackageName, "Google Play"),
                     (AndroidPlatformBoundary.SamsungPackageName, "Galaxy Store"),
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await provider.GetSnapshotAsync(candidate.Item1, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                games.Add(new InstalledGameDisplayInfo(
                    candidate.Item1,
                    candidate.Item2,
                    IsInstalled: false,
                    IsSelectable: false,
                    VersionName: null,
                    VersionCode: null,
                    Status: "not-installed"));
                continue;
            }

            var certificate = snapshot.SigningIdentity is null
                ? null
                : KnownGameCertificate.Verify(candidate.Item1, snapshot.SigningIdentity);
            var selectable = candidate.Item1 == AndroidPlatformBoundary.PlayPackageName &&
                             certificate?.AllowsCodeExecution == true;
            games.Add(new InstalledGameDisplayInfo(
                candidate.Item1,
                candidate.Item2,
                IsInstalled: true,
                IsSelectable: selectable,
                snapshot.VersionName,
                snapshot.LongVersionCode,
                Status: selectable
                    ? "supported"
                    : certificate?.Status == GameCertificateStatus.Unrecognized
                        ? "unrecognized"
                        : "detected-unsupported"));
        }

        return new EnvironmentDisplayInfo(ReadAppVersion(), games, GameHostRuntimeInformationReader.Read(context));
    }

    public HomeSummary ReadHomeSummary()
    {
        var profile = new ProfileLayout(
            Path.Combine(AndroidPrivateStorage.GetUserDataRoot(context), "profiles"),
            ProfileId.Parse("default"));
        var enabledMods = Directory.Exists(profile.EnabledDirectory)
            ? Directory.EnumerateFiles(profile.EnabledDirectory, "manifest.json", SearchOption.AllDirectories).Count()
            : 0;

        var savesRoot = AndroidPrivateStorage.GetGameSaveRoot(context);
        var latest = Directory.Exists(savesRoot)
            ? Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(static directory => directory.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        return new HomeSummary(
            enabledMods,
            latest?.Name,
            latest is null ? null : new DateTimeOffset(latest.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private string ReadAppVersion()
    {
        var manager = context.PackageManager
            ?? throw new InvalidOperationException("Android PackageManager is unavailable.");
        var package = manager.GetPackageInfo(context.PackageName!, (PackageInfoFlags)0)
            ?? throw new InvalidOperationException("JunimoGate package metadata is unavailable.");
        return package.VersionName ?? "unknown";
    }
}
