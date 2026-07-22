# Density and adjacency plan

Status: proposed, not started (revised 2026-07-22 after review — topology counts corrected, reproducibility and anchor-alignment gaps addressed)
Owner intent: keep the cliffs/voids; add denser floorplans with neighboring rooms and
directly abutting tiers, in the spirit of the gold-standard scene
(`Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/scenes/demoscene_dungeon_level_1_dungeon.unity`).

## Goals

- More rooms that share walls (door thresholds instead of long corridors).
- More rooms with 3+ connections instead of one-in/one-out chains — **if** measured
  final degrees confirm the gap (see Measurements; the loop pass already adds a
  median of 3 edges per dungeon).
- Tiers that abut directly across cliff/railing seams (slice 6 — a goal, not optional).
- Preserve the voids: vista reservations, the atrium center void, and the abyss are
  design features, not density failures.

## Non-goals and guardrails

- No parallel planner, second layout path, or legacy adapter. Every slice
  parameterizes or extends the existing route-first seam.
- **The `spacious` profile is immutable once slice 1 lands.** The current look must
  stay reproducible: every new knob defaults to today's hardcoded value. Until a
  settings digest exists in reports (slice 1), the git history of
  `generation_profile.asset` is the only reproducibility record — so slice 0
  experiments must end in a **revert with candidate values recorded**; tuned
  values are committed only after slice 1's identity support lands.
- Voids untouched: no slice may shrink the atrium center void or vista reservations
  as a side effect (this constrains pitch work — see slice 4).
- Locks, ability gates, runtime generation: still out of scope.

## Process for this work (owner ruling 2026-07-22)

This is design flux, not an identity-preserving refactor. The phase-budget ritual in
`CURRENT_STATUS.md` (locked corpus hashes, pre-locked acceptance budgets, planner
version bumps, per-increment evidence sections) does **not** apply here.

What does apply:

- **Hard validity gates** — connectivity, landings, headroom, port graph, collision
  export. These are the "don't break the generator" contract and run per build.
- **Run-twice determinism** on any seed being inspected — same seed must produce the
  same dungeon, because server collision and the client scene must agree.
- **Sentinel eyeballing** — the six fixed sentinel seeds are the taste-review tool.
  Look at them; no gate attached.
- **Batch sweep as a smoke test, not a gate** — if a change makes a large share of
  seeds fail placement, find out before tuning taste on top of it.

Re-lock a corpus once, later, if/when the new look stabilizes.

## Diagnosis (corrected)

1. **Route graphs are sparse but not uniform chains.**
   - Processional and atrium-ring: 13 nodes / 13 edges — two degree-1 endpoints,
     two degree-3 junctions, nine degree-2 rooms.
   - Twin-wing: 13 nodes / 14 edges, cycle rank 2, two required degree-4 junctions
     (`wing-hub`, `wing-rejoin` in `BuildTwinWingRouteIntent`).
   - The late loop pass (`AddLevelSafeLoopConnections`) then adds more: the Phase 7
     corpus (2000 seeds) shows loop edges min 1 / p50 3 / p95 4 / max 5. Final room
     degree is therefore better than the route graph alone suggests — measure the
     final degree distribution per topology (see Measurements) before concluding a
     cross-link/hub operation is needed.
2. **Room pitch is hardcoded and rooms are small relative to it.** Node centers sit
   9x9 cells apart (processional) / 7x9 (atrium) / baked nonuniform coordinates
   (twin-wing). Rooms inflate centered (`CenteredRect`) inside a radius-4 envelope
   (`Phase1RoomEnvelopeRadius`) but are only 4-5 x 5-7 cells
   (`BuildProcessionalRoomParts`). At processional/atrium pitches every edge
   crosses a multi-cell gap, so those connections render as corridors and rooms
   cannot abut; twin-wing's 5-6-cell spine spacing may already produce abutment —
   the slice 1 baseline settles that. (At atrium's 7-cell x-pitch the 9-wide
   envelopes already overlap; inflation overlap checks resolve it.)
3. **Loop candidates are throttled by the level grammar.** Candidates must land on
   0/4/8u deltas, and the +24u ascending spine puts spatially near rooms at
   incompatible levels. Accepted loops are center-to-center corridor paths — the
   loop pass cannot produce shared-wall doors regardless of tuning.
4. **The profile's density knobs are gates, not drivers.** `denseFloorplanMinRooms`
   and `denseFloorplanMinFillPercent` reject sparse output; they cannot create
   density. Fill percent includes the voids in its bounding box, so raising it
   fights the voids.

Useful precedent: `AddPlannedOverlookAppendages` already produces directly abutting
room appendages across an elevation seam in production (at processional's pitch-9
envelopes tile exactly and both appendages extend to their envelope edge). The
boundary, renderer, abyss, and collision paths already digest non-traversal room
adjacency. The genuinely new cases are a **traversable** shared wall (a door) and
abutment at junction rooms (see slice 2 spike).

## Design principles

- **Abut only rise-0 edges.** The elevation story plateaus (processional main route
  0,0,4,4,8,12,16,20,24). Level edges are the natural dense clusters. Stair-bearing
  edges keep a gap sized to the required transition footprint — stairs are planned
  anchors per `PROJECT_INVARIANTS.md` — which preserves the dramatic spaced
  transitions.
- **Connection anchors must not silently move.** `RoomFootprint.Center` is the
  dominant rect's center, and `TryConnectProcessionalRooms` rejects edges whose
  endpoint centers are not cardinally aligned. Any inflation bias therefore needs
  explicit per-edge threshold anchors (or stable logical anchors at the embedded
  node centers) instead of relying on footprint centers — especially at junctions
  biased toward multiple neighbors on different axes.
- **Slices don't add recipe abutment initially.** Recipe ports declare approach
  depth and headroom; abutting an authored room changes its approach contract.
  Note this is a rule about what new slices may *add*, not a claim about today:
  twin-wing already places the 5x5 compression recipe 5 cells from its hub, so
  recipe near-abutment may already exist in production — measure, don't assert.

## Measurements (added per review — cheap diagnostics, not gates)

Extend the existing per-seed report (`BuildLayoutGraphSummary` already reports
`loopEdges`) with:

- final room degree distribution (route + loop edges), per topology;
- corridor evidence: exterior corridor cell count and per-connection length
  histogram (a shared-wall door = length-0 exterior path);
- shared-wall door count — **baseline unknown, not assumed zero**: twin-wing's
  keep spine has connected centers 5-6 cells apart with 5-wide generic rooms and
  the 5x5 vestibule recipe, so zero-exterior-cell connections may already occur;
- void extent: reserved vista cells and atrium center void cell count, so "voids
  untouched" is checkable (`reservedVoidPreservedAfterTierLooping` already exists).

These diagnostics are **slice 1 deliverables** — slices 3, 5, and 7 read them.

"Denser" is done when these move — shorter corridors, doors > 0, degree
distribution shifted — **and** the owner likes the sentinels, not when a screenshot
looks favorable.

## Slices

### Slice 0 — throwaway profile calibration (no code, git-guarded)

Edit `generation_profile.asset` values and rebuild a few seeds to calibrate taste:

- `loopConnectionFraction` up (e.g. 0.3 -> 0.5-0.7) — a *target*; achieved loops are
  limited by surviving candidates, so read `loopEdges` from the report rather than
  assuming the knob worked.
- `maxLoopCandidateDistanceCells` — calibrate **independently**. Lowering it only
  prunes candidates (nearest-first sorting already prefers short loops) and can
  *reduce* achieved loop count. Loops remain corridors either way; this knob cannot
  make them door-like.

End state is explicit: **revert the asset and record the candidate values in this
file** (or a notes file). Committing tuned values waits for slice 1's identity
support — slice 0 discovers numbers, it does not ship them.

### Slice 1 — profile identity and diagnostics (small, enables everything else)

The generator loads exactly one fixed asset (`GenerationProfilePath`) and reports
record only the profile *name*. Before any behavior knob lands:

- add a settings digest (hash of all `DungeonGenerationSettings` values) to the
  per-seed and aggregate reports, so "same seed + same profile" is verifiable;
- keep **one loading seam**: `LoadActiveGenerationSettings` — already the single
  loader behind every entry point's `CurrentGenerationSettings` assignment —
  becomes a resolver that selects an asset by profile ID and returns one
  `DungeonGenerationSettings`. Menu, builder, or environment input supplies the
  ID only; nothing loads settings independently or branches downstream on
  profile identity. No fallback profile, no duplicated "dense generation" entry
  point. `spacious` stays immutable; `dense` is a selectable identity, not an
  in-place edit;
- implement the Measurements diagnostics (final degree distribution, corridor
  evidence, shared-wall door count, void extent) in the same report pass — this
  also establishes the true shared-wall baseline across all three topologies.

Done when: a report from any build states which settings produced it **and**
carries the Measurements fields.

### Slice 2 — shared-wall door spike (verification only)

First check existing twin-wing output: its 5-6-cell spine spacing may already
produce zero-exterior-cell connections in the corpus, answering the door question
with production evidence. Then answer, with a throwaway seed or focused test:

- What does the boundary builder do when a `RoomConnection` path crosses a shared
  wall with zero exterior corridor cells? Door punched, wall, or rejection?
- Does `TryConnectProcessionalRooms` tolerate the degenerate path when facing walls
  touch (path entirely inside the two endpoint rooms)?
- Does `ElevationEdgeModel` / collision export handle a traversable seam between
  two distinct rooms at the same level?
- **Junction case:** a room biased toward one neighbor while holding cardinal
  alignment with two others on different axes — what breaks first (alignment
  rejection, corridor validation, thresholds)?

Done when: each question has a yes/no with evidence, **and an anchor model is
chosen** — per-edge threshold anchors vs stable embedded-node anchors — with its
junction invariants written down. Slice 5 implements that model without reopening
the choice. No production behavior change in this slice.

### Slice 3 — room size becomes a profile knob (highest value per risk)

Replace the hardcoded per-role dimensions in `BuildProcessionalRoomParts` with
profile-driven ranges. Defaults reproduce today's 4-5 x 5-7. Growing generic rooms
toward 7x7 shrinks gaps from ~4 cells to ~1-2, turning long corridors into
thresholds without touching topology or validators.

**Processional-only first pass** (same scoping as slice 4): twin-wing's connected
centers are only 5-6 cells apart, so global 7x7 growth guarantees overlap and
inflation failures there. Per-pattern ranges validated against each pattern's
minimum connected-center spacing come later if wanted.

**Stair-aware growth cap lives in this slice, not later — and it cannot know the
chosen stair.** Reviewed stair contracts load only at tier planning
(`LoadReviewedActiveStairOptions`, after inflation), and the concrete option is
selected later still. So the first-pass cap is: faces on stair/stairwell/bridge-
bearing edges **do not grow at all** (today's gap is known-sufficient). If that
proves too sparse, the follow-up is a conservative minimum derived from all
compatible reviewed contracts loaded before inflation (they are static data) —
not moving stair selection earlier. Watch `STAIR_PLACEMENT` rejection rates in
the smoke sweep; a spike means the cap is wrong.

Done when: a `dense` profile variant (via slice 1 selection) visibly shortens
corridors on the sentinels, hard gates pass, smoke sweep failure rates acceptable.

### Slice 4 — per-pattern pitch knobs

Expose spacing as **per-pattern X/Y values** (defaults: processional 9/9, atrium
7/9), with envelope radius validated independently per pattern — a single coupled
scalar cannot reproduce current behavior. Scope the first pass to **processional
only**: atrium pitch reduction shrinks its center void (guardrail violation) and
twin-wing bakes nonuniform spacing into raw coordinates; both need their own
treatment or an explicit exemption.

**Spatial settings get one owner in this slice.** Resolve one pattern-specific
spatial configuration — pitch, envelope radius, and the slice 3 room-size ranges —
and consume it everywhere the constants reach today: embedding, envelope
reservation, the map-bounds check, overlook appendages, and batch diagnostics
(`Phase1RoomEnvelopeRadius` currently has consumers in both
`DungeonLabGenerator.RouteFirstPilot.cs` and `DungeonLabGenerator.Batch.cs`).
Delete the superseded hardcoded constants when this lands; no consumer stays on
old values.

Done when: lowered processional pitch + slice 3 sizes produce visibly packed
clusters on level plateaus; defaults reproduce today's output; atrium/twin-wing
untouched; `Phase1RoomEnvelopeRadius` and the spacing literals are gone.

### Slice 5 — neighbor-biased inflation (true abutment)

Bias room inflation toward route neighbors on **rise-0 edges only**, so facing
walls meet and the connection becomes a shared-wall door (per slice 2 findings).
**Processional-only first pass** (same scoping as slices 3-4); atrium and
twin-wing wait until their spacing constraints are addressed. Connection
endpoints move to the anchor model chosen in slice 2, so junction alignment
cannot break when dominant rects shift. Stair-bearing edges keep their slice 3
minimum gap. Bias strength is a profile knob; 0 reproduces today.

Done when: sentinels show same-level pairs sharing walls with working doors;
processional shared-wall door count **increases over the slice 1 baseline** (at
least one new door — twin-wing may make the global count nonzero already); hard
gates and collision parity pass.

### Slice 6 — tier-seam adjacency (abutting tiers — a stated goal)

Generalize the planned-overlook mechanism: allow declared non-traversal adjacency
between rooms at 4/8u deltas as a per-pattern knob (count/eligibility), rendering
as cliff/railing seams. Reuses the existing appendage + elevation-edge path.

The count/eligibility policy **replaces** the hardcoded overlook producer as the
sole producer of `intent.plannedOverlooks` — the two must not coexist. Today's
processional pairs become the `spacious` profile defaults.

Done when: patterns can request N overlook adjacencies and they read as intentional
tiered seams on the sentinels.

### Slice 7 — connectivity topology (decision gated on data)

Only if the slice 1 degree-distribution diagnostics show final degrees are still
chain-like after slices 3-5: add the deferred cross-link/hub graph operation
together with the first topology that consumes it, per the existing rule that graph
operations are introduced only with a consumer. Note twin-wing already carries two
degree-4 junctions and cycle rank 2 — it may already be the dense-connectivity
pattern, wanting tuning rather than a new operation. Own arc; fresh session.

## Clean end state

One profile resolver, one pattern-spatial configuration source, one room-inflation
path, one connection compiler, one planned-overlook producer. Each slice deletes
its superseded constants, helpers, temporary flags, and unused fields before
closing. When the plan exits, no dense-specific builder, renderer branch,
compatibility path, or fallback profile remains.

## Knob reference

| Driver | Today | Where |
| --- | --- | --- |
| Route pitch | 9/9 processional, 7/9 atrium, baked (twin-wing) | `TryEmbed*Route` spacing args, `DungeonLabGenerator.RouteFirstPilot.cs` |
| Envelope radius | 4 (hardcoded, all patterns) | `Phase1RoomEnvelopeRadius` |
| Generic room dims | 4-5 x 5-7 by role (hardcoded) | `BuildProcessionalRoomParts` |
| Inflation placement | centered (hardcoded) | `CenteredRect` in `BuildProcessionalRoomParts` |
| Connection anchors | dominant-rect centers, cardinal-aligned | `RoomFootprint.Center`, `TryConnectProcessionalRooms` |
| Wing chance/size | 0.4, 2x2 (hardcoded) | `BuildProcessionalRoomParts` |
| Loop fraction (target) | 0.3 (profile); achieved p50 3 edges | `loopConnectionFraction`, report `loopEdges` |
| Loop candidate distance | 18 (profile) | `maxLoopCandidateDistanceCells` |
| Room-count / fill gates | 10 / 0.26 (profile) | `denseFloorplanMinRooms`, `denseFloorplanMinFillPercent` |
| Graph shape | 13n/13e + 2 deg-3 (proc, atrium); 13n/14e + 2 deg-4 (twin-wing) | pattern builders, `DungeonLabGenerator.RouteFirstPilot.cs` |
