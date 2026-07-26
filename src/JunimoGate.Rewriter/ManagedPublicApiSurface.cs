using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace JunimoGate.Rewriter;

/// <summary>Bounds public/protected API fingerprinting for one managed assembly.</summary>
public sealed record ManagedPublicApiSurfaceLimits
{
    public const int MaximumTypeLimit = 1_000_000;
    public const int MaximumMemberLimit = 4_000_000;
    public const int MaximumRecordBytesLimit = 64 * 1024;
    public const long MaximumCanonicalBytesLimit = 256L * 1024 * 1024;

    public static ManagedPublicApiSurfaceLimits Default { get; } = new();

    public ManagedPublicApiSurfaceLimits(
        int maxTypes = 250_000,
        int maxMembers = 1_000_000,
        int maxRecordBytes = 16 * 1024,
        long maxCanonicalBytes = 128L * 1024 * 1024)
    {
        if (maxTypes < 1 || maxTypes > MaximumTypeLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTypes));
        }

        if (maxMembers < 1 || maxMembers > MaximumMemberLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMembers));
        }

        if (maxRecordBytes < 256 || maxRecordBytes > MaximumRecordBytesLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));
        }

        if (maxCanonicalBytes < 1024 || maxCanonicalBytes > MaximumCanonicalBytesLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCanonicalBytes));
        }

        MaxTypes = maxTypes;
        MaxMembers = maxMembers;
        MaxRecordBytes = maxRecordBytes;
        MaxCanonicalBytes = maxCanonicalBytes;
    }

    public int MaxTypes { get; }
    public int MaxMembers { get; }
    public int MaxRecordBytes { get; }
    public long MaxCanonicalBytes { get; }
}

/// <summary>A deterministic API-shape identity that contains no member names or paths.</summary>
public sealed record ManagedPublicApiSurfaceEvidence(
    string SchemaVersion,
    string SurfaceKey,
    int TypeCount,
    int MemberCount);

/// <summary>
/// Computes a bounded public/protected API fingerprint without resolving, loading, or writing the assembly.
/// Assembly version, MVID, target framework, and implementation bodies are intentionally excluded.
/// </summary>
public static class ManagedPublicApiSurfaceInspector
{
    public const string SchemaVersion = "junimogate.managed-public-api-surface/v1";

    public static ManagedPublicApiSurfaceEvidence Inspect(
        string assemblyPath,
        ManagedPublicApiSurfaceLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !Path.IsPathFullyQualified(assemblyPath))
        {
            throw new ArgumentException("The managed assembly path must be absolute.", nameof(assemblyPath));
        }

        limits ??= ManagedPublicApiSurfaceLimits.Default;
        var canonicalPath = Path.GetFullPath(assemblyPath);
        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters
        {
            ReadingMode = ReadingMode.Deferred,
            ReadSymbols = false,
            InMemory = false,
        });
        return Inspect(assembly, limits, cancellationToken);
    }

    internal static ManagedPublicApiSurfaceEvidence Inspect(
        AssemblyDefinition assembly,
        ManagedPublicApiSurfaceLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(limits);

        var records = new List<string>();
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
                if (typeCount > limits.MaxTypes)
                {
                    throw new InvalidDataException("The managed public API type count exceeds its bound.");
                }

                AddRecord(records, FormatTypeDefinition(type), limits, ref canonicalBytes);
                foreach (var field in type.Fields.Where(IsExternallyVisible))
                {
                    CountMember(limits, ref memberCount);
                    AddRecord(records, FormatFieldDefinition(field), limits, ref canonicalBytes);
                }

                foreach (var method in type.Methods.Where(IsExternallyVisible))
                {
                    CountMember(limits, ref memberCount);
                    AddRecord(records, FormatMethodDefinition(method), limits, ref canonicalBytes);
                }

                foreach (var property in type.Properties.Where(IsExternallyVisible))
                {
                    CountMember(limits, ref memberCount);
                    AddRecord(records, FormatPropertyDefinition(property), limits, ref canonicalBytes);
                }

                foreach (var @event in type.Events.Where(IsExternallyVisible))
                {
                    CountMember(limits, ref memberCount);
                    AddRecord(records, FormatEventDefinition(@event), limits, ref canonicalBytes);
                }
            }
        }

        records.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "schema", SchemaVersion);
        Append(hash, "typeCount", typeCount.ToString(CultureInfo.InvariantCulture));
        Append(hash, "memberCount", memberCount.ToString(CultureInfo.InvariantCulture));
        Append(hash, "recordCount", records.Count.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Append(hash, $"record[{index}]", records[index]);
        }

        return new ManagedPublicApiSurfaceEvidence(
            SchemaVersion,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            typeCount,
            memberCount);
    }

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

    private static bool IsExternallyVisible(PropertyDefinition property) =>
        (property.GetMethod is not null && IsExternallyVisible(property.GetMethod)) ||
        (property.SetMethod is not null && IsExternallyVisible(property.SetMethod));

    private static bool IsExternallyVisible(EventDefinition @event) =>
        (@event.AddMethod is not null && IsExternallyVisible(@event.AddMethod)) ||
        (@event.RemoveMethod is not null && IsExternallyVisible(@event.RemoveMethod)) ||
        (@event.InvokeMethod is not null && IsExternallyVisible(@event.InvokeMethod));

    private static string FormatTypeDefinition(TypeDefinition type)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("record", "type"),
            ("signature", FormatType(type)),
            ("kind", TypeKind(type)),
            ("visibility", TypeVisibility(type)),
            ("abstract", Bool(type.IsAbstract)),
            ("sealed", Bool(type.IsSealed)),
            ("serializable", Bool(type.IsSerializable)),
            ("beforeFieldInit", Bool(type.IsBeforeFieldInit)),
            ("layout", type.IsExplicitLayout ? "explicit" : type.IsSequentialLayout ? "sequential" : "auto"),
            ("packingSize", type.PackingSize.ToString(CultureInfo.InvariantCulture)),
            ("classSize", type.ClassSize.ToString(CultureInfo.InvariantCulture)),
            ("base", type.BaseType is null ? "<none>" : FormatType(type.BaseType)),
        };
        AddArray(fields, "interface", type.Interfaces.Select(item => FormatType(item.InterfaceType)).Order(StringComparer.Ordinal));
        AddArray(fields, "generic", type.GenericParameters.Select(FormatGenericParameter));
        return EncodeFields(fields);
    }

    private static string FormatFieldDefinition(FieldDefinition field)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("record", "field"),
            ("declaringType", FormatType(field.DeclaringType)),
            ("name", SafeComponent(field.Name)),
            ("fieldType", FormatType(field.FieldType)),
            ("visibility", FieldVisibility(field)),
            ("static", Bool(field.IsStatic)),
            ("initOnly", Bool(field.IsInitOnly)),
            ("literal", Bool(field.IsLiteral)),
            ("notSerialized", Bool(field.IsNotSerialized)),
            ("offset", field.Offset.ToString(CultureInfo.InvariantCulture)),
            ("constant", field.HasConstant ? FingerprintConstant(field.Constant) : "<none>"),
        };
        return EncodeFields(fields);
    }

    private static string FormatMethodDefinition(MethodDefinition method)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("record", "method"),
            ("declaringType", FormatType(method.DeclaringType)),
            ("name", SafeComponent(method.Name)),
            ("returnType", FormatType(method.ReturnType)),
            ("visibility", MethodVisibility(method)),
            ("hasThis", Bool(method.HasThis)),
            ("explicitThis", Bool(method.ExplicitThis)),
            ("callingConvention", method.CallingConvention.ToString()),
            ("static", Bool(method.IsStatic)),
            ("abstract", Bool(method.IsAbstract)),
            ("virtual", Bool(method.IsVirtual)),
            ("final", Bool(method.IsFinal)),
            ("newSlot", Bool(method.IsNewSlot)),
            ("hideBySig", Bool(method.IsHideBySig)),
            ("specialName", Bool(method.IsSpecialName)),
            ("runtimeSpecialName", Bool(method.IsRuntimeSpecialName)),
            ("pinvoke", method.IsPInvokeImpl && method.PInvokeInfo is not null
                ? EncodeFields([
                    ("module", SafeComponent(method.PInvokeInfo.Module.Name)),
                    ("entryPoint", SafeComponent(method.PInvokeInfo.EntryPoint)),
                    ("attributes", ((int)method.PInvokeInfo.Attributes).ToString("x4", CultureInfo.InvariantCulture)),
                ])
                : "<none>"),
        };
        AddArray(fields, "parameter", method.Parameters.Select(FormatParameter));
        AddArray(fields, "generic", method.GenericParameters.Select(FormatGenericParameter));
        return EncodeFields(fields);
    }

    private static string FormatPropertyDefinition(PropertyDefinition property)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("record", "property"),
            ("declaringType", FormatType(property.DeclaringType)),
            ("name", SafeComponent(property.Name)),
            ("propertyType", FormatType(property.PropertyType)),
            ("get", property.GetMethod is null ? "<none>" : MethodVisibility(property.GetMethod)),
            ("set", property.SetMethod is null ? "<none>" : MethodVisibility(property.SetMethod)),
            ("specialName", Bool(property.IsSpecialName)),
            ("runtimeSpecialName", Bool(property.IsRuntimeSpecialName)),
            ("constant", property.HasConstant ? FingerprintConstant(property.Constant) : "<none>"),
        };
        AddArray(fields, "parameter", property.Parameters.Select(FormatParameter));
        return EncodeFields(fields);
    }

    private static string FormatEventDefinition(EventDefinition @event) =>
        EncodeFields([
            ("record", "event"),
            ("declaringType", FormatType(@event.DeclaringType)),
            ("name", SafeComponent(@event.Name)),
            ("eventType", FormatType(@event.EventType)),
            ("add", @event.AddMethod is null ? "<none>" : MethodVisibility(@event.AddMethod)),
            ("remove", @event.RemoveMethod is null ? "<none>" : MethodVisibility(@event.RemoveMethod)),
            ("invoke", @event.InvokeMethod is null ? "<none>" : MethodVisibility(@event.InvokeMethod)),
            ("specialName", Bool(@event.IsSpecialName)),
            ("runtimeSpecialName", Bool(@event.IsRuntimeSpecialName)),
        ]);

    private static string FormatParameter(ParameterDefinition parameter) =>
        EncodeFields([
            ("type", FormatType(parameter.ParameterType)),
            ("in", Bool(parameter.IsIn)),
            ("out", Bool(parameter.IsOut)),
            ("optional", Bool(parameter.IsOptional)),
            ("constant", parameter.HasConstant ? FingerprintConstant(parameter.Constant) : "<none>"),
        ]);

    private static string FormatGenericParameter(GenericParameter parameter)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("position", parameter.Position.ToString(CultureInfo.InvariantCulture)),
            ("kind", parameter.Type.ToString()),
            ("attributes", ((int)parameter.Attributes).ToString("x4", CultureInfo.InvariantCulture)),
        };
        AddArray(fields, "constraint", parameter.Constraints
            .Select(item => FormatType(item.ConstraintType))
            .Order(StringComparer.Ordinal));
        return EncodeFields(fields);
    }

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

    private static string TypeKind(TypeDefinition type) =>
        type.IsInterface ? "interface" :
        type.IsEnum ? "enum" :
        type.IsValueType ? "value-type" :
        type.BaseType?.FullName == "System.MulticastDelegate" ? "delegate" :
        "class";

    private static string TypeVisibility(TypeDefinition type) =>
        type.DeclaringType is null ? "public" :
        type.IsNestedPublic ? "nested-public" :
        type.IsNestedFamily ? "nested-family" :
        "nested-family-or-assembly";

    private static string FieldVisibility(FieldDefinition field) =>
        field.IsPublic ? "public" : field.IsFamily ? "family" : "family-or-assembly";

    private static string MethodVisibility(MethodDefinition method) =>
        method.IsPublic ? "public" : method.IsFamily ? "family" :
        method.IsFamilyOrAssembly ? "family-or-assembly" :
        method.IsAssembly ? "assembly" : method.IsPrivate ? "private" : "other";

    private static string FingerprintConstant(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return $"string:{Encoding.UTF8.GetByteCount(text)}:{HashText(text)}";
        }

        if (value is char character)
        {
            return $"char:{(int)character}";
        }

        if (value is bool boolean)
        {
            return boolean ? "bool:true" : "bool:false";
        }

        return value is IFormattable formattable
            ? $"value:{value.GetType().Name}:{formattable.ToString(null, CultureInfo.InvariantCulture)}"
            : $"value:{SafeComponent(value.GetType().Name)}:{HashText(value.ToString() ?? string.Empty)}";
    }

    private static void CountMember(ManagedPublicApiSurfaceLimits limits, ref int memberCount)
    {
        memberCount = checked(memberCount + 1);
        if (memberCount > limits.MaxMembers)
        {
            throw new InvalidDataException("The managed public API member count exceeds its bound.");
        }
    }

    private static void AddRecord(
        ICollection<string> records,
        string record,
        ManagedPublicApiSurfaceLimits limits,
        ref long canonicalBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(record);
        if (bytes > limits.MaxRecordBytes)
        {
            throw new InvalidDataException("A managed public API record exceeds its bound.");
        }

        canonicalBytes = checked(canonicalBytes + bytes);
        if (canonicalBytes > limits.MaxCanonicalBytes)
        {
            throw new InvalidDataException("The managed public API canonical surface exceeds its bound.");
        }

        records.Add(record);
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

    private static string Bool(bool value) => value ? "true" : "false";

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
