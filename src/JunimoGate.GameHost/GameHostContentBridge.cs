using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Android.Util;
using HarmonyLib;
using JunimoGate.Extraction;
using JunimoGate.Rewriter;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;

namespace JunimoGate.GameHost;

/// <summary>
/// Redirects the public MonoGame TitleContainer to the exact Content files owned by one freshly
/// validated M4 execution plan. Every open rechecks the plan-owned file through the same handle.
/// </summary>
internal static class GameHostContentBridge
{
    private const string HarmonyId = "org.junimogate.gamehost.content.v1";
    private static readonly object SessionLock = new();
    private static ContentSession? session;
    private static bool patched;
    private static long successfulOpenCount;
    private static long successfulOpenBytes;
    private static long successfulReaderTypeCount;
    private static long readerResolverInvocationCount;
    private static long readerAssemblyResolutionCount;

    [DynamicDependency(nameof(OpenStreamPrefix), typeof(GameHostContentBridge))]
    [DynamicDependency(nameof(LoadAssetReadersTranspiler), typeof(GameHostContentBridge))]
    [DynamicDependency(nameof(ResolveContentReaderType), typeof(GameHostContentBridge))]
    internal static void Install(
        ValidatedExecutionPlan plan,
        GameHostManagedAssemblyLoadContext loadContext)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(loadContext);
        if (!plan.PackageName.Equals(GameHostRecipeCatalog.TestedPlayPackageName, StringComparison.Ordinal) ||
            !plan.VersionName.Equals(GameHostRecipeCatalog.TestedPlayVersionName, StringComparison.Ordinal) ||
            plan.LongVersionCode != GameHostRecipeCatalog.TestedPlayLongVersionCode ||
            !plan.SelectedAbi.Equals(GameHostRecipeCatalog.TestedPlayAbi, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Content bridge requires the exact trusted game identity.");
        }

        var content = plan.Payloads
            .Where(static payload => payload.Kind.Equals("content", StringComparison.Ordinal))
            .OrderBy(static payload => payload.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (content.Length == 0 ||
            content.Any(static payload => !IsCanonicalContentPath(payload.RelativePath)))
        {
            throw new InvalidDataException("The trusted execution plan has no valid Content payload set.");
        }

        var entries = new Dictionary<string, ContentEntry>(content.Length, StringComparer.Ordinal);
        foreach (var payload in content)
        {
            var path = ResolveContainedFile(plan.WorkspacePath, payload.RelativePath)
                ?? throw new InvalidDataException("A trusted Content path escaped its workspace.");
            if (!entries.TryAdd(
                    payload.RelativePath,
                    new ContentEntry(plan.WorkspacePath, path, payload.Size, payload.Sha256)))
            {
                throw new InvalidDataException("The trusted Content payload set contains a duplicate path.");
            }
        }

        lock (SessionLock)
        {
            if (session is not null)
            {
                if (ReferenceEquals(session.LoadContext, loadContext) &&
                    session.WorkspaceKey.Equals(plan.WorkspaceKey, StringComparison.Ordinal) &&
                    session.IdentityDigest.Equals(plan.IdentityDigest, StringComparison.Ordinal))
                {
                    return;
                }

                throw new InvalidOperationException("A different GameHost Content session is already installed.");
            }

            if (!patched)
            {
                var harmony = new Harmony(HarmonyId);
                var streamTarget = AccessTools.Method(typeof(TitleContainer), nameof(TitleContainer.OpenStream), [typeof(string)])
                    ?? throw new MissingMethodException(typeof(TitleContainer).FullName, nameof(TitleContainer.OpenStream));
                var streamPrefix = typeof(GameHostContentBridge).GetMethod(
                    nameof(OpenStreamPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(GameHostContentBridge).FullName, nameof(OpenStreamPrefix));
                harmony.Patch(streamTarget, prefix: new HarmonyMethod(streamPrefix));

                var readerTarget = typeof(ContentTypeReaderManager).GetMethod(
                    "LoadAssetReaders",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    types: [typeof(ContentReader)],
                    modifiers: null)
                    ?? throw new MissingMethodException(typeof(ContentTypeReaderManager).FullName, "LoadAssetReaders");
                var readerTranspiler = typeof(GameHostContentBridge).GetMethod(
                    nameof(LoadAssetReadersTranspiler),
                    BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(typeof(GameHostContentBridge).FullName, nameof(LoadAssetReadersTranspiler));
                harmony.Patch(
                    readerTarget,
                    transpiler: new HarmonyMethod(readerTranspiler));
                patched = true;
            }

            session = new ContentSession(plan.WorkspaceKey, plan.IdentityDigest, loadContext, entries);
        }
    }

    internal static void Detach(GameHostManagedAssemblyLoadContext? loadContext)
    {
        if (loadContext is null)
        {
            return;
        }

        lock (SessionLock)
        {
            if (session is not null && ReferenceEquals(session.LoadContext, loadContext))
            {
                session = null;
            }
        }
    }

    private static IEnumerable<CodeInstruction> LoadAssetReadersTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var source = typeof(Type).GetMethod(
            nameof(Type.GetType),
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(string)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(Type).FullName, nameof(Type.GetType));
        var replacement = typeof(GameHostContentBridge).GetMethod(
            nameof(ResolveContentReaderType),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(GameHostContentBridge).FullName, nameof(ResolveContentReaderType));

        var rewritten = instructions.ToList();
        var replaced = 0;
        foreach (var instruction in rewritten)
        {
            if (!instruction.Calls(source))
            {
                continue;
            }

            instruction.opcode = OpCodes.Call;
            instruction.operand = replacement;
            replaced++;
        }

        if (replaced != 1)
        {
            throw new InvalidDataException("The MonoGame Content reader resolver call shape changed.");
        }

        return rewritten;
    }

    private static Type? ResolveContentReaderType(string typeName)
    {
        var invocation = Interlocked.Increment(ref readerResolverInvocationCount);
        if (invocation <= 8)
        {
            Log.Info("JunimoGate.GameHost", $"trusted-content-reader-resolver-entered:count={invocation}");
        }

        try
        {
            ContentSession current;
            lock (SessionLock)
            {
                current = session ?? throw new InvalidOperationException(
                    "No trusted GameHost Content reader session is installed.");
            }

            var result = Type.GetType(
                typeName,
                assemblyName => ResolveReaderAssembly(current.LoadContext, assemblyName),
                ResolveReaderType,
                throwOnError: false,
                ignoreCase: false);
            if (result is not null)
            {
                var count = Interlocked.Increment(ref successfulReaderTypeCount);
                if (count <= 8 || count % 50 == 0)
                {
                    Log.Info("JunimoGate.GameHost", $"trusted-content-reader-type-resolved:count={count}");
                }
            }
            else
            {
                Log.Error(
                    "JunimoGate.GameHost",
                    $"trusted-content-reader-type-null:type={BoundedReaderTypeName(typeName)}");
            }

            return result;
        }
        catch (Exception exception)
        {
            Log.Error(
                "JunimoGate.GameHost",
                $"trusted-content-reader-type-failed:class={exception.GetType().Name}");
            return null;
        }
    }

    private static Type? ResolveReaderType(Assembly? assembly, string name, bool ignoreCase)
    {
        if (assembly is not null)
        {
            return assembly.GetType(name, throwOnError: false, ignoreCase);
        }

        if (name.StartsWith("Microsoft.Xna.Framework.Content.", StringComparison.Ordinal))
        {
            return typeof(ContentTypeReader).Assembly.GetType(name, throwOnError: false, ignoreCase);
        }

        return Type.GetType(name, throwOnError: false, ignoreCase);
    }

    private static Assembly? ResolveReaderAssembly(
        GameHostManagedAssemblyLoadContext loadContext,
        AssemblyName requested)
    {
        var resolution = Interlocked.Increment(ref readerAssemblyResolutionCount);
        var existing = loadContext.Assemblies
            .Concat(AssemblyLoadContext.Default.Assemblies)
            .FirstOrDefault(assembly =>
                assembly.GetName().Name?.Equals(requested.Name, StringComparison.OrdinalIgnoreCase) == true &&
                (requested.Version is null || assembly.GetName().Version == requested.Version));
        if (existing is not null)
        {
            if (resolution <= 16)
            {
                var context = AssemblyLoadContext.GetLoadContext(existing) == loadContext ? "sealed" : "default";
                Log.Info(
                    "JunimoGate.GameHost",
                    $"trusted-content-reader-assembly:name={requested.Name ?? "unknown"}:context={context}");
            }

            return existing;
        }

        var loaded = loadContext.LoadFromAssemblyName(requested);
        if (resolution <= 16)
        {
            Log.Info(
                "JunimoGate.GameHost",
                $"trusted-content-reader-assembly:name={requested.Name ?? "unknown"}:context=sealed-loaded");
        }

        return loaded;
    }

    private static string BoundedReaderTypeName(string typeName)
    {
        var normalized = typeName.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Contains("/data/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/storage/", StringComparison.OrdinalIgnoreCase))
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return $"type-sha256:{Convert.ToHexStringLower(digest)}";
        }

        const int maximumLength = 1400;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "…";
    }

    private static bool OpenStreamPrefix(string name, ref Stream __result)
    {
        try
        {
            __result = OpenTrustedStream(name);
            var count = Interlocked.Increment(ref successfulOpenCount);
            var bytes = Interlocked.Add(ref successfulOpenBytes, __result.Length);
            if (count <= 8 || count % 50 == 0)
            {
                Log.Info("JunimoGate.GameHost", $"trusted-content-open-succeeded:count={count}:bytes={bytes}");
            }

            return false;
        }
        catch (Exception exception)
        {
            var nameHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name ?? string.Empty)));
            Log.Error(
                "JunimoGate.GameHost",
                $"trusted-content-open-failed:class={ClassifyOpenFailure(exception)}:name-sha256={nameHash}");
            throw;
        }
    }

    private static string ClassifyOpenFailure(Exception exception) => exception switch
    {
        FileNotFoundException => "unlisted",
        InvalidDataException => "guard",
        UnauthorizedAccessException => "access",
        IOException => "io",
        _ => exception.GetType().Name,
    };

    private static Stream OpenTrustedStream(string requestedName)
    {
        var relativePath = NormalizeRequestedPath(requestedName);
        ContentEntry entry;
        lock (SessionLock)
        {
            var current = session ?? throw new InvalidOperationException(
                "No trusted GameHost Content session is installed.");
            if (!current.Entries.TryGetValue(relativePath, out entry!))
            {
                throw new FileNotFoundException("The requested game Content file is not in the trusted workspace.");
            }
        }

        var verifiedPath = ResolveContainedFile(entry.WorkspaceRoot, relativePath)
            ?? throw new InvalidDataException("The trusted Content path is no longer contained.");
        if (!verifiedPath.Equals(entry.Path, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The trusted Content path identity changed.");
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                entry.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != entry.Size)
            {
                throw new InvalidDataException("A trusted Content file size changed.");
            }

            var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            if (!digest.Equals(entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A trusted Content file digest changed.");
            }

            stream.Position = 0;
            var result = stream;
            stream = null;
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static string NormalizeRequestedPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathFullyQualified(name) || name.Contains(':'))
        {
            throw new InvalidDataException("The requested Content path is invalid.");
        }

        var normalized = name.Replace('\\', '/').TrimStart('/');
        if (!IsCanonicalContentPath(normalized))
        {
            throw new InvalidDataException("The requested Content path is outside the trusted Content root.");
        }

        return normalized;
    }

    private static bool IsCanonicalContentPath(string path)
    {
        if (!path.StartsWith("Content/", StringComparison.Ordinal) ||
            path.EndsWith("/", StringComparison.Ordinal) ||
            path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/').All(static segment =>
            segment.Length > 0 &&
            !segment.Equals(".", StringComparison.Ordinal) &&
            !segment.Equals("..", StringComparison.Ordinal));
    }

    private static string? ResolveContainedFile(string workspaceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) ||
            !Path.IsPathFullyQualified(workspaceRoot) ||
            !IsCanonicalContentPath(relativePath))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var current = candidate;
        while (!current.Equals(root, StringComparison.Ordinal))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
            if (current.Length == 0)
            {
                return null;
            }
        }

        return candidate;
    }

    private sealed record ContentSession(
        string WorkspaceKey,
        string IdentityDigest,
        GameHostManagedAssemblyLoadContext LoadContext,
        IReadOnlyDictionary<string, ContentEntry> Entries);

    private sealed record ContentEntry(string WorkspaceRoot, string Path, long Size, string Sha256);
}
