# Combat-build progression cutover — Phase 4 evidence

Date: 2026-08-26

Status: **COMPLETE — exact frozen action bars, atomic discipline/weapon
switching, build-only player active authorization, and selected-passive
authorization pass the Phase 4 exit gate. Phase 5 was not started.**

## 1. Approved boundary

This slice implemented only Phase 4 of
`docs/combat-build-progression-cutover-plan-2026-08-26.md`:

- resolve exact action assignments on each selected weapon discipline's bar;
- atomically switch the frozen discipline, materialized weapon, combat mode,
  and bar projection;
- make the frozen combat build the sole provisioned-player active and passive
  authorization boundary;
- remove learned-spell, spellbook, profile-neutral-copy, and
  school-as-discipline runtime alternatives;
- retain current passive mechanics while changing only their eligibility
  authority; and
- prove the cutover through exhaustive tests, instrumented denials, and a live
  disposable match.

This phase did not change the current Unity UI or build a replacement screen.
It also did not physically remove local-direct compatibility schema, current
Unity consumers, generated compatibility surfaces, or Hub tombstones assigned
to Phases 5 and 7.

## 2. One runtime active authority

For a provisioned player, `set_combat_discipline` now delegates to
`activate_frozen_combat_discipline`. That path:

1. requires a frozen `match_combat_build`;
2. requires the requested canonical discipline in
   `match_combat_build_discipline`;
3. resolves the same discipline's `match_discipline_configuration`;
4. validates and equips its materialized weapon instances and authored colors;
5. writes `ActiveCombatDiscipline` with the same canonical ID as both the
   discipline and compatibility profile projection;
6. clears discipline-specific transient state and synchronizes combat mode;
   and
7. projects the complete set of exact frozen bars.

The projection removes non-global legacy rows and stale global discipline
switches, then writes only:

- `GLOBAL / DISCIPLINE_0..2` switch actions in selected order; and
- each `match_discipline_action_bar_assignment` under its exact owning
  canonical discipline.

It never copies a profile-neutral ability to multiple frozen bars. In-match
action-bar edit/clear reducers and the independent weapon-loadout reducer reject
frozen players before mutation.

All player melee, auto-attack, movement, mode-toggle, and spell entry points
converge on `active_selectable_ability_for_authored_action` or
`active_selectable_ability_for_ability_id`. For frozen players those functions
use only `frozen_active_ability_for_request`, which requires:

- an active discipline selected by the frozen build;
- an exact assignment on that current discipline;
- canonical player `ACTIVE` metadata owned by that discipline; and
- for Staff, the ability's exact school in `match_staff_school_selection`.

A non-dummy player without the required frozen build fails closed. The
remaining non-frozen branch serves explicit local-direct compatibility and NPC
actors; it cannot be reached as a fallback by a provisioned player.

Denials emit `[COMBAT_BUILD_AUTH]` with one stable reason:
`NO_FROZEN_BUILD`, `NO_ACTIVE_DISCIPLINE`,
`ACTIVE_DISCIPLINE_NOT_SELECTED`, `DORMANT_DISCIPLINE`,
`WRONG_ACTION_BAR`, `WRONG_STAFF_SCHOOL`, `UNASSIGNED`, or
`INVALID_CATALOG_METADATA`.

## 3. One runtime passive authority

`player_has_selected_passive_ability` is the sole provisioned-player passive
predicate. It requires an explicit `match_discipline_passive_selection`, the
owning discipline in the frozen selected set, valid canonical player
`PASSIVE` metadata, and the selected Staff school where applicable. It does
not consult current equipment, so a passive remains active through every
weapon swap while its owning discipline remains selected.

`PLAYER_PASSIVE_RUNTIME_INVENTORY` lists 24 IDs. A catalog test derives every
authored player `PASSIVE` and asserts exact set equality and equal cardinality
with that inventory. All runtime passive entry points in combat, movement,
spell, Lingering Shade, and related progression helpers call the central
predicate. The separate `player_build_contains_active_ability` helper cannot
authorize an invocation; it exists only to reconcile persistent state created
by an earlier authorized active cast.

### Restless

`WARRIOR_RESTLESS` is now explicit canonical metadata:

- actor scope `PLAYER`;
- selection kind `PASSIVE`;
- combat discipline `TWO_HANDED_SWORD`;
- legacy family/action metadata `WAR` / `RESTLESS`; and
- resource kind `STAMINA`.

Only eligibility changed. The existing runtime mechanics remain:

- direct-damage amplification stack group `WARRIOR_RESTLESS`;
- maximum 50 stacks;
- four-second initial/reset delay;
- one stack per second in combat;
- one decay step every 500 ms out of combat;
- direct damage consumes 10 stacks and resets the delay; and
- 2% direct-damage amplification per stack.

Regression tests lock the catalog row, presentation, passive inventory, and
all of those tuning values.

## 4. Removed alternate authorization paths

The server no longer defines or calls:

- `player_knows_spell`;
- `equipped_spellbook_contains_spell`;
- `equipment_spell_slot_capacity_for_owner`;
- `spell_cast_is_authorized_by_action_bar_or_spellbook`;
- `discipline_uses_staff`;
- `character_has_selected_discipline`; or
- `opportunist_passive_is_active_for_profile`.

`ability_id_for_spell` resolves an exact current frozen assignment or an
explicit NPC spell ability and otherwise returns no ability. `cast_request`
has no learned/spellbook OR branch.

`PlayerKnownSpell`, `learn_spell`, `ItemSpell`, and
`assign_equipped_spellbook_spell` remain collection/schema surfaces. The live
probe writes both kinds of rows and proves that learned-only `NOVA` and
spellbook-only `BOLT` are still denied. Their Unity consumers and eventual
physical schema disposition remain ledgered for Phases 5 and 7 rather than
being silently treated as complete.

The inventory/weapon runtime recognizes only `DAGGERS`,
`TWO_HANDED_SWORD`, `SWORD_AND_SHIELD`, `ARCHER_BOW`, and `STAFF` for canonical
discipline weapon legality and starter lookup. Staff schools remain separate
frozen configuration rows and never create another Staff, action bar, or
discipline slot.

## 5. Live three-bar and authorization proof

`ops/test-combat-build-runtime.py` uses one fresh anonymous identity and the
canonical local Hub/provisioner. It saved this build:

- Daggers: `TRAINING_DAGGER_PAIR`, `DAGGER_QUICK_CUT` in `slot_0_0`;
- Bow: `TRAINING_BOW`, `ARCHER_POWER_SHOT` in `slot_0_0`;
- Staff: `NEWBIE_STAFF_01`, schools `RUIN` + `ARCANA`, `SPELL_FIREBALL` in
  `slot_0_0`, `SPELL_MANA_SHIELD` in `slot_0_1`, and selected passive
  `RUIN_FLAMING_WEAPON`.

The build contains five total abilities and four actives. The probe observed
the exact switch/equipment sequence:

```text
DAGGERS     -> TRAINING_DAGGER_PAIR
ARCHER_BOW  -> TRAINING_BOW
STAFF       -> NEWBIE_STAFF_01
DAGGERS     -> TRAINING_DAGGER_PAIR
```

At every step all four exact assignments remained only on their owning bars,
the one Staff passive remained selected even while Bow or Daggers was active,
and the three global switch rows retained selected order. The provisioned
identity had zero `CharacterDisciplineLoadout` and zero
`CharacterDisciplineAbilitySelection` rows.

The probe additionally proved:

- action-bar assign, action-bar clear, and independent weapon assignment all
  fail against the frozen build;
- `MANA_SHIELD` fails on Bow with `WRONG_ACTION_BAR`;
- unassigned Arcana `NOVA` fails with `UNASSIGNED`;
- Primal `GIGANTISM` fails with `WRONG_STAFF_SCHOOL`;
- unselected Two-Handed `FRENZY` fails with `DORMANT_DISCIPLINE`;
- learning `NOVA` does not change its denial;
- putting `BOLT` in the equipped spellbook does not authorize it;
- assigned current-Staff `MANA_SHIELD` commits and creates its authoritative
  status; and
- the exact ticket allocation reaches `CLEANED`.

Passing result:

```json
{
  "event": "combat_build_runtime_phase_4_pass",
  "selected_disciplines": ["DAGGERS", "ARCHER_BOW", "STAFF"],
  "staff_schools": ["ARCANA", "RUIN"],
  "authorization_log_reasons": [
    "DORMANT_DISCIPLINE",
    "UNASSIGNED",
    "WRONG_ACTION_BAR",
    "WRONG_STAFF_SCHOOL"
  ],
  "positive_cast": "MANA_SHIELD",
  "collection_only_checks": ["learned:NOVA", "spellbook:BOLT"],
  "cleanup": "CLEANED"
}
```

## 6. Required-scenario coverage

The twelve scenarios in plan §11 are covered cumulatively by the approved
phase gates:

- Phase 1 contract tests cover mixed-school validation, damage-type rejection,
  `16 + 4` and `15 + 5` acceptance, 17-active and 21-total rejection,
  per-discipline minimums, future-default validation, illegal weapon pairs,
  and invalid dormant catalog references.
- Phase 2 reducer/live tests cover dormant remove/save/reload/re-add
  preservation and atomic invalid-draft rollback.
- Phase 3 tests/live handoff cover exact immutable snapshot isolation,
  deterministic first-selected startup, and materialized legal weapons.
- This Phase 4 live probe covers three exact weapon bars, one mixed-school
  Staff bar, passive selection across every swap, all six specified
  authorization denials, assigned-current-bar success, weapon restoration,
  and frozen in-match mutation rejection.

## 7. Legacy-consumer audit and classified residue

`docs/combat-build-legacy-consumer-inventory-2026-08-26.json` now contains a
Phase 4 checkpoint and specific resolutions for `CBL-003`, `CBL-005`,
`CBL-006`, `CBL-007`, `CBL-011`, `CBL-012`, `CBL-013`, `CBL-020`,
`CBL-021`, and `CBL-022`.

Negative server audit:

```text
rg '\bplayer_knows_spell\b|\bequipped_spellbook_contains_spell\b|\
\bspell_cast_is_authorized_by_action_bar_or_spellbook\b|\
\bequipment_spell_slot_capacity_for_owner\b|\bdiscipline_uses_staff\b|\
\bopportunist_passive_is_active_for_profile\b|\bcharacter_has_selected_discipline\b' \
  server/src -g'*.rs'
PASS — no matches
```

Positive ownership audit:

- every player action entry point calls one of the two central active
  resolvers;
- both central active resolvers fail closed into
  `frozen_active_ability_for_request` for provisioned players;
- all 24 authored player passives equal the runtime passive inventory;
- the frozen switch path and match startup both call
  `activate_frozen_combat_discipline`;
- Staff school membership is checked by the shared validator, match
  materialization, and `frozen_ability_metadata_is_valid`; and
- the local publish regenerated bindings with no source diff.

Deliberate residue remains open, not hidden or claimed finished:

- `CharacterDisciplineLoadout`, `CharacterDisciplineAbilitySelection`, and
  their helper path serve local-direct compatibility only;
- `PlayerKnownSpell` and `ItemSpell` remain collection/schema-only;
- current Unity profile/global/spellbook resolution is assigned to Phase 5;
- generated compatibility types and data-preserving schema tombstones are
  assigned to Phase 7; and
- NPC spell resolution is an explicit non-player actor path.

Therefore Phase 4 has no competing provisioned-player runtime authorization
path. The entire migration cannot be called complete until the already-logged
Phase 5 and destructive Phase 7 exits pass.

## 8. Verification evidence

Final commands and results:

```text
cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast
PASS — 836 passed, 0 failed

cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast
PASS — 27 passed, 0 failed

python3 -m unittest match_provisioner.test_worker \
  ops.test_benchmark_local_match_start
PASS — 28 passed, 0 failed

ops/dungeon-compile-gate.sh
PASS — Assembly-CSharp, Assembly-CSharp-Editor, and Arena.EditModeTests
compiled with 0 errors

python3 ops/test-combat-build-runtime.py
PASS — exact bars/switches, frozen mutation rejection, four instrumented
denial classes, learned/spellbook negative checks, Mana Shield positive cast,
and exact allocation cleanup

python3 <arena-spell-pipeline>/scripts/hub_loadout_guard.py verify ...
PASS — 55 pre-existing Hub loadout rows unchanged; 5 new diagnostic rows
allowed

ops/setup-local-multiplayer.sh status
PASS — SpacetimeDB, PvP artifact, open-world artifact, and managed provisioner
ready
```

The local data-preserving publish rebuilt the generated bindings without a
tracked binding diff. Final artifact evidence:

- PvP WASM size guard: `3,490,575 / 3,500,000` bytes;
- PvP source provenance:
  `4ba3221c397b20bcab60bc42f08299b639e74c1427fe74d2fad3daf2dce41e3b`;
- open-world source provenance:
  `a831eadeea3f8f4fdfe03ccc5ff06b9ca1a73aaf7795b9f4d319322d7ba9ba0c`; and
- open-world artifact size: `126,167,870` bytes.

Unity was closed by the owner. No Unity batch-mode run was performed. The
generated C# contract was checked through the repository's non-Unity compile
gate; interactive Unity presentation was outside this no-UI phase.

## 9. Exit decision

Phase 4 exit gate: **PASS**.

Phase 5 was not started. Its Unity plumbing work requires separate explicit
authorization.
