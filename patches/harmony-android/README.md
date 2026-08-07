# Harmony Android patch

This directory contains JunimoGate's tracked downstream patch for the pinned Harmony/MonoMod Android ARM64 build.

Do not edit or apply the patch without following the maintenance procedure:

- [Harmony Android patch 维护手册](../../docs/harmony-android-maintenance.md)
- [RuntimeProbe result](../../docs/runtime-probe-result.md)
- [`build/harmony-android-versions.sh`](../../build/harmony-android-versions.sh)
- [`build/build-harmony-android.sh`](../../build/build-harmony-android.sh)

The current known-good package recipe is `Lib.Harmony 2.4.2-junimogate.63`. Any source or patch modification requires a new `-junimogate.N` version and proportional host/device verification for the changed Android path. Generated source trees, NuGet packages, and per-build provenance remain ignored; do not commit full upstream source snapshots or locally generated packages here.
