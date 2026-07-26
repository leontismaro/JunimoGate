using System.Collections.Immutable;
using System.Security.Cryptography;
using JunimoGate.Core;
using JunimoGate.Extraction;
using Mono.Cecil;

namespace JunimoGate.Rewriter;

public static class GameHostBridgeRewriteDiagnosticCodes
{
    public const string Succeeded = "gamehost_bridge_rewrite_succeeded";
    public const string Cancelled = "gamehost_bridge_rewrite_cancelled";
    public const string RequestRejected = "gamehost_bridge_rewrite_request_rejected";
    public const string InputRejected = "gamehost_bridge_rewrite_input_rejected";
    public const string DependencyRejected = "gamehost_bridge_rewrite_dependency_rejected";
    public const string GuardRejected = "gamehost_bridge_rewrite_guard_rejected";
    public const string OutputRejected = "gamehost_bridge_rewrite_output_rejected";
    public const string WriteFailed = "gamehost_bridge_rewrite_write_failed";
}

/// <summary>Exact mutation evidence produced by the guarded bridge writer.</summary>
public sealed record GameHostBridgeRewriteResult(
    RewriteResult Rewrite,
    Sha256Digest? InputDigest,
    string? InputModuleVersionId,
    ImmutableArray<AppliedRewriteMutationEvidence> Mutations)
{
    public bool IsSuccess => Rewrite.Status == RewriteStatus.Succeeded;
}

/// <summary>
/// Writes only the exact tested bridge recipe to a caller-owned, already-created staging directory.
/// Construction requires a catalog-issued Approved capability and a fresh validated execution plan;
/// no caller-supplied path or digest can become rewrite authority.
/// </summary>
public sealed class GameHostBridgeAssemblyRewriter : IAssemblyRewriter
{
    private readonly GameHostRecipeDecision decision;
    private readonly ValidatedExecutionPlan executionPlan;
    private readonly string expectedInputPath;
    private readonly long expectedInputSize;
    private readonly Sha256Digest expectedInputDigest;

    public GameHostBridgeAssemblyRewriter(
        GameHostRecipeDecision decision,
        ValidatedExecutionPlan executionPlan)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(executionPlan);

        if (!decision.CanRewrite ||
            decision.EntitlementPolicy != GameHostEntitlementPolicy.TrustedInstalledSource ||
            decision.Recipe != GameHostBridgeRecipe.Identity ||
            !decision.SupportKey.Equals(GameHostRecipeCatalog.TestedPlaySupportKey, StringComparison.Ordinal) ||
            !decision.ApprovedMutations.SequenceEqual(GameHostBridgeRecipe.ApprovedMutations))
        {
            throw new InvalidOperationException("The support catalog has not authorized the exact GameHost bridge recipe.");
        }

        if (!executionPlan.PackageName.Equals(GameHostRecipeCatalog.TestedPlayPackageName, StringComparison.Ordinal) ||
            !executionPlan.VersionName.Equals(GameHostRecipeCatalog.TestedPlayVersionName, StringComparison.Ordinal) ||
            executionPlan.LongVersionCode != GameHostRecipeCatalog.TestedPlayLongVersionCode ||
            !executionPlan.SelectedAbi.Equals(GameHostRecipeCatalog.TestedPlayAbi, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The execution plan does not match the exact supported installed source.");
        }

        var inputPayloads = executionPlan.Payloads
            .Where(static payload =>
                payload.Kind.Equals("assembly", StringComparison.Ordinal) &&
                payload.RelativePath.Equals(GameHostBridgeRecipe.InputRelativePath, StringComparison.Ordinal))
            .ToArray();
        if (inputPayloads.Length != 1 ||
            inputPayloads[0].Size < 0 ||
            !Sha256Digest.TryParse(inputPayloads[0].Sha256, out var inputDigest))
        {
            throw new InvalidOperationException("The execution plan does not contain one exact guarded bridge input.");
        }

        var workspaceRoot = Path.GetFullPath(executionPlan.WorkspacePath);
        var inputPath = Path.GetFullPath(Path.Combine(workspaceRoot, inputPayloads[0].RelativePath));
        if (!IsContained(workspaceRoot, inputPath))
        {
            throw new InvalidOperationException("The guarded bridge input escapes the validated workspace.");
        }

        this.decision = decision;
        this.executionPlan = executionPlan;
        expectedInputPath = inputPath;
        expectedInputSize = inputPayloads[0].Size;
        expectedInputDigest = inputDigest;
    }

    public async ValueTask<RewriteResult> RewriteAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default) =>
        (await RewriteWithEvidenceAsync(request, cancellationToken).ConfigureAwait(false)).Rewrite;

    public async ValueTask<GameHostBridgeRewriteResult> RewriteWithEvidenceAsync(
        RewriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<DiagnosticRecord>();
        var outputCreated = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Recipe != decision.Recipe || request.Recipe != GameHostBridgeRecipe.Identity)
            {
                return Failure(
                    diagnostics,
                    GameHostBridgeRewriteDiagnosticCodes.RequestRejected,
                    "The rewrite request does not select the authorized bridge recipe.");
            }

            var outputDirectory = Path.GetDirectoryName(request.StagingOutputPath);
            if (string.IsNullOrEmpty(outputDirectory) ||
                !Directory.Exists(outputDirectory) ||
                File.Exists(request.StagingOutputPath) ||
                IsContained(executionPlan.WorkspacePath, request.StagingOutputPath))
            {
                return Failure(
                    diagnostics,
                    GameHostBridgeRewriteDiagnosticCodes.OutputRejected,
                    "The staging directory must already exist and the output file must not exist.");
            }

            if (!request.InputAssemblyPath.Equals(expectedInputPath, StringComparison.Ordinal) ||
                !File.Exists(request.InputAssemblyPath) ||
                new FileInfo(request.InputAssemblyPath).Length != expectedInputSize)
            {
                return Failure(
                    diagnostics,
                    GameHostBridgeRewriteDiagnosticCodes.InputRejected,
                    "The trusted rewrite input is missing.");
            }

            var inputBytes = await File.ReadAllBytesAsync(request.InputAssemblyPath, cancellationToken).ConfigureAwait(false);
            var actualInputDigest = Digest(inputBytes);
            if (actualInputDigest != expectedInputDigest)
            {
                return Failure(
                    diagnostics,
                    GameHostBridgeRewriteDiagnosticCodes.InputRejected,
                    "The rewrite input digest does not match the trusted execution plan.",
                    actualInputDigest);
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] outputBytes;
            ImmutableArray<AppliedRewriteMutationEvidence> mutations;
            string inputModuleVersionId;
            using var resolver = new ValidatedExecutionPlanAssemblyResolver(executionPlan);
            using (var input = new MemoryStream(inputBytes, writable: false))
            using (var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters
                   {
                       AssemblyResolver = resolver,
                       ReadingMode = ReadingMode.Deferred,
                       ReadSymbols = false,
                       InMemory = true,
                   }))
            {
                if (!assembly.Name.FullName.Equals(GameHostRecipeCatalog.TestedPlayTargetIdentity, StringComparison.Ordinal) ||
                    !assembly.MainModule.Mvid.ToString("D").Equals(
                        GameHostRecipeCatalog.TestedPlayTargetMvid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        diagnostics,
                        GameHostBridgeRewriteDiagnosticCodes.InputRejected,
                        "The rewrite input assembly identity or MVID is not the approved target.",
                        actualInputDigest);
                }

                inputModuleVersionId = assembly.MainModule.Mvid.ToString("D").ToLowerInvariant();
                try
                {
                    mutations = GameHostBridgeRecipeEngine.Apply(assembly);
                }
                catch (InvalidDataException exception)
                {
                    return Failure(
                        diagnostics,
                        GameHostBridgeRewriteDiagnosticCodes.GuardRejected,
                        "An exact bridge precondition, mutation count, entitlement boundary, or postcondition failed.",
                        actualInputDigest,
                        inputModuleVersionId,
                        exception.Message);
                }

                using var output = new MemoryStream();
                try
                {
                    assembly.Write(output, new WriterParameters { WriteSymbols = false });
                }
                catch (Exception exception) when (exception is AssemblyResolutionException or InvalidDataException)
                {
                    return Failure(
                        diagnostics,
                        GameHostBridgeRewriteDiagnosticCodes.DependencyRejected,
                        "A rewrite dependency was missing or changed outside the validated execution plan.",
                        actualInputDigest,
                        inputModuleVersionId);
                }

                outputBytes = output.ToArray();
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var reopenedStream = new MemoryStream(outputBytes, writable: false))
            using (var reopened = AssemblyDefinition.ReadAssembly(reopenedStream, new ReaderParameters
                   {
                       ReadingMode = ReadingMode.Deferred,
                       ReadSymbols = false,
                       InMemory = true,
                   }))
            {
                if (!reopened.Name.FullName.Equals(GameHostRecipeCatalog.TestedPlayTargetIdentity, StringComparison.Ordinal) ||
                    !reopened.MainModule.Mvid.ToString("D").Equals(inputModuleVersionId, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        diagnostics,
                        GameHostBridgeRewriteDiagnosticCodes.GuardRejected,
                        "The reopened output changed the guarded assembly identity or MVID.",
                        actualInputDigest,
                        inputModuleVersionId);
                }

                try
                {
                    GameHostBridgeRecipeEngine.ValidatePostconditions(reopened);
                }
                catch (InvalidDataException exception)
                {
                    return Failure(
                        diagnostics,
                        GameHostBridgeRewriteDiagnosticCodes.GuardRejected,
                        "The independently reopened output failed exact bridge postconditions.",
                        actualInputDigest,
                        inputModuleVersionId,
                        exception.Message);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await using (var output = new FileStream(
                             request.StagingOutputPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                outputCreated = true;
                await output.WriteAsync(outputBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var outputDigest = Digest(outputBytes);
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Information,
                GameHostBridgeRewriteDiagnosticCodes.Succeeded,
                "The exact guarded GameHost bridge overlay was written and independently reopened."));
            return new GameHostBridgeRewriteResult(
                new RewriteResult(RewriteStatus.Succeeded, request.StagingOutputPath, outputDigest, diagnostics.AsReadOnly()),
                actualInputDigest,
                inputModuleVersionId,
                mutations);
        }
        catch (OperationCanceledException)
        {
            DeleteCreatedOutput(request.StagingOutputPath, outputCreated);
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Warning,
                GameHostBridgeRewriteDiagnosticCodes.Cancelled,
                "The bridge rewrite was cancelled before a complete staging output was accepted."));
            return new GameHostBridgeRewriteResult(
                new RewriteResult(RewriteStatus.Failed, null, null, diagnostics.AsReadOnly()),
                null,
                null,
                []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            DeleteCreatedOutput(request.StagingOutputPath, outputCreated);
            diagnostics.Add(Diagnostic(
                DiagnosticSeverity.Error,
                GameHostBridgeRewriteDiagnosticCodes.WriteFailed,
                "The bridge rewrite could not read or write its guarded staging files."));
            return new GameHostBridgeRewriteResult(
                new RewriteResult(RewriteStatus.Failed, null, null, diagnostics.AsReadOnly()),
                null,
                null,
                []);
        }
    }

    private static GameHostBridgeRewriteResult Failure(
        List<DiagnosticRecord> diagnostics,
        string code,
        string message,
        Sha256Digest? inputDigest = null,
        string? inputModuleVersionId = null,
        string? safeDetail = null)
    {
        diagnostics.Add(Diagnostic(DiagnosticSeverity.Error, code, message, safeDetail));
        return new GameHostBridgeRewriteResult(
            new RewriteResult(RewriteStatus.Failed, null, null, diagnostics.AsReadOnly()),
            inputDigest,
            inputModuleVersionId,
            []);
    }

    private static DiagnosticRecord Diagnostic(
        DiagnosticSeverity severity,
        string code,
        string message,
        string? detail = null) =>
        new(DateTimeOffset.UtcNow, StartupStage.Rewrite, severity, code, message, detail);

    private static Sha256Digest Digest(ReadOnlySpan<byte> bytes) =>
        Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static bool IsContained(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        return !Path.IsPathFullyQualified(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void DeleteCreatedOutput(string path, bool outputCreated)
    {
        if (!outputCreated)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller-owned staging recovery path will quarantine an undeletable partial output.
        }
    }
}
