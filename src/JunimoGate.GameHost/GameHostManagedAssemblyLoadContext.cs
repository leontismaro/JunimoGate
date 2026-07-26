using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;
using Microsoft.Xna.Framework;

namespace JunimoGate.GameHost;

/// <summary>
/// Non-collectible load context for one exact trusted GameHost session. Game-owned assemblies are
/// accepted only from the sealed source plan; the rewritten target is accepted only from the sealed
/// applied plan. Platform, public MonoGame and GameHost assemblies must come from the default context.
/// </summary>
internal sealed class GameHostManagedAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> PlatformAssemblyNames = CreatePlatformAssemblyNames();

    private static readonly HashSet<string> DefaultNativeModules = new(StringComparer.Ordinal)
    {
        "dl",
        "libc",
        "liblog",
        "libSystem.Globalization.Native",
        "libSystem.IO.Compression.Native",
        "libSystem.Native",
        "libSystem.Security.Cryptography.Native.Android",
        "xa-internal-api",
    };

    private readonly ValidatedGameHostAppliedWorkspacePlan appliedPlan;
    private readonly Dictionary<string, ValidatedWorkspacePayload> sourceAssemblies;
    private readonly object loadLock = new();
    private Assembly? gameAssembly;

    public GameHostManagedAssemblyLoadContext(ValidatedGameHostAppliedWorkspacePlan appliedPlan)
        : base("JunimoGate.GameHost.Play-1.6.15.3", isCollectible: false)
    {
        ArgumentNullException.ThrowIfNull(appliedPlan);
        this.appliedPlan = appliedPlan;
        sourceAssemblies = new Dictionary<string, ValidatedWorkspacePayload>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in appliedPlan.SourceExecutionPlan.Payloads.Where(static payload =>
                     payload.Kind.Equals("assembly", StringComparison.Ordinal)))
        {
            var simpleName = Path.GetFileNameWithoutExtension(payload.RelativePath);
            if (string.IsNullOrWhiteSpace(simpleName) || !sourceAssemblies.TryAdd(simpleName, payload))
            {
                throw new InvalidDataException("The trusted source plan contains ambiguous managed assembly names.");
            }
        }

        if (!sourceAssemblies.ContainsKey("StardewValley"))
        {
            throw new InvalidDataException("The trusted source plan does not contain the exact game target.");
        }
    }

    public Assembly LoadGameAssembly()
    {
        lock (loadLock)
        {
            gameAssembly ??= LoadVerifiedAssembly(
                appliedPlan.OverlayAssemblyPath,
                appliedPlan.OverlayAssemblySize,
                appliedPlan.OverlayAssemblySha256,
                GameHostRecipeCatalog.TestedPlayTargetIdentity,
                GameHostRecipeCatalog.TestedPlayTargetMvid);
            return gameAssembly;
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(simpleName))
        {
            throw new FileLoadException("A managed dependency requested an empty assembly name.");
        }

        if (simpleName.Equals("StardewValley", StringComparison.OrdinalIgnoreCase))
        {
            var target = LoadGameAssembly();
            RequireIdentity(assemblyName, target.GetName());
            return target;
        }

        var exactDefault = FindExactDefaultAssembly(assemblyName);
        if (exactDefault is not null)
        {
            return exactDefault;
        }

        if (IsPlatformAssembly(simpleName))
        {
            // Runtime framework identities are unified by the default Android/.NET binder. Game-owned
            // assemblies with System.* names are not in this exact platform set and remain plan-owned.
            return null;
        }

        if (HasDefaultSimpleNameConflict(assemblyName))
        {
            throw new FileLoadException("A default-context assembly has the requested simple name but a different identity.");
        }

        if (!sourceAssemblies.TryGetValue(simpleName, out var payload))
        {
            throw new FileNotFoundException("The managed dependency is not present in the trusted source plan.", simpleName);
        }

        var sourcePath = ResolveContainedFile(
            appliedPlan.SourceExecutionPlan.WorkspacePath,
            payload.RelativePath);
        var loaded = LoadVerifiedAssembly(
            sourcePath,
            payload.Size,
            payload.Sha256,
            expectedIdentity: null,
            expectedModuleVersionId: null);
        RequireIdentity(assemblyName, loaded.GetName());
        return loaded;
    }

    private Assembly LoadVerifiedAssembly(
        string path,
        long expectedSize,
        string expectedSha256,
        string? expectedIdentity,
        string? expectedModuleVersionId)
    {
        var bytes = ReadAndVerify(path, expectedSize, expectedSha256);
        using var stream = new MemoryStream(bytes, writable: false);
        var assembly = LoadFromStream(stream);
        if (expectedIdentity is not null &&
            !assembly.GetName().FullName.Equals(expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A managed payload identity does not match the trusted plan.");
        }

        if (expectedModuleVersionId is not null &&
            !assembly.ManifestModule.ModuleVersionId.ToString("D").Equals(expectedModuleVersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The rewritten game module identity does not match the approved recipe.");
        }

        NativeLibrary.SetDllImportResolver(assembly, ResolveNativeLibrary);
        return assembly;
    }

    private static byte[] ReadAndVerify(string path, long expectedSize, string expectedSha256)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A trusted managed payload is missing.");
        }

        var info = new FileInfo(path);
        if (info.Length != expectedSize)
        {
            throw new InvalidDataException("A trusted managed payload size changed before load.");
        }

        var bytes = File.ReadAllBytes(path);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!digest.Equals(expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A trusted managed payload digest changed before load.");
        }

        return bytes;
    }

    private static IntPtr ResolveNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        if (DefaultNativeModules.Contains(libraryName))
        {
            return IntPtr.Zero;
        }

        if (libraryName.Equals("rail_api", StringComparison.OrdinalIgnoreCase))
        {
            throw new DllNotFoundException("The desktop Rail native module is not authorized in the Android GameHost.");
        }

        throw new DllNotFoundException("The managed game requested a native module outside the frozen Android allowlist.");
    }

    private static Assembly? FindExactDefaultAssembly(AssemblyName requested) =>
        Default.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().FullName, requested.FullName, StringComparison.Ordinal));

    private static bool HasDefaultSimpleNameConflict(AssemblyName requested) =>
        Default.Assemblies.Any(assembly =>
            string.Equals(assembly.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase));

    private static bool IsPlatformAssembly(string simpleName) =>
        PlatformAssemblyNames.Contains(simpleName);

    private static HashSet<string> CreatePlatformAssemblyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib",
            "System.Runtime",
            "netstandard",
            "mscorlib",
            "Mono.Android",
            "Java.Interop",
            "Microsoft.CSharp",
            "Microsoft.VisualBasic.Core",
        };

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        foreach (var assembly in Default.Assemblies)
        {
            var name = assembly.GetName().Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static void RequireIdentity(AssemblyName requested, AssemblyName actual)
    {
        if (requested.Version is null)
        {
            if (!string.Equals(actual.Name, requested.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileLoadException("The resolved managed assembly simple name does not match the request.");
            }

            return;
        }

        if (!actual.FullName.Equals(requested.FullName, StringComparison.Ordinal))
        {
            throw new FileLoadException("The resolved managed assembly identity does not match the request.");
        }
    }

    private static string ResolveContainedFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathFullyQualified(relativePath) ||
            relativePath.Contains('\\'))
        {
            throw new InvalidDataException("A trusted managed payload path is invalid.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A trusted managed payload path escaped its workspace.");
        }

        var current = candidate;
        while (!current.Equals(normalizedRoot, StringComparison.Ordinal))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A trusted managed payload path contains a reparse point.");
            }

            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException(
                "A trusted managed payload path is not contained by its workspace.");
        }

        return candidate;
    }
}
