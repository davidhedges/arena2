# Combat Action Taxonomy

Date: 2026-04-24

Status: historical taxonomy note. Use `docs/combat-authoring-contract.md` as the current combat action authoring contract.

## Purpose

This document defines the intended long-term categories for combat actions.

The goal is to stop mixing:
- normal melee attacks
- phased melee presentation
- charge-like special abilities
- HUD/loadout slot concepts
- runtime plumbing ids

into one blurry model.

## Core Rule

`action_id` is the canonical authored identity.

It should identify the action itself:
- `SKYFALL_1`
- `SWORD_AND_SHIELD_LIGHT_COMBO_1`
- `SHIELD_CHARGE`

It should not be overloaded to mean:
- HUD slot
- keybind
- combo index
- runtime execution lane

## Combat Action Kinds

Every combat action should belong to one primary kind.

### 1. Melee Attack

Use this for normal weapon-authored attacks.

Examples:
- light combo attacks
- heavy attacks
- gap closers implemented as melee
- finishers
- utility melee attacks like `SKYFALL_1`

Owned by:
- combat animation set authoring
- melee manifest export

Owns:
- authored action id
- combat profile
- hit timing
- hit windows
- combo links
- delivery mode
- aerial rules
- presentation mode

Does not own:
- player loadout slot
- keybind
- subclass exposure

### 2. Special Ability

Use this for actions that are not just melee attacks with different numbers.

Examples:
- `SHIELD_CHARGE`
- channel abilities
- beam abilities
- movement abilities with custom lifecycle

Owned by:
- ability/spell definition systems
- dedicated server execution logic
- dedicated presentation logic when needed

Owns:
- authored action id
- behavior kind
- special lifecycle rules
- movement / channel / release semantics
- presentation behavior specific to that action type

Does not need to pretend to be:
- a normal melee strike
- a melee runtime lane
- a fake combo slot

### 3. Auto-Attack

Treat intrinsic auto-attack as its own contract.

Examples:
- `AUTO_ATTACK_1`

Owned by:
- combat profile / combat animation set
- auto-attack server logic

Owns:
- cadence
- intrinsic attack selection
- intrinsic damage authority

Notes:
- it may execute through melee definition data
- but it is not a selectable subclass ability by default

## Presentation Modes

Presentation mode is separate from action kind.

Examples:
- `SingleClip`
- `Phased`
- `ChargeLifecycle`

This means:
- a melee attack can be `SingleClip`
- a melee attack can be `Phased`
- a special ability like charge can use `ChargeLifecycle`

Do not model presentation mode as the action kind itself.

`Skyfall` is still a melee attack even though it is phased.

## Loadout And Keybind Model

Loadout slots and keybinds are not combat action kinds.

They are UI/input assignment concepts.

The intended model is:
1. player has ability-bar slots
2. player assigns abilities to those slots
3. player binds keys to those slots
4. slot activation resolves to `ability_id`
5. `ability_id` resolves to `action_id`
6. runtime executes the authored action

This means:
- there is no need for a privileged global 16-lane melee taxonomy
- combat animation sets do not define keyboard layout
- runtime lane ids are optional plumbing, not the user-facing model

## Runtime Id Guidance

If a runtime execution id is needed, it should be treated as internal plumbing only.

Good use:
- distinct execution routing where authored id should not double as a runtime token

Bad use:
- treating runtime ids as if they were the real content identity
- building HUD or loadout semantics on top of runtime ids
- forcing every action into a fixed global slot vocabulary

## What Should Be Data-Driven

These should be data-driven:
- which actions a combat animation set contains
- how those actions behave
- which subclass exposes which abilities
- which ability-bar slots a player fills
- which keys the player binds

## What Can Still Be Hard-Coded

These can still be explicit system categories:
- combat action kind
- presentation mode
- event source
- authority type

These are stable engine concepts, not content taxonomy.

## Immediate Implications

1. `StyleActionSlotIds` is a legacy fixed-kit concept and should not remain the long-term source of truth.
2. Combat animation sets should own authored melee attacks directly, without pretending they belong to a global ordered melee kit.
3. Special abilities like `SHIELD_CHARGE` should remain first-class special behaviors, not be squeezed into normal melee slot semantics.
4. HUD/loadout work should move toward player-assigned ability-bar slots instead of weapon-defined melee lanes.

## Migration Direction

Short term:
- stop introducing new dependencies on `StyleActionSlotIds`
- prefer authored action ids and weapon-set data
- keep special behaviors special
- keep presentation-only spell/charge slot ids local to presentation code until real editor-authored spell presentation entries exist

Medium term:
- remove remaining systems that treat melee runtime slot ids as globally privileged
- continue moving spell/charge presentation toward editor-authored spell animation entries on each combat animation set
- move HUD/input assumptions to loadout slot assignment

Long term:
- authored action ids remain canonical
- combat action kind and presentation mode are explicit
- player-facing slotting is ability-bar based
- runtime plumbing ids are internal details only
