using System.Text;

namespace JunimoGate.Mods;

/// <summary>A normalized relative ZIP entry path that has passed traversal and rooted-path checks.</summary>
public readonly record struct SafeArchivePath
{
    private SafeArchivePath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SafeArchivePath Parse(string value)
    {
        if (!TryParse(value, out var path, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        return path;
    }

    public static bool TryParse(string? value, out SafeArchivePath path) => TryParse(value, out path, out _);

    public static bool TryParse(string? value, out SafeArchivePath path, out string? error)
    {
        path = default;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "An archive entry path is required.";
            return false;
        }

        if (value.IndexOf('\0') >= 0)
        {
            error = "Archive entry paths cannot contain NUL characters.";
            return false;
        }

        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            HasWindowsDrivePrefix(normalized))
        {
            error = "Archive entry paths must be relative and cannot use a drive or UNC root.";
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "An archive entry path must contain a file or directory name.";
            return false;
        }

        if (segments.Any(static segment => segment is "." or ".."))
        {
            error = "Archive entry paths cannot contain traversal segments.";
            return false;
        }

        if (segments.Any(static segment => Encoding.UTF8.GetByteCount(segment) > 255))
        {
            error = "Archive entry path segments cannot exceed 255 UTF-8 bytes.";
            return false;
        }

        var canonical = string.Join('/', segments);
        if (Encoding.UTF8.GetByteCount(canonical) > 2048)
        {
            error = "Archive entry paths cannot exceed 2048 UTF-8 bytes.";
            return false;
        }

        path = new SafeArchivePath(canonical);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    private static bool HasWindowsDrivePrefix(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';
}
