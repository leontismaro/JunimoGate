# JunimoGate Android SMAPI patch series

Baseline: upstream SMAPI 4.5.2 at
`821167e5c511bf3a2d98f604e5e838561c469219`.

Android compatibility lineage: SMAPI-Android-1.6 at
`6a34bbeb6e891536cdd948594094482ba0d8d264` (`4.3.2.5`).

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
6. `upstream-4.5.2/v1`
   - adopt upstream input, content, Mod metadata, config-menu, and malicious loose-file checks;
   - retain the Android host, main-thread, content, Mod-loading, logging, and idle-runtime patches;
   - avoid console color auto-detection when the platform has no color-capable console;
   - express newer property backing fields using syntax supported by the pinned .NET 9 toolchain.

This file records patch intent. Git history remains the authoritative source diff.
