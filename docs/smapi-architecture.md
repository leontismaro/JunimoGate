# SMAPI architecture

JunimoGate builds its Android SMAPI fork from the `smapi/` Git submodule and references it as source projects. It does not invoke `Program.Main`, use SMAPILoader, or locate a separate desktop installation.

## Host contract

`JunimoGate.GameHost` creates `SmapiRuntime` and `SmapiSession` with host-owned values for:

- the current Android Activity;
- game assembly and Content roots;
- selected Mod roots;
- internal data, config, log, save, and backup roots;
- main-thread dispatch;
- managed assembly loading;
- view attachment;
- structured launch outcomes.

The Activity receives only an app-private session key. Paths are produced by JunimoGate and cannot be supplied by an external Intent.

## Assembly identity

Game, SMAPI, dependencies, and Mods are loaded through the Default `AssemblyLoadContext`. The loader rejects duplicate game/framework/host identities, path escape, and ambiguous candidates. This keeps the types used by GameHost, SMAPI, Harmony, and Mods identical within the process.

## Android adaptation

Android-specific behavior is kept behind bounded host services and patch points:

- Activity and lifecycle access;
- view attachment;
- app-private storage;
- Android main-thread dispatch;
- background task tracking;
- save serializer registration;
- managed dependency binding;
- product outcome reporting.

The fork does not select behavior by individual Mod ID. A Mod-specific defect is handled in the Mod or through a general runtime contract.

## Process boundary

SMAPI, Harmony, the game, and Mods run in `:game`. Ending that process releases their static state. The launcher process does not load game or Mod types and can recover or prepare runtime state independently.

## Update boundary

Game workspace identity, rewrite identity, SMAPI bundle identity, Mod selection identity, and persisted-data schemas are versioned separately. Updating the SMAPI bundle must not force extraction or rewrite of unchanged game inputs.
