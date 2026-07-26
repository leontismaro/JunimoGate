using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace JunimoGate.Rewriter;

/// <summary>Bounds metadata-only managed consumer/provider API compatibility inspection.</summary>
public sealed record ManagedApiCompatibilityLimits
{
    public const int MaximumConsumerAssemblyLimit = 4_096;
    public const int MaximumTypeRequirementLimit = 2_000_000;
    public const int MaximumMemberRequirementLimit = 8_000_000;
    public const int MaximumProviderTypeLimit = 1_000_000;
    public const int MaximumProviderMemberLimit = 4_000_000;
    public const int MaximumInstructionLimit = 50_000_000;
    public const long MaximumCanonicalBytesLimit = 512L * 1024 * 1024;

    public static ManagedApiCompatibilityLimits Default { get; } = new();

    public ManagedApiCompatibilityLimits(
        int maxConsumerAssemblies = 1_024,
        int maxTypeRequirements = 500_000,
        int maxMemberRequirements = 2_000_000,
        int maxProviderTypes = 250_000,
        int maxProviderMembers = 1_000_000,
        int maxInstructions = 20_000_000,
        long maxCanonicalBytes = 256L * 1024 * 1024)
    {
        MaxConsumerAssemblies = Validate(maxConsumerAssemblies, MaximumConsumerAssemblyLimit, nameof(maxConsumerAssemblies));
        MaxTypeRequirements = Validate(maxTypeRequirements, MaximumTypeRequirementLimit, nameof(maxTypeRequirements));
        MaxMemberRequirements = Validate(maxMemberRequirements, MaximumMemberRequirementLimit, nameof(maxMemberRequirements));
        MaxProviderTypes = Validate(maxProviderTypes, MaximumProviderTypeLimit, nameof(maxProviderTypes));
        MaxProviderMembers = Validate(maxProviderMembers, MaximumProviderMemberLimit, nameof(maxProviderMembers));
        MaxInstructions = Validate(maxInstructions, MaximumInstructionLimit, nameof(maxInstructions));
        if (maxCanonicalBytes < 1024 || maxCanonicalBytes > MaximumCanonicalBytesLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCanonicalBytes));
        }

        MaxCanonicalBytes = maxCanonicalBytes;
    }

    public int MaxConsumerAssemblies { get; }
    public int MaxTypeRequirements { get; }
    public int MaxMemberRequirements { get; }
    public int MaxProviderTypes { get; }
    public int MaxProviderMembers { get; }
    public int MaxInstructions { get; }
    public long MaxCanonicalBytes { get; }

    private static int Validate(int value, int maximum, string parameterName)
    {
        if (value < 1 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

/// <summary>Hashed API identities actually referenced by validated consumer assemblies.</summary>
public sealed record ManagedApiRequirementEvidence(
    string SchemaVersion,
    string TargetAssemblyName,
    string RequirementsKey,
    int ConsumerAssemblyCount,
    ImmutableArray<string> TypeRequirementHashes,
    ImmutableArray<string> MemberRequirementHashes);

/// <summary>Hashed externally callable API identities exposed by one provider assembly.</summary>
public sealed record ManagedApiProviderEvidence(
    string SchemaVersion,
    string TargetAssemblyName,
    string ProviderKey,
    int TypeCapabilityCount,
    int MemberCapabilityCount,
    ImmutableArray<string> TypeCapabilityHashes,
    ImmutableArray<string> MemberCapabilityHashes);

/// <summary>Set-containment result with hashes only; no commercial API names or paths are exposed.</summary>
public sealed record ManagedApiCompatibilityResult(
    bool IsCompatible,
    int RequiredTypeCount,
    int RequiredMemberCount,
    int MissingTypeCount,
    int MissingMemberCount,
    ImmutableArray<string> MissingTypeHashes,
    ImmutableArray<string> MissingMemberHashes);

/// <summary>
/// Compares the exact managed API consumed by assemblies with a candidate provider without resolving,
/// loading, executing, or writing any assembly. Public results contain hashes only.
/// </summary>
public static class ManagedApiCompatibilityInspector
{
    public const string SchemaVersion = "junimogate.managed-api-compatibility/v2";

    public static ManagedApiRequirementEvidence InspectRequirements(
        IEnumerable<string> consumerAssemblyPaths,
        string targetAssemblyName,
        ManagedApiCompatibilityLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumerAssemblyPaths);
        ValidateTargetName(targetAssemblyName);
        limits ??= ManagedApiCompatibilityLimits.Default;
        var paths = NormalizePaths(consumerAssemblyPaths, limits.MaxConsumerAssemblies, nameof(consumerAssemblyPaths));
        var typeRecords = new HashSet<string>(StringComparer.Ordinal);
        var memberRecords = new HashSet<string>(StringComparer.Ordinal);
        long canonicalBytes = 0;
        var instructionCount = 0;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = OpenRead(path);
            using var assembly = ReadAssembly(stream);
            foreach (var module in assembly.Modules)
            {
                foreach (var typeReference in module.GetTypeReferences())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TargetsAssembly(typeReference, targetAssemblyName))
                    {
                        AddRecord(typeRecords, FormatTypeCapability(typeReference), limits.MaxTypeRequirements, limits, ref canonicalBytes);
                    }
                }

                foreach (var memberReference in module.GetMemberReferences())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TargetsAssembly(memberReference.DeclaringType, targetAssemblyName))
                    {
                        continue;
                    }

                    var record = memberReference switch
                    {
                        MethodReference method => FormatMethodCapability(method),
                        FieldReference field => FormatFieldCapability(field),
                        _ => null,
                    };
                    if (record is not null)
                    {
                        AddRecord(memberRecords, record, limits.MaxMemberRequirements, limits, ref canonicalBytes);
                    }
                }

                foreach (var type in EnumerateTypes(module.Types, cancellationToken))
                {
                    foreach (var method in type.Methods)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (var @override in method.Overrides)
                        {
                            if (TargetsAssembly(@override.DeclaringType, targetAssemblyName))
                            {
                                AddRecord(memberRecords, FormatMethodCapability(@override), limits.MaxMemberRequirements, limits, ref canonicalBytes);
                            }
                        }

                        if (!method.HasBody)
                        {
                            continue;
                        }

                        instructionCount = checked(instructionCount + method.Body.Instructions.Count);
                        if (instructionCount > limits.MaxInstructions)
                        {
                            throw new InvalidDataException("Managed consumer instructions exceed the compatibility inspection bound.");
                        }

                        foreach (var instruction in method.Body.Instructions)
                        {
                            if (instruction.Operand is FieldReference field && TargetsAssembly(field.DeclaringType, targetAssemblyName))
                            {
                                var isStatic = instruction.OpCode == OpCodes.Ldsfld ||
                                    instruction.OpCode == OpCodes.Ldsflda ||
                                    instruction.OpCode == OpCodes.Stsfld;
                                AddRecord(
                                    memberRecords,
                                    FormatFieldUseCapability(field, isStatic),
                                    limits.MaxMemberRequirements,
                                    limits,
                                    ref canonicalBytes);
                            }
                        }
                    }
                }
            }
        }

        var typeHashes = HashRecords(typeRecords, cancellationToken);
        var memberHashes = HashRecords(memberRecords, cancellationToken);
        return new ManagedApiRequirementEvidence(
            SchemaVersion,
            targetAssemblyName,
            ComputeSetKey("requirements", targetAssemblyName, paths.Length, typeHashes, memberHashes),
            paths.Length,
            typeHashes,
            memberHashes);
    }

    public static ManagedApiProviderEvidence InspectProvider(
        string providerAssemblyPath,
        string targetAssemblyName,
        ManagedApiCompatibilityLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAbsolutePath(providerAssemblyPath, nameof(providerAssemblyPath));
        ValidateTargetName(targetAssemblyName);
        limits ??= ManagedApiCompatibilityLimits.Default;
        using var stream = OpenRead(Path.GetFullPath(providerAssemblyPath));
        using var assembly = ReadAssembly(stream);
        if (!assembly.Name.Name.Equals(targetAssemblyName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The provider assembly simple name does not match the requested compatibility target.");
        }

        var typeRecords = new HashSet<string>(StringComparer.Ordinal);
        var memberRecords = new HashSet<string>(StringComparer.Ordinal);
        var typeCount = 0;
        var memberCount = 0;
        long canonicalBytes = 0;
        foreach (var module in assembly.Modules)
        {
            foreach (var type in EnumerateTypes(module.Types, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExternallyVisible(type))
                {
                    continue;
                }

                typeCount = checked(typeCount + 1);
                if (typeCount > limits.MaxProviderTypes)
                {
                    throw new InvalidDataException("Managed provider type count exceeds the compatibility inspection bound.");
                }

                AddRecord(typeRecords, FormatTypeCapability(type), limits.MaxProviderTypes, limits, ref canonicalBytes);
                foreach (var field in type.Fields.Where(IsExternallyVisible))
                {
                    CountProviderMember(limits, ref memberCount);
                    AddRecord(memberRecords, FormatFieldCapability(field), limits.MaxProviderMembers, limits, ref canonicalBytes);
                    AddRecord(memberRecords, FormatFieldUseCapability(field, field.IsStatic), limits.MaxProviderMembers, limits, ref canonicalBytes);
                }

                foreach (var method in type.Methods.Where(IsExternallyVisible))
                {
                    CountProviderMember(limits, ref memberCount);
                    AddRecord(memberRecords, FormatMethodCapability(method), limits.MaxProviderMembers, limits, ref canonicalBytes);
                }
            }
        }

        var typeHashes = HashRecords(typeRecords, cancellationToken);
        var memberHashes = HashRecords(memberRecords, cancellationToken);
        return new ManagedApiProviderEvidence(
            SchemaVersion,
            targetAssemblyName,
            ComputeSetKey("provider", targetAssemblyName, 1, typeHashes, memberHashes),
            typeHashes.Length,
            memberHashes.Length,
            typeHashes,
            memberHashes);
    }

    public static ManagedApiCompatibilityResult Evaluate(
        ManagedApiRequirementEvidence requirements,
        ManagedApiProviderEvidence provider)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(provider);
        ValidateEvidence(requirements, provider);
        var providerTypes = provider.TypeCapabilityHashes.ToHashSet(StringComparer.Ordinal);
        var providerMembers = provider.MemberCapabilityHashes.ToHashSet(StringComparer.Ordinal);
        var missingTypes = requirements.TypeRequirementHashes
            .Where(hash => !providerTypes.Contains(hash))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var missingMembers = requirements.MemberRequirementHashes
            .Where(hash => !providerMembers.Contains(hash))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return new ManagedApiCompatibilityResult(
            missingTypes.IsEmpty && missingMembers.IsEmpty,
            requirements.TypeRequirementHashes.Length,
            requirements.MemberRequirementHashes.Length,
            missingTypes.Length,
            missingMembers.Length,
            missingTypes,
            missingMembers);
    }

    private static void ValidateEvidence(
        ManagedApiRequirementEvidence requirements,
        ManagedApiProviderEvidence provider)
    {
        if (!requirements.SchemaVersion.Equals(SchemaVersion, StringComparison.Ordinal) ||
            !provider.SchemaVersion.Equals(SchemaVersion, StringComparison.Ordinal) ||
            !requirements.TargetAssemblyName.Equals(provider.TargetAssemblyName, StringComparison.Ordinal) ||
            !IsCanonicalHash(requirements.RequirementsKey) ||
            !IsCanonicalHash(provider.ProviderKey) ||
            requirements.ConsumerAssemblyCount <= 0 ||
            !IsCanonicalHashArray(requirements.TypeRequirementHashes) ||
            !IsCanonicalHashArray(requirements.MemberRequirementHashes) ||
            !IsCanonicalHashArray(provider.TypeCapabilityHashes) ||
            !IsCanonicalHashArray(provider.MemberCapabilityHashes))
        {
            throw new ArgumentException("Managed API compatibility evidence is malformed or mismatched.");
        }

        var requirementKey = ComputeSetKey(
            "requirements",
            requirements.TargetAssemblyName,
            requirements.ConsumerAssemblyCount,
            requirements.TypeRequirementHashes,
            requirements.MemberRequirementHashes);
        var providerKey = ComputeSetKey(
            "provider",
            provider.TargetAssemblyName,
            1,
            provider.TypeCapabilityHashes,
            provider.MemberCapabilityHashes);
        if (!requirements.RequirementsKey.Equals(requirementKey, StringComparison.Ordinal) ||
            !provider.ProviderKey.Equals(providerKey, StringComparison.Ordinal) ||
            provider.TypeCapabilityCount != provider.TypeCapabilityHashes.Length ||
            provider.MemberCapabilityCount != provider.MemberCapabilityHashes.Length)
        {
            throw new ArgumentException("Managed API compatibility evidence keys or counts do not match their contents.");
        }
    }

    private static string[] NormalizePaths(IEnumerable<string> paths, int maximum, string parameterName)
    {
        var normalized = paths.Select((path, index) =>
        {
            ValidateAbsolutePath(path, $"{parameterName}[{index}]");
            return Path.GetFullPath(path);
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0 || normalized.Length > maximum)
        {
            throw new ArgumentException("The managed consumer assembly count is empty or exceeds its bound.", parameterName);
        }

        return normalized;
    }

    private static void ValidateAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A managed assembly path must be absolute.", parameterName);
        }
    }

    private static void ValidateTargetName(string targetAssemblyName)
    {
        if (string.IsNullOrWhiteSpace(targetAssemblyName) ||
            targetAssemblyName.Length > 256 ||
            targetAssemblyName.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '|' or '\0'))
        {
            throw new ArgumentException("The managed API target assembly name is invalid.", nameof(targetAssemblyName));
        }
    }

    private static bool TargetsAssembly(TypeReference type, string targetAssemblyName)
    {
        while (type is TypeSpecification specification)
        {
            type = specification.ElementType;
        }

        if (type is GenericParameter)
        {
            return false;
        }

        return type.Scope is AssemblyNameReference assembly &&
            assembly.Name.Equals(targetAssemblyName, StringComparison.Ordinal);
    }

    private static string FormatTypeCapability(TypeReference type) =>
        EncodeFields([
            ("record", "type"),
            ("type", FormatType(type)),
        ]);

    private static string FormatMethodCapability(MethodReference method)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("record", "method"),
            ("declaringType", FormatMemberDeclaringType(method.DeclaringType)),
            ("name", SafeComponent(method.Name)),
            ("returnType", FormatType(method.ReturnType)),
            ("hasThis", Bool(method.HasThis)),
            ("explicitThis", Bool(method.ExplicitThis)),
            ("callingConvention", method.CallingConvention.ToString()),
            ("genericArity", method.GenericParameters.Count.ToString(CultureInfo.InvariantCulture)),
        };
        AddArray(fields, "parameter", method.Parameters.Select(parameter => FormatType(parameter.ParameterType)));
        return EncodeFields(fields);
    }

    private static string FormatFieldCapability(FieldReference field) =>
        EncodeFields([
            ("record", "field"),
            ("declaringType", FormatMemberDeclaringType(field.DeclaringType)),
            ("name", SafeComponent(field.Name)),
            ("fieldType", FormatType(field.FieldType)),
        ]);

    private static string FormatFieldUseCapability(FieldReference field, bool isStatic) =>
        EncodeFields([
            ("record", "field-use"),
            ("declaringType", FormatMemberDeclaringType(field.DeclaringType)),
            ("name", SafeComponent(field.Name)),
            ("fieldType", FormatType(field.FieldType)),
            ("static", Bool(isStatic)),
        ]);

    private static string FormatMemberDeclaringType(TypeReference type) =>
        type is GenericInstanceType generic
            ? FormatType(generic.ElementType)
            : FormatType(type);

    private static string FormatType(TypeReference type) => type switch
    {
        ByReferenceType byReference => $"{FormatType(byReference.ElementType)}&",
        PointerType pointer => $"{FormatType(pointer.ElementType)}*",
        ArrayType array => $"{FormatType(array.ElementType)}[{new string(',', Math.Max(0, array.Rank - 1))}]",
        GenericInstanceType generic => $"{FormatType(generic.ElementType)}<{string.Join(",", generic.GenericArguments.Select(FormatType))}>",
        OptionalModifierType optional => $"{FormatType(optional.ElementType)} modopt({FormatType(optional.ModifierType)})",
        RequiredModifierType required => $"{FormatType(required.ElementType)} modreq({FormatType(required.ModifierType)})",
        PinnedType pinned => $"{FormatType(pinned.ElementType)} pinned",
        SentinelType sentinel => $"{FormatType(sentinel.ElementType)} sentinel",
        FunctionPointerType functionPointer => $"fnptr:{functionPointer.CallingConvention}:{FormatType(functionPointer.ReturnType)}({string.Join(",", functionPointer.Parameters.Select(parameter => FormatType(parameter.ParameterType)))})",
        GenericParameter parameter => parameter.Type == GenericParameterType.Method ? $"!!{parameter.Position}" : $"!{parameter.Position}",
        _ when type.DeclaringType is not null => $"{FormatType(type.DeclaringType)}+{SafeComponent(type.Name)}",
        _ when string.IsNullOrEmpty(type.Namespace) => SafeComponent(type.Name),
        _ => $"{SafeComponent(type.Namespace)}.{SafeComponent(type.Name)}",
    };

    private static IEnumerable<TypeDefinition> EnumerateTypes(
        IEnumerable<TypeDefinition> roots,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<TypeDefinition>(roots.Reverse());
        while (stack.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = stack.Pop();
            yield return type;
            for (var index = type.NestedTypes.Count - 1; index >= 0; index--)
            {
                stack.Push(type.NestedTypes[index]);
            }
        }
    }

    private static bool IsExternallyVisible(TypeDefinition type)
    {
        if (type.DeclaringType is null)
        {
            return type.IsPublic;
        }

        return IsExternallyVisible(type.DeclaringType) &&
            (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly);
    }

    private static bool IsExternallyVisible(FieldDefinition field) =>
        field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static bool IsExternallyVisible(MethodDefinition method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static void CountProviderMember(ManagedApiCompatibilityLimits limits, ref int count)
    {
        count = checked(count + 1);
        if (count > limits.MaxProviderMembers)
        {
            throw new InvalidDataException("Managed provider member count exceeds the compatibility inspection bound.");
        }
    }

    private static void AddRecord(
        HashSet<string> records,
        string record,
        int maximumCount,
        ManagedApiCompatibilityLimits limits,
        ref long canonicalBytes)
    {
        if (Encoding.UTF8.GetByteCount(record) > 64 * 1024)
        {
            throw new InvalidDataException("A managed API compatibility record exceeds its bound.");
        }

        if (records.Add(record))
        {
            if (records.Count > maximumCount)
            {
                throw new InvalidDataException("Managed API compatibility records exceed their count bound.");
            }

            canonicalBytes = checked(canonicalBytes + Encoding.UTF8.GetByteCount(record));
            if (canonicalBytes > limits.MaxCanonicalBytes)
            {
                throw new InvalidDataException("Managed API compatibility canonical data exceeds its byte bound.");
            }
        }
    }

    private static ImmutableArray<string> HashRecords(
        IEnumerable<string> records,
        CancellationToken cancellationToken) =>
        records.Select(record =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return HashText(record);
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

    private static string ComputeSetKey(
        string kind,
        string targetAssemblyName,
        int assemblyCount,
        IEnumerable<string> typeHashes,
        IEnumerable<string> memberHashes)
    {
        var types = typeHashes.ToImmutableArray();
        var members = memberHashes.ToImmutableArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "schema", SchemaVersion);
        Append(hash, "kind", kind);
        Append(hash, "target", targetAssemblyName);
        Append(hash, "assemblyCount", assemblyCount.ToString(CultureInfo.InvariantCulture));
        AppendArray(hash, "type", types);
        AppendArray(hash, "member", members);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddArray(
        ICollection<(string Name, string Value)> fields,
        string name,
        IEnumerable<string> values)
    {
        var materialized = values.ToImmutableArray();
        fields.Add(($"{name}.count", materialized.Length.ToString(CultureInfo.InvariantCulture)));
        for (var index = 0; index < materialized.Length; index++)
        {
            fields.Add(($"{name}[{index}]", materialized[index]));
        }
    }

    private static void AppendArray(IncrementalHash hash, string name, ImmutableArray<string> values)
    {
        Append(hash, $"{name}.count", values.Length.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < values.Length; index++)
        {
            Append(hash, $"{name}[{index}]", values[index]);
        }
    }

    private static string EncodeFields(IEnumerable<(string Name, string Value)> fields)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            text.Append(Encoding.UTF8.GetByteCount(name)).Append(':').Append(name)
                .Append('=').Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value).Append('\n');
        }

        return text.ToString();
    }

    private static void Append(IncrementalHash hash, string name, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(EncodeFields([(name, value)])));

    private static string SafeComponent(string? value)
    {
        value ??= string.Empty;
        if (value.Contains('/') || value.Contains('\\') || value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 1024)
        {
            return $"redacted-sha256:{HashText(value)}:length={Encoding.UTF8.GetByteCount(value)}";
        }

        return value;
    }

    private static bool IsCanonicalHash(string value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalHashArray(ImmutableArray<string> values) =>
        !values.IsDefault &&
        values.All(IsCanonicalHash) &&
        values.SequenceEqual(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static string Bool(bool value) => value ? "true" : "false";

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private static AssemblyDefinition ReadAssembly(Stream stream) =>
        AssemblyDefinition.ReadAssembly(stream, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = false,
            InMemory = false,
        });
}
