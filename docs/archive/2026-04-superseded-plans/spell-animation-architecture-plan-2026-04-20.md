# Spell Animation Architecture Plan

Date: April 20, 2026

Status: Proposed for external review

Supersedes:
- `docs/spell-animation-presentation-refactor-plan-2026-04-20.md`
- `docs/spell-animation-integration-blocker-2026-04-20.md`

## Executive Summary

The previous spell-animation work churned because it coupled three separate concerns:

- spell identity
- weapon-set-specific authored clip data
- spell presentation playback/routing

The correct boundary is:

- `CombatAnimationSet` may own spell clip data, because spell poses can vary by animation set
- spell presentation routing and playback do not belong in `CombatAnimationSet`
- the current generic cast path (`castDefault` / `castUp` on the masked `UpperBody` lane) is not the long-term playback model

This plan proposes a tighter v1:

1. replace `castDefault` / `castUp` with a proper `spellId -> {ground, air}` map on `CombatAnimationSet`
2. add a dedicated `SpellAction` playback lane modeled on the proven melee lane
3. route spell playback directly in `PlayerAnimator` from authoritative spell events
4. remove the temporary hacks and wrong abstractions created during the buff-animation debugging

This is the recommended implementation direction.

## What We Learned

Recent debugging established these facts:

1. `Buff.anim` is a valid clip and renders when played on the melee lane.
2. The existing cast path can bind `Buff` and enter `CastDefault`.
3. Despite entering the state, the current masked `UpperBody` cast lane does not produce the intended result for these authored clips.
4. Weapon-set-specific spell clips are a real requirement. The same spell may need different poses on sword-and-shield versus greatsword.

The practical conclusions are:

- the problem is not that the clip is invalid
- the problem is not that spell animation data must be globally weapon-agnostic
- the problem is that the current spell playback destination is the wrong lane and the current data model is too lossy

## Core Principles

1. Spell presentation is a joint `(animation set, spell id)` problem for clip selection.
2. Spell presentation routing/state ownership is not a weapon-set concern.
3. Generic `castDefault` / `castUp` slots are insufficient because they erase spell identity.
4. The melee playback lane is the proven pattern and should be mirrored rather than replaced by speculative new architecture.
5. V1 should solve the smallest clean problem: one-shot spell playback with ground/air variants.

## Ownership Model

### CombatAnimationSet Owns Spell Clip Data

`CombatAnimationSet` should continue to own weapon-specific authored animation data.

That should include a per-spell clip map:

```text
spellId -> SpellClips

SpellClips
  ground: AnimationClip?
  air: AnimationClip?
  groundEffectTime: float
  airEffectTime: float
  requiredPresentationStance: Combat | Any
```

This is the natural asset boundary because:

- the animation packs are authored per animation set
- different animation sets can require different spell poses
- adding a new animation set should mostly be localized to that animation set asset

Current implementation note:

- the first working implementation currently assumes these spell clips are authored for in-hand combat presentation
- if a spell is triggered while visually sheathed/stowed, runtime forces combat presentation before playback
- this is acceptable as a short-term rule for the current packs, but it should be replaced by authored spell metadata such as `requiredPresentationStance`

### CombatAnimationSet Does Not Own Spell Routing

`CombatAnimationSet` should not decide:

- when a spell animation plays
- which animator state/trigger is used
- how spell interruptions are handled
- how authoritative spell events are interpreted

That logic belongs in runtime code, primarily `PlayerAnimator`, with event forwarding from the existing entity/network binding layer.

## Data Model Changes

### Remove

From `CombatAnimationSet`:

- `castDefault`
- `castUp`
- `castDefaultEffectTime`
- `castUpEffectTime`

From runtime logic:

- `CastAnimationMap`

These fields reduce a real `(spellId, animation set)` problem to a fake `(cast type, animation set)` problem.

### Add

Add a per-spell collection on `CombatAnimationSet`.

Recommended shape:

```text
WeaponSpellAnimationEntry
  spellId: string
  groundClip: AnimationClip?
  airClip: AnimationClip?
  groundEffectTime: float
  airEffectTime: float
  requiredPresentationStance: Combat | Any
```

This can be implemented as a serialized list in Unity and indexed into a runtime dictionary at load/init time.

The serialized list is preferred over trying to serialize a raw dictionary directly in Unity assets.

## Playback Architecture

### Replace The Generic Cast Lane

Do not continue investing in:

- `CastDefault`
- `CastUp`
- masked `UpperBody` cast playback

That path is the wrong abstraction for these authored clips.

### Add A Dedicated SpellAction Lane

Add a new animator layer:

- `SpellAction`

Design:

- `Override`
- weight `1`
- no avatar mask
- structurally mirrored from `MeleeAttack`

Recommended first-pass state setup:

- `SpellAction1`
- `SpellAction2`
- `SpellAction3`
- `SpellAction4`

Recommended triggers:

- `TriggerSpellAction1`
- `TriggerSpellAction2`
- `TriggerSpellAction3`
- `TriggerSpellAction4`

Recommended slots:

- `slot_spell_1`
- `slot_spell_2`
- `slot_spell_3`
- `slot_spell_4`

This should work like the melee bank:

- resolve desired clip
- hot-swap into the next bank slot
- fire the matching trigger

This gives:

- explicit spell playback ownership
- no dependence on `UpperBody` mask behavior
- a scalable path for future spell animations

## Runtime Design

### PlayerAnimator Owns Spell Playback

Add a dedicated entry point:

- `TriggerSpell(string spellId, bool grounded)`

Responsibilities:

1. resolve the current animation set
2. look up the spell entry by `spellId`
3. choose `airClip` when airborne and available, otherwise `groundClip`
4. if the spell entry requires combat presentation, ensure the character is visually in combat stance first
5. bind the clip into the next spell bank slot
6. fire the matching `TriggerSpellActionN`

This should be structurally parallel to `TriggerStrike(...)`, not to `TriggerCast(...)`.

Important distinction:

- the current auto-draw-to-combat behavior is an implementation shortcut, not the ideal long-term contract
- the long-term contract should be data-driven per spell entry, not hardcoded in `PlayerAnimator`

### Event Forwarding

The existing entity/network layer can remain thin.

Recommended rule:

- `EntityRegistry` continues to forward authoritative spell events to the local/remote `PlayerEntity`
- `PlayerEntity` forwards to `PlayerAnimator.TriggerSpell(...)`

That is enough for v1.

A separate `SpellPresentationRouter` class is not needed unless the event model becomes materially more complex later.

## Trigger Source

For v1, keep this narrow:

- use the existing authoritative spell event path where available
- do not keep spell-specific local trigger hacks in `SpellInputHandler`

If instant one-shot spells still need a cleaner authoritative source later, that can be solved separately. It does not need to block the playback model correction.

## What This Plan Explicitly Avoids

This plan deliberately does not introduce:

- a new `SpellPresentationRouter`
- spell family enums
- spell playback mode enums
- spell interrupt policy enums
- a new masked upper-body spell lane
- a large multi-phase migration program

Those would be premature at this stage.

The first implementation should be mechanical and concrete.

## Cleanup Plan

The recent buff-animation debugging created temporary code and wrong abstractions that should not remain.

### Remove Temporary Runtime Hacks

From `SpellInputHandler`:

- any spell-id-specific local presentation calls added for debugging or experimentation

From `PlayerAnimator`:

- temporary cast debugging logs and trace helpers added during investigation
- temporary cast-lifetime glue that only existed to prop up the old generic cast path

### Remove Wrong Spell Abstractions

Delete or deprecate:

- `CastAnimationMap`
- `castDefault` / `castUp` weapon-set usage
- old `CastDefault` / `CastUp` spell playback dependence as the primary spell path

### Remove Mask Experiments That Are No Longer Needed

If the new `SpellAction` lane uses no avatar mask, then the spell-specific mask experiments should be removed rather than preserved as dead infrastructure.

## Implementation Plan

### Step 1: Add Per-Spell Clip Data To CombatAnimationSet

- add `WeaponSpellAnimationEntry[] spells`
- add runtime lookup by `spellId`
- migrate `FROST_NOVA` and one additional spell if useful for coverage

Exit criteria:

- animation sets store spell clip data by spell id, not generic cast type

### Step 2: Add SpellAction Animator Lane

- add `SpellAction` layer
- add four banked states and four slots
- mirror the `MeleeAttack` banking pattern

Exit criteria:

- animator has a dedicated spell playback lane independent of the old cast path

### Step 3: Add PlayerAnimator.TriggerSpell

- resolve spell clip from current animation set
- choose ground/air variant
- bind into spell bank
- trigger playback

Exit criteria:

- spell presentation no longer depends on `castDefault` / `castUp`

### Step 4: Wire Authoritative Events To TriggerSpell

- forward authoritative spell execution/presentation events to `TriggerSpell`
- remove local spell-specific hacks from input code

Exit criteria:

- spell playback uses the real runtime event path

### Step 5: Delete Old Spell Cast Path

- remove `CastAnimationMap`
- remove weapon-set cast slots
- remove obsolete `CastDefault` / `CastUp` dependency for spells
- remove temporary debug code and trace glue

Exit criteria:

- only the new spell playback model remains

## Validation Plan

### Manual

Test with at least:

- `FROST_NOVA` grounded on sword-and-shield
- `FROST_NOVA` airborne on sword-and-shield
- `FROST_NOVA` grounded on greatsword
- `FROST_NOVA` airborne on greatsword

Success criteria:

- the correct spell animation is visible
- ground/air variants behave correctly
- weapon transforms remain stable
- switching animation sets changes spell pose only when the authored spell clip differs

### Regression

Verify that:

- melee strike playback is unchanged
- draw/sheath remains unchanged
- dodge/block remains unchanged
- removing the old cast path does not break non-migrated spell gameplay

## Guardrails

1. No new generic cast slots should be added to `CombatAnimationSet`.
2. No new spell-specific presentation hacks should be added to `SpellInputHandler`.
3. No spell animation should be tested by rebinding melee clips except as a temporary debugging tool.
4. No masked upper-body spell lane should be introduced unless there is clear evidence the unmasked `SpellAction` model is insufficient.
5. New spell support should be data entry, not new per-spell code paths.

## Review Questions

1. Should `WeaponSpellAnimationEntry` include effect timing now, or can timing stay implicit until later?
2. Do we want four spell bank slots for parity with melee, or fewer in v1?
3. Is the existing authoritative spell event surface sufficient for the first migrated one-shot spells, or does one instant-spell gap still need a follow-up patch?

## Recommendation

Approve this direction:

- spell clip data remains on animation sets, keyed by `spellId`
- spell playback gets a dedicated `SpellAction` lane modeled on melee
- old generic cast slots and the generic cast mapping layer are removed

Reject these directions:

- a spell-agnostic global clip library as the first pass
- new routing classes or enum taxonomies before the data/playback model is corrected
- further investment in `castDefault` / `castUp` as the long-term spell solution
