using Android.Content;
using StardewModdingAPI.Mobile;

namespace JunimoGate.GameHost;

public sealed record GameHostRuntimeInformation(
    string SmapiApiVersion,
    string SmapiImplementationVersion,
    string BuildId,
    string BundleId,
    int BundleFileCount);

public static class GameHostRuntimeInformationReader
{
    public static GameHostRuntimeInformation Read(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bundle = BundledSmapiAssets.ReadPackagedInfo(context);
        return new GameHostRuntimeInformation(
            SMAPIAndroidBuild.ApiVersion,
            SMAPIAndroidBuild.ImplementationVersion,
            GameHostRuntimeIdentity.BuildId,
            bundle.BundleId,
            bundle.FileCount);
    }
}
