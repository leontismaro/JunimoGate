using System.Security.Cryptography;
using JunimoGate.Extraction;
using Mono.Cecil;

namespace JunimoGate.Rewriter.Tests;

internal static class ValidatedExecutionPlanAssemblyResolverTests
{
    public static void CachePrecedesFileValidation(string root)
    {
        var workspace = Path.Combine(root, "resolver-cache", "workspace");
        Directory.CreateDirectory(workspace);
        var relativePath = "assemblies/Dependency.dll";
        var path = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var assembly = AssemblyDefinition.CreateAssembly(
                   new AssemblyNameDefinition("Dependency", new Version(1, 0, 0, 0)),
                   "Dependency",
                   ModuleKind.Dll))
        {
            assembly.Write(path);
        }

        var bytes = File.ReadAllBytes(path);
        var plan = new ValidatedExecutionPlan(
            "com.example.game",
            "1.0",
            1,
            "arm64-v8a",
            new string('a', 64),
            workspace,
            new string('b', 64),
            DateTimeOffset.UtcNow,
            [new ValidatedWorkspacePayload(
                "assembly",
                relativePath,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)))]);

        using var resolver = new ValidatedExecutionPlanAssemblyResolver(plan);
        var identity = new AssemblyNameReference("Dependency", new Version(1, 0, 0, 0));
        var first = resolver.Resolve(identity);
        File.Delete(path);
        var second = resolver.Resolve(identity);

        if (!ReferenceEquals(first, second))
            throw new InvalidOperationException("Expected Cecil to reuse the previously validated assembly definition.");

        try
        {
            resolver.Resolve(new AssemblyNameReference("Dependency", new Version(2, 0, 0, 0)));
            throw new InvalidOperationException("A different full identity must not reuse the cached assembly definition.");
        }
        catch (InvalidDataException)
        {
            // A full-identity cache miss reaches file validation, which now fails because the file was removed.
        }
    }
}
