# Mod assembly binding

SMAPI reads manifests, orders Mod dependencies, and discovers the managed assemblies referenced from each selected `EntryDll`. JunimoGate supplies controlled roots, rewrite caching, and physical loading through the Default `AssemblyLoadContext`.

## Policies

- `Strict` rejects different-content candidates with the same simple name for an affected consumer.
- `FirstLoaded` preserves the first accepted identity for the process.
- `HighestCompatible` selects the highest assembly version and then checks the consumer-facing type/member surface.

`HighestCompatible` considers only files shipped in the selected Mod roots. It does not download packages, search unrelated directories, or fall back to a lower candidate after the chosen version fails compatibility checks.

## Selection boundary

The launcher freezes an immutable Mod selection before issuing the launch descriptor. SMAPI receives the exact selected roots; disabled library items and unselected groups are not scanned.

The loader rejects path escape, host/framework/game shadowing, duplicate physical identity, and tied same-version candidates with different content. A rejection is attributed to the affected Mod and recorded in SMAPI logs.
