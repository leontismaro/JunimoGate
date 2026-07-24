using System.Security.Cryptography;
using System.Text;
using JunimoGate.Core;

namespace JunimoGate.Extraction;

/// <summary>A deterministic, versioned identity for an extracted and rewritten workspace.</summary>
public readonly record struct WorkspaceCacheKey
{
    private const string CanonicalFormatVersion = "junimogate-workspace-cache-key:v2";

    private WorkspaceCacheKey(Sha256Digest digest)
    {
        Digest = digest;
    }

    public Sha256Digest Digest { get; }

    public static WorkspaceCacheKey Create(
        string packageName,
        long longVersionCode,
        string abi,
        SigningIdentity signingIdentity,
        IEnumerable<Sha256Digest> apkSourceDigests,
        string extractorSchema,
        string rewriterRecipe,
        string smapiBuildId)
    {
        RequireText(packageName, nameof(packageName));
        if (longVersionCode < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(longVersionCode));
        }

        RequireText(abi, nameof(abi));
        ArgumentNullException.ThrowIfNull(signingIdentity);
        RequireText(extractorSchema, nameof(extractorSchema));
        RequireText(rewriterRecipe, nameof(rewriterRecipe));
        RequireText(smapiBuildId, nameof(smapiBuildId));
        ArgumentNullException.ThrowIfNull(apkSourceDigests);

        var sourceDigests = apkSourceDigests.ToArray();
        if (sourceDigests.Length == 0)
        {
            throw new ArgumentException("At least one APK source digest is required.", nameof(apkSourceDigests));
        }

        if (sourceDigests.Any(static digest => !digest.IsValid))
        {
            throw new ArgumentException("Every APK source digest must be valid.", nameof(apkSourceDigests));
        }

        var sources = sourceDigests
            .Select(static digest => digest.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var payload = new StringBuilder();
        payload.Append(CanonicalFormatVersion).Append('\n');
        AppendField(payload, "packageName", packageName);
        AppendField(payload, "longVersionCode", longVersionCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(payload, "abi", abi);
        AppendField(payload, "currentSignerCount", signingIdentity.CurrentSignerDigests.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var signer in signingIdentity.CurrentSignerDigests)
        {
            AppendField(payload, "currentSignerSha256", signer.Value);
        }

        AppendField(payload, "rotationHistoryCount", signingIdentity.RotationHistory.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var signer in signingIdentity.RotationHistory)
        {
            AppendField(payload, "rotationHistorySha256", signer.Value);
        }

        AppendField(payload, "extractorSchema", extractorSchema);
        AppendField(payload, "rewriterRecipe", rewriterRecipe);
        AppendField(payload, "smapiBuildId", smapiBuildId);
        AppendField(payload, "apkSourceCount", sources.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var source in sources)
        {
            AppendField(payload, "apkSourceSha256", source);
        }

        var bytes = Encoding.UTF8.GetBytes(payload.ToString());
        var digestText = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new WorkspaceCacheKey(Sha256Digest.Parse(digestText));
    }

    public override string ToString() => Digest.ToString();

    private static void AppendField(StringBuilder payload, string name, string value)
    {
        var byteLength = Encoding.UTF8.GetByteCount(value);
        payload.Append(name).Append(':').Append(byteLength).Append(':').Append(value).Append('\n');
    }

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identity value is required.", parameterName);
        }
    }
}
