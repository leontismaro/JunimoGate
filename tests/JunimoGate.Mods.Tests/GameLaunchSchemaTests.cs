using System.Text.Json;
using System.Text.Json.Nodes;
using JunimoGate.GameHost;
using JunimoGate.Tests;

internal static class GameLaunchSchemaTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void ReadsLegacySnapshotWithoutBundleIdentity()
    {
        var current = CreateSnapshot(GameLaunchSchema.Snapshot);
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(current, JsonOptions))!.AsObject();
        legacy["schema"] = GameLaunchSchema.LegacySnapshotV7;
        legacy["buildId"] = "smapi-4.5.2-junimogate.74";
        legacy["internalDirectory"] = "/private/runtime/smapi/bundles/smapi-4.5.2-junimogate.74/smapi-internal";

        var parsed = legacy.Deserialize<PreparedGameSnapshot>(JsonOptions)
            ?? throw new InvalidOperationException("The legacy snapshot did not deserialize.");

        TestHarness.Equal(GameLaunchSchema.LegacySnapshotV7, parsed.Schema);
        TestHarness.True(GameLaunchSchema.IsSupportedSnapshot(parsed.Schema));
        TestHarness.Equal(current.SourceWorkspaceKey, parsed.SourceWorkspaceKey);
        TestHarness.Equal(current.AppliedWorkspaceKey, parsed.AppliedWorkspaceKey);
    }

    public static void RejectsUnknownSnapshotSchemas()
    {
        TestHarness.True(GameLaunchSchema.IsSupportedSnapshot(GameLaunchSchema.Snapshot));
        TestHarness.True(GameLaunchSchema.IsSupportedSnapshot(GameLaunchSchema.LegacySnapshotV7));
        TestHarness.False(GameLaunchSchema.IsSupportedSnapshot("junimogate-prepared-game-snapshot/v6"));
    }

    public static void DoesNotPersistSmapiBundleIdentity()
    {
        var json = JsonSerializer.SerializeToNode(CreateSnapshot(GameLaunchSchema.Snapshot), JsonOptions)!.AsObject();

        TestHarness.False(json.ContainsKey("buildId"));
        TestHarness.False(json.ContainsKey("internalDirectory"));
    }

    private static PreparedGameSnapshot CreateSnapshot(string schema) => new(
        schema,
        "stardew-android-mainactivity-bridge/v1",
        "com.chucklefish.stardewvalley",
        "1.6.15.3",
        245,
        "arm64-v8a",
        new string('a', 64),
        new string('b', 64),
        "/private/runtime/workspaces/source",
        new string('c', 64),
        "/private/runtime/workspaces/applied",
        "/private/runtime/workspaces/applied/StardewValley.dll",
        123,
        "/private/user/config",
        "/private/user/logs",
        "/external/Saves",
        "/private/user/save-backups",
        [new PreparedManagedAssembly("StardewValley", "StardewValley.dll", 123)],
        [new PreparedContentFile("Content/Maps/Farm.xnb", 456)],
        DateTimeOffset.UnixEpoch);
}
