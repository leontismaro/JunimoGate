using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoGate.RuntimeProbe.Core;

public static class RuntimeProbeCaseIds
{
    public const string DynamicCodeCapability = "runtime-dynamic-code-capability";
    public const string HarmonyMonoModAndroidSupport = "harmony-monomod-android-platform-support";
    public const string MonoManagedEntryPoint = "mono-managed-entry-point-diagnostic";
    public const string NativeCacheFlush = "native-arm64-cache-flush-diagnostic";
    public const string HarmonyPrivateMethod = "harmony-private-method-prefix-postfix";
    public const string HarmonyFieldInjection = "harmony-private-field-injection";
    public const string HarmonyTranspiler = "harmony-private-il-transpiler";
    public const string HarmonySmapiPrefix = "harmony-smapi-check-storage-migration-prefix";
    public const string MonoModDynamicMethod = "monomod-dmd-dynamicmethod-private-access";
    public const string MonoModCecil = "monomod-dmd-cecil-private-access";

    public static IReadOnlyList<string> All { get; } =
    [
        DynamicCodeCapability,
        HarmonyMonoModAndroidSupport,
        MonoManagedEntryPoint,
        NativeCacheFlush,
        HarmonyPrivateMethod,
        HarmonyFieldInjection,
        HarmonyTranspiler,
        HarmonySmapiPrefix,
        MonoModDynamicMethod,
        MonoModCecil,
    ];
}

public static class RuntimeProbeConclusions
{
    public const string Passed = "stock-runtime-passed";
    public const string PassedWithHarmonyMonoModFix = "stock-runtime-passed-with-harmony-monomod-fix";
    public const string Failed = "stock-runtime-failed-needs-investigation";

    public static string Evaluate(IEnumerable<ProbeCaseResult> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var hardCases = cases.Where(result => result.IsHardRequirement).ToArray();
        if (hardCases.Length == 0 || hardCases.Any(result => result.Status != ProbeCaseStatus.Passed))
        {
            return Failed;
        }

        var androidSupport = hardCases.FirstOrDefault(result =>
            string.Equals(result.Id, RuntimeProbeCaseIds.HarmonyMonoModAndroidSupport, StringComparison.Ordinal));
        var fixRequired = HasTrueDetail(androidSupport, "libraryFixRequired");
        var fixApplied = HasTrueDetail(androidSupport, "libraryFixApplied");
        return fixRequired && fixApplied ? PassedWithHarmonyMonoModFix : Passed;
    }

    private static bool HasTrueDetail(ProbeCaseResult? result, string key) =>
        result?.Details.TryGetValue(key, out var value) == true
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

[JsonConverter(typeof(JsonStringEnumConverter<ProbeCaseStatus>))]
public enum ProbeCaseStatus
{
    Passed,
    Failed,
}

public sealed record RuntimeProbeInput
{
    public Dictionary<string, string> PlatformMetadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed record ProbeProgress(
    string CaseId,
    int CaseNumber,
    int TotalCases,
    string Stage,
    ProbeCaseResult? Result = null);

public sealed record ProbeExceptionInfo(
    string Type,
    string Message,
    string? StackTrace);

public sealed record ProbeCaseResult(
    string Id,
    bool IsHardRequirement,
    ProbeCaseStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    double DurationMilliseconds,
    string Summary,
    Dictionary<string, string> Details,
    ProbeExceptionInfo? Exception);

public sealed record ProbeAssemblyInfo(
    string Name,
    string? AssemblyVersion,
    string? InformationalVersion,
    string ModuleVersionId);

public sealed record ProbeEnvironmentInfo(
    string RuntimeVersion,
    string RuntimeFramework,
    string RuntimeIdentifier,
    string OperatingSystem,
    string ProcessArchitecture,
    string OsArchitecture,
    bool IsDynamicCodeSupported,
    bool IsDynamicCodeCompiled,
    string? MonoRuntimeDisplayName,
    ProbeAssemblyInfo Harmony,
    ProbeAssemblyInfo MonoModUtils);

public sealed record RuntimeProbeReport(
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    double DurationMilliseconds,
    string Conclusion,
    ProbeEnvironmentInfo Environment,
    Dictionary<string, string> PlatformMetadata,
    IReadOnlyList<ProbeCaseResult> Cases);

public static class RuntimeProbeJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(RuntimeProbeReport report) =>
        JsonSerializer.Serialize(report, Options);

    public static RuntimeProbeReport Deserialize(string json) =>
        JsonSerializer.Deserialize<RuntimeProbeReport>(json, Options)
        ?? throw new JsonException("Runtime probe report JSON contained null.");

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
