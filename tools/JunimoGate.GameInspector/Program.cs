using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using JunimoGate.Extraction;

return await GameInspector.RunAsync(args);

internal static class GameInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return Usage();
            }

            switch (args[0])
            {
                case "inventory" when args.Length >= 2:
                    await InventoryAsync(args[1..]).ConfigureAwait(false);
                    return 0;
                case "extract-assemblies" when args.Length >= 3:
                    await ExtractAssembliesAsync(args[1], args[2..]).ConfigureAwait(false);
                    return 0;
                case "inspect-assemblies" when args.Length == 2:
                    InspectAssemblies(args[1]);
                    return 0;
                default:
                    return Usage();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  JunimoGate.GameInspector inventory <apk...>");
        Console.Error.WriteLine("  JunimoGate.GameInspector extract-assemblies <output> <apk...>");
        Console.Error.WriteLine("  JunimoGate.GameInspector inspect-assemblies <directory>");
        return 2;
    }

    private static async Task InventoryAsync(string[] apkPaths)
    {
        var inventories = new List<object>();
        foreach (var apkPath in apkPaths)
        {
            var fullPath = ValidateInputFile(apkPath, ".apk");
            var fileInfo = new FileInfo(fullPath);
            var sha256 = await HashFileAsync(fullPath).ConfigureAwait(false);

            using var archive = ZipFile.OpenRead(fullPath);
            var roles = ApkEntryInventory.Classify(archive.Entries.Select(entry => entry.FullName));
            var stores = new List<object>();
            var modernCandidates = AssemblyStoreV2.FindInApk(archive);
            foreach (var candidate in modernCandidates)
            {
                using var store = candidate.Open();
                stores.Add(new
                {
                    path = candidate.Entry.FullName,
                    abi = store.Abi,
                    rawVersion = $"0x{store.RawVersion:X8}",
                    entryCount = store.Items.Count,
                    indexEntryCount = store.IndexEntryCount,
                    payloadSize = store.PayloadSize,
                    entries = store.Items.Select(item => new
                    {
                        name = item.Name,
                        abi = item.Abi,
                        dataSize = item.DataSize,
                    }),
                });
            }

            object? legacyStore = null;
            if (modernCandidates.Count == 0 && LegacyAssemblyStoreSet.HasCandidate(archive))
            {
                using var store = LegacyAssemblyStoreSet.Open(archive, "arm64-v8a");
                legacyStore = new
                {
                    version = 1,
                    abi = "arm64-v8a",
                    entryCount = store.Items.Count,
                    entries = store.Items.Select(item => new
                    {
                        name = item.Name,
                        abi = item.Abi,
                        dataSize = item.DataSize,
                        sourceEntry = item.SourceEntry,
                    }),
                };
            }

            var nativeLibraries = archive.Entries
                .Select(ToRuntimeNativeLibrary)
                .Where(item => item is not null)
                .ToArray();

            inventories.Add(new
            {
                path = fullPath,
                size = fileInfo.Length,
                sha256,
                roles = RoleNames(roles.Roles),
                assemblyStores = stores,
                legacyAssemblyStore = legacyStore,
                runtimeNativeLibraries = nativeLibraries,
            });
        }

        WriteJson(Console.OpenStandardOutput(), new { apks = inventories });
    }

    private static async Task ExtractAssembliesAsync(string outputPath, string[] apkPaths)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var sourceFiles = new List<object>();
        var extracted = new List<object>();
        await using var transaction = new AssemblyExtractionTransaction(fullOutputPath);

        foreach (var apkPath in apkPaths)
        {
            var fullPath = ValidateInputFile(apkPath, ".apk");
            var apkHash = await HashFileAsync(fullPath).ConfigureAwait(false);
            var apkSize = new FileInfo(fullPath).Length;
            sourceFiles.Add(new { path = fullPath, size = apkSize, sha256 = apkHash });

            using var archive = ZipFile.OpenRead(fullPath);
            var modernCandidates = AssemblyStoreV2.FindInApk(archive);
            foreach (var candidate in modernCandidates)
            {
                using var store = candidate.Open();
                foreach (var item in store.Items)
                {
                    var file = await transaction.ExtractAsync(store, item).ConfigureAwait(false);
                    extracted.Add(new
                    {
                        name = file.Name,
                        size = file.Size,
                        sha256 = file.Sha256,
                        abi = item.Abi,
                        storedDataSize = item.DataSize,
                        sourceApk = fullPath,
                        sourceEntry = candidate.Entry.FullName,
                        descriptorIndex = item.DescriptorIndex,
                    });
                }
            }

            if (modernCandidates.Count == 0 && LegacyAssemblyStoreSet.HasCandidate(archive))
            {
                using var store = LegacyAssemblyStoreSet.Open(archive, "arm64-v8a");
                foreach (var item in store.Items)
                {
                    var file = await transaction.ExtractAsync(store, item).ConfigureAwait(false);
                    extracted.Add(new
                    {
                        name = file.Name,
                        size = file.Size,
                        sha256 = file.Sha256,
                        abi = item.Abi,
                        storedDataSize = item.DataSize,
                        sourceApk = fullPath,
                        sourceEntry = item.SourceEntry,
                        descriptorIndex = item.DescriptorIndex,
                    });
                }
            }
        }

        var manifest = new
        {
            formatVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            sourceApks = sourceFiles,
            assemblies = extracted,
        };
        var manifestPath = Path.Combine(fullOutputPath, "assemblies-manifest.json");
        WriteJsonAtomically(manifestPath, manifest);
        WriteJson(Console.OpenStandardOutput(), new
        {
            output = fullOutputPath,
            manifest = manifestPath,
            assemblyCount = extracted.Count,
        });
    }

    private static void InspectAssemblies(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"Assembly directory '{fullDirectory}' does not exist.");
        }

        var assemblies = Directory
            .EnumerateFiles(fullDirectory, "*.dll", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(InspectAssembly)
            .ToArray();
        WriteJson(Console.OpenStandardOutput(), new
        {
            directory = fullDirectory,
            assemblies,
        });
    }

    private static object InspectAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                return new { path, managed = false, error = "PE image has no .NET metadata." };
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return new { path, managed = false, error = "Metadata image is a module, not an assembly." };
            }

            var definition = reader.GetAssemblyDefinition();
            var targetFramework = ReadTargetFramework(reader, definition);
            var references = reader.AssemblyReferences.Select(handle =>
            {
                var reference = reader.GetAssemblyReference(handle);
                return new
                {
                    name = reader.GetString(reference.Name),
                    version = reference.Version.ToString(),
                    culture = reference.Culture.IsNil ? null : reader.GetString(reference.Culture),
                    publicKeyOrToken = reference.PublicKeyOrToken.IsNil
                        ? null
                        : Convert.ToHexStringLower(reader.GetBlobBytes(reference.PublicKeyOrToken)),
                    flags = reference.Flags.ToString(),
                };
            }).ToArray();

            var moduleReferences = Enumerable.Range(1, reader.GetTableRowCount(TableIndex.ModuleRef))
                .Select(row => MetadataTokens.ModuleReferenceHandle(row))
                .Select(handle => reader.GetString(reader.GetModuleReference(handle).Name))
                .Order(StringComparer.Ordinal)
                .ToArray();

            var pinvokes = new List<object>();
            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                {
                    continue;
                }

                var import = method.GetImport();
                var declaringType = reader.GetTypeDefinition(method.GetDeclaringType());
                pinvokes.Add(new
                {
                    declaringType = JoinTypeName(reader.GetString(declaringType.Namespace), reader.GetString(declaringType.Name)),
                    method = reader.GetString(method.Name),
                    module = reader.GetString(reader.GetModuleReference(import.Module).Name),
                    entryPoint = import.Name.IsNil ? null : reader.GetString(import.Name),
                    attributes = import.Attributes.ToString(),
                });
            }

            return new
            {
                path,
                managed = true,
                identity = new
                {
                    name = reader.GetString(definition.Name),
                    version = definition.Version.ToString(),
                    culture = definition.Culture.IsNil ? null : reader.GetString(definition.Culture),
                    publicKey = definition.PublicKey.IsNil
                        ? null
                        : Convert.ToHexStringLower(reader.GetBlobBytes(definition.PublicKey)),
                    flags = definition.Flags.ToString(),
                },
                targetFramework,
                assemblyReferences = references,
                moduleReferences,
                pinvokes,
            };
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new { path, managed = false, error = exception.Message };
        }
    }

    private static string? ReadTargetFramework(MetadataReader reader, AssemblyDefinition definition)
    {
        foreach (var handle in definition.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (!IsTargetFrameworkAttribute(reader, attribute.Constructor))
            {
                continue;
            }

            var value = reader.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                return null;
            }

            return value.ReadSerializedString();
        }

        return null;
    }

    private static bool IsTargetFrameworkAttribute(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle typeHandle;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                typeHandle = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                typeHandle = reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                break;
            default:
                return false;
        }

        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => MatchesReference(reader, reader.GetTypeReference((TypeReferenceHandle)typeHandle)),
            HandleKind.TypeDefinition => MatchesDefinition(reader, reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            _ => false,
        };

        static bool MatchesReference(MetadataReader reader, TypeReference type) =>
            reader.StringComparer.Equals(type.Namespace, "System.Runtime.Versioning") &&
            reader.StringComparer.Equals(type.Name, "TargetFrameworkAttribute");

        static bool MatchesDefinition(MetadataReader reader, TypeDefinition type) =>
            reader.StringComparer.Equals(type.Namespace, "System.Runtime.Versioning") &&
            reader.StringComparer.Equals(type.Name, "TargetFrameworkAttribute");
    }

    private static object? ToRuntimeNativeLibrary(ZipArchiveEntry entry)
    {
        var segments = entry.FullName.Split('/', StringSplitOptions.None);
        if (segments.Length != 3 ||
            !segments[0].Equals("lib", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[1]) ||
            !segments[2].EndsWith(".so", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new
        {
            path = entry.FullName,
            abi = segments[1],
            size = entry.Length,
        };
    }

    private static string[] RoleNames(ApkContentRole roles)
    {
        var names = new List<string>();
        foreach (var role in new[]
                 {
                     ApkContentRole.GameContent,
                     ApkContentRole.LegacyAssemblyBlob,
                     ApkContentRole.ModernAssemblyBlob,
                 })
        {
            if ((roles & role) != 0)
            {
                names.Add(role.ToString());
            }
        }

        return names.ToArray();
    }

    private static string ValidateInputFile(string path, string expectedExtension)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Input file '{fullPath}' does not exist.", fullPath);
        }

        if (!Path.GetExtension(fullPath).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Input file '{fullPath}' is not a {expectedExtension} file.");
        }

        return fullPath;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static void WriteJson(Stream destination, object value)
    {
        JsonSerializer.Serialize(destination, value, JsonOptions);
        destination.WriteByte((byte)'\n');
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        if (File.Exists(path))
        {
            throw new IOException($"Manifest '{path}' already exists; overwrite is not allowed.");
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteJson(stream, value);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static string JoinTypeName(string typeNamespace, string typeName) =>
        string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";
}
