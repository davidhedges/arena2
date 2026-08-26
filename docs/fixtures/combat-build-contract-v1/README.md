# Combat-build contract fixtures v1

Status: **Frozen in Phase 0 and executed by the Phase 1 production pure
validator in `server/src/combat_build.rs`.**

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

The error-code strings are the stable production codes. All 29 cases execute
through `CombatBuildCatalog::validate_draft`; Hub save, snapshot freeze, match
bootstrap, and runtime defense-in-depth tests must reuse that validator and
these fixtures rather than copying their rules.

The fixtures remain data only. Adding a second standalone semantic validator
here would create the parallel authority this migration is meant to eliminate.
