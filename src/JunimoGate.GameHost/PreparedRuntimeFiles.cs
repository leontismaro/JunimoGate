using System.Collections.ObjectModel;
using System.Diagnostics;
using Android.Util;
using JunimoGate.Core;

namespace JunimoGate.GameHost;

internal sealed class PreparedRuntimeFiles
{
    private PreparedRuntimeFiles(
        IReadOnlyDictionary<string, string> managedAssemblyPaths,
        IReadOnlyDictionary<string, string> contentPaths)
    {
        ManagedAssemblyPaths = managedAssemblyPaths;
        ContentPaths = contentPaths;
    }

    public IReadOnlyDictionary<string, string> ManagedAssemblyPaths { get; }
    public IReadOnlyDictionary<string, string> ContentPaths { get; }

    public static PreparedRuntimeFiles BuildAndValidate(PreparedGameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var stopwatch = Stopwatch.StartNew();

        foreach (var directory in new[]
                 {
                     snapshot.SourceWorkspacePath, snapshot.AppliedWorkspacePath, snapshot.ModsDirectory,
                     snapshot.InternalDirectory, snapshot.ConfigDirectory, snapshot.LogDirectory,
                     snapshot.SaveDirectory, snapshot.BackupDirectory,
                 })
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("A prepared runtime directory is missing.");
        }

        var sourceAssemblies = PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            snapshot.SourceWorkspacePath,
            snapshot.ManagedAssemblies.Select(static entry =>
                new PreparedRuntimeFileSpec(entry.SimpleName, entry.RelativePath, entry.Size)),
            StringComparer.OrdinalIgnoreCase,
            "managed assembly");
        var content = PreparedRuntimeFileInventoryBuilder.BuildAndValidate(
            snapshot.SourceWorkspacePath,
            snapshot.ContentFiles.Select(static entry =>
                new PreparedRuntimeFileSpec(entry.RelativePath, entry.RelativePath, entry.Size)),
            StringComparer.Ordinal,
            "Content",
            requiredPrefix: "Content/");

        var appliedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(snapshot.AppliedWorkspacePath));
        var overlay = Path.GetFullPath(snapshot.OverlayAssemblyPath);
        if (!overlay.StartsWith(appliedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("The prepared game overlay escaped its applied workspace.");
        var overlayInfo = new FileInfo(overlay);
        if (!overlayInfo.Exists)
            throw new FileNotFoundException("The prepared game overlay is missing.", overlay);
        if (overlayInfo.Length != snapshot.OverlayAssemblySize)
            throw new InvalidDataException("The prepared game overlay size changed.");

        var managed = new Dictionary<string, string>(sourceAssemblies, StringComparer.OrdinalIgnoreCase);
        if (!managed.ContainsKey("StardewValley"))
            throw new InvalidDataException("The prepared managed assembly inventory has no StardewValley entry.");
        managed["StardewValley"] = overlay;

        Log.Info(
            "JunimoGate.LaunchTrace",
            $"game packageSnapshots=0 runtimeInventoryPasses=1 assemblies={managed.Count} content={content.Count} " +
            $"durationMs={Math.Max(1, stopwatch.ElapsedMilliseconds)}");
        return new PreparedRuntimeFiles(
            new ReadOnlyDictionary<string, string>(managed),
            content);
    }
}
