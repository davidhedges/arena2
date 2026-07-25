# Dungeon generator: current status

Last updated: 2026-07-25

No phased plan is in progress. This page describes what the generator is and where the work stands. Keep it short — if it starts growing per-phase evidence sections again, that evidence belongs in `DungeonLabReports/` or `docs/archive/`, not here.

## What it does

One integer seed produces one deterministic Unity scene (`Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`) plus matching client/server collision payloads.

Generation is editor-time only, on purpose: a client-only runtime layout would disagree with the authoritative server collision.

Pipeline, in execution order:

```text
seed + generation profile + recipe catalog
  -> RouteIntent            semantic graph, no coordinates
  -> embedding              node centers
  -> room footprints        + recipe placements
  -> corridors              -> DungeonLayout
  -> elevation + transitions-> TieredLevelPlan
  -> ElevationEdgeModel     -> GameObjects
  -> collision export       -> Resources + server/src/world_data
```

## How to run it

| Goal | Action |
|---|---|
| Rebuild the playtest scene | **Arena > Dungeons > Rebuild Random Dungeon** |
| Reproduce a specific layout | **Arena > Dungeons > Rebuild Random Dungeon (Specific Seed)** |
| Switch density | **Arena > Dungeons > Generation Profile > Spacious / Dense** (per-user pref; `ARENA_DUNGEON_GENERATION_PROFILE` overrides; batch mode defaults to `spacious`) |
| Plan only, no scene | **Tools > Dungeon Lab > Generate** |
| Batch evidence | **Tools > Dungeon Lab > Batch Validate (50 / 200 / 100 Locked Seeds)** |
| Command line | `-executeMethod DungeonLab.Editor.RandomDungeonSceneBuilder.RebuildRandomDungeonBatch`, with `ARENA_RANDOM_DUNGEON_SEED` set |

Always publish/restart the server module after regenerating, so server movement and spawning use the same geometry as the client scene.

## Current shape

- Three route patterns selected deterministically from the seed: processional spine, atrium ring, twin-wing keep.
- Three required recipe slots (`required-compression`, `required-landmark`, `required-return`) filled from an explicit catalog; four enabled recipes currently.
- Vertical traversal from reviewed stair contracts, forged contracts, online synthesis, stairwell towers, and bridges — in that fallback order.
- One planned vista with a reserved sight corridor, plus 1–4 external connector promontories.
- Everything rises from a shared abyss datum.

## Where the work stands

All previously planned phases are closed. The per-phase evidence log lives in
[`docs/archive/2026-07-dungeon-phase-log/CURRENT_STATUS.md`](../archive/2026-07-dungeon-phase-log/CURRENT_STATUS.md)
if you ever need to reconstruct why a decision was made.

The current open item is the architectural review:
[`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md). Its
recommended next actions, in order, are:

1. Delete the closed-phase evidence code and its stale hash-lock tests (§12, R0).
2. Make hard validation gate generation, and make renderer skips fatal (§12, 2.1).
3. Give the tier planner derived RNG streams (§12, 2.2).

## Known stale artifacts

Three EditMode tests fail, and all three are stale locks from closed phases — none indicates a broken dungeon:

- `FixedAndRegressionProductionSeeds_AreHardValidAndPreservePlans` — hardcoded plan hash is out of date. Its `hardValid` assertions pass.
- `TierRetryOptimization_PreservesTheExactOutlierSeedResult` — same, different hash.
- `FinalDeletionLedger_HasNoRandomDaisProducerOrLegacyRendererScaffolding` — asserts source text contains `StairForge.TryGetBackedShowpieceDesign`, which has since moved.

They should be deleted along with the rest of the phase archive rather than re-baselined.

## Read next

1. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules. Read before changing generator, measurement, contract, or placement code.
2. [`GLOSSARY.md`](GLOSSARY.md) — authoritative vocabulary (role vs. beat, room vs. recipe, zone, port, transition, reservation).
3. [`ARCHITECTURE_REVIEW_2026-07-25.md`](ARCHITECTURE_REVIEW_2026-07-25.md) — current system model and recommended work.
4. [`ROOM_AUTHORING_GUIDE_CURRENT.md`](ROOM_AUTHORING_GUIDE_CURRENT.md) and [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — adding content.
5. [`stair_forge_design.md`](stair_forge_design.md) — vertical traversal decision history.
