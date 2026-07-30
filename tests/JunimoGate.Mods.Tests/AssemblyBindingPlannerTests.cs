using JunimoGate.Tests;
using Mono.Cecil;
using Mono.Cecil.Cil;
using StardewModdingAPI;
using StardewModdingAPI.AndroidHost;
using StardewModdingAPI.Framework;
using StardewModdingAPI.Framework.ModLoading;

internal static class AssemblyBindingPlannerTests
{
    public static void IgnoresUnreferencedFiles()
    {
        using var fixture = new BindingFixture();
        var mod = fixture.CreateMod("example.only-entry", null, null);
        File.WriteAllBytes(Path.Combine(mod.DirectoryPath, "windows-native.dll"), [0x4d, 0x5a, 0, 0]);
        fixture.WriteLibrary(Path.Combine(mod.DirectoryPath, "Unused.dll"), "UnusedIdentity", new Version(1, 0), ["Unused"]);

        var plan = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, mod);

        TestHarness.Equal(0, plan.Failures.Count);
        TestHarness.False(plan.TryResolve("UnusedIdentity", out _));
        TestHarness.False(plan.TryResolve(Path.GetFileNameWithoutExtension(mod.Manifest.EntryDll), out _));
    }

    public static void IgnoresNonLocalFrameworkReferences()
    {
        using var fixture = new BindingFixture();
        string frameworkName = fixture.UniqueName("DeviceFramework");
        var mod = fixture.CreateMod("example.framework-reference", frameworkName, "RuntimeApi");

        var plan = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, mod);

        TestHarness.Equal(0, plan.Failures.Count);
        TestHarness.False(plan.TryResolve(frameworkName, out _));
    }

    public static void IsolatesMalformedDependencies()
    {
        using var fixture = new BindingFixture();
        string commonName = fixture.UniqueName("MalformedCommon");
        var broken = fixture.CreateMod("a.broken", commonName, "Used");
        var healthy = fixture.CreateMod("b.healthy", null, null);
        File.WriteAllBytes(Path.Combine(broken.DirectoryPath, $"{commonName}.dll"), [0x4d, 0x5a, 0, 0]);

        var plan = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, broken, healthy);

        TestHarness.True(plan.Failures.ContainsKey(broken));
        TestHarness.False(plan.Failures.ContainsKey(healthy));
    }

    public static void StrictUsesByteIdentity()
    {
        using var fixture = new BindingFixture();
        string commonName = fixture.UniqueName("StrictCommon");
        var first = fixture.CreateMod("a.first", commonName, "Used");
        var second = fixture.CreateMod("b.second", commonName, "Used");
        string firstLibrary = fixture.WriteLibrary(
            Path.Combine(first.DirectoryPath, $"{commonName}.dll"), commonName, new Version(1, 0), ["Used"]);
        File.Copy(firstLibrary, Path.Combine(second.DirectoryPath, $"{commonName}.dll"));

        var shared = fixture.Build(ModAssemblyBindingPolicy.Strict, second, first);
        TestHarness.Equal(0, shared.Failures.Count);
        TestHarness.True(shared.TryResolve(commonName, out var selected));
        TestHarness.Equal(Path.GetFullPath(firstLibrary), selected);

        fixture.WriteLibrary(
            Path.Combine(second.DirectoryPath, $"{commonName}.dll"), commonName, new Version(1, 0), ["Used", "Different"]);
        var conflict = fixture.Build(ModAssemblyBindingPolicy.Strict, second, first);
        TestHarness.False(conflict.Failures.ContainsKey(first));
        TestHarness.True(conflict.Failures.ContainsKey(second));
    }

    public static void FirstLoadedIsStable()
    {
        using var fixture = new BindingFixture();
        string commonName = fixture.UniqueName("FirstCommon");
        var first = fixture.CreateMod("a.first", commonName, "V1");
        var second = fixture.CreateMod("b.second", commonName, "V2");
        string firstLibrary = fixture.WriteLibrary(
            Path.Combine(first.DirectoryPath, $"{commonName}.dll"), commonName, new Version(1, 0), ["V1"]);
        fixture.WriteLibrary(
            Path.Combine(second.DirectoryPath, $"{commonName}.dll"), commonName, new Version(2, 0), ["V2"]);

        var plan = fixture.Build(ModAssemblyBindingPolicy.FirstLoaded, second, first);

        TestHarness.Equal(0, plan.Failures.Count);
        TestHarness.True(plan.TryResolve(commonName, out var selected));
        TestHarness.Equal(Path.GetFullPath(firstLibrary), selected);
    }

    public static void HighestCompatibleValidatesConsumerReferences()
    {
        using var fixture = new BindingFixture();
        string commonName = fixture.UniqueName("HighestCommon");
        var oldConsumer = fixture.CreateMod("a.old", commonName, "Used");
        var newConsumer = fixture.CreateMod("b.new", commonName, "NewApi");
        fixture.WriteLibrary(
            Path.Combine(oldConsumer.DirectoryPath, $"{commonName}.dll"), commonName, new Version(1, 0), ["Used"]);
        string highest = fixture.WriteLibrary(
            Path.Combine(newConsumer.DirectoryPath, $"{commonName}.dll"), commonName, new Version(2, 0), ["Used", "NewApi"]);

        var compatible = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, oldConsumer, newConsumer);
        TestHarness.Equal(0, compatible.Failures.Count);
        TestHarness.True(compatible.TryResolve(commonName, out var selected));
        TestHarness.Equal(Path.GetFullPath(highest), selected);

        fixture.WriteLibrary(
            Path.Combine(newConsumer.DirectoryPath, $"{commonName}.dll"), commonName, new Version(2, 0), ["NewApi"]);
        var incompatible = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, oldConsumer, newConsumer);
        TestHarness.True(incompatible.Failures.ContainsKey(oldConsumer));
        TestHarness.False(incompatible.Failures.ContainsKey(newConsumer));
    }

    public static void HighestCompatibleRejectsAmbiguousTies()
    {
        using var fixture = new BindingFixture();
        string commonName = fixture.UniqueName("TiedCommon");
        var first = fixture.CreateMod("a.first", commonName, "One");
        var second = fixture.CreateMod("b.second", commonName, "Two");
        var consumer = fixture.CreateMod("c.consumer", commonName, "One");
        fixture.WriteLibrary(
            Path.Combine(first.DirectoryPath, $"{commonName}.dll"), commonName, new Version(2, 0), ["One"]);
        fixture.WriteLibrary(
            Path.Combine(second.DirectoryPath, $"{commonName}.dll"), commonName, new Version(2, 0), ["Two"]);

        var plan = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, first, second, consumer);

        TestHarness.True(plan.Failures.ContainsKey(first));
        TestHarness.True(plan.Failures.ContainsKey(second));
        TestHarness.True(plan.Failures.ContainsKey(consumer));
        TestHarness.False(plan.TryResolve(commonName, out _));
    }

    public static void HighestCompatiblePreservesTypeAssemblyScope()
    {
        using var fixture = new BindingFixture();
        string commonName = fixture.UniqueName("ScopedCommon");
        string oldScope = fixture.UniqueName("OldScope");
        string newScope = fixture.UniqueName("NewScope");
        var oldConsumer = fixture.CreateModWithScopedParameter("a.old", commonName, "Use", oldScope);
        var newConsumer = fixture.CreateModWithScopedParameter("b.new", commonName, "Use", newScope);
        fixture.WriteLibraryWithScopedParameter(
            Path.Combine(oldConsumer.DirectoryPath, $"{commonName}.dll"), commonName, new Version(1, 0), "Use", oldScope);
        fixture.WriteLibraryWithScopedParameter(
            Path.Combine(newConsumer.DirectoryPath, $"{commonName}.dll"), commonName, new Version(2, 0), "Use", newScope);

        var plan = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, oldConsumer, newConsumer);

        TestHarness.True(plan.Failures.ContainsKey(oldConsumer));
        TestHarness.False(plan.Failures.ContainsKey(newConsumer));
        TestHarness.True(plan.TryResolve(commonName, out _));
    }

    public static void HighestCompatibleResolvesInheritedMembers()
    {
        using var fixture = new BindingFixture();
        string commonName = fixture.UniqueName("InheritedCommon");
        var oldConsumer = fixture.CreateModWithInheritedReferences("a.old", commonName);
        var newConsumer = fixture.CreateModWithInheritedReferences("b.new", commonName);
        fixture.WriteLibraryWithInheritedMembers(
            Path.Combine(oldConsumer.DirectoryPath, $"{commonName}.dll"), commonName, new Version(1, 0));
        string highest = fixture.WriteLibraryWithInheritedMembers(
            Path.Combine(newConsumer.DirectoryPath, $"{commonName}.dll"), commonName, new Version(2, 0));

        var plan = fixture.Build(ModAssemblyBindingPolicy.HighestCompatible, oldConsumer, newConsumer);

        TestHarness.Equal(0, plan.Failures.Count);
        TestHarness.True(plan.TryResolve(commonName, out var selected));
        TestHarness.Equal(Path.GetFullPath(highest), selected);
    }
}

internal sealed class BindingFixture : IDisposable
{
    private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-binding-{Guid.NewGuid():N}"));

    public BindingFixture() => Directory.CreateDirectory(root);

    public string UniqueName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public TestMod CreateMod(string id, string? dependencyAssembly, string? requiredMethod)
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        string entryName = UniqueName("Entry");
        string entryPath = Path.Combine(directory, $"{entryName}.dll");
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(entryName, new Version(1, 0)),
            entryName,
            ModuleKind.Dll);
        var type = new TypeDefinition("Tests", "Entry", TypeAttributes.Public | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);
        if (dependencyAssembly != null && requiredMethod != null)
        {
            var reference = new AssemblyNameReference(dependencyAssembly, new Version(1, 0));
            assembly.MainModule.AssemblyReferences.Add(reference);
            var dependencyType = new TypeReference("Tests", "Api", assembly.MainModule, reference);
            var target = new MethodReference(requiredMethod, assembly.MainModule.TypeSystem.Void, dependencyType)
            {
                HasThis = false,
            };
            var run = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
            type.Methods.Add(run);
            run.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
            run.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        }
        assembly.Write(entryPath);
        return new TestMod(directory, new TestManifest(id, Path.GetFileName(entryPath), []));
    }

    public string WriteLibrary(string path, string assemblyName, Version version, IReadOnlyList<string> methods)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(assemblyName, version),
            assemblyName,
            ModuleKind.Dll);
        var type = new TypeDefinition("Tests", "Api", TypeAttributes.Public | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);
        foreach (string methodName in methods)
        {
            var method = new MethodDefinition(methodName, MethodAttributes.Public | MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }
        assembly.Write(path);
        return Path.GetFullPath(path);
    }

    public TestMod CreateModWithScopedParameter(string id, string dependencyAssembly, string requiredMethod, string parameterAssembly)
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        string entryName = UniqueName("Entry");
        string entryPath = Path.Combine(directory, $"{entryName}.dll");
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(entryName, new Version(1, 0)),
            entryName,
            ModuleKind.Dll);
        var type = new TypeDefinition("Tests", "Entry", TypeAttributes.Public | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);
        var dependencyReference = new AssemblyNameReference(dependencyAssembly, new Version(1, 0));
        var parameterReference = new AssemblyNameReference(parameterAssembly, new Version(1, 0));
        assembly.MainModule.AssemblyReferences.Add(dependencyReference);
        assembly.MainModule.AssemblyReferences.Add(parameterReference);
        var dependencyType = new TypeReference("Tests", "Api", assembly.MainModule, dependencyReference);
        var parameterType = new TypeReference("Tests", "ScopedValue", assembly.MainModule, parameterReference);
        var target = new MethodReference(requiredMethod, assembly.MainModule.TypeSystem.Void, dependencyType)
        {
            HasThis = false,
        };
        target.Parameters.Add(new ParameterDefinition(parameterType));
        var run = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
        type.Methods.Add(run);
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        assembly.Write(entryPath);
        return new TestMod(directory, new TestManifest(id, Path.GetFileName(entryPath), []));
    }

    public string WriteLibraryWithScopedParameter(
        string path,
        string assemblyName,
        Version version,
        string methodName,
        string parameterAssembly)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(assemblyName, version),
            assemblyName,
            ModuleKind.Dll);
        var type = new TypeDefinition("Tests", "Api", TypeAttributes.Public | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);
        var parameterReference = new AssemblyNameReference(parameterAssembly, new Version(1, 0));
        assembly.MainModule.AssemblyReferences.Add(parameterReference);
        var parameterType = new TypeReference("Tests", "ScopedValue", assembly.MainModule, parameterReference);
        var method = new MethodDefinition(methodName, MethodAttributes.Public | MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition(parameterType));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        assembly.Write(path);
        return Path.GetFullPath(path);
    }

    public TestMod CreateModWithInheritedReferences(string id, string dependencyAssembly)
    {
        string directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        string entryName = UniqueName("Entry");
        string entryPath = Path.Combine(directory, $"{entryName}.dll");
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(entryName, new Version(1, 0)),
            entryName,
            ModuleKind.Dll);
        var type = new TypeDefinition("Tests", "Entry", TypeAttributes.Public | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        assembly.MainModule.Types.Add(type);
        var dependencyReference = new AssemblyNameReference(dependencyAssembly, new Version(1, 0));
        assembly.MainModule.AssemblyReferences.Add(dependencyReference);
        var derivedType = new TypeReference("Tests", "Derived", assembly.MainModule, dependencyReference);
        var run = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, assembly.MainModule.TypeSystem.Void);
        type.Methods.Add(run);
        foreach (string methodName in new[] { "BaseApi", "InterfaceApi" })
        {
            var target = new MethodReference(methodName, assembly.MainModule.TypeSystem.Void, derivedType)
            {
                HasThis = true,
            };
            run.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
            run.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, target));
        }
        run.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        assembly.Write(entryPath);
        return new TestMod(directory, new TestManifest(id, Path.GetFileName(entryPath), []));
    }

    public string WriteLibraryWithInheritedMembers(string path, string assemblyName, Version version)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(assemblyName, version),
            assemblyName,
            ModuleKind.Dll);
        var baseType = new TypeDefinition("Tests", "Base", TypeAttributes.Public | TypeAttributes.Class, assembly.MainModule.TypeSystem.Object);
        var baseMethod = new MethodDefinition("BaseApi", MethodAttributes.Public, assembly.MainModule.TypeSystem.Void);
        baseMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        baseType.Methods.Add(baseMethod);
        assembly.MainModule.Types.Add(baseType);

        var interfaceType = new TypeDefinition(
            "Tests",
            "IContract",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract,
            assembly.MainModule.TypeSystem.Object);
        var interfaceMethod = new MethodDefinition(
            "InterfaceApi",
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot,
            assembly.MainModule.TypeSystem.Void);
        interfaceType.Methods.Add(interfaceMethod);
        assembly.MainModule.Types.Add(interfaceType);

        var derivedType = new TypeDefinition(
            "Tests",
            "Derived",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract,
            baseType);
        derivedType.Interfaces.Add(new InterfaceImplementation(interfaceType));
        assembly.MainModule.Types.Add(derivedType);
        assembly.Write(path);
        return Path.GetFullPath(path);
    }

    public ModAssemblyBindingPlan Build(ModAssemblyBindingPolicy policy, params TestMod[] mods) =>
        ModAssemblyBindingPlanner.Build(mods, policy, new TestMonitor());

    public void Dispose() => Directory.Delete(root, recursive: true);
}

internal sealed record TestManifest(
    string UniqueID,
    string EntryDll,
    IReadOnlyList<IManifestDependency> Dependencies) : IManifest;

internal sealed record TestMod(string DirectoryPath, IManifest Manifest) : IModMetadata
{
    public bool IsContentPack => false;
    public ModMetadataStatus Status => ModMetadataStatus.Found;
}

internal sealed class TestMonitor : IMonitor
{
    public void Log(string message, LogLevel level)
    {
    }
}
