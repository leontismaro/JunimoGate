using System.Reflection;
using HarmonyLib;
using MonoMod.Utils;

namespace JunimoGate.RuntimeProbe.Core;

internal sealed record HarmonyMonoModAndroidSupportResult(
    string DetectedOperatingSystem,
    bool LibraryFixRequired,
    bool LibraryFixApplied,
    string HarmonyInformationalVersion,
    string SystemType,
    string SystemTarget,
    string ArchitectureType,
    string ArchitectureTarget,
    string RuntimeType,
    string RuntimeTarget);

internal static class HarmonyMonoModAndroidSupport
{
    private const string PlatformTripleTypeName = "MonoMod.Core.Platforms.PlatformTriple";
    private const string PatchedVersionMarker = "junimogate.";

    public static HarmonyMonoModAndroidSupportResult Inspect()
    {
        var detectedOperatingSystem = PlatformDetection.OS;
        var harmonyAssembly = typeof(Harmony).Assembly;
        var informationalVersion = harmonyAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? string.Empty;
        var libraryFixRequired = detectedOperatingSystem == OSKind.Android;
        var libraryFixApplied = informationalVersion.Contains(PatchedVersionMarker, StringComparison.OrdinalIgnoreCase);

        if (libraryFixRequired && !libraryFixApplied)
        {
            throw new InvalidOperationException(
                $"Android requires the JunimoGate Harmony/MonoMod source patch, but 0Harmony reports '{informationalVersion}'.");
        }

        var platformTripleType = harmonyAssembly.GetType(
            PlatformTripleTypeName,
            throwOnError: true,
            ignoreCase: false)
            ?? throw new TypeLoadException($"{PlatformTripleTypeName} was not found in {harmonyAssembly.FullName}.");
        var current = GetRequiredProperty(platformTripleType, "Current").GetValue(null)
            ?? throw new InvalidOperationException($"{PlatformTripleTypeName}.Current returned null.");
        var system = GetRequiredProperty(platformTripleType, "System").GetValue(current)
            ?? throw new InvalidOperationException("Platform triple System returned null.");
        var architecture = GetRequiredProperty(platformTripleType, "Architecture").GetValue(current)
            ?? throw new InvalidOperationException("Platform triple Architecture returned null.");
        var runtime = GetRequiredProperty(platformTripleType, "Runtime").GetValue(current)
            ?? throw new InvalidOperationException("Platform triple Runtime returned null.");

        return new HarmonyMonoModAndroidSupportResult(
            detectedOperatingSystem.ToString(),
            libraryFixRequired,
            libraryFixApplied,
            informationalVersion,
            TypeName(system),
            ReadTarget(system),
            TypeName(architecture),
            ReadTarget(architecture),
            TypeName(runtime),
            ReadTarget(runtime));
    }

    private static PropertyInfo GetRequiredProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(type.FullName, name);

    private static string ReadTarget(object value)
    {
        var target = GetRequiredProperty(value.GetType(), "Target").GetValue(value)
            ?? throw new InvalidOperationException($"{value.GetType().FullName}.Target returned null.");
        return target.ToString() ?? target.GetType().FullName ?? target.GetType().Name;
    }

    private static string TypeName(object value) => value.GetType().FullName ?? value.GetType().Name;
}
