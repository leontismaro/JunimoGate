namespace StardewModdingAPI
{
    internal enum LogLevel
    {
        Debug,
        Warn,
    }

    internal interface IManifest
    {
        string UniqueID { get; }
        string EntryDll { get; }
        IReadOnlyList<IManifestDependency> Dependencies { get; }
    }

    internal interface IManifestDependency
    {
        string UniqueID { get; }
    }
}

namespace StardewModdingAPI.AndroidHost
{
    internal enum ModAssemblyBindingPolicy
    {
        Strict,
        FirstLoaded,
        HighestCompatible,
    }
}

namespace StardewModdingAPI.Framework
{
    internal interface IMonitor
    {
        void Log(string message, global::StardewModdingAPI.LogLevel level);
    }

    internal interface IModMetadata
    {
        string DirectoryPath { get; }
        bool IsContentPack { get; }
        global::StardewModdingAPI.IManifest Manifest { get; }
        ModLoading.ModMetadataStatus Status { get; }
    }
}

namespace StardewModdingAPI.Framework.ModLoading
{
    internal enum ModMetadataStatus
    {
        Found,
        Failed,
    }
}
