# Roadmap

The current repository contains the launcher, preparation pipeline, source-integrated SMAPI host, Mod management, logs, settings, and save-management surfaces. Remaining work is organized by release impact.

## Release blockers

- select and apply a project-wide license;
- resolve OpenAL corresponding-source and relink/replacement obligations;
- review LGPL combined-work requirements for the SMAPI submodule and packaged dependencies;
- complete production signing and update-channel ownership;
- review Android dynamic-code and native-library distribution policy;
- publish the referenced SMAPI submodule commits;
- verify a clean recursive clone and reproducible release build.

## Compatibility work

- exercise the semantic recipe after future game package updates;
- add ARM64 devices with different API levels and memory-page configurations;
- record structured failure fixtures for changed AssemblyStore and bridge patterns;
- expand Mod dependency and assembly-binding fixtures without Mod-specific launcher patches;
- keep compatibility results tied to exact observed checkpoints.

## Product work

- finish accessibility and narrow-screen checks across launcher pages;
- refine long-running import, preparation, and recovery feedback;
- add bounded diagnostic export with an explicit redaction manifest;
- define retention and cleanup controls for user-generated data;
- prepare contributor, security, release, and support documentation.

## Deferred work

- automatic Mod downloads or marketplace accounts;
- cloud synchronization;
- automatic acquisition of missing dependencies;
- per-group configuration overlays;
- remote telemetry;
- SMAPI delivery independent of an application update.
