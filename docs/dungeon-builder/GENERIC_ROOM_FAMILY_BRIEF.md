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

- Use a 3-by-2 authored footprint. The shared legal-quarter-turn placement
  contract supplies the 2-by-3 orientation; this is not a generic-room-only
  dimension pathway.
- Support variants with one, two, three, or four active cardinal exits.
- Preserve authored wall-height variation per boundary segment.
- Quarter-turn rotations produce the legal orientations. Mirroring is disabled
  for this even-width footprint because it adds no orientation that rotation
  does not already supply.
- Gateways are openable doors in the eventual design.
- Door visuals, open/closed state, collision, navigation, and replication are
  deferred. Until that work is explicitly approved, an active gateway is an
  unobstructed opening.
- Embedded wall dressing is also deferred and may be absent from initial
  generated output.

## Current schema-v1 route-bound socket attempt

`connector_generic_room_01` is the first bounded attempt:

- `Connector`, eligible for `connector` / `return`;
- one 3-by-2 Walkable zone, with 2-by-3 supplied by the shared quarter-turn
  transform;
- four potential level-0 corridor sockets at the north, east, south, and west
  boundaries;
- `IncidentCardinalSockets` binding with an allowed active range of one through
  four;
- placement activates exactly the sockets whose transformed outward directions
  have incident route edges; inactive sockets do not create openings;
- protected T-shaped circulation joins all four potential openings;
- like every recipe, placement uses the declared grid cells rather than the
  eventual modular floor silhouette. Shared ledge-corner rendering may replace
  eligible square floor corners with angled or curved modules after placement;
  the same corner decision replaces both ledge-wall faces with the matching
  angled/curved wall kit;
- no runtime room-prefab binding: the prefab remains a reference design from
  which shape and treatment rules may be extracted;
- no gateway behavior, transition, motif, wall-height contract, or dressing
  semantics in the schema-v1 prototype;
- enabled and explicitly present in the production catalog.

The socket policy belongs only to recipes that explicitly select
`IncidentCardinalSockets`; all existing reviewed recipes retain exact named
mandatory `entry` / `exit` binding. The current `required-return` placement has
two perpendicular incident route edges, so its present full-dungeon preview
activates the matching two-socket corner. A future explicitly bound slot with
degree one, three, or four can consume the same asset without changing its
footprint or authoring four separate recipes.

Schema v1 still cannot preserve per-boundary wall heights. Gateway visuals,
open/closed behavior, collision behavior, navigation, replication, and embedded
dressing remain deferred.
