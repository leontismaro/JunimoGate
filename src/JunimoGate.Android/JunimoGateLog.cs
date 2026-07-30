using System.Text;
using System.Text.Json;
using Android.Content;
using Android.Runtime;
using AndroidBuild = global::Android.OS.Build;
using AndroidLog = Android.Util.Log;

namespace JunimoGate.Android;

/// <summary>Writes bounded per-process product diagnostics while preserving Android logcat output.</summary>
public static class JunimoGateLog
{
    private const long MaximumFileBytes = 512 * 1024;
    private const int MaximumMessageCharacters = 8 * 1024;
    private const int MaximumExceptionCharacters = 48 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private static StreamWriter? writer;
    private static string? processName;
    private static string? currentPath;
    private static string? previousPath;

    public static void Initialize(Context context, string process, string buildId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (process is not ("launcher" or "game"))
            throw new ArgumentOutOfRangeException(nameof(process));
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);

        lock (Gate)
        {
            if (processName is not null)
            {
                if (!processName.Equals(process, StringComparison.Ordinal))
                    AndroidLog.Warn("JunimoGate.Log", $"process-name-change-rejected:{processName}:{process}");
                return;
            }

            try
            {
                var root = AndroidPrivateStorage.GetProductLogsRoot(context.ApplicationContext ?? context);
                currentPath = Path.Combine(root, $"{process}-current.jsonl");
                previousPath = Path.Combine(root, $"{process}-previous.jsonl");
                RotateFiles(currentPath, previousPath);
                writer = OpenWriter(currentPath);
                processName = process;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandledException;
                WriteFileEntry(
                    "info",
                    "JunimoGate.Process",
                    $"process-start build={buildId} sdk={(int)AndroidBuild.VERSION.SdkInt}",
                    null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                DisposeWriter();
                AndroidLog.Warn("JunimoGate.Log", $"persistent-log-unavailable:{exception.GetType().Name}");
            }
        }
    }

    public static void Info(string tag, string message) => Write("info", tag, message, null);

    public static void Warn(string tag, string message) => Write("warn", tag, message, null);

    public static void Warn(string tag, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("warn", tag, message, exception);
    }

    public static void Error(string tag, string message) => Write("error", tag, message, null);

    public static void Error(string tag, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("error", tag, message, exception);
    }

    private static void Write(string level, string tag, string message, Exception? exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        message ??= string.Empty;
        var logcatMessage = exception is null
            ? message
            : $"{message}:{exception.GetType().Name}:{exception.Message}";
        switch (level)
        {
            case "info":
                AndroidLog.Info(tag, logcatMessage);
                break;
            case "warn":
                AndroidLog.Warn(tag, logcatMessage);
                break;
            default:
                AndroidLog.Error(tag, logcatMessage);
                break;
        }

        lock (Gate)
        {
            if (writer is null)
                return;
            try
            {
                WriteFileEntry(level, tag, message, exception);
            }
            catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException or JsonException)
            {
                DisposeWriter();
                AndroidLog.Warn("JunimoGate.Log", $"persistent-log-write-failed:{writeException.GetType().Name}");
            }
        }
    }

    private static void WriteFileEntry(string level, string tag, string message, Exception? exception)
    {
        var entry = new ProductLogEntry(
            DateTimeOffset.UtcNow,
            level,
            processName ?? "initializing",
            Environment.ProcessId,
            tag,
            Truncate(message, MaximumMessageCharacters),
            exception is null ? null : Truncate(exception.ToString(), MaximumExceptionCharacters));
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        if (writer!.BaseStream.Length + Utf8WithoutBom.GetByteCount(line) + 1 > MaximumFileBytes)
        {
            DisposeWriter();
            RotateFiles(currentPath!, previousPath!);
            writer = OpenWriter(currentPath!);
        }
        writer.WriteLine(line);
    }

    private static StreamWriter OpenWriter(string path) => new(
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
        Utf8WithoutBom)
    {
        AutoFlush = true,
    };

    private static void RotateFiles(string current, string previous)
    {
        if (!File.Exists(current))
            return;
        File.Move(current, previous, overwrite: true);
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters] + " [truncated]";

    private static void DisposeWriter()
    {
        try
        {
            writer?.Dispose();
        }
        catch (IOException)
        {
            // Logging must never affect launcher behavior.
        }
        writer = null;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
            Error("JunimoGate.Process", $"unhandled-exception terminating={(eventArgs.IsTerminating ? 1 : 0)}", exception);
        else
            Error("JunimoGate.Process", $"unhandled-exception terminating={(eventArgs.IsTerminating ? 1 : 0)} value={eventArgs.ExceptionObject}");
    }

    private static void OnAndroidUnhandledException(object? sender, RaiseThrowableEventArgs eventArgs) =>
        Error("JunimoGate.Process", "android-unhandled-exception", eventArgs.Exception);

    private sealed record ProductLogEntry(
        DateTimeOffset TimestampUtc,
        string Level,
        string Process,
        int ProcessId,
        string Tag,
        string Message,
        string? Exception);
}
