# Compatibility model

JunimoGate evaluates whether the installed inputs satisfy the structures required by the current extraction, rewrite, runtime, and SMAPI contracts. A version string or exact binary fingerprint is not sufficient by itself.

## Installed package

Discovery records the selected package, version metadata, signer identity, base/split APK set, ABI, AssemblyStore role, and Content role. Package paths and device identifiers are not written to public reports.

The current execution policy accepts only configured package and signer identities. `KnownTested` means that the signer matches a project fixture; it is not publisher certification or proof of account entitlement.

## Game bridge

The active recipe family is `stardew-android-mainactivity-bridge/v1`. Its analyzer requires:

- the target type, fields, methods, parameters, and return types;
- a unique local IL source/consumer pattern for each replacement;
- valid stack and type behavior;
- a mapped host bridge for every rewritten operation;
- successful assembly serialization and reopen;
- rewrite postconditions for the targeted methods and direct dependencies.

The analyzer does not use whole-method hashes, a fixed MVID, global call totals, or an unrelated native hash set as eligibility gates. Licensing callbacks are not rewrite targets.

## Game updates

PackageManager metadata is compared on each launch action. A changed package marker invalidates the active snapshot and starts one Deep Prepare transaction:

```text
package changed
  -> inventory and hash each APK once
  -> create a source workspace
  -> run semantic analysis
  -> apply and reopen the rewritten assembly
  -> atomically publish the new snapshot
  -> start the requested session
```

When the local semantic pattern cannot be classified uniquely, preparation stops with a structured incompatibility result. The previous confirmed snapshot is retained until a new session reaches its confirmation checkpoint.

## SMAPI and Mods

The Android SMAPI fork retains the `StardewModdingAPI` assembly identity, public API, manifest semantics, dependency ordering, lifecycle events, and content pipeline. Android-specific work is kept in host adapters and bounded patches.

Game, SMAPI, dependencies, and selected Mods share the Default `AssemblyLoadContext`. Mod assembly selection supports:

- `Strict`;
- `FirstLoaded`;
- `HighestCompatible`.

Selection is limited to the exact active group snapshot. Unselected Mod roots are not scanned. A rejected Mod does not terminate unrelated Mods; a failure of the overall loading pipeline ends the launch attempt with a structured result.

## Evidence

Files named `*-result.md` record dated engineering observations. Current policy comes from this document, the source code, and focused tests.
