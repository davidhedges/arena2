# Combat Animation Architecture Plan

This document proposes a production-ready cleanup of the combat animation system.

It is intentionally not a "smallest fix" plan.

The goal is to replace the current patchwork of:

- strike triggers
- phased melee piggybacking on charge states
- local prediction replay suppression
- authoritative replay exceptions
- ad hoc animation priority rules

with a system that is explicit, debuggable, and stable under continued feature work.

## Scope

This plan covers client-side combat presentation architecture:

- animator controller topology
- combat animation request routing
- animation priority / interruption policy
- phased melee playback
- local prediction vs authoritative replay behavior

This plan does not change:

- server combat authority
- melee timing / damage rules
- auto-attack gameplay cadence
- progression / loadout rules

## Non-Negotiable Constraint

This cleanup must not leave competing animation architectures behind.

That means:

- no permanent dual-path system where both the old direct trigger model and the new request/arbiter model are considered valid
- no permanent reuse of charge topology for phased melee "just for a few cases"
- no hidden fallback path that still accepts old behavior after the new path is landed
- no ambiguous helper names that make old and new systems look equally supported

The desired outcome is not merely "works."

The desired outcome is:

- one obvious way to route combat presentation
- one obvious place to read priority rules
- one obvious topology for phased melee
- one obvious replay/arbitration path

If a migration step cannot end by deleting or quarantining the old path clearly, that step is incomplete.

## Status Of Existing Behavior

Some of the desired behavior already exists today, but it exists as scattered patches rather than as one visible policy.

Examples already present in the code:

- auto-attack presentation is partially suppressed while a skill presentation is active
- non-auto-attack melee is intended to preempt active auto-attack style presentation
- authoritative replay suppression already distinguishes some melee sources

So the goal of this plan is not "invent all-new behavior from scratch."

The goal is:

- move the current intended behavior into one explicit system
- remove accidental behavior created by reused states and hidden controller semantics
- make future behavior changes happen in one place instead of through more patches

## Why This Exists

The recent auto-attack and `Skyfall` work exposed the same structural problem repeatedly:

- combat gameplay is increasingly coherent
- combat authoring is much cleaner than before
- combat animation dispatch is still a soup of overlapping mechanisms

Today, the main pain points are:

- phased melee still reuses the charge animation path
- melee, auto-attack, spells, and charge share too much presentation machinery
- animation priority is being enforced as a growing set of patches
- local prediction and authoritative replay both feed the same animator through different paths
- "what should interrupt what?" is not encoded as one explicit contract

This is the wrong layer to keep patching indefinitely.

## Desired End State

### High-Level Outcome

Combat presentation should have one explicit request pipeline and one explicit priority policy.

The animator should no longer infer semantics from reused states.

Instead, the system should know:

- what category of action is being requested
- whether it is predicted or authoritative
- whether it can interrupt the currently playing action
- whether it should be ignored, queued, or take over immediately

### Presentation Categories

The production-ready model should distinguish at least these presentation categories:

- `melee_skill`
- `auto_attack`
- `spell`
- `charge`
- `defense`
  - block start / loop / end
  - parry
- `movement_action`
  - dodge
  - knockdown / get-up if you still consider those part of the combat presentation layer

The important point is not the exact names. The important point is that unrelated categories stop sharing the same controller states by accident.

### Core Priority Policy

The system should express a clear rule table instead of accumulating special cases.

Recommended default policy:

- `melee_skill` interrupts `auto_attack`
- `spell` interrupts `auto_attack`
- `charge` interrupts `auto_attack`
- `auto_attack` never interrupts `melee_skill`
- `auto_attack` never interrupts `spell`
- `auto_attack` never interrupts `charge`
- `auto_attack` may replace an older `auto_attack`
- `queued_followup` should be treated as `melee_skill`, not as `auto_attack`

This is mostly a formalization of the intended current behavior, not a proposal to change combat feel in Phase 3.

### Tie Rules

The priority table must also define what happens when categories are equal or adjacent.

Recommended rules:

- `melee_skill` vs `melee_skill`
  - replace immediately
- `spell` vs `spell`
  - replace immediately
- `melee_skill` vs `spell`
  - latest accepted request replaces immediately
- `charge` vs `charge`
  - replace immediately
- `auto_attack` vs `auto_attack`
  - latest request replaces immediately

This plan does not recommend queueing presentation by default.

Queueing should only exist where gameplay already has an explicit queue concept, such as combo followup release, not as a general-purpose animation fallback.

Defense and movement actions should remain governed by their own explicit rules rather than "whatever trigger won first."

### One Combat Animation Request Path

All combat presentation should pass through a single request entry point in `PlayerAnimator`.

Conceptually:

```csharp
RequestCombatAnimation(new CombatAnimationRequest {
    kind = "SKYFALL_1",
    category = CombatAnimationCategory.MeleeSkill,
    source = CombatEventSources.PlayerInput,
    authority = CombatAnimationAuthority.Predicted,
    priority = CombatAnimationPriority.Skill,
    playback = CombatAnimationPlayback.Phased,
})
```

This is the main architectural change.

Instead of calling:

- `TriggerStrike(...)`
- `TriggerStyleAction(...)`
- `TriggerSpell(...)`
- `BeginCharge()`

from multiple unrelated sites, the system should first construct a combat animation request, then let one arbitration layer decide what to do.

### Final-State API Shape

The end state should not leave a parallel "old direct API" beside the new request path.

Recommended end-state surface in `PlayerAnimator`:

- `RequestCombatAnimation(CombatAnimationRequest request)`
- small internal helpers for category-specific execution
  - `PlayMeleeSkill(...)`
  - `PlayAutoAttack(...)`
  - `PlaySpell(...)`
  - `PlayCharge(...)`
  - `PlayDefenseAction(...)`

The following should not remain as first-class public entry points once migration is complete:

- `TriggerStyleAction(...)`
- `TriggerSpell(...)`
- `BeginCharge()` as an unrelated direct path

Instead:

- a real charge becomes `RequestCombatAnimation(category=Charge, ...)`
- a phased melee attack becomes `RequestCombatAnimation(category=MeleeSkill, playback=Phased, ...)`
- auto-attack becomes `RequestCombatAnimation(category=AutoAttack, ...)`

This ensures Phase 1 does not create a permanent double surface area.

### Naming And Surface-Area Rule

By the end of the migration, the public combat-presentation API should read like one coherent design.

That means:

- no old method names kept alive as aliases for the new system unless they are clearly marked transitional and scheduled for deletion
- no mixed terminology where "style action," "strike trigger," "charge path," and "phased melee" all remain first-class routes
- category names and playback terms should map cleanly to the final architecture

This matters for humans and for LLM-assisted work.

A future reader should not have to infer which path is canonical.

## Recommended Architecture

## 1. Introduce an Explicit Combat Animation Request Model

Add a small request model in presentation code.

Suggested fields:

- `actionId`
- `category`
- `source`
- `authority`
  - `Predicted`
  - `Authoritative`
- `playbackMode`
  - `SingleClip`
  - `Phased`
- `priority`
- `interruptPolicy`
  - or derive this from category/priority instead of storing both
- `startedAtMs`
  - optional but useful for dedupe / debug

The goal is not to add data for its own sake. The goal is to stop passing bare strings and hidden assumptions directly into the animator.

## 2. Introduce a Combat Animation Arbiter in `PlayerAnimator`

`PlayerAnimator` should become the single authority on combat presentation takeover rules.

Responsibilities:

- receive combat animation requests
- inspect the currently active combat presentation
- decide:
  - `PlayNow`
  - `InterruptCurrentAndPlay`
  - `Ignore`
  - `Queue`
- track the currently active combat presentation in explicit state

Suggested tracked runtime state:

- current combat presentation category
- current action id
- current authority
- whether current playback is phased
- whether current playback is auto-attack
- whether the current request is interruptible by another category

This replaces the current patchwork of:

- "was this style action started by auto attack?"
- "is charge active?"
- "is phased melee currently piggybacking on charge?"
- "should this source be suppressed?"

The arbiter should produce a small explicit decision:

- `PlayNow`
- `InterruptCurrentAndPlay`
- `IgnoreAsDuplicate`
- `DropAsLowerPriority`

That decision should be visible in logging/debug output.

## 3. Stop Reusing Charge States for Phased Melee

This is the most important structural change.

Phased melee should not continue to use:

- `ChargeStart`
- `ChargeLoop`
- `ChargeEnd`
- `IsCharging`
- `TriggerChargeStart`
- `TriggerChargeEnd`

That reuse was acceptable as a bootstrap mechanism. It is the wrong production topology.

### Recommended Topology

Give phased melee its own state path in the controller.

Suggested base-layer states:

- `PhasedMeleeStart`
- `PhasedMeleeLoop`
- `PhasedMeleeEnd`

Suggested parameters:

- `TriggerPhasedMeleeStart`
- `TriggerPhasedMeleeEnd`
- optionally `IsPhasedMeleeActive`

Then let inline melee authoring bind clips into those slots the same way strikes and spells already bind clip slots.

This immediately removes a large amount of ambiguity:

- charge is charge
- phased melee is phased melee
- air-charge is not pretending to be `Skyfall`

### Two-Clip Phased Attacks

The new phased melee path should support:

- `start + loop + end`
- `start + end`
- `start + loop`
- `loop + end`

without pretending that the missing piece is literally "charge loop."

That support should be part of the phased melee state machine itself, not a hack around charge transitions.

### Charge Survival

Charge remains a real presentation category in the final system.

This plan is not proposing to delete charge presentation.

The final shape is:

- real charge keeps its own dedicated states and parameters
- phased melee gets its own dedicated states and parameters
- they no longer share topology just because they both happen to be multi-stage animations

## 4. Separate Controller Regions by Combat Category

The current controller should be reorganized so state ownership is obvious.

Recommended shape:

- base locomotion / movement
- base melee attacks
  - light / heavy / weapon skill / auto-attack
- base phased melee
- upper-body or full-body spell states
- charge states
- defense states
- movement-action states

The exact layer split can vary, but the key is:

- no category should exist only because another category happened to be reusable

### Auto-Attack Presentation

Auto-attack can keep using strike-style clips if you want.

But it should enter that path as:

- category = `auto_attack`
- not as "some melee strike that happens to share a clip"

That distinction matters because the arbiter should make priority decisions using category, not just clip identity.

## 5. Make Prediction vs Authority Explicit in Presentation

The system should stop mixing authority concerns into every call site.

Recommended rule:

- predicted local input creates a predicted combat animation request
- authoritative replay creates an authoritative combat animation request
- the arbiter decides whether the authoritative request is:
  - duplicate and should be ignored
  - corrective and should replace
  - distinct and should play

This replaces the current model where replay suppression is scattered across:

- local melee prediction memory
- `EntityRegistry`
- source-specific special cases

### Example

For local melee:

- predicted `player_input` request plays immediately
- authoritative `player_input` replay is usually ignored if it matches

For local auto-attack:

- no prediction request exists
- authoritative `auto_attack` request is the first request and should be evaluated normally

For local queued combo followup:

- it should be treated as a predicted or authoritative `melee_skill`, depending on how your combo release path works
- but it should not be treated as equivalent to `auto_attack`

This specific current-path assumption should be verified against the live implementation during Phase 1 rather than treated as already proven.

## 6. Make Priority Data-Driven Enough to Be Visible

Do not hide the animation priority model in random `if` statements.

Recommended implementation:

- a small priority enum
- a helper that answers:
  - `CanInterrupt(current, incoming)`
  - `ShouldDrop(current, incoming)`

For example:

```csharp
enum CombatAnimationPriority
{
    AutoAttack = 10,
    Skill = 20,
    Spell = 20,
    Charge = 30,
    Defense = 40,
}
```

You do not need to overbuild this.

You do need a single obvious place where someone can read the rules.

## 7. Keep Authoring and Presentation Identity Aligned

The animation request should be keyed from the same authored action identity already cleaned up in melee.

That means:

- `progression_catalog.action_id`
- weapon-set melee authored id
- phased melee presentation identity
- combat animation request `actionId`

should all be the same authored action id

unless a different identity is explicitly intended.

Runtime slot ids should not be the primary animation identity.

## 8. Add Better Presentation Debugging

If this system is rebuilt, it should be easier to inspect than the current one.

Recommended debug surfaces:

- log one line per incoming combat animation request
- log arbiter decision:
  - played
  - interrupted current
  - dropped as duplicate
  - dropped due to lower priority
- expose current active combat presentation on the local player in a debug inspector or on-screen panel

This should reuse existing debug infrastructure where possible.

Specifically:

- prefer `LoadoutActionTrace` for request / replay / arbitration traces
- do not create a second parallel combat-animation logging channel unless `LoadoutActionTrace` proves insufficient

## Recommended Phases

## Phase 1: Define the Request and Priority Model

Goal:

- get the policy explicit before changing the controller

Work:

- add `CombatAnimationRequest`
- add category / authority / priority enums
- add a central arbitration helper in `PlayerAnimator`
- route existing callers through that helper even if they still map to current states temporarily
- verify current `queued_followup` presentation path before codifying it as a permanent rule

Exit criteria:

- all combat animation entry points go through one request method
- no direct call sites remain that bypass arbitration for melee/spell/auto-attack
- charge entry points either also route through the request method or have a scheduled migration step with an explicit deadline

## Phase 2: Add Dedicated Phased Melee Playback Path

Goal:

- stop using charge semantics for phased melee

Work:

- add dedicated phased melee states and parameters to `Arena_Character.controller`
- update `CombatAnimatorControllerUpgrader.cs`
- bind inline phased melee clips into phased melee slots
- remove phased melee dependence on charge parameters/states

Exit criteria:

- `Skyfall`-style attacks no longer touch charge states
- two-clip phased melee works on its own path
- no phased-melee code path writes `slot_charge_*`
- no phased-melee code path depends on `IsCharging`, `TriggerChargeStart`, or `TriggerChargeEnd`

## Phase 3: Separate Auto-Attack Presentation Category

Goal:

- make auto-attack category explicit in client presentation

Work:

- route `auto_attack` requests through the same arbiter
- keep clip reuse if desired
- enforce priority rules through category, not through ad hoc suppression checks

Exit criteria:

- skill interrupts AA
- AA never interrupts skill
- no behavior change is required beyond making the policy explicit and central
- no special-case soup needed in `EntityRegistry`

## Phase 4: Simplify Replay Suppression

Goal:

- authoritative replay handling becomes obvious and local

Work:

- move melee replay suppression decisions behind the request/arbiter layer
- keep `EntityRegistry` thin
- let source + authority + action id determine whether replay is duplicate or distinct

Exit criteria:

- replay suppression is no longer a pile of one-off client rules
- replay arbitration decisions are visible in `LoadoutActionTrace`
- local prediction vs authoritative replay can be explained from one request/decision path

## Phase 5: Delete Transitional Paths

Goal:

- remove the bootstrap compatibility once the new path is stable

Candidates for deletion:

- phased melee through charge states
- old auto-attack-specific suppression patches
- obsolete animator booleans/trigger reuse
- presentation-side assumptions that "style action" and "charge" are interchangeable

Exit criteria:

- each combat category owns its own path
- no important category exists only by piggybacking on another category’s states
- zero references to phased-melee playback through `IsChargingHash`
- zero phased-melee writes to `slot_charge_*`
- `_activeStyleActionIsAutoAttack` is deleted or reduced to a pure request/arbiter concern rather than a fallback state-shadow patch
- `TriggerStyleAction(...)` is gone as a primary combat entry point
- charge remains only as a real charge category, not as a hidden transport for phased melee
- no production code path exists where both the old and new routing designs are considered valid
- transitional APIs are either deleted or clearly isolated behind migration-only comments and deadlines

## Anti-Drift Rule

This work should be judged against one simple question:

"If a new engineer or LLM reads the combat animation code six months from now, will they see one design or two?"

If the answer is "two," the migration is not done.

Specific anti-drift requirements:

- every combat animation request must enter through one canonical request path
- every category must have one canonical execution path
- every replay decision must be made in one canonical arbitration layer
- obsolete state flags and helper methods should be deleted rather than left as "just in case" scaffolding
- comments should explicitly mark transitional code as temporary and name the phase that deletes it

## Risks

### Short-Term Risk

- controller topology changes can break current content if done too broadly at once
- phased melee migration can cause regressions in existing `Skyfall`-style attacks
- prediction/replay behavior can regress if arbitration is introduced without careful duplicate handling

### Main Mitigation

Land the work in phases:

1. make the request model explicit first
2. route current calls through it
3. then replace controller topology
4. then delete transitional paths

Do not do all of this in one jump.

## Regression Checklist

The risky phases should not be accepted without concrete repro checks.

Minimum checklist:

1. `Skyfall` with `start + loop + end`
   - local player
   - remote mirror
2. `Skyfall` with `start + end`
   - local player
   - remote mirror
3. auto-attack becoming due during `Skyfall`
   - gameplay hit still occurs
   - AA animation does not override the skill animation
4. skill animation starting during an active AA animation
   - skill interrupts AA presentation immediately
5. combo followup release
   - local predicted path
   - authoritative replay path
6. local predicted melee followed by authoritative replay of the same action id
   - no duplicate animation
7. spell cast while moving if upper-body spell presentation is allowed
   - still respects the request arbiter
8. real charge start / loop / end
   - unaffected by phased melee migration

## Out of Scope

This plan does not propose:

- changing server melee authority
- changing combat balance
- changing loadout logic
- changing the weapon-set authoring contract again

Weapon authoring is expected to stay conceptually the same:

- charge clips remain charge clips
- phased melee clips remain inline on melee attacks

The main change is controller topology and runtime routing, not another authoring-model redesign.

## When This Should Be Considered Done

This system is polished enough when the following are true:

1. A new melee skill can be authored without asking which unrelated animator path it secretly reuses.
2. A `Skyfall`-style phased attack does not depend on charge semantics.
3. Auto-attack gameplay can occur during skills without forcing auto-attack visuals to override those skills.
4. Interrupt rules are readable from one explicit policy.
5. Local prediction and authoritative replay no longer require a growing pile of animation exceptions.
6. When animation behavior is wrong, the reason is visible in one request/decision path.

## Recommendation

If this work is undertaken, do not aim for the smallest patch set.

The current presentation layer has already crossed the line where additional local fixes are more likely to produce new soup than durable clarity.

The production-ready path is:

- explicit request model
- explicit arbitration
- explicit category separation
- explicit phased melee topology

That is the direction most likely to make the next six months of combat animation work calm instead of chaotic.
