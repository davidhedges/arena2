# Random dungeon integration

The former `dungeon_builder` Unity project is integrated as an Arena feature instead of a second project root.

## Start here

1. [`PROCEDURAL_3D_TOPOLOGY_PLAN.md`](PROCEDURAL_3D_TOPOLOGY_PLAN.md) — active architectural direction and bounded implementation order.
2. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — hard geometry, traversal, and placement rules. Read before changing generator, measurement, contract, or placement code.
3. [`GLOSSARY.md`](GLOSSARY.md) — authoritative vocabulary (role vs. beat, room vs. recipe, zone, port, transition, reservation).
4. [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md) — system model, findings, and historical recommendations.
5. [`layered-topology-design-2026-07-29.md`](layered-topology-design-2026-07-29.md) — landed multi-surface substrate and its implementation record.
6. [`ROOM_AUTHORING_GUIDE_CURRENT.md`](ROOM_AUTHORING_GUIDE_CURRENT.md), [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) and [`ROUTE_TOPOLOGY_AUTHORING.md`](ROUTE_TOPOLOGY_AUTHORING.md) — existing content-authoring workflows; where they imply that authored rooms or exact topology diagrams are the future production architecture, the procedural 3-D plan wins.
7. [`stair_forge_design.md`](stair_forge_design.md) — vertical-traversal design decisions and implementation history.

Completed phase plans and status/evidence snapshots live in
[`docs/archive/2026-07-dungeon-phase-log/`](../archive/2026-07-dungeon-phase-log/)
and
[`docs/archive/2026-08-dungeon-layering-status/`](../archive/2026-08-dungeon-layering-status/).
They are history, not instructions — do not treat their acceptance budgets,
production claims, or locked hashes as current constraints.

## Repository locations

- `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack` contains the original asset-pack content.
- `Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon` contains Dungeon Lab's authored stairs and set pieces.
- `Assets/Arena/Content/Settings/Dungeons/RandomDungeon` contains generation contracts, measurements, and the active generation profile.
- `Assets/Arena/Editor/Dungeons/RandomDungeon` contains the editor-only generator and Arena scene builder.
- `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity` is the baked playtest destination.

## Regenerating the destination

For interactive Unity testing, choose how packed the dungeon is under
**Arena > Dungeons > Density > 0..5**. 0 keeps today's large voids; 5 packs them
out. The checked choice is stored in per-user editor preferences, applies to
generation, rebuild, batch, and sentinel menu commands, and does not modify the
profile asset. An `ARENA_DUNGEON_DENSITY` environment variable set to an integer
0-5 overrides the editor choice; with neither, the dial is the profile asset's
own `densityLevel`, which is what makes a command-line batch reproducible from
the repo alone.

The dial is complete — all six phases of
[`density-scale-design-2026-07-27.md`](density-scale-design-2026-07-27.md)
landed 2026-07-28, and every level resolves to its own geometry. Measured floor
fill runs 26/33/47/65/80/93% across levels 0-5.

In Unity, use **Arena > Dungeons > Rebuild Random Dungeon**. Use the specific-seed command when reproducing a layout. For command-line builds, invoke:

```text
-executeMethod DungeonLab.Editor.RandomDungeonSceneBuilder.RebuildRandomDungeonBatch
```

Set `ARENA_RANDOM_DUNGEON_SEED` to an integer to make the batch entry point deterministic.

The builder recenters a safe generated floor at world `(0, 0, 0)`, authors the standard Arena gameplay camera, adds the scene to build settings, and exports `random_dungeon` collision payloads to both Unity Resources and `server/src/world_data`. Always publish/restart the server module after regenerating so server-authoritative movement, player spawning, and NPC/minion spawning use the same geometry as the client scene.

Runtime generation is intentionally not used yet: a client-only random layout would disagree with Arena's authoritative server collision. The editor workflow gives each rebuild a new random dungeon while keeping a single synchronized playtest destination.
