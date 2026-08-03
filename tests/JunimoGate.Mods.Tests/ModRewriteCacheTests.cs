using JunimoGate.Tests;
using StardewModdingAPI.Framework.ModLoading;
using StardewModdingAPI.Toolkit.Framework.ModData;

internal static class ModRewriteCacheTests
{
    public static void HitsOnlyForTheSameSourceAndContext()
    {
        using var fixture = new RewriteCacheFixture();
        byte[] source = [1, 2, 3, 4];
        byte[] rewritten = [5, 6, 7];
        byte[] symbols = [8, 9];
        var cache = fixture.Create("context-a");

        TestHarness.True(cache.TryStore(
            source,
            sourceSymbols: null,
            rewrittenAssembly: rewritten,
            symbols: symbols,
            references: ["Common, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"],
            warnings: ModWarning.None,
            isCacheable: true));
        TestHarness.True(cache.TryRead(source, sourceSymbols: null, out var hit));
        TestHarness.True(hit.Changed);
        TestHarness.True(hit.AssemblyBytes!.AsSpan().SequenceEqual(rewritten));
        TestHarness.True(hit.SymbolBytes!.AsSpan().SequenceEqual(symbols));
        TestHarness.Equal("Common, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", hit.AssemblyReferences[0].FullName);
        TestHarness.False(cache.TryRead(new byte[] { 1, 2, 3, 5 }, sourceSymbols: null, out _));
        TestHarness.False(fixture.Create("context-b").TryRead(source, sourceSymbols: null, out _));
    }

    public static void StoresAnUnchangedAnalysisResult()
    {
        using var fixture = new RewriteCacheFixture();
        byte[] source = [10, 11, 12];
        var cache = fixture.Create("unchanged");

        TestHarness.True(cache.TryStore(source, sourceSymbols: null, rewrittenAssembly: null, symbols: null, references: [], warnings: ModWarning.None, isCacheable: true));
        TestHarness.True(cache.TryRead(source, sourceSymbols: null, out var hit));
        TestHarness.False(hit.Changed);
    }

    public static void KeysExternalSymbolsAndReplaysWarnings()
    {
        using var fixture = new RewriteCacheFixture();
        byte[] source = [13, 14, 15];
        byte[] sourceSymbols = [16, 17];
        var warnings = ModWarning.PatchesGame | ModWarning.AccessesFilesystem;
        var cache = fixture.Create("symbols-and-warnings");

        TestHarness.True(cache.TryStore(source, sourceSymbols, rewrittenAssembly: new byte[] { 18 }, symbols: new byte[] { 19 }, references: [], warnings: warnings, isCacheable: true));
        TestHarness.True(cache.TryRead(source, sourceSymbols, out var hit));
        TestHarness.Equal(warnings, hit.Warnings);
        TestHarness.False(cache.TryRead(source, new byte[] { 16, 18 }, out _));
        TestHarness.False(cache.TryRead(source, sourceSymbols: null, out _));
    }

    public static void RejectsMalformedEntriesAsSafeMisses()
    {
        using var fixture = new RewriteCacheFixture();
        byte[] source = [20, 21, 22];
        var cache = fixture.Create("malformed");
        string path = cache.GetEntryPathForTests(source);
        File.WriteAllBytes(path, [0x4a, 0x47, 0x4d]);

        TestHarness.False(cache.TryRead(source, sourceSymbols: null, out _));
        TestHarness.False(File.Exists(path));
    }

    public static void DoesNotPublishNonCacheableResults()
    {
        using var fixture = new RewriteCacheFixture();
        byte[] source = [30, 31, 32];
        var cache = fixture.Create("warning");

        TestHarness.False(cache.TryStore(source, sourceSymbols: null, rewrittenAssembly: new byte[] { 33 }, symbols: null, references: [], warnings: ModWarning.None, isCacheable: false));
        TestHarness.False(cache.TryStore(source, sourceSymbols: null, rewrittenAssembly: new byte[] { 33 }, symbols: null, references: [], warnings: ModWarning.BrokenCodeLoaded, isCacheable: true));
        TestHarness.False(cache.TryRead(source, sourceSymbols: null, out _));
        TestHarness.Equal(0, Directory.EnumerateFiles(fixture.Root).Count());
    }

    private sealed class RewriteCacheFixture : IDisposable
    {
        public RewriteCacheFixture()
        {
            this.Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"junimogate-rewrite-cache-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(this.Root);
        }

        public string Root { get; }
        public ModRewriteCache Create(string context) => new(this.Root, context);
        public void Dispose() => Directory.Delete(this.Root, recursive: true);
    }
}
