# Historical engineering checkpoints

This file records only the architectural transitions needed to understand the current source tree.

## Semantic bridge

The earlier exact catalog was replaced by `stardew-android-mainactivity-bridge/v1`, which matches local type/member and IL relationships and checks rewrite postconditions. Deep Prepare owns analysis and rewrite; Fast Launch reuses the prepared result.

## Source-integrated SMAPI

The external-loader approach was replaced by source projects from the `smapi/` submodule. `SmapiGameActivity` now creates a host-injected runtime/session in `:game`, and the launcher remains free of game and Mod types.

## Product surfaces

Subsequent work added Mod archive import, a global library, groups, immutable launch selections, logs, settings, save management, localization, and the current launcher navigation. Current behavior is documented by the architecture files indexed in [README.md](README.md).
