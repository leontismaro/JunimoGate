using Android.Content;
using Android.Content.PM;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.GameHost;
using JunimoGate.Mods;

namespace JunimoGate.App;

internal enum GameStore
{
    GooglePlay,
    GalaxyStore,
}

internal enum InstalledGameStatus
{
    NotInstalled,
    Supported,
    DetectedUnsupported,
    Unrecognized,
}

internal sealed record InstalledGameDisplayInfo(
    string PackageName,
    GameStore Store,
    bool IsSelectable,
    string? VersionName,
    long? VersionCode,
    InstalledGameStatus Status)
{
    public bool IsInstalled => Status != InstalledGameStatus.NotInstalled;
}

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
                     (AndroidPlatformBoundary.PlayPackageName, GameStore.GooglePlay),
                     (AndroidPlatformBoundary.SamsungPackageName, GameStore.GalaxyStore),
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await provider.GetSnapshotAsync(candidate.Item1, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                games.Add(new InstalledGameDisplayInfo(
                    candidate.Item1,
                    candidate.Item2,
                    IsSelectable: false,
                    VersionName: null,
                    VersionCode: null,
                    Status: InstalledGameStatus.NotInstalled));
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
                IsSelectable: selectable,
                snapshot.VersionName,
                snapshot.LongVersionCode,
                Status: selectable
                    ? InstalledGameStatus.Supported
                    : certificate?.Status == GameCertificateStatus.Unrecognized
                        ? InstalledGameStatus.Unrecognized
                        : InstalledGameStatus.DetectedUnsupported));
        }

        return new EnvironmentDisplayInfo(ReadAppVersion(), games, GameHostRuntimeInformationReader.Read(context));
    }

    public HomeSummary ReadHomeSummary()
    {
        var userData = AndroidPrivateStorage.GetUserDataRoot(context);
        var profilesRoot = Path.Combine(userData, "profiles");
        var active = new ActiveModProfileSelectionRepository(profilesRoot)
            .OpenOrCreateAsync(ProfileId.Parse("default"))
            .AsTask().GetAwaiter().GetResult();
        var activeId = active.Validate();
        int enabledMods;
        try
        {
            var selected = new ModProfileV2Repository(profilesRoot)
                .ReadAsync(activeId).AsTask().GetAwaiter().GetResult();
            var library = new ModLibraryRepository(Path.Combine(userData, "mods"))
                .ReadAsync().AsTask().GetAwaiter().GetResult();
            var available = library.Items.Select(static item => item.LibraryItemId).ToHashSet(StringComparer.Ordinal);
            enabledMods = selected.Members.Count(member =>
                member.Enabled && member.LibraryItemId is not null && available.Contains(member.LibraryItemId));
        }
        catch (InvalidDataException) when (activeId.Value == "default")
        {
            var legacy = new ProfileLayout(profilesRoot, activeId);
            enabledMods = Directory.Exists(legacy.EnabledDirectory)
                ? Directory.EnumerateFiles(legacy.EnabledDirectory, "manifest.json", SearchOption.AllDirectories).Count()
                : 0;
        }

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
        return package.VersionName ?? "—";
    }
}
