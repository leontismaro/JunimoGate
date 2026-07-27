using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Android.App;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI.Framework;
using StardewValley;

namespace StardewModdingAPI.Mobile;

// Keep the queue used by SGameRunner, but don't apply the legacy DoUpdate detour globally.
// The public MonoGame provider already drives the Android frame loop; rewriting its private
// DoUpdate wrapper creates a dynamic method which can't legally call the protected Game.Update.
internal static class AndroidGameLoopManager
{
    internal delegate bool OnGameUpdatingDelegate(GameTime gameTime);
    static HashSet<OnGameUpdatingDelegate> listOnGameUpdating = new();
    static Queue<OnGameUpdatingDelegate> queueOnGameUpdatingToAdd = new();
    static Queue<OnGameUpdatingDelegate> queueOnGameUpdatingToRemove = new();

    /// <summary>
    /// Register On Main Thread Only!!
    /// </summary>
    /// <param name="onGameUpdate"></param>
    internal static void RegisterOnGameUpdating(OnGameUpdatingDelegate onGameUpdate)
    {
        queueOnGameUpdatingToAdd.Enqueue(onGameUpdate);
    }

    /// <summary>
    /// Unregister On Main Thread Only!!
    /// </summary>
    /// <param name="onGameUpdate"></param>
    internal static void UnregisterOnGameUpdating(OnGameUpdatingDelegate onGameUpdate)
    {
        queueOnGameUpdatingToRemove.Enqueue(onGameUpdate);
    }

    public static bool IsSkipOriginalGameUpdating { get; private set; } = false;
    internal static void UpdateFrame_OnGameUpdating(GameTime gameTime)
    {
        //reset
        IsSkipOriginalGameUpdating = false;

        if (queueOnGameUpdatingToAdd.Count > 0)
        {
            while (queueOnGameUpdatingToAdd.TryDequeue(out OnGameUpdatingDelegate? item))
            {
                if (item is not null)
                    listOnGameUpdating.Add(item);
            }
        }

        if (queueOnGameUpdatingToRemove.Count > 0)
        {
            while (queueOnGameUpdatingToRemove.TryDequeue(out OnGameUpdatingDelegate? item))
            {
                if (item is not null)
                    listOnGameUpdating.Remove(item);
            }
        }

        //Console.WriteLine("Android Looper OnGameUpdating...");
        foreach (var callback in listOnGameUpdating)
        {
            if (callback(gameTime))
            {
                IsSkipOriginalGameUpdating = true;
            }
        }
    }

    //fix game update freeze on GameTick & IsFixedTimeStep
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Game), "DoUpdate")]
    static void Postfix_DoUpdate(GameTime gameTime)
    {
        var game = SGameRunner.instance as Game
            ?? throw new InvalidOperationException("The hosted game runner is unavailable.");
        FieldInfo accumulatedElapsedTimeField = AccessTools.Field(game.GetType(), "_accumulatedElapsedTime")
            ?? throw new MissingFieldException(game.GetType().FullName, "_accumulatedElapsedTime");
        if (accumulatedElapsedTimeField.GetValue(game) is not TimeSpan accumulatedElapsedTime)
            throw new InvalidOperationException("The MonoGame accumulated elapsed time is unavailable.");

        if (accumulatedElapsedTime.TotalSeconds > 0.15f)
        {
            accumulatedElapsedTime = TimeSpan.FromSeconds(0f);
            accumulatedElapsedTimeField.SetValue(game, accumulatedElapsedTime);
            //release freeze loop Game.DoUpdate()
        }


        //debug
        //debug
#if false
        PrintMemory();
#endif
    }

    static Stopwatch TimerLogMemory = Stopwatch.StartNew();
    private static void PrintMemory()
    {
        const int refreshTime = 1000;
        if (TimerLogMemory.Elapsed.TotalMilliseconds < refreshTime)
            return;

        TimerLogMemory.Restart();

        var mainActivity = SMAPIActivityTool.MainActivity
            ?? throw new InvalidOperationException("The Android game Activity is unavailable.");
        ActivityManager activityManager = mainActivity.GetSystemService(Service.ActivityService) as ActivityManager
            ?? throw new InvalidOperationException("Android ActivityManager is unavailable.");
        var memoryInfo = new ActivityManager.MemoryInfo();
        activityManager.GetMemoryInfo(memoryInfo);


        StringBuilder log = new();
        log.AppendLine("====== Log Mem Info ======");
        log.AppendLine($"Total Mem: {memoryInfo.TotalMem.KbToMB():F3} MB");
        log.AppendLine($"Available  Mem: {memoryInfo.AvailMem.KbToMB():F3} MB");
        log.AppendLine($"Is Low Mem: {memoryInfo.LowMemory}");
        var monitor = SCore.Instance?.SMAPIMonitor;
        if (monitor != null)
            monitor.Log(log.ToString(), LogLevel.Trace);
    }
    static float KbToMB(this long val) => (float)val / (1024f * 1024f);
}
