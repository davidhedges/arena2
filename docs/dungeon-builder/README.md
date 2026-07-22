# Random dungeon integration

The former `dungeon_builder` Unity project is integrated as an Arena feature instead of a second project root.

## Start here

If you are returning after time away, read these in order:

1. [`CURRENT_STATUS.md`](CURRENT_STATUS.md) — active milestone, exact next action, and known state.
2. [`COHERENT_FLOORPLAN_PLAN.md`](COHERENT_FLOORPLAN_PLAN.md) — the route-first implementation roadmap and exit gates.
3. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — hard geometry, traversal, and placement rules.
4. [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — the workflow to use once the recipe foundation is implemented.
5. [`stair_forge_design.md`](stair_forge_design.md) — vertical-traversal design decisions and implementation history.
6. [`DENSITY_ADJACENCY_PLAN.md`](DENSITY_ADJACENCY_PLAN.md) — completed denser-floorplan, neighboring-room, and tier-seam workstream; closed at slice 6 with connectivity-topology slice 7 tabled.

Always read [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) before changing generator, measurement, contract, or placement code.

## Repository locations

- `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack` contains the original asset-pack content.
- `Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon` contains Dungeon Lab's authored stairs and set pieces.
- `Assets/Arena/Content/Settings/Dungeons/RandomDungeon` contains generation contracts, measurements, and the active generation profile.
- `Assets/Arena/Editor/Dungeons/RandomDungeon` contains the editor-only generator and Arena scene builder.
- `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity` is the baked playtest destination.

## Regenerating the destination

For interactive Unity testing, choose the active profile under
**Arena > Dungeons > Generation Profile > Spacious/Dense**. The checked choice is
stored in per-user editor preferences, applies to generation, rebuild, batch, and
sentinel menu commands, and does not modify either profile asset. A non-empty
`ARENA_DUNGEON_GENERATION_PROFILE` environment variable overrides the editor
choice; command-line batch mode otherwise keeps the reproducible `spacious`
default.

In Unity, use **Arena > Dungeons > Rebuild Random Dungeon**. Use the specific-seed command when reproducing a layout. For command-line builds, invoke:

```text
-executeMethod DungeonLab.Editor.RandomDungeonSceneBuilder.RebuildRandomDungeonBatch
```

Set `ARENA_RANDOM_DUNGEON_SEED` to an integer to make the batch entry point deterministic.

The builder recenters a safe generated floor at world `(0, 0, 0)`, authors the standard Arena gameplay camera, adds the scene to build settings, and exports `random_dungeon` collision payloads to both Unity Resources and `server/src/world_data`. Always publish/restart the server module after regenerating so server-authoritative movement, player spawning, and NPC/minion spawning use the same geometry as the client scene.

Runtime generation is intentionally not used yet: a client-only random layout would disagree with Arena's authoritative server collision. The editor workflow gives each rebuild a new random dungeon while keeping a single synchronized playtest destination.
