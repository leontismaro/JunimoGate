# Version and cache identities

JunimoGate uses separate identities because an Android application update, a SMAPI code change,
a bundled dependency change, and a persisted-data format change have different invalidation costs.
Changing one must not force unrelated game extraction or workspace rewriting.

| Identity | Current source | Purpose | Change when |
| --- | --- | --- | --- |
| Android app version | `JunimoGate.App.csproj` | Android install/upgrade ordering and user-visible version | publishing a new APK |
| SMAPI API version | `smapi/src/SMAPI/SMAPI.csproj` | Mod API compatibility | adopting a new upstream SMAPI API |
| SMAPI `BuildCode` | `SMAPIAndroidBuild.cs` | human-readable JunimoGate SMAPI implementation label used in logs and backups | when a release needs a new diagnostic label |
| SMAPI `BundleId` | generated `smapi-bundle-manifest.json` | immutable APK asset set under `smapi-managed` and `smapi-internal` | generated automatically whenever any bundled file changes |
| Schema/recipe version | owning contract type | persisted JSON or rewrite semantics | the corresponding persisted shape or interpretation changes |

`BuildCode` and `BundleId` are intentionally not stored in the prepared game snapshot. A pure
SMAPI or Harmony update deploys a new bundle and keeps the already prepared game workspace. A
game package update or a game-workspace schema/recipe change enters Deep Prepare once.

`build/JunimoGate.SmapiBundle.targets` owns the complete bundle input list. Immediately before
Android asset collection, it invokes `generate-smapi-bundle-manifest.py`, which hashes the final
SMAPI, Harmony, Cecil, configuration, and translation payloads and derives `BundleId` from their
canonical content manifest. Identical content keeps the same identity; any content change produces
a new identity without a manually maintained revision. The runtime reads the packaged manifest,
uses its identity as the private deployment-directory key, and does not re-hash bundle files on the
Fast Launch path.

The generated `BundleId`, rather than `BuildCode`, partitions the assembly materialization and Mod
rewrite caches. SMAPI code or dependency changes therefore invalidate those caches automatically.
`BuildCode` is no longer a correctness key and does not need to change during every development
iteration.

Generated Harmony and MonoGame packages live in the ignored local NuGet feed. Their exact
versions and source/output hashes are pinned by `build/harmony-android-versions.sh` and
`build/monogame-android-versions.sh`. Old packages are not runtime fallbacks: project references
request one exact version.
