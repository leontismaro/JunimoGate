# Startup chain

JunimoGate uses one APK with a launcher process and an isolated `:game` process.

## Launcher open

```text
MainActivity
  -> LauncherCoordinator
  -> read the active snapshot once
  -> validate the snapshot envelope
  -> show ready, preparation required, unsupported, or failed state
```

Opening the launcher does not scan PackageManager, hash APKs, enumerate game files, run Cecil, or start Deep Prepare.

## Launch action

```text
user action
  -> read one PackageManager snapshot
  -> compare the package update marker
  -> reuse the prepared handle, or run one Deep Prepare transaction
  -> freeze the selected Mod group and revisions
  -> write one app-private launch descriptor
  -> pass only a random session key to SmapiGameActivity
```

The descriptor contains controlled app-private paths and immutable selection data. External callers cannot provide workspace, assembly, Content, Mod, log, or save paths.

## Game process

```text
SmapiGameActivity (:game)
  -> atomically consume the launch descriptor
  -> read the prepared snapshot once
  -> build one runtime file inventory
  -> register Default AssemblyLoadContext resolution
  -> install GameHostBridge and SmapiContentBridge
  -> create SmapiRuntime and SmapiSession
  -> start SCore, SGameRunner, and SGame
  -> resolve the selected Mod roots
  -> load accepted Mod assemblies
  -> attach the MonoGame view
```

The runtime inventory covers required managed assemblies, the SMAPI bundle, and Content file metadata. Later assembly loading and Content access reuse it without additional JunimoGate size, existence, or hash checks.

## Failure and recovery

Before the session confirmation checkpoint, a request or bundle failure triggers a bounded local repair. If that fails, JunimoGate removes only rebuildable runtime state, performs one Deep Prepare, and retries once. A second failure stops and records the result.

Recovery and cache cleanup do not delete the Mod library, groups, settings, logs, backups, or saves. A previous game workspace is removed only after the replacement session is confirmed and no game process is using it.

## Process lifecycle

Home leaves the `:game` process available for foreground routing. Opening the launcher while that process is active brings the existing `SmapiGameActivity` forward. Back is converted into one game `Escape` input after SMAPI is ready; before readiness it backgrounds the task.

Launcher and game processes write separate bounded JSONL product logs. The UI can display and share selected current log files through an app `FileProvider`. Complete one-shot descriptor tokens are never logged.

## Storage

```text
app-private no_backup/junimogate/
  runtime/       rebuildable snapshots, workspaces, bundles, and caches
  user-data/     Mod library, groups, settings, logs, and backups

app external files/
  Saves/         game save directory supplied to SMAPI
```

APK replacement retains application data. Android removes app-private and app-specific external data when the user clears application data or uninstalls the application.
