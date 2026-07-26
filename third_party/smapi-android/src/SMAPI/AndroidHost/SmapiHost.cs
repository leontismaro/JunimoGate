using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Android.App;
using Android.Views;
using Microsoft.Xna.Framework;

namespace StardewModdingAPI.AndroidHost;

public sealed record SmapiFailure(string Code, string Message, Exception? Exception = null);

public interface IMainThreadDispatcher
{
    bool IsMainThread { get; }
    void Invoke(Action callback);
}

public interface IManagedAssemblyLoader
{
    Assembly LoadFromPath(string absolutePath);
    Assembly LoadRewritten(string sourcePath, ReadOnlyMemory<byte> assemblyBytes, ReadOnlyMemory<byte>? symbols);
}

public sealed record SmapiRuntimeOptions
{
    public required Activity Activity { get; init; }
    public required string GameAssemblyDirectory { get; init; }
    public required string ContentDirectory { get; init; }
    public required string ModsDirectory { get; init; }
    public required string InternalDirectory { get; init; }
    public required string ConfigDirectory { get; init; }
    public required string LogDirectory { get; init; }
    public required string SaveDirectory { get; init; }
    public required IMainThreadDispatcher MainThread { get; init; }
    public required IManagedAssemblyLoader AssemblyLoader { get; init; }
    public required Action<View> AttachGameView { get; init; }
    public required Action<SmapiFailure> ReportFailure { get; init; }
    public required Action RequestExit { get; init; }
}

public static class AndroidHostServices
{
    internal static SmapiRuntimeOptions? Options { get; private set; }
    internal static IManagedAssemblyLoader? AssemblyLoader => Options?.AssemblyLoader;
    internal static string? ManagedAssemblyDirectory => Options is null
        ? null
        : Path.Combine(Path.GetDirectoryName(Options.InternalDirectory)!, "managed");

    internal static void Configure(SmapiRuntimeOptions options)
    {
        Options = options;
        Directory.CreateDirectory(options.InternalDirectory);
        Directory.CreateDirectory(options.ConfigDirectory);
        Directory.CreateDirectory(options.LogDirectory);
        Directory.CreateDirectory(options.SaveDirectory);
        Directory.CreateDirectory(options.ModsDirectory);
        EarlyConstants.Configure(
            options.GameAssemblyDirectory,
            options.ContentDirectory,
            options.InternalDirectory,
            options.ConfigDirectory,
            options.LogDirectory,
            options.SaveDirectory,
            Path.GetDirectoryName(options.ConfigDirectory)!);
        Mobile.SMAPIActivityTool.Configure(options.Activity);
        Mobile.AndroidMainThread.Init([]);
    }
}

public sealed class SmapiRuntime
{
    private readonly SmapiRuntimeOptions options;
    public SmapiRuntime(SmapiRuntimeOptions options) => this.options = options ?? throw new ArgumentNullException(nameof(options));
    public SmapiSession CreateSession() => new(options);
}

public sealed class SmapiSession : IDisposable
{
    private readonly SmapiRuntimeOptions options;
    private int started;
    private Game? game;
    private View? gameView;
    private bool disposed;
    internal SmapiSession(SmapiRuntimeOptions options) => this.options = options;

    public void Run()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("SMAPI session can only run once.");
        try
        {
            AndroidHostServices.Configure(options);
            Mobile.AndroidPatcher.Setup();
            StardewValley.Mobile.MobileDisplay.SetupDisplaySettings();
            var core = new Framework.SCore(options.ModsDirectory, writeToConsole: false, developerMode: true);
            Mobile.AndroidPatcher.OnBeforeSCoreRun();
            core.RunInteractively();
            game = Framework.SGameRunner.Instance;
            gameView = game.Services.GetService(typeof(View)) as View
                ?? throw new InvalidOperationException("The SMAPI game runner did not register its Android view.");
            options.MainThread.Invoke(ResumeGame);
        }
        catch (Exception ex)
        {
            var failure = new SmapiFailure("session_start_failed", ex.Message, ex);
            options.ReportFailure(failure);
            throw;
        }
    }

    public void OnResume()
    {
        if (game is not null && gameView is not null && !disposed)
            options.MainThread.Invoke(ResumeGame);
    }
    public void OnPause()
    {
        if (game is not null && gameView is not null && !disposed)
            options.MainThread.Invoke(PauseGame);
    }
    public void OnWindowFocusChanged(bool hasFocus) { }
    public bool OnBackPressed() { options.RequestExit(); return true; }
    public void Dispose()
    {
        disposed = true;
        gameView = null;
        game = null;
    }

    private void ResumeGame()
    {
        if (game is null || gameView is null) return;
        SetPlatformActive(game, true);
        InvokeViewLifecycle(gameView, "Resume");
        gameView.RequestFocus();
        InvokeGameLifecycle(game, "OnAppResume");
    }

    private void PauseGame()
    {
        if (game is null || gameView is null) return;
        InvokeGameLifecycle(game, "OnAppPause");
        InvokeViewLifecycle(gameView, "Pause");
        SetPlatformActive(game, false);
    }

    private static void SetPlatformActive(Game game, bool active)
    {
        var platformType = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.GamePlatform", throwOnError: true)!;
        var platform = game.Services.GetService(platformType)
            ?? throw new InvalidOperationException("The SMAPI game runner did not register its MonoGame platform.");
        var property = platformType.GetProperty("IsActive", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(platformType.FullName, "IsActive");
        property.GetSetMethod(nonPublic: true)?.Invoke(platform, [active]);
    }

    private static void InvokeViewLifecycle(View view, string methodName)
    {
        var method = view.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(view.GetType().FullName, methodName);
        method.Invoke(view, null);
    }

    private static void InvokeGameLifecycle(Game game, string methodName)
    {
        var field = game.GetType().GetField("gamePtr", BindingFlags.Instance | BindingFlags.Public);
        var gameInstance = field?.GetValue(game);
        var method = gameInstance?.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        method?.Invoke(gameInstance, null);
    }
}
