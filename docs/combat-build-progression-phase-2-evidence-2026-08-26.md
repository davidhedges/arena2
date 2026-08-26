# Combat-build progression cutover — Phase 2 evidence

Date: 2026-08-26

Status: **COMPLETE — the persistent Hub combat-build aggregate and its sole
atomic writer pass the Phase 2 exit gate. Phase 3 was not started.**

## 1. Approved boundary

The owner approved Phase 2 of
`docs/combat-build-progression-cutover-plan-2026-08-26.md`. This slice was
limited to:

- canonical Hub build/configuration persistence;
- one revision-checked atomic `save_combat_build` reducer using the exact
  Phase 1 validator;
- deterministic canonical defaults;
- per-discipline weapons, exact action bars, selected passives, Staff schools,
  and dormant configuration retention;
- a caller-filtered read contract and regenerated Hub bindings; and
- removal of the legacy Hub discipline and weapon writers in the same release.

It did not change the match snapshot, provisioner transport, match runtime,
cast/passive authorization, current Disciplines UI, migration/reset/import
workflow, or any Phase 3-and-later surface.

## 2. Canonical Hub authority

The private durable aggregate is stored in these tables:

- `combat_build`;
- `combat_build_discipline`;
- `discipline_configuration`;
- `staff_school_selection`;
- `discipline_action_bar_assignment`; and
- `discipline_passive_selection`.

`save_combat_build` is the only combat-build reducer. It accepts one typed
nested draft, reads the caller's current revision, invokes
`CombatBuildCatalog::validate_draft` from the exact
`server/src/combat_build.rs` source, and replaces the aggregate only after the
whole draft validates. SpacetimeDB reducer transactions supply rollback on an
error. The stored revision advances once per successful replacement.

There is no duplicated Hub validation policy. The Hub source includes the
Phase 1 module directly, and the generated save DTOs only adapt the wire shape
to that contract.

The sole public canonical reader is the caller-filtered `my_combat_build` view.
It returns the complete nested aggregate in deterministic order, including
selected disciplines and all active or dormant configurations. The underlying
tables are private; there is no public child-table subscription surface.

On first connection, the same production validator creates this deterministic
revision-1 default:

```text
selected discipline: DAGGERS in slot 0
weapon: TRAINING_DAGGER_PAIR
active: DAGGER_QUICK_CUT in slot_0_0
starting discipline: unset (therefore first-selected fallback)
```

## 3. Live behavioral proof

`ops/test-hub-combat-build.py` connected with a fresh anonymous identity and
passed every Phase 2 behavior against the persistent local Hub:

```json
{
  "checks": [
    "deterministic_default",
    "save_reload",
    "stale_revision_rejection",
    "invalid_draft_rollback",
    "dormant_remove_readd",
    "per_discipline_weapons"
  ],
  "dormant_disciplines": ["ARCHER_BOW"],
  "event": "hub_combat_build_phase_2_pass",
  "identity": "c2009125860c",
  "revision": 4,
  "selected_disciplines": ["DAGGERS", "STAFF"],
  "staff_schools": ["ARCANA", "RUIN"]
}
```

This proves that Staff occupies one selected discipline while Arcana and Ruin
configure its one bar, per-discipline weapons survive round trips, a removed
Bow configuration remains dormant and is restored on re-add, stale revisions
are rejected, and a cross-discipline weapon draft rolls back without changing
the saved aggregate.

The one-sample local match-start benchmark also passed after the Hub schema
publish. Phase 3 is intentionally unmodified, so this benchmark still freezes
and transports the positional compatibility loadout:

```text
request to ready: 605.989 ms
request to initial state: 818.588 ms
match build: sha256-2c0ef195e7ed8ae6a9b6
cleanup: 1/1
```

## 4. No competing or parallel Hub path

The following legacy writer symbols and generated bindings are removed:

- `save_hub_discipline_loadout` / `SaveHubDisciplineLoadout`;
- `save_hub_weapon_loadout` / `SaveHubWeaponLoadout`.

The current legacy UI-facing `HubNetworkManager` methods remain only so the
unmodified screen compiles. They fail closed with an explicit canonical-editor
message and cannot invoke a server write. Armor remains a separately scoped
setting through `save_hub_armor_set`.

The positional `HubPlayerLoadout` and `my_hub_loadout` reader are intentionally
retained as noncanonical compatibility staging for the existing Phase 3 ticket
snapshot and provisioner. Only legacy catalog reconciliation and the separately
scoped armor setting may maintain that row; no player combat-build writer
targets it. `save_combat_build` does not update it, so there is no dual-write,
conversion adapter, or claim that it reflects the canonical build. Its final
removal remains open as `CBL-002` and related entries in
`docs/combat-build-legacy-consumer-inventory-2026-08-26.json`.

The legacy display catalogs, primary/secondary runtime handoff, match
authorization, and current UI are likewise still open in that inventory for
their approved later phases. They are not writers or alternate readers of the
new Hub aggregate in Phase 2.

During local schema iteration, SpacetimeDB 2.1 failed to materialize a set of
custom multi-row caller views after child-row updates. The release shape was
therefore collapsed to the one nested `my_combat_build` aggregate view. The
daemon was restarted before validation, and all intermediate child-view source,
bindings, and Unity metadata were removed. This is one canonical read model,
not a compatibility branch.

## 5. Verification evidence

Final commands and results:

```text
cd server && cargo test --no-fail-fast
PASS — 832 passed, 0 failed

cd hub-server && cargo test --no-fail-fast
PASS — 27 passed, 0 failed

python3 -m unittest match_provisioner/test_worker.py \
  ops/test_benchmark_local_match_start.py
PASS — 25 passed, 0 failed

python3 ops/test-hub-combat-build.py
PASS — all six live Phase 2 checks

python3 ops/benchmark-local-match-start.py --samples 1
PASS — initial state reached; cleanup 1/1

ops/dungeon-compile-gate.sh
PASS — Assembly-CSharp, Assembly-CSharp-Editor, and Arena.EditModeTests
compiled with 0 errors
```

The required release flow used the canonical, data-preserving local setup.
Final status reported SpacetimeDB, the match artifact, the open-world artifact,
and the provisioner ready. Artifact provenance was:

- PvP match:
  `f3f93bab192aa8839c900507a04334825df849eb95d452453b3c2560e3c4963e`;
- open world:
  `4adc9c60c4977da90d965923de2bfc3fe68001119dd8e05058cbc961a418da70`.

The Hub loadout guard verified all 36 protected pre-existing rows unchanged;
15 new anonymous test/benchmark rows were allowed. No reset or import occurred.

Unity was closed by the owner. No Unity batch-mode run was authorized or
performed. The generated C# bindings pass the repository's normal compile
gate; interactive Unity presentation was neither changed nor claimed as
verified.

## 6. Exit decision

Phase 2 exit gate: **PASS**.

The next plan item is Phase 3, but it is not authorized by completion of this
phase and was not started.
