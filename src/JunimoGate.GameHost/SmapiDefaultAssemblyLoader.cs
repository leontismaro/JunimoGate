using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using StardewModdingAPI.AndroidHost;

namespace JunimoGate.GameHost;

internal sealed class SmapiDefaultAssemblyLoader : IManagedAssemblyLoader, IDisposable
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "StardewValley", "StardewModdingAPI", "SMAPI.Toolkit", "SMAPI.Toolkit.CoreInterfaces",
        "StardewModdingAPI.Toolkit", "StardewModdingAPI.Toolkit.CoreInterfaces",
        "JunimoGate.App", "JunimoGate.GameHost", "JunimoGate.Android", "JunimoGate.Core", "JunimoGate.Extraction", "JunimoGate.Mods", "JunimoGate.Rewriter",
        "MonoGame.Framework", "0Harmony",
    };
    private readonly string modsRoot;
    private readonly Dictionary<string, string> gamePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegisteredModAssembly> modAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegisteredModAssembly> modAssembliesBySource = new(StringComparer.Ordinal);
    private readonly string loadCache;
    private bool installed;

    public SmapiDefaultAssemblyLoader(
        PreparedRuntimeFiles runtimeFiles,
        string loadCache,
        string modsRoot)
    {
        this.modsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsRoot));
        this.loadCache = Path.GetFullPath(loadCache);
        Directory.CreateDirectory(this.loadCache);
        foreach (var entry in runtimeFiles.ManagedAssemblyPaths)
            gamePaths.Add(entry.Key, entry.Value);
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
        var path = RequireContained(absolutePath, modsRoot);
        var entry = RegisterSource(path);
        return LoadRegistered(entry);
    }

    public Assembly LoadFromBytes(string sourcePath, ReadOnlyMemory<byte> assemblyBytes, ReadOnlyMemory<byte>? symbols)
    {
        var source = RequireContained(sourcePath, modsRoot);
        string loadPath = Materialize(assemblyBytes, symbols);
        var entry = RegisterSource(source, loadPath) with { LoadPath = loadPath };
        modAssemblies[entry.SimpleName] = entry;
        modAssembliesBySource[source] = entry;
        return LoadRegistered(entry);
    }

    public Assembly LoadRewritten(
        string sourcePath,
        ReadOnlyMemory<byte> sourceBytes,
        ReadOnlyMemory<byte> assemblyBytes,
        ReadOnlyMemory<byte>? symbols)
    {
        var source = RequireContained(sourcePath, modsRoot);
        string sourceMaterialized = Materialize(sourceBytes, symbols: null);
        var sourceEntry = RegisterSource(source, sourceMaterialized);
        string path = Materialize(assemblyBytes, symbols);
        var rewrittenIdentity = AssemblyName.GetAssemblyName(path);
        if (!sourceEntry.Identity.FullName.Equals(rewrittenIdentity.FullName, StringComparison.Ordinal))
            throw new FileLoadException("A rewritten Mod assembly changed its assembly identity.", DisplayPath(source));
        var rewritten = sourceEntry with { LoadPath = path };
        modAssemblies[sourceEntry.SimpleName] = rewritten;
        modAssembliesBySource[source] = rewritten;
        return LoadRegistered(rewritten);
    }

    private string Materialize(ReadOnlyMemory<byte> assemblyBytes, ReadOnlyMemory<byte>? symbols)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(assemblyBytes.Span));
        var path = Path.Combine(loadCache, digest + ".dll");
        WriteOnce(path, assemblyBytes);
        if (symbols is { } pdb && !pdb.IsEmpty)
            WriteOnce(Path.ChangeExtension(path, ".pdb"), pdb);
        return path;
    }

    private static void WriteOnce(string path, ReadOnlyMemory<byte> bytes)
    {
        if (File.Exists(path))
            return;
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes.ToArray());
            try
            {
                File.Move(tmp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
    {
        _ = context;
        var name = requested.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        var existing = LoadExisting(name);
        if (existing is not null) return existing;
        if (gamePaths.ContainsKey(name)) return LoadIndexed(name);
        if (modAssemblies.TryGetValue(name, out var mod)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(mod.LoadPath);
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
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private RegisteredModAssembly RegisterSource(string sourcePath, string? identityPath = null)
    {
        if (modAssembliesBySource.TryGetValue(sourcePath, out var registered))
            return registered;

        var identity = AssemblyName.GetAssemblyName(identityPath ?? sourcePath);
        var name = identity.Name;
        if (string.IsNullOrWhiteSpace(name))
            throw new FileLoadException("A Mod assembly has no simple name.", DisplayPath(sourcePath));
        if (IsProtected(name) || gamePaths.ContainsKey(name))
            throw Conflict(name, "host, game, or framework", DisplayPath(sourcePath));
        if (modAssemblies.TryGetValue(name, out var existing))
            throw Conflict(name, DisplayPath(existing.SourcePath), DisplayPath(sourcePath));
        var loaded = LoadExisting(name);
        if (loaded is not null)
            throw Conflict(name, $"loaded assembly {loaded.FullName}", DisplayPath(sourcePath));

        var entry = new RegisteredModAssembly(name, identity, sourcePath, sourcePath);
        modAssemblies.Add(name, entry);
        modAssembliesBySource.Add(sourcePath, entry);
        return entry;
    }

    private static Assembly LoadRegistered(RegisteredModAssembly entry) =>
        LoadExisting(entry.SimpleName) ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(entry.LoadPath);

    private static Assembly? LoadExisting(string name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true);
    private static bool IsProtected(string name) =>
        ProtectedNames.Contains(name) || name.StartsWith("System.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.", StringComparison.Ordinal);
    private static FileLoadException Conflict(string name, string firstOwner, string secondOwner) =>
        new($"Mod assembly identity '{name}' conflicts between '{firstOwner}' and '{secondOwner}'. Disable one of the conflicting Mods.");
    private string DisplayPath(string path) => Path.GetRelativePath(modsRoot, path).Replace(Path.DirectorySeparatorChar, '/');
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

    private sealed record RegisteredModAssembly(
        string SimpleName,
        AssemblyName Identity,
        string SourcePath,
        string LoadPath);
}
