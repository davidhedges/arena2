# Charge As Melee Gap Closer Migration Plan - 2026-05-06

## Decision

Charge should migrate from a selectable `MOVEMENT` ability with bespoke `CombatAnimationSet` charge fields to a selectable `MELEE` ability with `gameplay.gap_close` and normal melee presentation. That presentation can be single-clip or phased depending on the authored animation content; it must not be forced into phased mode just because the gameplay includes travel.

For this migration, do not carry forward the airborne-specific Charge animation fields. Charge should be grounded-only until a deliberate airborne Charge feature is designed and authored later. This is a gameplay eligibility change, not only an animation cleanup: airborne Charge should reject instead of falling back to a reused or partial clip set.

## Reasoning

The player-facing fantasy of the current Charge is a weapon attack, not pure movement. The animation is a large sword swing, the gameplay row already carries target damage, block/parry behavior, and a stun impact effect, and designers should tune it beside other weapon attacks.

The production-grade classification rule should be:

```text
Primary domain = what system owns impact resolution and combat rules.
Displacement = capability.
Animation = presentation.
```

Under that rule, a sword-first Charge belongs to melee. Its displacement should remain server-authoritative special movement, but melee should own hit windows, damage, defense behavior, resource/cooldown conventions, and combat presentation identity.

This is not a rejection of `MOVEMENT` abilities. `MOVEMENT` remains appropriate for actions whose primary outcome is movement: dodge, blink, dash without weapon impact, or repositioning skills. Charge is different because the movement delivers a weapon strike.

## Current State

Charge-like abilities are currently authored as `gameplay.kind: "MOVEMENT"` with `gameplay.delivery.kind: "DASH_TO_TARGET"` in `server/src/progression_catalog.shared.json`.

Runtime movement delivery:

- validates target, resource, cooldown, and active-cast state
- computes a contact destination
- starts `special_movement_runtime`
- starts an active cast for travel duration
- resolves arrival/fizzle at the end
- applies movement-delivery damage and impact effects

Client presentation currently maps `MovementActionState.Kind == "DASH_TO_TARGET"` or legacy `"CHARGE"` to `CombatAnimationCategory.Charge`. `PlayerAnimator` then uses the dedicated Base Layer Charge controller path and the `CombatAnimationSet` fields:

- `chargeStart`
- `chargeLoop`
- `chargeEnd`
- `airChargeStart`
- `airChargeLoop`
- `airChargeEnd`

This keeps Charge separate from normal melee/phased authoring even though its product identity is a weapon attack.

Current movement-delivery Charge is also server-authority gated for actual displacement. The client sends `CastRequest` with a predicted position/yaw snapshot, but it does not locally create `special_movement_runtime`; local special movement begins after the authoritative `SpecialMovementRuntime` row arrives. Migrating Charge to melee gap close therefore is not a proven loss of local displacement prediction. The real responsiveness risks are:

- current melee gap closers intentionally wait for authoritative server confirmation before local strike animation, cooldown prediction, or resource reservation
- current melee dispatch does not send the same input tick / predicted movement context contract that movement delivery sends through `CastRequest`

Both risks should be handled deliberately before the migrated Charge ships.

## Existing Gap-Close Support

Melee gap closers already use the shared special movement path. A `MELEE` ability with `gameplay.gap_close` syncs into `melee_gap_close_catalog`; melee dispatch resolves the target destination, calls `bake_linear_special_movement` for non-teleports, and starts `begin_special_movement` on accepted melee commit.

The important distinction is:

```text
MELEE + gap_close still uses special_movement_runtime for authoritative displacement.
MELEE + gap_close lets melee own the attack, hit windows, damage, block/parry, pending impact, and presentation identity.
```

That is the desired shape for Charge.

## Target Shape

Charge should become:

```text
gameplay.kind = MELEE
gameplay.gap_close = present
presentationMode = SingleClip or Phased, chosen from the actual authored clips
caster movement state = grounded-only
impact timing = authored melee Hit Windows
real displacement = special_movement_runtime
presentation phases = optionally coupled to special_movement_runtime lifetime only for true phased travel clips
```

The grounded Charge clips should move from the old Charge fields into normal melee attack authoring. Do not force every Charge into phased presentation: Warrior Charge uses a single run-attack clip, while Paladin Charge can remain phased because that pack has real `Charge_Run_Attack_Start` / `Loop` / `End` clips.

The airborne Charge clips should not be migrated in this pass. If airborne Charge becomes important later, add it as a deliberate feature with its own gameplay eligibility, authoring validation, and presentation clips.

Only movement-coupled phased attacks should use special-movement-driven phase control. This is not a default rule for every melee gap closer. Most melee gap closers should continue using authored clip timing unless a designer explicitly opts that phased action into `drivePhasesFromSpecialMovement`.

The migrated Charge should preserve or deliberately retune these current behavior points:

- target damage
- block/parry behavior
- stun or replacement impact effect on successful hit
- global cooldown and named cooldown intent
- accepted-action resource gain, or an explicit design decision to move that reward to hit/impact
- target acquisition range versus final impact range
- movement launch from the intended predicted player position/yaw
- whether target-facing is required before launch

## Migration Steps

1. Author Charge strikes before converting catalog rows.
   - Warrior: likely `WARRIOR_CHARGE`.
   - Paladin: likely `PALADIN_CHARGE`, unless shield/sword content needs a different profile-specific id.
   - Add a `WeaponMeleeAttackAuthoring` entry to each affected `CombatAnimationSet`.
   - Set `combat.id` to the eventual Charge ability `action_id`.
   - Re-export `server/src/melee_manifest.shared.json` before changing the ability to `MELEE`.
   - This order matters because existing validators require every `MELEE` ability action id to match an authored strike id in the class combat profile.

2. Add melee presentation entries in each affected `CombatAnimationSet`.
   - Use single-clip presentation for Charge content that is authored as one readable attack clip.
   - Use `presentationMode = Phased` only when the animation pack provides a real start/loop/end set.
   - Set `drivePhasesFromSpecialMovement` only on phased attacks whose travel must hold Loop until authoritative special movement ends. Leave it disabled for single-clip attacks and for phased attacks that should use normal authored timing.
   - Set caster movement eligibility to grounded-only.
   - Add explicit Hit Windows that match the sword impact moment.
   - Tune lower-body unlock and visual interruption against segmented elapsed time.

3. Convert Charge ability rows from `MOVEMENT` to `MELEE`.
   - Preserve acquisition range as melee `range`.
   - Move travel data into `gameplay.gap_close`.
   - Preserve target defense behavior as melee `block_behavior` / `parry_behavior`.
   - Preserve or intentionally retune damage as melee `base_damage`.
   - Preserve resource/cooldown/global-cooldown intent using normal melee ability fields.
   - Decide `gap_close.requires_target_facing` explicitly. Movement delivery did not enforce this gate; leaving a default value here would be a silent behavior change.
   - Drop movement-delivery `cast_time_ms` deliberately. Today it is validated on movement delivery but travel duration comes from distance/speed. If designers want a wind-up before movement, add that as explicit melee/gap-close policy rather than assuming the old field survives.

4. Add melee impact-effect support before migrating Charge if Charge still needs stun on hit.
   - Current movement delivery has `impact_effects`.
   - Melee currently owns damage, stagger, impact area, and projectile release, but not a fully generic `APPLY_STATUS` impact effect row.
   - Add a shared status-application impact shape to melee rather than keeping Charge in movement delivery solely for stun.
   - This is critical path, not a later cleanup. Migrating current Charge without this either removes the authored stun or requires a deliberate design retune.

5. Route presentation through the normal melee animation path.
   - Server melee cast events should carry the authored Charge strike id.
   - Client should request `CombatAnimationCategory.MeleeSkill`, not `CombatAnimationCategory.Charge`.
   - `PlayerAnimator.PlayMeleeAnimation` should resolve the phased Charge entry by action id like any other phased melee attack.
   - Phased Charge presentation should be authored with `drivePhasesFromSpecialMovement` only for real start/loop/end clip sets, so Start plays once, Loop holds while the matching special movement is active, and End plays when special movement ends.
   - Do not make all gap closers use special-movement phase control. It is an explicit phased-action authoring choice for attacks whose presentation is meant to cover the travel itself.

6. Preserve or deliberately replace current responsiveness behavior.
   - Do not claim local displacement prediction is preserved merely because both paths use `special_movement_runtime`; today that track is created by the server.
   - Melee gap closers must not locally predict only the attack animation while displacement remains server-created. Animation-only prediction can produce a visible Charge swing with no movement when the server rejects, delays, or resolves no meaningful displacement.
   - Current decision: wait for authoritative melee cast/special-movement acceptance before playing gap-close melee presentation. Reintroduce local responsiveness only when the client can predict the special movement track and reconcile it.
   - The current melee request already sends local position and yaw; keep input tick / predicted position / predicted yaw as a future networking quality upgrade rather than a Charge-specific dependency.
   - Use playtest/trace coverage for local-caster Charge latency when tuning travel speed and impact timing.

7. Preserve or deliberately retune accepted-action resource gain.
   - Current Charge grants primary resource on accepted movement launch.
   - Normal melee does not currently have the same generic `ON_ACCEPTED` effect path.
   - Either add that support to melee or retune the resource reward to an explicit melee event such as hit/impact.
   - Do not silently drop Rage/Vengeance generation during the migration.

8. Remove or hide bespoke Charge presentation fields.
   - Once all existing Charge content is migrated, remove the inspector surface for `Charge Ability`.
   - Remove runtime use of `TriggerChargeStart`, `IsCharging`, `TriggerChargeEnd`, and Base Layer Charge states only after validation proves no live path references them.
   - Do not migrate `airChargeStart`, `airChargeLoop`, or `airChargeEnd`.
   - Include the parallel `SharedActionProfile` Charge fields in cleanup, not only the `CombatAnimationSet` fields.
   - Remove the `CombatAnimationCategory.Charge` routing only after all `DASH_TO_TARGET`/legacy `"CHARGE"` client paths are either gone or deliberately redirected.
   - Clean up the legacy server `"CHARGE"` movement-action alias and no-op `start_charge` reducer only after generated bindings and old clients are no longer expected to call them.

## Validation

Add or update checks so the refactor cannot silently regress:

- existing validator coverage: every `MELEE` ability action id must match an authored strike id
- every Charge ability must be grounded-only during this migration
- every Charge ability must have `gameplay.gap_close`
- every Charge phased presentation must have at least start/end or start/loop/end grounded clips
- every Charge phased presentation must have at least one Hit Window
- every migrated Charge with stun design intent must have an equivalent melee impact-effect row
- every migrated Charge must explicitly choose `requires_target_facing`
- every migrated Charge must explicitly preserve or retune primary resource gain
- melee gap closer pending impact uses `gap_close.impact_range`, not acquisition range
- blocked required-arrival gap closers reject before resource spend, cooldown, cast event, and pending impact
- no selectable Charge path routes to `CombatAnimationCategory.Charge`
- no migrated Charge relies on airborne Charge fields
- local-caster Charge traces should show the intended animation start source and latency classification

## Non-Goals

- Do not implement airborne Charge in this migration.
- Do not preserve the old Charge controller path as a parallel production path.
- Do not classify all weapon-looking movement as melee by animation silhouette alone.
- Do not keep movement-delivery Charge solely because it already has status impact effects; generalize melee impact effects instead.
- Do not enable root motion as movement authority.
- Do not silently accept loss of current resource gain, stun, or responsiveness behavior. Preserve them or retune them explicitly.

## Implementation Status

- 2026-05-06 implementation status:
  - `WARRIOR_CHARGE` and `PALADIN_CHARGE` are now authored as `MELEE` abilities with `gameplay.gap_close`.
  - Both Charge abilities use grounded-only targeting, explicit `requires_target_facing: false`, `STOP_AT_BLOCK` gap-close collision, 32 melee damage, and authored melee stun impact effects.
  - Melee gap closers now author `arrival_epsilon`; Charge uses `0.05`, while Earthshatter keeps its previous `0.10` tolerance.
  - Melee gap closers wait for authoritative movement+animation acceptance. Server authority owns special movement resolution and impact; the client does not predict animation without predicting movement.
  - Warrior Charge keeps its accepted-action Rage grant through the generic melee accepted-action effect bridge.
  - TwoHandedSword and SwordAndShield now have grounded melee Charge entries and no migrated airborne Charge clips.
  - The legacy Charge clip fields have been removed from `CombatAnimationSet` / `SharedActionProfile` authoring, stale serialized asset fields were cleaned from the shipped animation set assets, and the old `slot_charge_*` placeholder clips were deleted.
  - The client no longer exposes `CombatAnimationCategory.Charge`, `PlayerAnimator` no longer owns `PlayChargeAnimation` / `EndCharge` / `IsCharging`, and `EntityRegistry` no longer routes `DASH_TO_TARGET` / legacy `"CHARGE"` movement rows into bespoke Charge presentation.
  - `Arena_Character.controller` no longer carries the old Base Layer `ChargeStart` / `ChargeLoop` / `ChargeEnd` states or `TriggerChargeStart` / `IsCharging` / `TriggerChargeEnd` parameters.
  - The generated `start_charge` client binding, generated project include, server no-op reducer, and legacy movement-action `"CHARGE"` alias were removed.
  - The temporary saved-spec fallback that treated stale `MOVEMENT` Charge assignments as abilities was removed; Charge must now be saved as an ability assignment.
  - Local animation-only prediction for melee gap closers was disabled; Charge now waits for authoritative movement+animation acceptance unless/until predicted special movement is implemented.
  - `PALADIN_CHARGE` opts into special-movement-driven phase control with `drivePhasesFromSpecialMovement` because it has real publisher start/loop/end charge clips.
  - `WARRIOR_CHARGE` is authored as single-clip melee presentation using the GreatSword `Run_Attack_01` clip; its phased clips are cleared and `drivePhasesFromSpecialMovement` is disabled.
  - Other melee gap closers and phased melee attacks still use authored clip timing unless their own phased attack entry explicitly enables the flag.
  - `cargo test` passes for the server crate after migration.
  - `dotnet build Assembly-CSharp.csproj --no-restore` passes after client presentation cleanup.
- 2026-05-07 implementation status:
  - `CombatAnimationRequest` can now mark an authoritative melee presentation as special-movement-coupled when the local `special_movement_runtime` kind matches the authored phased action.
  - Special-movement-coupled phased melee no longer samples or scrubs start/loop/end across runtime duration. Start plays once, Loop holds without repeated `Animator.Play`, and End is requested once when `special_movement_runtime` is deleted.
  - Movement-driven phased melee sets weapon/combat visual ownership without forcing the base layer into combat idle before the phased clip set starts.
  - `dotnet build Assembly-CSharp.csproj --no-restore` passes.
- Remaining validation is playtest-level feel tuning: arrival tolerance, travel speed, hit timing, whether accepted resource gain feels responsive enough with the existing server-authoritative resource update, and whether predicted special movement is worth adding later.
