# Server Tick Baseline Recipe

How to record the server-tick §2 baseline numbers with dummies, using only
what ships in the repo: the local SpacetimeDB publish script, the in-game
**PLAYGROUND** panel, and the projectile-load-harness overlay. There is no
training-ground scene — everything below runs in the normal open world.

## 1. Publish with the profiler baked in

```bash
ARENA_PROFILE_TICKS=1 ./ops/republish-local-clear.sh
```

For NPC-pack tests where you must stand inside aggro range (NPCs never target
dummies), also bake `ARENA_NPC_HARMLESS=1` — it zeroes NPC attack damage at
the template level while keeping aggro, chase, cadence, and swing events real:

```bash
ARENA_NPC_HARMLESS=1 ARENA_PROFILE_TICKS=1 ./ops/republish-local-clear.sh
```

The module logs a warning at first NPC-template access when it's active.
Local measurement builds only — never deploy with it.

The gate must be set at **build** time: the module targets
`wasm32-unknown-unknown`, where process env vars do not exist at runtime
(`std::env::var_os` is always `None`), so a runtime env var can never enable
the profiler inside the module. `option_env!` bakes the flag into the wasm at
compile time (see `server/src/tick_metrics.rs`).

Notes on the script defaults:

- `--delete-data=always` — this **clears the local database**.
- `ARENA_PROJECTILE_LOAD_HARNESS=1` (default) builds with the harness feature,
  which the `=` overlay needs.
- `ARENA_GENERATE_BINDINGS=0` skips the bindings regen if they are current.
- If the final dotnet verify fails with "type name does not exist" for newly
  generated bindings, the IDE-generated `Assembly-CSharp.csproj` file list is
  stale — focus the Unity editor so it regenerates project files, or set
  `ARENA_VERIFY_DOTNET=0`.

## 2. Watch the logs

```bash
spacetime logs -f arena
```

With profiling baked in you should see at init:

```
[INIT] Tick profiling enabled via compile-time ARENA_PROFILE_TICKS (wasm module). ...
```

Then, per profile window:

- One `[TICK_PROFILE_SCAN]` line — the counter window: `ticks` (see the
  caveat below), `status_collects`, `status_rows_scanned`, `equipment_scans`,
  `move_fallbacks`, `writes_<table>` (physics, intent, resource, charge state,
  NPC combat runtime, stacking passives), and populations sampled at flush
  (`alive_players`, `dummies`, `alive_npcs`, `active_statuses`,
  `active_casts`, `active_projectiles`).
- One sampled tick timed with host console timers ("Timing span" lines):
  `tick_profile/total`, `tick_profile/pre_tick`, `tick_profile/player_sim`,
  `tick_profile/post_tick`, `tick_profile/match`, plus one line per pre-tick
  sub-phase (progression sync, movement actions, active casts, NPC combat,
  combat cycle, …).

The `[TICK_PROFILE]` / `[TICK_PROFILE_PRE]` percentile lines only appear in
native builds (unit tests) — `Instant` does not exist on wasm, so in the
module the sampled console timers above are the wall-clock source.

**Read counters as per-tick ratios, not per-5s totals.** SpacetimeDB runs the
module in a pool of wasm instances, and each instance carries its own copy of
the profiler's static state. Ticks are distributed across instances, so each
scan line covers only the ticks that ran on that instance (its window still
spans ≥5s of its own samples), and lines from different instances interleave —
you may see a scan line every ~2-3 s instead of every 5 s, and more often
(with smaller, uneven `ticks=` counts) as load grows and the host activates
more instances. Counters and tick samples for any given tick always land on
the same instance, so `counter / ticks` is exact. Divide everything by the
line's `ticks=` value.
`status_collect_ms` is always `0.00` in the module (no `Instant` on wasm);
use the sampled `tick_profile/*` lines for wall-clock.

## 3. Spawn the load (PLAYGROUND panel)

Connect a client (editor play mode), then use the **PLAYGROUND** button
(top-right HUD):

| Button | What it spawns | Cap |
|---|---|---|
| PLAYER HOSTILE / PLAYER NEUTRAL | stationary dummy player-actor (`is_dummy`) | 1 each |
| PLAYER FRIENDLY | dummy party member | party cap (4 besides you) |
| KOBOLD HOSTILE / NEUTRAL / FRIENDLY | NPC (`alive_npcs`) | repeatable — click N times |
| CLEAR | despawns all playground targets and NPCs | — |

So the maximum **dummy** population from the panel is 6 (1 hostile +
1 neutral + 4 friendly); scale the NPC axis with repeated kobold spawns, and
the projectile axis with the `=` projectile-load-harness overlay
(`RunProjectileLoadHarness`; needs the harness-feature publish from step 1).
Add combat pressure by attacking the targets (moves `status_collects`,
`active_statuses`, `active_casts`).

## 4. Record the baseline

Capture a steady-state minute per configuration and paste representative
`[TICK_PROFILE_SCAN]` + `tick_profile/*` lines into
`docs/server-tick-compute-audit-2026-07-02.md` (Measurement status section).
Scale **one axis at a time** — dummies, NPCs, projectiles — to confirm the
predicted scaling shapes before optimizing anything.

Sanity expectations, as per-tick ratios (`counter / ticks`):

- `writes_player_physics` ≈ 1 × connected players. Settled dummies add ~0
  (the T3 write gate); before the gate each dummy added 1/tick.
- `status_collects` ≈ 2/tick since the T1 view threading (view A + view B,
  independent of player count) + per-cast/per-action extras while acting.
  Before T1 it was 6 × alive players + 2 globals.
- `equipment_scans` ≈ 1 × alive players since the T2 memo (the per-tick
  `PlayerTickContexts` build) + event-driven extras during combat. Before T2
  it was 5 × alive players.
- `writes_fixed_action_charge_state` ≈ 0 at rest since the T3 slice-2 gate
  (writes only on dodge consume, active recharge ticks, and the first sync
  after a reset). Before the gate it was 1 × (players + dummies) per tick —
  the dominant write family in the 2026-07-02 baseline.
- `move_fallbacks` near 0 on localhost with a responsive client. It rises when
  the client stops delivering command rows on time — including when the editor
  is unfocused/paused or the client frame rate collapses under load, not just
  under packet loss.
