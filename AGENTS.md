# JunimoGate repository instructions

## Product direction

JunimoGate is an Android launcher that prepares a private game workspace and hosts a source-integrated SMAPI runtime. Engineering decisions prioritize user actions, bounded update handling, launch latency, diagnostics, and maintainability.

## Compatibility model

The game bridge is a semantic recipe family, currently `stardew-android-mainactivity-bridge/v1`. Compatibility analysis is local to the required type/member signatures, IL source/consumer pattern, stack behavior, bridge capability, and rewrite postconditions.

Exact APK hashes, assembly MVIDs, whole-method hashes, global call counts, and native inventories are diagnostic or regression inputs. They are not the normal version-eligibility model.

## Launch model

Deep Prepare is limited to first import, detected game updates, relevant schema changes, missing or damaged runtime state, explicit repair, and release verification. Each expensive operation should occur at most once per preparation transaction.

Fast Launch reads the active snapshot once in the launcher, checks PackageManager once on launch, passes an in-memory prepared handle into descriptor creation, reads the snapshot once in `:game`, and builds one runtime file inventory before SMAPI starts. Later assembly and Content access reuses that inventory.

## Required boundaries

- Never commit or distribute commercial game payloads or decompiled source.
- Do not root, re-sign, inject into, or impersonate the original game process.
- Do not accept caller-selected workspace, assembly, or Content paths.
- Keep source workspaces immutable and rewritten overlays separate.
- Use staging and atomic commit for generated state and imported user data.
- Preserve ZIP traversal, duplicate-path, count, and size limits.
- Reject host/framework shadowing, duplicate managed identity, and path escape.
- Keep licensing callbacks outside rewrite targets unless a future rule explicitly requires a new review.
- Do not copy the game's runtime or AOT payload into JunimoGate.

## Working rules

- Read current code and documentation before changing behavior.
- Prefer existing project abstractions and focused tests.
- Keep changes scoped by page, feature, or runtime responsibility.
- Commit the SMAPI submodule change before the outer gitlink update.
- Use repository-local toolchains and an absolute `JUNIMOGATE_GAME_REFERENCE_DIR` for Android builds.
- Keep local game inputs and generated reports under ignored paths.
- Run broad artifact and commercial-payload gates for packaging, runtime, distribution, or release changes; use focused checks for ordinary feature work.

## Public documentation

Current architecture belongs in `README.md` and the documents indexed by `docs/README.md`. Historical engineering checkpoints belong in `docs/history.md`. Do not link to files outside the repository or use private device identifiers, save directory names, home-directory paths, credentials, or raw logcat captures.

Before claiming release readiness, verify the project license, third-party source obligations, production signing, Android policy constraints, submodule availability, clean recursive clone, and artifact contents.
