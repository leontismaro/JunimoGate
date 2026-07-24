using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using JunimoGate.RuntimeProbe.Target;
using MonoMod.Utils;

namespace JunimoGate.RuntimeProbe.Core;

internal static class MonoManagedEntryPointDiagnostic
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MonoCompileMethod(IntPtr method);

    public static bool HarmonyWritesAllowed { get; private set; } = true;

    public static IReadOnlyDictionary<string, string> Inspect()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["detectedRuntime"] = PlatformDetection.Runtime.ToString(),
            ["detectedOperatingSystem"] = PlatformDetection.OS.ToString(),
        };

        if (PlatformDetection.Runtime != RuntimeKind.Mono || PlatformDetection.OS != OSKind.Android)
        {
            HarmonyWritesAllowed = true;
            details["diagnosticRequired"] = "false";
            details["harmonyWritesAllowed"] = "true";
            return details;
        }

        HarmonyWritesAllowed = false;
        details["diagnosticRequired"] = "true";
        details["harmonyWritesAllowed"] = "false";

        var method = typeof(RuntimeProbeTarget).GetMethod(
            "HarmonyPatched",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RuntimeProbeTarget).FullName, "HarmonyPatched");
        var handle = method.MethodHandle;
        var monoMethod = handle.Value;
        var unmanagedWrapper = handle.GetFunctionPointer();
        var compiledMethod = CompileMonoMethod(monoMethod, details);
        var ldftnPointer = method.GetLdftnPointer();
        var patchedRuntimeEntry = GetPatchedRuntimeEntryPoint(method);

        AddPointer(details, "monoMethodHandle", monoMethod, includeWords: true);
        AddPointer(details, "getFunctionPointer", unmanagedWrapper, includeWords: true);
        AddPointer(details, "ldftnPointer", ldftnPointer, includeWords: true);
        AddPointer(details, "monoCompileMethod", compiledMethod, includeWords: true);
        AddPointer(details, "patchedRuntimeEntryPoint", patchedRuntimeEntry, includeWords: true);
        details["wrapperEqualsCompiledMethod"] = (unmanagedWrapper == compiledMethod).ToString();
        details["ldftnEqualsWrapper"] = (ldftnPointer == unmanagedWrapper).ToString();
        details["ldftnEqualsCompiledMethod"] = (ldftnPointer == compiledMethod).ToString();
        details["patchedRuntimeEntryEqualsCompiledMethod"] = (patchedRuntimeEntry == compiledMethod).ToString();

        var compiledMapping = FindMapping(Untag(unchecked((nuint)compiledMethod)));
        var patchedMapping = FindMapping(Untag(unchecked((nuint)patchedRuntimeEntry)));
        var patchedEntryIsExecutable = patchedMapping?.Permissions.Contains('x') == true;
        HarmonyWritesAllowed = patchedRuntimeEntry == compiledMethod
            && patchedEntryIsExecutable
            && compiledMapping?.Permissions.Contains('x') == true;
        details["harmonyWritesAllowed"] = HarmonyWritesAllowed.ToString();
        if (!HarmonyWritesAllowed)
        {
            details["writeBlockReason"] = "Patched MonoRuntime entry point must equal mono_compile_method and reside in an executable mapping.";
        }

        return details;
    }

    internal static string CapturePatchedRuntimeEntryBytes(MethodBase method, int byteCount = 32)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);

        if (PlatformDetection.Runtime != RuntimeKind.Mono || PlatformDetection.OS != OSKind.Android)
        {
            return "not-required";
        }

        var pointer = GetPatchedRuntimeEntryPoint(method);
        var mapping = FindMapping(Untag(unchecked((nuint)pointer)));
        if (mapping is null || !mapping.Permissions.StartsWith('r'))
        {
            throw new InvalidOperationException(
                $"Patched runtime entry 0x{unchecked((nuint)pointer):x16} is not in a readable mapping.");
        }

        var bytes = new byte[byteCount];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IntPtr GetPatchedRuntimeEntryPoint(MethodBase method)
    {
        var harmonyAssembly = typeof(HarmonyLib.Harmony).Assembly;
        var platformTripleType = harmonyAssembly.GetType(
            "MonoMod.Core.Platforms.PlatformTriple",
            throwOnError: true,
            ignoreCase: false)
            ?? throw new TypeLoadException("MonoMod.Core.Platforms.PlatformTriple was not found in 0Harmony.");
        var current = platformTripleType
            .GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null)
            ?? throw new MissingMemberException(platformTripleType.FullName, "Current");
        var runtime = platformTripleType
            .GetProperty("Runtime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(current)
            ?? throw new MissingMemberException(platformTripleType.FullName, "Runtime");
        var getMethodEntryPoint = runtime.GetType().GetMethod(
            "GetMethodEntryPoint",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(MethodBase)],
            modifiers: null)
            ?? throw new MissingMethodException(runtime.GetType().FullName, "GetMethodEntryPoint");
        return (IntPtr)(getMethodEntryPoint.Invoke(runtime, [method])
            ?? throw new InvalidOperationException("Patched MonoRuntime.GetMethodEntryPoint returned null."));
    }

    private static IntPtr CompileMonoMethod(IntPtr monoMethod, IDictionary<string, string> details)
    {
        var libraryNames = new[] { "libmonosgen-2.0.so", "monosgen-2.0", "libmonosgen-2.0" };
        IntPtr library = IntPtr.Zero;
        string? loadedName = null;
        foreach (var name in libraryNames)
        {
            if (NativeLibrary.TryLoad(name, out library))
            {
                loadedName = name;
                break;
            }
        }

        if (library == IntPtr.Zero)
        {
            throw new DllNotFoundException($"Could not load Mono runtime using: {string.Join(", ", libraryNames)}.");
        }

        details["monoNativeLibrary"] = loadedName!;
        var export = NativeLibrary.GetExport(library, "mono_compile_method");
        var compile = Marshal.GetDelegateForFunctionPointer<MonoCompileMethod>(export);
        var result = compile(monoMethod);
        if (result == IntPtr.Zero)
        {
            throw new InvalidOperationException("mono_compile_method returned null.");
        }

        return result;
    }

    private static void AddPointer(
        IDictionary<string, string> details,
        string name,
        IntPtr pointer,
        bool includeWords)
    {
        var raw = unchecked((nuint)pointer);
        var untagged = Untag(raw);
        details[$"{name}Raw"] = $"0x{raw:x16}";
        details[$"{name}Untagged"] = $"0x{untagged:x16}";

        var mapping = FindMapping(untagged);
        details[$"{name}Mapping"] = mapping?.Line ?? "not-found";
        details[$"{name}Permissions"] = mapping?.Permissions ?? "unknown";

        if (!includeWords || mapping is null || !mapping.Permissions.StartsWith('r'))
        {
            return;
        }

        try
        {
            var bytes = new byte[32];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            details[$"{name}Bytes32"] = Convert.ToHexString(bytes).ToLowerInvariant();
            for (var index = 0; index < 4; index++)
            {
                var word = BitConverter.ToUInt64(bytes, index * sizeof(ulong));
                var candidate = Untag((nuint)word);
                details[$"{name}Word{index}"] = $"0x{word:x16}";
                details[$"{name}Word{index}Mapping"] = FindMapping(candidate)?.Line ?? "not-found";
            }
        }
        catch (Exception exception)
        {
            details[$"{name}ReadError"] = $"{exception.GetType().FullName}: {exception.Message}";
        }
    }

    private static nuint Untag(nuint value)
        => IntPtr.Size == sizeof(ulong)
            ? value & unchecked((nuint)0x00ff_ffff_ffff_ffffUL)
            : value;

    private static MemoryMapping? FindMapping(nuint pointer)
    {
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            var separator = fields[0].IndexOf('-');
            if (separator <= 0
                || !nuint.TryParse(fields[0].AsSpan(0, separator), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var start)
                || !nuint.TryParse(fields[0].AsSpan(separator + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var end)
                || pointer < start
                || pointer >= end)
            {
                continue;
            }

            return new MemoryMapping(fields[1], line);
        }

        return null;
    }

    private sealed record MemoryMapping(string Permissions, string Line);
}
