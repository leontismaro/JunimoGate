using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;
using JunimoGate.Tests;

namespace JunimoGate.Rewriter.Tests;

internal static class AppliedWorkspacePreparationTests
{
    public static void RequestGuards(string root)
    {
        var fixture = AppliedFixture.Create(Path.Combine(root, "applied-request-guards"));
        TestHarness.Throws<ArgumentException>(() => new GameHostAppliedWorkspacePreparationRequest(
            Path.Combine(fixture.WorkspacePath, "applied"),
            fixture.Candidate,
            fixture.Plan));
    }

    public static void BuiltThenCacheHit(string root)
    {
        var fixture = AppliedFixture.Create(Path.Combine(root, "applied-built-cache"));
        var rewriteCount = 0;
        var preparer = new GameHostAppliedWorkspacePreparer(
            new FixedRevalidator(fixture.Candidate),
            (plan, request, cancellationToken) =>
            {
                rewriteCount++;
                return FakeRewriteAsync(fixture, request, cancellationToken);
            });
        var request = new GameHostAppliedWorkspacePreparationRequest(
            fixture.AppliedRoot,
            fixture.Candidate,
            fixture.Plan);

        var built = preparer.PrepareAsync(request).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(GameHostAppliedWorkspacePreparationStatus.Built, built.Status);
        TestHarness.True(built.IsSuccess);
        TestHarness.True(built.Plan is not null);
        TestHarness.Equal(1, built.Metrics.RewriteCount);
        AssertAppliedFiles(built.Plan!);

        var cached = preparer.PrepareAsync(request).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(GameHostAppliedWorkspacePreparationStatus.CacheHit, cached.Status);
        TestHarness.True(cached.IsSuccess);
        TestHarness.Equal(built.Plan!.AppliedWorkspaceKey, cached.Plan!.AppliedWorkspaceKey);
        TestHarness.Equal(0, cached.Metrics.RewriteCount);
        TestHarness.Equal(1, rewriteCount);
        TestHarness.Equal(1, Directory.EnumerateDirectories(Path.Combine(fixture.AppliedRoot, "committed")).Count());
        TestHarness.Equal(0, Directory.EnumerateDirectories(Path.Combine(fixture.AppliedRoot, "staging")).Count());

        var state = WorkspaceJson.ReadBoundedAsync<GameHostAppliedWorkspaceState>(
                Path.Combine(fixture.AppliedRoot, GameHostAppliedWorkspaceContract.StateFileName),
                64 * 1024,
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(state is not null);
        TestHarness.Equal(built.Plan.AppliedWorkspaceKey, state!.ActiveKey);
        TestHarness.Equal<string?>(null, state.PreviousKey);
    }

    public static void NullMutationIsRejectedBeforeOrdering(string root)
    {
        var fixture = AppliedFixture.Create(Path.Combine(root, "applied-null-mutation"));
        var preparer = new GameHostAppliedWorkspacePreparer(
            new FixedRevalidator(fixture.Candidate),
            (_, request, cancellationToken) => FakeRewriteAsync(fixture, request, cancellationToken));
        var request = new GameHostAppliedWorkspacePreparationRequest(
            fixture.AppliedRoot,
            fixture.Candidate,
            fixture.Plan);
        var built = preparer.PrepareAsync(request).AsTask().GetAwaiter().GetResult();
        TestHarness.True(built.IsSuccess);

        var rewritePath = Path.Combine(
            built.Plan!.AppliedWorkspacePath,
            GameHostAppliedWorkspaceContract.RewriteManifestFileName);
        var rewrite = WorkspaceJson.ReadBoundedAsync<GameHostRewriteManifestV2>(
                rewritePath,
                1024 * 1024,
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(rewrite is not null);

        var malformedPath = Path.Combine(root, "rewrite-manifest-null-mutation.json");
        File.WriteAllText(
            malformedPath,
            JsonSerializer.Serialize(rewrite! with { Mutations = [null!] }, WorkspaceJson.Options));
        var malformed = WorkspaceJson.ReadBoundedAsync<GameHostRewriteManifestV2>(
                malformedPath,
                1024 * 1024,
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        TestHarness.True(malformed is not null);

        var result = GameHostAppliedWorkspaceValidator.ValidateRecipeResult(malformed!);
        TestHarness.False(result.IsValid);
        TestHarness.True(result.ErrorCodes.SequenceEqual([GameHostAppliedWorkspaceErrorCodes.RecipeMismatch]));
    }

    public static void LiveIdentityRaceRejectsActivation(string root)
    {
        var fixture = AppliedFixture.Create(Path.Combine(root, "applied-live-race"));
        var changed = fixture.WithVersionCode(fixture.Candidate.Installation.LongVersionCode + 1);
        var preparer = new GameHostAppliedWorkspacePreparer(
            new FixedRevalidator(changed),
            (_, request, cancellationToken) => FakeRewriteAsync(fixture, request, cancellationToken));
        var request = new GameHostAppliedWorkspacePreparationRequest(
            fixture.AppliedRoot,
            fixture.Candidate,
            fixture.Plan);

        var result = preparer.PrepareAsync(request).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(GameHostAppliedWorkspacePreparationStatus.Rejected, result.Status);
        TestHarness.True(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == GameHostAppliedWorkspaceDiagnosticCodes.LiveIdentityChanged));
        TestHarness.False(File.Exists(Path.Combine(
            fixture.AppliedRoot,
            GameHostAppliedWorkspaceContract.StateFileName)));
        TestHarness.Equal(1, Directory.EnumerateDirectories(Path.Combine(fixture.AppliedRoot, "committed")).Count());
        TestHarness.Equal(0, Directory.EnumerateDirectories(Path.Combine(fixture.AppliedRoot, "staging")).Count());
    }

    public static void SourceManifestDriftRejectsBeforeRewrite(string root)
    {
        var fixture = AppliedFixture.Create(Path.Combine(root, "applied-source-drift"));
        var sourceManifestPath = Path.Combine(fixture.WorkspacePath, WorkspaceManifestConstants.SourceManifestFileName);
        var source = File.ReadAllText(sourceManifestPath);
        File.WriteAllText(sourceManifestPath, source.Replace(
            fixture.Candidate.Installation.VersionName,
            "9.9.9",
            StringComparison.Ordinal));
        var rewriteCalled = false;
        var preparer = new GameHostAppliedWorkspacePreparer(
            new FixedRevalidator(fixture.Candidate),
            (_, request, cancellationToken) =>
            {
                rewriteCalled = true;
                return FakeRewriteAsync(fixture, request, cancellationToken);
            });
        var request = new GameHostAppliedWorkspacePreparationRequest(
            fixture.AppliedRoot,
            fixture.Candidate,
            fixture.Plan);

        var result = preparer.PrepareAsync(request).AsTask().GetAwaiter().GetResult();
        TestHarness.Equal(GameHostAppliedWorkspacePreparationStatus.Rejected, result.Status);
        TestHarness.False(rewriteCalled);
        TestHarness.Equal(0, result.Metrics.RewriteCount);
        TestHarness.True(result.Diagnostics.Any(diagnostic =>
            diagnostic.Code == GameHostAppliedWorkspaceDiagnosticCodes.SourceRejected));
        TestHarness.False(File.Exists(Path.Combine(
            fixture.AppliedRoot,
            GameHostAppliedWorkspaceContract.StateFileName)));
    }

    private static void AssertAppliedFiles(ValidatedGameHostAppliedWorkspacePlan plan)
    {
        var files = Directory.EnumerateFiles(plan.AppliedWorkspacePath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(plan.AppliedWorkspacePath, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        TestHarness.True(files.SequenceEqual([
            GameHostAppliedWorkspaceContract.AppliedManifestFileName,
            "overlay/assemblies/StardewValley.dll",
            GameHostAppliedWorkspaceContract.RewriteManifestFileName,
        ]));
        TestHarness.True(File.Exists(plan.OverlayAssemblyPath));
        TestHarness.False(File.Exists(Path.Combine(plan.AppliedWorkspacePath, "Content", "test.xnb")));
        TestHarness.False(File.Exists(Path.Combine(plan.AppliedWorkspacePath, "assemblies", "MonoGame.Framework.dll")));
    }

    private static async ValueTask<GameHostBridgeRewriteResult> FakeRewriteAsync(
        AppliedFixture fixture,
        RewriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputBytes = new byte[] { 0x4a, 0x47, 0x42, 0x52, 0x49, 0x44, 0x47, 0x45 };
        await File.WriteAllBytesAsync(request.StagingOutputPath, outputBytes, cancellationToken).ConfigureAwait(false);
        var outputDigest = Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(outputBytes)));
        var mutations = GameHostBridgeRecipe.Rules
            .Select(rule => new AppliedRewriteMutationEvidence(
                rule.RuleId,
                rule.InputRelativePath,
                rule.TargetMemberSignature,
                rule.ExpectedMatchCount,
                rule.ExpectedMatchCount,
                rule.Replacements,
                PostconditionPassed: true))
            .ToImmutableArray();
        var rewrite = new RewriteResult(
            RewriteStatus.Succeeded,
            request.StagingOutputPath,
            outputDigest,
            [new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                StartupStage.Rewrite,
                DiagnosticSeverity.Information,
                GameHostBridgeRewriteDiagnosticCodes.Succeeded,
                "Synthetic semantic-writer output for applied transaction tests.")]);
        return new GameHostBridgeRewriteResult(
            rewrite,
            Sha256Digest.Parse(fixture.StardewPayload.Sha256),
            "StardewValley, Version=1.6.99.0, Culture=neutral, PublicKeyToken=null",
            mutations);
    }

    private sealed class FixedRevalidator : IWorkspaceCandidateRevalidator
    {
        private readonly GameInstallationCandidate candidate;

        public FixedRevalidator(GameInstallationCandidate candidate)
        {
            this.candidate = candidate;
        }

        public ValueTask<GameInstallationCandidate?> RevalidateAsync(
            string packageName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<GameInstallationCandidate?>(candidate);
        }
    }

    private sealed class AppliedFixture
    {
        private AppliedFixture(
            string appliedRoot,
            string workspacePath,
            GameInstallationCandidate candidate,
            ValidatedExecutionPlan plan,
            ValidatedWorkspacePayload stardewPayload,
            string baseApkPath,
            string splitApkPath,
            Sha256Digest baseDigest,
            Sha256Digest splitDigest)
        {
            AppliedRoot = appliedRoot;
            WorkspacePath = workspacePath;
            Candidate = candidate;
            Plan = plan;
            StardewPayload = stardewPayload;
            BaseApkPath = baseApkPath;
            SplitApkPath = splitApkPath;
            BaseDigest = baseDigest;
            SplitDigest = splitDigest;
        }

        public string AppliedRoot { get; }
        public string WorkspacePath { get; }
        public GameInstallationCandidate Candidate { get; }
        public ValidatedExecutionPlan Plan { get; }
        public ValidatedWorkspacePayload StardewPayload { get; }
        private string BaseApkPath { get; }
        private string SplitApkPath { get; }
        private Sha256Digest BaseDigest { get; }
        private Sha256Digest SplitDigest { get; }

        public static AppliedFixture Create(string root)
        {
            Directory.CreateDirectory(root);
            var packageRoot = Path.Combine(root, "installed");
            Directory.CreateDirectory(packageRoot);
            var baseApkPath = Path.Combine(packageRoot, "base.apk");
            var splitApkPath = Path.Combine(packageRoot, "split.apk");
            File.WriteAllBytes(baseApkPath, [0x42, 0x41, 0x53, 0x45]);
            File.WriteAllBytes(splitApkPath, [0x53, 0x50, 0x4c, 0x49, 0x54]);
            var baseDigest = DigestFile(baseApkPath);
            var splitDigest = DigestFile(splitApkPath);
            var signing = new SigningIdentity([
                Sha256Digest.Parse(KnownGameCertificate.PlayCertificateSha256),
            ]);
            var sources = new[]
            {
                new ApkSourceIdentity(baseApkPath, baseDigest, new FileInfo(baseApkPath).Length, "base", null),
                new ApkSourceIdentity(splitApkPath, splitDigest, new FileInfo(splitApkPath).Length, "split-1", "config.arm64_v8a"),
            };
            var installation = new GameInstallationIdentity(
                KnownGameCertificate.PlayPackageName,
                "1.6.99",
                999,
                signing,
                GameInstallationDiscoveryCoordinator.SupportedAbi,
                sources);
            var candidate = new GameInstallationCandidate(
                installation,
                [
                    new ApkSourceInventory("base", [ApkSourceRoleNames.GameContent], [], []),
                    new ApkSourceInventory(
                        "split-1",
                        [ApkSourceRoleNames.ModernAssemblyBlob],
                        [GameInstallationDiscoveryCoordinator.SupportedAbi],
                        [GameInstallationDiscoveryCoordinator.SupportedAbi]),
                ]);
            var workspaceKey = WorkspaceCacheKey.Create(
                installation.PackageName,
                installation.LongVersionCode,
                installation.SelectedAbi,
                installation.SigningIdentity,
                installation.ApkSources.Select(static source => source.Digest),
                WorkspacePreparationOptions.DefaultExtractorSchema,
                WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
                "none").ToString();
            var workspacePath = Path.Combine(root, "source-workspace", workspaceKey);
            Directory.CreateDirectory(Path.Combine(workspacePath, "Content"));
            Directory.CreateDirectory(Path.Combine(workspacePath, "assemblies"));

            var contentPayload = WritePayload(workspacePath, "content", "Content/test.xnb", [0x01, 0x02, 0x03]);
            var stardewPayload = WritePayload(
                workspacePath,
                "assembly",
                GameHostBridgeRecipe.InputRelativePath,
                [0x53, 0x44, 0x56]);
            var monoGamePayload = WritePayload(
                workspacePath,
                "assembly",
                "assemblies/MonoGame.Framework.dll",
                [0x4d, 0x47]);
            var payloads = new[] { contentPayload, stardewPayload, monoGamePayload };

            WriteManifest(
                Path.Combine(workspacePath, WorkspaceManifestConstants.SourceManifestFileName),
                WorkspaceManifestValidator.CreateSourceManifest(
                    candidate,
                    workspaceKey,
                    WorkspacePreparationOptions.DefaultManifestSchema));
            var extractedFiles = new[]
            {
                new WorkspaceExtractedFileManifest(
                    "content",
                    contentPayload.RelativePath,
                    contentPayload.Size,
                    contentPayload.Sha256,
                    "base",
                    "assets/Content/test.xnb"),
                new WorkspaceExtractedFileManifest(
                    "assembly",
                    stardewPayload.RelativePath,
                    stardewPayload.Size,
                    stardewPayload.Sha256,
                    "split-1",
                    "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so"),
                new WorkspaceExtractedFileManifest(
                    "assembly",
                    monoGamePayload.RelativePath,
                    monoGamePayload.Size,
                    monoGamePayload.Sha256,
                    "split-1",
                    "lib/arm64-v8a/libassemblies.arm64-v8a.blob.so"),
            };
            WriteManifest(
                Path.Combine(workspacePath, WorkspaceManifestConstants.ExtractionManifestFileName),
                new WorkspaceExtractionManifest(
                    WorkspaceManifestConstants.ExtractionManifestFormat,
                    WorkspacePreparationOptions.DefaultManifestSchema,
                    workspaceKey,
                    WorkspacePreparationOptions.DefaultExtractorSchema,
                    WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
                    "none",
                    extractedFiles,
                    new WorkspaceExtractionStatistics(
                        ContentFileCount: 1,
                        ContentBytes: contentPayload.Size,
                        AssemblyFileCount: 2,
                        AssemblyBytes: stardewPayload.Size + monoGamePayload.Size)));
            WriteManifest(
                Path.Combine(workspacePath, WorkspaceManifestConstants.RewriteManifestFileName),
                new WorkspaceRewriteManifest(
                    WorkspaceManifestConstants.RewriteManifestFormat,
                    WorkspacePreparationOptions.DefaultManifestSchema,
                    workspaceKey,
                    WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
                    WorkspaceExecutionTrustDefaults.Gate0RewriteStatus));

            var identityDigest = WorkspaceExecutionValidator.CreateIdentityDigest(
                candidate,
                workspaceKey,
                WorkspacePreparationOptions.DefaultManifestSchema,
                WorkspacePreparationOptions.DefaultExtractorSchema,
                WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
                WorkspaceExecutionTrustDefaults.Gate0RewriteStatus,
                payloads);
            var plan = new ValidatedExecutionPlan(
                candidate.Installation.PackageName,
                candidate.Installation.VersionName,
                candidate.Installation.LongVersionCode,
                candidate.Installation.SelectedAbi,
                workspaceKey,
                workspacePath,
                identityDigest,
                DateTimeOffset.UtcNow,
                payloads);
            return new AppliedFixture(
                Path.Combine(root, "applied-root"),
                workspacePath,
                candidate,
                plan,
                stardewPayload,
                baseApkPath,
                splitApkPath,
                baseDigest,
                splitDigest);
        }

        public GameInstallationCandidate WithVersionCode(long versionCode)
        {
            var installation = new GameInstallationIdentity(
                Candidate.Installation.PackageName,
                Candidate.Installation.VersionName,
                versionCode,
                Candidate.Installation.SigningIdentity,
                Candidate.Installation.SelectedAbi,
                [
                    new ApkSourceIdentity(BaseApkPath, BaseDigest, new FileInfo(BaseApkPath).Length, "base", null),
                    new ApkSourceIdentity(SplitApkPath, SplitDigest, new FileInfo(SplitApkPath).Length, "split-1", "config.arm64_v8a"),
                ]);
            return new GameInstallationCandidate(installation, Candidate.SourceInventories);
        }

        private static ValidatedWorkspacePayload WritePayload(
            string workspacePath,
            string kind,
            string relativePath,
            byte[] bytes)
        {
            var path = Path.Combine(workspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return new ValidatedWorkspacePayload(
                kind,
                relativePath,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        private static void WriteManifest<T>(string path, T value) =>
            File.WriteAllText(path, JsonSerializer.Serialize(value, WorkspaceJson.Options));

        private static Sha256Digest DigestFile(string path) =>
            Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }
}
