# Gap Closer Authoring And Runtime Plan

Status: V1 implemented for selectable melee on 2026-05-01. Charge-like abilities now use selectable ability-owned movement delivery as of 2026-05-06. This document remains useful for selectable melee `gap_close`, but its older charge/spell cleanup notes have been superseded by `docs/archive/2026-05-stale-plans/spell-system-unification-plan-2026-05-05.md`.

Goal: make gap closers first-class gameplay behavior while preserving the current source-of-truth split:

- `server/src/progression_catalog.shared.json` owns player-facing ability gameplay tuning.
- `Assets/Arena/Resources/CombatAnimationSets/*.asset` owns melee strike identity, clips, hit windows, recovery, and combo timing.
- `server/src/melee_manifest.shared.json` remains the generated bridge from Unity animation data to the server.
- `special_movement_runtime` remains the shared server-authoritative movement transport.

## Current Problem

`is_gap_closer` still exists in the legacy melee manifest schema and syncs into `MeleeDefinition`, but Unity editor authoring now hides it, export writes `false`, and melee runtime does not use it to move the caster.

The flag is also in the wrong place for gameplay truth. Gap closer behavior changes how an ability validates range, resolves movement, handles collision, spends cost, schedules impact, and fails. Those are player-facing gameplay semantics, so they belong in `server/src/progression_catalog.shared.json` alongside melee `range`, damage, cooldown, resource, and defense tuning.

## Design Direction

Gap closer is not a separate top-level combat category. It is movement behavior attached to an action.

Charge is a kind of gap closer, but charge-like abilities are now selectable movement abilities, not fixed actions or selectable melee implementation details. Shared helpers should serve both selectable melee gap closers and `MOVEMENT` delivery without forcing them into one authoring bucket.

Use `special_movement_runtime` for the actual authoritative movement. Do not create a second movement table for gap closers.

## Authoring Model

Add optional `gap_close` data to melee ability rows in `server/src/progression_catalog.shared.json`.

Example:

```json
{
  "ability_id": "WARRIOR_SUNDER",
  "class_id": "WARRIOR",
  "action_id": "COMBO_ATTACK_4_4_LUNGING_SLASH",
  "display_name": "Sunder",
  "resource_kind": "RAGE",
  "resource_cost": 35.0,
  "ability_tags": ["LOADOUT_ACTION"],
  "sort_order": 60,
  "gameplay": {
    "kind": "MELEE",
    "base_damage": 42,
    "applies_stagger": false,
    "range": 15.0,
    "cooldown_ms": 1200,
    "uses_global_cooldown": true,
    "parry_behavior": "UNPARRYABLE",
    "block_behavior": "UNBLOCKABLE",
    "airborne_targeting_mode": "ANY_TARGET",
    "gap_close": {
      "kind": "LINEAR",
      "destination": "NEAREST_CONTACT_POINT",
      "speed": 18.0,
      "arrival_buffer": 0.35,
      "impact_range": 2.5,
      "collision_policy": "REQUIRE_CLEAR_PATH",
      "require_arrival_for_swing": true,
      "requires_target_facing": true
    }
  }
}
```

Field meanings:

- `range`: max target acquisition/start range for the ability.
- `gap_close.kind`: movement style.
- `gap_close.destination`: where the caster wants to arrive relative to the target.
- `gap_close.speed`: meters per second for linear/leap styles.
- `gap_close.arrival_buffer`: extra spacing beyond hit radii.
- `gap_close.impact_range`: final allowed strike range after movement. This prevents a 15m gap closer from also becoming a 15m hit check.
- `gap_close.collision_policy`: what to do when path or destination validation fails.
- `gap_close.require_arrival_for_swing`: when true, blocked or invalid movement rejects/fizzles before scheduling melee impact.
- `gap_close.requires_target_facing`: when true, the caster must be facing the target before movement starts.

## Gap Close Kinds

V1:

- `LINEAR`: ground lunge/charge toward the target.
- `LEAP`: arcing or fixed-height movement toward the target, still destination validated.
- `TELEPORT`: instant movement to an authored destination near the target.
- `TELEPORT_BEHIND`: instant movement behind the target using target yaw.

Deferred:

- `POINT_DASH`: dash to a target point rather than a target actor.
- `PULL_TO_TARGET`: pull caster to target without using a melee swing.
- `TARGET_TO_CASTER`: pull target to caster.
- `PASS_THROUGH`: move through target and end behind/in front based on path direction.

## Destination Modes

V1:

- `NEAREST_CONTACT_POINT`: stop on the caster-to-target line at contact distance.
- `BEHIND_TARGET`: stand behind the target relative to target yaw.
- `TARGET_SIDE_LEFT`: stand to target's left.
- `TARGET_SIDE_RIGHT`: stand to target's right.
- `CURRENT_LINE`: preserve current approach line and stop at contact distance.

Each destination resolves to an intended end position. The resolver must account for caster hit radius, target hit radius, `arrival_buffer`, terrain height, and world context.

## Runtime Flow For Melee

For a melee ability with no `gap_close`, keep current behavior.

For a melee ability with `gap_close`:

1. Resolve ability and target as today.
2. Validate target is alive, in the same world context, and within ability `range`.
3. Validate airborne target rules.
4. Resolve intended gap-close destination.
5. Validate or bake movement path.
6. If movement is blocked and `require_arrival_for_swing` is true, reject/fizzle before resource spend, cooldown stamp, cast event, and pending impact creation.
7. Spend resource and clear block only after the gap-close movement is accepted.
8. Begin `special_movement_runtime` using a gap-close kind such as `MELEE_GAP_CLOSE:<ability_id>` or `MELEE_GAP_CLOSE:<strike_id>`.
9. Emit the existing melee cast event so melee animation still uses the authored strike path.
10. Schedule pending melee impacts no earlier than movement arrival. Use `gap_close.impact_range`, not ability `range`, for the pending hit check.
11. At impact time, revalidate target state, world context, final distance, and defense as today.

Important invariant: acquisition range and impact range are distinct for gap closers.

## Runtime Flow For Charge-Like Movement

Current state:

- Charge-like actions are class-owned selectable abilities.
- Default loadouts assign the ability id directly.
- The ability uses `gameplay.kind: "MOVEMENT"` and `gameplay.delivery.kind: "DASH_TO_TARGET"`.
- Client input sends the ability id through `CastRequest`; the server routes it into generic movement-delivery launch logic.
- Selectable melee gap closers still use `gameplay.gap_close`; do not force them through movement delivery unless their gameplay is genuinely a movement-delivery ability.

## Charge Cleanup Candidates

Existing charge behavior already has several gap-closer concepts under charge-specific names:

- `gameplay.delivery.max_distance` is charge acquisition range.
- `gameplay.delivery.speed` is movement travel speed.
- `gameplay.delivery.arrival.buffer` is final spacing beyond caster and target hit radii.
- `gameplay.delivery.arrival.epsilon` is final arrival tolerance for deciding hit vs fizzle.
- `movement_delivery_destination(...)` resolves the nearest contact destination.
- `movement_delivery_duration_ms(...)` derives movement duration from speed and baked travel distance.
- `resolve_movement_delivery_impact(...)` checks final arrival distance and fizzles if the caster did not arrive.

The former charge-specific helper names have already moved to movement-delivery names. Remaining cleanup is about shared vocabulary between selectable melee gap closers and movement-delivery abilities, not about removing a spell-charge path.

Keep `arrival.epsilon` as movement delivery's equivalent of "required arrival tolerance." If melee gap closers need the same behavior, add a `gap_close.arrival_epsilon` field rather than hiding it inside `impact_range`.

- Make the relationship explicit:
  - charge `max_distance` == gap-close acquisition range
  - charge `arrival.buffer` == gap-close `arrival_buffer`
  - charge final arrival check == gap-close `require_arrival_for_swing`

Clean up later, after more movement abilities exist:

- Decide whether movement delivery and selectable melee gap close should share a lower-level destination/collision config object.
- Review `gameplay.delivery.radius` for charge-like movement abilities. The path currently uses target arrival plus direct target hit resolution; if radius is not used by presentation or future behavior, remove or repurpose it deliberately.

Still intentionally separate from selectable melee gap-close authoring:

- Active cast ownership for movement delivery.
- Ability-specific resource side effects.
- Movement-delivery impact effects such as stun, knockdown, or stagger.
- Client presentation routing for `MovementActionState.Kind == "DASH_TO_TARGET"`.

## Schema Changes

Rust progression catalog parsing:

- Add `gap_close: Option<GapCloseDefinition>` to the in-memory progression ability definition type.
- Add a public table representation on `MeleeAbilityCatalog` or a parallel `MeleeGapCloseCatalog`.

Recommended V1 table:

```rust
#[table(accessor = melee_gap_close_catalog, public)]
pub struct MeleeGapCloseCatalog {
    #[primary_key]
    pub ability_id: String,
    pub kind: String,
    pub destination: String,
    pub speed: f32,
    pub arrival_buffer: f32,
    pub impact_range: f32,
    pub collision_policy: String,
    pub require_arrival_for_swing: bool,
    pub requires_target_facing: bool,
}
```

Use a separate table rather than expanding `MeleeAbilityCatalog` with many empty fields. This keeps non-gap-closer rows compact and makes optional behavior explicit.

Validation:

- `gap_close.kind` must be supported.
- `gap_close.destination` must be supported.
- `gap_close.speed` must be positive for non-teleport movement.
- `gap_close.arrival_buffer` must be non-negative.
- `gap_close.impact_range` must be positive and should usually be less than or equal to normal melee contact range.
- `gap_close.impact_range` must not default to ability `range`.
- Teleport destination must still be collision/destination validated.

## Movement Implementation

Create shared server helpers, likely outside `movement_actions.rs` and outside spell-specific charge code:

- `resolve_gap_close_destination(...)`
- `validate_gap_close_destination(...)`
- `bake_gap_close_movement(...)`
- `gap_close_duration_ms(...)`

These helpers should call existing primitives:

- `bake_linear_special_movement`
- `begin_special_movement` or `begin_special_movement_with_facing_policy`
- terrain sampling and world collision helpers already used by special movement

For `LINEAR`:

- Start at current/cast snapshot position.
- End at resolved destination.
- Bake with stop-at-block collision.
- If baked end is not close enough to intended destination and `REQUIRE_CLEAR_PATH`, fail before swing.

For `LEAP`:

- Use fixed-y or leap-specific y policy for travel.
- Still validate destination on ground or allowed landing height.
- Fail before swing if landing is invalid.

For `TELEPORT` and `TELEPORT_BEHIND`:

- Do not require clear path unless the behavior explicitly asks for line-of-sight.
- Require destination occupancy/collision validation.
- Use instant or near-instant `special_movement_runtime` so client prediction/interpolation has one authoritative transport path.

## Impact Scheduling

Gap-close melee impacts should not resolve before the caster can plausibly arrive.

V1 rule:

- `arrival_at = now + movement_duration`
- `authored_impact_at = now + first_hit_window.impact_delay_ms`
- `impact_at = max(arrival_at, authored_impact_at)`

For multi-hit windows:

- Preserve spacing between authored hit windows after the first adjusted impact, or independently clamp each hit to not precede `arrival_at`.
- Prefer preserving authored spacing so animation timing stays coherent.

Pending impacts need a final range:

- Add `impact_range` to gap-close catalog and store that in `PendingMeleeImpact.range`.
- Non-gap-closers continue using normal melee ability `range`.

## Presentation

Melee gap closers should continue to request melee animation through the normal melee cast event. Do not trigger `CombatAnimationCategory.Charge` for selectable melee gap closers by default.

Optional future presentation data:

- Use `gap_close.kind` or a presentation field to choose between lunge, leap, blink, or teleport VFX.
- Keep this separate from the gameplay requirement to move.

Existing `is_gap_closer` in combat animation sets is legacy presentation metadata only. It should stay hidden from editor authoring, import should not preserve it, and export should write `false` while server gameplay relies on progression `gap_close` data.

## Migration Plan

Status as of 2026-05-01:

- Phase 1 is implemented.
- Phase 2 is mostly implemented. Destination helpers exist; teleport destination validation now uses shared world collision validation. The required-arrival pre-commit gate is covered by unit tests; a true `ReducerContext` integration harness is still not present.
- Phase 3 is implemented for selectable melee. Impact timing, final impact range selection, and final target-distance misses are covered by unit tests.
- Phase 4 has started with `WARRIOR_HEW`; legacy Unity `is_gap_closer` authoring is hidden and inert.
- Phase 5 is mostly implemented. Charge and melee gap closers now share approach-contact, contact-distance, arrival-tolerance, and horizontal-duration helpers; charge-like abilities use selectable movement delivery while keeping active-cast impact effects and presentation routing separate.

Phase 1: Add data model and validation.

- Add optional `gap_close` parsing to progression catalog ability definitions.
- Add `melee_gap_close_catalog` sync.
- Add validation tests for supported enum values and numeric constraints.
- Do not change melee runtime yet.

Phase 2: Implement shared gap-close helpers.

- Implement destination resolution and movement validation helpers.
- Unit test nearest-contact, behind-target, blocked linear path, and teleport destination validation.
- Keep existing charge behavior passing.

Phase 3: Wire selectable melee runtime.

- In `perform_melee_attack_for_internal`, look up gap-close data for selectable melee abilities.
- Treat `range` as acquisition range.
- On accepted gap close, begin special movement and schedule impacts using final `impact_range`.
- Reject/fizzle before spend/cooldown/swing if required arrival fails.

Phase 4: Author content.

- Add `gap_close` rows only when specific abilities are intentionally promoted to gap closers.
- Increase their `range` values to desired acquisition distances.
- Set explicit `impact_range` values.
- Keep `is_gap_closer` as hidden/backward-compatible manifest metadata for now, but do not expose it in editor UI or export authored `true` values.

Phase 5: Consolidate charge-like movement delivery.

- Extract any duplicated destination/duration logic into shared movement-delivery helpers.
- Keep animation presentation separate from movement delivery execution.
- Add regression tests proving existing charge abilities remain unchanged.

Remaining charge work:

- Decide whether selectable melee gap closers need an authored `arrival_epsilon`, using charge `arrival.epsilon` as the model.
- Charge-like actions are selectable `MOVEMENT` abilities, not spells or fixed actions.
- Keep active-cast lifecycle, resource gain, fizzle event, impact effects, and presentation routing data-driven enough for additional movement abilities.
- If terrain/obstacle rejection remains too jagged, tune or author a movement/path tolerance explicitly rather than adding hidden client-side range grace.

## Tests

Server tests:

- A non-gap-closer melee ability still rejects targets outside normal range.
- A gap closer accepts a target within acquisition `range` but outside normal contact range.
- A gap closer stores pending impact range from `gap_close.impact_range`, not acquisition range. Covered by `gap_close_pending_impact_uses_impact_range_not_acquisition_range`.
- A blocked `REQUIRE_CLEAR_PATH` gap closer rejects before resource spend, cooldown stamp, cast event, and pending impact creation.
- Required-arrival gap closer rejection goes through an explicit pre-commit gate before resource spend, cooldown stamp, cast event, and pending impact creation.
- A teleport-behind gap closer resolves behind the target and validates destination collision.
- A target moving away before impact can still cause the impact to miss if final distance exceeds impact range. Covered by `target_moving_outside_gap_close_impact_range_can_miss_after_arrival`.
- Existing charge ability tests still pass.

Client/editor tests:

- Generated bindings include `melee_gap_close_catalog`.
- Melee animation still routes as melee, not charge, for selectable melee gap closers.
- Editor tooling does not display `is_gap_closer`; any future gap-close display should read progression data.

Manual verification:

- Try a 15m linear gap closer in training ground.
- Try the same ability against a wall or blocked path and confirm no swing occurs.
- Try teleport behind target near blocked geometry and confirm invalid destinations fail.
- Confirm normal melee and auto-attacks are unchanged.

## Open Decisions

- Should blocked gap closers return silent rejection, `SPELL_FIZZLE`, or a new melee-specific fizzle event?
- Should `gap_close.impact_range` be explicitly authored for every gap closer, or default from a global contact rule?
- Should teleport require line of sight by default?
- Should gap closer resource cost be spent on accepted movement start or only on successful arrival?
- Should combo follow-ups be allowed to gap close, or only root/loadout actions?

## Acceptance

- Gap closer gameplay is authored in `server/src/progression_catalog.shared.json`.
- `range` is the max acquisition range for gap closers.
- Gap closers use shared `special_movement_runtime` movement transport.
- Blocked/invalid gap closer movement can prevent the swing before resource/cooldown/impact.
- Pending melee impacts for gap closers use final impact range, not acquisition range.
- Charge-like movement abilities share gap-close helper code without becoming selectable melee behavior.
