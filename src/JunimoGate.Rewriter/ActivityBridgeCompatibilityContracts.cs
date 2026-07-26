using System.Collections.Immutable;

namespace JunimoGate.Rewriter;

public enum ActivityBridgeProbeStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

public enum ActivityBridgeProbeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ActivityBridgeProbeDiagnostic(
    string Code,
    ActivityBridgeProbeDiagnosticSeverity Severity,
    string Message);

public sealed record ActivityBridgeProbeLimits
{
    public const int MaximumTypeLimit = 1_000_000;
    public const int MaximumMemberLimit = 2_000_000;
    public const int MaximumInstructionLimit = 1_000_000;
    public const int MaximumEvidenceLimit = 100_000;

    public static ActivityBridgeProbeLimits Default { get; } = new();

    public ActivityBridgeProbeLimits(
        int maxTypes = 100_000,
        int maxMembers = 500_000,
        int maxInstructions = 250_000,
        int maxEvidenceItems = 25_000)
    {
        if (maxTypes < 1 || maxTypes > MaximumTypeLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTypes));
        }

        if (maxMembers < 1 || maxMembers > MaximumMemberLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMembers));
        }

        if (maxInstructions < 1 || maxInstructions > MaximumInstructionLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInstructions));
        }

        if (maxEvidenceItems < 1 || maxEvidenceItems > MaximumEvidenceLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvidenceItems));
        }

        MaxTypes = maxTypes;
        MaxMembers = maxMembers;
        MaxInstructions = maxInstructions;
        MaxEvidenceItems = maxEvidenceItems;
    }

    public int MaxTypes { get; }
    public int MaxMembers { get; }
    public int MaxInstructions { get; }
    public int MaxEvidenceItems { get; }
}

public sealed record ActivityBridgeCompatibilityProbeOptions
{
    public ActivityBridgeCompatibilityProbeOptions(
        string assemblyRootPath,
        string monoGameAssemblyPath,
        string gameAssemblyPath,
        IEnumerable<string> consumerAssemblyPaths,
        string parentSupportKey,
        ActivityBridgeProbeLimits? limits = null)
    {
        AssemblyRootPath = NormalizeAbsolute(assemblyRootPath, nameof(assemblyRootPath));
        MonoGameAssemblyPath = NormalizeContained(monoGameAssemblyPath, nameof(monoGameAssemblyPath));
        GameAssemblyPath = NormalizeContained(gameAssemblyPath, nameof(gameAssemblyPath));
        if (MonoGameAssemblyPath.Equals(GameAssemblyPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("MonoGame and game assembly paths must be different.");
        }

        ArgumentNullException.ThrowIfNull(consumerAssemblyPaths);
        var consumers = consumerAssemblyPaths
            .Select((path, index) => NormalizeContained(path, $"{nameof(consumerAssemblyPaths)}[{index}]"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (consumers.IsDefaultOrEmpty ||
            consumers.Length > ManagedApiCompatibilityLimits.Default.MaxConsumerAssemblies ||
            !consumers.Contains(GameAssemblyPath, StringComparer.Ordinal) ||
            consumers.Contains(MonoGameAssemblyPath, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Consumer assemblies must be a bounded contained set including the game assembly and excluding the MonoGame provider.",
                nameof(consumerAssemblyPaths));
        }

        ConsumerAssemblyPaths = consumers;

        if (!IsCanonicalSha256(parentSupportKey))
        {
            throw new ArgumentException("The parent support key must be canonical lowercase SHA-256.", nameof(parentSupportKey));
        }

        ParentSupportKey = parentSupportKey;
        Limits = limits ?? ActivityBridgeProbeLimits.Default;
    }

    public string AssemblyRootPath { get; }
    public string MonoGameAssemblyPath { get; }
    public string GameAssemblyPath { get; }
    public ImmutableArray<string> ConsumerAssemblyPaths { get; }
    public string ParentSupportKey { get; }
    public ActivityBridgeProbeLimits Limits { get; }

    private string NormalizeContained(string path, string parameterName)
    {
        var normalized = NormalizeAbsolute(path, parameterName);
        var relative = Path.GetRelativePath(AssemblyRootPath, normalized);
        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must remain inside the assembly root.", parameterName);
        }

        return normalized;
    }

    private static string NormalizeAbsolute(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static bool IsCanonicalSha256(string value) =>
        value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record ActivityBridgeAssemblyEvidence(
    string Identity,
    string ModuleVersionId,
    string? TargetFramework,
    ManagedPublicApiSurfaceEvidence PublicApiSurface);

public sealed record ActivityBridgeTypeEvidence(
    string Signature,
    string BaseType,
    bool IsAbstract,
    bool IsSealed,
    ImmutableArray<string> ConstructorSignatures,
    ImmutableArray<string> LifecycleMethodSignatures,
    ImmutableArray<InteropAttributeEvidence> InteropAttributes);

public sealed record MonoGameBridgeEvidence(
    ActivityBridgeTypeEvidence AndroidGameActivity,
    ActivityBridgeTypeEvidence Game,
    ImmutableArray<string> GameRunMethodSignatures,
    ImmutableArray<string> GameExitMethodSignatures,
    ImmutableArray<string> GameServicesPropertySignatures,
    ActivityBridgeTypeEvidence GameServiceContainer,
    ImmutableArray<string> GetServiceMethodSignatures);

public sealed record GameRunnerBridgeEvidence(
    ActivityBridgeTypeEvidence Type,
    ImmutableArray<string> StaticInstanceFieldSignatures,
    ImmutableArray<string> RunMethodSignatures);

public sealed record ActivityBridgeCallEvidence(
    int InstructionOrdinal,
    string OpCode,
    string CalledMethodSignature);

public sealed record ActivityBridgeFieldEvidence(
    int InstructionOrdinal,
    string OpCode,
    string FieldSignature);

public sealed record ActivityBridgeLifecycleBodyEvidence(
    string MethodSignature,
    int InstructionCount,
    ImmutableArray<ActivityBridgeCallEvidence> Calls,
    ImmutableArray<ActivityBridgeFieldEvidence> Fields);

public sealed record MainActivityBridgeEvidence(
    ActivityBridgeTypeEvidence Type,
    ImmutableArray<ActivityBridgeLifecycleBodyEvidence> LifecycleBodies);

public sealed record ActivityBridgeCompatibilityEvidence(
    string SchemaVersion,
    string ParentSupportKey,
    ActivityBridgeAssemblyEvidence MonoGameAssembly,
    ActivityBridgeAssemblyEvidence GameAssembly,
    ManagedApiRequirementEvidence MonoGameRequirements,
    MonoGameBridgeEvidence MonoGame,
    GameRunnerBridgeEvidence GameRunner,
    MainActivityBridgeEvidence MainActivity);

public sealed record ActivityBridgeCompatibilityProbeResult(
    ActivityBridgeProbeStatus Status,
    string? EvidenceKey,
    ActivityBridgeCompatibilityEvidence? Evidence,
    ImmutableArray<ActivityBridgeProbeDiagnostic> Diagnostics)
{
    public bool IsSuccess => Status == ActivityBridgeProbeStatus.Succeeded;
}
