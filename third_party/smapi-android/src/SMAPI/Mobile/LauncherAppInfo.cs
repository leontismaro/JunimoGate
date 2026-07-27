using System;
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace StardewModdingAPI.Mobile;

internal static class LauncherAppInfo
{

    public static PackageInfo? GetPackageInfo(string packageName)
    {
        try
        {
            var ctx = Application.Context;
            var packageManager = ctx.PackageManager ?? throw new InvalidOperationException("Android PackageManager is unavailable.");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                return packageManager.GetPackageInfo(packageName, PackageManager.PackageInfoFlags.Of(PackageInfoFlagsLong.None));
            else
                return packageManager.GetPackageInfo(packageName, 0);
        }
        catch (PackageManager.NameNotFoundException)
        {
            return null;
        }
    }

    public static PackageInfo MyPackageInfo { get; } = GetPackageInfo(
        Application.Context.PackageName ?? throw new InvalidOperationException("The launcher package name is unavailable."))
        ?? throw new InvalidOperationException("The launcher package metadata is unavailable.");
    // Android version names may contain a prerelease suffix (for example `0.1.0-dev`),
    // which System.Version rejects. SMAPI only formats this value in its startup log, so
    // preserve the package-provided string instead of introducing a launch-time parse failure.
    public static string CurrentVersion => MyPackageInfo.VersionName ?? "unknown";
    public static long CurrentBuild
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
                return MyPackageInfo.LongVersionCode;
            return MyPackageInfo.VersionCode;
        }
    }
}
