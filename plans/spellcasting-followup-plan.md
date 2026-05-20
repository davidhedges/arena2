# Spellcasting Follow-Up Plan

## Why This Exists

Movement and local presentation work exposed a separate spellcasting problem:

- projectile launch/pathing was being authored from stale authoritative caster positions
- local VFX spawn smoothing made that mismatch more obvious
- a client-only attempt to "straighten" projectiles caused visual hit mismatch

This document captures the current spellcasting state and the remaining work so it is not lost.

## What Was Fixed

### 1. Cast-Frame Alignment

`CastRequest` now carries the local cast frame:

- `cast_input_tick`
- `cast_pos_x`
- `cast_pos_y`
- `cast_pos_z`
- `cast_yaw`

The server sanitizes that client-authored cast frame against current authority and uses it for spell execution when reasonable.

This fixed the main bug where moving while casting caused projectiles like `FIREBALL` to launch from an older server position and travel on the wrong line.

### 2. Removed The Bad Client-Only Straight-Path Override

The temporary local-only straight-path hack in projectile VFX was removed.

That hack made the local projectile look straighter, but it also broke alignment with authoritative updates and caused visible impact mismatch.

Projectile VFX now follows authoritative updates again.

### 3. Reduced Projectile Homing From "Full Seeker" To "Soft Assist"

`FIREBALL` and `ICICLE` no longer retarget for their whole lifetime.

They now steer only during a short initial homing window:

- `FIREBALL`: short soft-homing window
- `ICICLE`: even shorter soft-homing window

This is the current compromise between:

- slight assist
- future dodgeability
- visually legible projectile travel

## Current State

The spell system is now in a better place, but it is still transitional.

Current behavior:

- projectile spawn timing is closer to the player's true cast frame
- local projectile VFX is no longer overriding authoritative trajectory rules
- projectile steering still exists, but only briefly

This should be treated as "usable and testable," not "finalized."

## Primary Remaining Work

### 1. Formalize Trajectory Classes

Right now projectile behavior still lives mostly in ad hoc server code.

Spell trajectories should become explicit per-spell policy, for example:

- `StraightProjectile`
- `SoftHomingProjectile`
- `SeekerProjectile`
- `InstantBeam`
- `GroundAoE`
- `Channel`

This needs to be authoritative and shared conceptually between server logic and client presentation.

Do not return to spell-name-specific client hacks.

### 2. Add Better Spell Instrumentation

Movement now has usable debug instrumentation. Spells do not.

Add instrumentation for:

- cast input tick sent by client
- authoritative caster tick at execution
- cast-frame position used by server
- projectile spawn origin
- projectile steering updates / retarget count
- impact position
- target position at impact

Without this, moving-target validation will remain slow and ambiguous.

### 3. Validate Against Moving Targets

The current fix felt acceptable in limited testing, but it was not validated against a real moving target for long enough.

Required test cases:

- caster moving, target stationary
- caster stationary, target moving laterally
- both caster and target moving
- target changing direction during projectile flight
- future dash test once dash exists

This is the next important validation step.

### 4. Decide Final Homing Tuning

The current homing windows are temporary tuning values.

Still needs tuning:

- homing window duration
- turn rate
- projectile speed
- projectile radius
- any future turn-budget cap

Design goal:

- projectile gets slight launch assistance
- projectile does not chase forever
- good players can dodge with movement and later with dash

### 5. Clarify Cast-Time / Release-Time Ownership

The current cast-frame fix is strongest for instant projectile launches.

Still needs explicit rules for:

- cast-time spells: use cast start frame or cast completion frame?
- release-based spells: use release frame or cast start frame?
- channels: how often should aim/facing be revalidated?

This should be written down as an intentional policy before more spells are added.

### 6. Separate Cast Presentation From Trajectory More Cleanly

The correct model is:

- local cast flash / hand-socket presentation is cosmetic
- projectile path is authoritative
- impact / fizzle is authoritative

That distinction should stay clean in the implementation.

If local presentation ever needs extra polish, add it at cast presentation time, not by silently changing projectile pathing.

## Recommended Execution Order

### Phase 1: Instrument Spells

Deliverables:

- debug overlay / logging for cast frame vs authoritative execution
- projectile steering metrics
- impact-vs-target diagnostics

### Phase 2: Validate With Moving Targets

Deliverables:

- moving-target test pass
- written observations about misses, unfair hits, and readability

### Phase 3: Formalize Trajectory Policy

Deliverables:

- explicit spell trajectory categories
- per-spell trajectory assignment
- shared assumptions between server behavior and client VFX

### Phase 4: Tune Soft Homing

Deliverables:

- final homing windows / turn rates
- reasonable dodgeability target
- future dash compatibility

### Phase 5: Resolve Cast-Time / Release-Time Semantics

Deliverables:

- documented ownership of launch frame for delayed and charge/release spells
- implementation aligned with that policy

## Success Criteria

Spellcasting follow-up is in a good place when:

- moving while casting no longer causes obvious off-line projectile launches
- projectile path and impact location read honestly on screen
- slight homing helps targeting without making projectiles feel unavoidable
- future dash can plausibly dodge soft-homing projectiles
- spell behavior is driven by explicit trajectory policy, not by scattered one-off logic

## Bottom Line

The worst spell bug was a timing/ownership bug:

- cast authored at one frame locally
- projectile spawned from another frame authoritatively

That is now improved.

The remaining work is to make spell trajectories explicit, measurable, and tunable before adding more projectile types or dash interactions.
