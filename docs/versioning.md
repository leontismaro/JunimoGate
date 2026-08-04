# Version and cache identities

JunimoGate uses separate identities because an Android application update, a SMAPI code change,
a bundled dependency change, and a persisted-data format change have different invalidation costs.
Changing one must not force unrelated game extraction or workspace rewriting.

| Identity | Current source | Purpose | Change when |
| --- | --- | --- | --- |
| Android app version | `JunimoGate.App.csproj` | Android install/upgrade ordering and user-visible version | publishing a new APK |
| SMAPI API version | `smapi/src/SMAPI/SMAPI.csproj` | Mod API compatibility | adopting a new upstream SMAPI API |
| SMAPI `BuildCode` | `SMAPIAndroidBuild.cs` | JunimoGate SMAPI implementation and runtime rewrite/load cache partition | SMAPI Android behavior or rewrite logic changes |
| SMAPI `BundleId` | `GameHostRuntimeIdentity.SmapiBundleId` | immutable APK asset set under `smapi-managed` and `smapi-internal` | any bundled file changes without a new `BuildCode` |
| Schema/recipe version | owning contract type | persisted JSON or rewrite semantics | the corresponding persisted shape or interpretation changes |

`BuildCode` and `BundleId` are intentionally not stored in the prepared game snapshot. A pure
SMAPI or Harmony update deploys a new bundle and keeps the already prepared game workspace. A
game package update or a game-workspace schema/recipe change enters Deep Prepare once.

The current bundle identity is the SMAPI `BuildCode` plus a small bundle revision. A SMAPI code
change therefore invalidates the bundle automatically. When only Harmony or another embedded
dependency changes, increment `SmapiBundleRevision`; do not rename the SMAPI build or the game
workspace schema to force invalidation.

Generated Harmony and MonoGame packages live in the ignored local NuGet feed. Their exact
versions and source/output hashes are pinned by `build/harmony-android-versions.sh` and
`build/monogame-android-versions.sh`. Old packages are not runtime fallbacks: project references
request one exact version.
