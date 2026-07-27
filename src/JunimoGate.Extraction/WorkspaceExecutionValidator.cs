using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>Stable diagnostics emitted while rebuilding execution trust from live package and workspace state.</summary>
public static class WorkspaceExecutionTrustErrorCodes
{
    public const string CertificateBlocked = "gamehost_trust_certificate_blocked";
    public const string StateMissing = "gamehost_trust_state_missing";
    public const string StateInvalid = "gamehost_trust_state_invalid";
    public const string ActiveKeyInvalid = "gamehost_trust_active_key_invalid";
    public const string WorkspacePathInvalid = "gamehost_trust_workspace_path_invalid";
    public const string ManifestInvalid = "gamehost_trust_manifest_invalid";
    public const string ManifestSchemaMismatch = "gamehost_trust_manifest_schema_mismatch";
    public const string SourceIdentityMismatch = "gamehost_trust_source_identity_mismatch";
    public const string RewriteRecipeMismatch = "gamehost_trust_rewrite_recipe_mismatch";
    public const string RewriteStatusMismatch = "gamehost_trust_rewrite_status_mismatch";
    public const string FileSetMismatch = "gamehost_trust_file_set_mismatch";
    public const string PayloadHashMismatch = "gamehost_trust_payload_hash_mismatch";
    public const string LiveRevalidationFailed = "gamehost_trust_live_revalidation_failed";
    public const string Cancelled = "gamehost_trust_cancelled";
}

/// <summary>Gate 0 rewrite identity expected from an immutable M4 workspace.</summary>
public static class WorkspaceExecutionTrustDefaults
{
    public const string Gate0RewriteRecipe = "unrewritten:v1";
    public const string Gate0RewriteStatus = "not-applied";
}

public enum WorkspaceExecutionValidationStatus
{
    Validated,
    Rejected,
    Cancelled,
}

/// <summary>Inputs needed to rebuild trust without accepting a caller-selected workspace path.</summary>
public sealed class WorkspaceExecutionValidationRequest
{
    public WorkspaceExecutionValidationRequest(
        GameInstallationCandidate liveCandidate,
        string runtimeRoot,
        string expectedExtractorSchema,
        string expectedManifestSchema,
        string expectedRewriteRecipe,
        string expectedRewriteStatus)
    {
        ArgumentNullException.ThrowIfNull(liveCandidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExtractorSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedManifestSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRewriteRecipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRewriteStatus);

        LiveCandidate = liveCandidate;
        RuntimeRoot = Path.GetFullPath(runtimeRoot);
        ExpectedExtractorSchema = expectedExtractorSchema;
        ExpectedManifestSchema = expectedManifestSchema;
        ExpectedRewriteRecipe = expectedRewriteRecipe;
        ExpectedRewriteStatus = expectedRewriteStatus;
    }

    public GameInstallationCandidate LiveCandidate { get; }
    public string RuntimeRoot { get; }
    public string ExpectedExtractorSchema { get; }
    public string ExpectedManifestSchema { get; }
    public string ExpectedRewriteRecipe { get; }
    public string ExpectedRewriteStatus { get; }
}

/// <summary>One payload whose exact size and digest were revalidated for this execution plan.</summary>
public sealed record ValidatedWorkspacePayload(
    string Kind,
    string RelativePath,
    long Size,
    string Sha256);

/// <summary>
/// Sealed, immutable, in-process evidence that one active workspace passed the complete execution trust chain.
/// It has no public constructor and contains no persisted authorization flag.
/// </summary>
public sealed class ValidatedExecutionPlan
{
    internal ValidatedExecutionPlan(
        string packageName,
        string versionName,
        long longVersionCode,
        string selectedAbi,
        string workspaceKey,
        string workspacePath,
        string identityDigest,
        DateTimeOffset validatedAtUtc,
        IEnumerable<ValidatedWorkspacePayload> payloads)
    {
        PackageName = packageName;
        VersionName = versionName;
        LongVersionCode = longVersionCode;
        SelectedAbi = selectedAbi;
        WorkspaceKey = workspaceKey;
        WorkspacePath = workspacePath;
        IdentityDigest = identityDigest;
        ValidatedAtUtc = validatedAtUtc;
        Payloads = Array.AsReadOnly(payloads.ToArray());
    }

    public string PackageName { get; }
    public string VersionName { get; }
    public long LongVersionCode { get; }
    public string SelectedAbi { get; }
    public string WorkspaceKey { get; }

    /// <summary>
    /// Canonical app-private active workspace path. This is public only so a platform adapter in another
    /// assembly can consume a validated plan; callers cannot supply it when constructing a plan.
    /// </summary>
    [JsonIgnore]
    public string WorkspacePath { get; }

    public string IdentityDigest { get; }
    public DateTimeOffset ValidatedAtUtc { get; }
    public ReadOnlyCollection<ValidatedWorkspacePayload> Payloads { get; }
}

public sealed class WorkspaceExecutionValidationResult
{
    internal WorkspaceExecutionValidationResult(
        WorkspaceExecutionValidationStatus status,
        ValidatedExecutionPlan? plan,
        IEnumerable<DiagnosticRecord> diagnostics)
    {
        Status = status;
        Plan = plan;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public WorkspaceExecutionValidationStatus Status { get; }
    public ValidatedExecutionPlan? Plan { get; }
    public ReadOnlyCollection<DiagnosticRecord> Diagnostics { get; }
}

/// <summary>Rebuilds execution trust from the exact live package and the active workspace state.</summary>
public sealed class WorkspaceExecutionValidator
{
    private const int MaximumStateBytes = 64 * 1024;
    private readonly IWorkspaceCandidateRevalidator revalidator;

    public WorkspaceExecutionValidator(IWorkspaceCandidateRevalidator revalidator)
    {
        ArgumentNullException.ThrowIfNull(revalidator);
        this.revalidator = revalidator;
    }

    public ValueTask<WorkspaceExecutionValidationResult> ValidateAsync(
        GameInstallationCandidate liveCandidate,
        string runtimeRoot,
        string expectedExtractorSchema,
        string expectedManifestSchema,
        string expectedRewriteRecipe,
        string expectedRewriteStatus,
        CancellationToken cancellationToken = default) =>
        ValidateAsync(
            new WorkspaceExecutionValidationRequest(
                liveCandidate,
                runtimeRoot,
                expectedExtractorSchema,
                expectedManifestSchema,
                expectedRewriteRecipe,
                expectedRewriteStatus),
            cancellationToken);

    public async ValueTask<WorkspaceExecutionValidationResult> ValidateAsync(
        WorkspaceExecutionValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<DiagnosticRecord>();
        try
        {
            // Recompute policy before touching workspace state or manifests. The discovery-time decision is not authority.
            var installation = request.LiveCandidate.Installation;
            if (!KnownGameCertificate.Verify(installation.PackageName, installation.SigningIdentity).AllowsCodeExecution)
            {
                return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.CertificateBlocked,
                    "The live installation certificate is not trusted for game code execution.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var statePath = Path.Combine(request.RuntimeRoot, WorkspaceManifestConstants.StateFileName);
            if (!File.Exists(statePath))
            {
                return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.StateMissing,
                    "No active workspace state is available.");
            }

            WorkspaceState? state;
            try
            {
                state = await WorkspaceJson.ReadBoundedAsync<WorkspaceState>(
                    statePath,
                    MaximumStateBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.StateInvalid,
                    "The active workspace state is corrupt or unreadable.");
            }

            if (state is null ||
                state.Format != WorkspaceManifestConstants.StateFormat ||
                state.Schema != WorkspaceManifestConstants.StateSchema)
            {
                return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.StateInvalid,
                    "The active workspace state format or schema is invalid.");
            }

            if (!IsCanonicalWorkspaceKey(state.ActiveKey) ||
                (state.PreviousKey is not null && (!IsCanonicalWorkspaceKey(state.PreviousKey) || state.PreviousKey == state.ActiveKey)))
            {
                return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.ActiveKeyInvalid,
                    "The active workspace key is not a canonical cache identity.");
            }

            string workspacePath;
            try
            {
                workspacePath = GetActiveWorkspacePath(request.RuntimeRoot, state.ActiveKey!);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.WorkspacePathInvalid,
                    "The active workspace is not safely contained by the runtime root.");
            }

            var manifestValidation = await WorkspaceManifestValidator.ValidateAsync(
                workspacePath,
                state.ActiveKey!,
                request.LiveCandidate,
                new WorkspaceManifestValidationExpectations(
                    request.ExpectedManifestSchema,
                    request.ExpectedExtractorSchema,
                    request.ExpectedRewriteRecipe,
                    request.ExpectedRewriteStatus,
                    WorkspaceManifestConstants.NoSmapiBuildId),
                cancellationToken).ConfigureAwait(false);
            if (!manifestValidation.IsValid)
            {
                var (code, message) = MapManifestFailure(manifestValidation.Failure);
                return Rejected(diagnostics, code, message);
            }

            // This exact-package query is intentionally last: package/source/certificate races after file hashing fail closed.
            var freshCandidate = await revalidator.RevalidateAsync(
                installation.PackageName,
                cancellationToken).ConfigureAwait(false);
            if (freshCandidate is null ||
                !WorkspaceManifestValidator.CandidateIdentityEquals(
                    request.LiveCandidate,
                    freshCandidate,
                    includeSourcePaths: true) ||
                !KnownGameCertificate.Verify(
                    freshCandidate.Installation.PackageName,
                    freshCandidate.Installation.SigningIdentity).AllowsCodeExecution)
            {
                return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.LiveRevalidationFailed,
                    "The exact live package identity changed during execution trust validation.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var extractionManifest = manifestValidation.ExtractionManifest!;
            var payloads = extractionManifest.Files
                .Select(static file => new ValidatedWorkspacePayload(file.Kind, file.RelativePath, file.Size, file.Sha256))
                .ToArray();
            var identityDigest = CreateIdentityDigest(
                freshCandidate,
                state.ActiveKey!,
                request,
                payloads);
            var plan = new ValidatedExecutionPlan(
                freshCandidate.Installation.PackageName,
                freshCandidate.Installation.VersionName,
                freshCandidate.Installation.LongVersionCode,
                freshCandidate.Installation.SelectedAbi,
                state.ActiveKey!,
                workspacePath,
                identityDigest,
                DateTimeOffset.UtcNow,
                payloads);
            return new WorkspaceExecutionValidationResult(
                WorkspaceExecutionValidationStatus.Validated,
                plan,
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(Diagnostic(
                WorkspaceExecutionTrustErrorCodes.Cancelled,
                DiagnosticSeverity.Information,
                "Execution trust validation was cancelled."));
            return new WorkspaceExecutionValidationResult(
                WorkspaceExecutionValidationStatus.Cancelled,
                null,
                diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or CryptographicException)
        {
            return Rejected(diagnostics, WorkspaceExecutionTrustErrorCodes.ManifestInvalid,
                "Execution trust validation could not safely read or validate the active workspace.");
        }
    }

    private static string GetActiveWorkspacePath(string runtimeRoot, string activeKey)
    {
        var canonicalRoot = Path.GetFullPath(runtimeRoot);
        var workspacesRoot = Path.GetFullPath(Path.Combine(canonicalRoot, "workspaces"));
        var workspacePath = Path.GetFullPath(Path.Combine(workspacesRoot, activeKey));
        var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        var workspacesPrefix = workspacesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? workspacesRoot
            : workspacesRoot + Path.DirectorySeparatorChar;
        if (!workspacesRoot.StartsWith(rootPrefix, StringComparison.Ordinal) ||
            !workspacePath.StartsWith(workspacesPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The active workspace escapes its runtime root.");
        }

        WorkspaceJson.RejectReparsePoint(canonicalRoot);
        WorkspaceJson.RejectReparsePoint(workspacesRoot);
        WorkspaceJson.RejectReparsePoint(workspacePath);
        if (!Directory.Exists(workspacePath))
        {
            throw new InvalidDataException("The active workspace does not exist.");
        }

        return workspacePath;
    }

    private static bool IsCanonicalWorkspaceKey(string? key) =>
        Sha256Digest.TryParse(key, out _);

    private static (string Code, string Message) MapManifestFailure(WorkspaceManifestValidationFailure failure) =>
        failure switch
        {
            WorkspaceManifestValidationFailure.ManifestSchemaMismatch => (
                WorkspaceExecutionTrustErrorCodes.ManifestSchemaMismatch,
                "A workspace manifest schema or extractor schema does not match the execution gate."),
            WorkspaceManifestValidationFailure.SourceIdentityMismatch => (
                WorkspaceExecutionTrustErrorCodes.SourceIdentityMismatch,
                "The workspace source manifest does not match the live installation identity."),
            WorkspaceManifestValidationFailure.RecipeMismatch => (
                WorkspaceExecutionTrustErrorCodes.RewriteRecipeMismatch,
                "The workspace rewrite recipe does not match the execution gate."),
            WorkspaceManifestValidationFailure.StatusMismatch => (
                WorkspaceExecutionTrustErrorCodes.RewriteStatusMismatch,
                "The workspace rewrite status does not match the execution gate."),
            WorkspaceManifestValidationFailure.FileSetMismatch => (
                WorkspaceExecutionTrustErrorCodes.FileSetMismatch,
                "The active workspace file set does not exactly match its extraction manifest."),
            WorkspaceManifestValidationFailure.PayloadHashMismatch => (
                WorkspaceExecutionTrustErrorCodes.PayloadHashMismatch,
                "A workspace payload size or SHA-256 digest does not match its extraction manifest."),
            _ => (
                WorkspaceExecutionTrustErrorCodes.ManifestInvalid,
                "An active workspace manifest is missing, corrupt, or semantically invalid."),
        };

    private static WorkspaceExecutionValidationResult Rejected(
        ICollection<DiagnosticRecord> diagnostics,
        string code,
        string message)
    {
        diagnostics.Add(Diagnostic(code, DiagnosticSeverity.Error, message));
        return new WorkspaceExecutionValidationResult(
            WorkspaceExecutionValidationStatus.Rejected,
            null,
            diagnostics);
    }

    private static DiagnosticRecord Diagnostic(string code, DiagnosticSeverity severity, string message) =>
        new(DateTimeOffset.UtcNow, StartupStage.RuntimeValidation, severity, code, message);

    public static bool MatchesGate0Identity(
        ValidatedExecutionPlan plan,
        GameInstallationCandidate candidate) =>
        plan.PackageName.Equals(candidate.Installation.PackageName, StringComparison.Ordinal) &&
        plan.IdentityDigest.Equals(
            CreateIdentityDigest(
                candidate,
                plan.WorkspaceKey,
                WorkspacePreparationOptions.DefaultManifestSchema,
                WorkspacePreparationOptions.DefaultExtractorSchema,
                WorkspaceExecutionTrustDefaults.Gate0RewriteRecipe,
                WorkspaceExecutionTrustDefaults.Gate0RewriteStatus,
                plan.Payloads),
            StringComparison.Ordinal);

    private static string CreateIdentityDigest(
        GameInstallationCandidate candidate,
        string workspaceKey,
        WorkspaceExecutionValidationRequest request,
        IReadOnlyList<ValidatedWorkspacePayload> payloads) =>
        CreateIdentityDigest(
            candidate,
            workspaceKey,
            request.ExpectedManifestSchema,
            request.ExpectedExtractorSchema,
            request.ExpectedRewriteRecipe,
            request.ExpectedRewriteStatus,
            payloads);

    internal static string CreateIdentityDigest(
        GameInstallationCandidate candidate,
        string workspaceKey,
        string expectedManifestSchema,
        string expectedExtractorSchema,
        string expectedRewriteRecipe,
        string expectedRewriteStatus,
        IReadOnlyList<ValidatedWorkspacePayload> payloads)
    {
        var installation = candidate.Installation;
        var canonical = new StringBuilder("junimogate-validated-execution-plan:v1\n");
        Append(canonical, "workspaceKey", workspaceKey);
        Append(canonical, "packageName", installation.PackageName);
        Append(canonical, "versionName", installation.VersionName);
        Append(canonical, "longVersionCode", installation.LongVersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(canonical, "abi", installation.SelectedAbi);
        foreach (var signer in installation.SigningIdentity.CurrentSignerDigests)
        {
            Append(canonical, "currentSigner", signer.Value);
        }

        foreach (var signer in installation.SigningIdentity.RotationHistory)
        {
            Append(canonical, "rotationSigner", signer.Value);
        }

        foreach (var source in installation.ApkSources)
        {
            Append(canonical, "sourceLabel", source.Label);
            Append(canonical, "sourceSplit", source.SplitName ?? string.Empty);
            Append(canonical, "sourceSha256", source.Digest.Value);
            Append(canonical, "sourceSize", source.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Append(canonical, "manifestSchema", expectedManifestSchema);
        Append(canonical, "extractorSchema", expectedExtractorSchema);
        Append(canonical, "rewriteRecipe", expectedRewriteRecipe);
        Append(canonical, "rewriteStatus", expectedRewriteStatus);
        foreach (var payload in payloads.OrderBy(static payload => payload.RelativePath, StringComparer.Ordinal))
        {
            Append(canonical, "payloadPath", payload.RelativePath);
            Append(canonical, "payloadSize", payload.Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(canonical, "payloadSha256", payload.Sha256);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder target, string name, string value) =>
        target.Append(name).Append(':')
            .Append(Encoding.UTF8.GetByteCount(value))
            .Append(':').Append(value).Append('\n');
}
