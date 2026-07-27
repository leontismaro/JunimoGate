using System.Text.Json;
using Android.App;
using Android.Content;
using JunimoGate.Android;

namespace JunimoGate.GameHost;

/// <summary>Tracks only whether JunimoGate's isolated game process is alive for launcher routing.</summary>
public static class GameSessionRegistry
{
    private const string SessionFileName = "active-game-session.json";
    private const int MaximumSessionBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MarkActive(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var path = GetPath(activity);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var session = new ActiveGameSession(
                global::Android.OS.Process.MyPid(),
                activity.TaskId,
                DateTimeOffset.UtcNow);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(session, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static bool TryRouteActiveGame(Activity launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        if (!TryGetActiveSession(launcher, out var session))
            return false;
        if (launcher.TaskId == session.TaskId)
            return true;

        var safe = launcher.ApplicationContext ?? launcher;
        var activityManager = safe.GetSystemService(Context.ActivityService) as ActivityManager;
        foreach (var task in activityManager?.AppTasks ?? [])
        {
            var info = task.TaskInfo;
            if (info is null || GetTaskId(info) != session.TaskId ||
                info.TopActivity?.ClassName != SmapiGameActivity.ActivityName)
                continue;

            task.MoveToFront();
            return true;
        }

        return false;
    }

    private static int GetTaskId(ActivityManager.RecentTaskInfo info) =>
        OperatingSystem.IsAndroidVersionAtLeast(29) ? info.TaskId : info.Id;

    public static bool IsGameProcessActive(Context context)
        => TryGetActiveSession(context, out _);

    private static bool TryGetActiveSession(Context context, out ActiveGameSession session)
    {
        session = null!;
        var path = GetPath(context);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 1 || file.Length > MaximumSessionBytes)
                return false;
            var candidate = JsonSerializer.Deserialize<ActiveGameSession>(File.ReadAllText(path), JsonOptions);
            if (candidate is null || candidate.ProcessId <= 0 || candidate.TaskId <= 0)
            {
                TryDelete(path);
                return false;
            }

            var safe = context.ApplicationContext ?? context;
            var activityManager = safe.GetSystemService(Context.ActivityService) as ActivityManager;
            var expectedProcessName = safe.PackageName + ":game";
            var isActive = activityManager?.RunningAppProcesses?.Any(process =>
                process.Pid == candidate.ProcessId &&
                process.Uid == global::Android.OS.Process.MyUid() &&
                process.ProcessName == expectedProcessName) == true;
            if (!isActive)
                TryDelete(path);
            else
                session = candidate;
            return isActive;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            TryDelete(path);
            return false;
        }
    }

    public static void ClearCurrentProcess(Context context)
    {
        var path = GetPath(context);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 1 || file.Length > MaximumSessionBytes)
                return;
            var session = JsonSerializer.Deserialize<ActiveGameSession>(File.ReadAllText(path), JsonOptions);
            if (session?.ProcessId == global::Android.OS.Process.MyPid())
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A stale marker is rejected the next time the launcher checks the live process list.
        }
    }

    private static string GetPath(Context context)
    {
        var root = Path.Combine(
            AndroidPrivateStorage.GetRuntimeRoot(context.ApplicationContext ?? context),
            "launch-sessions");
        Directory.CreateDirectory(root);
        return Path.Combine(root, SessionFileName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record ActiveGameSession(int ProcessId, int TaskId, DateTimeOffset StartedAtUtc);
}
