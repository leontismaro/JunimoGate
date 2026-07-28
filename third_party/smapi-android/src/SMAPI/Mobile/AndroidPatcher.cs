using System;
using HarmonyLib;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Internal;
using StardewModdingAPI.Mobile.Facade;
using StardewModdingAPI.Mobile.Mods;
using StardewModdingAPI.Mobile.Vectors;
using StardewValley.Menus;

namespace StardewModdingAPI.Mobile;

[HarmonyPatch]
internal static class AndroidPatcher
{
    public static Harmony? harmony { get; private set; }
    public static void Setup()
    {
        AndroidLogger.Log("===========================");
        AndroidLogger.Log("===========================");
        AndroidLogger.Log("On AndroidPatcher.Setup()");

        try
        {
            //setup
            Log.enabled = true;
            harmony = new Harmony(nameof(AndroidPatcher));

        }
        catch (Exception ex)
        {
            Console.WriteLine("Error on AndroidPatcher.Setup()");
            AndroidLogger.Log(ex);
            throw;
        }
    }
    static void ApplyHarmonyPatchAll()
    {
        var activeHarmony = harmony ?? throw new InvalidOperationException("Android Harmony setup has not completed.");
        var monitor = SCore.Instance.SMAPIMonitor;
        monitor.Log("On ApplyHarmonyPatchAll()..");
        try
        {
            activeHarmony.PatchAll();
            monitor.Log("Done harmony.PatchAll()");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            monitor.Log(ex.GetLogSummary(), LogLevel.Error);
            throw;
        }
    }
    static void SetupModFix()
    {
        //Register mod fix here
        var modFix = AndroidModFixManager.Init();
        //list mods
        FarmTypeManagerFix.Init(modFix);
        SpaceCoreFix.Init(modFix);
        SveFix.Init(modFix);
        GenericConfigMenuModFix.Init(modFix);
        UnlockableBundlesModFix.Init(modFix);
        FashionSenseModFix.Init(modFix);
        DisableSaveBackup.Init(modFix);
        ModQuickSaveOptionPage.Init(modFix);
    }
    public static void OnBeforeSCoreRun()
    {
        var activeHarmony = harmony ?? throw new InvalidOperationException("Android Harmony setup has not completed.");
        var saveBackupZip = new SaveBackupZip();
        saveBackupZip.Start();

        SetupModFix();
        ApplyHarmonyPatchAll();
        SCore.Instance.SMAPIMonitor.Log("Applying Android vector converters...");
        VectorTypeConverterFix.ApplyPatch(activeHarmony);
        // MobileFarmChooser's optional farm-selector compatibility detours target methods that
        // Android Mono may not JIT-compile before a menu instance exists. They are not needed for
        // the SMAPI title-screen/runtime baseline; defer this optional patch family until the
        // menu is created by a future lifecycle-aware adapter.
        SCore.Instance.SMAPIMonitor.Log("Applying Android letter viewer adapters...");
        LetterViewerMenuRewriter.ApplyPatch(activeHarmony);
        SCore.Instance.SMAPIMonitor.Log("Android compatibility adapters ready.");
    }


    // Disable checkForAndLoadEmergencySave for Emergency Save
    [HarmonyPatch(typeof(TitleMenu), nameof(TitleMenu.checkForAndLoadEmergencySave))]
    [HarmonyPrefix]
    static bool Disable_checkForAndLoadEmergencySave(ref bool __result)
    {
        TitleMenu.PromptedEmergencySave = true;
        __result = false;
        return false;
    }
}
