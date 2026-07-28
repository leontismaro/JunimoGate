# JunimoGate Android SMAPI patch series

Baseline: Android SMAPI 4.3.2.5 at
`6a34bbeb6e891536cdd948594094482ba0d8d264`.

1. `build-boundary/v1`
   - build as a JunimoGate-owned library project;
   - use the public MonoGame Android provider and patched Harmony package;
   - accept commercial assemblies only through non-copying local references;
   - package deterministic `smapi-internal` assets.
2. `host-runtime/v1`
   - inject Activity, paths, main-thread dispatch, assembly loading, View attachment,
     failure reporting, and exit behavior;
   - expose `SmapiRuntime` and one-shot `SmapiSession`;
   - keep the existing `SCore`, `SGameRunner`, `SGame`, events, content, and Mod lifecycle.
3. `default-alc/v1`
   - route Mod assembly loads through the JunimoGate managed loader;
   - write rewritten Mod assemblies to an app-private cache before loading;
   - reject host, framework, game, and SMAPI assembly shadowing.
4. `android-runtime-compat/v1`
   - configure `MobileDisplay` before constructing the SMAPI game runner;
   - provide app-private managed SMAPI assemblies for Cecil resolution without packaging commercial game DLLs;
   - surface asynchronous Mod-loading failures through SMAPI logs and the host failure callback;
   - raise the full-frame `Rendered` event after Android render-target composition and before MonoGame presents the backbuffer.
5. `android-idle-runtime/v1`
   - disable the mobile console listener by default and block instead of polling when enabled;
   - skip state watcher and render-event work while no Mod subscribes to the corresponding events;
   - remove the fork branding overlay and keep optional OGG workers at normal background priority.

This file records patch intent. Git history remains the authoritative source diff.
