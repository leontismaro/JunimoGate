using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;
using JunimoGate.Rewriter.Tests;
using JunimoGate.Tests;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-rewriter-tests-{Guid.NewGuid():N}"));
Directory.CreateDirectory(root);

try
{
    var tests = new List<(string Name, Action Test)>
    {
        ("Rewrite contracts reject unsafe requests", () =>
        {
            var input = Path.Combine(root, "contracts", "input.dll");
            var output = Path.Combine(root, "contracts", "output.dll");
            var request = new RewriteRequest(input, output, GameHostBridgeRecipe.Identity);
            TestHarness.Equal(GameHostBridgeRecipe.Identity, request.Recipe);
            TestHarness.Throws<ArgumentException>(() => new RewriteRequest("relative.dll", output, request.Recipe));
            TestHarness.Throws<ArgumentException>(() => new RewriteRequest(input, "relative.dll", request.Recipe));
            TestHarness.Throws<ArgumentException>(() => new RewriteRequest(input, input, request.Recipe));
        }),
        ("Semantic bridge accepts identity and unrelated IL changes", () =>
        {
            var first = SyntheticGameAssemblies.Create(
                Path.Combine(root, "semantic-first"),
                new SyntheticGameOptions(
                    AssemblyVersion: new Version(1, 6, 99, 0),
                    ModuleVersionId: Guid.Parse("11111111-2222-3333-4444-555555555555")));
            var second = SyntheticGameAssemblies.Create(
                Path.Combine(root, "semantic-second"),
                new SyntheticGameOptions(
                    AssemblyVersion: new Version(1, 7, 0, 0),
                    ModuleVersionId: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    AddIrrelevantCall: true,
                    AddExtraLocal: true));

            var firstResult = Rewrite(first);
            var secondResult = Rewrite(second);
            AssertSuccess(firstResult);
            AssertSuccess(secondResult);
            TestHarness.True(firstResult.InputAssemblyIdentity != secondResult.InputAssemblyIdentity);
        }),
        ("Semantic bridge rejects a missing local action", () =>
        {
            AssertGuardRejected(SyntheticGameAssemblies.Create(
                Path.Combine(root, "semantic-missing"),
                new SyntheticGameOptions(MissingRuleId: "save-disk-dialog")));
        }),
        ("Semantic bridge rejects an ambiguous local action", () =>
        {
            AssertGuardRejected(SyntheticGameAssemblies.Create(
                Path.Combine(root, "semantic-duplicate"),
                new SyntheticGameOptions(DuplicateRuleId: "startup-preferences-build")));
        }),
        ("Semantic bridge rejects invalid IL stack behavior", () =>
        {
            AssertGuardRejected(SyntheticGameAssemblies.Create(
                Path.Combine(root, "semantic-stack"),
                new SyntheticGameOptions(InvalidStackRuleId: "rumble-activity")));
        }),
        ("Semantic bridge rejects missing GameHost runtime fields", () =>
        {
            AssertGuardRejected(SyntheticGameAssemblies.Create(
                Path.Combine(root, "semantic-required-field"),
                new SyntheticGameOptions(MissingRequiredFieldName: "xEdge")));
        }),
        ("Applied request rejects source and overlay path overlap", () =>
            AppliedWorkspacePreparationTests.RequestGuards(root)),
        ("Applied workspace rewrites once then uses cache", () =>
            AppliedWorkspacePreparationTests.BuiltThenCacheHit(root)),
        ("Applied cache rejects a null mutation before ordering", () =>
            AppliedWorkspacePreparationTests.NullMutationIsRejectedBeforeOrdering(root)),
        ("Cecil resolver checks its identity cache before reading the dependency again", () =>
            ValidatedExecutionPlanAssemblyResolverTests.CachePrecedesFileValidation(root)),
        ("Applied activation rejects a package identity race", () =>
            AppliedWorkspacePreparationTests.LiveIdentityRaceRejectsActivation(root)),
        ("Applied preparation rejects source manifest drift before rewrite", () =>
            AppliedWorkspacePreparationTests.SourceManifestDriftRejectsBeforeRewrite(root)),
    };
    var goldenDirectory = Environment.GetEnvironmentVariable("JUNIMOGATE_GOLDEN_GAME_DIR");
    if (!string.IsNullOrWhiteSpace(goldenDirectory))
    {
        var goldenVersionName = Environment.GetEnvironmentVariable("JUNIMOGATE_GOLDEN_VERSION_NAME") ?? "1.6.15.3";
        var goldenVersionCodeText = Environment.GetEnvironmentVariable("JUNIMOGATE_GOLDEN_VERSION_CODE");
        var goldenVersionCode = string.IsNullOrWhiteSpace(goldenVersionCodeText)
            ? 245
            : long.Parse(goldenVersionCodeText, System.Globalization.CultureInfo.InvariantCulture);
        tests.Add(($"Play {goldenVersionName}/{goldenVersionCode} remains a golden semantic regression fixture", () =>
        {
            var fixture = GoldenGameFixture.Create(
                goldenDirectory,
                Path.Combine(root, "golden"),
                goldenVersionName,
                goldenVersionCode);
            AssertSuccess(Rewrite(fixture));
        }));
    }
    return TestHarness.Run(tests.ToArray());
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // Temporary test output is harmless when the host prevents cleanup.
    }
}

static GameHostBridgeRewriteResult Rewrite(SyntheticGameFixture fixture) =>
    new GameHostBridgeAssemblyRewriter(fixture.Plan)
        .RewriteWithEvidenceAsync(new RewriteRequest(
            fixture.InputPath,
            fixture.OutputPath,
            GameHostBridgeRecipe.Identity))
        .AsTask().GetAwaiter().GetResult();

static void AssertSuccess(GameHostBridgeRewriteResult result)
{
    TestHarness.True(
        result.IsSuccess,
        string.Join(" | ", result.Rewrite.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message} {diagnostic.Detail}")));
    TestHarness.Equal(RewriteStatus.Succeeded, result.Rewrite.Status);
    TestHarness.Equal(GameHostBridgeRecipe.Rules.Length, result.Mutations.Length);
    TestHarness.True(result.Mutations.All(static mutation => mutation.PostconditionPassed));
    TestHarness.True(result.Rewrite.StagingOutputPath is not null && File.Exists(result.Rewrite.StagingOutputPath));
}

static void AssertGuardRejected(SyntheticGameFixture fixture)
{
    var result = Rewrite(fixture);
    TestHarness.False(result.IsSuccess);
    TestHarness.True(result.Rewrite.Diagnostics.Any(static diagnostic =>
        diagnostic.Code == GameHostBridgeRewriteDiagnosticCodes.GuardRejected));
    TestHarness.False(File.Exists(fixture.OutputPath));
}
