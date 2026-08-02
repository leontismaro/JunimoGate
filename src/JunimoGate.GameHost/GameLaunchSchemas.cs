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
    public const string BuildId = SMAPIAndroidBuild.BuildCode;
}
