# Combat Authoring Contract

This is the current entry point for adding or reviewing combat actions. It summarizes the source-of-truth files, the identity rules between them, and the minimum checklist for each common action type.

Prefer Unity editor authoring tools when they exist for an action type. Until then, `docs/ability-implementation-prompt-template-2026-04-22.md` is the manual/LLM fallback prompt.

See also: `docs/combat-animation-authoring-contract.md` for animation layer ownership, Hit Windows, lower-body unlock, visual interruption, root motion, movement actions, casts, and reaction priority.

## Source Ownership

### Progression Catalog

File: `server/src/progression_catalog.shared.json`

Owns player-facing combat data:

- combat profiles and classes
- resources, combat rules, and stat scaling
- selectable abilities
- melee gameplay tuning
- spell gameplay authored on ability rows
- movement-delivery gameplay authored on ability rows
- auto-attack gameplay tuning
- auto-attack replacement tuning
- action presentations
- default loadout assignments
- fixed-action bindings
- loadout slots

`ability_tags` controls loadout eligibility and starter semantics. `LOADOUT_ACTION`
means the ability can be assigned to normal action-bar slots. `CORE_ABILITY`
means the ability is class-defining and should appear on newly seeded loadouts;
fixed actions such as `DODGE` and `PARRY` can be default loadout assignments,
but they are not core abilities.

It does not own melee clip timing, melee phased clips, VFX implementation, or server code for new behavior kinds.

### Combat Animation Sets

Files: `Assets/Arena/Resources/CombatAnimationSets/*.asset`

Own Unity-authored presentation and melee timing:

- combat-profile identity
- authored melee strike ids
- runtime melee slot ids
- melee hit windows
- targeted melee recovery timing
- combo links
- phased melee clips
- spell animation entries
- weapon presentation data

Melee hit timing comes from hit windows and recovery timing. Hit Windows are gameplay contact/release timing, not lower-body unlock or visual interruption timing. Animation presentation phase rules are defined in `docs/combat-animation-authoring-contract.md`.

### Melee Manifest

File: `server/src/melee_manifest.shared.json`

This is a generated/exported bridge from Unity combat animation sets to the server. Do not hand-edit it to fix identity drift; fix the animation set and re-export.

### Loadout Assignments

`SavedSpecSlotAssignment` uses ActionRef placement:

- `slot_id`: the action bar slot.
- `action_kind`: `ABILITY` or `FIXED`.
- `action_id`: the assigned action ref id.
  - For `ABILITY`, this is an `ability_id`.
  - For `FIXED`, this is a fixed action id such as `DODGE` or `PARRY`.

`ability_id` still exists as a legacy compatibility mirror. New action-bar work should resolve placement through `action_kind` and `action_id`.

Preferred selectable ability placement shape:

```json
{
  "class_id": "WARRIOR",
  "slot_id": "slot_1_6",
  "action_kind": "ABILITY",
  "action_id": "WARRIOR_GROUND_TO_AIR_PLACEHOLDER",
  "ability_id": "WARRIOR_GROUND_TO_AIR_PLACEHOLDER",
  "sort_order": 160
}
```

For selectable abilities, `action_id` is the `ability_id`. Do not put an ability id in `action_kind`; `action_kind` must be `ABILITY` or `FIXED`.

## Compatibility Seams

- `StartCharge` remains only as a generated-client compatibility reducer. Current action bars must not route through it; selectable movement abilities dispatch through `CastRequest` and server-side movement delivery.
- Internal Rust helpers use the name `movement_delivery` for the runtime domain. The authored JSON field is `gameplay.delivery` on `gameplay.kind: "MOVEMENT"` abilities.
- `SPELL:*` presentation rows are published for cast bars and legacy lookup paths, but they are derived from SPELL ability gameplay and ABILITY presentations. Do not author `presentation_kind: "SPELL"` rows in `progression_catalog.shared.json`.

## Glossary

- `ability_id`: player-facing ability row id in `abilities[]`.
- `gameplay.kind`: ability category, currently `MELEE`, `SPELL`, `MOVEMENT`, `AUTO_ATTACK_REPLACEMENT`, or `COMBAT_MODE_TOGGLE`.
- `ability_kind`: derived public table compatibility field. Do not author it in `progression_catalog.shared.json`.
- authored strike id: design-facing melee strike id authored in a combat animation set. Melee ability `action_id` values point here.
- runtime slot id: internal melee runtime id used for cooldown/combo plumbing. Player-facing progression rows must not point here.
- spell id: the spell ability `action_id`; runtime spell rows are derived from `abilities[]` where `gameplay.kind == "SPELL"`.
- movement delivery: `gameplay.delivery` on a `MOVEMENT` ability. Charge-like actions use this, not spell rows.
- fixed action id: UI/input action id such as `DODGE` or `PARRY`.
- ActionRef: loadout assignment identity made from `action_kind` plus `action_id`.
- loadout slot id: action-bar slot id, normalized to grid-style ids such as `SLOT_0_0`.
- combat profile id: animation/combat-profile id such as `TWO_HANDED_SWORD`.
- class id: player class id such as `WARRIOR`.

## Checklists

### New Class With New Weapon Set

1. Import the weapon/animation pack and follow `docs/weapon-visual-integration-contract.md` for semantic mount validation.
2. Create a `CombatAnimationSet` asset under `Assets/Arena/Resources/CombatAnimationSets`.
3. Set both `animationSetId` and `combatProfileId` on that asset to the new uppercase combat profile id.
4. Fill weapon presentation, draw/sheath clips, locomotion/combat clips, spell entries, and authored melee attacks.
5. Export `server/src/melee_manifest.shared.json` from the animation set inspector.
6. Add a `combat_profiles[]` row and a `classes[]` row in `server/src/progression_catalog.shared.json`, with the class `default_combat_profile_id` pointing at the new profile.
7. Add `auto_attacks[]` gameplay for the new combat profile.
8. Add class abilities, action presentations, fixed-action bindings if needed, and default loadout assignments.
9. Add or choose a default class outfit in the appearance catalogs if the class should be selectable in character creation.
10. Run server tests and Unity editor tests.

### Selectable Melee

1. Author or select a melee strike in the class combat profile's combat animation set.
2. Re-export `server/src/melee_manifest.shared.json`.
3. Add an `abilities[]` row with `gameplay.kind: "MELEE"`.
4. Set the ability `action_id` to the authored strike id, not the runtime slot id.
5. Put melee damage/range/cooldown/defense tuning inside `gameplay`; keep player-facing resource cost at the ability row level.
6. Add an `ABILITY` presentation row.
7. Add or update default loadout placement using `action_kind: "ABILITY"`, `action_id` set to the `ability_id`, and `ability_id` mirrored for legacy compatibility.
8. Run the server tests.

### Selectable Spell

1. Add or update an `abilities[]` row with `gameplay.kind: "SPELL"`.
2. Set the ability `action_id` to the spell id.
3. Put spell cooldown/cast/targeting/resource details inside `gameplay`.
4. Put spell delivery behavior inside `gameplay.delivery`.
5. Add a spell animation entry for every class combat profile that exposes the spell.
6. Add an `ABILITY` presentation row, and a `SPELL` presentation row when the spell is directly presented.
7. Add or update default loadout placement through ActionRef-compatible assignment data.
8. Run the server tests.

### Movement Delivery Ability

Movement delivery abilities are class-owned gameplay abilities that move the caster and optionally apply impact effects. Charge-like abilities are ordinary selectable abilities whose behavior is driven by `gameplay.delivery`.

1. Add an `abilities[]` row with `gameplay.kind: "MOVEMENT"`.
2. Put movement execution details inside `gameplay.delivery`, with `delivery.kind` such as `DASH_TO_TARGET`.
3. Put arrival impact effects in `gameplay.delivery.impact_effects`.
4. Add default loadout placement as an `ABILITY` assignment to the movement ability id.
5. Do not create a fake spell row or fixed action wrapper for movement delivery.
6. Run the server tests.

### Fixed Action

1. Keep the fixed action id as a UI/input action, such as `DODGE` or `PARRY`.
2. If the action resolves through class ability behavior, add the class-owned ability row.
3. Add or update `fixed_action_bindings[]` for each class only when the fixed action needs class ability behavior. Pure fixed actions such as `DODGE` and `PARRY` do not need bindings.
4. Add a `FIXED` presentation row for the fixed action id.
5. Add default loadout placement using `action_kind: "FIXED"` and `action_id` set to the fixed action id.
6. Keep client/server hardcoded dispatch policy narrow until there are enough fixed actions to justify data-driven behavior.
7. Run the server tests.

### Auto-Attack

Auto-attacks are intrinsic combat-profile behavior. Author the strike identity in the combat animation set, export the melee manifest, and tune gameplay in `auto_attacks[]`. Do not expose auto-attacks as selectable loadout abilities.

Per-hit primary resource gain is intrinsic auto-attack behavior. Selectable melee resource changes should come from authored costs or explicit behavior, not generic hit gain.

### Auto-Attack Replacement

Auto-attack replacements are player-facing loadout abilities that arm the next intrinsic auto-attack swing. They are distinct from selectable melee because the button does not perform a strike immediately.

Authoring path:

1. Author or select a melee strike in the class combat profile's combat animation set.
2. Re-export `server/src/melee_manifest.shared.json`.
3. Add an `abilities[]` row with `gameplay.kind: "AUTO_ATTACK_REPLACEMENT"`.
4. Set the ability `action_id` to an `auto_attack_replacements[]` `replacement_id`, not directly to the authored strike.
5. Put player-facing resource cost on the ability row.
6. Put replacement strike gameplay tuning on the `auto_attack_replacements[]` row, including `authored_melee_strike_id`, damage, range, cooldown, defense behavior, expiry, and whether the swing grants primary resource on hit.
7. Add an `ABILITY` presentation row.
8. Add or update default loadout placement through ActionRef-compatible assignment data.
9. Run the server tests.

Runtime rule: pressing the ability only arms a pending replacement. When the next auto-attack fires, the server pays the resource and uses the replacement strike only if the replacement is still valid and payable; otherwise the normal auto-attack fires and the pending replacement is consumed.

### Self-Buff

Self-buffs are spells with self-targeted `APPLY_STATUS` delivery. Author them as `gameplay.kind: "SPELL"` abilities with `gameplay.targeting: "SELF"` and `gameplay.delivery.kind: "APPLY_STATUS"`. Add combat animation set spell entries for exposing profiles.

## Validation

Run the server test suite from `server/`:

```bash
cargo test
```

The current combat authoring validator is the `combat_authoring_graph_validates_first_pass_contract` test in `server/src/progression.rs`. It builds an internal resolved action graph and fails on the first-pass authoring mistakes that have already caused or are likely to cause content churn:

- `ability-class-resolves`: an ability references an unknown class, or its class cannot resolve to a default combat profile. Fix the ability `class_id` or class combat-profile setup.
- `melee-action-id-matches-authored-strike`: a melee ability `action_id` does not match an authored strike id for the class combat profile. Fix the ability row to point at the authored strike id, or author/export the missing strike.
- `melee-action-id-not-runtime-slot`: a melee ability points at runtime slot plumbing. Use the authored strike id in progression; runtime slot ids stay internal.
- `spell-action-id-resolves-to-spell`: a spell ability cannot derive a runtime spell row from its `gameplay` block. Fix `gameplay.kind`, `action_id`, or `gameplay.delivery`.
- `selectable-spell-has-animation-entry`: a selectable spell lacks a combat animation set spell entry for the exposing class profile. Add the entry to the relevant `Assets/Arena/Resources/CombatAnimationSets/*.asset`.
- `auto-attack-replacement-resolves`: an auto-attack replacement ability points at a missing replacement row or a replacement row for the wrong combat profile. Fix the ability `action_id` or the replacement row.
- `auto-attack-replacement-strike-matches-authored-strike`: an auto-attack replacement row references a strike id that does not exist in the class combat profile. Use an authored strike id from the combat animation set, not runtime slot plumbing.
- `default-loadout-assignment-resolves`: a default loadout assignment references an unknown class, unknown slot, unknown ability, wrong-class ability, slot-incompatible ability, duplicate class/slot pair, or unsupported fixed action. Fix the assignment's slot and ActionRef target.
- `fixed-action-binding-resolves`: a fixed action binding references an unknown class, unknown ability, wrong-class ability, unsupported fixed action, duplicate class/action pair, or ability whose `fixed_action_id` does not match the binding. Fix the `fixed_action_bindings[]` row or bound ability row.
- `core-ability-has-default-assignment`: an ability tagged `CORE_ABILITY` has no default loadout assignment. Add a default `ABILITY` assignment for the same class, or remove the core tag.
- `player-facing-action-has-presentation`: a player-facing ability has no `ABILITY` presentation row, or a default fixed action has no `FIXED` presentation row. Add the row in `action_presentations[]`.
- `presentation-target-resolves`: an action presentation references an unknown ability, spell, fixed action, or unsupported presentation kind. Fix the `action_presentations[]` row.
- `spell-presentation-not-authored`: a `SPELL` presentation was authored directly. Remove it and author the corresponding `ABILITY` presentation instead; the server derives the public `SPELL:*` presentation row.
- `ability-kind-supported`: an ability uses an unsupported `gameplay.kind`. Use one of the supported gameplay kinds.

Editor-side C# contract tests also guard the current action-bar dispatch shape. Resolved loadout actions carry `action_kind`, `action_ref_id`, and a derived `ability_kind`; action-bar dispatch uses that resolved metadata instead of guessing melee vs. spell by probing unrelated tables.

## Worked Examples

These examples use current Warrior and Paladin rows as patterns. They are not new content requests.

### Selectable Melee: Warrior Hew

Files involved:

- `Assets/Arena/Resources/CombatAnimationSets/TwoHandedSword.asset`
- `server/src/melee_manifest.shared.json`
- `server/src/progression_catalog.shared.json`

Authoring path:

1. The greatsword combat animation set owns the melee strike identity and presentation. The authored strike id is `COMBO_ATTACK_1_1_HIGH_TO_LOW`; its runtime slot id is internal melee plumbing.
2. Unity export writes that authored strike into `server/src/melee_manifest.shared.json`.
3. `server/src/progression_catalog.shared.json` exposes it through an ability row:
   - `ability_id`: `WARRIOR_HEW`
   - `class_id`: `WARRIOR`
   - `action_id`: `COMBO_ATTACK_1_1_HIGH_TO_LOW`
   - `gameplay.kind`: `MELEE`
4. The ability row owns player-facing resource cost. `gameplay` owns melee tuning such as base damage, range, cooldown, GCD use, and defense behavior.
5. An `ABILITY` presentation row for `WARRIOR_HEW` owns display text.
6. A default loadout assignment places `WARRIOR_HEW` in a loadout slot through ability placement.

Key rule: the ability points at the authored strike id, not at a runtime slot id such as `light_combo_1`.

### Selectable Spell: Warrior Momentum

Files involved:

- `server/src/progression_catalog.shared.json`
- `Assets/Arena/Resources/CombatAnimationSets/TwoHandedSword.asset`

Authoring path:

1. The ability row exposes the spell to Warrior:
   - `ability_id`: `WARRIOR_MOMENTUM`
   - `class_id`: `WARRIOR`
   - `action_id`: `MOMENTUM`
   - `gameplay.kind`: `SPELL`
2. `gameplay` owns runtime behavior:
   - cooldown/GCD/cast/targeting/resource behavior
   - `delivery.kind`: `APPLY_STATUS`
   - self-buff payload under `gameplay.delivery.status`
3. The runtime spell catalog derives a `MOMENTUM` spell row from that ability gameplay.
4. The greatsword combat animation set contains a spell animation entry for `MOMENTUM`, because Warrior derives to `TWO_HANDED_SWORD`.
5. `ABILITY` and `SPELL` presentation rows provide player-facing display data.
6. A default loadout assignment places `WARRIOR_MOMENTUM` in a loadout slot through ability placement.

Key rule: spell behavior lives under the ability's `gameplay` block; `spells[]` is no longer authored.

### Charge-Like Movement Abilities

Files involved:

- `server/src/progression_catalog.shared.json`
- `Assets/Arena/Runtime/Input/ActionBarInputDispatcher.cs`
- `Assets/Arena/Runtime/Input/SpellInputHandler.cs`
- `server/src/movement_actions.rs`
- `server/src/spells/mod.rs`
- `server/src/spells/casting.rs`

Authoring path:

1. Every charge-like action is a distinct class-owned ability:
   - `WARRIOR_CHARGE` backs Warrior Charge.
   - `PALADIN_CHARGE` backs Paladin Charge.
2. Each row uses `gameplay.kind: "MOVEMENT"` and `gameplay.delivery.kind: "DASH_TO_TARGET"`.
3. Default loadout assignments use ActionRef placement to the ability id:
   - `action_kind`: `ABILITY`
   - `action_id`: `WARRIOR_CHARGE`
   - `ability_id`: `WARRIOR_CHARGE`
4. Add an `ABILITY` presentation row for each movement ability.
5. Do not add `fixed_action_id: "CHARGE"`, a `FIXED` presentation row for `CHARGE`, or a `fixed_action_bindings[]` row.
6. Unity dispatches the selectable movement ability through `CastRequest`; the server maps `MOVEMENT` delivery into generic movement runtime.

Key rule: class-specific charge tuning lives on the ability row. Shared behavior lives in movement delivery, not in a fixed action.

## Consumer Decisions

No progression JSON schema is currently required. Rust serde validates catalog structure at load time, and the graph validator covers cross-file authoring coherence. Add `server/src/progression_catalog.schema.json` only when an editor or non-Rust tool is ready to consume it.

No generated combat action manifest is currently checked in. The server and client now use structured action refs and resolved loadout metadata in the live resolver/dispatch paths; that is enough to justify the current refactor. Add a serialized manifest only when a runtime/editor/audit consumer reads that file directly.
