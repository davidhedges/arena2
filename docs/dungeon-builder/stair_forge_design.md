# Stair Forge & 1u Elevation Redesign — Decision Record

Status: PREPARATION. No code yet. Decisions below were made 2026-06-10 and are binding
unless explicitly revisited.

> **2026-06-13 — Magnificence-study decisions A–K supersede parts of this record.**
> The gold-scene magnificence study produced an approved design direction (elevation
> grammar of 4u major tiers with 1–2u intraroom accents; balanced 25–36-cell platforms;
> underworld structure below void edges; railings only at void edges and ≥4u drops;
> one monumental forge stair per dungeon; twin-stair dais; vestibule sub-rooms;
> colonnades; net climb ~24u+; pier over void; shell walls above the top floor).
> Where this record conflicts, A–K wins; each conflicting rule below is tagged
> **[SUPERSEDED-PENDING: …]** and gets rewritten in the implementation phase that
> touches it. Non-tagged rules stand (wall invariant, headroom, no-free-walkability,
> forge costing, uniform steepness, etc.).

## Core direction
- Replace tedious hand-authoring of stair variants with a **stair forge**: a generator
  that assembles staircases from the package's atomic pieces and emits geometry and
  contract from one code path (they can never disagree — the root cause of most past
  stair bugs was prefab/contract drift).
- Move the world elevation quantum from 2u to **1u** as a recalibration of the existing
  integer level system (levelHeight 2u -> 1u, all level numbers double). The plan-space
  grid stays **4u cells** — only the height quantum changes.
- Migration uses an **identity checkpoint**: switch the quantum but emit only even
  levels first; batch harness + fixed-seed comparison must show identical output before
  any odd-level feature turns on.

## Hard rules (user decisions)
1. **Wall invariant (unchanged, sacred)**: if there is a floor, walls render beneath
   its edges to complete the tier shape. Walls are never suppressed. Stairs only affect
   railings and port openings.
2. **Headroom**: minimum 3u clearance between any walkable surface and geometry above
   it. New validator; applies to pass-unders, bridges, overhangs, forge candidates.
3. **No free walkability across elevation deltas**: every delta, including 1u, requires
   steps. A row of steps across a room is a rise-1, N-lane transition (seam transition)
   using P_MOD_Stairs_01_E_straight_4 strips.
4. **Ledge policy (refined 2026-06-10 during step 4; face rule corrected 2026-06-11
   after the first odd-level visuals)**: a 1u drop gets no GUARD — no railing, no
   parapet — but its face below the edge IS closed like every other drop face
   (decision 1's wall invariant holds with no exceptions: bare 1u reveals read as
   holes against the pack's skirt-less one-sided floors). The pack has no 1u wall
   course, so wall stacks anchor at the TOP of the drop and an odd drop sinks its
   bottom course 1u below the lower floor/ground, hidden inside the tier mass.
   Drops of 2u or more get a guard that may be a railing OR a wall, as long as the
   choice is consistent within a room. "Half railing" refers to the half-LENGTH
   piece for sub-cell edges (e.g. tiny/half floor pieces), not a lighter guard for
   short drops. (Tunable, but this is the rule.)
   **[SUPERSEDED-PENDING: decision D, Phase 2]** — gold-scene data: railings mark
   void/danger, not tiers (128/181 railings on void edges; internal 2–4u drops open).
   New rule when Phase 2 lands: guard only at void edges and drops ≥4u; internal
   1–3u drops get no guard. The face-closure half of this rule (wall invariant,
   1u course sinking) is unaffected.
5. **Simplicity first, coolness opportunistic**: forge search is cost-based
   (pieces + turns + detour cells); minimal candidate wins by default. With a small
   flourish probability (~10-15%), an ornate candidate may win ONLY if it fits without
   detouring more than ~2x the minimal path. Never contrive twists for a 1-2 cell gap.
6. **Bridges can create connections** (aerial loop edges between high rooms), bounded
   count per dungeon. A bridge may exit a building side, run a long flat span, and
   re-enter another room.
7. **Doorway/gateway pieces are deferred**. Room entrances/exits are plain gaps in the
   wall, minimum 4u wide (one full cell edge — matches current behavior, so no
   sub-cell wall fitting is needed for entrances).
8. **Dual-lane eligibility**: any STRAIGHT staircase (no turns) with enough room is
   eligible for two lanes. Not forced. Fixed width per staircase (no mid-merges in v1).
9. **Uniform steepness per staircase**: one flight family throughout; no mixed rises.
   Consequence: achievable total rise must be divisible by the flight rise; steepness
   is chosen from divisors of the required rise (1u always works).
10. **Overlook gate demoted — DONE 2026-06-10**: the "spatial delta>=2 overlooks"
    hard requirement is now a reported stat only (gate removed from
    TryBuildTieredLevelPlanAttempt). Cantilevered/jutting platforms are NOT a
    feature; a small landing pad is permitted only when no simpler geometry closes
    a stair connection.
    **[SUPERSEDED — decision C DONE 2026-06-15; railing half still PENDING
    decision D]** — the gate stays dead (do not revive it). Decision C landed
    the underworld: every void-edge cliff now drops to a shared abyss base
    ~20u below the lowest floor (ElevationEdgeModel AbyssDepthLevels), so a
    platform bordering void IS now an overlook over a real deep drop. The
    "cantilevered platforms are NOT a feature" clause is re-read as "no
    gratuitous juts"; perching walkable space over the underworld void is
    intended. WHAT GUARDS those overlooks (railing vs nothing on ≥4u/void
    drops) is decision D, not yet implemented.
11. **Hand-authored contracts stay** in the pool; the forge competes on equal cost
    terms. Forge output passes the same automated validation gates.
12. **Support columns: yes** — decorative columns under long spans where they land on
    non-walkable ground and respect headroom. Never block walkable cells (v1).
13. **Grand stairs concept: REMOVE entirely** (EnableGrandStairTransitions,
    connection-point grand proof, spatial grand connectors, reserve-first grand
    stairs — all of it). Delete before the quantum recalibration to shrink the
    constants audit. A future "grand" staircase is just a high-spectacle forge output.
14. **Dais: rebuild on the 1u system** (it becomes an in-room raised zone with seam
    transitions; the parked 2u dais machinery is superseded).
15. **"Bridge" style definition**: stair/flat-span meshes + bottom cap meshes + some
    railing — none of the bulk (no side walls). Receiving faces still get full walls
    below the deck entry per the invariant (deck lands ON TOP of the wall).

## Piece library facts (measured truth beats names)
- P_MOD_Stairs_01_E_straight_4 = 1u rise (shallow), _3 = 2u, _2 = 3u, _1 = 4u (steep).
  Suffix is INVERSE to rise — never parse names; always measure.
- COMP_* pieces are pre-composed assemblies (walls/columns); forge prefers the largest
  COMP that fits, falls back to atomic P_MOD parts.
- Walls come in small/medium/large heights -> wall stack composition is change-making
  over measured course heights. Also measure widths (2u entrance gaps need sub-cell
  wall fitting).

## Sequencing
1. **Delete grand-stair systems** (decision 13) — shrinks every later step.
2. **Metrology pass**: measurement tool ingests pack stair pieces, COMP composites,
   wall denominations (heights AND widths), railings, caps into the step library JSON;
   one-time human confirmation per piece.
3. **Quantum recalibration with identity checkpoint** (even-levels-only; batch harness
   must show unchanged acceptance + fixed-seed visual parity). Includes the constants
   audit: every level-delta constant gets its intended meaning re-stated in u.
4. **Railing policy + headroom validator.**
5. **Intra-room 1u splits**: room zones, seam transitions, door thresholds bind to
   zones. Rooms become 1-2 graph nodes.
6. **Stair forge offline ("forge tool")**: generates staircase prefab + contract pairs
   into the existing reviewed pipeline. Grammar: cursor walk over (cell, direction,
   level); segments = flight(1|2|3|4u), landing, turn-90, curve, flat span; style
   tuple (flight family, steepness, railing family, cap family) sampled once per
   staircase.
7. **Online synthesis** once forge output survives batch validation at scale;
   automated gates (port alignment, overlap, headroom, railing coverage, landing
   ledger) become the acceptance bar (refined by decisions 16-21: provisional
   trust + a review queue precede full autonomy).
8. **Aerial bridge connections** (decision 6) in the loop-connection phase.
9. **Dais/multi-level room polish, support columns.**

## Online synthesis (step 7) — decided 2026-06-11

Step 7 changes the acceptance architecture: per-gap synthesized staircases are
accepted by automated gates plus a provisional-trust review queue. Context: at the
end of step 6 the dominant rejection (1124x/100 seeds) was placement-geometry
misfit — kinked or short corridor paths that no fixed contract can fit regardless
of pool size — so library widening has a hard ceiling and the fix is shaping the
staircase to the actual gap.

16. **Trigger — fallback-only, on-path walk.** The reviewed pool (hand-authored +
    forged) is tried first on equal cost terms (decision 11 unchanged); synthesis
    fires only when zero (contract, position) candidates survive placement search
    and ledger pruning for a connection. The synthesized walk follows the actual
    corridor path cells — turning where the path turns, steepness chosen from
    divisors that fit the available run (decision 9 holds). Off-path detours are
    deferred (the cost-search detour pricing exists; placement bookkeeping for
    off-path footprints does not). Synthesis usage therefore stays a meaningful
    diagnostic: it measures exactly where the pool failed.

17. **Asset story — in-memory contracts + direct piece placement.** No
    AssetDatabase or prefab writes during Generate, ever: planning stays
    headless-safe and the 100-seed harness runs the same synthesis code. The
    forge's piece placement is reified as a pure-data plan (source prefab path,
    position, yaw per piece) built by the same code path as the contract; two
    materializers consume it — the offline batch prefab writer (behavior
    unchanged) and direct scene instantiation in the edge model. A synthesized
    contract + piece plan ride the TransitionEdge in memory; the edge model never
    re-reads contract files for synthesized stairs. The round-trip gate splits
    honestly: the real-parser gate runs everywhere; the visual-alignment probe
    (needs GameObjects) runs in-editor only — alignment is exact by construction
    because contract and pieces come from one plan.

18. **Determinism — seed = dungeon seed + gap id.** Per-gap RNG =
    dungeonSeed ^ StableHash("synth:<fromRoom>:<toRoom>:<rise>"), the forge's
    per-request pattern: adding or removing gaps never reshuffles another gap's
    output. The active contract pool remains part of the deterministic input
    (unchanged from today).

19. **Trust posture — provisional, queue-measured.** A dungeon may use synthesized
    output immediately, but every synthesized staircase is appended (contract +
    piece plan + seed/gap id/date) to
    Assets/Arena/Content/Settings/Dungeons/RandomDungeon/synthesized_stair_log.json — that log IS the pending
    review queue; an editor tool rebuilds a review gallery from it on demand. The
    log is written by the editor Generate flow; headless planning only reports
    counts. Synthesized stairs are ephemeral scene artifacts: the forged library
    stays curated and promotion of a synthesized design into it is a manual act.
    Full autonomy — dropping the queue — happens only after the queue stops
    catching anything; that flip is the user's call. (Reminder of why: the curve
    round needed three visual fixes the gates cannot see.)

20. **Gates — the acceptance bar at synthesis time:** real-parser round-trip, port
    alignment by construction, StairPlacementLedger overlap pruning (the same
    filter contract placements pass), 3u headroom, and the reviewed kit's dressing
    rules (walled fill, covers, railing end posts, curve chirality). Stats:
    per-dungeon synthesis usage is reported like seam transitions; the harness
    reports synthesis counts and rejection-histogram deltas.

21. **v1 scope — embedded placements, full kit.** Straight / landing-turn / curve
    segments, walled + bridge styles, single + dual lanes per existing
    eligibility. External-span (bridge-over-gap) synthesis is deferred to step 8
    with the aerial work. First increment (built 2026-06-11): plumbing
    end-to-end with straight designs PLUS landing-turns at every leg split and
    both chiralities — straight-only was measured first and closed zero of the
    1124 misfits (every misfit path lacks a straight run of any pooled length;
    the win is folding at the path's actual corner, which fixed contracts
    cannot offer). Single lane, walled style; curves / bridge style / dual
    lanes activate after the plumbing survives review.

22. **Headless lesson (recorded so it is never relearned):**
    Quaternion.Euler/.eulerAngles are native ECalls, same hazard class as
    JsonUtility — the forge plan builder uses pure cardinal-yaw math
    (StairForge.RotateYaw) because synthesis now runs inside the headless
    100-seed planning harness.

## Walk & dressing rules from in-editor review (2026-06-11, post step-7 increment)

22. **Span decks fly over true gaps only.** An externalSpan footprint cell with a
    floor level is placement-invalid: a deck over walkable interior crosses at
    head height and can cross a room boundary mid-span where its enclosure wall
    stands (observed: an L-bridge descending over a room pierced its partition
    wall). Ports remain the only edges where a span meets a floor. Pass-under
    headroom is validated against the CONTRACT footprint cells (the real deck —
    Manhattan-linear, floored), not the corridor path the bridge replaced.

23. **Doorways are walk openings.** A doorway edge requires both cells to carry
    floors at render time; a doorway whose corridor was replaced by a bridge
    span is stale and closes back up (the partition renders; no railing in the
    gap). An enclosed room whose every doorway went stale demotes to open
    rather than sealing shut or failing the build. Bridge port faces suppress
    the above-floor partition (the deck entry is the entrance); drop-face walls
    below the deck stay per decision 15.

24. **No step strip in a doorway cell.** A seam strip whose lower or raised cell
    is a doorway cell is skipped outright (the perpendicular step lip half-blocked
    a room entrance); the skipped pair keeps its closed 1u face per the ledge
    policy and the seam's other strips carry zone connectivity. Corridor strips
    run parallel to the walk, so they PREFER doorway-free pairs near the corridor
    midpoint but may fall back to the midpoint on short door-to-door corridors
    (steps through an opening are ordinary architecture; rejecting them cost
    7.5x layout attempts).

25. **Ledge railings are covered.** Every top-edge railing gets the pack's
    wall-top trim curb (P_MOD_WallTrim_01_O family, measured as category
    "wallTrim") co-located beneath it, closing the open band under the bottom
    rail — the COMP walls ship this trim built in, which is why partitions read
    finished while bare railings did not. Warn-once + bare until metrology
    ingests the family; corner/angle trim variants are measured but not yet
    placed (railing corner posts still meet bare).

## Stairwells — 180° towers beside the path (decided 2026-06-12)

Context: the remaining placement misfits concentrate at tall rises in tight
corridors; folded stairwells pack rise R into a ~2xN tower but were unplaceable —
embedded placement requires the corridor path to trace the footprint, and
corridors never U-turn. The user asked for 180° stairwells and more curves.

26. **Placement model — tower beside the path, void cells only.** A stairwell's
    entry and exit ports both face the same side, column-aligned in adjacent
    rows (run-balanced legs by construction: flights split as evenly as the
    count allows and flat half-span pads even out the column runs, so every
    rise >= 2 has designs at some steepness — rise may split unevenly across
    the legs; run symmetry is what aligns the ports).
    It anchors on two ADJACENT path cells — lower at fromLevel, upper at
    toLevel, the seam-strip transition pattern — with the folded footprint
    extending perpendicular off the path into TRUE VOID (cells outside the
    floorplan; never floor cells, never leveled). The tower bulges from the
    dungeon silhouette like a turret and is fully self-dressed by the walled
    kit. Ledger + void checks are the placement filters; both sides of the path
    and both chiralities are enumerated.

27. **Trigger — third tier.** Pool contracts first, then on-path synthesis
    (straights/turns/curves), then stairwells only when both produced zero
    placements (decision 5: a tower bolted onto the building is more intrusive
    than an in-corridor stair). Stat stays a clean measure of gaps nothing else
    closes. Determinism: gap RNG seeded dungeonSeed ^ hash("synthwell:<gap>").

28. **Shapes and mechanics.** Switchback (two same-sign turn landings, any
    steepness) and chained-curve 180 (two quarter-turn curves stacked, the
    hand-authored convention; the measured curve steepness only, legs may be
    zero flights — a bare 1x2 tower). Leg pads sit adjacent to the turn column
    and read as a gallery. Contract topology "stairwell", placement class
    "stairwell":
    the face between the two landing path cells stays WALLED (a rise-R cliff —
    unlike embedded stairs the body is beside, not between), ports open via the
    existing set-piece reservation skip, and the walk never stacks vertically
    (legs occupy parallel rows) so the headroom gate is structurally satisfied.
    Provisional trust + queue + gallery exactly as decisions 19-20.

## Aerial bridges — step 8 (decided 2026-06-12)

29. **Aerial loop bridges first.** New loop edges between rooms, created in the
    plan AFTER every corridor connection has leveled its cells: two rooms with
    floor at the SAME level L (L >= 3) and a straight clear line between facing
    boundary cells get a flat deck spanning the gap, bounded per dungeon
    (MaxAerialBridges, default 2; selection uses the shared attempt stream like
    the other loop work). Aerial bridges are TRANSITIONS + port-graph edges,
    never RoomConnections: no corridor leveling and no doorway — the deck lands
    ON TOP of the receiving wall (decision 15: the wall below the entry stays,
    only the railing opens).

30. **Overflight: void + corridors with 3u headroom.** Deck cells may be true
    void, or non-room floor cells whose final level leaves >= 3u clearance —
    validated by the footprint-based deck registration from step 7's headroom
    gate (cells still unleveled at bridge time are permitted and caught by the
    same late gate once filled). Room interiors stay off-limits, which keeps the
    render-time enclosure choice untouched. (Full room overflight would require
    moving ChooseEnclosedRooms into the plan — deliberately deferred.)

31. **Flat decks, equal levels, synthesized like stairs.** Deck designs come
    from the forge grammar (flat-span walk, bridge style; span pairs coalesce
    into whole floors) with the one-plan contract+pieces property, provisional
    trust, the queue and the galleries — decisions 16-21 apply verbatim. The
    contract schema gains topology "deck" with rise 0 and ports at equal levels
    (ports resolve by array order, not by level). Endpoint deltas, gap-shaped
    span synthesis on ordinary connections, and outcropping landing pads
    (decision 10) are the NEXT increments of step 8. Known review item: the
    pack has no flat under-deck cap family measured yet — deck undersides may
    read open from below until the first review round names the right piece.

32. **Bridges are shortcuts, not alternatives (user review 2026-06-12).** The
    function of a bridge is to connect two tiers where no comparable path
    exists; first-increment decks placed as redundant twins of adjacent
    walkways. Two gates: (a) SHORTCUT VALUE — a candidate is rejected when its
    landings are already within walk distance 3 x (span + 2) through the live
    network (BFS over equal-level floor adjacency plus every placed transition,
    including earlier aerial decks, so twin parallel bridges self-exclude);
    (b) NO HUGGING — rejected when a leveled cell within 2u of the deck level
    sits laterally adjacent for half or more of the span (a parallel
    same-length neighbor reads as a duplicate even when poorly connected).

33. **Gap-shaped span synthesis on ordinary connections (2026-06-12).** The
    per-gap synthesis tier (decision 16) also emits BRIDGE-style designs —
    straights, turns and curves with bottom caps instead of walls — and the
    placement search runs them as externalSpan candidates between landings
    (allowExternalSpan on), on equal terms with the embedded designs in the
    same tier. The two fixed pool bridges stop being the only span shapes.
    Span rules unchanged: footprint over true gaps only (decision 22),
    footprint-based deck headroom.

34. **Aerial bridges may absorb small end deltas (2026-06-12).** Decision 29's
    equal-level requirement filtered 92% of candidate lines; aerial pairs may
    now differ by up to 2u. A sloped span is a deck walk of flat pads with the
    rise's flight(s) at the UPPER end (rise 1-2 at the matching steepness,
    standard "straight" bridge contract — no rise-0 special casing). Deck
    headroom registers the conservative MIN landing level for every span cell;
    the shortcut/hug gates (decision 32) apply unchanged; the even-floor
    railing masks stay flat-deck-only (a railing beside a 1u-offset floor sits
    on the slope — the user's stair exception). Level-delta gate: a synthesized
    rise-1 transition is legal (the gate otherwise admits only seam strips at
    delta 1). Outcropping landing pads (decision 10) are DEFERRED by the user
    (2026-06-12) — not part of step 8's close.

## Step 9 — support columns, dais, rounded tiers (decided 2026-06-12)

Order of streams (decision 35): columns -> dais -> round tiers, with the metrology
extension for the round families shipped in the FIRST increment so the measure pass
can run (user, in-editor) while columns and dais are built and reviewed — the
round-tier implementation then starts from measured numbers.

Before design, the pack's gold-standard scene was excavated
(demoscene_dungeon_level_1_dungeon.unity, 4290 placements parsed with world
transforms; includes the user's three hand-built `_claude_step_example` groups).
Measured truth from that scene is binding the way step-2 metrology is:

- **Column modules**: COMP_Column_01_med = 4u, _large = 6u, _small = 2u; stacks
  compose by change-making exactly like wall courses. P_MOD_Column_01_cap tops an
  exposed stack end; P_MOD_Base_01 blocks serve as plinths/cores inside piers.
- **Outcropping piers** (the scene group is named "bridge" but the deck DEAD-ENDS
  at a chest/loot/entrance perch — it is an external outcropping, and the demo has
  NO true room-to-room bridge in our sense; user correction 2026-06-12): column
  stacks rise from the chasm floor in 4u modules and top out exactly at the deck
  underside. Piers stand under the deck's two long EDGE lines (not the
  centerline), beneath floor-piece anchors, in an on/on/off rhythm (pier pairs
  with 12u period); the skipped positions get a small Base block hung 2u under
  the deck as a corbel. Deck sides/undersides are dressed with two bands of
  P_MOD_WallCover_01_M_straight (at deck level and 2u below) — this names the
  piece for step 8's open "deck undersides" review item. The pier/deck recipe is
  generic elevated-deck support and transfers to our spans; the outcropping SHAPE
  itself is the reference for the deferred outcropping feature (decision 10 —
  user wants demo-style outcroppings eventually).
- **Throne dais**: 1u platform; its EDGE is a ring of 1u step pieces (rule 3 holds —
  the whole rim is walkable steps): P_MOD_Stairs_01_E_straight_4 runs,
  E_convex/E_concave round corners and E_angle_concave/convex 45-degree corners,
  each co-located with the matching round floor cap (Floor_01_O_concave_med,
  convex_tiny, angle_tiny...); small/tiny straight floors pad odd rim gaps; large
  columns flank the throne at the dais back corners.
- **Round tier corners at cliff scale**: concave/convex/angle WALL pieces stack
  vertically in ordinary 4u/6u courses at a corner anchor, joining straight runs on
  both sides; matching WallTrim_01_O round variants cap them; pre-composed
  LVL_01_O_rail_* corner chunks crown high gallery edges.
- **LVL_01 chunks are real building blocks**, not just showcase: towers are stacks
  of LVL_01_O_med_angle_FILL_2 (one per 4u), spiral stairs are
  LVL_01_O_stairs_loop_* chunks climbing 3u per quarter-turn around a column
  cluster. Not ingested in step 9; recorded for future tower dressing/stairwells.

35. **Stream order — columns, then dais, then round tiers; metrology first.**
    Columns are one increment and immediately dress the step-8 bridges and decks.
    Metrology gains the round families (round/angle step pieces, round floors) in
    the same increment; round walls/trims/railings already measure under their
    generic categories.

36. **Rounded tiers v1 — render-only corner dressing.** At eligible tier corners
    (no stair, port, seam strip, doorway or railing-post contact), the square
    corner treatment is swapped for the matching round/angle variant: wall courses,
    floor corner cap, wall trim, railing arc — composed per the gold-scene corner
    pattern. Style is rolled once per room and stays consistent within it (the
    guard-rule pattern). Plan footprint and walkability are untouched: pure
    dressing, harness-stable by construction. Stepped round corners (the
    concave/convex FLIGHT pieces as 1u step-edge rounding on seams/dais rims) are
    the second increment, after the first survives review. Planner-aware rounding
    is explicitly out of scope for v1.

37. **Dais v1 — cosmetic interior 1u platform.** An interior raised rect with >= 1
    cell margin from room walls (doorways structurally cannot land on it), carved
    AFTER level assignment — not a graph node, no parity/odd-cycle cost. Height 1u:
    the rim is a walkable step ring per the gold scene (straight 1u strips + round/
    angle corners once measured; bare-faced per the ledge policy until then),
    so the dais is reachable from every open side. Bounded per dungeon; eligible
    rooms only (large enough after zone splits). The dead 2u dais machinery
    (TryReserveDais and its render path — zero callers) is deleted first,
    grand-stairs style. 2u/zone-node dais variants are explicitly deferred.
    Increment-2 implementation notes (2026-06-12): dais carving runs at the END
    of the cell-level field build (after corridors, bridges and the headroom
    gate — it can only decorate, never reject); flat dungeons are skipped so the
    single-level gate keeps its meaning. Per-room RNG = dungeonSeed ^
    StableHash("dais:<roomIndex>") — the forge per-request pattern, so other
    features never reshuffle a dais. Eligibility: UNSPLIT rooms with an interior
    after a 1-cell margin; dais rect 1-2 cells per axis; body avoids path cells,
    doorway cells and every ledger footprint/landing. The rim takes a rise-1
    strip on every eligible ring cell (placement class "dais" — same prefab,
    geometry and reservations as "seam"; distinct for histograms); at least one
    strip must survive or the dais is skipped. Constants: DaisChancePerRoom
    0.25, MaxDaisPerDungeon 2, MaxDaisSpanCells 2. The dead-code deletion
    removed ~950 lines (PlaceFloorMask legacy path included — it was the dais
    render host and had zero callers). Harness: 37/100 seeds carry a dais,
    acceptance/archetypes/synthesis/rejections byte-identical to the step-8
    baseline, double-run identical; tier spans now reach L+1 where a dais lands
    on a top floor (expected). Pipeline reports "seeds with dais platforms" +
    DAIS-SEED lines.

38. **Support columns — floor seams + ends, always when legal.** Under every
    externalSpan deck (aerial decks, bridge-style spans, pool bridges): column
    stacks beneath the deck's floor-piece anchor lines at both deck edges, at both
    span ends and at piece seams, whenever the landing cell is true void/unleveled
    ground (decision 12: never block walkable cells; cells that fail the rule are
    skipped — gaps read as variation). Stack = change-making over measured column
    modules, top flush with the deck underside, P_MOD_Column_01_cap on exposed
    tops, plinth Base block at ground. Deterministic — no RNG draw. The gold
    scene's on/on/off pier rhythm with corbels at skipped seams is recorded as a
    future flourish, not v1.
    Increment-1 implementation notes (2026-06-12): pier corners derive from the
    instantiated FLAT deck floor slabs of the synthesized piece plan (pitch-0
    P_MOD_Floor_01_O_straight pieces; flights and flipped caps contribute none),
    deduped on a half-unit grid so adjacent slabs share seam piers; the two
    frozen POOL bridge prefabs carry no piece plan and are not columned in v1.
    Corner legality checks all four touching cells against levels + reservations.
    Stacks reuse the stairwell base-fill change-making loop (top-anchored,
    bottom course may sink below ground); denominations = measured COMP_Column
    heights (small/med/large; brazier excluded, one piece per height). Metrology
    categories added for the later increments: "tierStepEdge" (E_concave/
    E_convex/E_angle_* risers) and "floorRound" (round floor caps), both with
    side-plane areas as the orientation signal.

## Contours — the unified step-edge model (decided 2026-06-12, after dais review)

User review of the first generated dais: bare corner notches between perpendicular
rim strips (user demonstrated the fix in-scene with E_angle_convex_4); sunken dais
and steeper raised/sunken features are wanted; elaborate/tiered dais (the throne
look) "need a real synthesizer"; and the gold scene's ROUNDED room elevation change
shares its contours with the throne dais. Measured suffix decode (tierStepEdge):
_1=4u, _2=3u, _3=2u all full-cell 4x4; _4=1u QUARTER-CELL 2x2 (the corner notch
between two strips); _5=1u full-cell (the throne corner, pairs with floorRound caps).

39. **One contour synthesizer.** A contour is the cell-boundary path of a raised
    or sunken region. One synthesizer walks it and emits straight strips + corner
    pieces chosen by CONVEXITY (outside corner = convex/angle_convex, inside =
    concave/angle_concave) + steepness family chosen by RISE, with the one-plan
    contract+pieces property and the provisional-trust queue (decisions 16-21
    verbatim). Dais rims, sunken pits, tiered dais and contoured zone seams are
    consumers of this one grammar — the gold scene's rounded room edge matches
    the throne dais because the pack authors used the same grammar.

40. **Order: notch fix -> dais forge -> contoured seams.** (a) Render-side
    corner-notch dressing on existing dais rims (the _4 quarter-cell pieces —
    the user's manual fix automated); (b) the dais forge: synthesized elaborate
    dais through the queue; (c) contoured zone seams — a planning change giving
    raised zones meandering boundaries dressed by the same grammar.

41. **Dais forge v1 scope.** Designs: 1-2 tiers, raised AND sunken variants,
    rises 1-2u per tier, full-cell round/angle corners with floorRound caps
    (the throne construction), optional flanking columns. Dais PURPOSES
    (throne/altar/loot) are recorded as a design tag for later prop work, not
    implemented.
    Increment-3 implementation notes (2026-06-12, gallery-first like step 6):
    StairForge.SynthesizeDaisDesigns emits 12 v1 designs (angle|round x raised
    2x2 r1 / 3x3 r1 2-tier / 2x2 r2, sunken 2x2 r1 / 3x2 r1 / 2x2 r2);
    EmitDaisTier is the contour-dresser seed (rect boundary walk; raised =
    convex corners + ring strips climbing inward; sunken = concave corners +
    in-pit strips + pit floors; corner cells = full-cell step piece + floorRound
    cap co-located, throne-style). Menu "Synthesis Review: Build Dais Design
    Gallery" lays raised rows then sunken with a gallery-only context floor
    ring. Headless probe: SmokeHarness 'dais' mode dumps all designs (12/12
    synthesize, deterministic). GEOMETRY CONVENTIONS AWAITING GALLERY
    CALIBRATION: full-cell curve corner assumed at local (-4,0) (one-table fix
    if rotated); concave family measures ~0.25u short of nominal rise (seat
    offset decision); 1x1 top tiers render as a single corner piece (may need
    forbidding); flanking columns on the top tier's front corner cells.
    Generation still uses the v1 strip-rim dais — dungeon integration (carve
    draws a forged design, plan rides to render, queue log) follows the
    gallery round.

42. **Corner style rolls per room.** Angle (chamfer) or round (quarter-circle),
    one style per room, consistent across its dais/seam corners — the guard-rule
    pattern. Both appear in the pack's own scenes.

    Dais close-out notes (2026-06-12, after five gallery rounds + integration):
    the gallery IS the review instrument — in-dungeon dais compose only
    gallery-approved constructions, so no per-dungeon queue log is kept (the
    implementation reading of decision 41's trust posture; same standing as
    seam strips). Calibrated grammar: dressing occupies the LOWER side of the
    edge; corner scale matches strip protrusion (rise 1 = quarter-cell _4
    notch at the corner vertex, rise 2 = full-cell _3 sweep in the diagonal
    ring cell, both pivot ON the vertex); sunken pits put the concave _5/_3
    full-cell sweep + med round cap ON the pit corner cell, replacing that
    cell's strips (transitions remain for walls/connectivity); raised corners
    take no floor cap. In-dungeon variants draw per-room: sunken 0.25 (needs
    room level >= rise and a clean 2x2+), steep 0.25 (rise 2, E_straight_3
    strips, diagonal sweep cells ledger-registered), tiered 0.3 (raised
    rise-1 with a 3x3 path-free interior — rare by construction). The render
    derives all dais dressing from dais-class transitions + cell levels; the
    standard floor renderer provides tier tops and pit floors for free.

## Ledge policy amendment (decision 43, user 2026-06-12, clarified;
## IMPLEMENTED 2026-06-13)

43. **Room-scoped 1u rules.**
    (a) A 1u drop WITHIN one room always gets step strips — never an
    unclimbable bare ledge inside a room (zone seams and dais rims already
    comply; any other intra-room delta-1 adjacency joins them). Doorway
    (rule 24) and stair footprint/landing exclusions remain the walled
    fallback.
    (b) A 1u drop that DIVIDES two rooms gets a WALL, not a railing and not
    steps — inter-room movement stays with doorways and corridors.
    (c) On a room's OUTER edge, any side that traverses a 1u drop is covered
    with WALLS, not railings, along that side (a 1u floor step alongside a
    walled side is acceptable). Rationale (MEASURED): the pack has no
    half-length angled railing and no sloped/stepped railing transition
    piece, so a railing line cannot step 1u mid-run. This activates the
    per-room wall-guard machinery deferred since step 5; guard kind stays
    consistent per side/room (decisions 4 + 42).
    **[SUPERSEDED-PENDING: decision D, Phase 2 — re-scope, not delete]** —
    43(a)/(b) stand. 43(c)'s "railings cannot step 1u" rationale remains
    measured fact, but under D a guard exists only where the edge faces void
    or a ≥4u drop; internal smaller drops get NO guard (no parapet, no
    railing). The guard-side machinery survives with D's edge classification
    deciding WHERE it runs; the stepped-side wall-over-railing choice decides
    WHAT it places. Most 1u-stepped outer sides disappear anyway once
    decision A makes inter-room deltas 4u majors.

    Implementation (2026-06-13, design round: full band dais-class sweep;
    small-wall parapet — user notes the parapet piece is INTERIM, to
    revisit when dungeons grow wall varieties):
    (a) SweepIntraRoom1uDrops runs at the very end of the level-field
    build: every intra-room delta-1 adjacency without a transition takes a
    dais-class strip (full band — the corner machinery dresses turns);
    doorway/reservation faces stay the walled fallback. FINDING: the sweep
    places ZERO strips across both 100-seed ranges — zone seams already
    strip every seam pair and dais rims are closed bands, so 43(a) was
    already satisfied; the sweep is the standing guarantee as features
    evolve. Mixed-class corner note: sweep strips are dais-class while
    seam strips are seam-class, so a corner between the two families would
    not pair — no instance can occur today (the sweep places nothing).
    (b) Inter-room delta-1 edges keep their walls: the sweep never crosses
    rooms, and the render already walls those adjacencies.
    (c) FindWallGuardSides: a guard side is (roomId, face direction); a
    side is 1u-crossed when two cliff edges sit at adjacent positions with
    upper floors one level apart. Such sides place PlaceParapetEdge
    (P_MOD_Wall_01_E_straight_small, double-sided, centered on the edge
    line like the railing it replaces, per-segment at each segment's
    floor) on every non-suppressed cliff edge of the side — 1u drops
    included ("a 1u floor step alongside a walled side is acceptable") —
    and railing corner posts are excluded there. Headless probe (WallProbe
    drives the real detection): 3-7 guarded sides per dungeon, keying on
    zone-split rooms whose seams meet the outer boundary and on backed
    dais sides. 100/100 both ranges, deterministic.

## Backed dais — wall-flush throne variant (decision 44, user 2026-06-12)

44. **A raised dais may stand flush against one room wall (the BACK).**
    The 1-cell margin drops on that side only; rim strips and the approved
    corner constructions dress the three exposed sides; the back side emits
    NO strips (cells beyond the room boundary belong to other features — a
    corridor hugging the far side of the wall must never receive a dais
    strip). Backed proportions bias wide-along-wall: up to 3 cells along the
    wall, 1-2 cells deep (contoured shapes may run wider). Rise 1 and 2 both
    allowed (rise-2 keeps its full-cell front sweeps; back corners need
    none — the wall terminates the rim). Sunken and tiered stay
    interior-only. Frequency: a raised non-tiered dais tries backed 50% of
    the time, falling back to interior placement when no wall side is
    eligible (back span free of doorways, paths and ledger cells); the
    backed draw is APPENDED after the existing per-room draws so every
    current dais stays byte-identical. Review route: gallery-first — backed
    rows join the dais gallery with a gallery-only context wall behind
    (straight wall courses; never part of the design's piece plan), then
    dungeon integration reviewed on the usual seeds.

    Gallery round 1 (user 2026-06-12): flanking columns SCRAPPED for now
    (focus the dais; the gold placement — COMP_Column_01_large_2 on the dais
    top at the back corners, 1u inset from side edge + 1u out from the wall
    plane, demo example x=-35/-25 z=-47 vs wall z=-48 — stays recorded here
    for revival); the context wall faced backwards (fixed: emit from the
    back row's north faces, not the far side's south faces); and the user
    expected ELABORATE/CONTOURED backed dais, not just plain rects. Round 2
    adds contoured shapes through a cell-set contour walker (decision 39's
    dresser, first non-rect tiers): per style, a lobed 3x2 (full-cell _5
    convex corner + tiny floorRound cap co-located at the vertex pivot — the
    gold throne / user-sandbox lobe), a 4x3 center-bay front and a 4x3
    winged front (protrusions take lobes; junction returns take the quarter
    _4 concave + tiny cap in the outside cell's vertex quadrant — the user's
    hand-built return construction; concave yaw = vertex-landing map, same
    landing the approved sunken corners resolve to; calibrate on review).

    Gallery round 2 (user 2026-06-12): corner pieces still wrong in the
    contoured designs; user directed reproduction of "the two
    contoured/walled dais" in the gold scene = their two hand-built
    demoscene level-1 backed platforms. DECODED and reproduced VERBATIM
    (piece tables GoldBackedBayPieces / GoldBackedScallopPieces in
    StairForge, scene world coords kept and rotated 180 into the
    wall-at-north gallery frame; designs dais_gold_backed_bay /
    dais_gold_backed_scallop in their own gallery row):
    (1) BACKED BAY (wall z=-48, top y=-2): 3-cell platform + full-cell
    center bay; bay shoulders are HALF-CELL tiny pads; lobes = E_convex_5 +
    convex_tiny co-located, pivot ON the convex vertex, footprint into the
    lower quadrant — the quadrant→yaw map MATCHES the synthesized lobe
    table; returns = E_angle_concave_4 + angle_tiny, pivot ON the concave
    vertex, footprint into the outside quadrant — MATCHES the synthesized
    landing map; flank entry strips run along the wall, guarded by sloped
    stair railings + ground posts, WallTrim_L/R_4 where they die into the
    wall; large_2 columns on the platform (1u from wall) AND on the ground
    at the bay front corners; LVL convex FILL half chunk at the bay's east
    foot. The bay FRONT edge is bare — a stage lip; access is the flank
    strips + shoulder steps.
    (2) BACKED SCALLOP (wall z=-32, ledge top y=5): 1-cell-deep
    column-plinth ledge; front = E_convex_4 quarter notch at the west end,
    two side-by-side E_concave_5 + O_concave_med scallops, half-floor pads
    between; columns stand ON the ledge (large on the scallop shoulders,
    med on the pads 1u off the wall); Railing_4_2 terminates the east end;
    LVL FILL half chunk on the floor at the foot.
    Synthesizer corrections from the decode: concave returns are ALWAYS
    the ANGLE quarter + angle tiny cap (the sandbox pairs them with round
    lobes; round E_concave_4's authored sweep is unverified and read as a
    serpentine in round 2). Both corner yaw TABLES were already correct —
    what differs is construction grammar: the gold contoured sections use
    sub-cell pads and carry NO strips beside the returns, and bay lips can
    be bare. That gap is the round-3 review question (gold rows vs
    synthesized rows, side by side).

    Gallery round 3 (user 2026-06-12): gold reproductions edited — columns
    removed from both; the scallop's east Railing_4_2 removed and the dais
    made SYMMETRIC (east end mirrored with a second E_convex_4 notch at
    scene (4,5,-32) yaw 90; the asymmetric LVL foot chunk dropped with it —
    the bay reproduction keeps its LVL chunk). Corner diagnosis: the
    round-2 walker placed lobes at OUTER (1-mass) vertices where the disc
    only point-touches the platform; gold lobes only ever wrap a half-cell
    pad at JUNCTION (3-mass) vertices, fusing with the mass along a full
    edge, and protrusion lips stay bare. The synthesized contoured designs
    were rebuilt as parametric gold-grammar constructions — EmitGoldShoulder
    emits the shoulder ensemble (straight_tiny pad hugging the protrusion
    edge, lobe disc continuing it outboard with the _5 sweep wrapping, the
    angle concave return stepping down the pad's outer half) at gold's
    exact relative offsets; bay = gold platform 1 minus furniture (3-cell
    mass, 1-cell center bay, flank entry strips along the wall, shoulders
    on both bay sides, bare bay lip), wings = the inverse plan (5-cell
    mass, 1-cell end wings with strip-dressed outer sides and bare lips,
    3-cell recess whose shoulders meet exactly at the recess center). The
    round-2 vertex walker and the lobed-rect design are deleted. VERIFIED:
    the synth bay's shoulder pieces land byte-identical to the gold
    reproduction's corresponding pieces. 24 designs.

## The band model (decision 45, user 2026-06-12)

45. **A dais rim is one continuous step band, verified by machine — never
    eyeballed constructions.** User correction after four gallery rounds of
    corner artifacts: "the stairs of a dais should not end abruptly in a 1u
    ledge, and should contour smoothly" — the misunderstanding was treating
    the rim as independent decorations (a strip here, a corner there)
    instead of a closed ribbon of steps tracing the contour, where every
    piece's lip continues its neighbor's and the band terminates only into
    a wall or by descending fully to ground. The gold scallop is the
    archetype: the whole ledge IS band (notches rounding the ends to
    ground, scallop bites, pad tops) with no plain slab at all.
    Tooling shipped with the decision (the fix for "you are guessing"):
    (a) RasterizeDaisDesign — top-down ASCII of every design from MEASURED
    piece bounds (1 char = 1u), printed by the harness dais probe; chopped
    noses and colliding pieces are visible headlessly before any editor
    review. (b) ValidateDaisBand — the machine form of the rule, run by the
    probe on every synthesized design (gold reproductions exempt/
    informational): (1) raised step pieces never overlap; (2) every strip
    END abuts band, pad, or wall; (3) every raised SLAB boundary
    sub-segment (1u granularity) faces band coverage or the back wall line.
    Pads and floorRound caps classify as band tops — their sculpted edges
    are legal (the scallop ledge); only plain slabs must be banded.
    Applied immediately: the synthesized bay closes its nose band (front
    strip + approved _4 notches wrapping into the shoulder returns —
    validator-green, raster-verified against the gold frame); the wings
    design is WITHDRAWN until a silhouette is approved and passes the band
    invariants; the gold bay reproduction's bare nose is flagged
    informational (verbatim truth — its lip was furniture-dressed in the
    scene). Gallery: 22 designs. Future contoured features (tiered noses,
    contoured seams 40c) must pass the same invariants before review.

    Gallery round 5 (user 2026-06-12): the gold scallop's nose was still
    chopped — its pad front carried med columns in the scene, and the
    initial validator EXEMPTED pad edges ("sculpted ledges"), so it passed
    silently. Rule falsified and tightened: ALL raised tops (slabs and
    pads) hold to the banded-edge rule — furniture never excuses a bare
    edge; the only pad exemption is a floorRound cap co-located with its
    step piece (the curved descent lives inside the shared footprint,
    invisible to AABB probes). Both gold reproductions got band
    completions, marked as such in their tables (not scene pieces): the
    scallop's pad front takes a strip; the bay nose takes the same strip +
    round _4 notch closure as the synthesized bay, and the scene's LVL
    convex FILL chunk is dropped (it was the furniture-era east-foot
    corner dressing and collides with the notch that replaces its role).
    All 22 designs now pass the band invariants with ZERO findings.

    Gallery round 6 (user 2026-06-12): the scallop's new front strip had
    TWO OPEN CUT ENDS — missing corner pieces the validator waved through.
    Two validator bugs found and fixed: (a) the TouchesBand predicate
    accepted zero-overlap axis contact (sum-of-overlaps heuristic), so a
    strip's end probe "touched" the strip's own stamp along its length —
    now requires true area overlap in both axes; (b) strip width-axis was
    inferred from AABB proportions, which flips for the steep E_straight_3
    (run 4.115u exceeds its 4u width — measured), so r2 designs probed
    climb ends instead of side flanks — stamps now carry placement yaw and
    the width axis derives from it (strip width is always local z). With
    both fixes the validator isolated exactly the user's finding (two open
    flanks at the scallop strip) and nothing else; _4 notches close them
    (the same strip+notch closure as the bay nose). ZERO findings again —
    now with teeth.

    Gallery round 7 (user 2026-06-12, "this churn was disappointing"): the
    scallop's END treatment had been mis-read since round 3 — the scene's
    LVL convex FILL half chunk IS the full east wing (a wide 2-step
    ground-level fan terminating the ledge), and the west E_convex_4 notch
    is the TRUNCATED left wing; round 3 did the exact inverse (dropped the
    chunk, mirrored the notch). The user renamed their reference group to
    `_claude_step_example_gold` to settle it. The reproduction now matches
    the reference verbatim plus the completed west wing (chunk pivot
    mirrored about composition center x=-2, yaw 0→90 per the pack quadrant
    convention — the single non-scene placement, calibrate on review) and
    keeps the rounds-5/6 pad-front strip+notch closure. LESSON: when a
    reference composition is asymmetric, ask WHICH side is canonical
    before "completing" it — the answer was the opposite of my guess.

    Gallery round 8 (user 2026-06-12; second chunk "randomly rotated",
    "all you had to do was make the nose longer"): PREFAB DECODE settled
    the LVL chunk — FILL_5_2_half is a HALF-DISC FAN (two E_convex_5
    quarters + convex_med_2_half cap co-located at one pivot; the full
    FILL_5_2 is four quarters + full cap), and in the reference it sits
    CENTERED at the composition's symmetry line as the NOSE — it was never
    an end wing; LVL chunks are prefab COMPOSITIONS of P_MOD pieces and
    can be decoded by parsing their YAML children. User design round
    (AskUserQuestion with sketches): nose protrudes one cell further (4u
    floored extension with strip flanks and _4 corner notches, fan at the
    tip — 8u total protrusion) and the ledge ends take E_convex_4 notches
    on both sides (west is scene-authored, east mirrors). The fan is
    emitted DECOMPOSED into its measured children so the raster and band
    invariants see it (the chunk itself is unmeasured); the dead LVL
    literal-path loader branch is removed. 17 pieces, zero band findings,
    raster confirms the full silhouette.

    Gallery round 9 (user 2026-06-12): the round-8 neck was too wide (8u)
    and its corner notches superfluous. Final form: the neck is ONE CELL
    wide — the pad bay width, consistent with the rest of the dais — with
    a single med floor and E_straight_4 flanks, and the half-disc fan tip
    overhangs the neck 2u per side; the fan arcs and flank strips
    terminate each other with no corner pieces. 14 pieces, zero findings.

## Backed dais integration (decision 46, user 2026-06-12)

46. **Phased integration; showpieces wherever they fit.** Increment 1 =
    plain backed rects through the carve: the backed draws (roll, side,
    along-span, deep-span, offset) are APPENDED after every existing
    per-room draw, so all current dais stay byte-identical; a raised
    non-tiered dais tries the four wall sides starting from a rolled one
    (flush placement, 1-cell margin on the perpendicular walls, up to 3
    cells along the wall, depth bounded as usual), and falls back to its
    already-drawn interior rect when no side carves. The back face emits
    no strips (cells beyond the boundary may belong to a corridor on the
    wall's far side); rise-2 sweeps exist only at the front corners. The
    render needed NO changes — strips and corner notches derive from
    transitions as before, and the back corners simply have no
    perpendicular pairs. Increment 2 (pending) = the bay and scallop as
    verbatim set-piece dais (the synthesized-stair set-piece pattern:
    carve footprint cells/levels/reservations, instantiate the approved
    design piece list at the wall anchor); when a room is large enough
    for a showpiece it ALWAYS gets one (rolled bay|scallop), plain rects
    serve smaller rooms.
    Increment-1 verification (2026-06-12): 100/100 both seed ranges,
    deterministic; rejections/archetypes/seams/synthesis byte-identical
    to baseline; tier spans shift slightly (more dais on top floors =
    documented L+1). Plans verified: backed dais flush at the wall with
    exactly three strip sides. NOTED FOR REVIEW: dais frequency jumped
    26->66 per 100 seeds with backed dominating (59) — wall spans rarely
    collide with corridor paths, so backed placements succeed where
    interior rects failed, and four sides are tried. If too common,
    tuning knobs: DaisChancePerRoom, DaisBackedChance, or trying only
    the rolled side. The harness prints BACKED-DAIS-SEED lines
    (stairCandidateSummary carries "backedDais:N").

    Sunken-dais carve fix found during integration review (user scene,
    2026-06-12): a generated 2x2 pit rendered with only THREE corner
    sweeps — the plan's rim had a single strip at one pit cell (its other
    ring face was blocked by a stair reservation), so the render's corner
    detector (which requires two perpendicular faces) correctly refused
    it and the bowl broke. Latent since the sunken increment ("at least
    one strip survives" was the only gate). Fix is decision 45's band
    model applied planning-side: a sunken pit carves only when its rim is
    CLOSED — every exposed face takes a strip — so every corner cell
    always has both faces and the sweep ring is complete. Counts
    unchanged on both harness ranges (the review pits were already fully
    rimmed).

    Universal closed-band rule (user 2026-06-13: "don't show a dais at
    all if there is no space for it"): a second in-scene defect — a 2x1
    BACKED dais with a bare front face (its ring cell sat at another
    tier's level, so the strip was skipped; the dais read as jammed
    against the wall) — generalized the fix: the closed-band carve
    requirement now applies to ALL dais, raised and sunken; the
    suppressed back face of a backed dais is wall-terminated and exempt
    by construction. Dais that cannot complete their rim do not carve.
    Counts: 66->61 and 58->53 per 100 across the two harness ranges;
    100/100, deterministic.

    Increment-2 implementation (2026-06-13): showpieces are pure SET
    PIECES — no cellLevels change, no transitions; the covered 5x3 cells
    are ledger-reserved and the approved gallery piece list (bay by
    rolled angle|round style, or the gold scallop) instantiates verbatim
    under one root carrying the wall anchor and side yaw (0/90/180/270),
    so the side rotation is free via the root transform and the design
    geometry is correct by construction. The room floor correctly
    continues underneath (gold-scene convention), and the sculpted tops
    never receive standard cell floors. Fit: 5 along x 3 deep at uniform
    level, clear of paths/doorways/reservations, margin 1 from
    perpendicular walls, and VOID behind all five wall cells (true outer
    wall backdrop). Draws (kind, style) appended after the increment-1
    draws; the showpiece pass runs before the plain backed pass.
    StairForge.TryGetBackedShowpieceDesign serves cached designs;
    summaries carry "showpiece:<design>@<originCell>". Verified: 100/100
    both ranges, deterministic; 3 + 7 showpieces per 100 seeds (both
    kinds appear; the strict fit makes them naturally rare). NOTE: a
    seed whose only dais is a showpiece carries no dais-class
    transitions, so DAIS-SEED lines undercount slightly — the showpiece
    token is the signal.

## Open items (deliberately deferred)
- **Game design pass (committed 2026-06-12, opens after this plan closes):**
  features must serve gameplay purpose, not just dynamicity — dais purposes
  (chest, throne, well-in-pit), room purposes (empty, trap, pacing), and
  purpose-driven prop placement. Until then, prefer generation features that
  can later carry a purpose tag; avoid pure decoration.
- Doorway/gateway prefabs at elevated openings (using plain 2u gaps for now).
- Mid-staircase lane merges; branching staircases (T-junctions) — not in v1.
- Sub-cell floorplans (small/tiny floor pieces beyond seam/landing trim).
- Exact flourish probability and spectacle scoring — tune after first forge batches.
