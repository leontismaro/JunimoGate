using System.Text.Json;
using System.Text.RegularExpressions;

namespace JunimoGate.Core;

public enum ProductLogLevel
{
    Trace,
    Debug,
    Info,
    Alert,
    Warn,
    Error,
    Critical,
    Unknown,
}

public sealed record ProductLogEntry(
    string Time,
    ProductLogLevel Level,
    string Source,
    string Message,
    bool IsPartial = false,
    int RepeatCount = 1);

public sealed record ProductLogParseResult(
    IReadOnlyList<ProductLogEntry> Entries,
    int WarningCount,
    int ErrorCount);

public static partial class ProductLogParser
{
    public static ProductLogParseResult ParseSmapi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<ProductLogEntry>();
        foreach (var rawLine in EnumerateLines(text))
        {
            var match = SmapiHeader().Match(rawLine);
            if (match.Success)
            {
                entries.Add(new ProductLogEntry(
                    match.Groups["time"].Value,
                    ParseLevel(match.Groups["level"].Value),
                    match.Groups["source"].Value.Trim(),
                    match.Groups["message"].Value));
            }
            else if (entries.Count > 0)
            {
                var previous = entries[^1];
                entries[^1] = previous with { Message = AppendLine(previous.Message, rawLine) };
            }
            else
            {
                entries.Add(new ProductLogEntry(
                    string.Empty,
                    ProductLogLevel.Unknown,
                    string.Empty,
                    rawLine,
                    IsPartial: true));
            }
        }
        return BuildResult(entries);
    }

    public static ProductLogParseResult ParseJsonLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<ProductLogEntry>();
        foreach (var line in EnumerateLines(text))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !TryGetString(root, "level", out var level) ||
                    !TryGetString(root, "message", out var message))
                {
                    continue;
                }

                _ = TryGetString(root, "tag", out var source);
                if (string.IsNullOrWhiteSpace(source))
                    _ = TryGetString(root, "process", out source);
                if (TryGetString(root, "exception", out var exception) && !string.IsNullOrWhiteSpace(exception))
                    message = AppendLine(message, exception);

                entries.Add(new ProductLogEntry(
                    GetJsonTime(root),
                    ParseLevel(level),
                    source ?? string.Empty,
                    message));
            }
            catch (JsonException)
            {
                // A bounded JSONL tail may begin with a partial line.
            }
        }
        return BuildResult(entries);
    }

    private static ProductLogParseResult BuildResult(IReadOnlyList<ProductLogEntry> entries)
    {
        var warnings = entries.Count(static entry => entry.Level == ProductLogLevel.Warn);
        var errors = entries.Count(static entry => entry.Level is ProductLogLevel.Error or ProductLogLevel.Critical);
        return new ProductLogParseResult(CollapseRepeats(entries), warnings, errors);
    }

    private static IReadOnlyList<ProductLogEntry> CollapseRepeats(IReadOnlyList<ProductLogEntry> entries)
    {
        if (entries.Count < 2)
            return entries.ToArray();
        var collapsed = new List<ProductLogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (collapsed.Count > 0 && IsSameMessage(collapsed[^1], entry))
            {
                var previous = collapsed[^1];
                collapsed[^1] = previous with { RepeatCount = previous.RepeatCount + 1 };
            }
            else
                collapsed.Add(entry);
        }
        return collapsed;
    }

    private static bool IsSameMessage(ProductLogEntry left, ProductLogEntry right) =>
        left.Level == right.Level &&
        left.IsPartial == right.IsPartial &&
        left.Source.Equals(right.Source, StringComparison.Ordinal) &&
        left.Message.Equals(right.Message, StringComparison.Ordinal);

    private static ProductLogLevel ParseLevel(string value) => value.Trim().ToUpperInvariant() switch
    {
        "TRACE" => ProductLogLevel.Trace,
        "DEBUG" => ProductLogLevel.Debug,
        "INFO" => ProductLogLevel.Info,
        "ALERT" => ProductLogLevel.Alert,
        "WARN" or "WARNING" => ProductLogLevel.Warn,
        "ERROR" => ProductLogLevel.Error,
        "CRITICAL" or "FATAL" => ProductLogLevel.Critical,
        _ => ProductLogLevel.Unknown,
    };

    private static string GetJsonTime(JsonElement root)
    {
        if (!TryGetString(root, "timestampUtc", out var value) ||
            !DateTimeOffset.TryParse(value, out var timestamp))
        {
            return string.Empty;
        }
        return timestamp.ToLocalTime().ToString("HH:mm:ss");
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static string AppendLine(string current, string next) =>
        current.Length == 0 ? next : $"{current}\n{next}";

    [GeneratedRegex(
        @"^\[(?<time>\d{2}:\d{2}:\d{2})\s+(?<level>TRACE|DEBUG|INFO|ALERT|WARN|ERROR|CRITICAL)\s+(?<source>[^\]]+?)\]\s?(?<message>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SmapiHeader();
}
