# Knockback — Formal Design (2026-07-17, rev 3)

Status: PROPOSED — rev 2 resolved the five P1 contract gaps from the first review; rev 3 resolves the second review's findings. No code has been changed.

Rev 2 changes at a glance: knockback is now a **first-class effect packet** (not a `StatusApplication` masquerade) carrying impact-time direction; explicit ordering-independent **stagger/knockback composition rule**; knockback **terminates self-initiated movement deliveries** (dash casts) instead of orphaning them; NPC AI is gated by the **forced-movement row itself**, not the hold stamp; the player mover's **non-scene-aware ground-Y sampling** is called out as a prerequisite fix; NPC resistance stays off the public catalog table; duration clamp contradiction removed.

Rev 3 changes at a glance: actor **teardown deletes forced movement unconditionally** (the scoped delete applies only to ordinary cast termination); zero-damage knockbacks **explicitly enter combat** via `mark_harmful_combat_action`; phases 1–2 **do require binding regen and the schema-change publish path** (new private tables still change the module schema); the stagger shove **keeps its authored 4.5 m/s feel** (no silent retune to 12 m/s); 12 m/s is documented as the *nominal* speed (the one-tick duration floor makes sub-0.4 m pushes slower).

## 1. What we're building

A general knockback combat interaction:

- Spells and melee abilities can author a **knockback** on-hit effect with varying **strength** (distance).
- Targets are displaced **away from the caster** (direction = caster→target), stopping at walls/obstacles.
- Targets can have **knockback resistance**: a `[0, 1]` scalar that shrinks the displacement proportionally; at `>= 1.0` the target is **immune** and does not move.
- Works on players and NPCs, in all scenes, under lag compensation, without opening movement exploits.

## 2. What already exists (investigation summary)

The single most important finding: **this game already has a working micro-knockback.** The `Stagger` status triggers `STAGGER_SHOVE` (0.45 m over 100 ms), built on the same forced-movement engine as dashes and gap-closers. Knockback is therefore mostly a *generalization + resistance + NPC parity* project, not a from-scratch mechanic.

### 2.1 The displacement engine (reuse with one fix)

- `SpecialMovementRuntime` (public table, one row per owner) — `server/src/spells/mod.rs:220`. Fields: kind, path mode (`LINEAR`/`INSTANT`), start/end, duration, facing policy, collision policy. `begin_special_movement_with_facing_policy` (`server/src/spells/casting.rs:4557`) is delete-then-insert, so a new track always replaces an in-flight one.
- **Collision-aware path baking**: `bake_linear_special_movement` (`casting.rs:3000`) walks the intended path against static movement geometry + terrain (scene-aware, `casting.rs:3022`) and clamps at the first block (`STOP_AT_BLOCK`). Dynamic `ActiveWorldObstacle` rows are re-resolved live each tick by the mover.
- **Per-tick mover**: `tick_special_movement_runtimes` (`server/src/game_loop.rs:1535`) samples the path and commits via `commit_player_physics(PhysicsWriteMode::SpecialMovementTick)`. While a track is active, `Normal` (input-driven) commits have position changes **rejected** (`server/src/player_physics.rs:165-188`) — the victim cannot run against the push, server-side, with zero new code.
- ⚠ **Prerequisite fix — scene-aware ground Y in the mover.** The mover resolves terrain height via the no-scene wrapper (`game_loop.rs:1585-1593` → `surface_height_for_world_at_y_with_layout`, `server/src/world_collision.rs:454`), which falls back to the default open-world profile. Baking is scene-aware but per-tick Y sampling is not, so non-default scenes can get incorrect authoritative Y during any special movement. This is a latent bug for existing dashes/leaps too; knockback's "all scenes" requirement makes fixing it (switch to the `_for_scene` variant with the owner's scene) a phase-1 prerequisite. **Flagged behavior change**: fixes dash/leap Y in non-default scenes as a side effect.
- **Lag comp is automatic (players)**: every non-`Normal` physics commit stamps a `CombatRewindBarrier` (`player_physics.rs:142-160`, `server/src/combat/position_history.rs`), so attackers can never rewind a victim to a pre-knockback position.
- **No anti-cheat conflict**: the client never reports position (intent only, `send_movement_intent`, `server/src/movement.rs:74`); there is no speed validator to fight.

### 2.2 The client side (mostly free for players)

- Local player: `LocalMovementPredictionDriver` yields prediction entirely to an active `SpecialMovementRuntime` track (`DriveLocalSpecialMovement`, `Assets/Arena/Runtime/Input/LocalMovementPredictionDriver.cs:405-456`), re-anchors input on exit, and discards buffered commands. Input suppression during the push is inherent.
- Remote players: the track short-circuits `RemotePresentationBuffer` interpolation (`Assets/Arena/Runtime/Simulation/ClientSimulationState.cs:459-466`) — deterministic, no smear, no hard-snap.
- `SpecialMovementRuntime` is already subscribed (`GameplaySubscriptionPlanner.BuildScopedSpecialMovementRuntimeQuery`) and already bound (`NetworkCallbackBinder.cs:156-158`). The client track sampler is kind-agnostic (verified) — **a player knockback renders correctly today with no client changes.**

### 2.3 The effect pipeline (one authoring rail, one hook)

- All four delivery types (direct-target, projectile, AoE, melee) funnel authored on-hit effects through `push_impact_effect_packets` → pending tables → `resolve_pending_effects` (`combat.rs:3768`), in queue order with damage.
- `ImpactEffect` = `StatusApplication` today (`server/src/spells/manifest.rs:385`); authored via `ImpactEffectRow` (`server/src/spells/catalog.rs:431`, `deny_unknown_fields` — the parser hard-fails on unknown keys, which is our schema safety net).
- **Constraint that shapes the design**: `EffectPacket::ApplyStatus` (`combat.rs:3390`) and its pending row carry *no positional data* — projectile origin, area center, and contact direction are all discarded by application time. Knockback direction must therefore be captured **at the hit site** into a dedicated packet (§3.1).
- Direction data is available at every hit site: caster→target for direct/melee (`source_to_target_direction`, `combat.rs:4761`), projectile spawn origin persisted on `ActiveCombatProjectile` (`combat.rs:637-645`), radial `area_contact_direction` for AoE (`casting.rs:5965`).

### 2.4 The resistance rails (extend, don't invent)

- Equipment: `EquipmentModifierTotals` (`server/src/inventory.rs:2248`) + `apply_modifier_value` (`inventory.rs:4043`) + `MODIFIER_*` constants (`inventory.rs:130-153`) + seeded affixes (`inventory.rs:979+`). Elemental resistances (e.g. `lightning_resistance`) are the exact template.
- Temporary buffs: `StatusEffect` table + `TemporaryCombatModifiers` accumulation (`combat.rs:5520-5629`); `MoveSlowImmunity` is the immunity-buff precedent.
- Character sheet: `InventoryScreen.RefreshCharacterStats` re-aggregates affix rows generically; an unknown modifier kind auto-renders as a `+X%` row — **zero client code for the stat to appear**.

### 2.5 The gaps (net-new work)

1. **NPCs are never displaced.** `start_stagger_shove` requires `player_state`/`player_physics`; the mover only advances players; the client `EntityRegistry` routes `SpecialMovementRuntime` to players only. NPCs write `NpcPhysics` directly each tick from AI steering (`move_npc_along`, `server/src/npcs.rs:2704`).
2. **No knockback-resistance stat exists anywhere** (no CC-resist of any kind).
3. **No boss/immunity flag on NPC templates**.
4. **NPC hit reactions are a separate, cruder pipeline** (`NpcAnimationController.PlayHit` on HP drop; no directional reactions).

## 3. Design

### 3.1 Effect model — knockback is a first-class displacement packet

Knockback is authored on the same `impact_effects` rail as statuses but is **not** a `StatusApplication` and never touches the `StatusEffect` table. Rationale (from review): `StatusApplication` requires a nonzero duration, serializes into fixed status columns, and — decisively — its packet/pending row cannot carry the impact-time direction that projectile and AoE knockbacks need. Stacking/dispel/expiry are meaningless for an already-applied displacement anyway.

Concretely:

- `ImpactEffect` changes from a type alias into a two-variant enum: `Status(StatusApplication)` | `Knockback { distance_meters: f32 }`. The alias-to-enum change ripples mechanically through the `impact_effects` plumbing (`manifest.rs`, `push_impact_effect_packets` in `casting.rs:237` / `projectiles.rs:359`, and their call sites), which all gain access to the hit-site direction they already compute.
- New `EffectPacket::Knockback { source, target, spell_id, dir_x, dir_z, distance_meters }` — **direction is normalized at the hit site** (per-delivery rules, §3.3) and baked into the packet.
- New private `PendingKnockback` table (queue-ordered like the other pending tables), routed by `queue_effect`, resolved by `resolve_pending_effects` → `start_knockback_shove`. Damage stays first in queue order (damage packets precede impact effects at every push site).

By default knockback is **pure displacement**: it does not interrupt casts and is not hard CC. Interrupting knockbacks are authored by composing effects (`KNOCKBACK` + `STAGGER`), which the multi-effect `impact_effects` list already supports (composition semantics in §3.5).

### 3.2 Authoring schema

Spells (`progression_catalog.shared.json`, per-delivery `impact_effects`):

```json
{ "kind": "KNOCKBACK", "distance_meters": 4.0 }
```

Melee: `MeleeImpactEffectDefinition` (`server/src/progression.rs:368`) gains a first-class `Knockback { distance_meters }` variant beside `ApplyStatus`/`RemoveStatus` (not routed through the status definition, matching the runtime split).

Global tuning (new `combat_rules` entries, published via the existing catalog flow):

| Rule | Default | Meaning |
|---|---|---|
| `KNOCKBACK_SPEED_METERS_PER_SEC` | 12.0 | **Nominal** push speed; duration derived (`distance / speed`, floor one tick = 33 ms) |
| `KNOCKBACK_MAX_DISTANCE_METERS` | 10.0 | Authored-distance cap (sanity bound; replaces any duration clamp) |
| `KNOCKBACK_MIN_EFFECTIVE_DISTANCE_METERS` | 0.1 | Below this (post-resistance), skip the shove entirely |
| `MAX_EQUIPMENT_KNOCKBACK_RESISTANCE` | 0.6 | Gear alone can never reach immunity |

Strength = authored distance, not an abstract tier. Duration is derived from the nominal speed with no independent clamp (the rev-1 `[60, 800]` ms clamp contradicted constant speed outside 0.72–9.6 m and is dropped). The one-tick duration floor means pushes under ~0.4 m travel slower than nominal — 12 m/s is the contract for every push above that, not a universal constant. After resistance scaling, duration shrinks with distance.

### 3.3 Direction rules (per delivery, captured at the hit site)

| Delivery | Direction | Source data (present at hit site) |
|---|---|---|
| Direct target / melee | caster→target, XZ-normalized | `source_to_target_direction` pattern (`combat.rs:4761`) |
| Projectile | projectile **spawn origin**→target; fallback: travel dir | `ActiveCombatProjectile.origin_*` / `dir_*` (`combat.rs:637-645`) |
| Area / Emanation / Aura | area **center**→target (radial outward) — **decided** | `area_contact_direction` (`casting.rs:5965`) |
| Degenerate (overlap) | target's facing yaw | existing `yaw_direction` fallback |

For caster-centered emanations, radial and away-from-caster coincide. Displacement is XZ-only; Y follows terrain via the existing `resolve_special_movement_y` (with the scene-aware fix, §2.1). No aerial launch in v1 (§8).

### 3.4 Resistance model

```
total_resistance = clamp(equipment (capped at MAX_EQUIPMENT_KNOCKBACK_RESISTANCE)
                       + status buffs (Σ modifier_scalar)
                       + npc_template.knockback_resistance,  0.0, 1.0)
effective_distance = min(authored_distance, KNOCKBACK_MAX_DISTANCE_METERS) * (1.0 - total_resistance)
if total_resistance >= 1.0 or effective_distance < MIN → no shove (immune / negligible)
```

Sources, each on an existing rail:

1. **Equipment** (persistent): new `knockback_resistance` field on `EquipmentModifierTotals`, new `MODIFIER_KNOCKBACK_RESISTANCE` constant + `apply_modifier_value` arm + clamp, new seeded `AFFIX_KNOCKBACK_RESISTANCE_MINOR` (mirrors the elemental-resistance affixes). Character sheet displays it automatically as a `+X%` row.
2. **Status buff** (temporary): new `StatusEffectKind::KnockbackResistance` carrying `modifier_scalar` (an authored buff spell can set 1.0 = immunity — the "Steadfast" pattern), accumulated in `TemporaryCombatModifiers` with an accessor like `knockback_resistance_for(target)`; modeled on `MagicResistance`/`MoveSlowImmunity`. Needs a `StatusTooltipResolver` entry client-side.
3. **NPC template** (innate): new optional `knockback_resistance` field on the **private authored `NpcTemplate` only** (`npcs.rs:286-310` + `npc_catalog.shared.json`, serde default 0.0 — none of the 65 templates need touching). It is deliberately **not** added to the public `NpcTemplateCatalog` table: the client has no use for it, and keeping it private avoids a public catalog-column change (review correction). Bosses/heavies author 1.0; this doubles as the boss-immunity flag.

Resistance is read once, at effect application time (not at cast time, not re-checked mid-push). **Decided**: the existing stagger shove routes through the same *resistance* formula — resist gear will shrink stagger shoves (flagged behavior change). It does **not** adopt the knockback speed rule: stagger keeps its authored 0.45 m / 100 ms (4.5 m/s) feel, and `STAGGER_SHOVE_DURATION_MS` stays live (§3.5.3).

### 3.5 Server pipeline changes

1. **Parse**: new `ImpactEffectRow::Knockback { distance_meters }` variant (`catalog.rs:431-548`) converting to `ImpactEffect::Knockback`; melee variant per §3.2. `deny_unknown_fields` forces JSON and structs to move together. New `StatusEffectKind::KnockbackResistance` (buff only — there is **no** knockback status kind).
2. **Queue/resolve**: `EffectPacket::Knockback` → `PendingKnockback` (private table) → `resolve_pending_effects` arm → `start_knockback_shove(ctx, now, source, target, dir, authored_distance)`. Zero-damage knockbacks are legal (no `requires_positive_damage` coupling); knockback is not hard CC. **Combat entry (review P1)**: damage marks harmful engagement only when the hit amount is positive (`combat.rs:3954`) and statuses mark it through the status path (`combat.rs:4572`) — a dedicated zero-damage knockback packet bypasses both, which would let utility pushes keep the victim out of combat (and regenerating). `PendingKnockback` resolution therefore explicitly calls `mark_harmful_combat_action` after validating the hostile interaction.
3. **Shove (players)**: `start_knockback_shove` mirrors `start_stagger_shove` (`combat.rs:4778`) with parameters: applies resistance, derives duration from a per-shove **nominal speed**, bakes via `bake_linear_special_movement`, starts kind `KNOCKBACK` with `FACE_START` facing and `STOP_AT_BLOCK` collision. `start_stagger_shove` becomes a thin call into it — direction from `source_to_target_direction`, distance from `STAGGER_SHOVE_DISTANCE_METERS`, and **its own nominal speed derived from the authored `STAGGER_SHOVE_DISTANCE_METERS` / `STAGGER_SHOVE_DURATION_MS` pair (4.5 m/s)**, not the 12 m/s knockback rule. Both rules stay live; resistance scales distance and duration proportionally, preserving each shove's authored speed (review correction — rev 2 silently retuned stagger to ~38 ms).
4. **Preemption & composition rules** (all flagged behavior changes):
   - **Externally-imposed kinds.** Define `is_externally_imposed_movement_kind(kind)` = {`KNOCKBACK`, `STAGGER_SHOVE`}. Both runtime-deletion sites — `clear_active_cast` (`casting.rs:4086-4091`, reached via cast fizzle) and `interrupt_player_actions_for_stagger` (`combat.rs:4729-4733`) — scope their `special_movement_runtime` delete to **skip externally-imposed kinds**. Semantics: those deletes exist to tear down movement the victim *initiated* (a dash cast owns its track); an imposed push is never cast-owned. Rev 1 only patched the explicit delete; the review showed the fizzle→`clear_active_cast` path deletes the runtime too, so both sites must be scoped.
   - **Teardown carve-out (review P1).** `clear_transient_actor_state` (`server/src/actor_lifecycle.rs:164`) currently relies on `clear_active_cast` to delete *all* special movement on despawn and reconnect; scoping that helper alone would leak a live knockback track across teardown. Actor teardown therefore deletes `special_movement_runtime` **unconditionally** (explicit delete added in `clear_transient_actor_state`); the scoped delete applies only to ordinary cast termination.
   - **Stagger + knockback co-authored (ordering-independent rule).** `start_stagger_shove` **no-ops** when a `PendingKnockback` row exists for the target (knockback later in this resolution batch) **or** an active `KNOCKBACK`-kind runtime exists (knockback already applied). Combined with the scoped deletes above, both resolution orders converge: the authored knockback wins, the 0.45 m stagger shove never stomps it.
   - **Knockback vs self-initiated movement deliveries (review P1-3).** Replacing only the runtime row would orphan a dash's owning state: dash-to-target also holds an `ActiveCast` and `MovementActionState`, and `tick_active_casts` (`casting.rs:2047`) would wait out the knockback then resolve the dash's hit/fizzle from the victim's new position. Therefore `start_knockback_shove`, before inserting its track: deletes `movement_action_state` and `pending_melee_timed_movement` for the victim, and — **only if** the victim's `ActiveCast` is a movement delivery (`movement_delivery_for_action_id` resolves) — fizzles that cast via the interrupt path. Ordinary non-movement casts continue through the push (**decided**; special-movement ticks do not bump `voluntary_move_epoch` — verified, no cast-mobility conflict). **Ordering**: resistance/immunity and the minimum-effective-distance check run *first* — if no shove will be produced, no preemption happens; an immune or negligibly-pushed target never loses its dash or movement action.
   - Root does not anchor against knockback (anchoring is expressed as a resistance buff on the root spell if desired).
5. **Shove (NPCs)** — the genuinely new machinery:
   - New **private** table `NpcForcedMovement { npc identity (pk), started_at, duration_ms, start, end }`. Baked at apply time with the NPC's `hit_radius` against the same movement sweep + obstacle helpers NPCs already use (`resolve_world_horizontal_sweep_collision_y_with_layout_for_scene`, `resolve_active_world_obstacle_movement`), scene-aware from day one.
   - New `tick_npc_forced_movement` in `game_tick`: lerp along the path, re-resolve live obstacles, write `npc_physics`, `record_position_sample`, and **stamp the NPC's rewind barrier on every forced-movement commit** — player parity with `commit_player_physics`'s per-commit stamping (`player_physics.rs:142`), not a single stamp at start (review correction).
   - **Exclusive ownership gate (review P1-4)**: the NPC combat loop checks for an active `NpcForcedMovement` row **at the top of each NPC's iteration** — before return-home/leash (`npcs.rs:1329-1360`), commitment facing, and steering — and skips all AI physics writes while it exists. The `hold_movement_until_micros` stamp is *not* the gate: return-home runs before the hold check (`npcs.rs:1390`), `clear_npc_combat_runtime` (`npcs.rs:1351`) wipes the hold, and idle NPCs may have no combat runtime to stamp. (A hold stamp may still be set as a courtesy so post-push AI doesn't instantly lunge, but correctness never depends on it.)
   - Lifecycle: delete the row on completion, death, and **despawn via `despawn_npc_identity` in `npcs.rs`** (review correction; not `actor_lifecycle.rs`).
   - Client rendering: **ordinary `NpcPhysics` interpolation.** At 12 m/s the per-tick delta is ~0.4 m — under the 2.0 m hard-snap threshold, so `RemotePresentationBuffer` renders it as fast smooth motion, exactly how NPC chase movement renders today. No new public subscription and no handwritten client wiring for v1 (the new private table still triggers the standard binding regen, §3.6). (Extending `SpecialMovementRuntime` to NPC owners is the polish path if the smoothing reads poorly — deferred.)

### 3.6 Client changes

Phase 1 (functionality) needs **no handwritten client changes**: player displacement plays through the existing `SpecialMovementRuntime` path on local and remote clients (sampler is kind-agnostic — verified); NPC displacement renders through normal physics interpolation; the resistance affix auto-appears on the character sheet. Note (review correction): `PendingKnockback` and `NpcForcedMovement` are new tables and therefore **module schema changes even though private** — this repo regenerates bindings after all server schema changes (private tables produce generated C# types too), so phases 1–2 each require the canonical binding regen and the schema-change publish path; what they don't require is any hand-authored client wiring.

Phase 3 (presentation polish):

- **Player reaction anim**: trigger a directional reaction when a `SpecialMovementRuntime` row with kind `KNOCKBACK` arrives (`EntityRegistry.OnSpecialMovementRuntime*` → entity → `PlayerAnimator`). Incoming direction = `-(end - start)`. v1 reuses the stagger clips/states via `CombatStatusReactionController.TriggerStagger`; a dedicated knockback clip set (new `CombatAnimationSet` field + `slot_knockback_*` slots on layer 2, `ClearPresentationForStagger`-style visual interrupt) is a follow-up.
- **NPC reaction**: v1 keeps `NpcAnimationController.PlayHit` (fires on HP drop). Directional NPC reactions are out of scope.
- **Feel**: optional `MeleeContactHitstop.Play` on knockback impact; there is no screenshake system to hook.
- **Immune feedback**: optional "Immune" floater — needs a `CombatEvent` metadata emission (`metadata_*` fields exist) and a `FloatingCombatText` case. Deferred.

## 4. Interaction rules (explicit)

| Situation | Ruling |
|---|---|
| Victim casting a non-movement spell | Cast continues (verified: special-movement ticks don't bump `voluntary_move_epoch`) |
| Victim mid-dash / gap-close / dodge | Knockback terminates the movement delivery (cast fizzled if movement-owned, `movement_action_state` deleted) and takes over from current position |
| Victim casting a movement-delivery spell | That cast is fizzled (it owns the track being replaced) |
| Stagger + knockback on the same hit | Knockback wins regardless of packet resolution order (§3.5.4) |
| Two knockbacks overlap | Latest wins; restarts from current position (pk = owner) |
| Rooted victim | Still knocked back (root ≠ anchor) |
| Airborne victim | Pushed in XZ; Y resolves to terrain (existing track behavior); jump state re-seeds at track end |
| Knockback into wall/obstacle | Stops at block: baked vs static geometry, live vs `ActiveWorldObstacle` |
| Knockback off a ledge | Y follows terrain down (accepted; no aerial arc in v1) |
| LOS at impact | Never re-checked (per combat geometry contract — knockback inherits the delivering action's targeting rules) |
| Damage vs knockback order | Damage first (existing packet queue order: damage packet precedes impact effects) |
| Resistance ≥ 1.0 (or effective distance < min) | No shove, no track, no reaction, **no preemption** — the victim's dash/cast is untouched |
| Lag comp | Rewind barrier stamped on every shove tick (players: automatic via `commit_player_physics`; NPCs: stamped per commit by the new mover) |

## 5. Full touchpoint inventory

**Server — effect rail**
- `server/src/spells/manifest.rs` — `ImpactEffect` alias → enum (`:385`); ripple through `SpellSecondaryTunables` consumers
- `server/src/spells/catalog.rs` — `ImpactEffectRow::Knockback` + conversion (`:431-548`, `:1265+`)
- `server/src/spells/casting.rs` / `server/src/combat/projectiles.rs` — `push_impact_effect_packets` gains hit-site direction (`casting.rs:237`, `projectiles.rs:359`) at all call sites (direct `casting.rs:6349`, area `casting.rs:5917`, projectile `projectiles.rs:1536`)
- `server/src/combat.rs` — `EffectPacket::Knockback` (`:3371`), `queue_effect` arm (`:3416`), `PendingKnockback` table + `resolve_pending_effects` arm (`:3768`) including `mark_harmful_combat_action` on resolution (combat entry for zero-damage pushes, cf. `:3954`/`:4572`), `start_knockback_shove` (generalizing `:4778-4847`), stagger no-op composition rule (`:4747/:4778`), scoped runtime deletes (`:4729-4733`), `StatusEffectKind::KnockbackResistance` (`:2150`) + `TemporaryCombatModifiers` accumulation + accessor (`:5520-5629`)
- `server/src/actor_lifecycle.rs` — unconditional `special_movement_runtime` delete in `clear_transient_actor_state` (`:164`), compensating for the scoped delete in `clear_active_cast`
- `server/src/spells/casting.rs` — scoped runtime delete in `clear_active_cast` (`:4086-4091`); `is_externally_imposed_movement_kind` helper; `KNOCKBACK` kind constant
- `server/src/progression.rs` — `MeleeImpactEffectDefinition::Knockback` (`:368`) + mapping in `melee_impact_effects_for_ability_id` (`:2487`); melee impact push site (`melee.rs:5020/:5175`)
- `server/src/progression_catalog.shared.json` — new combat rules (§3.2); knockback authored on pilot spells; resistance buff spell (optional)

**Server — displacement**
- `server/src/game_loop.rs` — **scene-aware ground-Y fix** in `tick_special_movement_runtimes` (`:1585-1593`, switch to `_for_scene` variant matching bake); `tick_npc_forced_movement` (new)
- `server/src/npcs.rs` — `NpcForcedMovement` table (new), apply + bake path, top-of-loop AI gate (before `:1329`), cleanup in `despawn_npc_identity`, `NpcTemplate.knockback_resistance` (private authored struct only, `:286-310`)
- `server/src/npc_catalog.shared.json` — optional per-template resistance values
- `server/src/combat/position_history.rs` — NPC rewind-barrier stamp per forced commit (API is identity-keyed; verify NPC support)

**Server — resistance**
- `server/src/inventory.rs` — `EquipmentModifierTotals.knockback_resistance` (`:2248`), `MODIFIER_KNOCKBACK_RESISTANCE` (`:130-153`), `apply_modifier_value` arm + clamp (`:4043`), `AFFIX_KNOCKBACK_RESISTANCE_MINOR` seed (`:979+`)
- `server/src/derived_stats.rs` — only if Fortitude-derived resistance is ever revived (**decided: deferred**)

**Client (phase 3 only)**
- `Assets/Arena/Runtime/Entity/EntityRegistry.cs` — knockback-kind reaction dispatch (`OnSpecialMovementRuntime*`, `:713-728`)
- `Assets/Arena/Runtime/Presentation/CombatStatusReactionController.cs` / `PlayerAnimator.cs` — dedicated reaction (v1 reuses `TriggerStagger`)
- `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs` + `CombatAnimationSetBinder.cs` + `Arena_Character.controller` — dedicated clips/slots (deferred)
- `Assets/Arena/Runtime/Combat/StatusTooltipResolver.cs` — `KnockbackResistance` buff tooltip
- Generated bindings — **regen required in phases 1 and 2** (new tables = schema change even when private; canonical regen = harness-featured cargo build + `spacetime generate --bin-path`, per `docs/project-structure.md:77`)

**Not touched (by contract)**: LOS/query raycast paths (`raycast_world_with_layout_for_scene_with_stats`, `ServerLosCollisionData`) — knockback uses *movement* geometry only, which is the correct dataset for displacement per the combat geometry contract.

## 6. Phasing

1. **Phase 1 — server core (players)**: scene-aware mover Y fix (prerequisite), effect variant end-to-end (`ImpactEffect` enum, packet, pending table, resolve + combat-entry mark), `start_knockback_shove` + composition/preemption/teardown rules, resistance plumbing (equipment + buff kind + NPC template field parsed but unused), combat rules, 1–2 pilot spells authored. Deploy via the **schema-change publish path** + catalog re-sync (`publish_progression_catalogs`) + canonical binding regen — `ops/republish-catalog.sh` is explicitly for no-schema changes only (`ops/republish-catalog.sh:4`) and does not apply here. *Playable with zero handwritten client changes.*
2. **Phase 2 — NPC displacement**: `NpcForcedMovement` + mover + top-of-loop AI gate + lifecycle + per-commit rewind stamps; author resistance on heavy/boss templates. Same schema-change publish + regen requirement (new table).
3. **Phase 3 — presentation polish**: knockback reaction dispatch + clips, tooltip entry, optional hitstop/immune floater.

Verification (per phase, live): headless probe in the `ops/s*-probe` style — cast a pilot knockback spell at a fixture target; assert displacement distance/direction from `PlayerPhysics`/`NpcPhysics` deltas; repeat with resistance gear equipped (scaled) and an immune NPC (unmoved); shove-into-wall asserts the baked stop; stagger+knockback pilot spell asserts the composition rule; dash-victim case asserts the dash cast fizzles; zero-damage push asserts the victim enters combat (regen stops); disconnect mid-push asserts no runtime row survives teardown. Catalog parse errors self-report at boot via the `deny_unknown_fields` panic.

## 7. Decision log

**Decided (owner, 2026-07-17 reviews):**
- AoE knockback direction is radial from area center.
- Knockback resistance applies to the existing stagger shove (behavior change: resist gear shrinks stagger shoves) — but stagger keeps its authored 0.45 m / 100 ms (4.5 m/s) feel; it is *not* retuned to the knockback speed rule.
- Fortitude-derived resistance is deferred.
- Ordinary (non-movement) casts continue through a knockback; interruption is authored by composing with Stagger.
- Hostile zero-damage pushes count as harmful combat actions (combat entry, regen interruption).

**Still open:**
- Tuning defaults: 12 m/s push speed, 10 m distance cap, 0.6 gear cap — all in `combat_rules`, cheap to retune post-playtest.

## 8. Out of scope / future

- Aerial launches / knock-up arcs (movement model has no forced airborne path; large new mechanic).
- Eased (non-linear) push curves — `SpecialMovementRuntime.path_mode` is LINEAR/INSTANT today; an `EASE_OUT` mode is a contained follow-up if linear feels stiff.
- Directional NPC reactions / NPC reaction-pipeline unification.
- Knockback-on-collision damage ("slammed into a wall") — the baked-vs-intended distance delta is available if ever wanted.
- Extending `SpecialMovementRuntime` to NPC owners for perfectly crisp NPC push rendering (only if phase-2 interpolation reads poorly).
