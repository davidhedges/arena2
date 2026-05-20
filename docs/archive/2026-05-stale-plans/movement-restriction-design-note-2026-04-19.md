# Movement Restriction Timeline Design Note

Status: historical.

This note originally documented the block movement-slow prediction fix from `2026-04-19`.
That system was removed on `2026-05-02` when block was aligned with parry as a held defensive action.

## Current State

- Block and parry no longer change movement speed.
- The old block-specific local movement restriction bridge was removed.
- `BLOCK_MOVE_SPEED_MULTIPLIER` was removed from client code, server movement modifiers, and the shared progression catalog.
- Movement modifiers now come from status effects and special movement state, not defense button state.

## Historical Context

The removed design solved a real desync:

- movement prediction ran on future input ticks
- block activation and release originally took effect through reducer replication
- the client could visually react to block immediately while authoritative movement slowdown arrived later

The fix at the time was to make block slowdown part of the same tick contract as movement. That was useful while block had movement gameplay, but it is no longer part of the runtime contract.

## Remaining Guidance

If a future gameplay action changes movement speed, movement blocking, jump allowance, or another locomotion rule, do not attach that rule directly to input presentation or reducer receipt time.

Instead:

1. Resolve the action into authoritative movement state.
2. Make that state effective on the same tick timeline used by movement simulation.
3. Feed the same state into local replay and prediction.
4. Let animation consume resolved movement state, not invent locomotion authority.

## Unrelated Generator Note

The checked-in SpacetimeDB bindings should come from `spacetime generate`, not hand edits.

As of `2026-04-19`, one generator issue remained active on the then-current toolchain:

- `spacetime generate` emitted a nullable `BTreeIndexBase<ulong?>` for `PlayerWorld.instance_id`
- the C# SDK index base rejected nullable generic index keys
- the checked-in `Tables/PlayerWorld.g.cs` kept the generated query columns but omitted that broken handle-level index so the client still built
