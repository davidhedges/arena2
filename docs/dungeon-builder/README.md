# Random dungeon integration

The former `dungeon_builder` Unity project is integrated as an Arena feature instead of a second project root.

Read [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) before changing generator, measurement, contract, or placement code.

- `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack` contains the original asset-pack content.
- `Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon` contains Dungeon Lab's authored stairs and set pieces.
- `Assets/Arena/Content/Settings/Dungeons/RandomDungeon` contains generation contracts, measurements, and the active generation profile.
- `Assets/Arena/Editor/Dungeons/RandomDungeon` contains the editor-only generator and Arena scene builder.
- `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity` is the baked playtest destination.

## Regenerating the destination

In Unity, use **Arena > Dungeons > Rebuild Random Dungeon**. Use the specific-seed command when reproducing a layout. For command-line builds, invoke:

```text
-executeMethod DungeonLab.Editor.RandomDungeonSceneBuilder.RebuildRandomDungeonBatch
```

Set `ARENA_RANDOM_DUNGEON_SEED` to an integer to make the batch entry point deterministic.

The builder recenters a safe generated floor at world `(0, 0, 0)`, authors the standard Arena gameplay camera, adds the scene to build settings, and exports `random_dungeon` collision payloads to both Unity Resources and `server/src/world_data`. Always publish/restart the server module after regenerating so server-authoritative movement, player spawning, and NPC/minion spawning use the same geometry as the client scene.

Runtime generation is intentionally not used yet: a client-only random layout would disagree with Arena's authoritative server collision. The editor workflow gives each rebuild a new random dungeon while keeping a single synchronized playtest destination.
