# Dungeon generation glossary

Status: authoritative vocabulary for the current dungeon-builder pipeline  
Last updated: 2026-07-22

Use these terms consistently in code, recipe assets, reports, documentation, and
review. Where the broader design vocabulary is larger than the current schema,
the entry says so explicitly.

## How the terms fit together

```text
Macro-topology pattern
    -> Route intent
        -> Route nodes (each has a role and a beat)
            -> Required recipe slots or generic rooms
                -> Recipes
                    -> zones + ports + transitions + motifs + variations
        -> DungeonLayout
            -> TieredLevelPlan
                -> canonical renderer and collision output
```

The important separation is: **role describes spatial function**, **beat describes
journey timing**, and **recipe describes authored construction**.

## Planning and pacing

| Term | Meaning in this project |
| --- | --- |
| **Macro-topology pattern** | A semantic graph template for the whole dungeon: main route, branches, loops, landmarks, and vista opportunities. It has no final world coordinates. Current patterns are the processional spine, atrium ring, and twin-wing keep. |
| **Route intent** | The transient semantic graph for one generation attempt. It contains route nodes, traversal edges, the planned vista, elevation requirements, and recipe slots. It is consumed while producing `DungeonLayout`; it is not a second renderer-facing plan. |
| **Route node** | One intended place on the route graph. A node has a stable ID, a **role**, a **beat**, route/branch order, relative elevation, and possibly a required recipe slot. It becomes a room footprint during embedding. |
| **Role** | What a route node must do spatially. Examples currently include `arrival`, `connector`, `junction`, `grand-room`, `landmark`, `processional-hall`, `return-hall`, `culmination`, `overlook`, and `optional-room`. Role can influence generic room geometry and recipe compatibility. |
| **Beat** | Where the node sits in the player's journey or pacing sequence. Current examples include `arrival`, `compression`, `choice`, `reveal`, `landmark`, `ascent`, `approach`, `rejoin`, `culmination`, `branch`, `reward`, and `return`. A beat is a semantic label—not a combat state, victory condition, or claim that the room can be “beaten.” |
| **Eligible role / eligible beat** | Exact compatibility filters on a recipe. A recipe can fill a slot only when both the node's role and beat occur in its eligible lists. Eligibility means “may be selected here”; it does not guarantee selection and does not confer gameplay behavior. |
| **Reward beat** | A pacing slot intended to feel rewarding, currently used on optional branches. The label does not itself spawn loot or consume a separately implemented loot-room quota. Actual rewards remain a gameplay/content responsibility. |
| **Recipe slot** | A route node binding that requires a particular reviewed recipe. Current production has three required recipe bindings. Nodes without a recipe slot may use generic room construction; missing required recipes are not silently replaced by generic rooms. |
| **Traversal edge** | A graph connection the player can physically use. Current transition kinds are level corridor, stair, bridge, and stairwell. |
| **Vista edge** | A planned line of sight from a source node to a target node. It does not imply adjacency or a traversable connection. |
| **Main route** | The ordered entrance-to-culmination path through the route graph. |
| **Branch / rejoin** | A route that leaves the main path and later reconnects. A branch may contain optional, overlook, or reward beats. |
| **Generic room** | A room footprint generated from the route node's role and normal room-shape policy rather than from an authored recipe. “Generic” describes its construction source, not its eventual visual quality. |
| **Floorplan** | The resolved spatial arrangement of rooms, corridors, branches, and connections. Use **macro-topology** for the coordinate-free graph and `DungeonLayout` for the canonical cell-space result. |

## Authored content

| Term | Meaning in this project |
| --- | --- |
| **Room** | A realized spatial footprint in the dungeon: a connected set of floor cells with boundaries and connections. A room may be generic or may realize a recipe. “Room” is not currently a `DungeonRecipeKind` enum value. |
| **Room recipe** | General prose for a recipe that produces one room or one tightly coupled room composition. In the implemented schema, the actual recipe kinds are only `Connector` and `Episode`. |
| **Recipe** | A reviewed, versioned authored spatial contract. It declares eligible roles/beats, zones, ports, transitions, motifs, legal orientations, symmetry, and controlled variations. It is not a prefab and it does not infer semantics from a scene hierarchy. |
| **Connector recipe** | The current recipe kind for a compact composition whose main purpose is traversal, such as a vestibule or corner return. It is stored under `Recipes/Rooms/`. This is distinct from the route-node role named `connector`, although they commonly match. |
| **Episode** | The current recipe kind for a composition whose identity depends on several coupled architectural elements that must be selected and placed atomically. Schema v1 realizes an episode inside one room footprint; “episode” does not currently mean a multi-room floorplan. The throne hall is an episode because its focal showpiece, protected axis, paired stairs, elevated regions, landings, and thresholds belong to one composition. Episode assets live under `Recipes/Episodes/`. |
| **Motif** | A subordinate implementation declaration inside a recipe, not an independently selectable room. Current motif kinds are `StairTransition` and `FocalVisual`. A dais, paired stair arrangement, bridge landing, or gallery edge can be a motif when it has an explicit consumer and contract. There is no standalone motif-asset catalog today. |
| **StairTransition motif** | A motif identifying the implementation family used by an explicit recipe transition. The transition itself owns exact cells, rise, lane, landings, footprint, and headroom. |
| **FocalVisual motif** | A measured visual payload used as a recipe's focal composition. Current production uses it for reviewed backed-dais showpieces. It does not independently change the cell-level floorplan. |
| **Variation** | A weighted, reviewed alternative inside one recipe contract. Current throne-hall variations choose between compatible focal visuals; they do not independently add or remove the paired stairs or elevated regions. |
| **Zone** | A rectangular recipe declaration with an exact offset, size, relative level, and kind. Current kinds are `Walkable`, `Elevated`, `ProtectedCirculation`, and `ProtectedFocal`. |
| **Walkable zone** | Base traversable floor belonging to the recipe footprint. |
| **Elevated zone** | Traversable floor at an exact relative elevation. It must connect through declared transitions rather than an implicit repair. |
| **ProtectedCirculation** | Cells reserved so generic fill, dressing, or later features cannot block required movement. Protection is a reservation, not a visible object. |
| **ProtectedFocal** | Cells reserved to preserve a focal composition, approach, and sightline. The current backed showpiece placement derives from this declared region and recipe orientation. |
| **Port** | An exact typed opening on the recipe boundary. A port declares its cell, outward direction, relative level, width, approach depth, and headroom. Current recipe ports are corridor ports. |
| **Transition** | An explicit elevation connection inside a recipe. It declares lower/upper transition cells, landing cells, occupied footprint, climb direction, rise, lane count, and headroom. |
| **Reservation** | Cells or volume made unavailable to unrelated placement because a port, stair, landing, protected zone, showpiece, vista, or other contract needs them. Reservations prevent overlap; they are not a parallel geometry system. |
| **Atomic group** | A transition grouping whose members belong to one composition. It prevents coupled features such as paired stairs from being treated as unrelated random options. |
| **Symmetry pair** | Two declared zones that must mirror across the recipe's primary axis. It is a structural contract, not a request for the renderer to guess symmetry. |
| **Recipe lifecycle** | `Draft` means work in progress, `Reviewed` means current validation and human review passed, and `Deprecated` means unavailable for new selection. Editing reviewed content makes its digest stale until it is reviewed again. |
| **Recipe catalog** | The explicit list of recipe assets eligible for catalog validation and production admission. Only current, valid, reviewed recipes are admitted. |

## Geometry and realization

| Term | Meaning in this project |
| --- | --- |
| **Cell** | One 4-by-4-world-unit square in the dungeon's horizontal planning grid. |
| **Elevation level** | One world unit of vertical height. Route-scale elevation uses larger 4-level steps; recipe and other intra-room accents may use 1-level changes where explicitly supported. |
| **Room footprint** | The exact set of cells belonging to one realized room. It can be non-rectangular even though individual recipe zones are rectangular. |
| **`DungeonLayout`** | The canonical 2D spatial result: room footprints, floor cells, connections, room zones, and only the intent metadata required by downstream consumers. |
| **`TieredLevelPlan`** | The canonical resolved elevation and transition result consumed by boundary construction, rendering, abyss support, and collision export. |
| **Showpiece** | A reviewed synthesized piece plan instantiated as one visual composition. The current backed dais focal visuals are showpieces. |
| **Dais** | An architectural raised platform, often supporting a focal object such as a throne or shrine. Current production placement is recipe-owned through measured backed `FocalVisual` showpieces and explicit recipe elevation transitions. The former random-room dais roll and wall search are retired. Sunken, rise-2, tiered, or freely sized recipe daises are not schema-v1 authoring options. |
| **Approach** | The spatial sequence leading toward a room or focal feature. It may be expressed by route order, a threshold/connector, protected circulation, elevation, or room geometry; it is not currently a separate recipe kind. |
| **Dressing / props** | Decorative content that gives a space character without defining its required topology. Junk, shelves, tables, bones, books, and similar objects belong here unless their placement becomes structurally contractual. Schema v1 does not yet provide a prop-set field. |
| **Room intent / room type** | Design-language shorthand such as junk room, library, armory, jail, or throne room. It is not currently one universal enum. Depending on what defines it, the implementation may eventually be dressing on a generic room, a recipe, an episode, or a combination. |
| **Plan report** | A serializable diagnostic projection of one attempt. It is evidence and replay metadata, not another mutable planning model. |

## Common distinctions

| Do not conflate | Distinction |
| --- | --- |
| **Role vs. beat** | Role is the node's spatial job; beat is its place in the journey. A node can have role `optional-room` and beat `reward`. |
| **Room vs. recipe** | A room is realized space. A recipe is an authored contract that may produce that space. Many rooms remain generic. |
| **Recipe vs. motif** | A recipe competes for an eligible route slot. A motif is subordinate content selected within a recipe. |
| **Episode vs. floorplan** | An episode is one atomic authored composition. A floorplan is the complete resolved dungeon arrangement. |
| **Eligibility vs. selection** | Eligibility permits a recipe-slot match; the slot/catalog/variation process still determines the selected content. |
| **Reward beat vs. loot implementation** | The beat communicates pacing intent. It does not itself create a chest, treasure table, or gameplay reward. |
| **Protected vs. occupied** | A protected cell must stay clear for a contract. It need not contain visible geometry. |
| **Visual showpiece vs. walkable elevation** | A showpiece supplies measured visual pieces. Zones and transitions own the recipe's walkable level field. |

## Current recipe directories

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/
  Catalog/   explicit recipe catalog asset
  Rooms/     Connector recipe assets
  Episodes/  Episode recipe assets
```

The folder names are organizational. The recipe asset's `kind` field is the
authoritative classification.
