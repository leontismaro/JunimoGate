using System.IO.Compression;
using System.Text;
using JunimoGate.Core;
using JunimoGate.Tests;

internal static class ProductDiagnosticTests
{
    public static void ReadsOnlyTheBoundedTail()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("source.log", "secret-first-line\nkept-second-line\nkept-last-line\n");
        var text = DiagnosticBundleBuilder.ReadTailTextAsync(path, 31).AsTask().GetAwaiter().GetResult();

        TestHarness.False(text.Contains("secret-first-line", StringComparison.Ordinal));
        TestHarness.True(text.Contains("kept-last-line", StringComparison.Ordinal));
    }

    public static void RedactsPrivatePathsAndTokens()
    {
        var redacted = DiagnosticTextRedactor.Redact(
            "path=/data/user/0/org.junimogate.app/files/a token=abcdefghijklmnopqrstuvwxyz123456 " +
            "\"launchKey\":\"nonhexadecimal_secret_value\" " +
            "id=0123456789abcdef0123456789abcdef C:\\Users\\Example\\save");

        TestHarness.False(redacted.Contains("/data/user", StringComparison.Ordinal));
        TestHarness.False(redacted.Contains("abcdefghijklmnopqrstuvwxyz", StringComparison.Ordinal));
        TestHarness.False(redacted.Contains("nonhexadecimal_secret_value", StringComparison.Ordinal));
        TestHarness.False(redacted.Contains("0123456789abcdef", StringComparison.Ordinal));
        TestHarness.False(redacted.Contains("C:\\Users", StringComparison.Ordinal));
        TestHarness.True(redacted.Contains("<private-path>", StringComparison.Ordinal));
        TestHarness.True(redacted.Contains("<redacted>", StringComparison.Ordinal));
        TestHarness.True(redacted.Contains("<redacted-id>", StringComparison.Ordinal));
    }

    public static void CreatesAPathFreeBoundedZip()
    {
        using var fixture = new Fixture();
        var sourcePath = fixture.Write(
            "launcher.jsonl",
            "{\"message\":\"/home/example/private token=abcdefghijklmnopqrstuvwxyz123456\"}\n");
        var source = new DiagnosticTextSource("launcher-current.txt", sourcePath, 1024);
        var preview = DiagnosticBundleBuilder.Preview([source]);
        TestHarness.Equal(1, preview.Sources.Count);
        TestHarness.True(preview.TotalIncludedBytes > 0);

        using var output = new MemoryStream();
        DiagnosticBundleBuilder.CreateAsync(
            output,
            new Dictionary<string, string?> { ["version"] = "test", ["path"] = sourcePath },
            [source]).AsTask().GetAwaiter().GetResult();
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        TestHarness.True(archive.Entries.Select(static entry => entry.FullName)
            .SequenceEqual(["diagnostics.json", "logs/launcher-current.txt"]));
        var combined = string.Join('\n', archive.Entries.Select(ReadEntry));
        TestHarness.False(combined.Contains(fixture.Root, StringComparison.Ordinal));
        TestHarness.False(combined.Contains("abcdefghijklmnopqrstuvwxyz", StringComparison.Ordinal));
        TestHarness.True(combined.Contains("<private-path>", StringComparison.Ordinal));
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-diagnostics-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string name, string value)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, value, new UTF8Encoding(false));
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
