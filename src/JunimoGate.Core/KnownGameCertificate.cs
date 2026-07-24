namespace JunimoGate.Core;

/// <summary>Result of comparing an installed package certificate with JunimoGate's tested game identity.</summary>
public enum GameCertificateStatus
{
    /// <summary>No tested certificate has been configured for this package.</summary>
    NotConfigured,

    /// <summary>The current certificate exactly matches the certificate tested by JunimoGate.</summary>
    KnownTested,

    /// <summary>Android verified that the current certificate descends from the certificate tested by JunimoGate.</summary>
    KnownTestedAfterRotation,

    /// <summary>The package certificate is unrelated to the certificate tested by JunimoGate.</summary>
    Unrecognized,
}

/// <summary>A certificate identity decision that can be enforced before extracted game code is executed.</summary>
public sealed record GameCertificateVerification
{
    internal GameCertificateVerification(
        GameCertificateStatus status,
        Sha256Digest? matchedKnownCertificate)
    {
        if (matchedKnownCertificate is { IsValid: false })
        {
            throw new ArgumentException("A matched game certificate must be a valid SHA-256 digest.", nameof(matchedKnownCertificate));
        }

        Status = status;
        MatchedKnownCertificate = matchedKnownCertificate;
    }

    /// <summary>Gets how the installed certificate relates to JunimoGate's tested game certificate.</summary>
    public GameCertificateStatus Status { get; }

    /// <summary>Gets the tested certificate matched directly or through Android-verified rotation.</summary>
    public Sha256Digest? MatchedKnownCertificate { get; }

    /// <summary>Gets whether later workspace/host stages may execute code from this installation.</summary>
    public bool AllowsCodeExecution =>
        Status is GameCertificateStatus.KnownTested or GameCertificateStatus.KnownTestedAfterRotation;
}

/// <summary>The minimal package/certificate identity currently tested by JunimoGate.</summary>
public static class KnownGameCertificate
{
    /// <summary>Google Play package name for Stardew Valley.</summary>
    public const string PlayPackageName = "com.chucklefish.stardewvalley";

    /// <summary>
    /// SHA-256 of the app-signing certificate observed consistently before and after installation
    /// for the tested Google Play 1.6.15.3/versionCode 245 package. This is a tested identity anchor,
    /// not a claim of independent publisher certification.
    /// </summary>
    public const string PlayCertificateSha256 =
        "c7b27f1faf2f350e3c117875bde2353cea837ebe1b3c2ce23513bb191d95852d";

    private static readonly Sha256Digest PlayCertificate = Sha256Digest.Parse(PlayCertificateSha256);

    /// <summary>Compares an Android-verified package signing identity with the tested Play identity.</summary>
    public static GameCertificateVerification Verify(
        string packageName,
        SigningIdentity signingIdentity)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("A package name is required.", nameof(packageName));
        }

        ArgumentNullException.ThrowIfNull(signingIdentity);
        if (!packageName.Equals(PlayPackageName, StringComparison.Ordinal))
        {
            return new GameCertificateVerification(GameCertificateStatus.NotConfigured, null);
        }

        var currentSigners = signingIdentity.CurrentSignerDigests;
        if (currentSigners.Count == 1 && currentSigners[0].Equals(PlayCertificate))
        {
            return new GameCertificateVerification(GameCertificateStatus.KnownTested, PlayCertificate);
        }

        // Android SigningInfo supplies this oldest-to-current history only after verifying the
        // APK Signature Scheme v3 proof of rotation. API 26-27 snapshots have no history, so they
        // intentionally require the direct match above.
        if (currentSigners.Count == 1 &&
            signingIdentity.RotationHistory.Count > 1 &&
            signingIdentity.RotationHistory.Take(signingIdentity.RotationHistory.Count - 1).Contains(PlayCertificate))
        {
            return new GameCertificateVerification(
                GameCertificateStatus.KnownTestedAfterRotation,
                PlayCertificate);
        }

        return new GameCertificateVerification(GameCertificateStatus.Unrecognized, null);
    }
}
