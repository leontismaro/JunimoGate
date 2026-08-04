# Harmony Android patch

This directory contains JunimoGate's tracked downstream patch for the pinned Harmony/MonoMod Android ARM64 build.

Do not edit or apply the patch without following the maintenance procedure:

- [Harmony Android patch 维护手册](../../docs/harmony-android-maintenance.md)
- [RuntimeProbe result](../../docs/runtime-probe-result.md)
- [`build/harmony-android-versions.sh`](../../build/harmony-android-versions.sh)
- [`build/build-harmony-android.sh`](../../build/build-harmony-android.sh)

The current immutable known-good package is `Lib.Harmony 2.4.2-junimogate.63`. Any binary-changing modification requires a new `-junimogate.N` version, regenerated hashes, and proportional host/device verification for the changed Android path. Generated source trees and NuGet packages remain ignored; do not commit full upstream source snapshots or locally generated packages here.
