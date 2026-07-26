using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JunimoGate.Rewriter;

/// <summary>Platform-neutral selected-ABI native identity used only to derive Gate 0 support evidence.</summary>
public sealed record GameHostNativeEvidence(
    string SourceLabel,
    string EntryPath,
    long Size,
    string Sha256,
    int ElfClass,
    int DataEncoding,
    int IdentVersion,
    int OsAbi,
    int AbiVersion,
    int ObjectType,
    int Machine,
    uint Flags);

/// <summary>Builds the final Gate 0 support key from managed metadata and selected-ABI native evidence.</summary>
public static class GameHostSupportKey
{
    public const string SchemaVersion = "junimogate.gamehost-support-key/v1";

    public static string Create(
        GameHostCompatibilityEvidence managedEvidence,
        string selectedAbi,
        IEnumerable<GameHostNativeEvidence> nativeEntries)
    {
        ArgumentNullException.ThrowIfNull(managedEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedAbi);
        ArgumentNullException.ThrowIfNull(nativeEntries);

        if (!selectedAbi.Equals("arm64-v8a", StringComparison.Ordinal))
        {
            throw new ArgumentException("The selected ABI is not supported by the ARM64 Gate 0 evidence schema.", nameof(selectedAbi));
        }

        ValidateManaged(managedEvidence);

        var native = nativeEntries.ToImmutableArray();
        if (native.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one selected-ABI native entry is required.", nameof(nativeEntries));
        }

        ValidateNative(native, selectedAbi);

        var canonical = new CanonicalHashBuilder();
        canonical.Add("schema", SchemaVersion);
        canonical.Add("managed.schema", managedEvidence.SchemaVersion);
        canonical.Add("target.identity", managedEvidence.TargetAssembly.Identity);
        canonical.Add("target.mvid", managedEvidence.TargetAssembly.ModuleVersionId);
        canonical.Add("target.framework", managedEvidence.TargetAssembly.TargetFramework ?? "<none>");
        canonical.AddArray(
            "target.reference",
            managedEvidence.TargetAssembly.References
                .Select(static reference => reference.Identity)
                .Order(StringComparer.Ordinal));
        canonical.Add("abi", selectedAbi);
        canonical.Add("activity.base", managedEvidence.MainActivity.BaseType);
        canonical.Add("activity.instance", managedEvidence.MainActivity.InstanceFieldSignature);
        canonical.AddArray("activity.method", managedEvidence.MainActivity.MethodSignatures.Order(StringComparer.Ordinal));
        canonical.AddArray("activity.lifecycle", managedEvidence.MainActivity.LifecycleMethodSignatures.Order(StringComparer.Ordinal));
        canonical.AddArray("activity.bootstrap", managedEvidence.MainActivity.BootstrapMethodSignatures.Order(StringComparer.Ordinal));
        canonical.AddArray(
            "field-use",
            managedEvidence.FieldUses
                .Select(FormatManaged)
                .Order(StringComparer.Ordinal));
        canonical.Add("field-use.count.read", managedEvidence.FieldUseCounts.Read.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.write", managedEvidence.FieldUseCounts.Write.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.address", managedEvidence.FieldUseCounts.Address.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.other", managedEvidence.FieldUseCounts.Other.ToString(CultureInfo.InvariantCulture));
        canonical.Add("field-use.count.total", managedEvidence.FieldUseCounts.Total.ToString(CultureInfo.InvariantCulture));
        canonical.AddArray(
            "call-site",
            managedEvidence.CallSites
                .Select(FormatManaged)
                .Order(StringComparer.Ordinal));
        canonical.Add("call-site.count", managedEvidence.CallSiteCount.ToString(CultureInfo.InvariantCulture));
        canonical.AddArray(
            "pinvoke",
            managedEvidence.PInvokes
                .Select(FormatManaged)
                .Order(StringComparer.Ordinal));
        canonical.AddArray(
            "interop",
            managedEvidence.InteropAttributes
                .Select(FormatManaged)
                .Order(StringComparer.Ordinal));
        canonical.AddArray(
            "native",
            native
                .OrderBy(static entry => entry.EntryPath, StringComparer.Ordinal)
                .ThenBy(static entry => entry.SourceLabel, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Sha256, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Size)
                .Select(FormatNative));
        return canonical.GetHash();
    }

    private static void ValidateManaged(GameHostCompatibilityEvidence evidence)
    {
        if (!evidence.SchemaVersion.Equals(GameHostCompatibilityProbe.SchemaVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(evidence.TargetAssembly.Identity) ||
            !Guid.TryParseExact(evidence.TargetAssembly.ModuleVersionId, "D", out _) ||
            string.IsNullOrWhiteSpace(evidence.MainActivity.BaseType) ||
            string.IsNullOrWhiteSpace(evidence.MainActivity.InstanceFieldSignature) ||
            evidence.TargetAssembly.References.IsDefault ||
            evidence.MainActivity.MethodSignatures.IsDefault ||
            evidence.MainActivity.LifecycleMethodSignatures.IsDefault ||
            evidence.MainActivity.BootstrapMethodSignatures.IsDefault ||
            evidence.FieldUses.IsDefault || evidence.CallSites.IsDefault || evidence.PInvokes.IsDefault ||
            evidence.InteropAttributes.IsDefault ||
            evidence.FieldUseCounts.Total != evidence.FieldUses.Length ||
            evidence.FieldUseCounts.Read != evidence.FieldUses.Count(static item => item.Operation == FieldUseOperation.Read) ||
            evidence.FieldUseCounts.Write != evidence.FieldUses.Count(static item => item.Operation == FieldUseOperation.Write) ||
            evidence.FieldUseCounts.Address != evidence.FieldUses.Count(static item => item.Operation == FieldUseOperation.Address) ||
            evidence.FieldUseCounts.Other != evidence.FieldUses.Count(static item => item.Operation == FieldUseOperation.Other) ||
            evidence.CallSiteCount != evidence.CallSites.Length)
        {
            throw new ArgumentException("Managed evidence is malformed or internally inconsistent.", nameof(evidence));
        }
    }

    private static void ValidateNative(ImmutableArray<GameHostNativeEvidence> entries, string selectedAbi)
    {
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var sourcePathCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalPathCollisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var sourcePath = entry is null ? string.Empty : $"{entry.SourceLabel}\0{entry.EntryPath}";
            var hasGlobalPathCollision = entry is not null &&
                globalPathCollisions.TryGetValue(entry.EntryPath, out var priorPath) &&
                !priorPath.Equals(entry.EntryPath, StringComparison.Ordinal);
            if (entry is null ||
                !IsCanonicalLabel(entry.SourceLabel) ||
                string.IsNullOrWhiteSpace(entry.EntryPath) ||
                !entry.EntryPath.Equals(entry.EntryPath.Normalize(NormalizationForm.FormC), StringComparison.Ordinal) ||
                entry.EntryPath.IndexOfAny(['\\', '\0', '|']) >= 0 ||
                entry.EntryPath.Any(char.IsControl) ||
                !entry.EntryPath.StartsWith($"lib/{selectedAbi}/", StringComparison.Ordinal) ||
                entry.EntryPath.Split('/', StringSplitOptions.None).Length != 3 ||
                !IsCanonicalLibraryFileName(Path.GetFileName(entry.EntryPath)) ||
                entry.Size <= 0 ||
                !IsCanonicalSha256(entry.Sha256) ||
                entry.ElfClass != 2 || entry.DataEncoding != 1 || entry.IdentVersion != 1 ||
                entry.ObjectType is not 2 and not 3 || entry.Machine != 183 ||
                hasGlobalPathCollision ||
                !sourcePaths.Add(sourcePath) || !sourcePathCollisions.Add(sourcePath))
            {
                throw new ArgumentException(
                    "Native evidence must contain unique canonical selected-ABI ARM64 ELF identities.",
                    nameof(entries));
            }

            globalPathCollisions.TryAdd(entry.EntryPath, entry.EntryPath);
        }
    }

    private static bool IsCanonicalLabel(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.Equals(value.Normalize(NormalizationForm.FormC), StringComparison.Ordinal) &&
        value.IndexOfAny(['/', '\\', '\0', '|']) < 0 &&
        !value.Any(char.IsControl);

    private static bool IsCanonicalLibraryFileName(string value) =>
        value.Length is > 0 and <= 255 &&
        value.EndsWith(".so", StringComparison.Ordinal) &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '+');

    private static bool IsCanonicalSha256(string value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        return value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string FormatManaged(FieldUseEvidence evidence) => EncodeFields([
        ("assembly", evidence.AssemblyIdentity),
        ("method", evidence.ContainingMethodSignature),
        ("ordinal", evidence.InstructionOrdinal.ToString(CultureInfo.InvariantCulture)),
        ("opcode", evidence.OpCode),
        ("operation", evidence.Operation.ToString()),
        ("field", evidence.FieldSignature),
    ]);

    private static string FormatManaged(CallSiteEvidence evidence) => EncodeFields([
        ("assembly", evidence.AssemblyIdentity),
        ("method", evidence.ContainingMethodSignature),
        ("ordinal", evidence.InstructionOrdinal.ToString(CultureInfo.InvariantCulture)),
        ("opcode", evidence.OpCode),
        ("called", evidence.CalledMethodSignature),
        ("targetsMainActivity", evidence.TargetsMainActivity ? "true" : "false"),
    ]);

    private static string FormatManaged(PInvokeEvidence evidence) => EncodeFields([
        ("module", evidence.ModuleName),
        ("entryPoint", evidence.EntryPoint),
        ("callingConvention", evidence.CallingConvention),
        ("characterSet", evidence.CharacterSet),
        ("attributes", evidence.Attributes),
        ("assembly", evidence.AssemblyIdentity),
        ("method", evidence.MethodSignature),
    ]);

    private static string FormatManaged(InteropAttributeEvidence evidence)
    {
        var fields = new List<(string Name, string Value)>
        {
            ("assembly", evidence.AssemblyIdentity),
            ("owner", evidence.OwnerSignature),
            ("attributeType", evidence.AttributeType),
            ("constructor", evidence.ConstructorSignature),
        };
        fields.AddRange(evidence.ArgumentFingerprints.Select(
            static (value, index) => ($"argument[{index.ToString(CultureInfo.InvariantCulture)}]", value)));
        return EncodeFields(fields);
    }

    private static string FormatNative(GameHostNativeEvidence evidence) => EncodeFields([
        ("sourceLabel", evidence.SourceLabel),
        ("entryPath", evidence.EntryPath),
        ("size", evidence.Size.ToString(CultureInfo.InvariantCulture)),
        ("sha256", evidence.Sha256),
        ("elfClass", evidence.ElfClass.ToString(CultureInfo.InvariantCulture)),
        ("dataEncoding", evidence.DataEncoding.ToString(CultureInfo.InvariantCulture)),
        ("identVersion", evidence.IdentVersion.ToString(CultureInfo.InvariantCulture)),
        ("osAbi", evidence.OsAbi.ToString(CultureInfo.InvariantCulture)),
        ("abiVersion", evidence.AbiVersion.ToString(CultureInfo.InvariantCulture)),
        ("objectType", evidence.ObjectType.ToString(CultureInfo.InvariantCulture)),
        ("machine", evidence.Machine.ToString(CultureInfo.InvariantCulture)),
        ("flags", evidence.Flags.ToString(CultureInfo.InvariantCulture)),
    ]);

    private static string EncodeFields(IEnumerable<(string Name, string Value)> fields)
    {
        var result = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            result.Append(Encoding.UTF8.GetByteCount(name).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(name).Append('=')
                .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append('\n');
        }

        return result.ToString();
    }

    private sealed class CanonicalHashBuilder
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Add(string name, string value)
        {
            var text = $"{Encoding.UTF8.GetByteCount(name)}:{name}={Encoding.UTF8.GetByteCount(value)}:{value}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(text));
        }

        public void AddArray(string name, IEnumerable<string> values)
        {
            var materialized = values.ToImmutableArray();
            Add($"{name}.count", materialized.Length.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < materialized.Length; index++)
            {
                Add($"{name}[{index}]", materialized[index]);
            }
        }

        public string GetHash() => Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
