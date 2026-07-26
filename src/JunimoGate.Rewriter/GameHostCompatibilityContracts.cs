using System.Collections.Immutable;

namespace JunimoGate.Rewriter;

public enum GameHostProbeStatus
{
    Succeeded,
    Failed,
    Cancelled,
}

public enum GameHostProbeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record GameHostProbeDiagnostic(
    string Code,
    GameHostProbeDiagnosticSeverity Severity,
    string Message);

public sealed record GameHostProbeLimits
{
    public const int MaximumAssemblyLimit = 1_024;
    public const int MaximumTypeLimit = 1_000_000;
    public const int MaximumMethodLimit = 2_000_000;
    public const int MaximumInstructionLimit = 10_000_000;
    public const int MaximumEvidenceLimit = 2_000_000;

    public static GameHostProbeLimits Default { get; } = new();

    public GameHostProbeLimits(
        int maxAssemblies = 256,
        int maxTypes = 250_000,
        int maxMethods = 500_000,
        int maxInstructions = 5_000_000,
        int maxEvidenceItems = 500_000)
    {
        MaxAssemblies = Validate(maxAssemblies, MaximumAssemblyLimit, nameof(maxAssemblies));
        MaxTypes = Validate(maxTypes, MaximumTypeLimit, nameof(maxTypes));
        MaxMethods = Validate(maxMethods, MaximumMethodLimit, nameof(maxMethods));
        MaxInstructions = Validate(maxInstructions, MaximumInstructionLimit, nameof(maxInstructions));
        MaxEvidenceItems = Validate(maxEvidenceItems, MaximumEvidenceLimit, nameof(maxEvidenceItems));
    }

    public int MaxAssemblies { get; }

    public int MaxTypes { get; }

    public int MaxMethods { get; }

    public int MaxInstructions { get; }

    public int MaxEvidenceItems { get; }

    private static int Validate(int value, int maximum, string parameterName)
    {
        if (value < 1 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The value must be between 1 and {maximum}.");
        }

        return value;
    }
}

public sealed record GameHostCompatibilityProbeOptions
{
    public GameHostCompatibilityProbeOptions(
        string assemblyRootPath,
        string targetAssemblyPath,
        IEnumerable<string> assemblyPaths,
        GameHostProbeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);

        AssemblyRootPath = NormalizeAbsolute(assemblyRootPath, nameof(assemblyRootPath));
        TargetAssemblyPath = NormalizeContainedPath(targetAssemblyPath, nameof(targetAssemblyPath));
        Limits = limits ?? GameHostProbeLimits.Default;

        var suppliedPaths = assemblyPaths
            .Select((path, index) => NormalizeContainedPath(path, $"{nameof(assemblyPaths)}[{index}]"))
            .ToImmutableArray();
        var normalizedPaths = suppliedPaths
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

        if (normalizedPaths.Length != suppliedPaths.Length)
        {
            throw new ArgumentException("Duplicate assembly paths are not allowed.", nameof(assemblyPaths));
        }

        if (normalizedPaths.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one assembly path is required.", nameof(assemblyPaths));
        }

        if (normalizedPaths.Length > Limits.MaxAssemblies)
        {
            throw new ArgumentException("The assembly count exceeds the configured bound.", nameof(assemblyPaths));
        }

        if (!normalizedPaths.Contains(TargetAssemblyPath, StringComparer.Ordinal))
        {
            throw new ArgumentException("The target assembly must be included in the assembly path set.", nameof(targetAssemblyPath));
        }

        AssemblyPaths = normalizedPaths;
    }

    public string AssemblyRootPath { get; }

    public string TargetAssemblyPath { get; }

    public ImmutableArray<string> AssemblyPaths { get; }

    public GameHostProbeLimits Limits { get; }

    private string NormalizeContainedPath(string path, string parameterName)
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
}

public sealed record AssemblyReferenceEvidence(string Identity);

public sealed record TargetAssemblyEvidence(
    string Identity,
    string ModuleVersionId,
    string? TargetFramework,
    ImmutableArray<AssemblyReferenceEvidence> References);

public sealed record MainActivityEvidence(
    string BaseType,
    string InstanceFieldSignature,
    ImmutableArray<string> MethodSignatures,
    ImmutableArray<string> LifecycleMethodSignatures,
    ImmutableArray<string> BootstrapMethodSignatures);

public enum FieldUseOperation
{
    Read,
    Write,
    Address,
    Other,
}

public sealed record FieldUseEvidence(
    string AssemblyIdentity,
    string ContainingMethodSignature,
    int InstructionOrdinal,
    string OpCode,
    FieldUseOperation Operation,
    string FieldSignature);

public sealed record CallSiteEvidence(
    string AssemblyIdentity,
    string ContainingMethodSignature,
    int InstructionOrdinal,
    string OpCode,
    string CalledMethodSignature,
    bool TargetsMainActivity);

public sealed record PInvokeEvidence(
    string AssemblyIdentity,
    string MethodSignature,
    string ModuleName,
    string EntryPoint,
    string CallingConvention,
    string CharacterSet,
    string Attributes);

public sealed record InteropAttributeEvidence(
    string AssemblyIdentity,
    string OwnerSignature,
    string AttributeType,
    string ConstructorSignature,
    ImmutableArray<string> ArgumentFingerprints);

public sealed record FieldUseCounts(int Read, int Write, int Address, int Other, int Total);

public sealed record GameHostCompatibilityEvidence(
    string SchemaVersion,
    TargetAssemblyEvidence TargetAssembly,
    MainActivityEvidence MainActivity,
    ImmutableArray<FieldUseEvidence> FieldUses,
    FieldUseCounts FieldUseCounts,
    ImmutableArray<CallSiteEvidence> CallSites,
    int CallSiteCount,
    ImmutableArray<PInvokeEvidence> PInvokes,
    ImmutableArray<InteropAttributeEvidence> InteropAttributes);

public sealed record GameHostCompatibilityProbeResult(
    GameHostProbeStatus Status,
    string? ManagedEvidenceKey,
    GameHostCompatibilityEvidence? Evidence,
    ImmutableArray<GameHostProbeDiagnostic> Diagnostics)
{
    public bool IsSuccess => Status == GameHostProbeStatus.Succeeded;
}
