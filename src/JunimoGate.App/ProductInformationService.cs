using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.GameHost;
using JunimoGate.Mods;
using System.Xml;
using Log = JunimoGate.Android.JunimoGateLog;

namespace JunimoGate.App;

internal enum InstalledGameStatus
{
    Supported,
    DetectedUnsupported,
    Unrecognized,
}

internal sealed record InstalledGameDisplayInfo(
    string PackageName,
    string DisplayName,
    string? VersionName,
    long? VersionCode,
    InstalledGameStatus Status,
    Drawable? Icon);

internal sealed record EnvironmentDisplayInfo(
    string AppVersion,
    IReadOnlyList<InstalledGameDisplayInfo> Games,
    GameHostRuntimeInformation Smapi,
    EnvironmentPackageReadStatus PackageReadStatus);

internal sealed record LocalEnvironmentDisplayInfo(
    string AppVersion,
    GameHostRuntimeInformation Smapi);

internal sealed record HomeSummary(
    int EnabledModCount,
    string? LatestSaveName,
    DateTimeOffset? LatestSaveTimeUtc,
    TimeSpan TotalPlayTime);

internal sealed class ProductInformationService
{
    private static readonly BoundedRetirableTaskGate EnvironmentPackageReadGate = new(2);
    private readonly Context context;
    private readonly AndroidInstalledPackageSummaryReader packageSummaryReader;
    private readonly EnvironmentPackageReadService environmentPackageReader;

    public ProductInformationService(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context.ApplicationContext ?? context;
        packageSummaryReader = new AndroidInstalledPackageSummaryReader(this.context);
        environmentPackageReader = new EnvironmentPackageReadService(
            packageSummaryReader,
            new ProductEnvironmentPackageReadLog(),
            [
                ("play", AndroidPlatformBoundary.PlayPackageName),
                ("galaxy", AndroidPlatformBoundary.SamsungPackageName),
            ]);
    }

    public Task<LocalEnvironmentDisplayInfo> ReadLocalEnvironmentAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => new LocalEnvironmentDisplayInfo(
                ReadAppVersion(),
                GameHostRuntimeInformationReader.Read(context)),
            cancellationToken);

    public async ValueTask<EnvironmentDisplayInfo> ReadEnvironmentAsync(
        long generation,
        LocalEnvironmentDisplayInfo local,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(local);
        return await EnvironmentPackageReadGate.RunAsync(
            token => ReadEnvironment(generation, local, token),
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private EnvironmentDisplayInfo ReadEnvironment(
        long generation,
        LocalEnvironmentDisplayInfo local,
        CancellationToken cancellationToken)
    {
        var packageResult = environmentPackageReader.Read(generation, cancellationToken);
        var games = new List<InstalledGameDisplayInfo>(2);
        foreach (var result in packageResult.Packages)
        {
            if (result.Status != InstalledPackageSummaryStatus.Found || result.Summary is not { } summary)
                continue;

            var certificate = summary.SigningIdentity is null
                ? null
                : KnownGameCertificate.Verify(summary.PackageName, summary.SigningIdentity);
            var selectable = summary.PackageName == AndroidPlatformBoundary.PlayPackageName &&
                             certificate?.AllowsCodeExecution == true;
            games.Add(new InstalledGameDisplayInfo(
                summary.PackageName,
                summary.DisplayName,
                summary.VersionName,
                summary.VersionCode,
                Status: selectable
                    ? InstalledGameStatus.Supported
                    : certificate?.Status == GameCertificateStatus.Unrecognized
                        ? InstalledGameStatus.Unrecognized
                        : InstalledGameStatus.DetectedUnsupported,
                packageSummaryReader.GetIcon(summary.PackageName)));
        }

        return new EnvironmentDisplayInfo(
            local.AppVersion,
            games,
            local.Smapi,
            packageResult.Status);
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
        var latest = FindLatestSave(savesRoot);
        var playSummary = new GamePlaySessionRepository(Path.Combine(userData, "sessions"))
            .ReadSummaryAsync(GameSessionRegistry.IsGameProcessActive(context))
            .AsTask().GetAwaiter().GetResult();
        return new HomeSummary(
            enabledMods,
            latest?.DisplayName,
            latest?.LastWriteTimeUtc,
            playSummary.TotalPlayTime);
    }

    private static HomeSaveSummary? FindLatestSave(string savesRoot)
    {
        if (!Directory.Exists(savesRoot))
            return null;
        DirectoryInfo? latestDirectory = null;
        DateTimeOffset latestWriteTime = DateTimeOffset.MinValue;
        foreach (var path in Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var directory = new DirectoryInfo(path);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.Name.StartsWith(".", StringComparison.Ordinal))
                    continue;
                var writeTime = GetSaveWriteTime(directory);
                if (latestDirectory is not null && writeTime <= latestWriteTime)
                    continue;
                latestDirectory = directory;
                latestWriteTime = writeTime;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A save can be replaced while the home summary is refreshing; skip only that entry.
            }
        }
        return latestDirectory is null
            ? null
            : new HomeSaveSummary(ReadSaveDisplayName(latestDirectory), latestWriteTime);
    }

    private static DateTimeOffset GetSaveWriteTime(DirectoryInfo directory)
    {
        var latest = directory.LastWriteTimeUtc;
        foreach (var path in new[]
                 {
                     Path.Combine(directory.FullName, "SaveGameInfo"),
                     Path.Combine(directory.FullName, directory.Name),
                 })
        {
            if (!File.Exists(path))
                continue;
            var writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime > latest)
                latest = writeTime;
        }
        return new DateTimeOffset(latest, TimeSpan.Zero);
    }

    private static string ReadSaveDisplayName(DirectoryInfo directory)
    {
        var infoPath = Path.Combine(directory.FullName, "SaveGameInfo");
        if (File.Exists(infoPath))
        {
            try
            {
                using var stream = new FileStream(infoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (SaveGameMetadataReader.Read(stream).FarmName is { Length: > 0 } farmName)
                    return farmName;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
            {
                // Keep the home summary useful when a save's optional metadata can't be read.
            }
        }
        var separator = directory.Name.LastIndexOf('_');
        return separator > 0 && separator < directory.Name.Length - 1 &&
               directory.Name.AsSpan(separator + 1).IndexOfAnyExceptInRange('0', '9') < 0
            ? directory.Name[..separator]
            : directory.Name;
    }

    private sealed record HomeSaveSummary(string DisplayName, DateTimeOffset LastWriteTimeUtc);

    private string ReadAppVersion()
    {
        var manager = context.PackageManager
            ?? throw new InvalidOperationException("Android PackageManager is unavailable.");
        var package = manager.GetPackageInfo(context.PackageName!, (PackageInfoFlags)0)
            ?? throw new InvalidOperationException("JunimoGate package metadata is unavailable.");
        return package.VersionName ?? "—";
    }

    private sealed class ProductEnvironmentPackageReadLog : IEnvironmentPackageReadLog
    {
        private const string Tag = "JunimoGate.Environment";

        public void Info(string message) => Log.Info(Tag, message);

        public void Warn(string message) => Log.Warn(Tag, message);
    }
}
