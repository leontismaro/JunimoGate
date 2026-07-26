using System.Runtime.Versioning;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JunimoGate.Rewriter.Tests;

internal sealed record SyntheticGameOptions(
    bool IncludeMainActivity = true,
    bool DuplicateMainActivity = false,
    bool IncludeInstanceField = true,
    bool DuplicateInstanceField = false,
    bool StaticInstanceField = true,
    bool IncludeUnresolvedInteropEnumAttribute = false);

internal sealed record SyntheticGamePaths(string Root, string Target, string Dependency);

internal static class SyntheticGameAssemblies
{
    private static readonly Guid TargetMvid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid DependencyMvid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public static SyntheticGamePaths Create(string root, SyntheticGameOptions? options = null)
    {
        options ??= new SyntheticGameOptions();
        Directory.CreateDirectory(root);
        var targetPath = Path.Combine(root, "StardewValley.dll");
        var dependencyPath = Path.Combine(root, "GameDependency.dll");
        var unresolvedContractsPath = Path.Combine(root, "Missing.Android.Contracts.dll");

        if (options.IncludeUnresolvedInteropEnumAttribute)
        {
            WriteUnresolvedAndroidContracts(unresolvedContractsPath);
        }

        WriteTarget(targetPath, options, unresolvedContractsPath);
        if (options.IncludeUnresolvedInteropEnumAttribute)
        {
            File.Delete(unresolvedContractsPath);
        }

        WriteDependency(dependencyPath);
        return new SyntheticGamePaths(root, targetPath, dependencyPath);
    }

    private static void WriteTarget(
        string path,
        SyntheticGameOptions options,
        string unresolvedContractsPath)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("StardewValley", new Version(1, 6, 15, 3)),
            "StardewValley",
            ModuleKind.Dll);
        using (assembly)
        {
            var module = assembly.MainModule;
            module.Mvid = TargetMvid;
            AddTargetFramework(assembly, module);

            if (options.IncludeMainActivity)
            {
                AddMainActivity(module, options);
                if (options.DuplicateMainActivity)
                {
                    AddMainActivity(module, options);
                }
            }

            if (options.IncludeUnresolvedInteropEnumAttribute)
            {
                AddUnresolvedInteropEnumAttribute(assembly, unresolvedContractsPath);
            }

            assembly.Write(path, new WriterParameters { WriteSymbols = false });
        }
    }

    private static void AddTargetFramework(AssemblyDefinition assembly, ModuleDefinition module)
    {
        var constructor = typeof(TargetFrameworkAttribute).GetConstructor([typeof(string)])!;
        var attribute = new CustomAttribute(module.ImportReference(constructor));
        attribute.ConstructorArguments.Add(
            new CustomAttributeArgument(module.TypeSystem.String, ".NETCoreApp,Version=v9.0"));
        assembly.CustomAttributes.Add(attribute);
    }

    private static void AddMainActivity(ModuleDefinition module, SyntheticGameOptions options)
    {
        var androidAssembly = new AssemblyNameReference("Mono.Android", new Version(0, 0, 0, 0));
        if (!module.AssemblyReferences.Any(reference => reference.Name == androidAssembly.Name))
        {
            module.AssemblyReferences.Add(androidAssembly);
        }

        var activityBase = new TypeReference("Android.App", "Activity", module, androidAssembly, false);
        var activity = new TypeDefinition(
            "StardewValley",
            "MainActivity",
            TypeAttributes.Public | TypeAttributes.Class,
            activityBase);
        module.Types.Add(activity);

        FieldDefinition? instance = null;
        if (options.IncludeInstanceField)
        {
            var attributes = FieldAttributes.Public;
            if (options.StaticInstanceField)
            {
                attributes |= FieldAttributes.Static;
            }

            instance = new FieldDefinition("instance", attributes, activity);
            activity.Fields.Add(instance);
            if (options.DuplicateInstanceField)
            {
                activity.Fields.Add(new FieldDefinition("instance", attributes, activity));
            }
        }

        var constructor = AddVoidMethod(activity, ".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        AddVoidMethod(activity, "OnCreate", MethodAttributes.Family | MethodAttributes.Virtual);
        AddVoidMethod(activity, "OnResume", MethodAttributes.Family | MethodAttributes.Virtual);
        AddVoidMethod(activity, "OnPause", MethodAttributes.Family | MethodAttributes.Virtual);
        AddVoidMethod(activity, "OnDestroy", MethodAttributes.Family | MethodAttributes.Virtual);
        var bootstrap = AddVoidMethod(activity, "Bootstrap", MethodAttributes.Public | MethodAttributes.Static);
        var run = AddVoidMethod(activity, "Run", MethodAttributes.Public);

        AddRegisterAttribute(module, activity);

        if (instance is not null)
        {
            AddReadAndCallMethod(activity, instance, run);
            AddWriteMethod(activity, instance);
            AddAddressMethod(activity, instance);
            AddDirectBootstrapCall(activity, bootstrap);
        }

        _ = constructor;
        AddPInvokeMethod(module, activity, "NativeTick", "libgame.so", "native_tick", PInvokeAttributes.CallConvCdecl | PInvokeAttributes.CharSetAnsi);
    }

    private static MethodDefinition AddVoidMethod(TypeDefinition type, string name, MethodAttributes attributes)
    {
        var method = new MethodDefinition(name, attributes, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return method;
    }

    private static void AddReadAndCallMethod(TypeDefinition type, FieldReference instance, MethodReference run)
    {
        var method = new MethodDefinition("UseInstance", MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, instance));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, run));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
    }

    private static void AddWriteMethod(TypeDefinition type, FieldReference instance)
    {
        var method = new MethodDefinition("WriteInstance", MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, instance));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
    }

    private static void AddAddressMethod(TypeDefinition type, FieldReference instance)
    {
        var method = new MethodDefinition("AddressInstance", MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsflda, instance));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
    }

    private static void AddDirectBootstrapCall(TypeDefinition type, MethodReference bootstrap)
    {
        var method = new MethodDefinition("InvokeBootstrap", MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, bootstrap));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
    }

    private static void AddRegisterAttribute(ModuleDefinition module, TypeDefinition activity)
    {
        var androidAssembly = module.AssemblyReferences.Single(reference => reference.Name == "Mono.Android");
        var attributeType = new TypeReference("Android.Runtime", "RegisterAttribute", module, androidAssembly, false);
        var constructor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType) { HasThis = true };
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var attribute = new CustomAttribute(constructor);
        attribute.ConstructorArguments.Add(
            new CustomAttributeArgument(module.TypeSystem.String, "com/chucklefish/stardew/MainActivity"));
        activity.CustomAttributes.Add(attribute);
    }

    private static void AddUnresolvedInteropEnumAttribute(
        AssemblyDefinition target,
        string contractsPath)
    {
        if (target.MainModule.AssemblyResolver is BaseAssemblyResolver resolver)
        {
            resolver.AddSearchDirectory(Path.GetDirectoryName(contractsPath)!);
        }

        using var contracts = AssemblyDefinition.ReadAssembly(contractsPath);
        var attributeType = contracts.MainModule.GetType("Android.Runtime.UnresolvedModeAttribute");
        var enumType = contracts.MainModule.GetType("Android.Runtime.UnresolvedMode");
        var constructor = attributeType.Methods.Single(method => method.IsConstructor);
        var attribute = new CustomAttribute(target.MainModule.ImportReference(constructor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(
            target.MainModule.ImportReference(enumType),
            1));
        target.CustomAttributes.Add(attribute);
    }

    private static void WriteUnresolvedAndroidContracts(string path)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Missing.Android.Contracts", new Version(1, 0, 0, 0)),
            "Missing.Android.Contracts",
            ModuleKind.Dll);
        var module = assembly.MainModule;
        var enumType = new TypeDefinition(
            "Android.Runtime",
            "UnresolvedMode",
            TypeAttributes.Public | TypeAttributes.Sealed,
            module.ImportReference(typeof(Enum)));
        enumType.Fields.Add(new FieldDefinition(
            "value__",
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
            module.TypeSystem.Int32));
        enumType.Fields.Add(new FieldDefinition(
            "Enabled",
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
            enumType)
        {
            Constant = 1,
        });
        module.Types.Add(enumType);

        var attributeType = new TypeDefinition(
            "Android.Runtime",
            "UnresolvedModeAttribute",
            TypeAttributes.Public | TypeAttributes.Sealed,
            module.ImportReference(typeof(Attribute)));
        var constructor = new MethodDefinition(
            ".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            module.TypeSystem.Void);
        constructor.Parameters.Add(new ParameterDefinition("mode", ParameterAttributes.None, enumType));
        constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        attributeType.Methods.Add(constructor);
        module.Types.Add(attributeType);
        assembly.Write(path, new WriterParameters { WriteSymbols = false });
    }

    private static void AddPInvokeMethod(
        ModuleDefinition module,
        TypeDefinition owner,
        string methodName,
        string moduleName,
        string entryPoint,
        PInvokeAttributes attributes)
    {
        var method = new MethodDefinition(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PInvokeImpl,
            module.TypeSystem.Int32)
        {
            PInvokeInfo = new PInvokeInfo(attributes, entryPoint, GetOrAddModuleReference(module, moduleName)),
        };
        owner.Methods.Add(method);
    }

    private static ModuleReference GetOrAddModuleReference(ModuleDefinition module, string name)
    {
        var existing = module.ModuleReferences.FirstOrDefault(reference => reference.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ModuleReference(name);
        module.ModuleReferences.Add(created);
        return created;
    }

    private static void WriteDependency(string path)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("GameDependency", new Version(2, 0, 0, 0)),
            "GameDependency",
            ModuleKind.Dll);
        using (assembly)
        {
            var module = assembly.MainModule;
            module.Mvid = DependencyMvid;
            var type = new TypeDefinition("Game", "NativeBridge", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(type);
            AddPInvokeMethod(
                module,
                type,
                "InitializeSdl",
                "libSDL2-2.0.so.0",
                "SDL_Init",
                PInvokeAttributes.CallConvCdecl | PInvokeAttributes.CharSetUnicode | PInvokeAttributes.SupportsLastError);

            var gameAssembly = new AssemblyNameReference("StardewValley", new Version(1, 6, 15, 3));
            module.AssemblyReferences.Add(gameAssembly);
            var activity = new TypeReference("StardewValley", "MainActivity", module, gameAssembly, false);
            var instance = new FieldReference("instance", activity, activity);
            var run = new MethodReference("Run", module.TypeSystem.Void, activity) { HasThis = true };
            var method = new MethodDefinition("RunGame", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, instance));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, run));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);

            assembly.Write(path, new WriterParameters { WriteSymbols = false });
        }
    }
}
