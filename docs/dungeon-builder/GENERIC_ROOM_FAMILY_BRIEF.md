# Generic Room family reference brief

Status: authoring reference; not independent production scope authority

Reference prefab:

```text
Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/SetPieces/Generic_Room.prefab
```

## Authored reference

- Nine floor modules form a 3-by-3 level footprint.
- Four cardinal boundary locations are potential exit sockets.
- The current prefab demonstrates two active perpendicular gateways:
  - south gateway using `COMP_Door_01_med_01`;
  - west gateway using `COMP_Door_01_med_02`.
- Boundary assemblies deliberately vary in height. The reference includes base
  medium wall assemblies plus additional small wall segments at approximately
  local `y=4` and `y=6`.
- The tall east wall includes embedded detail pieces. Treat those pieces as
  ignored dressing for the first structural implementation; do not reinterpret
  them as required traversal geometry.

## Intended room family

- Preserve the 3-by-3 footprint.
- Support variants with one, two, three, or four active cardinal exits.
- Preserve authored wall-height variation per boundary segment.
- Rotations and mirrors may produce additional legal placements.
- Gateways are openable doors in the eventual design.
- Door visuals, open/closed state, collision, navigation, and replication are
  deferred. Until that work is explicitly approved, an active gateway is an
  unobstructed opening.
- Embedded wall dressing is also deferred and may be absent from initial
  generated output.

## Current schema-v1 attempt

`connector_generic_room_01` is the first bounded attempt:

- `Connector`, eligible for `connector` / `return`;
- one 3-by-3 Walkable zone;
- south `entry` and perpendicular `exit` matching the two demonstrated
  gateways after normalizing the prefab's west-facing side to recipe-local
  positive X, as required by the current route-forward orientation contract;
- protected L-shaped circulation between the openings;
- no runtime room-prefab binding: the prefab remains a reference design from
  which shape and treatment rules may be extracted;
- no gateway behavior, transition, motif, wall-height contract, or dressing
  semantics in the schema-v1 prototype;
- disabled and absent from the production catalog while under review.

This first asset represents only the two-exit corner member. Schema v1 and the
three existing production recipe slots cannot yet consume one-, three-, or
four-exit members, nor can they preserve per-boundary wall heights.
