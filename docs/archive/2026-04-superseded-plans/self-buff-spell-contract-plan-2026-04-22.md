# Self-Buff Spell Contract Plan

## Summary

Self-buffs like `Momentum` should remain normal spells that apply statuses to the caster.

That contract is now implemented for the first shipped self-buff. The two main follow-ups this note originally proposed are both landed:

1. authoring validation now ties progression spell abilities to spell definitions and weapon-set presentation
2. generic `SELF_BUFF` dispatch now replaces spell-specific self-buff execution helpers

This doc is now a concise record of the contract and the guardrails that were added.

## Already Landed

The current runtime already supports the core `Momentum` contract:

- spell manifest:
  - `behavior = SELF_BUFF`
  - `targeting = SELF`
  - `cast_time_ms = 0`
  - `requires_target = false`
  - mobile cast policy
- input:
  - `SELF` spells skip aim mode
  - `SELF` spells do not require a target
  - `Momentum` sends a normal `CastRequest(...)`
- server:
  - `Momentum` executes through the spell pipeline
  - applies a status to the caster
  - uses normal cooldown/resource/GCD handling
- presentation:
  - combat animation set owns spell animation binding
  - local instant self-buffs can predict animation immediately
  - authoritative `SPELL_CAST` de-dupes local replay

So the architectural question is no longer “should self-buffs be spells?”  
That answer is already yes.

## Landed Guardrails

### 1. Authoring validation lock-down

This is now in place.

Right now a player-facing spell ability can still drift from presentation authoring unless somebody notices at runtime. We already saw that failure mode with `Momentum`.

We need validation that ties together:

- progression ability action id
- spell definition existence
- subclass-derived combat profile
- owning combat animation set
- spell animation entry in that combat animation set

Concrete checks:

- a spell-like player-facing ability must resolve to a real spell definition
- the subclass-derived combat profile must resolve to a real combat animation set
- that combat animation set must contain a spell animation entry for the spell id
- `SELF` spells should have `requires_target = false`
- instant mobile self-buffs should not be authored with stationary-cast requirements

This is detection, not prevention, but it closes a real source of drift.

### 2. Replace per-spell self-buff execution helpers

This is now in place.

`SELF_BUFF` spells dispatch through a shared server branch keyed by spell behavior and authored self-buff payload data, instead of requiring a spell-named helper for each new buff.

Good end state:

- `SELF_BUFF` spells dispatch through one generic server path
- spell authoring or a small registered behavior table provides:
  - status kind
  - status payload
  - stack group
  - stack policy
  - max stacks
  - duration

This does **not** mean inventing a new buff system. It means removing spell-specific branching from the spell system where a generic self-buff branch should exist.

## Target State

The desired contract is:

- spells own input/cast/cooldown/GCD/resource behavior
- statuses own timed buff state
- combat animation sets own spell cast animation presentation
- progression owns player-facing exposure

For self-buffs specifically:

- `behavior` determines that the spell applies a self buff
- `targeting = SELF` determines that no target resolution or aim mode is involved
- the caster is always the recipient

## Small Remaining Cleanup

The main remaining cleanup is smaller and less urgent:

- continue reducing stringly spell semantics on the client side so targeting/behavior checks go through small shared helpers instead of ad hoc string comparisons

That is cleanup, not a missing architecture piece.

## Tests

### Existing behavior to pin

These confirm already-shipped behavior does not regress:

- `SELF` spell can cast with empty `target_id`
- `SELF` spell applies to caster
- instant mobile self-buff can cast while moving
- instant `SELF` spell does not require selected target
- instant `SELF` spell predicts local animation once and does not double replay

### Validation now covered

- progression spell ability must resolve to a spell definition
- progression spell ability must resolve to a combat animation set spell animation entry through subclass-derived combat profile

### Refactor coverage now covered

- `Momentum` still applies `MoveSlowImmunity`
- stack refresh semantics remain unchanged
- no spell-specific execution helper is needed for the behavior to work

## What This Plan Is Not

This is not a proposal to:

- build target buffs or point buffs right now
- invent a second buff system
- move buff meaning out of statuses
- replace the spell system with something separate

It is a cleanup and lock-down plan for the self-buff spell model that is already the right direction.
