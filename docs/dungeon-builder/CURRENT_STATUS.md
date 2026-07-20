# Dungeon generator: current status

Start here when returning to dungeon work. Keep this page short and update it at the end of every dungeon-generation session.

Last updated: 2026-07-21  
Active milestone: single-path roadmap revised; Phase 0 implementation has not started  
Production mode: current generator; no alternate planner mode exists  
Route-first planner: design only; not selectable  
Recipe authoring UI: design only; not implemented

## Read next

1. [`COHERENT_FLOORPLAN_PLAN.md`](COHERENT_FLOORPLAN_PLAN.md) — architecture, phases, and exit gates.
2. [`PROJECT_INVARIANTS.md`](PROJECT_INVARIANTS.md) — non-negotiable geometry and placement rules.
3. [`RECIPE_AUTHORING_WORKFLOW.md`](RECIPE_AUTHORING_WORKFLOW.md) — the post-foundation content workflow.
4. [`stair_forge_design.md`](stair_forge_design.md) — detailed vertical-traversal decisions and history.

## Locked context

- Everything rises from the common abyss. The resulting vertical supports are intentional and acceptable.
- The current generator is route-aware but not strict route-first: it places rooms/corridors before deriving the final route/elevation structure.
- Current seeds create many layouts and there are multiple named elevation archetypes, but there is no named semantic macro-topology catalog yet.
- Traversal and visibility will be planned separately so overlooks can reveal meaningful distant rooms.
- Vista realization is not deferred architecturally: Phase 1's skeleton must represent facing, reserved void, and an unobstructed source-to-target sight volume; Phase 3 proves one required vista.
- Recipes will complement generic generation; they will not replace the whole floor with a large pool of fixed room prefabs.
- Active late-pass step-formation placement is parked. Normal transition stairs, stair forging/synthesis, bridges, seam steps, daises, showpieces, and promontories are still active capabilities.
- Useful step formations should enter coherent mode as explicitly reserved recipe motifs, not by enabling the old global pass.
- Current doorway expression is generally a deliberate wall gap. Rich gateway geometry is not a prerequisite for the planner.
- `DungeonLayout` and `TieredLevelPlan` remain the canonical downstream plans. Do not create parallel intent/placed/compiled DTO families or a legacy adapter.
- Migration gets one temporary selector at layout-builder selection. It is removed with the old 2D builder in Phase 2, before vertical-intent and recipe work.
- Each phase has a deletion ledger. A phase is incomplete if its scheduled temporary or superseded code remains reachable.
- The first spatial algorithm is skeleton-first: embed a coarse self-avoiding route, attach a bounded branch/rejoin, reserve room envelopes, then inflate rooms and connect boundary anchors.
- Named topology variants must compose shared graph operations rather than become separate planner implementations.
- Locks, ability gates, and one-way traversal are out of scope, but the traversal-edge design must not foreclose those future consumers.

## Exact next action

Begin Phase 0 without changing generation behavior:

1. make the existing 200-seed batch use a documented deterministic seed range and lightly annotate only six visual sentinels spanning good, weak, and edge-case output;
2. add a batch-readable layout/elevation/transition summary with a stable hash;
3. add editor tests for seed determinism, connectivity, vertical traversal, headroom, and renderer preconditions;
4. record the current completion and layout-attempt distribution over a deterministic sample large enough to set a meaningful p95, without building a general metrics dashboard;
5. characterize the existing `BuildRandomDungeonLayoutData -> DungeonLayout -> TryBuildTieredLevelPlan -> TieredLevelPlan -> renderer` seam;
6. lock Phase 1's completion floor, attempt ceiling, and p95 attempt target before implementing the pilot; the completion floor cannot be below 95/100 over the first 100 seeds in the fixed range;
7. record rejection reasons and the empty initial deletion ledger.

Do not add new plan DTOs, adapters, or a planner-mode framework in Phase 0. Do not begin general recipe authoring or tooling until Phase 5. The single throne-hall episode in Phase 4 is a deliberately minimal schema probe, not the start of a broad content library.

## Known implementation facts

- Generator code: `Assets/Arena/Editor/Dungeons/RandomDungeon/`
- Content settings: `Assets/Arena/Content/Settings/Dungeons/RandomDungeon/`
- Authored dungeon prefabs: `Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/`
- Gold-standard reference scene: `Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity`
- Baked destination: `Assets/Arena/Content/Scenes/OpenWorld/RandomDungeon.unity`
- Existing editor-test area: `Assets/Arena/Tests/Editor/`
- `ActiveStepFormationPlacementEnabled` is currently false.
- `step_library_index.json` is currently absent, so the parked step-formation path is not ready to enable by changing one flag.
- `DungeonLabGenerator.cs`, `ElevationEdgeModel.cs`, and `StairForge.cs` total roughly 27,000 lines; do not pre-emptively refactor them.

## Last verified commands and seeds

None recorded yet. Phase 0 must establish these.

When work begins, replace this section with copyable commands and a small table:

| Purpose | Seed | Result | Report path |
| --- | ---: | --- | --- |
| Known-good baseline | — | Not established | — |
| Known-bad/diagnostic | — | Not established | — |

## Active blockers or pending decisions

No blocker prevents Phase 0. The exact Phase 1 attempt ceiling and p95 target are intentionally pending baseline measurement; record them here before Phase 1 starts. Later human-review thresholds must likewise be locked before their corresponding final review.

## End-of-session handoff template

Replace the status fields above, then record:

```text
Milestone/phase:
Completed this session:
Current validation result:
Last known-good seed and report:
Last diagnostic seed and failure reason:
Uncommitted/generated files to preserve:
Deletion-ledger items added/closed:
Exact next file, command, or Unity menu action:
Blocker or decision needed:
```
