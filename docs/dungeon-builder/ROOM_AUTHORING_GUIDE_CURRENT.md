# Exact room-authoring guide for the system that exists today

Status: exact for recipe schema v1 at commit `0187d33d`

Unity version: `6000.4.0f1`

Last verified: 2026-07-24

> Procedural-topology note (2026-08-02): production no longer contains three
> fixed required slots. Route families generate zero through three authored
> content opportunities per seed. The schema-v1 field instructions below remain
> useful, but the fixed-slot tables and pinned slot IDs describe deprecated
> evidence graphs. Current compatibility is by generated node role/beat,
> degree, ports or sockets, orientation, and layer mapping; see
> [`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md).

This guide answers one question: **how do I author another room that the current
dungeon generator can select from a pool?**

## First: what this system actually authors

Today, a dungeon recipe is **not a prefab**. There is no prefab field on
`DungeonRecipeAsset`, and the generator does not instantiate a room prefab from
a recipe.

A recipe is a `ScriptableObject` containing grid data:

- rectangular floor zones;
- optional raised floor zones;
- exact named corridor openings, or an explicitly opted-in cardinal socket set;
- optional internal rise-1 stairs;
- protected cells;
- legal rotations and mirroring;
- exact compatibility labels.

When the recipe is selected, the existing dungeon renderer builds that room
from the existing modular floor, wall, stair, abyss-support, and collision
systems.

The current system can therefore make a **grid-defined architectural room**.
It cannot currently make any of the following:

- a room by pointing at an arbitrary prefab;
- a decorated room with a recipe-owned prop set;
- a new general-purpose pool used by every ordinary room;
- a new route slot;
- a multi-room prefab or arbitrary topology;
- a room with arbitrary-width doors;
- a room with arbitrary elevation changes.

Most dungeon rooms are still generic procedural rooms. Recipes compete only for
three fixed places in every generated dungeon:

| Fixed slot | Where it is used | Required eligibility | Existing pool |
| --- | --- | --- | --- |
| `required-compression` | An early threshold/connector room | role `connector`, beat `compression` | `connector_example_01`, `connector_flexible_vestibule_01` |
| `required-landmark` | The main landmark room | role `landmark`, beat `landmark` | `episode_throne_twin_stairs_01` |
| `required-return` | The branch/rejoin corner room | role `connector`, beat `return` | `connector_corner_return_01` |

Those names are internal labels for fixed insertion points. They do not create
geometry. For the least surprising “make another room and put it in a pool”
workflow, author a `required-compression` candidate. It will compete uniformly
with the two existing compression candidates.

`connector_generic_room_01` is an enabled catalog member for the generic room
family. It opts into `IncidentCardinalSockets`, declares north/east/south/west
potential openings, and allows one through four of those sockets to become
active. Placement activates only the sides that match incident route edges.
Every other current recipe remains in `ExactNamedPorts` mode and retains the
literal `entry` / `exit` behavior described below.

## The shortest reliable path: make a flat 5-by-5 room

This is the recommended workflow. It produces a real generated room with two
opposed corridor connections and no internal stair or focal object.

### 1. Open the correct project

Open the repository root in Unity `6000.4.0f1`:

```text
/Users/davidhedges/Projects/arena2
```

Wait for Unity to finish compiling and importing before editing assets.

### 2. Duplicate a working compression recipe

In Unity's Project window, open:

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Rooms/
```

Select:

```text
connector_flexible_vestibule_01.asset
```

Duplicate it with **Edit > Duplicate** or `Command-D` on macOS. Rename the new
asset in the Project window. This guide uses:

```text
connector_my_room_01.asset
```

Use your own descriptive ID, but it must contain only:

```text
lowercase letters a-z
digits 0-9
underscore _
```

No spaces, hyphens, uppercase letters, or punctuation are valid. The recipe ID
must be unique across the catalog.

Do not add the duplicate to the catalog yet. Do not leave the copied recipe
enabled while editing it.

### 3. Enter the exact top-level values

Select the duplicate. In the normal Unity Inspector—not just the Dungeon Recipe
Authoring window—set:

| Inspector field | Exact value |
| --- | --- |
| Recipe Id | `connector_my_room_01` |
| Display Name | Any human-readable name, for example `My Flat Room` |
| Kind | `Connector` |
| Schema Version | `1` |
| Content Version | `1` |
| Disabled For Generation | checked / `true` |
| Eligible Roles > Size | `1` |
| Eligible Roles > Element 0 | `connector` |
| Eligible Beats > Size | `1` |
| Eligible Beats > Element 0 | `compression` |
| Allow Mirror | unchecked / `false` |
| Legal Quarter Turns > Size | `4` |
| Legal Quarter Turns > Element 0 | `0` |
| Legal Quarter Turns > Element 1 | `1` |
| Legal Quarter Turns > Element 2 | `2` |
| Legal Quarter Turns > Element 3 | `3` |

`Recipe Id` is the stable machine identity. The asset filename and Recipe Id
should match so a human can find the asset, although validation only treats
Recipe Id as authoritative. `Display Name` is only a label.

Keep `Disabled For Generation` checked until the room is valid, previewed, and
explicitly added to the catalog.

### 4. Replace Zones with these two entries

Set **Zones > Size** to `2`.

#### Zones > Element 0

| Field | Value |
| --- | --- |
| Id | `room` |
| Kind | `Walkable` |
| Offset X | `-2` |
| Offset Y | `-2` |
| Size X | `5` |
| Size Y | `5` |
| Relative Level | `0` |

This declares cells `x=-2..2`, `y=-2..2`: a 5-by-5 floor. One grid cell is
4-by-4 Unity world units, so the planned room is 20-by-20 world units before
walls and decorative overhang.

#### Zones > Element 1

| Field | Value |
| --- | --- |
| Id | `protected_route` |
| Kind | `Protected Circulation` |
| Offset X | `-2` |
| Offset Y | `-1` |
| Size X | `5` |
| Size Y | `1` |
| Relative Level | `0` |

This reserves a clear one-cell-wide strip between the entry and exit. A
protected zone does not create extra floor; it marks cells inside the existing
floor that must remain clear.

The local grid now looks like this:

```text
                 x
             -2 -1  0  1  2
          2   .  .  .  .  .
          1   .  .  .  .  .
      y   0   .  .  .  .  .
         -1   E  =  =  =  X
         -2   .  .  .  .  .
```

- Every shown cell is part of the `room` Walkable zone.
- `=` is the `protected_route`.
- `E` is the entry port cell.
- `X` is the exit port cell.
- Positive local X is the authored entry-to-exit direction.
- The generator rotates the whole recipe to match the route in the generated
  floorplan.

### 5. Replace Ports with these two entries

Set **Ports > Size** to `2`.

#### Ports > Element 0

| Field | Value |
| --- | --- |
| Id | `entry` |
| Type | `Corridor` |
| Mandatory | checked / `true` |
| Cell X | `-2` |
| Cell Y | `-1` |
| Outward Direction X | `-1` |
| Outward Direction Y | `0` |
| Relative Level | `0` |
| Width Cells | `1` |
| Approach Depth Cells | `1` |
| Headroom Levels | `3` |

#### Ports > Element 1

| Field | Value |
| --- | --- |
| Id | `exit` |
| Type | `Corridor` |
| Mandatory | checked / `true` |
| Cell X | `2` |
| Cell Y | `-1` |
| Outward Direction X | `1` |
| Outward Direction Y | `0` |
| Relative Level | `0` |
| Width Cells | `1` |
| Approach Depth Cells | `1` |
| Headroom Levels | `3` |

The port cell must be inside the room. The adjacent cell in its outward
direction must be outside the room:

```text
entry: (-2,-1) + (-1,0) = (-3,-1), outside the room
exit:   (2,-1) + ( 1,0) = ( 3,-1), outside the room
```

The three current fixed slots bind ports by the literal IDs `entry` and `exit`.
For a usable current production candidate, use exactly two ports, make both
mandatory, and use those exact IDs.

### 6. Remove the copied raised-room data

Set all of these array sizes to `0`:

| Inspector field | Size |
| --- | --- |
| Motifs | `0` |
| Transitions | `0` |
| Symmetry Pairs | `0` |
| Variations | `0` |

This is valid. A flat room does not need a motif or transition.

After reducing an array's Size in Unity, collapse and re-expand it to confirm
that no copied elements remain.

### 7. Save the asset

Use **File > Save Project**. Leave the room disabled and absent from the catalog
for the first validation and preview.

### 8. Validate the room contract

Keep the new asset selected in the Project window. Run:

```text
Arena > Dungeons > Recipes > Validate Current Recipe
```

Read the Unity Console. A passing contract for this room reports:

```text
passed=True
schema=True
structure=True
variation=True
neighbor=True
fullDungeon=False
```

`fullDungeon=False` is expected here. The command only executes the first four
validation layers. It does not mean those layers failed.

If `passed=False`, do not catalog or enable the room. Fix every reported
finding and validate again.

### 9. Force the disabled room through a full-dungeon preview

Keep the asset selected and run:

```text
Arena > Dungeons > Recipes > Build Preview Gallery
```

The preview deliberately accepts a valid recipe that is still disabled and not
yet in the production catalog. It temporarily inserts and forces that recipe
into one compatible existing slot, runs the existing full-dungeon placement
and validation path, then removes the temporary preview state.

For the flat room above, it should use:

```text
topology: processional-spine
slot: required-compression
route node: threshold
fixed preview seed: 2026072100
```

A successful Console result ends with all five layers true:

```text
passed=True
schema=True
structure=True
variation=True
neighbor=True
fullDungeon=True
```

The output is written relative to the repository root:

```text
DungeonLabReports/Recipes/connector_my_room_01/
```

Open:

```text
DungeonLabReports/Recipes/connector_my_room_01/gallery_manifest.json
```

For this exact non-mirrored, non-variant flat room, the manifest should contain:

- 16 PNG entries: 4 legal rotations multiplied by 4 diagnostic view kinds;
- 2 generic-neighbor entries: one for `entry`, one for `exit`;
- `boundMandatoryPorts: 2`;
- `resolvedTransitions: 0`;
- `placedAtomically: true`;
- `canonicalPlan: true`;
- `renderer: true`;
- `abyssSupport: true`;
- `collision: true`;
- `forced: true`;
- `forcedRecipeId: connector_my_room_01`;
- `recipeSlotId: required-compression`.

The PNGs are schematic grid overlays, not beauty screenshots of the rendered
room. The full renderer, abyss, and collision results are recorded as evidence
in the manifest.

The authoring window and gallery use these colors:

| Color | Meaning |
| --- | --- |
| Blue | Walkable |
| Orange | Elevated |
| Green | Protected Circulation |
| Magenta | Protected Focal |
| White | Port |
| Red/orange-red | Transition footprint and landings |
| Cyan line | Axis between the first two mandatory ports |
| `H` label/ticks | Reserved headroom |

### 10. Add the valid disabled room to the explicit catalog

In the Project window, select:

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Catalog/dungeon_recipe_catalog.asset
```

In its normal Inspector:

1. Expand **Recipes**.
2. Increase **Size** by exactly one. The current committed catalog has Size
   `4`, so the first new room makes it `5`.
3. Drag `connector_my_room_01.asset` from the Project window into the new last
   element.
4. Verify that no existing element was replaced and no element says `None`.
5. Use **File > Save Project**.

Run:

```text
Arena > Dungeons > Recipes > Validate Catalog
```

With the four committed recipes still enabled and the new valid recipe still
disabled, the report should include:

```text
cataloged=5
enabled=4
disabled=1
invalid=[]
status=PASS
```

Catalog membership alone does not make the room selectable.

### 11. Enable the room

Select `connector_my_room_01.asset`. Uncheck:

```text
Disabled For Generation
```

You can do this in the normal Inspector or in:

```text
Arena > Dungeons > Recipes > Create Recipe
```

when the new recipe is assigned to that window's **Recipe** field.

Save the project, then run:

```text
Arena > Dungeons > Recipes > Validate Catalog
```

The report should now include:

```text
cataloged=5
enabled=5
disabled=0
invalid=[]
status=PASS
```

The catalog digest will change because the active catalog now contains another
enabled recipe.

At this point, and only at this point, the room is in ordinary generation:

```text
catalog member
AND Disabled For Generation == false
AND current contract validation passes
```

There is no approval object, promotion command, review token, or additional
availability state.

### 12. Generate a dungeon

For a disposable in-editor generation in the current scene, use:

```text
Tools > Dungeon Lab > Generate
```

or:

```text
Tools > Dungeon Lab > Generate (Specific Seed)
```

The specific-seed command opens a small wizard with one integer **Seed** field.
Enter the seed and click **Generate**. It creates/selects a `Generated Dungeon`
root in the current scene and marks the scene dirty; it does not save the scene
or export the production collision payload.

For the actual Arena production rebuild, use:

```text
Arena > Dungeons > Rebuild Random Dungeon
```

or:

```text
Arena > Dungeons > Rebuild Random Dungeon (Specific Seed)
```

The production rebuild:

- asks whether to save currently modified scenes;
- creates a new `RandomDungeon` scene;
- generates the dungeon;
- centers the spawn;
- prepares collision;
- saves
  `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`;
- adds that scene to build settings if needed;
- exports matching client/server shared collision data;
- saves the resulting assets.

Use the `Tools > Dungeon Lab` command when you only want to inspect a room.
Use the `Arena > Dungeons > Rebuild` command only when you intend to replace the
checked-in generated production scene and collision outputs.

The compression slot chooses uniformly and deterministically from all compatible
enabled compression recipes, sorted by Recipe Id. There is no per-recipe
selection weight. Adding this room gives the compression slot three candidates;
a particular ordinary seed may choose either existing room instead of the new
one.

The current normal-generation Console summary does not print the selected
recipe IDs, and there is no normal-generation “force this recipe” menu. The
deterministic authoring preview is the current fast way to force and validate
the new room. In an ordinary generated scene, identify the selected candidate
from its geometry, or use the existing batch/report tooling if exact
machine-readable selection evidence is required.

## Creating from the menu instead of duplicating

Duplication is recommended because it starts from a correctly typed Unity
asset. The official creation command is:

```text
Arena > Dungeons > Recipes > Create Recipe
```

With no Recipe assigned, the window shows:

- **Stable ID**, initially `connector_new_01`;
- **Kind**, `Connector` or `Episode`;
- **Create explicit disabled asset**.

For a compression room:

1. Enter a unique lowercase-underscore Stable ID.
2. Choose `Connector`.
3. Click **Create explicit disabled asset**.

The asset is created at:

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Rooms/<id>.asset
```

An Episode is created under:

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Episodes/<id>.asset
```

Creation sets only:

- Recipe Id;
- Display Name equal to Recipe Id;
- Kind;
- Schema Version `1`;
- Content Version `1`;
- Disabled For Generation `true`.

It leaves roles, beats, zones, ports, motifs, transitions, symmetry pairs, and
variations empty. The authoring window does not provide editors for those
arrays. Select the asset and fill them in the normal Inspector using the exact
values above.

The window's other controls are:

- a Recipe object field;
- read-only schema/content version text;
- read-only computed content digest;
- Disabled For Generation toggle;
- Validate button;
- Build deterministic gallery button;
- contract overlay;
- text output.

## Editing the room shape without breaking its contract

Each `Walkable` or `Elevated` zone is an axis-aligned rectangle:

```text
offset = its minimum local x and y cell
size = its cell count along x and y
```

The room footprint is the union of all Walkable and Elevated zone cells.
Multiple rectangles may overlap or combine into a non-rectangular footprint.
Protected zones do not add cells to the footprint.

For a compression candidate, keep these constraints:

- exactly two ports;
- IDs exactly `entry` and `exit`;
- both mandatory;
- both `Corridor`;
- both at Relative Level `0`;
- Width Cells exactly `1`;
- Approach Depth Cells at least `1`;
- Headroom Levels at least `3`;
- distinct cardinal outward directions;
- each port cell inside the footprint;
- each port's outward neighbor outside the footprint;
- roles include exact lowercase string `connector`;
- beats include exact lowercase string `compression`;
- at least one legal quarter turn;
- stay within the existing slot's available embedding envelope.

The proven 5-by-5 shape is the safe starting size. Larger, disjoint, or awkward
multi-rectangle rooms may pass the isolated contract but fail full-dungeon
placement because the existing route slot cannot embed them. The preview is
the authority for that integration check.

If you move a wall edge, move a port only when its exact boundary rule remains
true. If you extend the room left from `x=-2` to `x=-3`, for example, the entry
at `(-2,-1)` is no longer on an open boundary; either keep the original boundary
or move the entry cell to `(-3,-1)`.

## Adding a raised area and one internal stair

Do this only after the flat room passes. The current proven internal elevation
change is exactly one elevation level. One elevation level is one Unity world
unit.

To reproduce the existing central raised-gallery pattern, add:

### Elevated zone

| Field | Value |
| --- | --- |
| Id | `central_gallery` |
| Kind | `Elevated` |
| Offset | `(-1, 0)` |
| Size | `(3, 3)` |
| Relative Level | `1` |

### Motif

| Field | Value |
| --- | --- |
| Id | `rise_1_stair` |
| Kind | `Stair Transition` |
| Implementation Id | `seam-rise-1` |

### Transition

| Field | Value |
| --- | --- |
| Id | `gallery_stair` |
| Atomic Group Id | `gallery_stair` |
| Motif Id | `rise_1_stair` |
| Lower Transition Cell | `(0, -1)` |
| Upper Transition Cell | `(0, 0)` |
| Lower Landing Cells > Size | `1` |
| Lower Landing Cells > Element 0 | `(1, -1)` |
| Upper Landing Cells > Size | `1` |
| Upper Landing Cells > Element 0 | `(0, 1)` |
| Footprint Cells > Size | `1` |
| Footprint Cells > Element 0 | `(0, -1)` |
| Climb Direction | `(0, 1)` |
| Rise Levels | `1` |
| Lane Count | `1` |
| Headroom Levels | `3` |

The lower transition and lower landing cells must resolve to level `0`. The
upper transition and upper landing cells must resolve to level `1`. All listed
cells must be in the Walkable/Elevated footprint. Current production candidate
compatibility requires rise `1`, lane count `1`, nonempty lower and upper
landing arrays, a nonempty transition footprint, and at least 3 levels of
headroom.

Do not use an arbitrary stair prefab. The known reviewed stair implementation
used by current recipes is the exact string `seam-rise-1`.

## Complete schema-v1 field reference and validation rules

### Recipe

| Field | Meaning and current rule |
| --- | --- |
| Recipe Id | Required stable ID; only lowercase ASCII letters, digits, and underscore |
| Display Name | Human-readable label; not used for compatibility |
| Kind | Only `Connector` or `Episode`; folder name does not override this field |
| Schema Version | Must equal `1` |
| Content Version | Must be at least `1`; manually increment after meaningful edits to an existing recipe |
| Disabled For Generation | `true` excludes ordinary generation; preview still works |
| Eligible Roles | Nonempty exact-string list; compatibility requires a matching route-node role |
| Eligible Beats | Nonempty exact-string list; compatibility requires a matching route-node beat |
| Port Binding Mode | `ExactNamedPorts` preserves existing slot bindings; `IncidentCardinalSockets` activates declared cardinal sockets from incident route edges |
| Minimum Active Sockets | Used only by `IncidentCardinalSockets`; must be within `1..4` |
| Maximum Active Sockets | Used only by `IncidentCardinalSockets`; must be within `1..4` and not below the minimum |
| Allow Mirror | If true, placement may mirror across the local primary/X axis |
| Legal Quarter Turns | Nonempty unique values from `0`, `1`, `2`, `3` only |
| Zones | Rectangular floor/elevation/protection declarations |
| Ports | Exact boundary openings |
| Motifs | Only `StairTransition` and `FocalVisual` declarations exist |
| Transitions | Exact internal rise, landing, footprint, lane, and headroom contracts |
| Symmetry Pairs | Pairs of zones that must mirror across local X, mapping `(x,y)` to `(x,-y)` |
| Variations | Weighted focal alternatives inside one recipe, not weights between recipes |

The computed content digest includes all structural fields and content version.
It is diagnostic/catalog identity, not an approval state. Availability itself
is not serialized into the digest.

### Zone

Every zone has a stable unique Id, Kind, Offset, Size, and Relative Level.

| Kind | Rule |
| --- | --- |
| Walkable | Adds its rectangle to the room footprint; Relative Level must be `0` |
| Elevated | Adds its rectangle to the room footprint; Relative Level must be greater than `0` |
| Protected Circulation | Adds no floor; every cell must already be inside the footprint; Relative Level must be `0` |
| Protected Focal | Adds no floor; every cell must already be inside the footprint; Relative Level must be `0` |

Every Size X and Size Y must be at least `1`. The footprint must contain at
least one Walkable or Elevated cell. Where Elevated zones overlap, the resolved
cell level is the highest declared Elevated Relative Level.

### Port

Every port has a stable unique Id. Current rules are:

- Type must be `Corridor` for a mandatory production neighbor;
- Outward Direction must be one of `(1,0)`, `(-1,0)`, `(0,1)`, `(0,-1)`;
- Cell must be inside the footprint;
- Cell plus Outward Direction must be outside the footprint;
- Width Cells must equal `1`;
- Approach Depth Cells must be at least `1`;
- Headroom Levels must be at least `3`;
- Relative Level must equal the resolved level at Cell;
- exact named recipes require at least two Mandatory ports with distinct
  outward directions;
- incident-socket recipes require exactly four non-mandatory, level-0 Corridor
  sockets covering north, east, south, and west;
- all four socket cells must belong to one connected room footprint.

Exact named candidates in the three implemented fixed slots require every port
to be mandatory and bound by the slot. In practice those recipes have exactly
`entry` and `exit`. The generic-room prototype's explicit
`IncidentCardinalSockets` mode is the only current exception: its active subset
is bound by direction after placement, and inactive sockets create no corridor
opening.

### Motif

Every motif has a stable unique Id, Kind, and string Implementation Id.

- `StairTransition` is referenced by a Transition.
- `FocalVisual` is referenced by a Variation.

There is no Unity object reference or prefab reference in this record.

### Transition

Every transition has a stable unique Id and must:

- reference an existing `StairTransition` motif by Motif Id;
- use a cardinal Climb Direction;
- currently use Rise Levels exactly `1`;
- currently use Lane Count exactly `1`;
- reserve at least `3` Headroom Levels;
- have at least one Lower Landing Cell;
- have at least one Upper Landing Cell;
- have at least one Footprint Cell;
- place lower and upper transition cells inside the room footprint;
- resolve upper level minus lower level to the declared rise;
- place every lower landing at the lower level;
- place every upper landing at the upper level;
- keep every transition footprint cell inside the room footprint.

Atomic Group Id groups transitions that belong to one composition. The schema
does not independently validate that string as a stable member ID, but use a
stable lowercase-underscore ID and keep coupled transitions in the same group.

### Symmetry pair

Every pair has a stable unique Id and references two existing zone IDs. The
second zone's exact cells must equal the first zone mirrored across local X:

```text
(x, y) -> (x, -y)
```

### Variation

Every variation has a stable unique Id, references an existing `FocalVisual`
motif, and has Weight at least `1`. That motif's Implementation Id must resolve
to an existing backed StairForge showpiece containing actual pieces.

Weights select among focal alternatives inside one recipe. They do not affect
which recipe the compression pool selects.

## Exact catalog behavior

The catalog source is:

```text
Assets/Arena/Content/Settings/Dungeons/RandomDungeon/Recipes/Catalog/dungeon_recipe_catalog.asset
```

At load time:

1. The catalog must exist and have Schema Version `1`.
2. Every catalog element must be a non-null recipe asset.
3. Every Recipe Id must be unique, including IDs of disabled members.
4. Disabled members are skipped for active selection.
5. Every enabled member must pass current contract validation.
6. Enabled valid members are sorted by Recipe Id.
7. The active catalog digest is computed from that sorted enabled set.

An enabled invalid member fails the whole active catalog. It is not silently
skipped. A disabled incomplete member is excluded from active generation, but
**Validate Catalog** will still list that member in `invalid=[...]` because the
report separately validates every explicit catalog member. The cleanest
workflow is therefore to keep an incomplete recipe out of the catalog entirely.

Selection for each fixed slot is deterministic from:

- dungeon seed;
- topology ID;
- route node ID;
- the constant recipe-selection stream identity.

It does not use layout-attempt randomness and does not perturb spatial random
streams. Within the compatible set, selection is uniform; recipe IDs are sorted
before the deterministic index is chosen.

## Error checklist

| Result or symptom | Exact thing to check |
| --- | --- |
| `RECIPE_ID` | Recipe Id contains something other than lowercase letters, digits, or underscore |
| `RECIPE_SCHEMA_VERSION` | Schema Version is not `1` |
| `RECIPE_CONTENT_VERSION` | Content Version is below `1` |
| `RECIPE_ELIGIBILITY` | Roles or Beats has Size `0` |
| `RECIPE_ORIENTATION` | Turns is empty, duplicated, below `0`, or above `3` |
| `RECIPE_MEMBER_ID` | A zone/port/motif/transition/symmetry/variation ID is invalid or duplicated |
| `RECIPE_ZONE_SIZE` | A zone dimension is below `1` |
| `RECIPE_ZONE_LEVEL` | Non-elevated zone is not level `0`, or Elevated is not above `0` |
| `RECIPE_FOOTPRINT` | No Walkable or Elevated cells exist |
| `RECIPE_PROTECTED_ZONE` | Protected cells extend outside the floor footprint |
| `RECIPE_PORT_GEOMETRY` | Port is not on an exact open boundary, has bad direction/level/width/approach/headroom |
| `RECIPE_MANDATORY_PORTS` | Fewer than two ports are mandatory |
| `RECIPE_PORT_BINDING_MODE` | Port Binding Mode is not a supported enum value |
| `RECIPE_SOCKET_POLICY` | Incident socket minimum/maximum is outside `1..4` or inverted |
| `RECIPE_CARDINAL_SOCKETS` | Incident socket recipe does not declare exactly four non-mandatory level-0 cardinal Corridor sockets |
| `RECIPE_SOCKET_CONNECTIVITY` | One or more socket cells are disconnected from the common room footprint |
| `RECIPE_NEIGHBOR_PORT` | Mandatory port is not Corridor or duplicates another mandatory outward direction |
| `RECIPE_NEIGHBOR_MATRIX` | Fewer than two distinct mandatory approach directions exist |
| `RECIPE_TRANSITION_CONTRACT` | Transition lacks the required stair motif/rise/lane/landing/footprint/headroom data |
| `RECIPE_TRANSITION_GEOMETRY` | Transition cells or landings do not match the room's resolved levels and footprint |
| `RECIPE_SYMMETRY` | Zone references are missing or the pair is not an exact local-X mirror |
| `RECIPE_VARIATION` | Focal variation has bad weight or no backed showpiece implementation |
| `RECIPE_FULL_DUNGEON` | Forced placement, ports, transitions, canonical plan, renderer, abyss, collision, or preview context failed |
| `ROLE_INELIGIBLE` | Compression room does not include exact role `connector` |
| `BEAT_INELIGIBLE` | Compression room does not include exact beat `compression` |
| `TRAVERSAL_DEGREE_MISMATCH` | Candidate does not have exactly the two mandatory ports required by the slot |
| `PORT_BINDING_MISMATCH` | Port IDs are not exactly the slot's `entry` and `exit`, or extra/optional ports exist |
| `PORT_CLEARANCE_INCOMPATIBLE` | Port width is not `1`, approach is below `1`, or headroom is below `3` |
| `PORT_ELEVATION_INCOMPATIBLE` | A current fixed-slot port is not at level `0` |
| `ORIENTATION_UNSUPPORTED` | Legal turns is empty or the slot cannot resolve the required orientation |
| `TRANSITION_CONTEXT_INCOMPATIBLE` | Transition is not the supported rise-1, lane-1, reserved-landing form |
| `CONTRACT_INVALID` | One of the schema/structure/variation/neighbor checks failed |
| `recipe ... had no compatible required route slot` | Roles, beats, ports, orientation, or transition contract does not match any of the three implemented slots |
| Catalog `duplicate recipe ID` | Two catalog elements have the same Recipe Id, even if one is disabled |
| Catalog `catalog contained a null asset` | A Recipes element is `None` |
| Catalog `enabled recipe ... failed current validation` | Disable or fix the named enabled recipe |

## Editing an existing recipe later

For an existing cataloged room:

1. Check **Disabled For Generation** before making incomplete edits.
2. Make the structural change.
3. Increment Content Version manually.
4. Run **Validate Current Recipe**.
5. Run **Build Preview Gallery**.
6. Run **Validate Catalog**.
7. Re-enable only after all required checks pass.
8. Run **Validate Catalog** again.

Changing Recipe Id is a migration, not a normal edit. For a genuinely different
room, duplicate to a new asset and use a new stable ID.

## Bottom line

To create a room with what exists today:

```text
duplicate a compression recipe
-> give it a new stable ID
-> describe its floor in grid zones
-> keep exact entry/exit ports
-> validate while disabled
-> force-preview it
-> add it to the explicit catalog
-> enable it
```

That creates a selectable grid-defined room. If the intended workflow is
instead “build a room prefab in Unity, drag that prefab into a pool, and let the
generator connect it,” the current implementation does not provide that
workflow.
