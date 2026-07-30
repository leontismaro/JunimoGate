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

public sealed record GameHostBridgeRewriteResult(
    RewriteResult Rewrite,
    Sha256Digest? InputDigest,
    string? InputAssemblyIdentity,
    ImmutableArray<AppliedRewriteMutationEvidence> Mutations)
{
    public bool IsSuccess => Rewrite.Status == RewriteStatus.Succeeded;
}

/// <summary>Applies the local MainActivity bridge rules to one validated source workspace.</summary>
public sealed class GameHostBridgeAssemblyRewriter : IAssemblyRewriter
{
    private readonly ValidatedExecutionPlan executionPlan;
    private readonly string expectedInputPath;
    private readonly long expectedInputSize;
    private readonly Sha256Digest expectedInputDigest;

    public GameHostBridgeAssemblyRewriter(ValidatedExecutionPlan executionPlan)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);
        if (executionPlan.PackageName != KnownGameCertificate.PlayPackageName ||
            executionPlan.SelectedAbi != GameInstallationDiscoveryCoordinator.SupportedAbi)
        {
            throw new InvalidOperationException("The execution plan is not a supported Play ARM64 source.");
        }

        var inputPayloads = executionPlan.Payloads.Where(static payload =>
            payload.Kind == "assembly" && payload.RelativePath == GameHostBridgeRecipe.InputRelativePath).ToArray();
        if (inputPayloads.Length != 1 || inputPayloads[0].Size <= 0 ||
            !Sha256Digest.TryParse(inputPayloads[0].Sha256, out var inputDigest))
        {
            throw new InvalidOperationException("The execution plan does not contain one bridge input assembly.");
        }

        var workspaceRoot = Path.GetFullPath(executionPlan.WorkspacePath);
        var inputPath = Path.GetFullPath(Path.Combine(workspaceRoot, inputPayloads[0].RelativePath));
        if (!IsContained(workspaceRoot, inputPath))
            throw new InvalidOperationException("The bridge input escapes the validated workspace.");

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
            if (request.Recipe != GameHostBridgeRecipe.Identity)
            {
                return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.RequestRejected,
                    "The rewrite request does not select the MainActivity bridge rule family.");
            }

            var outputDirectory = Path.GetDirectoryName(request.StagingOutputPath);
            if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory) ||
                File.Exists(request.StagingOutputPath) || IsContained(executionPlan.WorkspacePath, request.StagingOutputPath))
            {
                return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.OutputRejected,
                    "The staging directory must exist outside the source workspace and the output must be new.");
            }

            if (request.InputAssemblyPath != expectedInputPath || !File.Exists(request.InputAssemblyPath) ||
                new FileInfo(request.InputAssemblyPath).Length != expectedInputSize)
            {
                return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.InputRejected,
                    "The validated bridge input is missing or changed.");
            }

            var inputBytes = await File.ReadAllBytesAsync(request.InputAssemblyPath, cancellationToken).ConfigureAwait(false);
            var actualInputDigest = Digest(inputBytes);
            if (actualInputDigest != expectedInputDigest)
            {
                return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.InputRejected,
                    "The bridge input digest does not match the validated source workspace.", actualInputDigest);
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] outputBytes;
            string inputAssemblyIdentity;
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
                if (assembly.Name.Name != "StardewValley")
                {
                    return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.InputRejected,
                        "The bridge input is not the StardewValley assembly.", actualInputDigest);
                }

                inputAssemblyIdentity = assembly.Name.FullName;
                try
                {
                    _ = GameHostBridgeRecipeEngine.Apply(assembly);
                }
                catch (InvalidDataException exception)
                {
                    return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.GuardRejected,
                        "The game assembly does not satisfy the local MainActivity bridge rules.",
                        actualInputDigest, inputAssemblyIdentity, exception.Message);
                }

                using var output = new MemoryStream();
                try
                {
                    assembly.Write(output, new WriterParameters { WriteSymbols = false });
                }
                catch (Exception exception) when (exception is AssemblyResolutionException or InvalidDataException)
                {
                    return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.DependencyRejected,
                        "A rewrite dependency was missing or incompatible with the validated source workspace.",
                        actualInputDigest, inputAssemblyIdentity);
                }
                outputBytes = output.ToArray();
            }

            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<AppliedRewriteMutationEvidence> mutations;
            using (var reopenedStream = new MemoryStream(outputBytes, writable: false))
            using (var reopened = AssemblyDefinition.ReadAssembly(reopenedStream, new ReaderParameters
                   {
                       ReadingMode = ReadingMode.Deferred,
                       ReadSymbols = false,
                       InMemory = true,
                   }))
            {
                if (reopened.Name.FullName != inputAssemblyIdentity)
                {
                    return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.GuardRejected,
                        "The rewritten output changed the input assembly identity.",
                        actualInputDigest, inputAssemblyIdentity);
                }
                try
                {
                    mutations = GameHostBridgeRecipeEngine.ValidatePostconditions(reopened);
                }
                catch (InvalidDataException exception)
                {
                    return Failure(diagnostics, GameHostBridgeRewriteDiagnosticCodes.GuardRejected,
                        "The reopened output failed local bridge postconditions.",
                        actualInputDigest, inputAssemblyIdentity, exception.Message);
                }
            }

            await using (var output = new FileStream(
                             request.StagingOutputPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                outputCreated = true;
                await output.WriteAsync(outputBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var outputDigest = Digest(outputBytes);
            diagnostics.Add(Diagnostic(DiagnosticSeverity.Information,
                GameHostBridgeRewriteDiagnosticCodes.Succeeded,
                "The semantic MainActivity bridge overlay was written and independently reopened."));
            return new GameHostBridgeRewriteResult(
                new RewriteResult(RewriteStatus.Succeeded, request.StagingOutputPath, outputDigest, diagnostics.AsReadOnly()),
                actualInputDigest,
                inputAssemblyIdentity,
                mutations);
        }
        catch (OperationCanceledException)
        {
            DeleteCreatedOutput(request.StagingOutputPath, outputCreated);
            diagnostics.Add(Diagnostic(DiagnosticSeverity.Warning,
                GameHostBridgeRewriteDiagnosticCodes.Cancelled,
                "The bridge rewrite was cancelled before a complete staging output was accepted."));
            return new GameHostBridgeRewriteResult(
                new RewriteResult(RewriteStatus.Failed, null, null, diagnostics.AsReadOnly()), null, null, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            DeleteCreatedOutput(request.StagingOutputPath, outputCreated);
            diagnostics.Add(Diagnostic(DiagnosticSeverity.Error,
                GameHostBridgeRewriteDiagnosticCodes.WriteFailed,
                "The bridge rewrite could not read or write its staging files."));
            return new GameHostBridgeRewriteResult(
                new RewriteResult(RewriteStatus.Failed, null, null, diagnostics.AsReadOnly()), null, null, []);
        }
    }

    private static GameHostBridgeRewriteResult Failure(
        List<DiagnosticRecord> diagnostics,
        string code,
        string message,
        Sha256Digest? inputDigest = null,
        string? inputAssemblyIdentity = null,
        string? safeDetail = null)
    {
        diagnostics.Add(Diagnostic(DiagnosticSeverity.Error, code, message, safeDetail));
        return new GameHostBridgeRewriteResult(
            new RewriteResult(RewriteStatus.Failed, null, null, diagnostics.AsReadOnly()),
            inputDigest,
            inputAssemblyIdentity,
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
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void DeleteCreatedOutput(string path, bool outputCreated)
    {
        if (!outputCreated)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller-owned staging recovery path handles an undeletable partial output.
        }
    }
}
