using System.Security.Cryptography;
using JunimoGate.Core;
using JunimoGate.Extraction;
using Mono.Cecil;

namespace JunimoGate.Rewriter;

/// <summary>
/// Resolves only managed assemblies named by one fresh execution plan. Every dependency is rechecked
/// against the plan-owned size and SHA-256 immediately before Cecil reads it.
/// </summary>
internal sealed class ValidatedExecutionPlanAssemblyResolver : IAssemblyResolver
{
    private readonly Dictionary<string, ValidatedAssemblyPayload> payloads;
    private readonly Dictionary<string, AssemblyDefinition> cache = new(StringComparer.Ordinal);
    private bool disposed;

    internal ValidatedExecutionPlanAssemblyResolver(ValidatedExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var workspaceRoot = Path.GetFullPath(plan.WorkspacePath);
        var candidates = new Dictionary<string, ValidatedAssemblyPayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var payload in plan.Payloads.Where(static payload =>
                     payload.Kind.Equals("assembly", StringComparison.Ordinal)))
        {
            if (!GameHostAppliedWorkspaceValidator.IsValidManagedInputPath(payload.RelativePath) ||
                payload.Size < 0)
            {
                throw new InvalidDataException("The trusted execution plan contains an invalid managed payload.");
            }

            if (!Sha256Digest.TryParse(payload.Sha256, out var digest))
            {
                throw new InvalidDataException("The trusted execution plan contains an invalid managed payload digest.");
            }

            var path = Path.GetFullPath(Path.Combine(workspaceRoot, payload.RelativePath));
            if (!IsContained(workspaceRoot, path))
            {
                throw new InvalidDataException("A managed payload escapes the trusted workspace root.");
            }

            var simpleName = Path.GetFileNameWithoutExtension(payload.RelativePath);
            if (string.IsNullOrWhiteSpace(simpleName) ||
                !candidates.TryAdd(simpleName, new ValidatedAssemblyPayload(path, payload.Size, digest)))
            {
                throw new InvalidDataException("The trusted execution plan contains an ambiguous managed assembly name.");
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidDataException("The trusted execution plan contains no managed assemblies.");
        }

        payloads = candidates;
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name) =>
        Resolve(name, new ReaderParameters());

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);

        if (!payloads.TryGetValue(name.Name, out var payload))
        {
            throw new AssemblyResolutionException(name);
        }

        var bytes = ReadValidatedBytes(payload);
        if (cache.TryGetValue(name.FullName, out var cached))
        {
            return cached;
        }

        var stream = new MemoryStream(bytes, writable: false);
        AssemblyDefinition assembly;
        try
        {
            assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters
            {
                AssemblyResolver = this,
                ReadingMode = ReadingMode.Deferred,
                ReadSymbols = false,
                InMemory = true,
            });
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        if (!assembly.Name.FullName.Equals(name.FullName, StringComparison.Ordinal))
        {
            assembly.Dispose();
            throw new InvalidDataException("A trusted managed dependency has an unexpected assembly identity.");
        }

        cache.Add(name.FullName, assembly);
        return assembly;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var assembly in cache.Values)
        {
            assembly.Dispose();
        }

        cache.Clear();
    }

    private static byte[] ReadValidatedBytes(ValidatedAssemblyPayload payload)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(payload.Path);
            if (!info.Exists || info.Length != payload.Size)
            {
                throw new InvalidDataException("A trusted managed dependency changed size before resolution.");
            }

            bytes = File.ReadAllBytes(payload.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("A trusted managed dependency could not be read.", exception);
        }

        if (bytes.LongLength != payload.Size || Digest(bytes) != payload.Digest)
        {
            throw new InvalidDataException("A trusted managed dependency changed digest before resolution.");
        }

        return bytes;
    }

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static Sha256Digest Digest(ReadOnlySpan<byte> bytes) =>
        Sha256Digest.Parse(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private sealed record ValidatedAssemblyPayload(string Path, long Size, Sha256Digest Digest);
}
