# Android SMAPI source provenance

- Android fork repository: `https://github.com/NRTnarathip/SMAPI-Android-1.6`
- Android fork base: `6a34bbeb6e891536cdd948594094482ba0d8d264` (`4.3.2.5`)
- Upstream repository: `https://github.com/Pathoschild/SMAPI`
- Upstream release: `821167e5c511bf3a2d98f604e5e838561c469219` (`4.5.2`)
- Android patches migrated on: `2026-07-28`
- License: GNU Lesser General Public License v3.0

The vendored snapshot applies the necessary Android host/runtime patches from the
fork lineage to upstream SMAPI 4.5.2. It contains only the SMAPI runtime, Toolkit,
CoreInterfaces, shared internal sources, translations, and bundled
metadata/blacklist files. It intentionally excludes SMAPILoader and all
commercial game binaries.

JunimoGate-specific changes are summarized in
`patches/smapi-android/series.md`.
