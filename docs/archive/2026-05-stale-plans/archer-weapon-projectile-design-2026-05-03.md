# Archer Weapon Projectile Design

Date: 2026-05-03

## Goal

Most Archer attacks should release fast physical arrows. Arrows should be authoritative gameplay objects, not cosmetic traces and not delayed melee range checks. They should travel through the world, stop on world line-of-sight blockers, and hit the first valid enemy capsule in their swept path, even when that enemy is not the caster's selected target.

This design should support:

- Archer auto attacks and most authored Archer strike animations.
- Selected-target bow shots that can be intercepted by another enemy in the path.
- Fast projectile motion without tunneling through players or world geometry.
- Reuse of the existing spell projectile collision model where it is correct.
- Future non-arrow weapon projectiles without putting targetless projectile delivery back into melee.

## Current System Facts

Relevant code:

- `server/src/spells/casting.rs`
- `server/src/spells/simulation.rs`
- `server/src/spells/scene_query.rs`
- `server/src/spells/collision.rs`
- `server/src/melee.rs`
- `server/src/auto_attack.rs`
- `Assets/Arena/Runtime/Presentation/SpellVFXDispatcher.cs`
- `Assets/Arena/Runtime/Presentation/CombatVFXDispatcher.cs`
- `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs`
- `docs/archive/2026-05-stale-plans/orbit-projectile-spell-migration-plan-2026-05-01.md`

Important observations:

- Spell projectiles already use an authoritative `active_spell` row and swept segment collision through `first_hit_on_segment`.
- `first_hit_on_segment` already checks world collision and player capsules, then returns the earliest hit along the segment.
- Spell casts currently require line of sight to the selected target before spawning targeted projectiles.
- Projectile simulation can already hit a different enemy from the selected target because the sweep checks all alive players in the caster's world context.
- The melee runtime currently emits legacy `SpellEvent` rows for animation/VFX transport with non-spell source labels such as `player_input`, `queued_followup`, `auto_attack`, and `practice`. The table is already a combat event table in practice; the name is stale.
- The Archer seeding plan intentionally treats Archer rows as current generic authored attacks, with true projectile gameplay left as follow-up work.
- The Orbit projectile cleanup explicitly says targetless weapon-shaped projectiles should be added as spell behavior if they become player-facing again, and melee should stay animation-first targeted strike data.

The missing piece is not collision math. The missing piece is an action-runtime contract for weapon attacks whose animation timing releases a projectile instead of resolving a melee impact.

## Design Principle

Add a first-class `PROJECTILE` delivery lane for combat actions. Do not make arrows fake spells for authoring, and do not make them fake melee hits for runtime.

The clean boundary is:

- The Archer attack remains an authored combat-profile strike for animation, combo timing, cooldowns, resource costs, and loadout identity.
- A hit window on that strike can schedule a projectile spawn instead of a direct melee impact.
- Projectile flight, collision, defense, damage, and terminal events are handled by shared projectile runtime code.
- Presentation consumes neutral combat events and can draw arrows, trails, block sparks, impacts, and fizzles without caring whether the source was a spell or a bow strike.

## Proposed Data Model

Add projectile delivery data as an optional part of authored attack gameplay, not as a replacement for combat animation rows.

### Shared Projectile Definition

Create a shared projectile model that spell projectiles and weapon projectiles can both use:

```rust
struct ProjectileDefinition {
    projectile_type_id: String,
    speed: f32,
    max_distance: f32,
    radius: f32,
    spawn_forward: f32,
    spawn_height: f32,
    aim_height_scale: f32,
    turn_rate: f32,
    homing_window_seconds: f32,
    update_interval_seconds: f32,
    max_targets: u32,
    pierce_count: u32,
    block_behavior: BlockBehavior,
    parry_behavior: ParryBehavior,
    collision_policy: ProjectileCollisionPolicy,
}
```

For arrows, V1 should use:

- `speed`: high enough to feel like an arrow, for example 35-55 m/s after playtest.
- `radius`: small but forgiving, for example 0.08-0.15 m.
- `turn_rate`: `0` for normal arrows.
- `homing_window_seconds`: `0` for normal arrows.
- `update_interval_seconds`: correction cadence, not simulation cadence. Start at `0.10` seconds for standard arrows and tune with client snap thresholds.
- `max_targets`: `1`.
- `pierce_count`: `0`.
- `collision_policy`: `WORLD_AND_ENEMY_CAPSULES`.

Spell abilities load this data from `gameplay.delivery` when `gameplay.kind == "SPELL"`, but internally they should convert to the same `ProjectileDefinition`.

### Weapon Projectile Delivery

Extend Archer-capable strike gameplay with optional projectile delivery. Conceptually:

```json
{
  "delivery": {
    "kind": "PROJECTILE",
    "projectile_id": "ARROW_STANDARD",
    "spawn_anchor": "WEAPON_RELEASE",
    "fallback_spawn_height": 1.35,
    "fallback_spawn_forward": 0.65,
    "aim": {
      "kind": "SELECTED_TARGET_AT_RELEASE",
      "height_scale": 0.62,
      "requires_initial_line_of_sight": true,
      "allow_interception": true
    }
  }
}
```

Do not put damage on the projectile definition if damage is action-specific. Keep action damage in the existing ability/auto-attack gameplay rows, then pass the resolved damage into the projectile instance at release time.

### Hit Window Semantics

Today, strike hit windows mean "resolve direct impact at this time." For Archer, the same authoring concept should mean "release arrow at this animation timestamp."

Add a hit-window delivery override:

```json
{
  "impact_delay_ms": 340,
  "delivery": "PROJECTILE"
}
```

If a strike has no projectile delivery, hit windows keep existing melee behavior. If it has projectile delivery, due hit windows spawn projectile instances and do not insert `pending_melee_impact` rows.

This keeps the `CombatAnimationSet` editor timing model usable. Designers still tune the release frame in one place.

## Runtime Tables

Create a neutral active combat projectile table instead of overloading `active_spell`.

```rust
#[table(accessor = active_combat_projectile)]
pub struct ActiveCombatProjectile {
    #[primary_key]
    pub projectile_instance_id: String,
    pub source_kind: String, // SPELL, MELEE_STRIKE, AUTO_ATTACK, AUTO_ATTACK_REPLACEMENT
    pub action_kind: String, // spell id or authored strike id
    pub ability_id: String,
    pub caster: Identity,
    pub intended_target: Identity,
    pub origin_x: f32,
    pub origin_y: f32,
    pub origin_z: f32,
    pub pos_x: f32,
    pub pos_y: f32,
    pub pos_z: f32,
    pub dir_x: f32,
    pub dir_y: f32,
    pub dir_z: f32,
    pub speed: f32,
    pub max_distance: f32,
    pub radius: f32,
    pub traveled: f32,
    pub age: f32,
    pub lifetime: f32,
    pub update_accum: f32,
    pub damage: i32,
    pub parry_behavior: String,
    pub block_behavior: String,
    pub grants_primary_resource_on_hit: bool,
    pub hit_index: u32,
    pub created_at: Timestamp,
}
```

Normal spell projectiles and weapon projectiles should migrate to `active_combat_projectile` within the same epic that introduces it. Two parallel projectile tables are not an acceptable end state. During migration, `active_spell` may continue to exist only for bespoke non-projectile spell runtimes that are not ordinary linear projectile flight.

## Event Contract

Use `CombatEvent` as the authoritative combat event transport. This should be a rename of the current `SpellEvent` concept or a new table that receives all new combat event writes while the legacy table is retired. Archer projectile V1 should not add another source-string convention to the legacy `SpellEvent` table.

Recommended table shape:

```rust
#[table(accessor = combat_event, public)]
pub struct CombatEvent {
    #[primary_key]
    #[auto_inc]
    pub event_id: u64,
    pub action_instance_id: String,
    pub source_kind: String,
    pub action_kind: String,
    pub ability_id: String,
    pub hit_index: i32,
    pub event_type: String, // COMBAT_CAST, COMBAT_UPDATE, COMBAT_IMPACT, COMBAT_FIZZLE, COMBAT_BLOCK, COMBAT_PARRY
    pub caster: Identity,
    pub hit: Identity,
    pub origin_x: f32,
    pub origin_y: f32,
    pub origin_z: f32,
    pub dir_x: f32,
    pub dir_y: f32,
    pub dir_z: f32,
    pub speed: f32,
    pub max_distance: f32,
    pub scalar_kind: String, // "", TRAVEL_DURATION_SECONDS, BEAM_CHARGE_PCT, MELEE_RELEASE_DELAY_SECONDS
    pub scalar_value: f32,
    pub sequence_kind: String, // "", BEAM, or another typed sequence label
    pub sequence_index: u32,
    pub sequence_count: u32,
    pub point_x: f32,
    pub point_y: f32,
    pub point_z: f32,
    pub created_at: Timestamp,
    pub damage: i32,
    pub metadata_kind: String, // "", CONSUMED_MELEE_MODIFIER, or another typed metadata label
    pub metadata_key: String,
    pub metadata_value: String,
}
```

Do not add action-specific column names such as `duration`, `beam_index`, or `consumed_modifier_status_kind` to this table. Optional event payload should go through typed scalar, sequence, or metadata slots unless it grows large enough to justify a dedicated side table.

Event type constants should move from `SPELL_*` names to `COMBAT_*` names during the `CombatEvent` migration. This is a broad generated-binding/client subscription change, but it should happen before Archer arrows ship so projectile events do not calcify the legacy spell naming.

For Archer projectile events:

- `source_kind` is `MELEE_STRIKE`, `AUTO_ATTACK`, or `AUTO_ATTACK_REPLACEMENT`, matching the action path that released the arrow.
- `action_kind` is the authored strike id.
- `action_instance_id` is the projectile/action instance id.
- `speed` and `max_distance` are populated with projectile values.
- `origin` is the authoritative arrow spawn point.
- `point` is the impact, block, parry, or fizzle point.
- `hit` is the actual entity hit, not necessarily the intended target.

Do not encode Archer arrows as `source_kind = SPELL` unless the action is truly a spell. This matters because combat presentation should route spell visuals and weapon projectile visuals differently even though they share event transport.

## Casting And Release Flow

### Selectable Archer Attack

1. Player presses an Archer attack bound through the normal loadout/action bar path.
2. Server validates the authored strike, cooldown, resource, target, facing, and initial range using current melee/action logic.
3. If the strike's delivery is projectile, initial validation uses bow range and line of sight to the intended target.
4. Server emits `COMBAT_CAST` for animation playback.
5. Server schedules a `PendingProjectileRelease` at the authored hit window timestamp instead of `PendingMeleeImpact`.
6. At release time, server snapshots caster and intended target again.
7. If caster is dead, disabled, no longer in world context, or release-specific policy fails, emit a fizzle/cancel event and do not spawn.
8. Compute launch origin and direction.
9. Insert `ActiveCombatProjectile`.
10. Projectile simulation owns all later collision and damage.

### Auto Attack

The existing auto-attack cadence remains useful. Only the due swing behavior changes for Archer's `AUTO_ATTACK_1`:

1. `tick_auto_attacks` still arms and attempts the authored auto attack.
2. `perform_intrinsic_auto_attack_for` starts the Archer quick-shot animation.
3. The quick-shot hit window schedules a projectile release.
4. The next auto attack is scheduled from swing start as it is today.
5. Resource generation happens on confirmed projectile hit, not on release.

This preserves the cadence system while making the actual damage depend on arrow flight.

## Aim And Line Of Sight

Arrows should use two checks with different purposes.

Initial action validation:

- Target must be valid, alive, in world context, and inside facing arc.
- Target must be within authored bow acquisition range.
- If `requires_initial_line_of_sight` is true, the existing `has_line_of_sight` helper should pass.
- This prevents starting shots at enemies fully hidden behind cover.

Projectile flight:

- Once spawned, the arrow does not reserve the selected target.
- It sweeps from previous position to next position each tick.
- `first_hit_on_segment` finds the earliest world or player hit.
- If another enemy enters or stands in the path, that enemy is hit first.
- If cover is in the path, the arrow impacts the world and stops.

This gives the behavior requested: target selection helps choose a shot, but the arrow itself behaves like a physical arrow.

## Spawn Origin And Animation Sync

Server gameplay should not depend on client bone positions. Use deterministic server origin math:

- Base origin: caster authoritative position.
- Vertical offset: projectile definition `spawn_height`.
- Forward offset: projectile definition `spawn_forward`.
- Direction: release-time vector from origin to intended target aim point, or facing direction fallback.

Client presentation can use richer visuals:

- Spawn the visible arrow from a bow/string/muzzle transform when available.
- Reconcile to the authoritative `origin` and `dir` from the spawn event.
- Use fast interpolation so the visual starts at the bow but converges to the server path.

This avoids trusting animated bone state for gameplay while still making the arrow appear to leave the bow.

## Collision And Tunneling

Keep swept segment collision as the mandatory server behavior. Never rely on per-frame overlap at arrow speed.

Required projectile simulator behavior:

- Advance by `speed * dt`.
- Sweep from previous position to next position.
- Use projectile radius as padding for world and capsule casts.
- Ignore caster by default.
- Check only alive enemies sharing world context.
- Pick the smallest positive hit distance.
- Clamp impact point to the actual collision point.
- Delete or update the active projectile exactly once after terminal events.

The existing `server/src/spells/scene_query.rs` already provides the correct shape for this. The refactor should move that helper out of `spells` or expose it as a combat scene query module, because arrows should not import spell-specific names.

## Defense Semantics

Arrows should be defensible as projectiles, not as melee contacts.

Recommended defaults:

- Normal arrows: `block_behavior = BLOCKABLE`, `parry_behavior = PARRYABLE` only if projectile parry is intended to work with the current parry timing.
- Heavy/power arrows: configurable per attack.
- Rain/volley shots: probably `BLOCKABLE` and `UNPARRYABLE` unless the individual incoming arrow can be meaningfully parried.

Defense should resolve when the projectile impacts a player, using source direction, impact point, impact time, and projectile speed. Dedupe the current melee and spell defense paths into a shared combat helper:

```rust
pub(crate) enum CombatHitDeliveryKind {
    Melee,
    Projectile,
    Spell,
    SpellCharge,
}

pub(crate) struct DefensibleCombatHit<'a> {
    pub delivery_kind: CombatHitDeliveryKind,
    pub defender: Identity,
    pub active_from: Timestamp,
    pub active_until: Timestamp,
    pub parry_behavior: &'a str,
    pub block_behavior: &'a str,
    pub source_x: f32,
    pub source_y: f32,
    pub source_z: f32,
    pub impact_x: f32,
    pub impact_y: f32,
    pub impact_z: f32,
    pub dir_x: f32,
    pub dir_y: f32,
    pub dir_z: f32,
    pub speed: f32,
}

resolve_defensible_combat_hit(ctx, hit)
```

The resolver should use impact point first, then source point, then inverse travel direction to determine the defense arc. Projectile hits must carry positive finite speed so later tuning can make projectile parry/block behavior depend on speed without changing the API again.

On block/parry, emit terminal projectile events and do not apply normal damage. On parry, cancel remaining effects for that projectile instance. If later pierce arrows are added, define explicitly whether block/parry terminates the projectile.

## Damage And Effects

Projectile definitions should describe flight. Actions should describe damage.

For Archer V1:

- Auto attack damage comes from `auto_attacks[]`.
- Selectable shot damage comes from MELEE ability `gameplay` or a renamed future weapon-action catalog.
- Damage is captured into the projectile instance at release.
- Primary resource gain occurs on confirmed hit.
- Stagger, status, and area effects should be action-owned and applied on confirmed impact.

Avoid making Archer shots fake spell abilities only to get projectile damage. That would couple bow attacks to spell resource/cast behavior and make animation authoring harder.

## Multi-Shot And Rain Shot

Support these as extensions of the same delivery lane:

- Multi-shot: one hit window spawns N projectile instances with deterministic spread angles.
- Burst shot: multiple hit windows each spawn one projectile.
- Rain shot: either an area spell-like projectile family from sky origin, or a delayed area impact action if arrows are not individually simulated.
- Piercing arrow: same projectile instance can continue after hit until `pierce_count` is exhausted.

Do not special-case these in Archer code. Add projectile spawn patterns:

```json
{
  "spawn_pattern": {
    "kind": "SPREAD",
    "count": 3,
    "yaw_degrees": 7.5
  }
}
```

V1 should implement only single-arrow release. Add spread/pierce only after the base path has tests.

## Client Presentation

Add reusable arrow projectile VFX instead of routing arrows through hardcoded spell VFX.

Recommended Unity pieces:

- `WeaponProjectileVFX` or `CombatProjectileVFX` implementing the same lifecycle as spell projectile VFX.
- A projectile visual registry keyed by projectile archetype or authored strike id.
- Arrow mesh prefab, trail, impact spark, blocked impact, parry deflect.
- `CombatVFXDispatcher` support for `CombatEvent` projectile spawn/update/impact/fizzle/block/parry triggers.

After the `CombatEvent` migration, `SpellVFXDispatcher` should either subscribe through a spell-only adapter over `CombatEvent` or be folded into `CombatVFXDispatcher`. Archer arrows should route through combat projectile presentation with authored cues, not through the unknown-spell placeholder path.

## Authoring Changes

Unity authoring should expose projectile delivery where designers already tune attacks.

Add to `WeaponStrikeCombatAuthoring` or a sibling struct:

- Delivery kind: `DIRECT_IMPACT` or `PROJECTILE`.
- Projectile id.
- Release hit-window index or per-window delivery.
- Projectile spawn offsets.
- Initial LOS required.
- Allow interception.
- Block/parry behavior.
- Optional VFX projectile id.

Validation rules:

- Projectile strikes must have at least one hit/release window.
- Projectile id must resolve to a projectile catalog row.
- Projectile speed, radius, and max distance must be finite and positive.
- Direct melee-only fields should not be required for projectile strikes except where they still drive acquisition/cooldown.
- Archer auto attack must point at an authored projectile-capable strike.
- Projectile-capable strikes should not export as old targetless melee projectile rows.

## Migration Plan

### Phase 0: Combat Naming And Shared Hit Plumbing

- Rename `SpellEvent` to `CombatEvent`, or introduce `CombatEvent` and stop all new combat writes to `SpellEvent`.
- Rename event constants from `SPELL_CAST`, `SPELL_UPDATE`, `SPELL_IMPACT`, `SPELL_FIZZLE`, `SPELL_BLOCK`, and `SPELL_PARRY` to `COMBAT_CAST`, `COMBAT_UPDATE`, `COMBAT_IMPACT`, `COMBAT_FIZZLE`, `COMBAT_BLOCK`, and `COMBAT_PARRY`.
- Update generated bindings, Unity subscriptions, `SpellVFXDispatcher`, `CombatVFXDispatcher`, animation request translation, floating damage, and any tests that still reference the legacy table or event names.
- Dedupe `resolve_melee_defense` and `resolve_defensible_spell_hit` into `resolve_defensible_combat_hit(ctx, source_kind, ...)`. Keep source-specific event emission thin and explicit.
- Preserve behavior while doing this phase. The output should be naming and code ownership cleanup, not combat balance changes.

### Phase 1: Shared Scene Query Cleanup

- Move `first_hit_on_segment`, `has_line_of_sight`, `line_of_sight_blocker`, capsule raycast helpers, and related scene-hit structs from `server/src/spells/scene_query.rs` and `server/src/spells/collision.rs` into a neutral module such as `server/src/combat/scene_query.rs`.
- Keep spell call sites behavior-identical.
- Add unit tests for earliest hit ordering: world before player, player before world, blocker target before selected target.

### Phase 2: Neutral Projectile Runtime

- Add `ActiveCombatProjectile`.
- Add `ProjectileDefinition` loading for a small hardcoded or catalog-backed `ARROW_STANDARD`.
- Implement `tick_projectiles` using the shared segment collision helper.
- Emit projectile lifecycle events as `CombatEvent` rows.
- Migrate ordinary `SpellBehavior::Projectile` spells to `ActiveCombatProjectile` in this phase. Do not leave normal spell projectiles and weapon projectiles on separate active tables.
- Use `resolve_defensible_combat_hit` for both migrated spell projectiles and weapon projectiles.

### Phase 3: Archer Release Scheduling

- Add `PendingProjectileRelease`.
- Extend resolved melee/weapon gameplay with optional projectile delivery.
- In `perform_melee_attack_for_internal`, schedule projectile release instead of `PendingMeleeImpact` for projectile-delivery strikes.
- Treat missing resolved hit-window damage as authoring/runtime corruption. Do not silently spawn 0-damage arrows with full visuals.
- Keep cast animation events unchanged.
- Make auto attacks work through the same path.

### Phase 4: Client Arrow Presentation

- Add arrow projectile prefab and VFX lifecycle.
- Route Archer projectile events through combat VFX cues or a projectile dispatcher.
- Ensure spell projectile VFX still work.
- Suppress non-spell fallback projectile placeholders for Archer actions.

### Phase 5: Archer Catalog Wiring

- Mark most Archer attacks as projectile delivery.
- Leave explicitly non-projectile actions as direct/area/utility actions.
- Recommended projectile V1:
  - `ARCHER_QUICK_SHOT`
  - `ARCHER_FOLLOW_THROUGH`
  - `ARCHER_LOW_DRAW`
  - `ARCHER_FINISHING_SHOT`
  - `ARCHER_POWER_SHOT`
  - `ARCHER_EVASIVE_SHOT`
  - `ARCHER_AIR_SHOT`
- Do not force `ARCHER_RAIN_SHOT` into single-arrow behavior if the intended fantasy is a volley or area pattern. Implement it later as a projectile pattern or area action.

## Testing Requirements

Server tests:

- Projectile hits selected target when unobstructed.
- Projectile hits intervening enemy before selected target.
- Projectile impacts world blocker before selected target.
- Projectile misses if target moves out of path after release.
- Initial cast rejects target behind full cover when LOS is required.
- Projectile does not tunnel through a player at high speed.
- Blockable projectile emits block event and does not apply damage.
- Parryable projectile emits parry event and terminates.
- Auto attack cadence schedules arrows and grants primary resource only on confirmed hit.
- Multiple projectiles from different casters resolve independently in the same tick.

Unity/editor tests:

- Projectile delivery exports and imports through `CombatAnimationSet`.
- Archer auto attack asset validates with projectile delivery.
- Combat VFX cue rows resolve for Archer projectile spawn and impact.
- No unknown-spell placeholder appears for Archer projectile events.

Manual playtest checklist:

- Arrow visibly leaves the bow near the release frame.
- Arrow path matches authoritative hit/fizzle point.
- Enemy standing between Archer and selected target is hit first.
- Wall or terrain cover stops arrows.
- Fast arrows feel responsive without becoming hitscan.
- Auto attack animation cadence still feels stable when arrows miss.

## Non-Goals

- Do not reintroduce old targetless projectile delivery to melee.
- Do not model arrow gravity in V1 unless design explicitly wants ballistic arrows.
- Do not trust client bone transforms for authoritative projectile origin.
- Do not make every Archer action a spell ability.
- Do not make Rain Shot a fake single-arrow attack if it needs volley behavior.

## Recommendation

Build this as a shared combat projectile runtime, then connect Archer attack hit windows to projectile release scheduling. This is more work than an adapter that casts a spell from a melee animation, but it gives the game the right long-term model: spell projectiles and weapon projectiles share collision and defense machinery, while action identity, animation authoring, resource rules, and presentation remain honest.

For V1, keep the scope tight: one `ARROW_STANDARD` projectile, one projectile release per authored shot, no homing, no pierce, no gravity, and no multi-shot. Once that path is authoritative and tested, spread, piercing, charged arrows, and rain/volley behavior become catalog extensions instead of new systems.
