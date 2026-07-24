namespace JunimoGate.Core;

public enum DiagnosticSeverity
{
    Trace,
    Information,
    Warning,
    Error,
    Critical,
}

public enum StartupStage
{
    Discovery,
    Inventory,
    Extraction,
    Rewrite,
    RuntimeValidation,
    GameHost,
    ModLoading,
}

/// <summary>A transport-neutral diagnostic suitable for logs and future UI presentation.</summary>
public sealed record DiagnosticRecord
{
    public DiagnosticRecord(
        DateTimeOffset timestamp,
        StartupStage stage,
        DiagnosticSeverity severity,
        string code,
        string message,
        string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A diagnostic code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A diagnostic message is required.", nameof(message));
        }

        Timestamp = timestamp;
        Stage = stage;
        Severity = severity;
        Code = code;
        Message = message;
        Detail = detail;
    }

    public DateTimeOffset Timestamp { get; }

    public StartupStage Stage { get; }

    public DiagnosticSeverity Severity { get; }

    public string Code { get; }

    public string Message { get; }

    public string? Detail { get; }
}
