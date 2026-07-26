using Android.App;
using Android.Content.PM;
using Android.Util;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;

namespace JunimoGate.GameHost;

/// <summary>
/// Narrow Android capabilities exposed to an exact rewritten game recipe. The bridge is unusable
/// until a catalog-approved recipe and a freshly validated installed-source plan are attached.
/// </summary>
public static class GameHostBridge
{
    private const string LogTag = "JunimoGate.GameHost";

    private static readonly object SessionLock = new();

    private static readonly string[] RequiredNormalPermissions =
    [
        global::Android.Manifest.Permission.AccessNetworkState,
        global::Android.Manifest.Permission.AccessWifiState,
        global::Android.Manifest.Permission.Internet,
        global::Android.Manifest.Permission.Vibrate,
    ];

    private static BridgeSession? session;

    /// <summary>Returns the real Android framework-created host Activity.</summary>
    public static Activity GetActivity() => GetSession().Activity;

    /// <summary>Checks the four normal permissions expected by the tested game build.</summary>
    public static bool HasPermissions()
    {
        var activity = GetSession().Activity;
        return RequiredNormalPermissions.All(permission =>
            activity.CheckSelfPermission(permission) == Permission.Granted);
    }

    /// <summary>
    /// Normal permissions are granted at install time. Continue immediately when present and fail
    /// closed rather than invoking the original package-specific permission UI when they are not.
    /// </summary>
    public static void PromptForPermissionsIfNecessary(Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        if (!HasPermissions())
        {
            throw new InvalidOperationException("The GameHost package is missing a required normal Android permission.");
        }

        continuation();
    }

    /// <summary>Logs only host-package permission states; no path or game payload data is emitted.</summary>
    public static void LogPermissions()
    {
        var activity = GetSession().Activity;
        foreach (var permission in RequiredNormalPermissions)
        {
            var state = activity.CheckSelfPermission(permission) == Permission.Granted ? "granted" : "denied";
            Log.Info(LogTag, $"permission {permission}: {state}");
        }
    }

    /// <summary>Shows a host-owned fallback when the game detects insufficient save storage.</summary>
    public static void ShowDiskFullDialogue()
    {
        var activity = GetSession().Activity;
        activity.RunOnUiThread(() =>
        {
            if (activity.IsFinishing || activity.IsDestroyed)
            {
                return;
            }

            var builder = new AlertDialog.Builder(activity);
            builder.SetTitle("Storage unavailable");
            builder.SetMessage("The game could not write its save data. Free storage space and try again.");
            builder.SetCancelable(false);
            builder.SetPositiveButton("OK", (_, _) => { });
            var dialog = builder.Create() ?? throw new InvalidOperationException("Could not create the storage warning dialog.");
            dialog.Show();
        });
    }

    /// <summary>Returns the versionCode bound into the fresh trusted installed-source plan.</summary>
    public static int GetBuild()
    {
        var versionCode = GetSession().VersionCode;
        if (versionCode is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidOperationException("The trusted game versionCode does not fit the tested game API.");
        }

        return checked((int)versionCode);
    }

    /// <summary>
    /// Hosted workspaces are app-private. Legacy public-folder migration is intentionally disabled;
    /// this is separate from original Play licensing and does not copy public saves automatically.
    /// </summary>
    public static bool CheckStorageMigration()
    {
        _ = GetSession();
        Log.Info(LogTag, "Legacy public-folder storage migration is disabled for the hosted workspace.");
        return false;
    }

    /// <summary>The host never launches the original storage-migration Activity flow.</summary>
    public static bool IsDoingStorageMigration()
    {
        _ = GetSession();
        return false;
    }

    internal static void Attach(
        GameHostActivity activity,
        ValidatedExecutionPlan plan,
        GameHostRecipeDecision decision)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.CanRewrite ||
            decision.EntitlementPolicy != GameHostEntitlementPolicy.TrustedInstalledSource ||
            !decision.SupportKey.Equals(GameHostRecipeCatalog.TestedPlaySupportKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The support catalog has not authorized this GameHost bridge session.");
        }

        if (!plan.PackageName.Equals(GameHostRecipeCatalog.TestedPlayPackageName, StringComparison.Ordinal) ||
            !plan.VersionName.Equals(GameHostRecipeCatalog.TestedPlayVersionName, StringComparison.Ordinal) ||
            plan.LongVersionCode != GameHostRecipeCatalog.TestedPlayLongVersionCode ||
            !plan.SelectedAbi.Equals(GameHostRecipeCatalog.TestedPlayAbi, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The validated execution plan does not match the approved game identity.");
        }

        if (activity.IsFinishing || activity.IsDestroyed)
        {
            throw new InvalidOperationException("The framework-created GameHost Activity is not usable.");
        }

        lock (SessionLock)
        {
            if (session is not null && !ReferenceEquals(session.Activity, activity))
            {
                throw new InvalidOperationException("A different GameHost bridge session is already active.");
            }

            session = new BridgeSession(activity, plan.LongVersionCode);
        }
    }

    internal static void Attach(Activity activity, PreparedGameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PackageName != GameHostRecipeCatalog.TestedPlayPackageName ||
            snapshot.VersionName != GameHostRecipeCatalog.TestedPlayVersionName ||
            snapshot.VersionCode != GameHostRecipeCatalog.TestedPlayLongVersionCode ||
            snapshot.Abi != GameHostRecipeCatalog.TestedPlayAbi || activity.IsFinishing || activity.IsDestroyed)
            throw new InvalidOperationException("The prepared SMAPI bridge snapshot is not usable.");
        lock (SessionLock)
        {
            if (session is not null && !ReferenceEquals(session.Activity, activity))
                throw new InvalidOperationException("A different GameHost bridge session is already active.");
            session = new BridgeSession(activity, snapshot.VersionCode);
        }
    }

    internal static void Detach(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        lock (SessionLock)
        {
            if (session is not null && ReferenceEquals(session.Activity, activity))
            {
                session = null;
            }
        }
    }

    private static BridgeSession GetSession()
    {
        lock (SessionLock)
        {
            return session ?? throw new InvalidOperationException(
                "No catalog-approved trusted GameHost bridge session is active.");
        }
    }

    private sealed record BridgeSession(Activity Activity, long VersionCode);
}
