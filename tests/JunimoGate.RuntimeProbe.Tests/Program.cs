using JunimoGate.RuntimeProbe.Core;
using JunimoGate.Tests;

Console.WriteLine("Host CoreCLR implementation verification only; this is not an Android runtime conclusion.");

var report = await RuntimeProbeRunner.RunAsync(new RuntimeProbeInput
{
    PlatformMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["probePlatform"] = "host-coreclr",
        ["androidConclusion"] = "false",
    },
}, progress =>
{
    if (progress.Result is not null)
    {
        Console.WriteLine($"CASE {progress.Result.Status.ToString().ToUpperInvariant()} {progress.CaseId}: {progress.Result.Summary}");
        if (progress.Result.Exception is not null)
        {
            Console.WriteLine($"  {progress.Result.Exception.Type}: {progress.Result.Exception.Message}");
            Console.WriteLine(progress.Result.Exception.StackTrace);
        }
    }
});

var json = RuntimeProbeJson.Serialize(report);
var roundTripped = RuntimeProbeJson.Deserialize(json);

return TestHarness.Run(
    ("all hard runtime cases pass on host CoreCLR", () =>
    {
        TestHarness.Equal(RuntimeProbeCaseIds.All.Count, report.Cases.Count);
        TestHarness.True(report.Cases.All(result => result.IsHardRequirement));
        TestHarness.True(report.Cases.All(result => result.Status == ProbeCaseStatus.Passed),
            string.Join(Environment.NewLine, report.Cases
                .Where(result => result.Status != ProbeCaseStatus.Passed)
                .Select(result => $"{result.Id}: {result.Exception?.Type}: {result.Exception?.Message}")));
    }),
    ("hard case IDs are complete, unique, and ordered", () =>
    {
        TestHarness.True(RuntimeProbeCaseIds.All.SequenceEqual(report.Cases.Select(result => result.Id)));
        TestHarness.Equal(RuntimeProbeCaseIds.All.Count, report.Cases.Select(result => result.Id).Distinct(StringComparer.Ordinal).Count());
    }),
    ("passing hard cases produce the application-local Mono passed conclusion", () =>
    {
        TestHarness.Equal(RuntimeProbeConclusions.Passed, report.Conclusion);
        TestHarness.Equal("host-coreclr", report.PlatformMetadata["probePlatform"]);
        TestHarness.Equal("false", report.PlatformMetadata["androidConclusion"]);
    }),
    ("soft failures do not override hard case conclusions", () =>
    {
        var timestamp = DateTimeOffset.UtcNow;
        var hardPass = new ProbeCaseResult(
            "hard-pass",
            true,
            ProbeCaseStatus.Passed,
            timestamp,
            timestamp,
            0,
            "passed",
            new Dictionary<string, string>(StringComparer.Ordinal),
            null);
        var softFailure = hardPass with
        {
            Id = "soft-failure",
            IsHardRequirement = false,
            Status = ProbeCaseStatus.Failed,
        };
        var hardFailure = hardPass with
        {
            Id = "hard-failure",
            Status = ProbeCaseStatus.Failed,
        };
        var androidLibraryFix = hardPass with
        {
            Id = RuntimeProbeCaseIds.HarmonyMonoModAndroidSupport,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["libraryFixRequired"] = "true",
                ["libraryFixApplied"] = "true",
            },
        };

        TestHarness.Equal(RuntimeProbeConclusions.Passed, RuntimeProbeConclusions.Evaluate([hardPass, softFailure]));
        TestHarness.Equal(
            RuntimeProbeConclusions.PassedWithHarmonyMonoModFix,
            RuntimeProbeConclusions.Evaluate([hardPass, androidLibraryFix, softFailure]));
        TestHarness.Equal(RuntimeProbeConclusions.Failed, RuntimeProbeConclusions.Evaluate([hardFailure, softFailure]));
        TestHarness.Equal(RuntimeProbeConclusions.Failed, RuntimeProbeConclusions.Evaluate([softFailure]));
    }),
    ("JSON report roundtrip preserves conclusion and case results", () =>
    {
        TestHarness.Equal(report.Conclusion, roundTripped.Conclusion);
        TestHarness.Equal(report.Cases.Count, roundTripped.Cases.Count);
        TestHarness.True(report.Cases.Select(result => (result.Id, result.Status))
            .SequenceEqual(roundTripped.Cases.Select(result => (result.Id, result.Status))));
        TestHarness.Equal(report.Environment.Harmony.ModuleVersionId, roundTripped.Environment.Harmony.ModuleVersionId);
        TestHarness.Equal(report.Environment.MonoModUtils.ModuleVersionId, roundTripped.Environment.MonoModUtils.ModuleVersionId);
    }));
