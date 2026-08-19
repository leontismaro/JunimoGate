using Android.Content;
using Android.Content.PM;
using JunimoGate.Android;
using JunimoGate.Core;
using JunimoGate.GameHost;
using JunimoGate.Mods;
using System.Xml;

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
    InstalledGameStatus Status);

internal sealed record EnvironmentDisplayInfo(
    string AppVersion,
    IReadOnlyList<InstalledGameDisplayInfo> Games,
    GameHostRuntimeInformation Smapi);

internal sealed record HomeSummary(
    int EnabledModCount,
    string? LatestSaveName,
    DateTimeOffset? LatestSaveTimeUtc,
    TimeSpan TotalPlayTime);

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
        foreach (var packageName in new[]
                 {
                     AndroidPlatformBoundary.PlayPackageName,
                     AndroidPlatformBoundary.SamsungPackageName,
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await provider.GetSnapshotAsync(packageName, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
                continue;

            var certificate = snapshot.SigningIdentity is null
                ? null
                : KnownGameCertificate.Verify(packageName, snapshot.SigningIdentity);
            var selectable = packageName == AndroidPlatformBoundary.PlayPackageName &&
                             certificate?.AllowsCodeExecution == true;
            games.Add(new InstalledGameDisplayInfo(
                packageName,
                ReadApplicationLabel(packageName),
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

    private string ReadApplicationLabel(string packageName)
    {
        var manager = context.PackageManager
            ?? throw new InvalidOperationException("Android PackageManager is unavailable.");
        try
        {
            var application = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? manager.GetApplicationInfo(
                    packageName,
                    PackageManager.ApplicationInfoFlags.Of(0L))
                : manager.GetApplicationInfo(packageName, (PackageInfoFlags)0);
            return application?.LoadLabel(manager)?.ToString() is { Length: > 0 } label
                ? label
                : "Stardew Valley";
        }
        catch (PackageManager.NameNotFoundException)
        {
            return "Stardew Valley";
        }
    }

    public HomeSummary ReadHomeSummary(CancellationToken cancellationToken = default)
    {
        var userData = AndroidPrivateStorage.GetUserDataRoot(context);
        var profilesRoot = Path.Combine(userData, "profiles");
        var libraryRepository = new ModLibraryRepository(Path.Combine(userData, "mods"));
        var profileRepository = new ModProfileV2Repository(profilesRoot);
        var active = new ActiveModProfileSelectionRepository(profilesRoot)
            .OpenOrCreateAsync(ProfileId.Parse("default"), cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        var activeId = active.Validate();
        var selected = profileRepository.ReadAsync(activeId, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        var library = libraryRepository.ReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
        var available = library.Items.Select(static item => item.LibraryItemId).ToHashSet(StringComparer.Ordinal);
        var enabledMods = selected.Members.Count(member =>
            member.Enabled && member.LibraryItemId is not null && available.Contains(member.LibraryItemId));

        var savesRoot = AndroidPrivateStorage.GetGameSaveRoot(context);
        var latest = FindLatestSave(savesRoot);
        var playSummary = new GamePlaySessionRepository(Path.Combine(userData, "sessions"))
            .ReadSummaryAsync(
                GameSessionRegistry.IsGameProcessActive(context),
                cancellationToken: cancellationToken)
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
}
