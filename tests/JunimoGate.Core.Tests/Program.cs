using JunimoGate.Core;
using JunimoGate.Tests;

return TestHarness.Run(
    ("Sha256Digest accepts canonical lowercase hex", () =>
    {
        var text = new string('a', Sha256Digest.HexLength);
        var digest = Sha256Digest.Parse(text);
        TestHarness.Equal(text, digest.Value);
        TestHarness.True(Sha256Digest.TryParse(text, out _));
    }),
    ("Sha256Digest rejects wrong length", () =>
    {
        TestHarness.False(Sha256Digest.TryParse(new string('a', 63), out _));
        TestHarness.Throws<FormatException>(() => Sha256Digest.Parse(string.Empty));
    }),
    ("Sha256Digest rejects uppercase, non-hex, and default values", () =>
    {
        TestHarness.False(Sha256Digest.TryParse(new string('A', 64), out _));
        TestHarness.False(Sha256Digest.TryParse(new string('g', 64), out _));
        TestHarness.False(default(Sha256Digest).IsValid);
    }),
    ("Game installation validates required identity", () =>
    {
        var digest = Sha256Digest.Parse(new string('1', 64));
        var installation = new GameInstallationIdentity(
            "com.chucklefish.stardewvalley",
            "1.6.0",
            10600,
            "arm64-v8a",
            digest,
            [new ApkSourceIdentity(Path.Combine(Path.GetTempPath(), "base.apk"), digest)]);

        TestHarness.Equal(10600L, installation.LongVersionCode);
        TestHarness.Equal(1, installation.ApkSources.Count);
        TestHarness.Throws<ArgumentException>(() => new ApkSourceIdentity(Path.Combine(Path.GetTempPath(), "invalid.apk"), default));
        TestHarness.Throws<ArgumentException>(() => new GameInstallationIdentity(
            "com.chucklefish.stardewvalley",
            "1.6.0",
            1,
            "arm64-v8a",
            default,
            installation.ApkSources));
        TestHarness.Throws<ArgumentException>(() => new GameInstallationIdentity(
            "invalid",
            "1.6.0",
            1,
            "arm64-v8a",
            digest,
            installation.ApkSources));
    }));
