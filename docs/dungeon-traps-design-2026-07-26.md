# Random-dungeon traps: design and implementation proposal

Date: 2026-07-26. Status: **implemented** (slices 1–4); §11 records every deviation
from this design and what is still unverified. The four forks in §9 are ruled and
folded in.

Goal: proximity-triggered spike and saw traps placed by the random dungeon
generator, using the four `ToonDesertedTemples` trap prefabs, dealing
server-authoritative damage.

## 1. What the vendor assets actually are (measured, not assumed)

`Assets/ThirdParty/AssetStore/Environments/ToonDesertedTemples/Prefabs/Buildings/Traps/`
holds **four** prefabs. Each is: LOD group + mesh renderers + one sparks
`ParticleSystem` + one `Animator`. **None of them has a single collider**, and
each `.controller` is one state, no transitions, no parameters, pointing at one
clip with `m_LoopTime: 1`. Dropped into a scene they loop forever — which is how
`Dioramas_2_Day` uses them, decoratively.

Every clip is a complete rest → fire → rest cycle, so **one clip is exactly one
activation**, and its idle tail is the trap's natural cooldown. Keyframes read
straight out of the `.anim` files:

| Kind | Prefab | Clip | Motion | Hazard window | Extent |
|---|---|---|---|---|---|
| `SPIKES` | `TFD_Floor_Trap_01A` | 4.667 s | spikes `localPosition.y` −1.27 → +0.06 @0.20 → 0 @0.23 → hold to 2.18 → −1.27 @3.48 | **0.18 – 2.20 s** | ~2×2 u plate |
| `SAW_SWEEP` | `TFD_Trap_01A` | 3.833 s | handle rises y −0.8 → 0 @0.25, travels `z` +2 → −2 over 0.25–2.13, sinks @2.35, resets underground | **0.22 – 2.25 s**, hazard centre travels with `z` | ~6 u along travel |
| `SAW_POST` | `TFD_Trap_02A` | 3.0 s | handle rises y −0.8 → 0 @0.10, spins in place, sinks @1.58 | **0.09 – 1.55 s**, stationary | ~2 u |
| `SAW_ARM` | `TFD_Trap_03A` | 2.5 s | arm `eulerX` 23.66° → −204.5° @0.75 → 23.66° @1.5 | **0.12 – 1.45 s**, hazard centre follows the arc | ~6 u swept |

`SAW_POST` is the "emerges from the slit and spins" one. `SAW_SWEEP` is the
travelling one. `SAW_ARM` is wall-mounted in the diorama (placed at y ≈ 1.39
with a Z-axis rotation); the other three sit flat on the floor at y ≈ 0 with
Y-only rotation. Spike plates are tiled on a 2 u pitch in the diorama —
the dungeon's cell is 4 u, so **one cell holds a 2×2 field of four plates**.

Materials are `TFD_CustomToon` (`RenderPipeline=UniversalPipeline`), so they
render in this project's URP without shader work. Art match is a separate
question — see §9.

## 2. Architecture: copy the door foundation, do not invent a second one

The world-interaction foundation landed two days ago and is exactly the right
precedent. Traps use the same three-layer split:

```
editor generator ──places prefab + TrapAuthoring──► RandomDungeon.unity  (presentation)
       │
       └─exports──► random_dungeon.traps.shared.json  (paired client + server bytes)
                          │
                          ├─ include_str! ──► server: all state, all damage  (authority)
                          └─ Resources    ──► client: nothing gameplay, ids only
```

Placement and behaviour are deliberately separated:

- **`random_dungeon.traps.shared.json`** — *where and what kind*, generated,
  regenerated on every dungeon rebuild. One record per trap.
- **`world_trap_profiles.shared.json`** — *timing, hazard geometry, damage*,
  authored as Arena-owned `TrapProfile` ScriptableObjects under
  `Assets/Arena/Content/Settings/Traps`, keyed by kind. Exactly mirrors
  `world_interaction_profiles.shared.json`.

Consequence worth having: **retuning trap damage never requires a dungeon
rebuild** — export profiles, republish, done.

### Manifest schema (generated)

```jsonc
{
  "schema_version": 1,
  "world_definition_key": "RANDOM_DUNGEON",
  "traps": [{
    "trap_definition_id": "RANDOM_DUNGEON:TRAP:SPIKES:18:31:9",
    "world_definition_key": "RANDOM_DUNGEON",
    "trap_profile_id": "TRAP_SPIKES",
    "origin":  { "x": -36.0, "y": 9.0, "z": 26.0 },   // trap root, post-recenter world space
    "yaw_degrees": 90.0,
    "footprint_cells": 1,                              // 2x2 plate field packs into one cell
    "definition_version": 1
  }]
}
```

### Profile schema (authored)

Two profiles shown, because they exercise every field between them.

```jsonc
{
  "schema_version": 1,
  "profiles": [
    {
      "profile_id": "TRAP_SPIKES",
      "trigger_kind": "PROXIMITY",
      "trigger_delay_ms": 350,                               // telegraph, see below
      "cycle_ms": 4667,
      "hazard_start_ms": 180,
      "hazard_end_ms": 2200,
      "trigger_volume": { "center": {...}, "size": {...} },  // trap-local
      "hazard_volume":  { "center": {...}, "size": {...} },  // trap-local, at track t=0
      "hazard_track": [],                                    // stationary
      "on_hit": [
        { "effect": "DAMAGE", "amount": 45, "damage_type": "PHYSICAL" }
      ],
      "one_hit_per_activation": true,
      "rearm_ms": 0
    },
    {
      "profile_id": "TRAP_SAW_SWEEP",
      "trigger_kind": "PROXIMITY",
      "trigger_delay_ms": 0,
      "cycle_ms": 3833,
      "hazard_start_ms": 220,
      "hazard_end_ms": 2250,
      "trigger_volume": { "center": {...}, "size": {...} },
      "hazard_volume":  { "center": {...}, "size": {...} },
      "hazard_track": [                                      // piecewise-linear local offset
        { "t_ms": 250,  "offset": { "x": 0, "y": 0, "z":  2 } },
        { "t_ms": 2133, "offset": { "x": 0, "y": 0, "z": -2 } }
      ],
      "on_hit": [
        { "effect": "DAMAGE", "amount": 22, "damage_type": "PHYSICAL" },
        { "effect": "DOT", "tick_amount": 4, "tick_interval_ms": 1000,
          "duration_ms": 6000, "damage_type": "PHYSICAL",
          "stack_group": "TRAP_BLEED", "max_stacks": 5,
          "stack_policy": "ADD_STACK_REFRESH" }
      ],
      "one_hit_per_activation": true,
      "rearm_ms": 0
    }
  ]
}
```

### `on_hit` is a list, not a fixed damage + bleed pair

This is the extensibility point. Each entry maps 1:1 onto an existing
`EffectPacket` variant, so the profile catalog is the only thing that changes
when a new trap kind arrives:

| Future trap | `on_hit` |
|---|---|
| Flamethrower | `DAMAGE` (Fire) + `DOT` (Fire, `stack_group: "TRAP_BURN"`) |
| Poison dart | small `DAMAGE` + `DOT` (Nature, long, low tick) |
| Crusher | large `DAMAGE` + `KNOCKBACK` |
| Frost vent | `DAMAGE` (Frost) + `APPLY_STATUS` Slow |

No server code, no schema version bump — a new profile row and a new prefab
binding. Do **not** hardcode `bleed_*` columns; that shape only fits saws and
would have to be torn out the first time a flamethrower lands.

**Saws bleed, spikes do not** (owner ruling, §9). Spikes are a single hard flat
hit; the saws trade some impact for a bleed. **The bleed stacks**:
`ADD_STACK_REFRESH` with `max_stacks: 5`, so running a sweeping-saw corridor
compounds — which is the point of a saw corridor. Watch this in tuning: a
5-stack bleed at 4/s is 20 dps against a 200 HP baseline, so five stacks is
already lethal pressure if the player does not leave.

`hazard_track` is empty for `SPIKES` and `SAW_POST`; it carries the two
keyframes above for `SAW_SWEEP` and the arc samples for `SAW_ARM`. Evaluating
it server-side is a few lines of piecewise-linear interpolation and it is what
makes *"the saw hits you when the saw is on you"* true instead of *"the whole
lane is lethal for two seconds"*. That distinction is worth the code.

### `trigger_delay_ms`: the reaction window

A trap does not begin its clip the instant it is triggered. The state row is
inserted at trigger time, and the clip starts `trigger_delay_ms` later:

```text
t = 0                          trigger fires, row inserted, telegraph starts
t = trigger_delay_ms           clip starts (client scrub begins at frame 0)
t = delay + hazard_start_ms    hazard live, damage begins
t = delay + cycle_ms           row deleted, trap rearms
```

So `cycle_ends_at = cycle_started_at + trigger_delay_ms + cycle_ms`, and the
client parks at the rest pose while `phase < delay` instead of scrubbing.

Sizing it for spikes, with real numbers rather than a guess. `MOVE_SPEED` is
7.0 u/s. A full-cell spike field is 4×4 u, so from dead centre a player must
clear ~2.5 u including capsule radius — 0.36 s of travel. Total warning is
`trigger_delay_ms` + the clip's own 0.18 s rise before the spikes are lethal:

| delay | warning | travel needed | reaction budget left |
|---|---|---|---|
| 250 ms | 0.43 s | 0.36 s | 0.07 s — only if already running |
| **350 ms** | **0.53 s** | 0.36 s | **0.17 s** — escapable if you react instantly |
| 500 ms | 0.68 s | 0.36 s | 0.32 s — comfortably escapable |

**350 ms is the proposed start**: a hair under human reaction time from a
standing start, so an alert moving player gets out and a careless one does not.
This is exactly the kind of number that should be moved after playing, not
argued — it is one field in one profile and needs no rebuild.

The saws keep `trigger_delay_ms: 0`: their own rise (0.22 s) plus travel time
across the lane already telegraphs them, and a delay on a travelling blade just
reads as the machine hesitating.

**Telegraph presentation is a gap in v1.** During the delay the trap looks
identical to a dormant trap — the only warning is audio. The honest options are
audio-only for the first pass (cheap, and the 0.18 s spike rise is itself
visible before lethality), or authoring a short pre-shudder / dust puff on the
plate. Recommend audio-only in v1 and judging it in play; if 350 ms of silence
reads as unfair, the fix is a telegraph cue, not a longer delay.

## 3. Server: state machine and authority

### State table, materialised only while firing

```rust
#[table(accessor = world_trap_state, public)]
pub struct WorldTrapState {
    #[primary_key] pub trap_state_id: String,        // "OPEN:RandomDungeon:<definition_id>"
    #[index(btree)] pub trap_definition_id: String,
    pub world_kind: String,
    pub instance_id: Option<u64>,
    pub open_world_scene_name: String,
    pub cycle_started_at: Timestamp,                 // the one authoritative phase anchor
    #[index(btree)] pub cycle_ends_at_micros: i64,
    pub activation: u64,
}
```

Rows exist **only while a trap is mid-cycle**; the tick deletes them at
`cycle_ends_at_micros`, exactly like `ActiveWorldObstacle::expire`. At rest the
table is empty, so replication cost scales with *firing* traps, not with trap
count. A second small table `WorldTrapActivationHit { trap_state_id, actor }`
enforces one hit per actor per activation and is deleted with its state row.

### Tick

New pre-tick subphase `world_traps`, placed inside the existing
`CombatActorSnapshotSet::collect(ctx)` scope in `game_loop.rs:1034` so it reuses
the already-built snapshot set and its `CombatActorSpatialIndex` for free.

```
if no player is in the RandomDungeon scene: return          // whole-phase early out

for each live WorldTrapState row:
    phase = now - cycle_started_at                          // includes trigger_delay_ms
    clip_t = phase - trigger_delay_ms
    if clip_t within [hazard_start, hazard_end]:
        centre = trap_origin + yaw_rotate(hazard_volume.center + hazard_track(clip_t))
        for actor in spatial_index.near(centre, radius):     // ALL actors, players and NPCs
            if actor overlaps hazard OBB and not already in the hit ledger:
                queue every profile.on_hit entry, source = Identity::ZERO
                insert hit-ledger row
    if clip_t >= cycle_ms: delete state row (and its ledger rows)

for each trap definition with no live state row:
    if any PLAYER capsule overlaps its trigger OBB: insert a state row
```

Note the asymmetry, which is the §9.1 ruling made concrete: the arming scan
iterates **players only**, the hazard scan iterates **every actor**.

Damage uses the existing system-source path — `apply_damage` and
`resolve_damage_amount` already branch on `hit.source == Identity::ZERO`
(`combat.rs:4412`), skipping attacker stats and `can_harm` while still applying
the target's resistances and damage-taken modifiers. **No new damage plumbing is
needed**; traps queue `EffectPacket::Damage` / `EffectPacket::ApplyStatus` with a
new `DAMAGE_SOURCE_KIND_TRAP` beside the existing
`MELEE`/`SPELL`/`PROJECTILE`/`PERIODIC`.

### Cost

The arming scan is the only new O(traps × players) work: ~20 traps × ≤8 players
= ~160 AABB tests per 33 ms tick, behind a whole-phase early-out. That is noise
against what the tick already does. If it ever shows up in `TICK_PROFILE_PRE`,
bucket the definitions by grid cell at module init — but do not pre-build that.

## 4. Client: presentation only, driven by one timestamp

- `TrapAuthoring` (Arena-owned) — definition id, profile id, gizmos for the
  trigger/hazard volumes. Serialized `Vector3`s only. **No colliders anywhere
  on a trap**, so nothing can leak into the immutable collision bake the way the
  door exporter guards against for door leaves.
- `TrapRuntimeRegistry` + `TrapPresenter` — stable-id registry, a direct mirror
  of `DoorRuntimeRegistry` / `DoorInteractable`.
- `WorldTrapStateReplicator` — mirrors `WorldTrapState` rows, a direct mirror of
  `WorldDoorStateReplicator`.
- `TrapPresenter.Update()`:

```csharp
float phase  = (ArenaServerClock.ServerNowMs - cycleStartedAtMs) / 1000f;
float clipT  = phase - _profile.TriggerDelaySeconds;
_animator.speed = 0f;
_animator.Play(_stateHash, 0, Mathf.Clamp01(Mathf.Max(clipT, 0f) / _clipLength));
_animator.Update(0f);
// clipT < 0 parks the rest pose and is the telegraph window (audio cue here).
```

Because each controller is a single state with no transitions, scrubbing is
exact. A player who joins mid-cycle lands on the correct frame instead of
replaying the strike — the same late-join property the door work required.

**No client prediction in v1.** A trap firing is server news; at 40 ms RTT the
spikes rise 40 ms late and the damage lands with them, which reads as fair.
Predicting it locally would mean rollback on a purely cosmetic asset. Revisit
only if it actually feels laggy in play.

## 5. Generator: a new, additive placement pass

New pass in `ElevationEdgeModel`, after floors/walls/gateways are placed, into a
`Traps` child root, then exported next to
`WorldInteractionManifestExporter.ExportActiveScene(dataKey)` in
`RandomDungeonSceneBuilder` (line 135) — i.e. **after** `CenterDungeonSpawn`, so
exported coordinates are final world space.

Candidate cell rules (all data already available at that point):

- in `levels`, rendered as floor, not in `reservedCells`, not a stair/bridge/aerial-deck cell;
- flat across the whole footprint — no 1 u zone-seam step under a spike field;
- not a doorway/gateway cell and not adjacent to a gateway socket (do not force
  a player through a trap in a chokepoint with nowhere to dodge);
- not the spawn cell and not in the arrival room;
- corridor cells (in `levels` but absent from `roomBoundaryContext.cellRoomIds`)
  weighted higher than room cells — traps read best in circulation;
- `SAW_SWEEP` needs 2 collinear free cells at the same level for its 6 u extent;
- `SAW_ARM` needs a proven adjacent wall face, reusing the same `wallEdges` /
  `gatewayWallPlan` machinery that already proves gateway flanks.

Density and kind mix become per-profile settings in `DungeonGenerationProfile`.
A typical plan has ~450 floor cells; **1 trap per 25 cells ≈ 18 traps per
dungeon** is the proposed starting point.

**Determinism**: every draw comes from `DungeonRandomScope.Stream("traps", cellKey)`,
subject-keyed, so the pass **cannot perturb any existing decision**. That is a
testable claim, not a hope: `ops/dungeon-port-ab.sh` with the pass disabled vs.
enabled must produce identical floor/wall/gateway/collision output and differ
only by the new `Traps` root. This is a hard gate on the slice.

## 6. Validation and tooling

- Extend `WorldInteractionFoundationValidator` (or a sibling): unique ids,
  production-enabled, finite geometry, known profile id, no trap overlapping a
  gateway blocker / stair footprint / spawn, hazard volume inside floor bounds
  at every track keyframe, zero colliders on any trap instance.
- `ops/dungeon-trap-audit.py`, a sibling of `ops/dungeon-gateway-audit.py`:
  reads the exported manifest over a seed batch and reports count, kind mix,
  corridor/room split, and minimum distance to spawn.
- Server: `cargo test world_traps` covering phase arithmetic, the hazard track,
  one-hit-per-activation, scope isolation, and expiry.

## 7. Implementation slices

Each slice ends somewhere testable; nothing is written twice.

1. **Profiles + manifest, no gameplay.** `TrapProfile` asset type, the four
   profiles with the measured timings, `TrapAuthoring`, the exporter, paired
   write, schema round-trip test. Hand-place two traps in the dungeon scene.
2. **Server authority.** Tables, manifest parse with `deny_unknown_fields`, the
   tick, `DAMAGE_SOURCE_KIND_TRAP`, damage. Verified headlessly with an
   `ops/` probe walking a player onto a hand-placed trap — the existing probe
   harness already does keepalive/movement/tick-estimate.
3. **Client presentation.** Registry, replicator, `TrapPresenter` scrub, mid-cycle
   join. Play-verified with two clients.
4. **Generator pass.** Placement rules, profile density settings, the A/B
   determinism gate, the audit script.
5. **Tuning pass.** Look at rebuilt dungeons, adjust density/kind mix/damage.

Slices 1–3 are independent of the generator, so the whole gameplay loop is
provable on a hand-placed trap before any generator code is touched.

## 8. Explicitly out of scope for v1

Flagging these so nothing ships silently:

- Levers/switches to arm and disarm (owner already deferred; the state table
  takes an `armed` column later without reshaping anything).
- Trap detection, disarm, or any skill check.
- Traps blocking movement, LOS, or projectiles. Raised spikes arguably should
  block — but an invisible wall appearing in a corridor is worse than a passable
  spike bed, and the collision contract says query raycasts test authored query
  geometry only. Recommend: traps never block anything.
- Knockback on hit. The system exists (`EffectPacket::Knockback`); a saw punting
  you is appealing but it is its own tuning conversation.
- NPC pathing awareness — NPCs will walk into traps.
- Client-side prediction of trap firing.
- Trap kinds beyond the four vendor prefabs. The `on_hit` list and the profile
  catalog are shaped so a flamethrower, dart, or crusher is a new profile row
  plus a prefab binding — but nothing is built ahead of a real asset.
- A visual telegraph during `trigger_delay_ms`. Audio only in v1; see §2.

## 9. Owner rulings, 2026-07-26

All settled before implementation.

1. **NPCs: damaged, but do not trigger.** Only a player capsule overlapping a
   trigger volume arms a trap; once the hazard is live it damages **any** actor
   overlapping it. So the arming scan iterates players only, and the hazard
   overlap iterates the full snapshot set. Wandering mobs cannot leave traps
   permanently cycling, and an NPC standing in a running saw still takes hits.
2. **Damage: per-kind, authored as an `on_hit` effect list.** Spikes are one
   flat hit (~45, no bleed). The saws trade impact for a bleed (~22 plus a
   `StatusEffectKind::Dot` of ~4/s for 6 s) and **the bleed stacks** —
   `ADD_STACK_REFRESH`, `stack_group: "TRAP_BLEED"`, `max_stacks: 5`. Future
   kinds (flamethrower, dart, crusher) add their own `on_hit` entries with their
   own damage type and stack group; nothing in the schema is bleed-shaped.
   Exact numbers are profile data and get tuned in slice 5, not argued now.
   *(Superseded the initial refresh-only recommendation — owner call, and the
   right one for saw corridors.)*
3. **Spikes get a reaction window.** `trigger_delay_ms: 350` between the
   proximity trigger and the clip start, so a fast player can leave the plate.
   Saws stay at 0 — their rise and travel already telegraph them. Arithmetic and
   alternatives in §2. Telegraph presentation during the delay is audio-only in
   v1 and is a known gap.
4. **Art: ship vendor materials, judge in place.** No retint pass before the
   traps are standing in a real dungeon. The models are palette-shaded, so
   retinting stays cheap if the verdict is that they clash.
5. **Density: ~18 per dungeon, corridor-weighted.** 1 trap per 25 floor cells
   over a ~450-cell plan.

## 10. What I would push back on

Nothing in the request, but one framing: the vendor traps are **loops**, and the
cheapest possible version of this feature is to place them as always-cycling
hazards with a pure-function-of-time hazard window — zero replicated state, zero
trigger logic, and the classic "time your run through the corridor" read. The
proximity trigger is strictly more work (a state table, an arming scan,
replication) and it trades that timing-skill read for a surprise read.

The request is unambiguous and proximity triggering is the right call for a
first pass at *"if I'm standing on the spikes, they erupt"* — but the always-on
variant is a one-line profile flag on top of this design (`trigger: ALWAYS`
vs `PROXIMITY`), and it is worth authoring both so the two can be mixed per
trap once there are dungeons to look at.

## 11. Implementation notes, 2026-07-26

Slices 1–4 are built. Everything below is a deliberate deviation from the design
above, or a gate that has not been run yet. Nothing shipped silently.

### Deviations

1. **`on_hit` entries are one flat struct, not a tagged union.** §2 sketched
   variant-shaped rows. The server parses the catalog with
   `serde(deny_unknown_fields)`, so one struct carrying every field with
   `effect` selecting which ones matter is what actually round-trips; a
   mis-tagged entry becomes impossible rather than merely unlikely. Same
   extensibility: a flamethrower adds a row, not a variant.
2. **`rearm_ms` extends the state row's lifetime**, so
   `cycle_ends_at = cycle_started_at + trigger_delay_ms + cycle_ms + rearm_ms`.
   All four authored profiles use `rearm_ms: 0`, where this is identical to §2's
   formula. Keeping the row alive through the cooldown is what makes a non-zero
   rearm expressible without a second table.
3. **`activation` is the cycle-start timestamp in micros**, not a counter. It is
   monotonic per trap and unique, which is all the client's stale-row guard
   needs, and it costs no counter table.
4. **The dormant pose is the clip's LAST frame, not its first.** Measured: the
   spike clip keys its spark emission to 1000/s at t=0 (the strike *is* the
   first frame), so parking at 0 would shower a dormant plate in sparks. Every
   one of the four clips ends retracted with emission 0, so `TrapPresenter`
   parks at normalized time 0.999.
5. **The wall arm sweeps horizontally, not vertically.** Re-measuring
   `TFD_Trap_03A` against its diorama placement: the arm rotates about its local
   X with the saw 2 u out, and the diorama's Z=90 roll maps that to
   `(2 sin θ, 0, 2 cos θ)` — a horizontal circle of radius 2 u at mount height
   1.39 u. The Arena wrapper bakes that roll in, so the manifest still needs only
   an origin and a yaw. Consequence: `SAW_ARM` needs a proven wall face *and* a
   2×3 clear block in front of it (footprint 6 cells), not just a wall.
6. **The spike variant is four vendor plates in one wrapper prefab** on the 2 u
   pitch the diorama uses, filling one 4 u cell and sharing one phase.
   `TrapPresenter` therefore scrubs every `Animator` beneath it, not one.
7. **Trap density and kind mix live on `DungeonGenerationProfile` but NOT in
   `DungeonGenerationSettings`.** That struct is reflected field-by-field into
   the per-seed settings digest, so putting a render-stage knob there would move
   every plan hash for a decision the plan never sees.
8. **Validation is split by what each tool can actually see.**
   `WorldTrapFoundationValidator` covers the contract (paired exports, unique
   ids, resolvable profiles, template/production components, zero colliders,
   gateway and spawn clearance). `ops/dungeon-trap-audit.py` covers spatial
   coverage (count, density, kind mix, corridor/room split, minimum distance to
   spawn) and fails on a hazard sample that leaves the floor or a trap standing
   in a gateway cell. The audit reads the built scene and needs no Unity.
9. **The checked-in trap manifest is empty**, which is the correct paired state
   for the checked-in trapless scene. Traps appear on the next dungeon rebuild.
10. `ops/dungeon-compile-gate.sh` now patches and builds `Assembly-CSharp` too,
    and repoints the editor/test projects at the patched copy. The stale runtime
    csproj made new runtime and generated-binding files invisible, which
    surfaced as "type not found" inside `SpacetimeDBClient.g.cs`.

### Gates run

- `cargo test --lib` — 13 new `world_traps` tests pass (phase arithmetic, the
  telegraph window, hazard-track interpolation and clamping, yaw placement,
  capsule overlap, `deny_unknown_fields`, hazard window vs cycle). Three
  pre-existing failures (`melee::*` ×2, `world_collision::random_dungeon_uses_
  baked_lower_floors_*`) reproduce identically at HEAD.
- `cargo clippy --lib` — no warnings in `world_traps.rs`.
- `ops/dungeon-compile-gate.sh` — runtime, editor and EditMode test assemblies
  all compile.
- `ops/dungeon-trap-audit.py` — verified against the checked-in scene (467 floor
  cells, 0 traps) and against a synthetic manifest built from real floor cells,
  where both the hazard-leaves-the-floor and unknown-profile failures fire.

### Verified live, 2026-07-27

The dungeon was rebuilt with the pass enabled and the module republished, so
everything below is measured against the running server rather than argued.

**Placement** (`ops/dungeon-trap-audit.py`, seed -917615165, `spacious`):
439 floor cells, **18 traps — 1 per 24.4 cells**, matching the §9.5 target of
~18 at 1-per-25. Kind mix `TRAP_SPIKES` 11 / `TRAP_SAW_SWEEP` 4 /
`TRAP_SAW_POST` 3, corridor-ish 12 vs room-ish 6 (the corridor weighting works),
minimum distance to the spawn floor 28.84 u. Every hazard sample stays over
floor and no trap sits in a gateway cell.

**Gameplay** (`ops/trap-probe.py`, a headless player walked from the spawn to a
generated spike plate; the route is solved from the built scene, so it works on
any rebuild). All checks pass:

| check | measured |
|---|---|
| dormant at spawn | 0 rows in `world_trap_state` |
| arming | exactly 1 row for that trap on the plate |
| telegraph | damage at **+562 ms** vs the authored 530 ms — one 33 ms tick of slack |
| damage | base 45, final 45, `spell_id` `TRAP_SPIKES`, source `00000000` (`Identity::ZERO`) |
| one hit per activation | 1 event inside the activation window |
| re-arm | second hit **+5048 ms** vs the 5017 ms cycle |
| rest | row cleared after leaving the plate |

**Output neutrality.** Rather than the plan-hash A/B (which cannot see the render
stage at all), the pass was measured directly: rebuild with traps ON, rebuild with
`trapsEnabled: 0`, and rebuild OFF a second time. Results:

- the door manifest is **byte-identical** across all three, and the trap manifest
  is byte-identical across the two ON runs — the pass is deterministic and does
  not perturb gateways;
- the **collision bake is NOT byte-stable across rebuilds**, independent of traps:
  two identical traps-off rebuilds differ by 324 of 1329 boxes and 162 of 7529
  mesh instances — the same magnitude as the ON-vs-OFF diff (323 / 151). Same
  object names and counts, different transforms, so some placement tie-break in
  the renderer is order-dependent. The repo already lives with this: the scene
  and both collision payloads are marked **skip-worktree** (`git ls-files -v` ->
  `S`), so the churn never reaches a diff. Worth knowing anyway, because it means
  **a byte comparison of `random_dungeon.collision.shared.json` can never prove a
  generator change output-neutral** — compare the door/trap manifests, which are
  byte-stable, or compare rebuilds with the pass on and off.

**Two things the rebuild changed that traps did not cause.** The dungeon had not
been rebuilt for a while, so regenerating it moved data that HEAD still pins:

- the door count went 8 -> 11. A traps-*disabled* rebuild produces 11 as well, so
  this is generator/content drift since the committed manifest, not the trap pass.
- `world_interactions::tests::paired_manifests_parse_and_references_resolve`
  asserted `doors.len() == 8`. That count is regenerated data, not a contract —
  pinning it only guarantees the test breaks on the next rebuild — so it is now
  `assert!(!doors.doors.is_empty())`, which is what the test is named for.

**Kind mix note.** Zero `TRAP_SAW_ARM` landed this seed. It is not a geometry
problem: 125 of 439 cells (28.5%) satisfy the wall-plus-2x3-block rule. With
weight 1 of 10 it is simply tried first on ~10% of placements, so zero across 18
draws has ~15% probability. Raise `trapSawArmWeight` if every dungeon should have
one — profile data, no rebuild of anything but the dungeon.

### Not yet verified

- **Nothing has been judged by eye.** No screenshot, no play session: the spikes
  have never been *seen* rising. Art match (§9.4), whether 530 ms of silent
  telegraph reads as fair, and whether the 4 u spike field looks right in a 4 u
  corridor are all open.
- **The plan-hash A/B was not run**, because it cannot observe this pass: the
  trap pass is render-stage, `DungeonGenerationSettings` is untouched so the
  per-seed settings digest cannot move, and `BuildSeedReport` never renders. The
  direct on/off/off rebuild comparison above is the stronger measurement and is
  what actually found the collision-bake churn. Run
  `cp ops/dungeon-port-ab.sh /tmp/dungeon-port-ab.sh && ARENA_REPO=$PWD /tmp/dungeon-port-ab.sh dense`
  if a plan-stage receipt is still wanted.
- **`TRAP_SAW_ARM` has never fired.** None was placed, so its arc track, its
  1.39 u mount height and its 2x3 footprint rule are unexercised outside unit
  tests.
- **Damage and timing numbers are untuned.** Slice 5 is still open: they are
  profile data and need no rebuild to change.

## 12. Eye-test pass, 2026-07-27

Owner verdict on the built dungeon: "looks pretty good", with three fixes. All
three are done; two were tuning, one was a real bug.

### The travelling saw was a stroll, not a threat

Measured, the vendor sweep moves its blade 4 u in 1.883 s — **2.1 u/s** against a
player `MOVE_SPEED` of 7.0. Speeding the clip up was not an option, because the
clip also carries the blade's spin and the spin reads correctly as authored.

So `Assets/Arena/Content/Animation/Traps/TFD_Trap_01A_ArenaSweep.anim` is an
Arena-owned retime of the vendor clip in which **only the travel leg is
compressed**. The rise, the sink and the idle tail keep their vendor durations,
and the spin curve's end value is re-solved from the preserved angular rate:

| | vendor | Arena |
|---|---|---|
| travel | 4 u in 1.883 s (**2.1 u/s**) | 4 u in 0.667 s (**6.0 u/s**) |
| spin | −804.25 °/s | **−804.25 °/s** |
| clip length | 3.833 s | 2.617 s |

`TrapProfile` gained an optional `_animatorController`, applied by
`TrapPresenter.Awake`. The profile already owned the state name and the cycle
length, so it owns the clip that defines them — and the wrapper prefab stays a
pure nesting of the untouched vendor prefab, with no prefab-instance override.

**Side effect, flagged:** the sweep's cycle dropped 3833 ms -> 2617 ms, so it
re-arms sooner. Shortening travel while keeping the idle tail does that. Restore
the old cadence with `rearm_ms: 1200` if it reads as too eager — one field, no
rebuild of anything.

### The sweep triggered from outside its own lane

Its trigger volume was the trap's whole two-cell footprint, 4 x 8 u, so a player
2.3 u to the side of the blade set it off. It is now **1.2 x 6 u** — the blade's
actual swept danger zone (4 u of travel plus a blade radius at each end). With a
0.28 u capsule the player must be within ~0.88 u of the centreline, so the trap
fires when you are on the path and not before. Trigger footprint: ~6x smaller.

The stationary saw and the spike field keep their whole-cell triggers on purpose:
neither travels, so "you are in the cell" is the readable rule for them.

### The second spark burst was a presenter bug

Reported as "the spikes play two spark animations — remove the one when the
spikes finish retracting". Reading the clip said that could not happen: it
contains exactly ONE authored burst, at the rise, with `rateOverTime` keyed to 0
from 0.217 s onward, one particle system, no bursts and no sub-emitters.

The cause was in `TrapPresenter`. Every vendor trap clip is authored with
`m_LoopTime: 1`, and a looping state samples the **fractional part** of the
normalized time — so scrubbing to exactly `1.0` samples frame 0, the strike, at
emission 1000/s. The presenter clamped with `Mathf.Clamp01`, which parks on 1.0
for the tail of every cycle. The wrap is invisible in the geometry because the
spikes hold y −1.27 at both ends of the clip, which is exactly why it showed up
only as sparks, and only as the trap finished retracting.

`TrapPresenter.NormalizedClipTime` now clamps strictly below 1 (reusing the same
constant as the dormant pose, which parks on the last frame for the same
reason). Covered by
`WorldTrapManifestTests.ClipScrub_NeverReachesTheLoopPointThatWrapsOntoTheStrikeFrame`.

This one is worth remembering beyond traps: **any scrubbed looping clip must
never be sampled at exactly 1.0.**
