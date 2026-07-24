using JunimoGate.Mods;
using JunimoGate.Tests;

return TestHarness.Run(
    ("SafeArchivePath normalizes separators and redundant slashes", () =>
    {
        TestHarness.Equal("Folder/Sub/mod.dll", SafeArchivePath.Parse("Folder\\Sub//mod.dll").Value);
        TestHarness.Equal("folder", SafeArchivePath.Parse("folder/").Value);
    }),
    ("SafeArchivePath rejects traversal", () =>
    {
        foreach (var candidate in new[] { "../mod.dll", "mods/../evil.dll", "./mod.dll", "mods/./mod.dll" })
        {
            TestHarness.False(SafeArchivePath.TryParse(candidate, out _), candidate);
        }
    }),
    ("SafeArchivePath rejects absolute drive and UNC paths", () =>
    {
        foreach (var candidate in new[] { "/etc/passwd", "\\rooted\\file", "C:\\mods\\mod.dll", "C:relative.dll", "\\\\server\\share\\mod.dll" })
        {
            TestHarness.False(SafeArchivePath.TryParse(candidate, out _), candidate);
        }
    }),
    ("SafeArchivePath rejects empty and NUL paths", () =>
    {
        TestHarness.False(SafeArchivePath.TryParse("", out _));
        TestHarness.False(SafeArchivePath.TryParse("   ", out _));
        TestHarness.False(SafeArchivePath.TryParse("mods/evil\0.dll", out _));
    }),
    ("ProfileId enforces conservative stable syntax", () =>
    {
        TestHarness.Equal("farm-2", ProfileId.Parse("farm-2").Value);
        TestHarness.True(ProfileId.TryParse(new string('a', 64), out _));
        foreach (var candidate in new[] { "", "-farm", "Farm", "farm_name", new string('a', 65) })
        {
            TestHarness.False(ProfileId.TryParse(candidate, out _), candidate);
        }
    }),
    ("ProfileLayout produces absolute per-profile paths", () =>
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "junimogate-profiles"));
        var layout = new ProfileLayout(root, ProfileId.Parse("main"));
        var profile = Path.Combine(root, "main");
        TestHarness.Equal(Path.Combine(profile, "profile.json"), layout.ProfileJsonPath);
        TestHarness.Equal(Path.Combine(profile, "enabled"), layout.EnabledDirectory);
        TestHarness.Equal(Path.Combine(profile, "disabled"), layout.DisabledDirectory);
        TestHarness.Equal(Path.Combine(profile, "downloads"), layout.DownloadsDirectory);
        TestHarness.Equal(Path.Combine(profile, "staging"), layout.StagingDirectory);
        TestHarness.True(Path.IsPathFullyQualified(layout.StagingDirectory));
        TestHarness.Throws<ArgumentException>(() => new ProfileLayout("relative/profiles", ProfileId.Parse("main")));
    }));
