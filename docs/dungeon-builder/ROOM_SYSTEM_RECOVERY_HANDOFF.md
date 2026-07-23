# Dungeon room-system recovery handoff

Status: factual handoff only; not independent scope authority

Date: 2026-07-24

## Owner target

Continue in this repository. Do not restart the dungeon generator.

The intended room system must support:

- rooms with distinct, understandable purposes;
- convincing and varied room shapes;
- spatially grouped neighboring rooms rather than only a linear route;
- shared connections between neighboring rooms;
- rooms with two, three, or four exits;
- recipes selected from pools;
- reference prefabs or demo-scene constructions used to extract shape and
  treatment rules without normally instantiating a whole room prefab over
  generated geometry.

The gold-standard spatial reference is:

```text
Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity
```

The owner-authored reference prefab is:

```text
Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/SetPieces/Generic_Room.prefab
```

## 2026-07-24 committed foundation

- `b0101950` simplified recipe availability to explicit catalog membership,
  current validation, and one disabled flag.
- `46c02abb` replaced exact recipe IDs with deterministic selection from the
  active catalog for the three existing fixed slots.
- `5fa1521f` allowed disabled recipes to be forced through an isolated
  authoring-preview catalog scope.
- `0187d33d` added `connector_example_01` as an approved fixed-slot proof.

These commits remain in history. They are not authorization to keep extending
the fixed-slot, route-first model. Catalog selection and preview isolation are
potentially reusable foundations; the current three fixed slots are an
implementation limitation, not the owner target.

## Recovery completed after visual review

An uncommitted experiment temporarily added a `roomPrefab` field to
`DungeonRecipeAsset` and instantiated `Generic_Room.prefab` after generated
layout rendering. Visual review showed that it duplicated and trampled a
generated room that was already spatially useful.

That entire whole-room binding pathway was removed before checkpointing:

- no room-prefab field remains in the recipe schema;
- no prefab digest or prefab validator remains;
- no recipe-prefab renderer remains;
- no prefab-instance preview assertion remains;
- `connector_generic_room_01` has no serialized prefab reference.

Do not restore that experiment as the default room workflow.

## Preserved authoring work

- The Dungeon Recipe Authoring window has tooltips for its visible fields and
  actions.
- A focused queued-preview helper exists for driving the current schema-v1
  authoring preview through the already-open Unity editor.
- `ROOM_AUTHORING_GUIDE_CURRENT.md` records the exact schema-v1 workflow at
  commit `0187d33d`. It documents the current fixed-slot limitation; it is not
  the desired future workflow.
- `GENERIC_ROOM_FAMILY_BRIEF.md` records measured facts from the reference
  prefab.
- `connector_generic_room_01` is disabled and absent from the production
  catalog. It is only a schema-v1 two-exit structural prototype, not the final
  multi-exit room model.

## Working-tree ownership

Do not overwrite, delete, or revert these without explicit owner approval:

```text
Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/SetPieces/Generic_Room.prefab
Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/SetPieces/Generic_Room.prefab.meta
Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity
```

The prefab is owner-authored and intentionally preserved. The gold-standard
scene is currently modified in the working tree; determine ownership and
whether generated preview debris is present before proposing any cleanup.

## Safe next-chat starting point

Read this handoff and inspect repository state first. The next implementation
direction is not yet approved.

Before editing, perform a read-only comparison between the gold-standard
scene's grouped-room floorplan and the current generator's room opportunity,
adjacency, shape, and exit-degree models. Report which existing components can
be retained and which fixed-slot or route-first assumptions block the owner
target. Do not add content, another prefab pathway, or a new plan phase during
that audit.
