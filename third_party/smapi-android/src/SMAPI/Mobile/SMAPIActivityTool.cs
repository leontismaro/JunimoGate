using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using HarmonyLib;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Internal;
using StardewValley;
using StardewModdingAPI.AndroidHost;

namespace StardewModdingAPI.Mobile;

internal static class SMAPIActivityTool
{
    static Activity? _activity;
    public static void Configure(Activity activity) => _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    public static Activity MainActivity
    {
        get
        {
            return _activity ?? throw new InvalidOperationException("SMAPI Android host Activity was not configured.");
        }
    }

    public static void ExitGame()
    {
        IMonitor? monitor = SCore.Instance?.SMAPIMonitor;
        monitor?.Log("Try Exit Game At SMAPIActivityTool");
        try
        {
            MainActivity.Finish();
            monitor?.Log("Done Exit Game.");
        }
        catch (Exception ex)
        {
            monitor?.Log(ex.GetLogSummary());
            Console.WriteLine(ex);
            throw;
        }

    }
}
