using StardewModdingAPI.Mobile;

namespace JunimoGate.GameHost;

public static class GameLaunchSchema
{
    public const string Snapshot = "junimogate-prepared-game-snapshot/v8";
    public const string LegacySnapshotV7 = "junimogate-prepared-game-snapshot/v7";
    public const string Descriptor = "junimogate-game-launch-descriptor/v4";
    public const string Activation = "junimogate-game-activation/v1";
    public const string Outcome = "junimogate-game-launch-outcome/v1";

    public static bool IsSupportedSnapshot(string schema) =>
        schema is Snapshot or LegacySnapshotV7;
}

public static class GameHostRuntimeIdentity
{
    /// <summary>Identifies SMAPI host behavior and partitions runtime rewrite/load caches.</summary>
    public const string BuildId = SMAPIAndroidBuild.BuildCode;

    /// <summary>
    /// Identifies the complete embedded SMAPI asset set. Increment the revision when a bundled
    /// dependency changes without a corresponding SMAPI <see cref="BuildId"/> change.
    /// </summary>
    public const int SmapiBundleRevision = 1;

    public static string SmapiBundleId { get; } = $"{BuildId}-bundle.{SmapiBundleRevision}";
}
