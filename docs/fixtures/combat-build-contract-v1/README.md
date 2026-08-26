# Combat-build contract fixtures v1

Status: **Phase 0 target fixtures; no production schema or validator is
implemented here.**

`cases.json` freezes the product rules that Phase 1's canonical catalog and
pure validator must implement. It intentionally uses the target contract
vocabulary instead of the current primary/secondary tables.

The fixture set covers:

- one to three equal canonical weapon disciplines;
- first-selected startup and the reserved explicit starting-discipline field;
- exact per-discipline active slots and selected passives;
- one combined 20-ability budget and the independent 16-active maximum;
- one active or passive minimum for every selected discipline;
- Staff as one discipline with one bar and one weapon while drawing from one to
  three of the six consolidated schools;
- damage-type rejection at the Staff-school boundary;
- dormant configuration exclusion from active counts;
- duplicate, kind, ownership, slot, weapon, and unknown-reference failures.

The error-code strings are the target stable codes for Phase 1. Phase 1 must
execute every case through the production pure validator; Hub save, snapshot
freeze, match bootstrap, and runtime defense-in-depth tests must reuse the same
fixtures rather than copying their rules.

Phase 0 verification is intentionally limited to JSON parsing, fixture-shape
checks, declared count consistency, and catalog-reference review. Adding a
second standalone semantic validator here would create the parallel authority
this migration is meant to eliminate.
