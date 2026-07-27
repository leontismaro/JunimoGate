using System.Collections.ObjectModel;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

public static class WorkspaceErrorCodes
{
    public const string CertificateBlocked = "certificate_blocked";
    public const string SourceIdentityMismatch = "source_identity_mismatch";
    public const string SourceHashMismatch = "source_hash_mismatch";
    public const string UnsafeContentEntry = "unsafe_content_entry";
    public const string ContentLimitsExceeded = "content_limits_exceeded";
    public const string DuplicateOutput = "duplicate_output";
    public const string UnsupportedAssemblyStore = "unsupported_assembly_store";
    public const string RequiredOutputMissing = "required_output_missing";
    public const string ManifestInvalid = "manifest_invalid";
    public const string CacheCorrupt = "cache_corrupt";
    public const string ActivationFailed = "activation_failed";
    public const string Cancelled = "cancelled";
}

public enum WorkspacePreparationStatus
{
    Built,
    CacheHit,
    Blocked,
    Failed,
    Cancelled,
}

public enum WorkspaceProgressStage
{
    AcquiringLock,
    CleaningStaging,
    VerifyingCertificate,
    VerifyingSources,
    ValidatingCache,
    ScanningContent,
    ExtractingContent,
    ExtractingAssemblies,
    WritingManifests,
    ValidatingOutputs,
    Committing,
    RevalidatingInstallation,
    Activating,
    Completed,
}

public sealed record WorkspaceProgressEvent(
    WorkspaceProgressStage Stage,
    string Message,
    int? Completed = null,
    int? Total = null);

public sealed record WorkspaceExtractionLimits
{
    public int MaximumContentEntries { get; init; } = 100_000;
    public long MaximumContentFileBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumTotalContentBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public double MaximumCompressionRatio { get; init; } = 200;
    public long CompressionRatioMinimumFileBytes { get; init; } = 64 * 1024;
    public int MaximumPathSegmentLength { get; init; } = 255;
    public int MaximumRelativePathLength { get; init; } = 1024;
    public int MaximumPathDepth { get; init; } = 32;

    internal void Validate()
    {
        if (MaximumContentEntries <= 0 || MaximumContentFileBytes <= 0 || MaximumTotalContentBytes <= 0 ||
            MaximumCompressionRatio <= 0 || CompressionRatioMinimumFileBytes < 0 || MaximumPathSegmentLength <= 0 ||
            MaximumRelativePathLength <= 0 || MaximumPathDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkspaceExtractionLimits), "Workspace extraction limits must be positive.");
        }
    }
}

public sealed record WorkspacePreparationOptions
{
    public const string DefaultExtractorSchema = "junimogate-extraction:v1";
    public const string DefaultManifestSchema = "junimogate-workspace-manifest:v1";

    public string ExtractorSchema { get; init; } = DefaultExtractorSchema;
    public string RewriterRecipe { get; init; } = "unrewritten:v1";
    public string SmapiBuildId { get; init; } = "none";
    public string ManifestSchema { get; init; } = DefaultManifestSchema;
    public WorkspaceExtractionLimits Limits { get; init; } = new();
    public IProgress<WorkspaceProgressEvent>? Progress { get; init; }

    /// <summary>Whether a newly written workspace must immediately re-hash every payload.</summary>
    public bool ValidateWrittenPayloadHashes { get; init; } = true;

    /// <summary>Whether package discovery must be repeated before workspace activation.</summary>
    public bool RevalidateInstallation { get; init; } = true;

    /// <summary>Whether this preparer updates the historical source-workspace state pointer.</summary>
    public bool ActivateWorkspace { get; init; } = true;
}

public sealed record WorkspacePreparationRequest
{
    public WorkspacePreparationRequest(
        string workspaceRoot,
        GameInstallationCandidate candidate,
        WorkspacePreparationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(candidate);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        Candidate = candidate;
        Options = options ?? new WorkspacePreparationOptions();
        Options.Limits.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.ExtractorSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.RewriterRecipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.SmapiBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.ManifestSchema);
    }

    public string WorkspaceRoot { get; }
    public GameInstallationCandidate Candidate { get; }
    public WorkspacePreparationOptions Options { get; }
}

public interface IWorkspaceCandidateRevalidator
{
    ValueTask<GameInstallationCandidate?> RevalidateAsync(
        string packageName,
        CancellationToken cancellationToken = default);
}

public sealed record WorkspaceSignerManifest(
    IReadOnlyList<string> Current,
    IReadOnlyList<string> History);

public sealed record WorkspaceSourceManifestEntry(
    string Label,
    string? SplitName,
    string Sha256,
    long Size);

public sealed record WorkspaceSourceManifest(
    string Format,
    string Schema,
    string CacheKey,
    string PackageName,
    string VersionName,
    long LongVersionCode,
    string Abi,
    WorkspaceSignerManifest Signers,
    IReadOnlyList<WorkspaceSourceManifestEntry> Sources);

public sealed record WorkspaceExtractedFileManifest(
    string Kind,
    string RelativePath,
    long Size,
    string Sha256,
    string SourceLabel,
    string SourceEntry);

public sealed record WorkspaceExtractionStatistics(
    int ContentFileCount,
    long ContentBytes,
    int AssemblyFileCount,
    long AssemblyBytes);

public sealed record WorkspaceExtractionManifest(
    string Format,
    string Schema,
    string CacheKey,
    string ExtractorSchema,
    string RewriterRecipe,
    string SmapiBuildId,
    IReadOnlyList<WorkspaceExtractedFileManifest> Files,
    WorkspaceExtractionStatistics Statistics);

public sealed record WorkspaceRewriteManifest(
    string Format,
    string Schema,
    string CacheKey,
    string Recipe,
    string Status);

public sealed record WorkspacePreparationMetrics(
    long DurationMilliseconds,
    long PeakTemporaryBytes,
    long FinalWorkspaceBytes)
{
    public int ApkSourceOpenCount { get; init; }
    public int ApkFullHashCount { get; init; }
    public long ApkBytesHashed { get; init; }
    public int WorkspacePayloadHashPassCount { get; init; }
    public long WorkspacePayloadBytesHashed { get; init; }
}

public sealed record WorkspaceState(
    string Format,
    string Schema,
    string? ActiveKey,
    string? PreviousKey);

public sealed record WorkspacePreparationResult
{
    public WorkspacePreparationResult(
        WorkspacePreparationStatus status,
        string? workspacePath,
        string? workspaceKey,
        IEnumerable<DiagnosticRecord> diagnostics,
        WorkspaceExtractionStatistics? statistics = null,
        WorkspacePreparationMetrics? metrics = null,
        ValidatedExecutionPlan? executionPlan = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Status = status;
        WorkspacePath = workspacePath;
        WorkspaceKey = workspaceKey;
        Diagnostics = new ReadOnlyCollection<DiagnosticRecord>(diagnostics.ToArray());
        Statistics = statistics;
        Metrics = metrics;
        ExecutionPlan = executionPlan;
    }

    public WorkspacePreparationStatus Status { get; }
    public string? WorkspacePath { get; }
    public string? WorkspaceKey { get; }
    public ReadOnlyCollection<DiagnosticRecord> Diagnostics { get; }
    public WorkspaceExtractionStatistics? Statistics { get; }
    public WorkspacePreparationMetrics? Metrics { get; }
    public ValidatedExecutionPlan? ExecutionPlan { get; }
}

internal sealed class WorkspacePreparationException : IOException
{
    public WorkspacePreparationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
