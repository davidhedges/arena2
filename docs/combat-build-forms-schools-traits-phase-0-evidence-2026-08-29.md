# Combat Build v2 Phase 0 evidence

Date: 2026-08-29  
Status: PASS

## Scope and artifacts

Phase 0 locks the product decisions, complete player-feature classification,
initial Form catalog, validation-fixture inventory, animation compatibility
matrix, baseline health, and reset ledger. It does not change production Hub
schema or data, match/runtime behavior, UI, input assets, or animation assets.

The machine-checked source is
`ops/generate-combat-build-v2-phase0.py`. It generates
`docs/combat-build-v2-phase-0-contract-2026-08-29.json` from the current shared
progression catalog and fails closed if any selectable player ability is
unclassified, duplicated between Forms, or assigned an invalid structural
kind.

## Locked decisions

- Selected Specializations: 1–3 Forms/Schools; repeated parent Disciplines are
  legal.
- Combined Technique + Spell + Perk capacity: 18.
- Trait capacity: 3. Traits do not consume feature capacity.
- `MASTERY`: +10% normal player-authored outgoing damage while selected and
  the build has exactly one distinct parent Discipline.
- Selected Perks remain active while their source Specialization is selected,
  independent of the equipped weapon.
- Staff has no selectable or intrinsic Techniques and retains its ordinary
  autoattack.
- One global active-feature order supplies 18 stable direct input identities,
  `COMBAT_ACTION_00` through `COMBAT_ACTION_17`. The default keyboard bindings
  reuse the current unshifted combat keys: `1`–`0`, `E`, `R`, `T`, `F`, `G`,
  `Z`, `X`, and `C`.
- The always-visible Spell bar and current-parent Technique bar are projections
  of the global order. Neither bar introduces a separate cap.
- Dormant preferred-order collisions keep the active order and append/reflow
  returning features deterministically; they do not reject a save or discard
  a selection.

`server/src/combat.rs::resolve_damage_amount` is the existing central damage
resolution path. Phase 4 can apply `MASTERY` there so autoattacks, Techniques,
Spells, and owned delayed/projectile damage share one rule while the existing
self-authored/final/copied-damage bypasses remain non-rescaled.

## Initial specialization catalog

All six existing Staff Schools retain their stable IDs: `BLIGHT`, `MORTALITY`,
`RUIN`, `DIVINITY`, `ARCANA`, and `PRIMAL`.

| Parent Discipline | Form | Retained features |
| --- | --- | ---: |
| Daggers | Bladedancer | 12 |
| Daggers | Executioner | 11 |
| Daggers | Shadow | 11 |
| Two-Handed Sword | Vanguard | 12 |
| Two-Handed Sword | Reaver | 10 |
| Two-Handed Sword | Berserker | 11 |
| Sword and Shield | Guardian | 9 |
| Sword and Shield | Vindicator | 10 |
| Sword and Shield | Templar | 6 |
| Bow | Marksman | 4 |
| Bow | Skirmisher | 5 |
| Bow | Volley | 5 |

Every seeded Form is nonempty. The generated ledger classifies all 208 retained
selectable player features exactly once: 80 Techniques, 104 Spells, and all 24
current passives as Perks.

Notable semantic classifications:

- `DAGGER_DARKNESS`, `DAGGER_STALK`, and `DAGGER_SHADOWREND` move to the Shadow
  Form and remain Spells usable with any equipped weapon.
- Form-owned Paladin magic such as Consecrate, Cleansing Touch, auras, fonts,
  Blade Barrier, Radiant Burst, and Sacred Flame are Spells.
- `DAGGER_DISARM`, `PALADIN_BLESSED_SHIELD`, all Bow actives, and weapon-bound
  Warrior/Paladin/Dagger attacks remain Techniques even where their current
  executor uses the generic spell machinery. They require their parent weapon.
- A feature's existing `gameplay.kind` continues to choose its executor and
  presentation discovery. New `loadout_kind` controls selection, authorization,
  bar placement, and animation coverage requirements.

## Removal and private-action ledger

The v2 selectable catalog removes `STAFF_STRIKE`, `STAFF_STRIKE_2`,
`STAFF_SWEEP`, and `STAFF_THRUST`. `STAFF_STRIKE_2` may retain only private
clip/action data needed by ordinary Staff autoattack presentation.

The following private continuations remain nonselectable and consume no
capacity: `WARRIOR_HEW_2`, `WARRIOR_AIR_TO_GROUND_PLACEHOLDER`,
`WARRIOR_SKYFALL_4`, `DAGGER_TRIP`, and `DAGGER_STALK_SHADOWSTEP`.

## Default v2 build and reset boundary

The post-cutover default is exact:

- schema version 2, revision 0;
- starting parent `DAGGERS`;
- slot 0 specialization `DAGGERS_BLADEDANCER`;
- `TRAINING_DAGGER_PAIR` with existing empty color/off-hand values;
- selected feature `DAGGER_QUICK_CUT` at preferred order 0;
- no selected Traits and no dormant Specializations.

The ignored recoverable pre-reset snapshot is
`Library/ArenaLocalMultiplayer/combat-build-v2.before.json`, SHA-256
`9c9b6864142859c5b305c96e8270f72341508dc85a9c6cc63bc88a57ceaa3af5`.
It records:

| Table | Rows | Cutover disposition |
| --- | ---: | --- |
| `combat_build` | 8 | reset |
| `combat_build_discipline` | 12 | reset |
| `discipline_configuration` | 12 | reset |
| `staff_school_selection` | 4 | reset |
| `discipline_action_bar_assignment` | 14 | reset |
| `discipline_passive_selection` | 2 | reset |
| `hub_player` | 8 | preserve |
| `hub_player_armor_selection` | 8 | preserve |

No reset was executed in Phase 0. The approved combat-build-only reset remains
the Phase 7 cutover and has no v1-to-v2 converter.

## Animation compatibility result

No animation architecture rewrite is required. Existing
`SpellCastAnimationMap` discovery remains driven by `gameplay.kind`; semantic
Spells require coverage under all five weapon animation profiles, while a
Technique implemented by the spell executor requires only its parent weapon
profile. The current `CombatVFXAuthoringValidator` already walks each map entry
across every `CombatAnimationSet`, and `CombatAnimationSet` retains its existing
spell-family fallback. Phase 5 adds executable checks against `loadout_kind`;
it does not add animator topology.

The ledger records 104 semantic Spells and 23 Techniques that currently use the
spell executor.

## Validation fixture inventory

The generated contract defines 32 stable valid/invalid fixtures covering all
Phase 0 invariants, including same-parent Forms, three Schools, mixed builds,
18-feature success, nineteenth-feature rejection, Trait capacity, empty
Specializations, owner/kind mismatch, forbidden Staff Techniques, dormant
reflow, Mastery parent counting, duplicate/order errors, atomic rejection, and
unknown catalog references. Phase 1 turns these reviewed fixture identities
into executable validator tests.

## Baseline evidence

- `cargo test --manifest-path server/Cargo.toml --lib --no-fail-fast`: PASS,
  788 passed, 0 failed; 20 existing unused/dead-code warnings in progression.
- `cargo test --manifest-path hub-server/Cargo.toml --lib --no-fail-fast`:
  PASS, 25 passed, 0 failed.
- `ops/setup-local-multiplayer.sh setup`: PASS, data-preserving. Hub and match
  artifacts rebuilt and published; managed provisioner started.
- `ops/setup-local-multiplayer.sh status`: PASS. Local Spacetime, Hub artifact,
  match artifact, and managed provisioner reported ready/running.
- Unity Editor log: scripts compiled and domain reloaded successfully with no
  C# compiler error. Two pre-existing, unrelated VFX validation errors remain:
  `VFX_CLEANSE_HOLY_01` and `VFX_EARTHQUAKE_GROUND_01` lack their expected
  prefab/component authoring. Unity batch mode was not used.
- The skill-bundled legacy Hub loadout guard is intentionally inapplicable: it
  queries the removed public `hub_player_loadout` v0 table. The current private
  eight-table snapshot above is the fail-closed v1 cutover evidence.
- `python3 ops/generate-combat-build-v2-phase0.py --check`: PASS.

## Exit gate

PASS. Every retained selectable player feature has one reviewed owner and
loadout kind; all four Staff melee abilities have an explicit disposition; all
seeded Forms are nonempty; Mastery, dynamic-bar access, reset targets, default
seed, fixtures, and animation compatibility are exact; and every observed
baseline issue is classified.
