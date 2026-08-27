# Combat Build / Progression Cutover — Phase 7 Evidence

Date: 2026-08-27

Result: **PASS — Phase 7 is complete. Phase 8 was not started.**

Authority: `docs/combat-build-progression-cutover-plan-2026-08-26.md`

## Boundary and data disposition

Phase 7 removed the destructive legacy schema and every remaining production
consumer, regenerated bindings, published the final local modules, and ran the
plan's negative/positive ownership audits. It did not add a new progression
feature or begin Phase 8.

The pre-reset local Hub contained 64 identities with one legacy current-state
record each. These were not 64 disciplines, favorite builds, or a history of
one player's sessions. A read-only conversion report classified all 64 as
convertible and found zero failures. The user then explicitly approved
discarding the local data. Consequently:

- `arena-hub-local` was reset with `HUB_DELETE_DATA=always`;
- none of the 64 records was imported;
- the temporary offline converter was deleted before final publication;
- the ignored source snapshot and conversion report were deleted after the
  reset decision was complete; and
- no history, favorite-loadout, migration, compatibility, or dual-write table
  is shipped.

“Combat build” remains the internal aggregate name for one player's mutable
current combat configuration. Saving replaces that aggregate atomically. Its
revision is a stale-write token, not history. A removed discipline's one
configuration is retained as dormant current state for a less annoying
remove/re-add experience; it contributes no budget, action, or passive while
dormant. Match-ticket snapshots are temporary consistency copies and are
deleted with their tickets.

## Final authoritative schema

### Persistent Hub current state

- `combat_build`
- `combat_build_discipline`
- `discipline_configuration`
- `staff_school_selection`
- `discipline_action_bar_assignment`
- `discipline_passive_selection`
- `hub_player_armor_selection` (intentionally independent armor state)

`my_combat_build` and `my_hub_armor_selection` expose only the authenticated
caller's current state. `save_combat_build` is the only player-facing durable
combat-build writer.

### Temporary ticket and disposable-match state

- Hub: `match_player_combat_build_snapshot`
- Match: `match_combat_build`
- Match: `match_combat_build_discipline`
- Match: `match_discipline_configuration`
- Match: `match_staff_school_selection`
- Match: `match_discipline_action_bar_assignment`
- Match: `match_discipline_passive_selection`
- Runtime projection: `active_combat_build_discipline`

The provisioner transports one versioned JSON snapshot without reinterpreting
it. The disposable module parses and revalidates the same shared contract.

## Removed production authority

The final schema and regenerated C# bindings contain none of the following:

- primary/secondary discipline fields, limits, or defaults;
- fixed-slot Hub or match loadout tables;
- an undifferentiated selected-ability list;
- school rows in a discipline catalog or Staff-to-Arcana fallback mappings;
- a public independently selected combat-profile catalog/state;
- a global primary weapon pair or match-side durable loadout writer;
- profile-neutral replication across action bars;
- learned-spell, spellbook-content, or item-spell cast/bar authorization;
- positional loadout bootstrap parameters;
- the old Unity loadout DTO/writer, action-bar writer, or stat allocator;
- legacy spellbook/catalog panels that read removed tables; or
- temporary conversion/import code.

The generic empty spellbook equipment/item shell is retained as
non-authorizing future item infrastructure. It cannot grant a cast, active
slot, passive, discipline, or Staff school.

## Positive ownership audit

1. `hub-server/src/lib.rs::save_combat_build` is the sole player-facing Hub
   write reducer. It performs the revision check, calls
   `CombatBuildCatalog::validate_draft`, and only then calls
   `replace_combat_build`.
2. Hub initialization also uses the same validator for the deterministic
   starter state; it is not a second client write contract.
3. `freeze_player_combat_build_for_ticket` reloads and revalidates the saved
   revision through `validated_combat_build_for_owner` before serializing one
   versioned snapshot.
4. The provisioner transports `combat_build_snapshot_json` unchanged; match
   bootstrap parses, validates, and materializes the six canonical match
   tables.
5. Spells, melee abilities, and auto-attack replacements enter through
   `active_selectable_ability_for_authored_action` or
   `active_selectable_ability_for_ability_id`, which use the exact frozen
   current-discipline assignment predicate.
6. Player passive call sites use
   `player_has_selected_passive_ability`; the Phase 4 passive inventory remains
   exhaustive.
7. `activate_frozen_combat_discipline` verifies selection, applies the same
   frozen discipline's weapon configuration, and updates the active bar/mode
   projection in one transaction.
8. Staff school ownership is catalog-validated, save-validated, serialized in
   the ticket snapshot, revalidated at match bootstrap, and checked again by
   runtime active authorization.
9. Budget, slot, discipline, school, weapon, and selection rules originate in
   `progression_catalog.shared.json#combat_build_contract` and the one shared
   validator in `server/src/combat_build.rs`.
10. Hub, match, and main Unity bindings were regenerated from the final WASM
    schemas by `ops/setup-local-multiplayer.sh setup`; none was hand-authored.
    Orphan metadata for deleted generated files was removed, and every new
    canonical generated file has a unique Unity GUID.

Representative ownership commands:

```text
rg -n 'pub fn save_combat_build|validate_draft\(|replace_combat_build\(' hub-server/src/lib.rs
rg -n 'freeze_player_combat_build_for_ticket|validated_combat_build_for_owner|combat_build_snapshot_json' hub-server/src/lib.rs match_provisioner/worker.py server/src/match_contract.rs
rg -n 'frozen_active_ability_for_request|active_selectable_ability_for_|player_has_selected_passive_ability|activate_frozen_combat_discipline' server/src
```

## Negative source audit and allowlist

The final production search covered `server`, `hub-server`, `match-server`,
`match_provisioner`, `ops`, and current Unity runtime/editor/test source. It
returned zero hits for the removed fields, reducers, tables, mappings,
generated DTOs, and authorization symbols listed in §12.1 of the plan.

Representative command:

```text
rg -n --hidden --glob '!**/target/**' --glob '!**/Library/**' --glob '!**/*.meta' \
  '(primary_discipline_id|secondary_discipline_id_1|secondary_discipline_id_2|selected_ability_ids|SPELL_SCHOOL|combat_discipline_for_profile|combat_profile_for_discipline|player_knows_spell|PlayerKnownSpell|learn_spell|SaveHubDisciplineLoadout|SaveCharacterDisciplineLoadout|CharacterDisciplineLoadout|CharacterActionBarAssignment|CombatProfileCatalog|MatchPlayerLoadoutSnapshot|HubPlayerLoadout|MyHubLoadout)' \
  server hub-server match-server match_provisioner ops Assets/Arena/Runtime Assets/Arena/Editor Assets/Arena/Tests
```

The current-document search had one retained exact schema-name hit:
`docs/netcode-sync-audit-2026-07-02.md` records `ItemSpell` in a dated 2026-07
table inventory. It is historical evidence, not current authority. The only
whole-word legacy-discipline search hit in production was `ZEAL` as the current
resource kind in `progression_catalog.shared.json`; it is not a discipline ID.

Other permitted classes are documented in
`docs/combat-build-documentation-conflict-audit-2026-08-26.md`: the plan,
ledger/evidence, explicitly archived designs/prototypes, immutable legacy words
inside ability IDs, generic non-authorizing item/art terminology, and private
runtime “profile” helpers purely derived from canonical discipline state.

There are zero unclassified or authority-bearing allowlist hits.

## Required behavior scenarios

| Scenario | Evidence |
|---|---|
| Three weapon bars | Final live probe saved Daggers, Bow, and Staff, switched through all three, and returned to Daggers with exact equipment and assignments. |
| Mixed Staff schools | The same Staff configuration carried Ruin + Arcana abilities on one Staff bar and one Staff weapon. |
| School rejection | Live `WRONG_STAFF_SCHOOL` denial passed; contract fixtures reject damage types and unselected schools. |
| Combined/active budgets | Shared fixtures pass `16+4` and `15+5`, reject 17 active, and reject 21 combined. |
| Per-discipline minimum | Fixtures accept one active or one passive and reject a selected empty discipline. |
| Passive independence | The selected Staff passive remained materialized while Daggers and Bow were equipped; dormant selections fail closed. |
| Dormant preservation | Shared-validator dormant fixtures pass, invalid dormant references fail, and the Phase 6 editor behavior test restores Staff schools/weapon/active/passive state after remove/re-add. The Phase 7 test assembly compiles against final bindings. |
| Start behavior | Default benchmark starts slot 0 (`DAGGERS`); fixtures prove a valid explicit starting discipline overrides slot 0. |
| Freeze isolation | Ticket freeze uses a revalidated saved revision; match bootstrap revalidates and stores that immutable revision. Prior Phase 3 freeze-isolation tests remain green in the full server/Hub suites. |
| Authorization denial | Live reasons were `DORMANT_DISCIPLINE`, `UNASSIGNED`, `WRONG_ACTION_BAR`, and `WRONG_STAFF_SCHOOL`; removed learned/spellbook schemas were reported `ABSENT`. Assigned `MANA_SHIELD` succeeded. |
| Weapon validation | Contract fixtures and Hub tests reject cross-discipline weapons and illegal hand pairs; live switching restored Daggers/Bow/Staff weapons. |
| Catalog removal | Mutation tests reject unknown dormant ability references without pruning, replacement, or fallback. |

## Local publication and preservation

Approved destructive publication:

```text
HUB_DELETE_DATA=always ops/setup-local-multiplayer.sh setup
```

The command reset only local `arena-hub-local`, published the final Hub schema,
regenerated Hub/match bindings, rebuilt both disposable artifacts, reclaimed no
live replicas, and restarted the managed provisioner.

After fresh canonical probe rows existed, a normal data-preserving setup was
wrapped by before/after SQL snapshots of all eight current-state tables. Sorted
rows were identical:

| Table | Preserved rows |
|---|---:|
| `hub_player` | 3 |
| `hub_player_armor_selection` | 3 |
| `combat_build` | 3 |
| `combat_build_discipline` | 5 |
| `discipline_configuration` | 5 |
| `staff_school_selection` | 2 |
| `discipline_action_bar_assignment` | 6 |
| `discipline_passive_selection` | 1 |

Result: `preserved=true`, `changed_tables=[]`.

The spell-pipeline skill's old `hub_loadout_guard.py` intentionally failed
closed because it queries the now-deleted `hub_player_loadout` table. It was not
changed to preserve a compatibility schema. The canonical eight-table
comparison above replaces that obsolete guard for this cutover.

## Verification results

| Command/gate | Result |
|---|---|
| `cargo test --manifest-path server/Cargo.toml` | PASS — 786 passed, 0 failed |
| `cargo test --manifest-path hub-server/Cargo.toml --lib` | PASS — 24 passed, 0 failed |
| `python3 -m unittest match_provisioner.test_worker ops.test_benchmark_local_match_start` | PASS — 28 passed |
| Python compilation for changed provisioner/ops files | PASS |
| `python3 ops/generate-weapon-appearance-catalog.py --check` | PASS |
| `ops/test-setup-local-multiplayer.sh` | PASS |
| `ops/dungeon-compile-gate.sh` | PASS — 0 errors in `Assembly-CSharp`, `Assembly-CSharp-Editor`, and `Arena.EditModeTests` |
| `git diff --check` | PASS |
| `ops/setup-local-multiplayer.sh status` | PASS — local DB ready, both artifacts ready, managed provisioner running |
| `python3 ops/test-combat-build-runtime.py` | PASS — three disciplines, 4 actives, 1 passive, 2 Staff schools, expected denials, positive cast, `CLEANED` |
| `python3 ops/benchmark-local-match-start.py --samples 1` | PASS — 36-query initial state, request-to-initial-state 948.507 ms, `1/1 CLEANED` |

`cargo fmt --manifest-path hub-server/Cargo.toml -- --check` passes. The
repository-wide server formatting check still reports two pre-existing
format-only diffs in untouched `server/src/combat/projectiles.rs` and
`server/src/spells/catalog.rs`; Phase 7's touched Rust files were formatted,
and the unrelated files were left unchanged.

One attempted pair of compile gates was accidentally run concurrently. Their
shared temporary project filename caused an `MSB1009` missing-project failure;
it was a harness race, not a compiler error. A subsequent isolated non-batch
run passed all three assemblies and is the recorded result above.

Unity batch mode was not authorized and was not used. The relevant EditMode
test assembly compiled against the regenerated schemas; the Unity test runner
was not launched in Phase 7. Previously executed focused behavior results are
retained in the Phase 5/6 evidence and the server-authoritative/live gates were
rerun here.

## Artifact evidence

| Artifact | Bytes | Source fingerprint | WASM SHA-256 |
|---|---:|---|---|
| Optimized disposable PvP match | 3,331,094 / 3,500,000 cap | `7f3f2ef2454d8d6f98c5ddc7634a6736325e346f5bc7fea21946d8e41b85378a` | `b4219e8b4d96a0e36d23dd111fc9be1efc56f699d79ba9515b32116306ad8674` |
| Optimized disposable open world | 125,859,869 | `97d9ea3207f83b5925208d4865b13a500cd6291ea045aedf55d1f35a4d90e69f` | `0f537eb009fb42d12d3dbe0b57637f6f9e8a1e6b16d55b512ceb0f702ff157d7` |

The final live assignments reported match build ID
`sha256-b4219e8b4d96a0e36d23`, matching the optimized PvP artifact.

## Ledger closure

`docs/combat-build-legacy-consumer-inventory-2026-08-26.json` now reports:

- `closed`: 25
- `archived`: 1 (`CBL-026`, explicitly superseded documentation)
- `not_present`: 1 (`CBL-027`, prohibited temporary/dual authority)
- unresolved/open: 0

The Phase 7 exit gate is satisfied. This evidence authorizes no Phase 8 work.
