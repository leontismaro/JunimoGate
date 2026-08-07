# Android save serializer bridge

The Android game exposes a serializer cache that differs from the desktop members expected by some SMAPI Mods. JunimoGate's SMAPI fork provides a bounded facade for the missing serializer slots.

The bridge:

- locates the unique static cache keyed by `Type` with `XmlSerializer` values;
- validates the expected cache shape before registration;
- registers the required root serializers once;
- preserves the game's existing `GetSerializer(Type)` path;
- uses initialization synchronization instead of hooks on save access;
- stops with a focused compatibility error when the cache shape changes.

This bridge addresses only serializer member availability. It does not synthesize portable PDB data or patch unrelated initialization logic in individual Mods.
