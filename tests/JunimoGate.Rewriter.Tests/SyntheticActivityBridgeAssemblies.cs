using System.Runtime.Versioning;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JunimoGate.Rewriter.Tests;

internal sealed record SyntheticActivityBridgeOptions(
    bool IncludeAndroidGameActivity = true,
    bool DuplicateAndroidGameActivity = false,
    bool IncludeGameRunner = true,
    bool IncludeGameRunnerInstance = true,
    bool IncludeGameServicesProperty = true,
    bool IncludeMonoGameRunMethod = true,
    bool IncludeExtraMonoGameApi = false,
    bool IncludeConstructedGenericConsumer = false,
    bool IncludeRunCall = true,
    bool IncludeMainActivity = true);

internal sealed record SyntheticActivityBridgePaths(
    string Root,
    string MonoGame,
    string Game);

internal static class SyntheticActivityBridgeAssemblies
{
    private static readonly Guid MonoGameMvid = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid GameMvid = Guid.Parse("77777777-8888-9999-aaaa-bbbbbbbbbbbb");

    public static SyntheticActivityBridgePaths Create(
        string root,
        SyntheticActivityBridgeOptions? options = null)
    {
        options ??= new SyntheticActivityBridgeOptions();
        Directory.CreateDirectory(root);
        var monoGamePath = Path.Combine(root, "MonoGame.Framework.dll");
        var gamePath = Path.Combine(root, "StardewValley.dll");
        WriteMonoGame(monoGamePath, options);
        WriteGame(gamePath, options);
        return new SyntheticActivityBridgePaths(root, monoGamePath, gamePath);
    }

    private static void WriteMonoGame(string path, SyntheticActivityBridgeOptions options)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("MonoGame.Framework", new Version(1, 0, 0, 0)),
            "MonoGame.Framework",
            ModuleKind.Dll);
        var module = assembly.MainModule;
        module.Mvid = MonoGameMvid;
        AddTargetFramework(assembly, module);

        var monoAndroid = new AssemblyNameReference("Mono.Android", new Version(0, 0, 0, 0));
        module.AssemblyReferences.Add(monoAndroid);
        var androidActivity = new TypeReference("Android.App", "Activity", module, monoAndroid, false);
        var androidBundle = new TypeReference("Android.OS", "Bundle", module, monoAndroid, false);

        if (options.IncludeAndroidGameActivity)
        {
            var activity = new TypeDefinition(
                "Microsoft.Xna.Framework",
                "AndroidGameActivity",
                TypeAttributes.Public | TypeAttributes.Class,
                androidActivity);
            module.Types.Add(activity);
            AddVoidMethod(activity, ".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            AddVoidMethod(activity, "OnCreate", MethodAttributes.Family | MethodAttributes.Virtual, androidBundle);
            AddVoidMethod(activity, "OnResume", MethodAttributes.Family | MethodAttributes.Virtual);
            AddVoidMethod(activity, "OnPause", MethodAttributes.Family | MethodAttributes.Virtual);
            AddVoidMethod(activity, "OnDestroy", MethodAttributes.Family | MethodAttributes.Virtual);
            AddRegisterAttribute(module, activity, "microsoft/xna/framework/AndroidGameActivity");
            if (options.DuplicateAndroidGameActivity)
            {
                module.Types.Add(new TypeDefinition(
                    "Microsoft.Xna.Framework",
                    "AndroidGameActivity",
                    TypeAttributes.Public | TypeAttributes.Class,
                    androidActivity));
            }
        }

        var services = new TypeDefinition(
            "Microsoft.Xna.Framework",
            "GameServiceContainer",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(services);
        AddVoidMethod(services, ".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        var getService = new MethodDefinition("GetService", MethodAttributes.Public | MethodAttributes.Virtual, module.TypeSystem.Object);
        getService.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(Type))));
        getService.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        getService.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        services.Methods.Add(getService);

        var game = new TypeDefinition(
            "Microsoft.Xna.Framework",
            "Game",
            TypeAttributes.Public | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(game);
        var serviceField = new FieldDefinition("services", FieldAttributes.Private, services);
        game.Fields.Add(serviceField);
        AddVoidMethod(game, ".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        if (options.IncludeMonoGameRunMethod)
        {
            AddVoidMethod(game, "Run", MethodAttributes.Public | MethodAttributes.Virtual);
        }

        AddVoidMethod(game, "Exit", MethodAttributes.Public | MethodAttributes.Virtual);
        if (options.IncludeGameServicesProperty)
        {
            var getter = new MethodDefinition(
                "get_Services",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                services);
            getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, serviceField));
            getter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            game.Methods.Add(getter);
            game.Properties.Add(new PropertyDefinition("Services", PropertyAttributes.None, services) { GetMethod = getter });
        }

        if (options.IncludeConstructedGenericConsumer)
        {
            var contentTypeReader = new TypeDefinition(
                "Microsoft.Xna.Framework.Content",
                "ContentTypeReader`1",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Class,
                module.TypeSystem.Object);
            contentTypeReader.GenericParameters.Add(new GenericParameter("T", contentTypeReader));
            module.Types.Add(contentTypeReader);
            AddVoidMethod(
                contentTypeReader,
                ".ctor",
                MethodAttributes.Family | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
        }

        if (options.IncludeExtraMonoGameApi)
        {
            var extra = new TypeDefinition(
                "Microsoft.Xna.Framework",
                "ExtraApi",
                TypeAttributes.Public | TypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(extra);
            AddVoidMethod(extra, ".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            AddVoidMethod(extra, "UnusedByGame", MethodAttributes.Public | MethodAttributes.Static);
        }

        assembly.Write(path, new WriterParameters { WriteSymbols = false });
    }

    private static void WriteGame(string path, SyntheticActivityBridgeOptions options)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("StardewValley", new Version(1, 6, 15, 3)),
            "StardewValley",
            ModuleKind.Dll);
        var module = assembly.MainModule;
        module.Mvid = GameMvid;
        AddTargetFramework(assembly, module);

        var monoGame = new AssemblyNameReference("MonoGame.Framework", new Version(1, 0, 0, 0));
        var monoAndroid = new AssemblyNameReference("Mono.Android", new Version(0, 0, 0, 0));
        module.AssemblyReferences.Add(monoGame);
        module.AssemblyReferences.Add(monoAndroid);
        var androidGameActivity = new TypeReference(
            "Microsoft.Xna.Framework",
            "AndroidGameActivity",
            module,
            monoGame,
            false);
        var gameBase = new TypeReference("Microsoft.Xna.Framework", "Game", module, monoGame, false);
        var androidBundle = new TypeReference("Android.OS", "Bundle", module, monoAndroid, false);

        if (options.IncludeConstructedGenericConsumer)
        {
            var contentTypeReader = new TypeReference(
                "Microsoft.Xna.Framework.Content",
                "ContentTypeReader`1",
                module,
                monoGame,
                false);
            contentTypeReader.GenericParameters.Add(new GenericParameter("T", contentTypeReader));
            var constructedReader = new GenericInstanceType(contentTypeReader);
            constructedReader.GenericArguments.Add(module.TypeSystem.String);
            var reader = new TypeDefinition(
                "StardewValley",
                "SyntheticStringReader",
                TypeAttributes.Public | TypeAttributes.Class,
                constructedReader);
            module.Types.Add(reader);
            var constructor = new MethodDefinition(
                ".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);
            var baseConstructor = new MethodReference(".ctor", module.TypeSystem.Void, constructedReader)
            {
                HasThis = true,
            };
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseConstructor));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            reader.Methods.Add(constructor);
        }

        TypeDefinition? runner = null;
        MethodDefinition? runnerConstructor = null;
        FieldDefinition? runnerInstance = null;
        if (options.IncludeGameRunner)
        {
            runner = new TypeDefinition(
                "StardewValley",
                "GameRunner",
                TypeAttributes.Public | TypeAttributes.Class,
                gameBase);
            module.Types.Add(runner);
            runnerConstructor = AddVoidMethod(
                runner,
                ".ctor",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            if (options.IncludeGameRunnerInstance)
            {
                runnerInstance = new FieldDefinition("instance", FieldAttributes.Public | FieldAttributes.Static, runner);
                runner.Fields.Add(runnerInstance);
            }
        }

        if (options.IncludeMainActivity)
        {
            var main = new TypeDefinition(
                "StardewValley",
                "MainActivity",
                TypeAttributes.Public | TypeAttributes.Class,
                androidGameActivity);
            module.Types.Add(main);
            AddVoidMethod(main, ".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            var checkPermissions = AddVoidMethod(main, "CheckAppPermissions", MethodAttributes.Public);
            var onCreate = new MethodDefinition("OnCreate", MethodAttributes.Family | MethodAttributes.Virtual, module.TypeSystem.Void);
            onCreate.Parameters.Add(new ParameterDefinition(androidBundle));
            var baseOnCreate = new MethodReference("OnCreate", module.TypeSystem.Void, androidGameActivity)
            {
                HasThis = true,
            };
            baseOnCreate.Parameters.Add(new ParameterDefinition(androidBundle));
            onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
            onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseOnCreate));
            onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Call, checkPermissions));
            if (runner is not null && runnerConstructor is not null && runnerInstance is not null)
            {
                onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, runnerConstructor));
                onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Dup));
                onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, runnerInstance));
                if (options.IncludeRunCall)
                {
                    var run = new MethodReference("Run", module.TypeSystem.Void, gameBase) { HasThis = true };
                    onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, run));
                }
                else
                {
                    onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                }
            }

            onCreate.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            main.Methods.Add(onCreate);
            AddVoidMethod(main, "OnResume", MethodAttributes.Family | MethodAttributes.Virtual);
            AddVoidMethod(main, "OnPause", MethodAttributes.Family | MethodAttributes.Virtual);
            AddVoidMethod(main, "OnDestroy", MethodAttributes.Family | MethodAttributes.Virtual);
            AddRegisterAttribute(module, main, "com/chucklefish/stardewvalley/MainActivity");
        }

        assembly.Write(path, new WriterParameters { WriteSymbols = false });
    }

    private static MethodDefinition AddVoidMethod(
        TypeDefinition type,
        string name,
        MethodAttributes attributes,
        params TypeReference[] parameters)
    {
        var method = new MethodDefinition(name, attributes, type.Module.TypeSystem.Void);
        foreach (var parameter in parameters)
        {
            method.Parameters.Add(new ParameterDefinition(parameter));
        }

        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return method;
    }

    private static void AddTargetFramework(AssemblyDefinition assembly, ModuleDefinition module)
    {
        var constructor = typeof(TargetFrameworkAttribute).GetConstructor([typeof(string)])!;
        var attribute = new CustomAttribute(module.ImportReference(constructor));
        attribute.ConstructorArguments.Add(
            new CustomAttributeArgument(module.TypeSystem.String, ".NETCoreApp,Version=v9.0"));
        assembly.CustomAttributes.Add(attribute);
    }

    private static void AddRegisterAttribute(ModuleDefinition module, TypeDefinition type, string javaName)
    {
        var monoAndroid = module.AssemblyReferences.Single(reference => reference.Name == "Mono.Android");
        var attributeType = new TypeReference("Android.Runtime", "RegisterAttribute", module, monoAndroid, false);
        var constructor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType) { HasThis = true };
        constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var attribute = new CustomAttribute(constructor);
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, javaName));
        type.CustomAttributes.Add(attribute);
    }
}
