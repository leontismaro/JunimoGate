# Harmony Android patch

This directory contains JunimoGate's tracked downstream patch for the pinned Harmony/MonoMod Android ARM64 build.

Do not edit or apply the patch without following the maintenance procedure:

- [Harmony Android patch 维护手册](../../docs/harmony-android-maintenance.md)
- [RuntimeProbe result](../../docs/runtime-probe-result.md)
- [`build/harmony-android-versions.sh`](../../build/harmony-android-versions.sh)
- [`build/build-harmony-android.sh`](../../build/build-harmony-android.sh)

The current build recipe is `Lib.Harmony 2.4.2-junimogate.67`; `.64` remains the previous device-tested baseline. The `.67` hardening requires fresh Android Debug/Release device acceptance before a runtime compatibility claim. Any source or patch modification requires a new `-junimogate.N` version and proportional host/device verification for the changed Android path. Generated source trees, NuGet packages, and per-build provenance remain ignored; do not commit full upstream source snapshots or locally generated packages here.

The hardening validates complete page ranges and executable mappings, permits only provable
shared-file write aliases, bounds Mono ARM64 detours using JIT code metadata, and restores
original bytes/cache/protections after recoverable patch failures. Unconfirmed rollback
terminates the game process. Host fault-injection tests exercise the policy from the actual
packaged assembly; they do not establish Android device compatibility or concurrent code
replacement safety.
