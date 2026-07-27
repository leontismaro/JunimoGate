using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using StardewModdingAPI.AndroidHost;

namespace JunimoGate.GameHost;

internal sealed class SmapiDefaultAssemblyLoader : IManagedAssemblyLoader, IDisposable
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "StardewValley", "StardewModdingAPI", "StardewModdingAPI.Toolkit", "StardewModdingAPI.Toolkit.CoreInterfaces",
        "JunimoGate.App", "JunimoGate.GameHost", "JunimoGate.Android", "JunimoGate.Core", "JunimoGate.Extraction", "JunimoGate.Rewriter",
        "MonoGame.Framework", "0Harmony",
    };

    private readonly PreparedGameSnapshot snapshot;
    private readonly Dictionary<string, string> gamePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> modPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string rewriteCache;
    private bool installed;

    public SmapiDefaultAssemblyLoader(PreparedGameSnapshot snapshot)
    {
        this.snapshot = snapshot;
        rewriteCache = Path.Combine(Path.GetDirectoryName(snapshot.InternalDirectory)!, "mod-rewrite-cache");
        Directory.CreateDirectory(rewriteCache);
        foreach (var entry in snapshot.ManagedAssemblies)
        {
            if (string.IsNullOrWhiteSpace(entry.SimpleName) || !gamePaths.TryAdd(entry.SimpleName, ResolveSource(entry.RelativePath)))
                throw new InvalidDataException("The prepared managed assembly index contains a duplicate identity.");
        }
        gamePaths["StardewValley"] = Path.GetFullPath(snapshot.OverlayAssemblyPath);
    }

    public void Install()
    {
        if (installed) return;
        AssemblyLoadContext.Default.Resolving += Resolve;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAppDomain;
        installed = true;
    }

    public Assembly LoadGameAssembly() => LoadIndexed("StardewValley");

    public Assembly LoadFromPath(string absolutePath)
    {
        var path = RequireContained(absolutePath, snapshot.ModsDirectory);
        var name = AssemblyName.GetAssemblyName(path).Name ?? throw new FileLoadException("A Mod assembly has no simple name.");
        RejectShadow(name);
        modPaths[name] = path;
        IndexModDirectory(Path.GetDirectoryName(path)!);
        return LoadExisting(name) ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    public Assembly LoadRewritten(string sourcePath, ReadOnlyMemory<byte> assemblyBytes, ReadOnlyMemory<byte>? symbols)
    {
        _ = RequireContained(sourcePath, snapshot.ModsDirectory);
        var digest = Convert.ToHexStringLower(SHA256.HashData(assemblyBytes.Span));
        var path = Path.Combine(rewriteCache, digest + ".dll");
        if (!File.Exists(path))
        {
            var tmp = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, assemblyBytes.ToArray());
            File.Move(tmp, path, overwrite: false);
        }
        if (symbols is { } pdb && !pdb.IsEmpty)
        {
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdbPath)) File.WriteAllBytes(pdbPath, pdb.ToArray());
        }
        var name = AssemblyName.GetAssemblyName(path).Name ?? throw new FileLoadException("A rewritten Mod assembly has no simple name.");
        RejectShadow(name);
        modPaths[name] = path;
        return LoadExisting(name) ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
    {
        _ = context;
        var name = requested.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var existing = LoadExisting(name);
        if (existing is not null) return existing;
        if (gamePaths.ContainsKey(name)) return LoadIndexed(name);
        if (modPaths.TryGetValue(name, out var modPath)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
        if (ProtectedNames.Contains(name) || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal))
            return null;
        return null;
    }

    private Assembly? ResolveAppDomain(object? sender, ResolveEventArgs args) => Resolve(AssemblyLoadContext.Default, new AssemblyName(args.Name));

    private Assembly LoadIndexed(string name)
    {
        var existing = LoadExisting(name);
        if (existing is not null) return existing;
        if (!gamePaths.TryGetValue(name, out var path)) throw new FileNotFoundException("The prepared assembly index has no requested game assembly.", name);
        if (!File.Exists(path)) throw new FileNotFoundException("A prepared game assembly is missing.");
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private void IndexModDirectory(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var contained = RequireContained(path, snapshot.ModsDirectory);
            var name = AssemblyName.GetAssemblyName(contained).Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            RejectShadow(name);
            if (gamePaths.ContainsKey(name)) throw new FileLoadException("A Mod cannot shadow a game assembly.");
            modPaths.TryAdd(name, contained);
        }
    }

    private static Assembly? LoadExisting(string name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
    private static void RejectShadow(string name)
    {
        if (ProtectedNames.Contains(name) || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal))
            throw new FileLoadException("A Mod attempted to shadow a protected host or framework assembly.");
    }
    private string ResolveSource(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(snapshot.SourceWorkspacePath, relative.Replace('/', Path.DirectorySeparatorChar)));
        return RequireContained(path, snapshot.SourceWorkspacePath);
    }
    private static string RequireContained(string path, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("A managed assembly path escaped its controlled root.");
        return fullPath;
    }
    public void Dispose()
    {
        if (!installed) return;
        AssemblyLoadContext.Default.Resolving -= Resolve;
        AppDomain.CurrentDomain.AssemblyResolve -= ResolveAppDomain;
        installed = false;
    }
}
