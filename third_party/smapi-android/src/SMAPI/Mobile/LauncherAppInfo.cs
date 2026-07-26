using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace StardewModdingAPI.Mobile;

internal static class LauncherAppInfo
{

    public static PackageInfo GetPackageInfo(string PackageName)
    {
        try
        {
            var ctx = Application.Context;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                return ctx.PackageManager.GetPackageInfo(PackageName, PackageManager.PackageInfoFlags.Of(PackageInfoFlagsLong.None));
            else
                return ctx.PackageManager.GetPackageInfo(PackageName, 0);
        }
        catch (Exception e)
        {
            return null;
        }
    }

    public static PackageInfo MyPackageInfo = GetPackageInfo(Application.Context.PackageName);
    // Android version names may contain a prerelease suffix (for example `0.1.0-dev`),
    // which System.Version rejects. SMAPI only formats this value in its startup log, so
    // preserve the package-provided string instead of introducing a launch-time parse failure.
    public static string CurrentVersion => MyPackageInfo.VersionName ?? "unknown";
    public static int CurrentBuild => MyPackageInfo.VersionCode;
}
