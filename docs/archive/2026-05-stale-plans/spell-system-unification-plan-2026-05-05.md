# Spell System Unification Plan - 2026-05-05

## Context For The Implementing Agent

This plan tracks the spell/ability unification work that started from Warrior `INTIMIDATE` and exposed broader authoring problems. As of May 6, 2026 the main implementation is complete; this document now records the current contract and the remaining follow-ups, not a fresh plan to execute from scratch.

The plan supersedes the spell-behavior decision in [docs/archive/2026-05-stale-plans/combat-intimidate-crowd-control-plan-2026-05-05.md](archive/2026-05-stale-plans/combat-intimidate-crowd-control-plan-2026-05-05.md). That older plan proposed adding a one-off `TARGET_STATUS` behavior. The user agreed to a broader generalization (`APPLY_STATUS`) instead, plus three follow-on cleanups that the `INTIMIDATE` work exposed:

1. **Step 1 - APPLY_STATUS generalization: complete.** Status application is data-driven enough for Intimidate and shared impact effects.
2. **Step 2 - Ship INTIMIDATE: complete.** Intimidate is shipped.
3. **Step 3 - Move Charge to ability-owned movement delivery: complete.** Charge-like actions are movement abilities, not spells. The shared `SHIELD_CHARGE` spell row and legacy `SpellBehavior::Charge` are gone.
4. **Step 4 - Merge `spells[]` into `abilities[]`: complete.** Runtime spell rows derive from ability rows whose `gameplay.kind == SPELL`; top-level `spells[]` is gone.

Current authoring rule: player-facing combat actions live in `abilities[]`. `gameplay.kind` selects the domain, and `gameplay.delivery` describes the delivery shape for SPELL and MOVEMENT abilities.

### Repo layout you will need

- Server (Rust, SpacetimeDB module): `server/src/`
  - `combat.rs` (status types, movement modifiers, has_active_disabling_status)
  - `spells/manifest.rs` (SpellBehavior enum, SpellDefinition, secondary tunables)
  - `spells/catalog.rs` (JSON loader, validators, SpellCatalogDelivery, BespokeRuntimeSpell budget)
  - `spells/casting.rs` (process_spell_cast dispatch, cast_self_buff, spawn_X bespoke functions)
  - `spells/mod.rs` (table definitions, cast_request reducer)
  - `progression.rs` (abilities/loadouts/presentations validation, action_presentation_catalog)
  - `progression_catalog.shared.json` (data: spells, abilities, presentations, loadouts, slots, fixed_action_bindings)
- Client (Unity, C#): `Assets/Arena/Runtime/`
  - `Combat/GameplayContracts.cs` (SpellIds constants, ActionPresentation resolver)
  - `Entity/EntityRegistry.cs` (StatusEffect callbacks, RefreshStatusPresentation, HasSuppressingReactionStatus)
  - `Entity/PlayerEntity.cs` (`SetHardCrowdControl`, `SetKnockedDown`, `ApplyStatusEffect` tinting)
  - `Presentation/PlayerAnimator.cs` / `Presentation/CombatStatusReactionController.cs` (Animator parameter wiring, `slot_hard_crowd_control_loop` override, `slot_spell_N` bank)
  - `Presentation/Animation/CombatAnimationSet.cs` (asset shape: spells[], stunStart/Loop/End, etc.)
  - `Editor/CombatAnimationSetEditor.cs` (inspector for the asset)
  - `Editor/CombatAnimatorControllerInventory.cs` (declares which Animator parameters/states are owned vs legacy)
  - `Generated/SpacetimeDB/` (auto-generated SpacetimeDB bindings; regenerate after server schema changes)
- Combat profile assets: `Assets/Arena/Resources/CombatAnimationSets/*.asset` (TwoHandedSword.asset, SwordAndShield.asset, ArcherBow.asset)
- Mixamo source FBX for Intimidate: `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/{point.fbx, terrified.fbx}`. Their `.meta` files are present.

### Build & validation commands

- Server tests: `cargo test --manifest-path server/Cargo.toml`
- Server build only: `cargo build --manifest-path server/Cargo.toml`
- SpacetimeDB binding regeneration after server schema changes:
  `spacetime generate --yes --lang csharp --module-path server --out-dir Assets/Arena/Runtime/Generated/SpacetimeDB`
- Unity editor validation: open the project; the build-blocking edit-mode test gate ([Assets/Arena/Editor/BuildBlockingEditModeTestGate.cs](Assets/Arena/Editor/BuildBlockingEditModeTestGate.cs)) runs the C# tests on save. The Rust test suite includes catalog/animation-set cross-validation that will fail loudly if e.g. a SPELL ability has no animation entry.

### Hard rules established by the user

- No emojis in code or docs unless explicitly requested.
- Don't add comments unless the WHY is non-obvious.
- Bespoke runtime spell budget cap (`BespokeRuntimeSpell::ALL.len() == 5`) is policed by [server/src/spells/catalog.rs:1170-1194](server/src/spells/catalog.rs#L1170-L1194). Do not grow it. Anything that would touch that test is a smell - generalize instead.
- Do not hand-edit generated bindings under `Assets/Arena/Runtime/Generated/SpacetimeDB/` except for the established workaround pattern (if any exists - none documented as of this writing).
- Do not regenerate or flatten existing `CombatAnimationSet` assets - edit them surgically.
- Server is authoritative for status truth. Client status presentation is derived; never let client animation state become the gameplay record.

### Key system facts you should not have to re-derive

- `StatusEffectKind` enum includes `Root, Stun, Stagger, Knockdown, Intimidated, Slow, Dot, Hot, MoveSlowImmunity, DamageAmp, DirectDamageAmp, HealingTakenReduction, MeleeAttackModifier, AttackSpeed, CastSpeed`.
- `StatusPayload` carries the per-kind data and encodes to four sparse columns (`slow_pct`, `tick_amount`, `tick_interval_ms`, `modifier_scalar`) on the `status_effect` table ([combat.rs:1213-1437](server/src/combat.rs#L1213-L1437)).
- `EffectPacket::ApplyStatus` ([combat.rs:1508-1518](server/src/combat.rs#L1508-L1518)) is the only path that inserts status effects. `queue_effect` validates payloads via `is_invalid` and rejects bad rows.
- `SpellBehavior` today: `Projectile, Area, InstantBeam, Channel, ApplyStatus, SelfResource`. Legacy spell Charge authoring has been removed; Charge-like actions use ability-owned `gameplay.delivery`.
- Bespoke spells: `InstantBeam, Electrocute, Meteor, FrostNova, Negate` ([spells/manifest.rs:14-52](server/src/spells/manifest.rs#L14-L52)).
- Cast lifecycle: `cast_request` (reducer) -> `cast_spell` -> `cast_spell_for` (universal validation) -> `process_spell_cast(...Execute)` for instants, or `begin_active_cast` + `tick_active_casts` -> `finish_active_cast` -> `process_spell_cast(...Execute)` for cast-time spells.
- Hard-CC classification: `has_active_disabling_status(ctx, target, now)` returns true for `Stun`, `Intimidated`, `Stagger`, and `Knockdown`. Five callers: `melee.rs`, `defense.rs`, `auto_attack.rs`, `movement_actions.rs`, `spells/casting.rs`. `MovementModifiers::is_disabled` uses the same hard-CC classification. `ROOT` blocks movement but is NOT a disabling status today.
- Animator hard-CC wiring: `IsHardCrowdControlled` bool + `TriggerHardCrowdControl` trigger + `Base Layer/HardCrowdControlLoop` state + `slot_hard_crowd_control_loop` override slot ([CombatAnimatorControllerInventory.cs:72-134](Assets/Arena/Editor/CombatAnimatorControllerInventory.cs#L72-L134); [CombatStatusReactionController.cs](Assets/Arena/Runtime/Presentation/CombatStatusReactionController.cs)). Single clip path - any new hard-CC reuses it with a per-status clip swap.
- Spell bank: 4 reusable Animator states (`SpellAction1..4`) + 4 override slots (`slot_spell_1..4`), bound dynamically per cast in `PlaySpellAnimation` ([PlayerAnimator.cs:1529](Assets/Arena/Runtime/Presentation/PlayerAnimator.cs#L1529)). The plan must NOT mirror these states statically.
- Validators in `progression.rs`:
  - `SelectableSpellHasAnimationEntry` ([progression.rs:3900-3908](server/src/progression.rs#L3900-L3908)) - every SPELL ability whose action_id is not a fixed action must have a matching `spells[]` entry in the class's combat profile's `CombatAnimationSet` asset.
  - `PlayerFacingActionHasPresentation` ([progression.rs:3963-3971](server/src/progression.rs#L3963-L3971)) - must have an ABILITY presentation row.
  - SPELL/FIXED presentation rows are referenced but not strictly required for validator success.
- `action_presentation_catalog` is keyed by `KIND:ID` ([progression.rs:3373-3377](server/src/progression.rs#L3373-L3377), [GameplayContracts.cs:1023-1024](Assets/Arena/Runtime/Combat/GameplayContracts.cs#L1023-L1024)). Display lookups in C# split across `ResolveAbilityDisplayName` (uses ABILITY:id) and `ResolveDisplayName` (uses SPELL:id then falls back). This split is part of why the spells/abilities cleanup matters.
- Default loadout assignments: `slot_1_7` is open for Warrior. Slot row exists at [progression_catalog.shared.json:1767-1775](server/src/progression_catalog.shared.json#L1767-L1775).

---

## Step 1 - APPLY_STATUS Generalization

### Why

Status application originally existed in five different shapes:

1. `SelfBuff` used `SelfBuffStatusDefinition { kind, modifier_scalar, max_stacks, stack_policy }` and a `duration_seconds` on the row, restricted to a small allowlist of buff kinds.
2. Projectile, area, and legacy charge impact effects each had their own closed enum.
3. Movement delivery previously authored `arrival_effects`, then bridged those into charge-only impact effects.
4. Runtime spell execution had per-delivery helpers for the same status concepts.
5. Bespoke runtime functions (`spawn_frost_nova` for Root, `spawn_meteor` for Stun) hardcode their own status payloads.

Adding INTIMIDATE under the current model requires a sixth shape. That's the trigger for unifying.

The unification: every status-applying authoring point uses one shared shape, `StatusApplication`, that maps cleanly onto `StatusPayload` at runtime. Adding any new status authoring becomes a data row, not a new code path.

This step also introduces `SpellBehavior::ApplyStatus` with a `targeting_mode` field, subsuming `SelfBuff` and adding the targeted-single-target case (`INTIMIDATE`'s shape). `SelfResource` stays separate (it's a resource grant, not a status).

The examples in this section describe the status and spell-delivery shape. After Step 4, those fields live under an ability row's `gameplay` block rather than in a separate top-level spell row.

### Historical Pre-Edit Checks

These checks were useful before Step 1 landed; they are retained only as context for what changed.

```bash
# Confirm the closed enums and budget cap still match what's described above:
grep -n "ImpactEffect\|SelfBuffStatusDefinition" server/src/spells/manifest.rs
grep -n "BespokeRuntimeSpell::ALL" server/src/spells/catalog.rs
grep -n "fn cast_self_buff\|cast_self_resource" server/src/spells/casting.rs
```

### Target shape

In `server/src/spells/manifest.rs` (or a new `server/src/spells/status_application.rs` if you prefer):

```rust
#[derive(Clone, Debug, PartialEq)]
pub(crate) struct StatusApplication {
    pub kind: StatusEffectKind,
    pub duration: Duration,
    pub stack_group: Option<String>,
    pub max_stacks: u32,        // default 1
    pub stack_policy: StackPolicy, // default Refresh
    pub payload_data: StatusPayloadData,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub(crate) enum StatusPayloadData {
    Empty,                                                      // Stun, Knockdown, Stagger, Root, MoveSlowImmunity, MeleeAttackModifier, Intimidated
    Slow { slow_pct: f32 },
    Periodic { tick_amount: i32, tick_interval: Duration },     // Dot, Hot
    Modifier { scalar: f32 },                                   // DamageAmp, DirectDamageAmp, HealingTakenReduction, AttackSpeed, CastSpeed
}

impl StatusApplication {
    pub fn into_payload(&self) -> StatusPayload { /* dispatch by kind, validate payload_data shape */ }
}
```

JSON authoring shape (deserialized into `StatusApplication`):

```json
{ "kind": "INTIMIDATED", "duration_ms": 4000 }
{ "kind": "DOT", "duration_ms": 10000, "tick_interval_ms": 1000, "tick_damage": 3 }
{ "kind": "DAMAGE_AMP", "duration_ms": 8000, "modifier_scalar": 0.5, "stack_group": "BLOOD_RAGE", "max_stacks": 3, "stack_policy": "ADD_STACK_REFRESH" }
{ "kind": "STUN", "duration_ms": 5000 }
```

The validator must reject:
- `payload_data` shape that doesn't match `kind` (e.g. `tick_damage` on `Stun`)
- duration <= 0
- modifier_scalar non-finite or out of bounds for the kind
- unknown `kind` (already handled by serde tagged enum)

New delivery catalog row:

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
  "delivery": {
    "kind": "APPLY_STATUS",
    "targeting_mode": "TARGET",
    "max_distance": 20.0,
    "block_behavior": "UNBLOCKABLE",
    "parry_behavior": "UNPARRYABLE",
    "status": { "kind": "INTIMIDATED", "duration_ms": 4000 }
  }
}
```

Existing SELF_BUFF rows migrate to:

```json
{
  "kind": "MOMENTUM",
  ...
  "delivery": {
    "kind": "APPLY_STATUS",
    "targeting_mode": "SELF",
    "duration_seconds": 4.0,
    "status": {
      "kind": "MOVE_SLOW_IMMUNITY",
      "duration_ms": 4000,
      "stack_group": "MOMENTUM"
    }
  }
}
```

Note: `duration_seconds` outside the status block is redundant once duration moves into `status`. Pick one home for it - the status block is the right home for status duration. Keep `cast_time_ms` etc. at the top level (those are cast properties, not status properties).

### Files changed

These bullets describe the Step 1 implementation that landed, not remaining work.

Server:

- `server/src/spells/manifest.rs`
  - Add `SpellBehavior::ApplyStatus` variant.
  - Add `ApplyStatusSecondaryTunables { targeting_mode: ApplyStatusTargetingMode, max_distance: f32, block_behavior, parry_behavior, status: StatusApplication }`.
  - Add `enum ApplyStatusTargetingMode { Self_, Target }` (start with these two; add `Aoe` later if needed).
  - Add `StatusApplication` and `StatusPayloadData` per above.
  - Replace the projectile/area/charge-specific impact effect enums with a single shared `ImpactEffect` runtime shape.
  - Add `SpellSecondaryTunables.apply_status: Option<ApplyStatusSecondaryTunables>`.
  - Deprecate `SelfBuffStatusDefinition` (or delete, depending on appetite).

- `server/src/spells/catalog.rs`
  - Add `SpellCatalogDelivery::ApplyStatus { targeting_mode, max_distance, block_behavior, parry_behavior, status: StatusApplicationRow }` variant.
  - Add `StatusApplicationRow` for JSON shape (with `kind`, `duration_ms`, optional `stack_group`, optional `max_stacks`, optional `stack_policy`, plus payload-kind-specific optional fields like `tick_interval_ms`, `tick_damage`, `slow_pct`, `modifier_scalar`).
  - Update projectile/area/charge impact effect rows to a single shared catalog shape. Three closed enums become one impact-effect row type.
  - Update `into_definition()` to populate the new shape for `SelfBuff` rows that currently exist (back-compat shim while you migrate the JSON), then delete the shim once JSON is migrated.
  - Update `validate_definition` and `validate_secondary_tunables` to validate `ApplyStatus` and the per-payload data shape match.
  - Remove `validate_self_buff_status` once SELF_BUFF rows are gone.

- `server/src/spells/casting.rs`
  - Add `cast_apply_status(ctx, caster, state, spell_kind, target_id)` that:
    - Resolves target if `targeting_mode == Target`.
    - Validates: target exists, alive, not self, same world context, within facing arc, line of sight, within `max_distance`.
    - Emits `COMBAT_CAST` (caster animation) and `COMBAT_IMPACT` (target reaction) - copy the event-emission shape from `spawn_frost_nova` ([casting.rs:2570](server/src/spells/casting.rs#L2570)) which is the closest precedent that emits both.
    - Queues `EffectPacket::ApplyStatus { payload: StatusApplication.into_payload(), polarity: Debuff (Target) | Buff (Self), ... }`.
    - For `targeting_mode == Self`, no target validation; use caster as target and polarity Buff.
  - In `process_spell_cast`, dispatch `SpellBehavior::ApplyStatus` to `cast_apply_status` and remove the `SelfBuff` arm.
  - Delete `cast_self_buff`.
  - Update charge/projectile/area impact resolution to consume the unified `StatusApplication` shape. Look for the call sites that create `EffectPacket::ApplyStatus { payload: StatusPayload::Stagger, ... }` etc. - they all become `StatusApplication.into_payload()`.

- `server/src/progression_catalog.shared.json`
  - Migrate `MOMENTUM`, `GIANT_SWING` to `APPLY_STATUS` + `targeting_mode: SELF`.
  - Migrate `FIREBALL`'s `delivery.impact_effects` from `BURN` to a generic `APPLY_STATUS` with `kind: DOT`.
  - Migrate `METEOR`'s impact effects (STUN, STAGGER) similarly.
  - Migrate movement-delivery arrival effects that were bridged into charge impact effects (STUN).
  - Migrate any other `Stagger` impact effects in projectile rows.

- `server/src/spells/mod.rs`
  - The `SpellDefinition` SpacetimeDB row may need new public columns for `apply_status` tunables (mirrors `secondary` tunables that are already on the row). Check what's currently published; many secondary tunables already have flat columns on `SpellDefinition` (see `sync_spell_definitions` at [mod.rs:257-302](server/src/spells/mod.rs#L257-L302)). Decide if `apply_status` data needs to be on the public table; if clients don't need it, leave it server-only.

Tests to add (`server/src/spells/catalog.rs`, `server/src/spells/manifest.rs`, `server/src/combat.rs`):

- StatusApplication round-trip: every `StatusEffectKind` produces a valid `StatusPayload`.
- StatusApplication invalid shape rejected: e.g. `{ "kind": "STUN", "tick_damage": 5 }`, `{ "kind": "DOT", "modifier_scalar": 0.5 }`.
- APPLY_STATUS catalog parse: SELF and TARGET targeting modes, with and without optional fields.
- APPLY_STATUS validator: missing `max_distance` for TARGET fails; missing `status` block fails; status kind not authorable from JSON (e.g. unknown) fails.
- SELF_BUFF -> APPLY_STATUS migration test: parsing existing-shape data should fail (since you removed the variant) and parsing new-shape data should succeed and reproduce the same `StatusPayload`.
- Order-preserving catalog test ([catalog.rs:912-936](server/src/spells/catalog.rs#L912-L936)) still passes (update if you reorder rows).
- Bespoke runtime spell budget unchanged: `BespokeRuntimeSpell::ALL.len() == 5` still holds.

### Risks and rollback

- The largest risk is silently breaking `MOMENTUM`/`GIANT_SWING` for players. Manual playtest after migration to confirm both still apply their buffs and consume rage correctly.
- Burn DoT migration: confirm `FIREBALL`'s burn still ticks for the same total damage. The existing `Burn { duration: 10s, tick_interval: 1s, tick_damage: 3 }` should produce 10 ticks of 3 damage; `APPLY_STATUS` with `kind: DOT, duration_ms: 10000, tick_interval_ms: 1000, tick_damage: 3` should match.
- Historical fallback, now resolved: if `SpellSecondaryTunables` had more callers than expected, this step could have kept `Projectile/Area/Charge` impact effect types unchanged and only done SelfBuff -> ApplyStatus. The implementation did not take that fallback; movement delivery now authors shared `impact_effects`.

### Validation

```bash
cargo test --manifest-path server/Cargo.toml
```

Plus manual playtest: cast `MOMENTUM` (slow immunity buff), cast `GIANT_SWING` (melee modifier), cast `FIREBALL` (DoT), confirm same gameplay behavior as before the refactor.

---

## Step 2 - Ship INTIMIDATE

### Why

This is the original feature. With Step 1 done, the spell side is one catalog row. The remaining work is animation authoring, client status presentation, and progression wiring.

### What this plan supersedes from the older INTIMIDATE plan

The older plan ([docs/archive/2026-05-stale-plans/combat-intimidate-crowd-control-plan-2026-05-05.md](archive/2026-05-stale-plans/combat-intimidate-crowd-control-plan-2026-05-05.md)) proposed adding a one-off `TARGET_STATUS` behavior. That section is superseded by Step 1's `APPLY_STATUS`. The rest of the Intimidate work has shipped; these older-plan sections now describe completed implementation areas:

- "Add `INTIMIDATED` As A Real Status Kind" - completed with `StatusEffectKind::Intimidated`, `StatusPayload::Intimidated`, encode/decode.
- "Centralize Hard Crowd Control Classification" - completed; `INTIMIDATED` is hard CC alongside Stun/Stagger/Knockdown.
- "Client Status Presentation Should Become Hard-CC Aware" - completed; `EntityRegistry`, `PlayerEntity`, and `PlayerAnimator` expose `SetHardCrowdControl(statusKind)` and use `KNOCKDOWN > STUN > INTIMIDATED` priority. Controller wiring uses `IsHardCrowdControlled` / `TriggerHardCrowdControl` / `slot_hard_crowd_control_loop`.
- "Extend CombatAnimationSet For Status Reactions" - completed with `StatusReactionAnimationEntry[] statusReactions` and status reaction lookup.
- "Author Intimidate Spell Presentation" and "Left-Hand Point Implementation" - completed through the Intimidate animation authoring path and `TwoHandedSword.asset`.
- "Imported Animation Asset Setup" - completed; `point.fbx`, `terrified.fbx`, and their `.meta` files are present.

### Corrections Applied From The Older Plan

These issues from the older plan were handled during implementation:

1. **Action presentation: ONE row is required, not two.** The validator (`PlayerFacingActionHasPresentation`) only requires `ABILITY:WARRIOR_INTIMIDATE`. A `SPELL:INTIMIDATE` row gives you a prettier display in the cast bar (and is conventional in the existing data) but is not required. Author the SPELL row for consistency; do not treat it as load-bearing.

2. **Hard-CC client refresh: widen `HasSuppressingReactionStatus` too.** [Assets/Arena/Runtime/Entity/EntityRegistry.cs:862-878](Assets/Arena/Runtime/Entity/EntityRegistry.cs#L862-L878) does its own scan for stun/knockdown to suppress hit reactions. Easy to miss because it's a sibling of `RefreshStatusPresentation`. Both functions need to use the same hard-CC predicate.

3. **Atomic clip swap before trigger.** When `RefreshStatusPresentation` decides which hard-CC clip to use, the override-controller write for `slot_hard_crowd_control_loop` must happen BEFORE setting `IsHardCrowdControlled`/`TriggerHardCrowdControl`. Otherwise the Animator can pick up the old clip on the same frame the trigger fires. The cleanest shape is a single `SetHardCrowdControl(string? statusKind)` that does override write then trigger pulse atomically.

4. **`is_hard_crowd_control_kind` should NOT be a method on `MovementModifiers`.** That struct stores per-kind `HashSet<Identity>` populated by separate scans. The cleanest refactor collapses `stunned`/`staggered`/`knocked_down`/(new `intimidated`) into one `disabled: HashSet<Identity>` populated by a single scan that uses the helper. Check callers of the per-kind accessors first - some code in `melee.rs` etc. may rely on the distinction.

5. **`mark_harmful_targeted_spell_start` early-returns for zero-damage spells.** [casting.rs:644-662](server/src/spells/casting.rs#L644-L662) skips when `definition.damage <= 0`. INTIMIDATE has zero damage. Confirm during implementation that the apply-status path on the consumer side (`pending_apply_status` -> `status_effect`) marks combat engagement somewhere downstream. If it doesn't, add an explicit `mark_harmful_combat_action` call inside `cast_apply_status` for `targeting_mode == Target` + `polarity == Debuff`.

6. **Reuse one hard-CC loop slot, do not add a parallel Animator path.** The `IsHardCrowdControlled` parameter and `slot_hard_crowd_control_loop` slot ARE the hard-CC presentation path. Keep the Animator graph from gaining gameplay meaning (no new IsIntimidated bool, no separate state).

7. **Cast cancellation flows automatically.** `tick_active_casts` ([casting.rs:943](server/src/spells/casting.rs#L943)) uses `has_active_disabling_status` to interrupt active casts. Once you route that through `is_hard_crowd_control_kind`, INTIMIDATED automatically interrupts the target's active casts. Same for `cast_spell_for`'s entry check ([casting.rs:369](server/src/spells/casting.rs#L369)).

### Files changed

These bullets describe the Step 2 implementation that landed, not remaining work.

Server:

- `server/src/combat.rs`
  - Add `StatusEffectKind::Intimidated` (wire string `"INTIMIDATED"`).
  - Add `StatusPayload::Intimidated` (empty payload like Stun).
  - Update `as_str`, `from_wire`, `kind`, `encode`, `decode`, `is_invalid`, `is_stronger_than_status` to cover it.
  - Add `pub fn is_hard_crowd_control_kind(kind: StatusEffectKind) -> bool` returning true for `Stun | Intimidated | Stagger | Knockdown`.
  - Refactor `MovementModifiers`: collapse `stunned`/`staggered`/`knocked_down` into one `disabled: HashSet<Identity>` populated by a single scan that uses `is_hard_crowd_control_kind`. Update `is_disabled`, `blocks_movement`, and any callers (grep for `is_stunned`, `is_staggered`, `is_knocked_down`).
  - Update `has_active_disabling_status` to use `is_hard_crowd_control_kind`.
  - Add round-trip tests for `StatusPayload::Intimidated` and movement-modifier tests proving INTIMIDATED blocks movement and casts like Stun.

- `server/src/progression_catalog.shared.json`
  - Add `WARRIOR_INTIMIDATE` ability row with `gameplay.kind: SPELL` and Step 1's `APPLY_STATUS` shape (see Step 1 example above).
  - Add ABILITY presentation row for `WARRIOR_INTIMIDATE` (display_name "Intimidate", description per design).
  - Add SPELL presentation row for `INTIMIDATE` (same display_name, mostly for cast-bar display).
  - Add default loadout assignment: `class_id: WARRIOR`, `slot_id: slot_1_7`, `ability_id: WARRIOR_INTIMIDATE`, `sort_order: 170`.

Client:

- `Assets/Arena/Runtime/Combat/GameplayContracts.cs`
  - Add `public const string Intimidate = "INTIMIDATE";` to `SpellIds`.
  - If you add a `StatusKinds` constants class while you're in there, do it; otherwise the string lives in EntityRegistry.

- `Assets/Arena/Runtime/Entity/EntityRegistry.cs`
  - Add `IntimidatedStatusKind` constant alongside the existing `StunStatusKind`/`KnockdownStatusKind`.
  - Refactor `RefreshStatusPresentation` to return a `(hardControlKind, isKnockedDown)` decision. Knockdown wins; otherwise pick the highest-priority hard-CC kind among Stun/Intimidated (deterministic, e.g. STUN > INTIMIDATED).
  - Refactor `HasSuppressingReactionStatus` to use the same predicate (any hard-CC suppresses hit reactions).
  - Call `entity.SetHardCrowdControl(string? kind)` for the hard-CC loop decision.

- `Assets/Arena/Runtime/Entity/PlayerEntity.cs`
  - Use `SetHardCrowdControl(string? kind)`. Keep the underlying tint logic if useful (or extend `EffectColors` with INTIMIDATED).
  - Forward to `_animator?.SetHardCrowdControl(kind)`.

- `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs` / `Assets/Arena/Runtime/Presentation/CombatStatusReactionController.cs`
  - Use `SetHardCrowdControl(string? kind)`. Implementation:
    - If kind is null, clear: `_animator.SetBool(IsHardCrowdControlledHash, false)` and `_animator.ResetTrigger(TriggerHardCrowdControlHash)`.
    - If kind is non-null, resolve the loop clip from `_animationSet` via the new status-reaction resolver (see CombatAnimationSet edit below).
    - Write the resolved clip into `_overrideController["slot_hard_crowd_control_loop"]` BEFORE setting the bool/trigger.
    - Set `IsHardCrowdControlledHash` true; pulse `TriggerHardCrowdControlHash` only on transitions (track the previous kind).
    - Knockdown precedence already lives in this method ([PlayerAnimator.cs:2669-2673](Assets/Arena/Runtime/Presentation/PlayerAnimator.cs#L2669-L2673)) and should keep working.
  - On hard-CC clear, restore the slot to the default `set.stunLoop` so a later stun doesn't inherit the intimidated/terrified clip.
  - Document in `CombatAnimatorControllerInventory.cs` that `IsHardCrowdControlled`/`TriggerHardCrowdControl`/`Base Layer/HardCrowdControlLoop` are the hard-CC-loop path.

- `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs`
  - Add `[Serializable] public struct StatusReactionAnimationEntry { public string statusKind; public AnimationClip? loop; }`.
  - Add `public StatusReactionAnimationEntry[] statusReactions = Array.Empty<...>();` next to `spells[]`.
  - Add `public bool TryGetStatusReactionLoop(string statusKind, out AnimationClip clip)` that normalizes and matches case-insensitively (mirror `TryGetSpellAnimation`).
  - Resolution rule: if the requested status is `STUN` and no entry exists, fall back to `stunLoop`. If the requested status is `INTIMIDATED` and no entry exists, log a warning and return false (do not silently fall back to stunLoop - that hides the missing asset).

- `Assets/Arena/Editor/CombatAnimationSetEditor.cs`
  - Expose `statusReactions` in the inspector with a list editor.

- `Assets/Arena/Resources/CombatAnimationSets/TwoHandedSword.asset`
  - Add a `spells[]` entry: `spellId: INTIMIDATE`, `ground` referencing the left-hand mirrored point clip, `air` optional, `requiresCombatStance: true`, `combatEntryMode: AnimatedAfterCast`, `playbackLayer: LeftGesture`, `groundEffectTime` chosen from clip review.
  - Add a `statusReactions[]` entry: `statusKind: INTIMIDATED`, `loop` referencing the terrified clip.
  - Do not regenerate or flatten this asset - edit surgically.
  - Do NOT add this entry to `SwordAndShield.asset` in V1.

- `Assets/ThirdParty/AssetStore/Animation/Mixamo/Animations/Humanoid/{point.fbx, terrified.fbx, point.fbx.meta, terrified.fbx.meta}`
  - Import both as Humanoid in Unity. Verify avatar mapping.
  - Verify `terrified` loops cleanly (it must hold for the full debuff duration).
  - Create a left-hand mirrored variant of `point` (recommended name: `point_left`). Either via Unity's per-clip mirror flag in the import inspector, or by exporting a real mirrored clip. The mirror MUST live on the clip, not on the reusable Animator spell-bank state.
  - Commit all `.meta` files. They are the GUID source of truth for serialized asset references.

- `Assets/Arena/Runtime/Generated/SpacetimeDB/`
  - Regenerate after the server `StatusEffectKind`/`SpellBehavior` changes. See the spacetime-generate memory note for the exact command.

### Tests

Server:

- `Intimidated` round-trips through `StatusPayload::encode`/`decode`.
- `is_hard_crowd_control_kind(Intimidated)` is true; `Root` is false.
- `MovementModifiers::is_disabled` returns true for an INTIMIDATED target.
- `cast_apply_status` rejects out-of-range targets, missing targets, dead targets, self.
- `cast_apply_status` queues an `EffectPacket::ApplyStatus` with `polarity: Debuff` for TARGET mode and `Buff` for SELF mode.
- `cast_apply_status` does not consult block/parry mitigation when behavior is UNBLOCKABLE/UNPARRYABLE.
- Catalog parse: INTIMIDATE row loads, delivery is APPLY_STATUS + TARGET, status kind is INTIMIDATED, duration is 4000ms.
- Progression: WARRIOR_INTIMIDATE has an ABILITY presentation; combat profile validation passes (TwoHandedSword animation set has the spell entry).

Client:

- (No automated UI tests are practical here.) Manual playtest:
  - Warrior casts INTIMIDATE on a target within 20m. Cast accepted.
  - Cast outside 20m rejects. Cast with no target rejects.
  - Caster plays the left-hand point clip.
  - Target is unable to move/cast/attack for 4s.
  - Target plays the terrified loop for the full 4s.
  - Knockdown applied during INTIMIDATED preempts the terrified loop with knockdown.
  - Stun applied alongside INTIMIDATED resolves to STUN (priority).
  - Normal hit flinch is suppressed while INTIMIDATED.
  - Status clears at expiration; locomotion resumes.

### Validation

```bash
cargo test --manifest-path server/Cargo.toml
```

Plus Unity editor validation pass and the manual playtest above.

---

## Step 3 - Move Charge To Ability-Owned Movement Delivery

### Why

Design call from the user: every class may have charge-like ranged movement abilities, but each one should tune independently (range, resource cost, cooldown, possible extra effect). The currently shared `SHIELD_CHARGE` spell row contradicts that intent and also preserves the wrong abstraction.

Charge is not fundamentally a spell. It is movement delivery plus arrival effects. One arrival effect can be a melee strike, another can be a status application, another can be damage, displacement, shielding, or nothing. Do not replace `SpellBehavior::Charge` with a melee-only `GapCloserStrike` bucket; that would bake in one outcome and fail again when the next non-strike movement ability arrives.

### Current state after this step

- `server/src/progression_catalog.shared.json`:
  - No authored spell row `SHIELD_CHARGE`.
  - `WARRIOR_CHARGE` and `PALADIN_CHARGE` are `gameplay.kind: MOVEMENT` rows with per-ability `gameplay.delivery`.
  - Default loadouts assign `WARRIOR_CHARGE` and `PALADIN_CHARGE` directly as selectable abilities.
  - Action presentations are ability-owned; the old SPELL `SHIELD_CHARGE` presentation row is gone.
- `Assets/Arena/Runtime/Combat/GameplayContracts.cs`: the `ShieldCharge` constant is gone.
- Animation: charge clips live as named slots on `CombatAnimationSet` (`chargeStart`/`chargeLoop`/`chargeEnd` and air variants), already per-combat-profile, NOT in `spells[]`. So animation is already class-tunable.
- Charge is no longer a fixed action. Selectable movement abilities dispatch through `CastRequest`; the server detects `gameplay.kind: MOVEMENT`, reads `gameplay.delivery`, and enters the generic movement-delivery launch path. It no longer derives runtime `SpellDefinition` rows for `WARRIOR_CHARGE` or `PALADIN_CHARGE`.

### Target

Ability rows own special movement delivery:

```jsonc
{
  "ability_id": "WARRIOR_CHARGE",
  "class_id": "WARRIOR",
  "action_id": "WARRIOR_CHARGE",
  "gameplay": {
    "kind": "MOVEMENT",
    "delivery": {
      "kind": "DASH_TO_TARGET",
      "cooldown_ms": 1600,
      "uses_global_cooldown": true,
      "cast_time_ms": 650,
      "cast_mobility": "MOBILE",
      "targeting": "TARGET",
      "requires_target": true,
      "speed": 23.0,
      "max_distance": 18.0,
      "damage": 32,
      "radius": 1.25,
      "block_behavior": "BLOCKABLE",
      "parry_behavior": "PARRYABLE",
      "arrival": { "buffer": 0.75, "epsilon": 0.05 },
      "impact_effects": [
        {
          "kind": "APPLY_STATUS",
          "status": { "kind": "STUN", "duration_ms": 5000 }
        }
      ]
    }
  }
}
```

The action bar slot points at the concrete ability id. Charge active-cast/network paths now use `gameplay.delivery` data directly. Spell authoring must not keep a `SHIELD_CHARGE` row and new movement abilities must not add more `SpellBehavior::Charge` rows.

### Files changed

Server:

- `server/src/progression_catalog.shared.json`
  - Delete the shared `SHIELD_CHARGE` spell row.
  - Change `WARRIOR_CHARGE` and `PALADIN_CHARGE` to `gameplay.kind: MOVEMENT`.
  - Give each row its own `gameplay.delivery` block. Start by copying the current `SHIELD_CHARGE` tuning; design can iterate later.
  - Update each charge ability `action_id` to its ability-owned action id (`WARRIOR_CHARGE`, `PALADIN_CHARGE`) rather than `SHIELD_CHARGE`.
  - Delete the SPELL `SHIELD_CHARGE` presentation row unless a runtime compatibility shim still needs a display fallback. ABILITY presentation rows remain the player-facing source.
  - Remove fixed-action `CHARGE` placement and `fixed_action_bindings[]` rows; charge-like movement is placed as ordinary `ABILITY` action refs.

- `server/src/progression.rs`
  - Add `gameplay.kind: MOVEMENT` support.
  - Parse and validate `gameplay.delivery`.
  - Charge-like movement abilities must have `gameplay.delivery.kind == DASH_TO_TARGET`.
  - Do not require movement abilities to resolve to spell catalog rows.

- `server/src/spells/catalog.rs`
  - Remove charge authoring from `spells[]`.
  - Do not derive runtime charge definitions from movement ability rows.

- `server/src/movement_actions.rs` / `server/src/spells/mod.rs` / `server/src/spells/casting.rs`
  - Selectable movement abilities enter through `CastRequest`, then call the generic movement-delivery launcher while still reusing shared active-cast, special movement, cooldown, and combat-event infrastructure.

- `Assets/Arena/Runtime/Combat/GameplayContracts.cs`
  - Remove `ShieldCharge` constant if it's no longer referenced. Grep first - it may be used in charged-release presentation helpers or elsewhere.
  - Movement abilities are selectable `ABILITY` rows, not fixed actions.

- `Assets/Arena/Runtime/Generated/SpacetimeDB/` - no regeneration was needed for this slice because no public table schema changed.

- Tests in `server/src/spells/manifest.rs`, `catalog.rs`, `progression.rs`, and `movement_actions.rs` that hardcode `SHIELD_CHARGE` - update to assert no shared charge spell is authored and charge resolves through ability-owned movement delivery.

- `BespokeRuntimeSpell::ALL` is unaffected.

### Tests

- Catalog still loads and no `SHIELD_CHARGE` spell row exists.
- Both charge ability rows have `gameplay.kind: MOVEMENT` and `gameplay.delivery.kind: DASH_TO_TARGET`.
- Default loadouts assign charge abilities directly as selectable abilities.
- Runtime compatibility definitions, if retained, derive from ability movement data rather than from `spells[]`.
- No `fixed_action_bindings[]` rows are required for charge-like movement abilities.
- Charge mechanic still works in playtest for both classes (manual).

### Validation

```bash
cargo test --manifest-path server/Cargo.toml
```

Plus manual playtest: both classes can charge and the gameplay parameters match the per-class catalog rows.

### Risks

- Removing a spell id is observable across UI, animation references, VFX dispatch tables. Grep for `SHIELD_CHARGE` and `ShieldCharge` across both server and client before deleting.
- `runtime_action_ids_are_normalized_before_progression_matching` and similar tests may have stale references.
- New movement abilities should author `gameplay.kind: MOVEMENT` plus `gameplay.delivery` on ability rows.

---

## Step 4 - Merge `spells[]` Into `abilities[]`

### Why

After Step 3, every player-owned spell-like ability should author gameplay on its ability row. The split table model expresses sharing that no longer exists for those ability-owned spells. The cost is real:

- Authors write each spell's display name twice (in ABILITY presentation and SPELL presentation rows).
- Spell tunables and ability metadata sit in different rows that only make sense together.
- The ability-kind asymmetry persists: MELEE abilities have gameplay data on the ability row; SPELL abilities have it on a sibling row. Same logical concept, two shapes.

Goal: ability rows carry their own gameplay block. The runtime spell catalog is derived at startup from ability rows whose `gameplay.kind == SPELL`.

This step was the largest and most schema-intrusive part of the plan. It landed after the status, Intimidate, and movement-delivery work so the authoring-shape migration did not mix with gameplay behavior changes.

### Current bridge state

Step 4 has landed with the same compatibility pattern used for movement delivery:

- All runtime spell definitions now author their mechanics directly on ability rows under `gameplay`.
- The top-level progression catalog `spells[]` array has been removed; runtime spell rows now derive from `gameplay.kind == SPELL` abilities.
- Ability rows now carry `gameplay.kind`; the old top-level kind field has been removed from `progression_catalog.shared.json`, while the published `AbilityCatalog` table still derives a kind string for client compatibility.
- SPELL cast fields live directly inside `gameplay`, and their spell delivery shape lives in `gameplay.delivery`.
- MOVEMENT execution fields live inside `gameplay.delivery`; there is no separate movement payload field.
- MELEE tuning fields now live directly inside `gameplay` too (`base_damage`, `range`, `cooldown_ms`, parry/block policy, stagger, gap close, impact area). The derived `MeleeAbilityCatalog` table still publishes the same runtime shape.
- `server/src/spells/catalog.rs` derives runtime `SpellDefinition` rows from flattened spell fields under `ability.gameplay`, so existing `cast_request(spell_id)` callers keep working.
- Runtime spell impact effects now use one `ImpactEffect` type instead of projectile/area/charge-specific enums.
- Movement delivery now authors `impact_effects` instead of `arrival_effects`, and charge-like abilities read movement delivery directly rather than deriving runtime charge spell rows.
- Legacy spell Charge authoring has been removed: there is no `SpellBehavior::Charge`, no spell-catalog `delivery.kind: CHARGE`, and no charge secondary tunables. Charge-like actions must author `gameplay.kind: MOVEMENT` plus `gameplay.delivery`.
- Fixed-action Charge has been retired. `StartCharge` remains only as generated-client compatibility; current loadouts dispatch movement abilities through `CastRequest`.
- Public `SPELL:*` presentation rows are derived from SPELL ability gameplay and ABILITY presentations. `presentation_kind: "SPELL"` is no longer authored in `progression_catalog.shared.json`.
- Legacy/system spell ids that were not selectable ability rows now have hidden owner ability rows. They are not assigned to default loadouts and do not require animation-set spell entries until they become player-facing.

### Target shape

```jsonc
{
  "ability_id": "WARRIOR_INTIMIDATE",
  "class_id": "WARRIOR",
  "display_name": "Intimidate",
  "description": "Casts intimidate, locking the target in fear for a brief duration.",
  "resource_kind": "RAGE",
  "ability_tags": ["LOADOUT_ACTION"],
  "sort_order": 95,
  "gameplay": {
    "kind": "SPELL",
    "cooldown_ms": 60000,
    "uses_global_cooldown": true,
    "cast_time_ms": 0,
    "cast_mobility": "MOBILE",
    "targeting": "TARGET",
    "requires_target": true,
    "resource_cost": 0.0,
    "arms_auto_attack_on_cast": true,
    "delivery": {
      "kind": "APPLY_STATUS",
      "targeting_mode": "TARGET",
      "max_distance": 20.0,
      "block_behavior": "UNBLOCKABLE",
      "parry_behavior": "UNPARRYABLE",
      "status": { "kind": "INTIMIDATED", "duration_ms": 4000 }
    }
  }
}
```

MELEE abilities use `gameplay: { kind: MELEE, ... }` with the data that used to sit on the top-level ability row (damage, range, cooldown_ms, parry_behavior, block_behavior, applies_stagger, airborne_targeting_mode, projectile_overrides if present). This is now the catalog contract; the values themselves did not change.

`spells[]` is deleted. The runtime spell catalog (`spell_definition()` in [catalog.rs](server/src/spells/catalog.rs)) is derived at startup by walking ability rows with `gameplay.kind == SPELL`, keyed by the ability `action_id`.

### Why separate ability id and spell action id?

Decoupling `ability_id` (ownership concept: "Warrior's intimidate") from spell action id (mechanic concept: "the INTIMIDATE spell") preserves the existing cast-request API (`cast_request(spell_id)`) and keeps the SpellId validation rule (uppercase, no double-underscore) intact. The `BespokeRuntimeSpell` enum and any test fixture that hardcodes spell ids continue to reference the same string tokens.

If you instead make ability id and spell action id the same string, every player-facing spell would pick up the class prefix (`WARRIOR_INTIMIDATE`), the SpellId namespace stops being class-agnostic, and the bespoke spell tests would all need migration. Not worth it. Keep them separate.

### Files changed

This was the largest scope of the plan. The final implementation contract is:

Server:

- `server/src/progression.rs`
  - `AbilityDefinition` parses a `gameplay` block with a `kind` discriminator.
  - SPELL cast fields live directly in `gameplay`; SPELL delivery lives in `gameplay.delivery`.
  - MOVEMENT execution fields live in `gameplay.delivery`; helpers parse them into `MovementDeliveryRuntime`.
  - MELEE tuning fields live directly in `gameplay` next to `kind`.
  - Combat-authoring validators read from the new shape.
  - The published runtime ability table keeps a derived kind string for client compatibility.

- `server/src/spells/catalog.rs`
  - Runtime spell rows derive by walking `progression_catalog.abilities[]` and extracting rows where `gameplay.kind == SPELL`.
  - Spell ids are unique across derived spell abilities.
  - Non-SPELL abilities may still carry non-spell `gameplay.delivery` shapes; spell catalog parsing only deserializes delivery as `SpellCatalogDelivery` after confirming `gameplay.kind == SPELL`.

- `server/src/spells/manifest.rs`
  - Runtime `SpellDefinition` stays the same; only its authoring source changed.
  - Keep `BespokeRuntimeSpell::ALL` and its budget cap test.

- `server/src/progression_catalog.shared.json`
  - Every spell row's data moved inline onto its corresponding ability row.
  - Every melee ability row moved its existing fields into `gameplay: { kind: MELEE, ... }`.
  - Movement abilities use `gameplay: { kind: MOVEMENT, delivery: { ... } }`.
  - The top-level `spells[]` block is gone.
  - Authored SPELL presentation rows are gone; the server derives public `SPELL:*` rows from spell ability gameplay.

- `server/src/spells/mod.rs`
  - `cast_request(spell_id)` reducer signature stays the same.
  - `sync_spell_definitions` reads from the derived catalog; clients still see the same runtime spell table shape.

Client:

- `Assets/Arena/Runtime/Generated/SpacetimeDB/` - no binding regeneration was needed for the final authoring-shape cleanup because the public runtime table shape did not change.

- `Assets/Arena/Runtime/Combat/ActionTooltipResolver.cs`, `Assets/Arena/Runtime/Combat/GameplayContracts.cs:ActionPresentation`
  - The fallback chain still supports public `SPELL:*` presentation rows, but those rows are now derived server-side.

### Tests

- Catalog loads with new shape. All existing spells still produce identical `SpellDefinition` runtime rows (assert per-spell parity against a snapshot of the pre-migration values).
- Every melee ability still produces identical authoring data (no gameplay change).
- `BespokeRuntimeSpell::ALL` budget unchanged.
- Validators still reject the same misconfigurations they used to (selectable spell missing animation entry, ability kind unsupported, etc.).
- Snapshot test of the full set of derived spell ids: identical before and after.

### Validation

```bash
cargo test --manifest-path server/Cargo.toml
```

Manual playtest: every spell still casts. Every melee ability still hits. Loadout grid still shows the same names and tooltips.

### Risks

- Largest diff of the four steps. Estimate 600-1000 lines of code + JSON.
- Schema change is observable across modules, generated bindings, and live scripts. Land on a clean branch with no other in-flight work.
- Public presentation shape is still compatible; authored SPELL presentation duplication has been removed.

---

## Out Of Scope

- Cleanse mechanics for INTIMIDATED (the system supports them through generic status removal; deliberate design work happens later).
- Combat balance tuning of new spells (data-only iteration after ship).
- A future hard-CC Animator parameter rename. This has been completed: controller wiring now uses `IsHardCrowdControlled` / `TriggerHardCrowdControl`.
- Removing the bespoke runtime spell tier entirely (requires generalizing AOE / Channel / InstantBeam / Projectile travel paths, much bigger).

## Resolved Decisions

1. **APPLY_STATUS scope:** resolved. Impact-effect authoring is shared, and movement delivery authors `impact_effects` directly.
2. **Per-class charge naming:** resolved for now as `WARRIOR_CHARGE` and `PALADIN_CHARGE` action ids with the shared display name "Charge". Flavor renames can stay data-only later.
3. **Step 4 melee data nesting:** resolved. MELEE fields live directly inside `gameplay` next to `kind`, not under a second nested melee object.
4. **Animator parameter rename:** completed. The controller now uses `IsHardCrowdControlled` / `TriggerHardCrowdControl`.

## Completion Checks

The four steps stay complete only if these checks remain true:

- All four `cargo test --manifest-path server/Cargo.toml` runs pass after each step.
- Unity editor validation passes after Step 2 and Step 4. Binding regeneration is only required when the public SpacetimeDB schema changes; the final authoring-shape cleanup kept the public runtime table shape stable.
- Manual playtest after Step 2: every existing spell still works; INTIMIDATE plays its left-hand point clip on the caster, terrified loop on the target, locks the target out for 4s, and resolves cleanly.
- Manual playtest after Step 3: both classes can charge with their per-class tuning.
- After Step 4: every spell still casts; every melee ability still hits; loadout grid is unchanged from the player's perspective; `spells[]` block is gone from `progression_catalog.shared.json`.
