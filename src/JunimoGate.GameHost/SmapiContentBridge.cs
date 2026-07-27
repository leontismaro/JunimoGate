using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;

namespace JunimoGate.GameHost;

internal static class SmapiContentBridge
{
    private const string HarmonyId = "org.junimogate.smapi.content.v1";
    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? entries;
    private static bool patched;

    internal static void Install(PreparedRuntimeFiles runtimeFiles)
    {
        var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var item in runtimeFiles.ContentPaths)
            next.Add(item.Key, new Entry(item.Value));
        lock (Gate)
        {
            if (!patched)
            {
                var harmony = new Harmony(HarmonyId);
                harmony.Patch(AccessTools.Method(typeof(TitleContainer), nameof(TitleContainer.OpenStream), [typeof(string)]),
                    prefix: new HarmonyMethod(typeof(SmapiContentBridge), nameof(OpenPrefix)));
                var reader = typeof(ContentTypeReaderManager).GetMethod("LoadAssetReaders", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(ContentReader)], null)
                    ?? throw new MissingMethodException(typeof(ContentTypeReaderManager).FullName, "LoadAssetReaders");
                harmony.Patch(reader, transpiler: new HarmonyMethod(typeof(SmapiContentBridge), nameof(ReaderTranspiler)));
                patched = true;
            }
            entries = next;
        }
    }

    internal static void Detach() { lock (Gate) entries = null; }

    private static bool OpenPrefix(string name, ref Stream __result)
    {
        var relative = name.Replace('\\', '/').TrimStart('/');
        Entry entry;
        lock (Gate)
        {
            if (entries is null || !entries.TryGetValue(relative, out entry!)) throw new FileNotFoundException("The requested Content file is not prepared.");
        }
        var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        __result = stream;
        return false;
    }

    private static IEnumerable<CodeInstruction> ReaderTranspiler(IEnumerable<CodeInstruction> source)
    {
        var target = typeof(Type).GetMethod(nameof(Type.GetType), BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null)!;
        var replacement = typeof(SmapiContentBridge).GetMethod(nameof(ResolveReaderType), BindingFlags.NonPublic | BindingFlags.Static)!;
        var count = 0;
        foreach (var instruction in source)
        {
            if (instruction.Calls(target)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; count++; }
            yield return instruction;
        }
        if (count != 1) throw new InvalidDataException("The MonoGame reader resolver shape changed.");
    }

    private static Type? ResolveReaderType(string name) => Type.GetType(
        name,
        ResolveReaderAssembly,
        static (assembly, typeName, ignoreCase) =>
        {
            if (assembly is not null)
                return assembly.GetType(typeName, throwOnError: false, ignoreCase);
            if (typeName.StartsWith("Microsoft.Xna.Framework.Content.", StringComparison.Ordinal))
                return typeof(ContentTypeReader).Assembly.GetType(typeName, throwOnError: false, ignoreCase);
            return Type.GetType(typeName, throwOnError: false, ignoreCase);
        },
        throwOnError: false,
        ignoreCase: false);

    private static Assembly? ResolveReaderAssembly(AssemblyName requested)
    {
        if (requested.Name is "Microsoft.Xna.Framework" or "MonoGame.Framework")
            return typeof(ContentTypeReader).Assembly;
        return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
            assembly.GetName().Name?.Equals(requested.Name, StringComparison.OrdinalIgnoreCase) == true);
    }
    private sealed record Entry(string Path);
}
