# Combat Intimidate Crowd Control Plan - 2026-05-05

Status: shipped and superseded by `docs/archive/2026-05-stale-plans/spell-system-unification-plan-2026-05-05.md`. This file is historical context for the original Intimidate design. Do not use its old `SPELL`/`spells[]`, `CHARGE`, or `SELF_BUFF` findings as current authoring guidance.

## Goal

Add a Warrior spell, `Intimidate`, that applies an `INTIMIDATED` debuff to the opponent.

Required behavior:

- `Intimidate` is an instant targeted spell.
- Range is 20 meters.
- The target receives an `INTIMIDATED` debuff.
- `INTIMIDATED` functions exactly like stun for gameplay disabling, but remains a distinct crowd-control type.
- Future cleanse logic must be able to remove `INTIMIDATED` separately from `STUN`.
- The caster uses the imported `point` animation, but the visual must point with the left hand.
- The intimidated target uses the imported `terrified` animation while the debuff is active.

## Current System Findings

### Authoring Ownership

- `server/src/progression_catalog.shared.json` is the source of truth for player-facing abilities, spell tuning, class ownership, action presentations, and default loadouts.
- Spell rows in `progression_catalog.shared.json` are parsed by `server/src/spells/catalog.rs` into runtime spell definitions.
- Player-facing ability rows in `progression_catalog.shared.json` are parsed by `server/src/progression.rs`; `SPELL` abilities must point their `action_id` at a spell row.
- `Assets/Arena/Resources/CombatAnimationSets/*.asset` is the Unity source of truth for combat presentation clips.
- Spell presentation is authored in `CombatAnimationSet.spells[]`, keyed by runtime spell/action id.
- `server/src/melee_manifest.shared.json` is generated from Unity melee animation authoring and should not be involved for this spell.

### Status Gameplay

- Status truth is server-authoritative in `server/src/combat.rs`.
- Status kinds currently include `ROOT`, `STUN`, `STAGGER`, `KNOCKDOWN`, `SLOW`, DOT/HOT, and buff modifiers.
- Status payloads are encoded through `StatusPayload` into sparse status columns and decoded by `StatusEffectKind`.
- Movement disabling is not currently generic. It is hardcoded in several places:
  - `has_active_disabling_status(...)` returns true for `STUN`, `STAGGER`, `KNOCKDOWN`.
  - `MovementModifiers` has separate sets for `stunned`, `staggered`, `knocked_down`.
  - `MovementModifiers::is_disabled(...)` checks those three sets.
  - Client hit-reaction suppression checks `STUN` and `KNOCKDOWN`.
- `ROOT` blocks movement but is not treated as a full disabling/cast-interrupt status.

### Spell Runtime

- Instant spells with zero cast time go through `cast_spell_for(...)`, validate with `process_spell_cast(...ValidateOnly)`, execute with `process_spell_cast(...Execute)`, then stamp cooldown/GCD and spend resource.
- Cast-time spells validate in `cast_spell_for(...)`, create an `ActiveCast`, and execute later through `finish_active_cast(...)`, which calls `process_spell_cast(...Execute)` before consuming the active cast row.
- `process_spell_cast(...Execute)` should remain the single spell-behavior execution dispatch. `INTIMIDATE` is instant in V1, but the behavior must still be written so a future non-instant `APPLY_STATUS` spell can execute correctly through `finish_active_cast(...)`.
- Existing generic spell behaviors are `PROJECTILE`, `AREA`, `INSTANT_BEAM`, `CHANNEL`, `CHARGE`, `SELF_BUFF`, and `SELF_RESOURCE`.
- Existing status applications are not fully generic across spell behaviors:
  - `CHARGE` impact effects can apply stun/knockdown/stagger.
  - `METEOR` area effects can apply stun/stagger.
  - `FROST_NOVA` has bespoke root logic.
  - Self buffs use `SELF_BUFF`.
- There is no generic `APPLY_STATUS` behavior today.

### Client Presentation

- Status row inserts/updates/deletes are handled by `Assets/Arena/Runtime/Entity/EntityRegistry.cs`.
- Entity status tinting is handled by `PlayerEntity.ApplyStatusEffect/RemoveStatusEffect`.
- Hard-CC presentation uses one generic loop path:
  - `EntityRegistry.RefreshStatusPresentation(...)` scans active target statuses for hard CC and `KNOCKDOWN`.
  - `PlayerEntity.SetHardCrowdControl(...)` forwards to `PlayerAnimator.SetHardCrowdControl(...)`.
  - `CombatStatusReactionController` drives `IsHardCrowdControlled`, `TriggerHardCrowdControl`, and `slot_hard_crowd_control_loop`.
  - `CombatAnimationSet` has `stunStart`, `stunLoop`, `stunEnd`, but runtime currently maps `stunLoop` as the default clip for the generic hard-CC loop slot.
- The Animator controller has one hard-CC loop presentation path, not separate status-reaction states per CC type.
- Spell cast presentation uses four reusable spell bank slots (`slot_spell_1` through `slot_spell_4`) populated at runtime from `CombatAnimationSet.spells[]`.

### Imported Assets

- New files exist:
  - `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/point.fbx`
  - `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/terrified.fbx`
- Their `.meta` files are not present in the workspace yet. Unity will need to import them and produce stable `.meta` GUIDs before serialized `CombatAnimationSet` references are reliable.

## Resolved Design Decisions

- `INTIMIDATED` duration is `4000ms`.
- The duration is authored on the `INTIMIDATE` spell row in `server/src/progression_catalog.shared.json`, inside the `APPLY_STATUS` behavior data. Do not hardcode this in status runtime or animation code.
- `INTIMIDATE` cooldown is `60000ms`.
- `INTIMIDATE` is an instant targeted `APPLY_STATUS` spell, not a projectile, beam, area, or charge.
- `INTIMIDATE` is `UNBLOCKABLE` and `UNPARRYABLE`.
- Future outplay should come from a separate fear/intimidation-immunity style buff, not from block/parry.
- `INTIMIDATE` costs `0` Rage.
- `INTIMIDATED` interrupts active casts/actions the same way stun does because it participates in the hard crowd-control/disabling-status path.
- Add `WARRIOR_INTIMIDATE` to the Warrior default loadout.
- Default placement: `slot_1_7`, the first currently open Warrior default slot after `slot_1_6`.
- Add presentation only to Warrior's combat profile: `TWO_HANDED_SWORD`.
- Do not add Intimidate animation authoring to `SWORD_AND_SHIELD` unless Warrior profile access changes later.
- The left-hand point animation must be a distinct clip/import result assigned to `INTIMIDATE`; do not mirror reusable Animator spell-bank states.

## Design Direction

### Add `INTIMIDATED` As A Real Status Kind

`INTIMIDATED` must not be encoded as `STUN` with a different stack group. That would make future cleanse logic ambiguous and would hide the requested separate CC type.

Add:

- `StatusEffectKind::Intimidated`
- `StatusPayload::Intimidated`
- wire value `"INTIMIDATED"`
- encode/decode support using the same empty payload columns as stun
- invalid/strength comparison handling equivalent to stun
- round-trip tests beside existing status payload tests

### Centralize Hard Crowd Control Classification

Avoid adding another scattered `|| kind == StatusEffectKind::Intimidated` branch everywhere.

Add a single server helper, for example:

```rust
pub fn is_hard_crowd_control_kind(kind: StatusEffectKind) -> bool {
    matches!(
        kind,
        StatusEffectKind::Stun
            | StatusEffectKind::Intimidated
            | StatusEffectKind::Stagger
            | StatusEffectKind::Knockdown
    )
}
```

Then route disabling behavior through that helper:

- `has_active_disabling_status(...)`
- `MovementModifiers` population and `is_disabled(...)`
- any future cleanse validation

Keep `ROOT` out of this helper unless design changes. Root blocks movement but is not currently a full stun-equivalent disabling status.

`MovementModifiers` currently stores separate `stunned`, `staggered`, and `knocked_down` sets, but production callers only ask whether movement is blocked. Prefer replacing those three movement-only sets with one `disabled: HashSet<Identity>` populated by scanning active `StatusEffect` rows through `is_hard_crowd_control_kind(...)`. Keep root as a separate set because root and hard CC have different gameplay meaning. Preserve status distinctions in `StatusEffect`, not in movement-only derived state.

### Add Generic `APPLY_STATUS` Spell Behavior

Do not make `Intimidate` a zero-damage projectile, fake beam, bespoke one-off spell path, or `TARGET_STATUS` sibling to `SELF_BUFF`. The cleaner detour is to generalize current `SELF_BUFF` into `APPLY_STATUS`.

Add a new behavior `APPLY_STATUS` to:

- `SpellBehavior`
- `SpellCatalogBehavior`
- `SpellDefinition` secondary tunables if needed
- `process_spell_cast(...)`

First-pass scope:

- Support `SELF` and `TARGET` application modes.
- Migrate the existing `MOMENTUM` `SELF_BUFF` row to `APPLY_STATUS` with `targeting: "SELF"` and `polarity: "BUFF"`.
- Add `INTIMIDATE` as `APPLY_STATUS` with `targeting: "TARGET"` and `polarity: "DEBUFF"`.
- Keep `SELF_RESOURCE` for `ENRAGE`; resource generation is not a status application.
- Do not collapse `FROST_NOVA` into `APPLY_STATUS` in this pass. Later, move Frost Nova root into generic `AREA` impact effects instead of broadening `APPLY_STATUS` into a damage/AoE/event behavior.

Suggested self-buff catalog shape:

```json
{
  "kind": "MOMENTUM",
  "cooldown_ms": 12000,
  "uses_global_cooldown": true,
  "cast_time_ms": 0,
  "cast_mobility": "MOBILE",
  "targeting": "SELF",
  "requires_target": false,
  "resource_cost": 20.0,
  "arms_auto_attack_on_cast": false,
  "behavior": {
    "kind": "APPLY_STATUS",
    "targeting": "SELF",
    "polarity": "BUFF",
    "duration_ms": 4000,
    "status_stack_group": "MOMENTUM",
    "status": {
      "kind": "MOVE_SLOW_IMMUNITY",
      "modifier_scalar": 0.0,
      "max_stacks": 1,
      "stack_policy": "REFRESH"
    }
  }
}
```

Suggested `INTIMIDATE` catalog shape:

```json
{
  "kind": "INTIMIDATE",
  "cooldown_ms": 60000,
  "uses_global_cooldown": true,
  "cast_time_ms": 0,
  "cast_mobility": "MOBILE",
  "targeting": "TARGET",
  "requires_target": true,
  "resource_cost": 0.0,
  "arms_auto_attack_on_cast": true,
  "behavior": {
    "kind": "APPLY_STATUS",
    "targeting": "TARGET",
    "polarity": "DEBUFF",
    "max_distance": 20.0,
    "block_behavior": "UNBLOCKABLE",
    "parry_behavior": "UNPARRYABLE",
    "duration_ms": 4000,
    "status_stack_group": "INTIMIDATED",
    "status": {
      "kind": "INTIMIDATED",
      "modifier_scalar": 0.0,
      "max_stacks": 1,
      "stack_policy": "REFRESH"
    }
  }
}
```

Validation rules:

- Top-level `targeting` and behavior-level `targeting` must agree.
- `SELF` apply-status rows must use `targeting: "SELF"`, `requires_target: false`, `polarity: "BUFF"`, and must not define target-only range/defense fields.
- `TARGET` apply-status rows must use `targeting: "TARGET"`, `requires_target: true`, define `max_distance`, `block_behavior`, and `parry_behavior`, and may use `polarity: "DEBUFF"` for harmful status application.
- `duration_ms` must be positive. Tests for invalid duration belong in catalog validation, not runtime `queue_effects`, because `queue_effects` already rejects invalid payload shapes but does not own spell authoring semantics.
- Preserve the current self-buff status allowlist for `BUFF` statuses unless deliberately expanding it. This keeps `APPLY_STATUS` generic without allowing arbitrary nonsensical buff rows.
- Add an explicit allowed debuff-status validation path for `INTIMIDATED` and future deliberate debuffs.
- Implement the new status row type with serde tagged enums and `deny_unknown_fields` so unknown status kinds/fields fail during catalog parse.

`TARGET` validation should match other targeted combat spells:

- target exists
- target is not self
- target is alive
- same world context
- within facing arc, unless design explicitly says otherwise
- line of sight, unless design explicitly says otherwise
- horizontal/live range <= 20m
- no block/parry mitigation because the spell is explicitly unblockable and unparryable

Execution should:

- follow the existing `cast_self_buff(...)` template for status application, then delete/replace that specialized function
- emit `COMBAT_CAST` for caster animation
- for `TARGET` applications, also emit `COMBAT_IMPACT` when the debuff is applied so target-side presentation and VFX can key off an impact fact; existing self buffs do not emit impact, so this is a target-mode distinction
- queue one `EffectPacket::ApplyStatus` with resolved source, target, payload, polarity, duration, stack group, max stacks, and stack policy
- rely on the pending apply-status consumer to mark harmful combat engagement for debuffs; do not synthesize fake damage only to enter combat
- leave `ActiveSpell` untouched; pure apply-status has no projectile/travel simulation

Parry behavior storage:

- `SpellDefinition.block_behavior` already exists as a flat field.
- There is no flat spell-level `parry_behavior`; projectile and charged-release behavior currently store it in behavior-specific secondary tunables.
- Store apply-status `parry_behavior` inside a new `ApplyStatusSecondaryTunables` or equivalent behavior-specific secondary data. Do not add a misleading flat parry field unless multiple non-projectile/non-charged-release behaviors will share it.

### Add Warrior Ability Catalog Rows

Add an ability row:

- `ability_id`: `WARRIOR_INTIMIDATE`
- `class_id`: `WARRIOR`
- `action_id`: `INTIMIDATE`
- `display_name`: `Intimidate`
- `resource_kind`: `RAGE`
- `ability_tags`: `["LOADOUT_ACTION"]`
- `ability_kind`: `SPELL`
- sort order: `95`, after `Enrage` and before `Charge`

Add an action presentation row for `INTIMIDATE` so loadout/UI display is not inferred.

Add a default loadout assignment:

- `class_id`: `WARRIOR`
- `slot_id`: `slot_1_7`
- `ability_id`: `WARRIOR_INTIMIDATE`
- `sort_order`: `170`

### Client Status Presentation Should Become Hard-CC Aware

Do not bolt an `IsIntimidated` path beside `IsHardCrowdControlled` unless the Animator controller is deliberately expanded and validated.

Preferred V1:

- Add client constants for `INTIMIDATED`.
- Replace `EntityRegistry`'s stun-only scan with a deterministic hard-control scan:
  - `KNOCKDOWN` wins over all hard-control loops.
  - `STUN` and `INTIMIDATED` both request the hard-control loop.
  - If both stun and intimidated are active, choose a deterministic presentation priority, probably `STUN` over `INTIMIDATED`.
- Add `PlayerEntity.SetHardCrowdControl(string? statusKind)` or equivalent.
- Add `PlayerAnimator.SetHardCrowdControl(string? statusKind)`.
- `EntityRegistry` must compute one hard-control decision per refresh, such as `(active: bool, kind: string)`, and call one entity method. Do not separately toggle the bool and swap clips from different paths.
- `PlayerAnimator.SetHardCrowdControl(...)` must write the correct slot override before pulsing the trigger/bool so the Animator enters with the intended clip. Otherwise a stun/intimidated overlap can set the bool, enter the old clip, then never re-enter after the override changes.
- Under the hood, use the generic Animator hard-CC loop path, but choose the loop clip by status kind:
  - `STUN` -> existing `CombatAnimationSet.stunLoop`
  - `INTIMIDATED` -> new intimidated/terrified clip
- The implementation should update the override for `slot_hard_crowd_control_loop` before setting the trigger/bool.
- When the hard-control loop clears, restore `slot_hard_crowd_control_loop` to the normal stun clip so a later stun cannot inherit the terrified clip.
- Because `CombatStatusReactionController.TriggerHit(...)` suppresses hit reactions when `IsHardCrowdControlled` is true, direct hit suppression works automatically for `INTIMIDATED`.
- `EntityRegistry.HasSuppressingReactionStatus(...)` separately scans status rows before flushing deferred hit reactions and must also include `INTIMIDATED`.

This keeps the Animator graph from defining gameplay meaning while avoiding a second graph path that can drift from stun.

`IsHardCrowdControlled` / `TriggerHardCrowdControl` are the controller mechanism. Runtime/gameplay APIs should expose `SetHardCrowdControl(statusKind)`.

### Extend CombatAnimationSet For Status Reactions

Preferred data model:

```csharp
[Serializable]
public struct StatusReactionAnimationEntry
{
    public string statusKind;
    public AnimationClip? loop;
}
```

Add to `CombatAnimationSet`:

```csharp
public StatusReactionAnimationEntry[] statusReactions;
```

Add resolver:

```csharp
public bool TryGetStatusReactionLoop(string statusKind, out AnimationClip clip)
```

Fallback:

- `STUN` may use existing `stunLoop` for compatibility.
- `INTIMIDATED` must not silently fall back to `stunLoop`; if the terrified clip is missing, warn and skip the special presentation rather than pretending the asset is authored.

Alternative narrower V1:

- Add `public AnimationClip? intimidatedLoop;`
- This is faster, but less aligned with future typed CC animations.

The status-reaction array is preferred because the user specifically called out future CC types and cleanses.

### Author Intimidate Spell Presentation

In the Warrior combat animation set (`TwoHandedSword.asset` unless design says otherwise):

- Add a `spells[]` entry:
  - `spellId: INTIMIDATE`
  - `ground: point_left` or the verified left-hand mirrored clip
  - `air`: optional; leave empty only if grounded fallback is acceptable
  - `playbackLayer`: decide whether this is full-body, upper-body, or upper-body only while moving. Since the spell is instant and range-targeted, `UpperBody` is likely appropriate if the clip reads correctly.
  - `requiresCombatStance` + `combatEntryMode`: author whether the spell should request combat stance before playback, after playback starts, or not at all.
  - `lowerBodyUnlockAtSeconds`: set from clip review, not guessed
  - `visualInterruptibleAtSeconds`: set from clip review, not guessed

For the target:

- Add status reaction entry:
  - `statusKind: INTIMIDATED`
  - `loop: terrified`

Make sure `terrified` imports as a looping humanoid animation if it must hold for arbitrary debuff duration.

Do not add this spell animation entry to non-Warrior combat profiles in V1.

### Left-Hand Point Implementation

Implementation rule:

- The mirror must live on the `point` clip/derived clip, not on the reusable spell bank Animator states.

Steps:

1. Import `point.fbx` and `terrified.fbx` in Unity so `.meta` files are created.
2. Set both rigs to Humanoid and verify avatar mapping.
3. Create a left-hand point clip:
   - Preferred: import or duplicate a mirrored humanoid clip variant, e.g. `point_left`.
   - If Unity importer supports per-clip mirror for this FBX, use that and commit the resulting `.meta`.
   - If not, create/export a real left-hand authored clip.
4. Confirm the mirrored point does not mirror unrelated spell bank playback.
5. Reference only the left-hand point clip in `CombatAnimationSet.spells[]`.

## Implementation Phases

### Phase 1 - Server Status Type

Files:

- `server/src/combat.rs`

Work:

- Add `Intimidated` to `StatusEffectKind`.
- Add `Intimidated` to `StatusPayload`.
- Add wire encode/decode support.
- Add `is_hard_crowd_control_kind(...)`.
- Update disabling/movement modifier code to include `INTIMIDATED` through the helper.
- Prefer replacing movement-only `stunned`, `staggered`, and `knocked_down` sets with one `disabled` set, since production callers only consume `blocks_movement(...)`.
- Add status payload round-trip tests.
- Add movement modifier tests proving `INTIMIDATED` blocks movement like stun.
- Add disabling status tests proving `INTIMIDATED` blocks new spell/cast starts and interrupts active casts like stun.

### Phase 2 - Generic `APPLY_STATUS` Spell Behavior

Files:

- `server/src/spells/manifest.rs`
- `server/src/spells/catalog.rs`
- `server/src/spells/casting.rs`
- optional tests in `server/src/spells/catalog.rs` and `server/src/spells/casting.rs`

Work:

- Add generic `APPLY_STATUS` behavior with `SELF` and `TARGET` modes.
- Migrate existing `SELF_BUFF` rows to `APPLY_STATUS` and remove the `SELF_BUFF` behavior variant.
- Replace `cast_self_buff(...)` with generic apply-status execution.
- Keep `SELF_RESOURCE` and `cast_self_resource(...)` for `ENRAGE`.
- Parse and validate duration, polarity, status payload, stack group, target mode, target range, and target defensive behavior.
- Store target-mode `parry_behavior` in apply-status secondary tunables.
- Execute the behavior through `process_spell_cast(...Execute)` so both instant and future cast-time apply-status spells use one execution site.
- Emit existing combat events; do not add a parallel event table.
- Do not create `ActiveSpell` rows for `INTIMIDATE`; there is no projectile or travel simulation.
- Add tests for:
  - the existing `MOMENTUM` row loads as `APPLY_STATUS`
  - unknown status kinds/fields rejected
  - missing/invalid duration rejected at catalog validation
  - `SELF` rows reject target-only range/defense fields
  - `TARGET` rows require range/defense fields
  - range validation
  - successful cast queues `INTIMIDATED`
  - unblockable/unparryable behavior does not consult block/parry as mitigation

### Phase 3 - Progression Catalog

Files:

- `server/src/progression_catalog.shared.json`
- `server/src/progression.rs` tests only if validator coverage needs a new assertion

Work:

- Add `INTIMIDATE` spell row.
- Convert `MOMENTUM` from `SELF_BUFF` to `APPLY_STATUS`.
- Add `WARRIOR_INTIMIDATE` ability row.
- Add action presentation row.
- Add default loadout row at `slot_1_7`.
- Add/adjust catalog tests so `SPELL` ability validation catches any missing spell row or wrong action id.

### Phase 4 - Generated Bindings

Files:

- `Assets/Arena/Runtime/Generated/SpacetimeDB/**`

Work:

- Regenerate SpacetimeDB C# bindings after the Rust schema changes.
- Do not hand-edit generated binding files except as part of the established project workaround pattern, if one is required.
- Confirm `StatusEffect.EffectKind` remains a string on the client, so most client code should not need generated enum changes.

### Phase 5 - Client Status Presentation Refactor

Files:

- `Assets/Arena/Runtime/Entity/EntityRegistry.cs`
- `Assets/Arena/Runtime/Entity/PlayerEntity.cs`
- `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs`
- `Assets/Arena/Runtime/Combat/GameplayContracts.cs`

Work:

- Add `INTIMIDATED` constants.
- Replace stun-only presentation refresh with hard-control presentation refresh.
- Suppress normal hit reactions while `INTIMIDATED` is active by widening `HasSuppressingReactionStatus(...)`; direct `CombatStatusReactionController.TriggerHit(...)` suppression is automatic through `IsHardCrowdControlled`.
- Add a status-kind-aware hard-control loop API.
- Make override swap and trigger pulse atomic inside `SetHardCrowdControl(...)`.
- Preserve knockdown priority over stun/intimidated.
- Add an `INTIMIDATED` tint if desired, but keep gameplay presentation independent from tinting.

### Phase 6 - CombatAnimationSet Authoring Support

Files:

- `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs`
- `Assets/Arena/Editor/CombatAnimationSetEditor.cs`
- `Assets/Arena/Resources/CombatAnimationSets/TwoHandedSword.asset`

Work:

- Add status reaction authoring data and resolver.
- Expose status reaction entries in the editor.
- Add `INTIMIDATE` spell animation entry to the Warrior animation set.
- Add `INTIMIDATED` status reaction loop using `terrified`.
- Route `INTIMIDATED` through the same runtime presentation path as stun, with a status-specific clip override, not through a new conflicting Animator controller branch.
- Preserve existing `stunLoop` behavior.
- Do not regenerate or flatten existing combat animation set assets.

### Phase 7 - Imported Animation Asset Setup

Files:

- `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/point.fbx`
- `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/point.fbx.meta`
- `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/terrified.fbx`
- `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/terrified.fbx.meta`
- any derived `point_left` clip asset/meta if Unity creates one

Work:

- Import as Humanoid.
- Verify `terrified` loops cleanly.
- Create/verify left-hand point.
- Commit `.meta` files.
- Assign clips in the relevant `CombatAnimationSet` asset.

### Phase 8 - Validation

Server:

```bash
cargo test --manifest-path server/Cargo.toml
```

Unity/editor:

- Run editor validation for `CombatAnimationSet` and Animator controller inventory.
- Confirm no Animator parameter/state validation drift after repurposing the stun loop as a hard-control loop.
- Confirm no generated binding compile errors.

Manual playtest:

- Warrior casts `Intimidate` on a target within 20m.
- Cast rejects outside 20m.
- Cast rejects without target.
- Cast obeys the targeted spell line-of-sight and facing rules.
- Caster plays left-hand point.
- Target becomes unable to move/cast/attack for the full debuff duration.
- Target plays terrified loop for the duration.
- Knockdown preempts terrified loop.
- Stun and intimidated overlap resolves deterministically.
- Normal hit flinch is suppressed while intimidated.
- Status clears at expiration and locomotion resumes.

## Non-Goals

- Do not implement cleanse in this change.
- Do not alias `INTIMIDATED` to `STUN`.
- Do not add a second combat event path.
- Do not make Unity animation state authoritative for gameplay.
- Do not mirror reusable spell bank Animator states.
- Do not hand-edit generated bindings as source-of-truth code.
- Do not move melee timing or combat-profile identity out of `CombatAnimationSet`.

## Stop Conditions

The implementation is complete when:

- `INTIMIDATED` exists as a distinct server status kind.
- Hard crowd control behavior is centralized and includes `STUN`, `INTIMIDATED`, `STAGGER`, and `KNOCKDOWN`.
- `INTIMIDATE` is a data-authored Warrior spell/ability, not a bespoke fake projectile or stun alias.
- Client presentation treats `INTIMIDATED` as hard control and plays the terrified loop.
- The point cast animation is left-handed without mirroring unrelated spell actions.
- Server tests pass.
- Unity compiles and the animation set/controller validation is clean.
