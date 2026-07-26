using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;
using JunimoGate.Rewriter.Tests;
using JunimoGate.Tests;
using Mono.Cecil;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-rewriter-tests-{Guid.NewGuid():N}"));
Directory.CreateDirectory(root);

try
{
    var input = Path.Combine(root, "contracts", "input", "StardewValley.dll");
    var output = Path.Combine(root, "contracts", "staging", "StardewValley.dll");
    var recipe = new RewriteRecipeIdentity("android-activity-bridge", "1");

    return TestHarness.Run(
        ("Rewrite request accepts absolute input and staging output", () =>
        {
            var request = new RewriteRequest(input, output, recipe);
            TestHarness.Equal(input, request.InputAssemblyPath);
            TestHarness.Equal(output, request.StagingOutputPath);
            TestHarness.Equal("android-activity-bridge@1", request.Recipe.ToString());
        }),
        ("Rewrite request rejects relative input", () =>
        {
            TestHarness.Throws<ArgumentException>(() => new RewriteRequest("StardewValley.dll", output, recipe));
        }),
        ("Rewrite request rejects relative staging output", () =>
        {
            TestHarness.Throws<ArgumentException>(() => new RewriteRequest(input, "staging/StardewValley.dll", recipe));
        }),
        ("Rewrite request rejects in-place output", () =>
        {
            TestHarness.Throws<ArgumentException>(() => new RewriteRequest(input, input, recipe));
        }),
        ("Rewrite recipe identity is required and non-empty", () =>
        {
            TestHarness.Throws<ArgumentException>(() => new RewriteRecipeIdentity("", "1"));
            TestHarness.Throws<ArgumentException>(() => new RewriteRecipeIdentity("recipe", ""));
            TestHarness.Throws<ArgumentNullException>(() => new RewriteRequest(input, output, null!));
        }),
        ("Probe options enforce bounded absolute contained paths and snapshot inputs", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "options"));
            var paths = new List<string> { fixture.Target, fixture.Dependency };
            var options = new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, paths);
            paths.Clear();
            TestHarness.Equal(2, options.AssemblyPaths.Length);
            TestHarness.Throws<ArgumentException>(() => new GameHostCompatibilityProbeOptions("relative", fixture.Target, [fixture.Target]));
            TestHarness.Throws<ArgumentException>(() => new GameHostCompatibilityProbeOptions(fixture.Root, "relative.dll", [fixture.Target]));
            TestHarness.Throws<ArgumentException>(() => new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [Path.Combine(root, "outside.dll"), fixture.Target]));
            TestHarness.Throws<ArgumentException>(() => new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [fixture.Target, fixture.Target]));
            TestHarness.Throws<ArgumentOutOfRangeException>(() => new GameHostProbeLimits(maxAssemblies: 0));
            TestHarness.Throws<ArgumentOutOfRangeException>(() => new GameHostProbeLimits(maxInstructions: GameHostProbeLimits.MaximumInstructionLimit + 1));
        }),
        ("Metadata probe succeeds deterministically with canonical complete evidence", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "success"));
            var stagingPath = Path.Combine(fixture.Root, "staging", "StardewValley.dll");
            var beforeTarget = SHA256.HashData(File.ReadAllBytes(fixture.Target));
            var beforeDependency = SHA256.HashData(File.ReadAllBytes(fixture.Dependency));
            var probe = new GameHostCompatibilityProbe();
            var first = probe.Probe(new GameHostCompatibilityProbeOptions(
                fixture.Root,
                fixture.Target,
                [fixture.Target, fixture.Dependency]));
            var second = probe.Probe(new GameHostCompatibilityProbeOptions(
                fixture.Root,
                fixture.Target,
                [fixture.Dependency, fixture.Target]));

            TestHarness.Equal(GameHostProbeStatus.Succeeded, first.Status);
            TestHarness.True(first.IsSuccess);
            TestHarness.Equal(64, first.ManagedEvidenceKey!.Length);
            TestHarness.Equal(first.ManagedEvidenceKey, second.ManagedEvidenceKey);
            TestHarness.Equal(JsonSerializer.Serialize(first.Evidence), JsonSerializer.Serialize(second.Evidence));
            TestHarness.Equal("gamehost_probe_succeeded", first.Diagnostics.Single().Code);

            var evidence = first.Evidence!;
            TestHarness.Equal(GameHostCompatibilityProbe.SchemaVersion, evidence.SchemaVersion);
            TestHarness.True(evidence.TargetAssembly.Identity.StartsWith("StardewValley, Version=1.6.15.3", StringComparison.Ordinal));
            TestHarness.Equal("11111111-2222-3333-4444-555555555555", evidence.TargetAssembly.ModuleVersionId);
            TestHarness.Equal(".NETCoreApp,Version=v9.0", evidence.TargetAssembly.TargetFramework);
            TestHarness.True(evidence.TargetAssembly.References.Length >= 2);
            TestHarness.Equal("Android.App.Activity", evidence.MainActivity.BaseType);
            TestHarness.Equal("StardewValley.MainActivity StardewValley.MainActivity::instance", evidence.MainActivity.InstanceFieldSignature);
            TestHarness.Equal(5, evidence.MainActivity.LifecycleMethodSignatures.Length);
            TestHarness.Equal(2, evidence.MainActivity.BootstrapMethodSignatures.Length);
            TestHarness.Equal(4, evidence.FieldUseCounts.Total);
            TestHarness.Equal(2, evidence.FieldUseCounts.Read);
            TestHarness.Equal(1, evidence.FieldUseCounts.Write);
            TestHarness.Equal(1, evidence.FieldUseCounts.Address);
            TestHarness.Equal(0, evidence.FieldUseCounts.Other);
            TestHarness.Equal(3, evidence.CallSiteCount);
            TestHarness.Equal(2, evidence.PInvokes.Length);
            TestHarness.Equal("libSDL2-2.0.so.0", evidence.PInvokes[0].ModuleName);
            TestHarness.Equal("SDL_Init", evidence.PInvokes[0].EntryPoint);
            TestHarness.Equal("cdecl", evidence.PInvokes[0].CallingConvention);
            TestHarness.Equal("unicode", evidence.PInvokes[0].CharacterSet);
            TestHarness.Equal("libgame.so", evidence.PInvokes[1].ModuleName);
            TestHarness.True(evidence.InteropAttributes.Length >= 1);
            TestHarness.True(evidence.InteropAttributes[0].ArgumentFingerprints[0].StartsWith("blob:", StringComparison.Ordinal));

            var serialized = JsonSerializer.Serialize(first);
            TestHarness.False(serialized.Contains(fixture.Root, StringComparison.Ordinal), "Probe output exposed an input path.");
            TestHarness.False(serialized.Contains("com/chucklefish", StringComparison.Ordinal), "Probe output exposed an arbitrary metadata string.");
            TestHarness.False(serialized.Contains("Instructions", StringComparison.Ordinal), "Probe output contained an IL body dump.");
            TestHarness.False(File.Exists(stagingPath), "The metadata-only probe created staging output.");
            TestHarness.True(beforeTarget.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Target))), "The target assembly was modified.");
            TestHarness.True(beforeDependency.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Dependency))), "A dependency assembly was modified.");
        }),
        ("Probe fingerprints unresolved external enum attributes without assembly resolution", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(
                Path.Combine(root, "unresolved-interop-enum"),
                new SyntheticGameOptions(IncludeUnresolvedInteropEnumAttribute: true));
            TestHarness.False(File.Exists(Path.Combine(fixture.Root, "Missing.Android.Contracts.dll")));

            var result = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(
                    fixture.Root,
                    fixture.Target,
                    [fixture.Target, fixture.Dependency]));

            TestHarness.Equal(GameHostProbeStatus.Succeeded, result.Status);
            TestHarness.True(result.IsSuccess);
            var attribute = result.Evidence!.InteropAttributes.Single(evidence =>
                evidence.AttributeType.Equals(
                    "Android.Runtime.UnresolvedModeAttribute",
                    StringComparison.Ordinal));
            TestHarness.Equal(1, attribute.ArgumentFingerprints.Length);
            TestHarness.True(
                System.Text.RegularExpressions.Regex.IsMatch(
                    attribute.ArgumentFingerprints[0],
                    "^blob:[0-9]+:[0-9a-f]{64}$"));
            TestHarness.True(result.Evidence.TargetAssembly.References.Any(reference =>
                reference.Identity.StartsWith(
                    "Missing.Android.Contracts, Version=1.0.0.0",
                    StringComparison.Ordinal)));
        }),
        ("Probe reports missing MainActivity with a stable diagnostic", () =>
        {
            AssertFailure(
                Path.Combine(root, "missing-main"),
                new SyntheticGameOptions(IncludeMainActivity: false),
                "gamehost_probe_main_activity_missing");
        }),
        ("Probe reports duplicate MainActivity with a stable diagnostic", () =>
        {
            AssertFailure(
                Path.Combine(root, "duplicate-main"),
                new SyntheticGameOptions(DuplicateMainActivity: true),
                "gamehost_probe_main_activity_duplicate");
        }),
        ("Probe reports missing instance with a stable diagnostic", () =>
        {
            AssertFailure(
                Path.Combine(root, "missing-instance"),
                new SyntheticGameOptions(IncludeInstanceField: false),
                "gamehost_probe_instance_missing");
        }),
        ("Probe reports duplicate instance with a stable diagnostic", () =>
        {
            AssertFailure(
                Path.Combine(root, "duplicate-instance"),
                new SyntheticGameOptions(DuplicateInstanceField: true),
                "gamehost_probe_instance_duplicate");
        }),
        ("Probe rejects a non-static instance field", () =>
        {
            AssertFailure(
                Path.Combine(root, "non-static-instance"),
                new SyntheticGameOptions(StaticInstanceField: false),
                "gamehost_probe_instance_signature_invalid");
        }),
        ("Probe reports malformed managed metadata without exposing paths", () =>
        {
            var fixtureRoot = Path.Combine(root, "malformed");
            Directory.CreateDirectory(fixtureRoot);
            var malformed = Path.Combine(fixtureRoot, "StardewValley.dll");
            File.WriteAllBytes(malformed, [0x4a, 0x47, 0x00, 0xff]);
            var result = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(fixtureRoot, malformed, [malformed]));
            AssertDiagnostic(result, GameHostProbeStatus.Failed, "gamehost_probe_assembly_malformed");
            TestHarness.False(JsonSerializer.Serialize(result).Contains(fixtureRoot, StringComparison.Ordinal));
        }),
        ("Probe reports an unreadable or missing assembly without exposing paths", () =>
        {
            var fixtureRoot = Path.Combine(root, "unreadable");
            Directory.CreateDirectory(fixtureRoot);
            var missing = Path.Combine(fixtureRoot, "StardewValley.dll");
            var result = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(fixtureRoot, missing, [missing]));
            AssertDiagnostic(result, GameHostProbeStatus.Failed, "gamehost_probe_assembly_unreadable");
            TestHarness.True(result.Diagnostics.Single().Message.Contains("StardewValley.dll", StringComparison.Ordinal));
            TestHarness.True(result.Diagnostics.Single().Message.Contains("FileNotFoundException", StringComparison.Ordinal));
            TestHarness.False(JsonSerializer.Serialize(result).Contains(fixtureRoot, StringComparison.Ordinal));
        }),
        ("Probe identifies a malformed dependency by safe basename without partial evidence", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "malformed-dependency"));
            File.WriteAllBytes(fixture.Dependency, [0x4a, 0x47, 0x00, 0xff]);
            var result = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(
                    fixture.Root,
                    fixture.Target,
                    [fixture.Target, fixture.Dependency]));
            AssertDiagnostic(result, GameHostProbeStatus.Failed, "gamehost_probe_assembly_malformed");
            TestHarness.True(result.Diagnostics.Single().Message.Contains("GameDependency.dll", StringComparison.Ordinal));
            TestHarness.False(result.Diagnostics.Single().Message.Contains(fixture.Root, StringComparison.Ordinal));
        }),
        ("Probe cancellation returns a stable redacted diagnostic", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "cancelled"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [fixture.Target, fixture.Dependency]),
                cancellation.Token);
            AssertDiagnostic(result, GameHostProbeStatus.Cancelled, "gamehost_probe_cancelled");
        }),
        ("Probe fails closed when bounded metadata limits are exceeded", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "bounded"));
            var limits = new GameHostProbeLimits(maxInstructions: 1);
            var result = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [fixture.Target, fixture.Dependency], limits));
            AssertDiagnostic(result, GameHostProbeStatus.Failed, "gamehost_probe_metadata_limit_exceeded");
        }),
        ("Composite support key is deterministic and binds managed ABI and native identities", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "support-key"));
            var probe = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [fixture.Target, fixture.Dependency]));
            TestHarness.True(probe.IsSuccess);
            TestHarness.True(probe.ManagedEvidenceKey is { Length: 64 });

            var native = new[]
            {
                Native("split-2", "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so", 'b'),
                Native("base", "lib/arm64-v8a/libgame.so", 'c'),
                Native("split-1", "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so", 'a'),
            };
            var key = GameHostSupportKey.Create(probe.Evidence!, "arm64-v8a", native);
            var reordered = GameHostSupportKey.Create(probe.Evidence!, "arm64-v8a", native.Reverse());
            TestHarness.Equal(64, key.Length);
            TestHarness.Equal(key, reordered);
            TestHarness.False(key.Equals(probe.ManagedEvidenceKey, StringComparison.Ordinal));

            var sourceChanged = native.ToArray();
            sourceChanged[0] = sourceChanged[0] with { SourceLabel = "split-3" };
            TestHarness.False(key.Equals(
                GameHostSupportKey.Create(probe.Evidence!, "arm64-v8a", sourceChanged),
                StringComparison.Ordinal));

            var hashChanged = native.ToArray();
            hashChanged[1] = hashChanged[1] with { Sha256 = new string('d', 64) };
            TestHarness.False(key.Equals(
                GameHostSupportKey.Create(probe.Evidence!, "arm64-v8a", hashChanged),
                StringComparison.Ordinal));

            var managedChanged = probe.Evidence! with
            {
                TargetAssembly = probe.Evidence.TargetAssembly with
                {
                    ModuleVersionId = "99999999-2222-3333-4444-555555555555",
                },
            };
            TestHarness.False(key.Equals(
                GameHostSupportKey.Create(managedChanged, "arm64-v8a", native),
                StringComparison.Ordinal));
        }),
        ("Composite support key rejects malformed or inconsistent evidence", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "support-key-invalid"));
            var probe = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [fixture.Target, fixture.Dependency]));
            var evidence = probe.Evidence!;
            var valid = Native("split-1", "lib/arm64-v8a/libgame.so", 'a');

            TestHarness.Throws<ArgumentException>(() => GameHostSupportKey.Create(evidence, "x86_64", [valid]));
            TestHarness.Throws<ArgumentException>(() => GameHostSupportKey.Create(evidence, "arm64-v8a", []));
            TestHarness.Throws<ArgumentException>(() => GameHostSupportKey.Create(
                evidence,
                "arm64-v8a",
                [valid, valid]));
            TestHarness.Throws<ArgumentException>(() => GameHostSupportKey.Create(
                evidence,
                "arm64-v8a",
                [valid, valid with { SourceLabel = "split-2", EntryPath = "LIB/arm64-v8a/libgame.so" }]));
            TestHarness.Throws<ArgumentException>(() => GameHostSupportKey.Create(
                evidence,
                "arm64-v8a",
                [valid with { Sha256 = new string('A', 64) }]));
            TestHarness.Throws<ArgumentException>(() => GameHostSupportKey.Create(
                evidence,
                "arm64-v8a",
                [valid with { Machine = 62 }]));
            TestHarness.Throws<ArgumentException>(() => GameHostSupportKey.Create(
                evidence with { CallSiteCount = evidence.CallSiteCount + 1 },
                "arm64-v8a",
                [valid]));
        }),
        ("Gate 2 catalog approves the exact trusted-installed-source bridge recipe", () =>
        {
            var entry = GameHostRecipeCatalog.KnownEntries.Single();
            TestHarness.Equal(GameHostRecipeCatalog.TestedPlaySupportKey, entry.SupportKey);
            TestHarness.Equal(GameHostRecipeCatalog.TestedPlayManagedEvidenceKey, entry.ManagedEvidenceKey);
            TestHarness.Equal(GameHostEntitlementPolicy.TrustedInstalledSource, entry.EntitlementPolicy);
            TestHarness.Equal(GameHostRecipeEligibilityStatus.Approved, entry.Status);
            TestHarness.Equal(GameHostRecipeDecisionCodes.Approved, entry.DecisionCode);
            TestHarness.Equal(GameHostBridgeRecipe.Identity, entry.ApprovedRecipe);
            TestHarness.Equal(13, entry.ApprovedMutations.Length);
            TestHarness.Equal(14, entry.ApprovedMutations.Sum(static mutation => mutation.ExpectedMatchCount));
            TestHarness.True(entry.ApprovedMutations.SequenceEqual(GameHostBridgeRecipe.ApprovedMutations));
            TestHarness.Equal(18, entry.Guard.FieldUseCounts.Total);
            TestHarness.Equal(0, entry.Guard.FieldUseCounts.Address);
            TestHarness.Equal(3, entry.Guard.EntitlementProtectedFieldUseMethods.Length);
            TestHarness.True(entry.Guard.EntitlementProtectedFieldUseMethods.All(method =>
                method.Contains("+LicensingChecker::", StringComparison.Ordinal)));
        }),
        ("Gate 2 bridge recipe freezes 13 method guards for exactly 14 non-licensing reads", () =>
        {
            var mutations = GameHostBridgeRecipe.ApprovedMutations;
            TestHarness.Equal(13, mutations.Length);
            TestHarness.Equal(14, mutations.Sum(static mutation => mutation.ExpectedMatchCount));
            TestHarness.Equal(13, mutations.Select(static mutation => mutation.MutationId).Distinct(StringComparer.Ordinal).Count());
            TestHarness.True(mutations.All(static mutation =>
                mutation.InputRelativePath == GameHostBridgeRecipe.InputRelativePath &&
                mutation.PreconditionSha256.Length == 64 &&
                mutation.PostconditionSha256.Length == 64 &&
                mutation.PreconditionSha256 != mutation.PostconditionSha256 &&
                mutation.EntitlementBehavior == AppliedEntitlementBehavior.Preserved));
            TestHarness.True(mutations.All(static mutation =>
                !mutation.TargetMemberSignature.Contains("LicensingChecker", StringComparison.Ordinal) &&
                !mutation.TargetMemberSignature.Contains("MainActivity::OnCreate", StringComparison.Ordinal)));
            TestHarness.Equal("play-1.6.15.3-gamehost-bridge@1", GameHostBridgeRecipe.Identity.ToString());
        }),
        ("Bridge writer construction requires the exact catalog capability mutation set and trusted plan", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "bridge-writer-construction"));
            var planFixture = CreateValidatedExecutionPlan(fixture.Root, fixture.Target);
            var blocked = new GameHostRecipeDecision(
                GameHostRecipeEligibilityStatus.BlockedPendingBridgeRecipe,
                GameHostRecipeDecisionCodes.BridgeRecipePending,
                GameHostRecipeCatalog.TestedPlaySupportKey,
                GameHostEntitlementPolicy.TrustedInstalledSource,
                null,
                []);
            TestHarness.Throws<InvalidOperationException>(() =>
                new GameHostBridgeAssemblyRewriter(blocked, planFixture.Plan));

            var forged = ApprovedBridgeDecision();
            var forgedMutations = forged.ApprovedMutations.SetItem(
                0,
                forged.ApprovedMutations[0] with { PostconditionSha256 = new string('b', 64) });
            var forgedDecision = new GameHostRecipeDecision(
                GameHostRecipeEligibilityStatus.Approved,
                GameHostRecipeDecisionCodes.Approved,
                GameHostRecipeCatalog.TestedPlaySupportKey,
                GameHostEntitlementPolicy.TrustedInstalledSource,
                GameHostBridgeRecipe.Identity,
                forgedMutations);
            TestHarness.Throws<InvalidOperationException>(() =>
                new GameHostBridgeAssemblyRewriter(forgedDecision, planFixture.Plan));

            var wrongIdentityPlan = CreateValidatedExecutionPlan(
                fixture.Root,
                fixture.Target,
                packageName: "invalid.package");
            TestHarness.Throws<InvalidOperationException>(() =>
                new GameHostBridgeAssemblyRewriter(ApprovedBridgeDecision(), wrongIdentityPlan.Plan));
        }),
        ("Bridge writer rejects wrong assembly identity without creating a staging output", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "bridge-writer-wrong-identity"));
            var planFixture = CreateValidatedExecutionPlan(fixture.Root, fixture.Target);
            var staging = Path.Combine(fixture.Root, "staging");
            Directory.CreateDirectory(staging);
            var outputPath = Path.Combine(staging, "StardewValley.dll");
            var writer = new GameHostBridgeAssemblyRewriter(ApprovedBridgeDecision(), planFixture.Plan);
            var result = writer.RewriteWithEvidenceAsync(new RewriteRequest(
                planFixture.InputPath,
                outputPath,
                GameHostBridgeRecipe.Identity)).AsTask().GetAwaiter().GetResult();
            TestHarness.False(result.IsSuccess);
            TestHarness.Equal(GameHostBridgeRewriteDiagnosticCodes.InputRejected, result.Rewrite.Diagnostics.Single().Code);
            TestHarness.False(File.Exists(outputPath));
            TestHarness.Equal(0, result.Mutations.Length);
        }),
        ("Bridge writer rejects plan digest output and cancellation guards without partial files", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "bridge-writer-guards"));
            var planFixture = CreateValidatedExecutionPlan(fixture.Root, fixture.Target);
            var staging = Path.Combine(fixture.Root, "staging");
            Directory.CreateDirectory(staging);
            var outputPath = Path.Combine(staging, "StardewValley.dll");

            var wrongDigestPlan = CreateValidatedExecutionPlan(
                fixture.Root,
                fixture.Target,
                payloadSha256: new string('a', 64));
            var wrongDigestWriter = new GameHostBridgeAssemblyRewriter(
                ApprovedBridgeDecision(),
                wrongDigestPlan.Plan);
            var digestResult = wrongDigestWriter.RewriteWithEvidenceAsync(new RewriteRequest(
                wrongDigestPlan.InputPath,
                outputPath,
                GameHostBridgeRecipe.Identity)).AsTask().GetAwaiter().GetResult();
            TestHarness.Equal(GameHostBridgeRewriteDiagnosticCodes.InputRejected, digestResult.Rewrite.Diagnostics.Single().Code);
            TestHarness.False(File.Exists(outputPath));

            File.WriteAllText(outputPath, "owned-existing-output");
            var outputWriter = new GameHostBridgeAssemblyRewriter(ApprovedBridgeDecision(), planFixture.Plan);
            var outputResult = outputWriter.RewriteWithEvidenceAsync(new RewriteRequest(
                planFixture.InputPath,
                outputPath,
                GameHostBridgeRecipe.Identity)).AsTask().GetAwaiter().GetResult();
            TestHarness.Equal(GameHostBridgeRewriteDiagnosticCodes.OutputRejected, outputResult.Rewrite.Diagnostics.Single().Code);
            TestHarness.Equal("owned-existing-output", File.ReadAllText(outputPath));
            File.Delete(outputPath);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = outputWriter.RewriteWithEvidenceAsync(new RewriteRequest(
                planFixture.InputPath,
                outputPath,
                GameHostBridgeRecipe.Identity), cancellation.Token).AsTask().GetAwaiter().GetResult();
            TestHarness.Equal(GameHostBridgeRewriteDiagnosticCodes.Cancelled, cancelled.Rewrite.Diagnostics.Single().Code);
            TestHarness.False(File.Exists(outputPath));
        }),
        ("Validated execution plan resolver allowlists identities and rechecks cached dependency hashes", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "bridge-resolver-allowlist"));
            var planFixture = CreateValidatedExecutionPlan(
                fixture.Root,
                fixture.Target,
                additionalAssemblyPaths: [fixture.Dependency]);
            using var expectedAssembly = AssemblyDefinition.ReadAssembly(fixture.Dependency);
            var expectedName = new AssemblyNameReference(
                expectedAssembly.Name.Name,
                expectedAssembly.Name.Version);

            using var resolver = new ValidatedExecutionPlanAssemblyResolver(planFixture.Plan);
            var resolved = resolver.Resolve(expectedName);
            TestHarness.Equal(expectedAssembly.Name.FullName, resolved.Name.FullName);

            var dependencyPath = Path.Combine(
                planFixture.Plan.WorkspacePath,
                "assemblies",
                Path.GetFileName(fixture.Dependency));
            var bytes = File.ReadAllBytes(dependencyPath);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(dependencyPath, bytes);
            TestHarness.Throws<InvalidDataException>(() => resolver.Resolve(expectedName));
        }),
        ("Validated execution plan resolver rejects absent and mismatched dependency identities", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "bridge-resolver-reject"));
            var planFixture = CreateValidatedExecutionPlan(
                fixture.Root,
                fixture.Target,
                additionalAssemblyPaths: [fixture.Dependency]);
            using var resolver = new ValidatedExecutionPlanAssemblyResolver(planFixture.Plan);

            TestHarness.Throws<AssemblyResolutionException>(() => resolver.Resolve(
                new AssemblyNameReference("NotAllowlisted", new Version(1, 0, 0, 0))));
            TestHarness.Throws<InvalidDataException>(() => resolver.Resolve(
                new AssemblyNameReference("GameDependency", new Version(9, 9, 9, 9))));
        }),
        ("Gate 2 catalog rejects mismatched and unsupported synthetic support evidence", () =>
        {
            var fixture = SyntheticGameAssemblies.Create(Path.Combine(root, "recipe-catalog"));
            var probe = new GameHostCompatibilityProbe().Probe(
                new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [fixture.Target, fixture.Dependency]));
            var native = new[] { Native("split-1", "lib/arm64-v8a/libgame.so", 'a') };
            var supportKey = GameHostSupportKey.Create(probe.Evidence!, "arm64-v8a", native);

            var unsupported = GameHostRecipeCatalog.Evaluate(
                supportKey,
                probe.ManagedEvidenceKey!,
                probe.Evidence!,
                "arm64-v8a",
                native);
            TestHarness.Equal(GameHostRecipeEligibilityStatus.UnsupportedSupportKey, unsupported.Status);
            TestHarness.False(unsupported.CanRewrite);
            TestHarness.Equal<RewriteRecipeIdentity?>(null, unsupported.Recipe);
            TestHarness.Equal(0, unsupported.ApprovedMutations.Length);

            var mismatch = GameHostRecipeCatalog.Evaluate(
                new string('0', 64),
                probe.ManagedEvidenceKey!,
                probe.Evidence!,
                "arm64-v8a",
                native);
            TestHarness.Equal(GameHostRecipeEligibilityStatus.EvidenceMismatch, mismatch.Status);
            TestHarness.False(mismatch.CanRewrite);
        }),
        ("Original payload identity is deterministic and rejects ambiguous paths", () =>
        {
            var payloads = new[]
            {
                new OriginalPayloadIdentity("assembly", "assemblies/StardewValley.dll", 100, new string('a', 64)),
                new OriginalPayloadIdentity("content", "Content/Data/game.xnb", 200, new string('b', 64)),
            };
            var first = OriginalPayloadSetIdentity.Create(payloads);
            var second = OriginalPayloadSetIdentity.Create(payloads.Reverse());
            TestHarness.Equal(first, second);
            TestHarness.Equal(2, first.FileCount);
            TestHarness.Equal(300L, first.TotalBytes);
            TestHarness.Equal(64, first.Digest.Length);
            TestHarness.Throws<ArgumentException>(() => OriginalPayloadSetIdentity.Create([payloads[0], payloads[0]]));
            TestHarness.Throws<ArgumentException>(() => OriginalPayloadSetIdentity.Create([
                payloads[0],
                payloads[0] with { RelativePath = "assemblies/STARDEWVALLEY.dll" },
            ]));
            TestHarness.Throws<ArgumentException>(() => OriginalPayloadSetIdentity.Create([
                payloads[0] with { RelativePath = "../StardewValley.dll" },
            ]));
        }),
        ("Applied workspace key and manifest shape bind exact inputs mutations and outputs", () =>
        {
            var fixture = CreateAppliedFixture();
            var result = GameHostAppliedWorkspaceValidator.ValidateShape(
                fixture.Rewrite,
                fixture.Applied,
                fixture.OriginalPayloads,
                fixture.ActualFiles);
            TestHarness.True(result.IsValid, string.Join(",", result.ErrorCodes));
            TestHarness.Equal(0, result.ErrorCodes.Length);
            TestHarness.Equal(64, fixture.Rewrite.AppliedWorkspaceKey.Length);

            var reorderedKey = GameHostAppliedWorkspaceKey.Create(
                fixture.Rewrite.Source,
                fixture.Rewrite.SupportKey,
                fixture.Rewrite.Recipe,
                fixture.Rewrite.Tool,
                fixture.Rewrite.Inputs.Reverse(),
                fixture.Rewrite.Mutations.Reverse(),
                fixture.Rewrite.Outputs.Reverse());
            TestHarness.Equal(fixture.Rewrite.AppliedWorkspaceKey, reorderedKey);

            var outputChanged = fixture.Rewrite.Outputs.Single() with { Sha256 = new string('8', 64) };
            var changedKey = GameHostAppliedWorkspaceKey.Create(
                fixture.Rewrite.Source,
                fixture.Rewrite.SupportKey,
                fixture.Rewrite.Recipe,
                fixture.Rewrite.Tool,
                fixture.Rewrite.Inputs,
                fixture.Rewrite.Mutations,
                [outputChanged]);
            TestHarness.False(fixture.Rewrite.AppliedWorkspaceKey.Equals(changedKey, StringComparison.Ordinal));
        }),
        ("Applied workspace validation rejects licensing mutation failed postconditions and extra files", () =>
        {
            var fixture = CreateAppliedFixture();
            var licensingMutation = fixture.Rewrite.Mutations.Single() with
            {
                TargetMemberSignature =
                    "instance;callconv=Default;generic-arity=0;System.Void StardewValley.MainActivity+LicensingChecker::Allow(System.String)",
            };
            var licensingRewrite = fixture.Rewrite with { Mutations = [licensingMutation] };
            var licensing = GameHostAppliedWorkspaceValidator.ValidateShape(
                licensingRewrite,
                fixture.Applied,
                fixture.OriginalPayloads,
                fixture.ActualFiles);
            TestHarness.False(licensing.IsValid);
            TestHarness.True(licensing.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.EntitlementNotPreserved));

            var failedPost = fixture.Rewrite with
            {
                PostValidation = fixture.Rewrite.PostValidation with { PostconditionsPassed = false },
            };
            var post = GameHostAppliedWorkspaceValidator.ValidateShape(
                failedPost,
                fixture.Applied,
                fixture.OriginalPayloads,
                fixture.ActualFiles);
            TestHarness.False(post.IsValid);
            TestHarness.True(post.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.PostValidationFailed));

            var extra = GameHostAppliedWorkspaceValidator.ValidateShape(
                fixture.Rewrite,
                fixture.Applied,
                fixture.OriginalPayloads,
                fixture.ActualFiles.Append("overlay/assemblies/unexpected.dll"));
            TestHarness.False(extra.IsValid);
            TestHarness.True(extra.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.FileSetMismatch));
        }),
        ("Applied workspace validation binds original payload membership and complete mutation coverage", () =>
        {
            var fixture = CreateAppliedFixture();
            var changedOriginals = fixture.OriginalPayloads
                .Select(payload => payload.RelativePath == "assemblies/StardewValley.dll"
                    ? payload with { Sha256 = new string('d', 64) }
                    : payload)
                .ToArray();
            var changedSource = GameHostAppliedWorkspaceValidator.ValidateShape(
                fixture.Rewrite,
                fixture.Applied,
                changedOriginals,
                fixture.ActualFiles);
            TestHarness.False(changedSource.IsValid);
            TestHarness.True(changedSource.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.SourceBindingInvalid));
            TestHarness.True(changedSource.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.InputInvalid));

            var changedInput = fixture.Rewrite.Inputs.Single() with { Sha256 = new string('e', 64) };
            var inputMismatch = GameHostAppliedWorkspaceValidator.ValidateShape(
                fixture.Rewrite with { Inputs = [changedInput] },
                fixture.Applied,
                fixture.OriginalPayloads,
                fixture.ActualFiles);
            TestHarness.False(inputMismatch.IsValid);
            TestHarness.True(inputMismatch.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.InputInvalid));

            var caseCollision = GameHostAppliedWorkspaceValidator.ValidateShape(
                fixture.Rewrite,
                fixture.Applied,
                fixture.OriginalPayloads,
                fixture.ActualFiles.Append("overlay/assemblies/STARDEWVALLEY.dll"));
            TestHarness.False(caseCollision.IsValid);
            TestHarness.True(caseCollision.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.FileSetMismatch));

            var secondInput = new AppliedRewriteInputIdentity(
                "assemblies/MonoGame.Framework.dll",
                "MonoGame.Framework, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                "22222222-3333-4444-5555-666666666666",
                200,
                new string('f', 64));
            var secondOutput = new AppliedRewriteOutputIdentity(
                secondInput.RelativePath,
                "overlay/assemblies/MonoGame.Framework.dll",
                secondInput.AssemblyIdentity,
                secondInput.ModuleVersionId,
                AppliedModuleVersionIdPolicy.Preserve,
                201,
                new string('0', 64));
            var extendedSource = fixture.Rewrite.Source with
            {
                OriginalPayloadSet = OriginalPayloadSetIdentity.Create(
                    fixture.OriginalPayloads.Append(new OriginalPayloadIdentity(
                        "assembly",
                        secondInput.RelativePath,
                        secondInput.Size,
                        secondInput.Sha256))),
            };
            TestHarness.Throws<ArgumentException>(() => GameHostAppliedWorkspaceKey.Create(
                extendedSource,
                fixture.Rewrite.SupportKey,
                fixture.Rewrite.Recipe,
                fixture.Rewrite.Tool,
                fixture.Rewrite.Inputs.Append(secondInput),
                fixture.Rewrite.Mutations,
                fixture.Rewrite.Outputs.Append(secondOutput)));
        }),
        ("Applied authorization compares complete catalog-owned mutation guards", () =>
        {
            var fixture = CreateAppliedFixture();
            var mutation = fixture.Rewrite.Mutations.Single();
            var approvedMutation = new GameHostApprovedMutationContract(
                mutation.MutationId,
                mutation.InputRelativePath,
                mutation.TargetMemberSignature,
                mutation.ExpectedMatchCount,
                mutation.PreconditionSha256,
                mutation.PostconditionSha256,
                mutation.EntitlementBehavior);
            var approved = new GameHostRecipeDecision(
                GameHostRecipeEligibilityStatus.Approved,
                GameHostRecipeDecisionCodes.Approved,
                fixture.Rewrite.SupportKey,
                GameHostEntitlementPolicy.TrustedInstalledSource,
                fixture.Rewrite.Recipe,
                [approvedMutation]);
            var accepted = GameHostAppliedWorkspaceValidator.ValidateAuthorization(fixture.Rewrite, approved);
            TestHarness.True(accepted.IsValid, string.Join(",", accepted.ErrorCodes));

            var missingPolicy = new GameHostRecipeDecision(
                GameHostRecipeEligibilityStatus.Approved,
                GameHostRecipeDecisionCodes.Approved,
                fixture.Rewrite.SupportKey,
                null,
                fixture.Rewrite.Recipe,
                [approvedMutation]);
            var policyRejected = GameHostAppliedWorkspaceValidator.ValidateAuthorization(
                fixture.Rewrite,
                missingPolicy);
            TestHarness.False(policyRejected.IsValid);
            TestHarness.True(policyRejected.ErrorCodes.SequenceEqual([
                GameHostAppliedWorkspaceErrorCodes.RecipeNotApproved,
            ]));

            var forgedMutation = mutation with
            {
                TargetMemberSignature =
                    "instance;callconv=Default;generic-arity=0;System.Void StardewValley.MainActivity::OnResume()",
            };
            var forged = GameHostAppliedWorkspaceValidator.ValidateAuthorization(
                fixture.Rewrite with { Mutations = [forgedMutation] },
                approved);
            TestHarness.False(forged.IsValid);
            TestHarness.True(forged.ErrorCodes.SequenceEqual([
                GameHostAppliedWorkspaceErrorCodes.RecipeNotApproved,
            ]));
        }),
        ("Current Gate 2 catalog cannot authorize a bridge without an exact recipe", () =>
        {
            var fixture = CreateAppliedFixture();
            var blocked = new GameHostRecipeDecision(
                GameHostRecipeEligibilityStatus.BlockedPendingBridgeRecipe,
                GameHostRecipeDecisionCodes.BridgeRecipePending,
                GameHostRecipeCatalog.TestedPlaySupportKey,
                GameHostEntitlementPolicy.TrustedInstalledSource,
                null,
                []);
            var authorization = GameHostAppliedWorkspaceValidator.ValidateAuthorization(fixture.Rewrite, blocked);
            TestHarness.False(authorization.IsValid);
            TestHarness.True(authorization.ErrorCodes.SequenceEqual([
                GameHostAppliedWorkspaceErrorCodes.RecipeNotApproved,
            ]));
        }),
        ("Applied workspace recovery retains state and cleans only owned pending directories", () =>
        {
            var active = new string('a', 64);
            var previous = new string('b', 64);
            var orphan = new string('c', 64);
            var ownedPending = $"pending-{new string('d', 32)}";
            var state = new GameHostAppliedWorkspaceState(
                GameHostAppliedWorkspaceContract.StateFormat,
                GameHostAppliedWorkspaceContract.StateSchema,
                active,
                previous);
            var plan = GameHostAppliedWorkspaceRecoveryPlanner.Create(
                state,
                [orphan, previous, active],
                ["keep-me", ownedPending, $"pending-{new string('E', 32)}"]);
            TestHarness.True(plan.IsValid);
            TestHarness.Equal(active, plan.ActiveKey);
            TestHarness.Equal(previous, plan.PreviousKey);
            TestHarness.True(plan.OwnedStagingDirectoriesToDelete.SequenceEqual([ownedPending]));
            TestHarness.True(plan.OrphanedCommittedKeysToQuarantine.SequenceEqual([orphan]));

            var missingActive = GameHostAppliedWorkspaceRecoveryPlanner.Create(
                state,
                [previous, orphan],
                [ownedPending]);
            TestHarness.False(missingActive.IsValid);
            TestHarness.Equal(0, missingActive.OwnedStagingDirectoriesToDelete.Length);
            TestHarness.Equal(0, missingActive.OrphanedCommittedKeysToQuarantine.Length);

            var noState = GameHostAppliedWorkspaceRecoveryPlanner.Create(
                null,
                [orphan, active],
                [ownedPending]);
            TestHarness.True(noState.IsValid);
            TestHarness.True(noState.OrphanedCommittedKeysToQuarantine.SequenceEqual([active, orphan]));
        }),
        ("Applied workspace validator fails closed for deserialized null contract members", () =>
        {
            var fixture = CreateAppliedFixture();
            var malformedRewrite = fixture.Rewrite with
            {
                Source = null!,
                Recipe = null!,
                Tool = null!,
                Inputs = null!,
                Mutations = null!,
                Outputs = null!,
                PostValidation = null!,
            };
            var malformedApplied = fixture.Applied with
            {
                Recipe = null!,
                OverlayFiles = null!,
            };
            var result = GameHostAppliedWorkspaceValidator.ValidateShape(
                malformedRewrite,
                malformedApplied,
                [],
                []);
            TestHarness.False(result.IsValid);
            TestHarness.True(result.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.ManifestInvalid));
            TestHarness.True(result.ErrorCodes.Contains(GameHostAppliedWorkspaceErrorCodes.SourceBindingInvalid));
        }),
        ("Activity bridge probe options require bounded contained inputs and parent support identity", () =>
        {
            var fixture = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "bridge-options"));
            var parent = GameHostRecipeCatalog.TestedPlaySupportKey;
            var options = new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                fixture.Game,
                [fixture.Game],
                parent);
            TestHarness.Equal(parent, options.ParentSupportKey);
            TestHarness.Equal(fixture.MonoGame, options.MonoGameAssemblyPath);
            TestHarness.Equal(fixture.Game, options.GameAssemblyPath);
            TestHarness.Equal(1, options.ConsumerAssemblyPaths.Length);
            TestHarness.Equal(fixture.Game, options.ConsumerAssemblyPaths[0]);
            TestHarness.Throws<ArgumentException>(() => new ActivityBridgeCompatibilityProbeOptions(
                "relative",
                fixture.MonoGame,
                fixture.Game,
                [fixture.Game],
                parent));
            TestHarness.Throws<ArgumentException>(() => new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                Path.Combine(root, "outside.dll"),
                [fixture.Game],
                parent));
            TestHarness.Throws<ArgumentException>(() => new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                fixture.MonoGame,
                [fixture.Game],
                parent));
            TestHarness.Throws<ArgumentException>(() => new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                fixture.Game,
                [fixture.Game],
                parent.ToUpperInvariant()));
            TestHarness.Throws<ArgumentOutOfRangeException>(() => new ActivityBridgeProbeLimits(maxTypes: 0));
            TestHarness.Throws<ArgumentOutOfRangeException>(() => new ActivityBridgeProbeLimits(
                maxMembers: ActivityBridgeProbeLimits.MaximumMemberLimit + 1));
        }),
        ("Activity bridge metadata probe is deterministic complete and read-only", () =>
        {
            var fixture = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "bridge-success"));
            var beforeMonoGame = SHA256.HashData(File.ReadAllBytes(fixture.MonoGame));
            var beforeGame = SHA256.HashData(File.ReadAllBytes(fixture.Game));
            var probe = new ActivityBridgeCompatibilityProbe();
            var first = probe.Probe(new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                fixture.Game,
                [fixture.Game],
                GameHostRecipeCatalog.TestedPlaySupportKey));
            var second = probe.Probe(new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                fixture.Game,
                [fixture.Game],
                GameHostRecipeCatalog.TestedPlaySupportKey));

            TestHarness.Equal(ActivityBridgeProbeStatus.Succeeded, first.Status);
            TestHarness.True(first.IsSuccess);
            TestHarness.Equal(64, first.EvidenceKey!.Length);
            TestHarness.Equal(first.EvidenceKey, second.EvidenceKey);
            TestHarness.Equal(JsonSerializer.Serialize(first.Evidence), JsonSerializer.Serialize(second.Evidence));
            TestHarness.Equal("gamehost_bridge_probe_succeeded", first.Diagnostics.Single().Code);

            var evidence = first.Evidence!;
            TestHarness.Equal(ActivityBridgeCompatibilityProbe.SchemaVersion, evidence.SchemaVersion);
            TestHarness.Equal(GameHostRecipeCatalog.TestedPlaySupportKey, evidence.ParentSupportKey);
            TestHarness.True(evidence.MonoGameAssembly.Identity.StartsWith(
                "MonoGame.Framework, Version=1.0.0.0",
                StringComparison.Ordinal));
            TestHarness.Equal("22222222-3333-4444-5555-666666666666", evidence.MonoGameAssembly.ModuleVersionId);
            TestHarness.True(evidence.GameAssembly.Identity.StartsWith(
                "StardewValley, Version=1.6.15.3",
                StringComparison.Ordinal));
            TestHarness.Equal("77777777-8888-9999-aaaa-bbbbbbbbbbbb", evidence.GameAssembly.ModuleVersionId);
            TestHarness.Equal(".NETCoreApp,Version=v9.0", evidence.MonoGameAssembly.TargetFramework);
            TestHarness.Equal("Android.App.Activity", evidence.MonoGame.AndroidGameActivity.BaseType);
            TestHarness.Equal(5, evidence.MonoGame.AndroidGameActivity.LifecycleMethodSignatures.Length);
            TestHarness.True(evidence.MonoGame.AndroidGameActivity.InteropAttributes.Single()
                .ArgumentFingerprints.Single().StartsWith("blob:", StringComparison.Ordinal));
            TestHarness.Equal(1, evidence.MonoGame.GameRunMethodSignatures.Length);
            TestHarness.Equal(1, evidence.MonoGame.GameExitMethodSignatures.Length);
            TestHarness.Equal(1, evidence.MonoGame.GameServicesPropertySignatures.Length);
            TestHarness.Equal(1, evidence.MonoGame.GetServiceMethodSignatures.Length);
            TestHarness.Equal("Microsoft.Xna.Framework.Game", evidence.GameRunner.Type.BaseType);
            TestHarness.Equal(1, evidence.GameRunner.StaticInstanceFieldSignatures.Length);
            TestHarness.Equal("Microsoft.Xna.Framework.AndroidGameActivity", evidence.MainActivity.Type.BaseType);

            var onCreate = evidence.MainActivity.LifecycleBodies.Single(body =>
                body.MethodSignature.Contains("::OnCreate(", StringComparison.Ordinal));
            TestHarness.True(onCreate.InstructionCount > 0);
            TestHarness.True(onCreate.Calls.Any(call =>
                call.CalledMethodSignature.Contains("Microsoft.Xna.Framework.AndroidGameActivity::OnCreate(", StringComparison.Ordinal)));
            TestHarness.True(onCreate.Calls.Any(call =>
                call.OpCode == "newobj" &&
                call.CalledMethodSignature.Contains("StardewValley.GameRunner::.ctor(", StringComparison.Ordinal)));
            TestHarness.True(onCreate.Calls.Any(call =>
                call.CalledMethodSignature.Contains("StardewValley.MainActivity::CheckAppPermissions(", StringComparison.Ordinal)));
            TestHarness.True(onCreate.Calls.Any(call =>
                call.CalledMethodSignature.Contains("Microsoft.Xna.Framework.Game::Run(", StringComparison.Ordinal)));
            TestHarness.True(onCreate.Fields.Any(field =>
                field.OpCode == "stsfld" &&
                field.FieldSignature.Contains("StardewValley.GameRunner::instance", StringComparison.Ordinal)));

            var changedParent = probe.Probe(new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                fixture.Game,
                [fixture.Game],
                new string('a', 64)));
            TestHarness.True(changedParent.IsSuccess);
            TestHarness.False(first.EvidenceKey.Equals(changedParent.EvidenceKey, StringComparison.Ordinal));

            var serialized = JsonSerializer.Serialize(first);
            TestHarness.False(serialized.Contains(fixture.Root, StringComparison.Ordinal));
            TestHarness.False(serialized.Contains("microsoft/xna/framework", StringComparison.Ordinal));
            TestHarness.False(serialized.Contains("Instructions", StringComparison.Ordinal));
            TestHarness.True(beforeMonoGame.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.MonoGame))));
            TestHarness.True(beforeGame.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Game))));
            TestHarness.False(Directory.Exists(Path.Combine(fixture.Root, "staging")));
        }),
        ("Activity bridge probe rejects missing and duplicate required types", () =>
        {
            var cases = new[]
            {
                new SyntheticActivityBridgeOptions(IncludeAndroidGameActivity: false),
                new SyntheticActivityBridgeOptions(IncludeGameRunner: false),
                new SyntheticActivityBridgeOptions(IncludeMainActivity: false),
            };
            foreach (var options in cases)
            {
                var fixture = SyntheticActivityBridgeAssemblies.Create(
                    Path.Combine(root, $"bridge-missing-{Guid.NewGuid():N}"),
                    options);
                AssertBridgeDiagnostic(
                    new ActivityBridgeCompatibilityProbe().Probe(new ActivityBridgeCompatibilityProbeOptions(
                        fixture.Root,
                        fixture.MonoGame,
                        fixture.Game,
                        [fixture.Game],
                        GameHostRecipeCatalog.TestedPlaySupportKey)),
                    ActivityBridgeProbeStatus.Failed,
                    "gamehost_bridge_probe_type_missing");
            }

            var duplicate = SyntheticActivityBridgeAssemblies.Create(
                Path.Combine(root, "bridge-duplicate"),
                new SyntheticActivityBridgeOptions(DuplicateAndroidGameActivity: true));
            AssertBridgeDiagnostic(
                new ActivityBridgeCompatibilityProbe().Probe(new ActivityBridgeCompatibilityProbeOptions(
                    duplicate.Root,
                    duplicate.MonoGame,
                    duplicate.Game,
                    [duplicate.Game],
                    GameHostRecipeCatalog.TestedPlaySupportKey)),
                ActivityBridgeProbeStatus.Failed,
                "gamehost_bridge_probe_type_duplicate");
        }),
        ("Activity bridge probe rejects structurally incomplete service and runner contracts", () =>
        {
            foreach (var options in new[]
                     {
                         new SyntheticActivityBridgeOptions(IncludeGameRunnerInstance: false),
                         new SyntheticActivityBridgeOptions(IncludeGameServicesProperty: false),
                     })
            {
                var fixture = SyntheticActivityBridgeAssemblies.Create(
                    Path.Combine(root, $"bridge-incomplete-{Guid.NewGuid():N}"),
                    options);
                AssertBridgeDiagnostic(
                    new ActivityBridgeCompatibilityProbe().Probe(new ActivityBridgeCompatibilityProbeOptions(
                        fixture.Root,
                        fixture.MonoGame,
                        fixture.Game,
                        [fixture.Game],
                        GameHostRecipeCatalog.TestedPlaySupportKey)),
                    ActivityBridgeProbeStatus.Failed,
                    "gamehost_bridge_probe_contract_incomplete");
            }
        }),
        ("Activity bridge probe records an absent lifecycle Run call without inventing evidence", () =>
        {
            var fixture = SyntheticActivityBridgeAssemblies.Create(
                Path.Combine(root, "bridge-no-run"),
                new SyntheticActivityBridgeOptions(IncludeRunCall: false));
            var result = new ActivityBridgeCompatibilityProbe().Probe(new ActivityBridgeCompatibilityProbeOptions(
                fixture.Root,
                fixture.MonoGame,
                fixture.Game,
                [fixture.Game],
                GameHostRecipeCatalog.TestedPlaySupportKey));
            TestHarness.True(result.IsSuccess);
            var onCreate = result.Evidence!.MainActivity.LifecycleBodies.Single(body =>
                body.MethodSignature.Contains("::OnCreate(", StringComparison.Ordinal));
            TestHarness.False(onCreate.Calls.Any(call =>
                call.CalledMethodSignature.Contains("::Run(", StringComparison.Ordinal)));
        }),
        ("Activity bridge probe cancellation and all metadata bounds fail closed", () =>
        {
            var fixture = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "bridge-bounds"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            AssertBridgeDiagnostic(
                new ActivityBridgeCompatibilityProbe().Probe(new ActivityBridgeCompatibilityProbeOptions(
                    fixture.Root,
                    fixture.MonoGame,
                    fixture.Game,
                    [fixture.Game],
                    GameHostRecipeCatalog.TestedPlaySupportKey), cancellation.Token),
                ActivityBridgeProbeStatus.Cancelled,
                "gamehost_bridge_probe_cancelled");

            foreach (var limits in new[]
                     {
                         new ActivityBridgeProbeLimits(maxTypes: 1),
                         new ActivityBridgeProbeLimits(maxMembers: 1),
                         new ActivityBridgeProbeLimits(maxInstructions: 1),
                         new ActivityBridgeProbeLimits(maxEvidenceItems: 1),
                     })
            {
                AssertBridgeDiagnostic(
                    new ActivityBridgeCompatibilityProbe().Probe(new ActivityBridgeCompatibilityProbeOptions(
                        fixture.Root,
                        fixture.MonoGame,
                        fixture.Game,
                        [fixture.Game],
                        GameHostRecipeCatalog.TestedPlaySupportKey,
                        limits)),
                    ActivityBridgeProbeStatus.Failed,
                    "gamehost_bridge_probe_metadata_limit_exceeded");
            }
        }),
        ("Activity bridge probe reports malformed and missing inputs without path or partial evidence", () =>
        {
            var malformed = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "bridge-malformed"));
            File.WriteAllBytes(malformed.MonoGame, [0x4a, 0x47, 0x00, 0xff]);
            var malformedResult = new ActivityBridgeCompatibilityProbe().Probe(
                new ActivityBridgeCompatibilityProbeOptions(
                    malformed.Root,
                    malformed.MonoGame,
                    malformed.Game,
                    [malformed.Game],
                    GameHostRecipeCatalog.TestedPlaySupportKey));
            AssertBridgeDiagnostic(
                malformedResult,
                ActivityBridgeProbeStatus.Failed,
                "gamehost_bridge_probe_assembly_malformed");
            TestHarness.True(malformedResult.Diagnostics.Single().Message.Contains(
                "MonoGame.Framework.dll",
                StringComparison.Ordinal));
            TestHarness.False(JsonSerializer.Serialize(malformedResult).Contains(malformed.Root, StringComparison.Ordinal));

            var missing = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "bridge-missing-input"));
            File.Delete(missing.Game);
            var missingResult = new ActivityBridgeCompatibilityProbe().Probe(
                new ActivityBridgeCompatibilityProbeOptions(
                    missing.Root,
                    missing.MonoGame,
                    missing.Game,
                    [missing.Game],
                    GameHostRecipeCatalog.TestedPlaySupportKey));
            AssertBridgeDiagnostic(
                missingResult,
                ActivityBridgeProbeStatus.Failed,
                "gamehost_bridge_probe_assembly_unreadable");
            TestHarness.True(missingResult.Diagnostics.Single().Message.Contains(
                "StardewValley.dll",
                StringComparison.Ordinal));
            TestHarness.False(JsonSerializer.Serialize(missingResult).Contains(missing.Root, StringComparison.Ordinal));
        }),
        ("Managed API compatibility accepts a provider superset without whole-surface equality", () =>
        {
            var consumer = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "api-compat-consumer"));
            var providerSuperset = SyntheticActivityBridgeAssemblies.Create(
                Path.Combine(root, "api-compat-superset"),
                new SyntheticActivityBridgeOptions(IncludeExtraMonoGameApi: true));
            var requirements = ManagedApiCompatibilityInspector.InspectRequirements(
                [consumer.Game],
                "MonoGame.Framework");
            var exactProvider = ManagedApiCompatibilityInspector.InspectProvider(
                consumer.MonoGame,
                "MonoGame.Framework");
            var supersetProvider = ManagedApiCompatibilityInspector.InspectProvider(
                providerSuperset.MonoGame,
                "MonoGame.Framework");
            var exact = ManagedApiCompatibilityInspector.Evaluate(requirements, exactProvider);
            var superset = ManagedApiCompatibilityInspector.Evaluate(requirements, supersetProvider);

            TestHarness.True(exact.IsCompatible);
            TestHarness.True(superset.IsCompatible);
            TestHarness.True(requirements.TypeRequirementHashes.Length > 0);
            TestHarness.True(requirements.MemberRequirementHashes.Length > 0);
            TestHarness.Equal(0, exact.MissingTypeCount);
            TestHarness.Equal(0, exact.MissingMemberCount);
            TestHarness.Equal(0, superset.MissingTypeCount);
            TestHarness.Equal(0, superset.MissingMemberCount);
            TestHarness.False(exactProvider.ProviderKey.Equals(supersetProvider.ProviderKey, StringComparison.Ordinal));

            var exactSurface = ManagedPublicApiSurfaceInspector.Inspect(consumer.MonoGame);
            var supersetSurface = ManagedPublicApiSurfaceInspector.Inspect(providerSuperset.MonoGame);
            TestHarness.False(exactSurface.SurfaceKey.Equals(supersetSurface.SurfaceKey, StringComparison.Ordinal));
            var serialized = JsonSerializer.Serialize(new { requirements, superset });
            TestHarness.False(serialized.Contains(consumer.Root, StringComparison.Ordinal));
            TestHarness.False(serialized.Contains("AndroidGameActivity", StringComparison.Ordinal));
            TestHarness.False(serialized.Contains("Game::Run", StringComparison.Ordinal));
        }),
        ("Managed API compatibility normalizes constructed generic declaring types", () =>
        {
            var fixture = SyntheticActivityBridgeAssemblies.Create(
                Path.Combine(root, "api-compat-generic"),
                new SyntheticActivityBridgeOptions(IncludeConstructedGenericConsumer: true));
            var requirements = ManagedApiCompatibilityInspector.InspectRequirements(
                [fixture.Game],
                "MonoGame.Framework");
            var provider = ManagedApiCompatibilityInspector.InspectProvider(
                fixture.MonoGame,
                "MonoGame.Framework");
            var result = ManagedApiCompatibilityInspector.Evaluate(requirements, provider);

            TestHarness.True(result.IsCompatible);
            TestHarness.Equal(0, result.MissingTypeCount);
            TestHarness.Equal(0, result.MissingMemberCount);
        }),
        ("Managed API compatibility rejects missing required provider types and members", () =>
        {
            var consumer = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "api-compat-missing-consumer"));
            var requirements = ManagedApiCompatibilityInspector.InspectRequirements(
                [consumer.Game],
                "MonoGame.Framework");

            var missingType = SyntheticActivityBridgeAssemblies.Create(
                Path.Combine(root, "api-compat-missing-type"),
                new SyntheticActivityBridgeOptions(IncludeAndroidGameActivity: false));
            var typeResult = ManagedApiCompatibilityInspector.Evaluate(
                requirements,
                ManagedApiCompatibilityInspector.InspectProvider(missingType.MonoGame, "MonoGame.Framework"));
            TestHarness.False(typeResult.IsCompatible);
            TestHarness.True(typeResult.MissingTypeCount > 0);

            var missingMember = SyntheticActivityBridgeAssemblies.Create(
                Path.Combine(root, "api-compat-missing-member"),
                new SyntheticActivityBridgeOptions(IncludeMonoGameRunMethod: false));
            var memberResult = ManagedApiCompatibilityInspector.Evaluate(
                requirements,
                ManagedApiCompatibilityInspector.InspectProvider(missingMember.MonoGame, "MonoGame.Framework"));
            TestHarness.False(memberResult.IsCompatible);
            TestHarness.True(memberResult.MissingMemberCount > 0);
            TestHarness.True(memberResult.MissingMemberHashes.All(hash => hash.Length == 64));
        }),
        ("Managed API compatibility is deterministic bounded and rejects forged evidence", () =>
        {
            var fixture = SyntheticActivityBridgeAssemblies.Create(Path.Combine(root, "api-compat-guards"));
            var first = ManagedApiCompatibilityInspector.InspectRequirements(
                [fixture.Game, fixture.Game],
                "MonoGame.Framework");
            var second = ManagedApiCompatibilityInspector.InspectRequirements(
                [fixture.Game],
                "MonoGame.Framework");
            TestHarness.Equal(first.RequirementsKey, second.RequirementsKey);
            TestHarness.Equal(1, first.ConsumerAssemblyCount);
            TestHarness.True(first.TypeRequirementHashes.SequenceEqual(second.TypeRequirementHashes));
            TestHarness.True(first.MemberRequirementHashes.SequenceEqual(second.MemberRequirementHashes));

            var provider = ManagedApiCompatibilityInspector.InspectProvider(
                fixture.MonoGame,
                "MonoGame.Framework");
            TestHarness.Throws<ArgumentException>(() => ManagedApiCompatibilityInspector.Evaluate(
                first with { RequirementsKey = new string('A', 64) },
                provider));
            TestHarness.Throws<ArgumentException>(() => ManagedApiCompatibilityInspector.Evaluate(
                first,
                provider with { TargetAssemblyName = "Other.Framework" }));
            TestHarness.Throws<ArgumentException>(() => ManagedApiCompatibilityInspector.InspectRequirements(
                ["relative.dll"],
                "MonoGame.Framework"));
            TestHarness.Throws<ArgumentOutOfRangeException>(() => new ManagedApiCompatibilityLimits(maxInstructions: 0));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            TestHarness.Throws<OperationCanceledException>(() => ManagedApiCompatibilityInspector.InspectRequirements(
                [fixture.Game],
                "MonoGame.Framework",
                cancellationToken: cancellation.Token));
            TestHarness.Throws<InvalidDataException>(() => ManagedApiCompatibilityInspector.InspectRequirements(
                [fixture.Game],
                "MonoGame.Framework",
                new ManagedApiCompatibilityLimits(maxInstructions: 1)));
        }),
        ("Applied workspace request rejects overlap and blocked capability", () =>
            AppliedWorkspacePreparationTests.RequestGuards(Path.Combine(root, "applied-request-tests"))),
        ("Applied workspace builds then exact-cache revalidates without copying originals", () =>
            AppliedWorkspacePreparationTests.BuiltThenCacheHit(Path.Combine(root, "applied-built-cache-tests"))),
        ("Applied workspace live package race rejects state activation", () =>
            AppliedWorkspacePreparationTests.LiveIdentityRaceRejectsActivation(Path.Combine(root, "applied-live-race-tests"))),
        ("Applied workspace source manifest drift stops before rewrite", () =>
            AppliedWorkspacePreparationTests.SourceManifestDriftRejectsBeforeRewrite(Path.Combine(root, "applied-source-drift-tests"))));
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static GameHostRecipeDecision ApprovedBridgeDecision() =>
    new(
        GameHostRecipeEligibilityStatus.Approved,
        GameHostRecipeDecisionCodes.Approved,
        GameHostRecipeCatalog.TestedPlaySupportKey,
        GameHostEntitlementPolicy.TrustedInstalledSource,
        GameHostBridgeRecipe.Identity,
        GameHostBridgeRecipe.ApprovedMutations);

static ValidatedPlanFixture CreateValidatedExecutionPlan(
    string root,
    string sourceAssemblyPath,
    string? payloadSha256 = null,
    string? packageName = null,
    IEnumerable<string>? additionalAssemblyPaths = null)
{
    var workspacePath = Path.Combine(root, $"validated-workspace-{Guid.NewGuid():N}");
    var assemblyDirectory = Path.Combine(workspacePath, "assemblies");
    Directory.CreateDirectory(assemblyDirectory);
    var inputPath = Path.Combine(assemblyDirectory, "StardewValley.dll");
    File.Copy(sourceAssemblyPath, inputPath, overwrite: false);

    var payloads = new List<ValidatedWorkspacePayload>
    {
        new(
            "assembly",
            GameHostBridgeRecipe.InputRelativePath,
            new FileInfo(inputPath).Length,
            payloadSha256 ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(inputPath)))),
    };
    if (additionalAssemblyPaths is not null)
    {
        foreach (var sourcePath in additionalAssemblyPaths)
        {
            var fileName = Path.GetFileName(sourcePath);
            var relativePath = $"assemblies/{fileName}";
            var destinationPath = Path.Combine(assemblyDirectory, fileName);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            payloads.Add(new ValidatedWorkspacePayload(
                "assembly",
                relativePath,
                new FileInfo(destinationPath).Length,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(destinationPath)))));
        }
    }

    var plan = new ValidatedExecutionPlan(
        packageName ?? GameHostRecipeCatalog.TestedPlayPackageName,
        GameHostRecipeCatalog.TestedPlayVersionName,
        GameHostRecipeCatalog.TestedPlayLongVersionCode,
        GameHostRecipeCatalog.TestedPlayAbi,
        GameHostRecipeCatalog.TestedPlaySupportKey,
        workspacePath,
        new string('d', 64),
        DateTimeOffset.UtcNow,
        payloads);
    return new ValidatedPlanFixture(plan, inputPath);
}

static AppliedContractFixture CreateAppliedFixture()
{
    var originalPayloads = new[]
    {
        new OriginalPayloadIdentity("assembly", "assemblies/StardewValley.dll", 100, new string('7', 64)),
        new OriginalPayloadIdentity("content", "Content/Data/game.xnb", 200, new string('2', 64)),
    };
    var payloadSet = OriginalPayloadSetIdentity.Create(originalPayloads);
    var source = new AppliedSourceWorkspaceBinding(
        new string('3', 64),
        new string('4', 64),
        new string('5', 64),
        new string('6', 64),
        payloadSet);
    var recipe = new RewriteRecipeIdentity("synthetic-activity-bridge", "1");
    var tool = new AppliedRewriterToolIdentity(
        "gate2-contract-tests",
        GameHostAppliedWorkspaceContract.PinnedMonoCecilVersion);
    var input = new AppliedRewriteInputIdentity(
        "assemblies/StardewValley.dll",
        "StardewValley, Version=1.6.15.3, Culture=neutral, PublicKeyToken=null",
        "11111111-2222-3333-4444-555555555555",
        100,
        new string('7', 64));
    var mutation = new AppliedRewriteMutationEvidence(
        "activity-bridge.synthetic",
        input.RelativePath,
        "instance;callconv=Default;generic-arity=0;System.Void StardewValley.MainActivity::OnCreate(Android.OS.Bundle)",
        1,
        1,
        new string('8', 64),
        new string('9', 64),
        AppliedEntitlementBehavior.Preserved);
    var output = new AppliedRewriteOutputIdentity(
        input.RelativePath,
        "overlay/assemblies/StardewValley.dll",
        input.AssemblyIdentity,
        input.ModuleVersionId,
        AppliedModuleVersionIdPolicy.Preserve,
        101,
        new string('a', 64));
    var supportKey = new string('b', 64);
    var appliedKey = GameHostAppliedWorkspaceKey.Create(
        source,
        supportKey,
        recipe,
        tool,
        [input],
        [mutation],
        [output]);
    var rewrite = new GameHostRewriteManifestV2(
        GameHostAppliedWorkspaceContract.RewriteManifestFormat,
        GameHostAppliedWorkspaceContract.RewriteManifestSchema,
        appliedKey,
        source,
        supportKey,
        recipe,
        GameHostAppliedWorkspaceContract.RewriteStatusApplied,
        tool,
        [input],
        [mutation],
        [output],
        new AppliedRewritePostValidation(
            GameHostAppliedWorkspaceContract.PostValidationPassed,
            ReopenedWithIndependentReader: true,
            InputGuardsPassed: true,
            PostconditionsPassed: true,
            AssemblyIdentityPassed: true,
            ReferenceClosurePassed: true));
    var applied = new GameHostAppliedWorkspaceManifest(
        GameHostAppliedWorkspaceContract.AppliedManifestFormat,
        GameHostAppliedWorkspaceContract.AppliedManifestSchema,
        appliedKey,
        source.WorkspaceKey,
        supportKey,
        recipe,
        new string('c', 64),
        payloadSet.Digest,
        [new AppliedOverlayFileIdentity(
            "managed-assembly",
            output.OverlayRelativePath,
            output.Size,
            output.Sha256)]);
    return new AppliedContractFixture(
        rewrite,
        applied,
        originalPayloads,
        [
            GameHostAppliedWorkspaceContract.AppliedManifestFileName,
            GameHostAppliedWorkspaceContract.RewriteManifestFileName,
            output.OverlayRelativePath,
        ]);
}

static GameHostNativeEvidence Native(
    string sourceLabel,
    string entryPath,
    char digestCharacter) =>
    new(
        sourceLabel,
        entryPath,
        4096,
        new string(digestCharacter, 64),
        2,
        1,
        1,
        0,
        0,
        3,
        183,
        0);

static void AssertFailure(string root, SyntheticGameOptions options, string expectedCode)
{
    var fixture = SyntheticGameAssemblies.Create(root, options);
    var result = new GameHostCompatibilityProbe().Probe(
        new GameHostCompatibilityProbeOptions(fixture.Root, fixture.Target, [fixture.Target, fixture.Dependency]));
    AssertDiagnostic(result, GameHostProbeStatus.Failed, expectedCode);
    TestHarness.False(JsonSerializer.Serialize(result).Contains(fixture.Root, StringComparison.Ordinal));
}

static void AssertBridgeDiagnostic(
    ActivityBridgeCompatibilityProbeResult result,
    ActivityBridgeProbeStatus expectedStatus,
    string expectedCode)
{
    TestHarness.Equal(expectedStatus, result.Status);
    TestHarness.False(result.IsSuccess);
    TestHarness.Equal<string?>(null, result.EvidenceKey);
    TestHarness.Equal<ActivityBridgeCompatibilityEvidence?>(null, result.Evidence);
    TestHarness.Equal(expectedCode, result.Diagnostics.Single().Code);
    TestHarness.True(result.Diagnostics.Single().Code.StartsWith("gamehost_bridge_probe_", StringComparison.Ordinal));
}

static void AssertDiagnostic(GameHostCompatibilityProbeResult result, GameHostProbeStatus expectedStatus, string expectedCode)
{
    TestHarness.Equal(expectedStatus, result.Status);
    TestHarness.False(result.IsSuccess);
    TestHarness.Equal<string?>(null, result.ManagedEvidenceKey);
    TestHarness.Equal<GameHostCompatibilityEvidence?>(null, result.Evidence);
    TestHarness.Equal(expectedCode, result.Diagnostics.Single().Code);
    TestHarness.True(result.Diagnostics.Single().Code.StartsWith("gamehost_probe_", StringComparison.Ordinal));
}

sealed record ValidatedPlanFixture(
    ValidatedExecutionPlan Plan,
    string InputPath);

sealed record AppliedContractFixture(
    GameHostRewriteManifestV2 Rewrite,
    GameHostAppliedWorkspaceManifest Applied,
    IReadOnlyList<OriginalPayloadIdentity> OriginalPayloads,
    IReadOnlyList<string> ActualFiles);
