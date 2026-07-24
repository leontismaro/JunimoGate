using JunimoGate.Core;

namespace JunimoGate.Rewriter;

public sealed record RewriteRecipeIdentity
{
    public RewriteRecipeIdentity(string name, string version)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A rewrite recipe name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("A rewrite recipe version is required.", nameof(version));
        }

        Name = name;
        Version = version;
    }

    public string Name { get; }

    public string Version { get; }

    public override string ToString() => $"{Name}@{Version}";
}

public sealed record RewriteRequest
{
    public RewriteRequest(string inputAssemblyPath, string stagingOutputPath, RewriteRecipeIdentity recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        InputAssemblyPath = NormalizeAbsolute(inputAssemblyPath, nameof(inputAssemblyPath));
        StagingOutputPath = NormalizeAbsolute(stagingOutputPath, nameof(stagingOutputPath));
        if (string.Equals(InputAssemblyPath, StagingOutputPath, StringComparison.Ordinal))
        {
            throw new ArgumentException("The staging output must differ from the input assembly.", nameof(stagingOutputPath));
        }

        Recipe = recipe;
    }

    public string InputAssemblyPath { get; }

    public string StagingOutputPath { get; }

    public RewriteRecipeIdentity Recipe { get; }

    private static string NormalizeAbsolute(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }
}

public enum RewriteStatus
{
    Succeeded,
    Skipped,
    Failed,
}

public sealed record RewriteResult(
    RewriteStatus Status,
    string? StagingOutputPath,
    Sha256Digest? OutputDigest,
    IReadOnlyList<DiagnosticRecord> Diagnostics);

/// <summary>
/// Mono.Cecil-backed rewriting is deliberately deferred to Phase 2. This interface fixes only the transaction boundary.
/// </summary>
public interface IAssemblyRewriter
{
    ValueTask<RewriteResult> RewriteAsync(RewriteRequest request, CancellationToken cancellationToken = default);
}
