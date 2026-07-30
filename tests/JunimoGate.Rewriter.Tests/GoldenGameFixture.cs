using System.Security.Cryptography;
using JunimoGate.Core;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;

namespace JunimoGate.Rewriter.Tests;

internal static class GoldenGameFixture
{
    public static SyntheticGameFixture Create(
        string assemblyDirectory,
        string outputRoot,
        string versionName = "1.6.15.3",
        long versionCode = 245)
    {
        var directory = Path.GetFullPath(assemblyDirectory);
        if (!Path.GetFileName(directory).Equals("assemblies", StringComparison.Ordinal))
            throw new ArgumentException("The golden game directory must be the extracted assemblies directory.");
        var workspace = Directory.GetParent(directory)?.FullName
            ?? throw new ArgumentException("The golden game directory has no workspace parent.");
        var input = Path.Combine(directory, "StardewValley.dll");
        if (!File.Exists(input))
            throw new FileNotFoundException("The golden game directory has no StardewValley.dll.", input);

        var payloads = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var bytes = File.ReadAllBytes(path);
                return new ValidatedWorkspacePayload(
                    "assembly",
                    "assemblies/" + Path.GetFileName(path),
                    bytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)));
            })
            .ToArray();
        Directory.CreateDirectory(outputRoot);
        var plan = new ValidatedExecutionPlan(
            KnownGameCertificate.PlayPackageName,
            versionName,
            versionCode,
            GameInstallationDiscoveryCoordinator.SupportedAbi,
            new string('c', 64),
            workspace,
            new string('d', 64),
            DateTimeOffset.UtcNow,
            payloads);
        return new SyntheticGameFixture(
            workspace,
            input,
            Path.Combine(outputRoot, "StardewValley.dll"),
            plan);
    }
}
