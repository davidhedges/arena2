# Latency Testing Recipe (feel audit F2c)

Reproducible latency/jitter/loss against the **local** SpacetimeDB endpoint
(`ws://localhost:3000`, module `arena` — `NetworkEnvironmentConfig.LocalServerUri`)
on macOS, using the built-in dummynet traffic shaper (`dnctl`) driven by pf
(`pfctl`). Everything here is host-side and port-scoped: no Unity, SDK, or
server changes, and nothing outside port 3000 is affected.

Read the results in the netcode debug overlay (backslash in any arena runtime
scene): RTT last/p50/p95 and clock offset (F2b `ping_clock` sampler), remote
hard snaps, interp/extrap sample ratio, remote position error, predicted
results by kind, per-table row rates. Server-side `MOVE_FALLBACK` counts are in
the `[TICK_PROFILE_SCAN]` window line (compile-time `ARENA_PROFILE_TICKS`; see
`docs/tick-baseline-recipe.md`).

## How it works

- `dnctl` configures numbered **pipes**, each applying a one-way delay and a
  packet loss rate (`plr`).
- pf **dummynet rules** route matching packets through those pipes. The stock
  macOS `/etc/pf.conf` already declares `dummynet-anchor "com.apple/*"`, so we
  load our rules into a sub-anchor (`com.apple/arena-latency`) instead of
  replacing the main ruleset.
- SpacetimeDB speaks WebSocket over a single TCP stream, so `plr` loss shows up
  as **retransmit stalls** (bursty row delivery after a pause), not silently
  dropped rows. That is the realistic failure mode — watch for extrapolation
  ratio and hard snaps rising during stalls.
- Delay is per direction. Total added RTT = client→server pipe + server→client
  pipe.
- dummynet has no native jitter parameter. We approximate it bimodally: a
  `probability` rule sends ~30 % of packets through a second, slower pipe.

## Shaped-local movement is representative (S5, 2026-07-04)

The 2026-07-03 caveat that lived here — "local-player movement fidelity
cannot be validated against a shaped localhost endpoint" — is dead. It
described the open-loop pipeline: an endpoint-kind lead switch (2 ticks on
`Local`/`Custom`, 8 on `Remote`) plus an arrival-anchored tick estimate,
which spent its whole budget at ~30–40 ms added one-way delay and
rubberbanded continuously. S5 replaced that with a closed loop
(`InputLeadController`: the server publishes per-tick consume truth on the
ack surface, the client steers buffer occupancy to a 1–2 command setpoint)
and a precise-clock tick estimate, identical on every endpoint kind. Shaping
localhost now exercises exactly the machinery real remote play uses — full
up+down shaping is the intended acceptance harness, not an artifact.

What to expect while shaped (watch the overlay's `Input lead (S5)` lines and
`Logs/remote-presentation-ab.csv` `s5_*` columns, summarized by
`ops/analyze-s5-input-loop.py`):

- After a lead step (session start, RTT change), a brief burst of fallback
  acks while the loop raises the lead — then fallback returns to rare-event.
  Steady-state fallback (`s5_fb_acks` climbing every second) is a bug again,
  not a harness artifact.
- Lead converges to ~2 ticks on unshaped loopback and to roughly
  upstream-delay-in-ticks + 1–2 under shaping, and comes back down (slowly,
  by design — one tick per 5 s of sustained surplus) after shaping is torn
  down.
- Jumps never vanish: `s5_jump_lost` stays 0 (a near-miss jump slides one
  tick server-side — `[MOVE_JUMP_SLIDE]` in module logs).
- Reconcile corrections read as a capped 0.5 m/s drift or one honest snap
  (`s5_corr_snaps`), never continuous elastic yanking.
- Buffer occupancy (`s5_occ`) holds near the 1–2 setpoint, and `s5_acks`
  accumulates at ≈ 30/s. The server runs a fixed-RATE tick chain (measured
  33.0 ms; the pre-fix Interval scheduling ran fixed-delay at a real
  36.6 ms/tick, which pinned occupancy at the prediction bound no matter
  what the lead did — acceptance run 1, 2026-07-04). A session-wide ack
  rate near 27/s or occupancy stuck ≥ 5 means cadence drift is back; check
  `[GAME_LOOP]` re-anchor warnings in module logs.
- Run one play-mode session per leg (baseline, shaped, post-teardown): the
  analyzer summarizes per session, and a blended session hides the legs.

Downstream-only shaping (`sudo dnctl pipe 2 config delay 40ms` with only the
`from any port 3000` pf rule) remains useful when a check wants row-delivery
delay while leaving the input path clean — e.g. the F4 A/B protocol, whose
jitter branch and CSV leg-comparison notes are unchanged: add pipe 4 at
65 ms with the `probability 30%` rule before the pipe 2 rule, and compare
legs as counter deltas in `Logs/remote-presentation-ab.csv`. Under
downstream-only shaping the overlay RTT reads roughly the downstream delay
alone (the upstream leg is unshaped), so dot-threshold checks must put the
full threshold into the one direction — e.g. `pipe 2 config delay 180ms`,
stationary, for the Bad (≥180 ms p50) check.

The F4 A/B run itself is automated end to end (S7): `ops/s7-lap-probe.py`
joins the live `arena` database as a headless player, spawns a hostile
kobold, and runs full-speed laps on a collision-data-verified circuit in
Desert_Day so the kobold chases IT continuously (settled% stays ~0 with no
hand-kiting); the observing Unity client runs the scripted leg driver in
`NetcodeDebugOverlay` — period key while the overlay is visible starts a
60 s OFF warmup followed by ON/OFF/ON/OFF 80 s legs (or set
`ARENA_S7_AB_AUTORUN=1` to have it self-start once a precise clock sample
and an NPC have been present ~10 s, which is what the batchmode runner
`Arena.EditorTools.S7HeadlessAbRunner` uses). Score with
`ops/analyze-remote-presentation-ab.py` — it prints per-leg late ratio,
err p95, settled%, paid delay, the S7 adaptive-budget columns, and the S7
gate verdict. Requires the measurement republish
(`ARENA_NPC_NO_ATTACK=1 ARENA_NPC_AGGRO_RADIUS=100
./ops/republish-local-clear.sh`).

## S8 lag compensation (attacker-view rewind) evidence

Server truth is fully automated: `ops/s8-lag-comp-probe.py` (recipe in its
docstring — two throwaway publishes: `ARENA_NPC_NO_ATTACK=1` for the rewind
legs, `ARENA_NPC_HARMLESS=1` for the defense-grace leg, both with
`ARENA_NPC_AGGRO_RADIUS=100`). It prints PASS/FAIL per check: config
defaults, honest-report no-op, the 16-slot history ring, the verdict flip
(present-pose reject vs attacker-view accept on a kobold crossing the
charge minimum ring), the rewind-barrier stamp, and the widened 150 ms
defense success grace.

For any live session (probe or shaped owner leg), score the audit trail
with `ops/analyze-s8-lag-comp.py --database arena` — it parses the
`[LAG_COMP]` dual-verdict lines: flip rate by check and switch state,
rewind-ms distribution, pose-source mix. The runtime switch is
`spacetime call arena set_lag_comp_config true 250` (and `false` to
disable); presses log both verdicts in either state, so an on-screen A/B
needs no republish.

## Profiles

Both directions get the same treatment; `plr` is per direction.

**Profile L — light (~80 ms added RTT, no jitter, no loss):**

```bash
sudo dnctl pipe 1 config delay 40ms    # client -> server
sudo dnctl pipe 2 config delay 40ms    # server -> client
```

Start here (and skip the two `probability` jitter lines in step 3 — pipes 3/4
are unused). Pure delay is cheap for the host and keeps movement fully
playable while still making round-trip-shaped feel effects (gap-closer dash
delay, rejection unwind, connection-dot Degraded) clearly visible. For the F4
timeline A/B, add a small jitter branch on top — still zero loss:

```bash
sudo dnctl pipe 3 config delay 65ms    # client -> server, jitter branch
sudo dnctl pipe 4 config delay 65ms    # server -> client, jitter branch
```

**Why loss (and big jitter spreads) hurt so much on loopback TCP:** all of
this rides one TCP stream. A `plr` drop stalls the whole stream for a
retransmit timeout — hundreds of milliseconds on macOS — during which no rows
arrive *and* no input/acks depart; a long enough stall blows past the 12-tick
prediction headroom and trips the emergency resync (> 12 pending commands),
which reads as severe local rubberbanding, far worse than the nominal added
RTT suggests. The bimodal jitter branch also reorders packets inside the
stream, which TCP answers with dup-ACK retransmit churn (CPU heat, bursty
delivery). That is realistic WAN-adjacent behavior and worth observing — but
briefly, and only after the delay-only checks pass. Expect Profile A/B
sessions to be unpleasant to play and hard on the machine; treat them as
short observation windows, not tuning sessions.

**Profile A — moderate (~100 ms added RTT, ~+30 ms jitter, 1 % loss):**

```bash
sudo dnctl pipe 1 config delay 50ms plr 0.01    # client -> server, base
sudo dnctl pipe 2 config delay 50ms plr 0.01    # server -> client, base
sudo dnctl pipe 3 config delay 80ms plr 0.01    # client -> server, jitter branch
sudo dnctl pipe 4 config delay 80ms plr 0.01    # server -> client, jitter branch
```

**Profile B — harsh (~200 ms added RTT, ~+60 ms jitter, 3 % loss):**

```bash
sudo dnctl pipe 1 config delay 100ms plr 0.03
sudo dnctl pipe 2 config delay 100ms plr 0.03
sudo dnctl pipe 3 config delay 160ms plr 0.03
sudo dnctl pipe 4 config delay 160ms plr 0.03
```

`dnctl pipe N config` overwrites in place — switch profiles live without
touching pf.

## Step by step

1. Publish and run the local server as usual (`ops/republish-local-clear.sh`),
   confirm the client connects clean, and note baseline overlay numbers.
   Expect overlay RTT of roughly **5–15 ms** on loopback, not curl's
   sub-millisecond: the ping's send and receive timestamps are both taken on
   the main thread (`SendClockPingIfDue` runs in `Update`, and the reducer
   result callback fires inside the once-per-frame `FrameTick` pump), so every
   sample carries up to a frame or so of scheduling alignment on top of wire
   RTT. `curl -s -o /dev/null -w '%{time_total}s\n'
   http://localhost:3000/v1/ping` (~1 ms) is the wire baseline; the overlay
   number is the game-observed baseline and is the one the added profile
   delays stack onto.

2. Configure the pipes with one of the profiles above.

3. Load the pf rules (destination port 3000 = client→server; source port
   3000 = server→client; `out`-only so loopback packets — which pf sees twice,
   once out and once in — are shaped exactly once):

   ```bash
   cat <<'EOF' | sudo pfctl -a "com.apple/arena-latency" -f -
   dummynet out quick proto tcp from any to any port 3000 probability 30% pipe 3
   dummynet out quick proto tcp from any to any port 3000 pipe 1
   dummynet out quick proto tcp from any port 3000 to any probability 30% pipe 4
   dummynet out quick proto tcp from any port 3000 to any pipe 2
   EOF
   ```

   If your pf build rejects `probability` on dummynet rules, drop the two
   jitter lines — you lose the jitter approximation, keep delay and loss.

4. Enable pf and verify:

   ```bash
   sudo pfctl -E                 # prints an enable token
   sudo dnctl list               # pipes with delay/plr and traffic counters
   sudo pfctl -a "com.apple/arena-latency" -sn   # loaded dummynet rules
   curl -s -o /dev/null -w '%{time_total}s\n' http://localhost:3000/v1/ping
   ```

   The curl timing (any HTTP endpoint on port 3000 works) should jump to at
   least **twice** the configured RTT — `time_total` spans the TCP handshake
   plus the HTTP exchange, each a full round trip, so Profile A reads
   ~0.2–0.35 s (not ~0.1 s) and Profile L ~0.16–0.2 s. The `dnctl list`
   packet counters should be climbing while the client runs.

5. Play. In the overlay expect: RTT last/p50/p95 up by the profile's RTT and
   spread by the jitter branch; clock offset re-converging via precise samples;
   extrapolation ratio and hard snaps rising with loss; the S5 input lead
   stepping up to cover the added delay and then holding, with fallback acks
   settling back to rare-event; under Profile B, visible catch-up on remote
   actions. On the server, `MOVE_FALLBACK` per profile window should rise
   with loss stalls but not sit at one-per-tick.

6. Tear down (order matters — flush the anchor before disabling pf so no
   unshaped window routes through stale pipes):

   ```bash
   sudo pfctl -a "com.apple/arena-latency" -F all
   sudo dnctl -q flush
   sudo pfctl -d                 # or: sudo pfctl -X <token from -E>
   ```

   If pf was enabled before you started (some VPN/firewall tools enable it),
   skip `pfctl -d` — flushing the anchor is enough.

Caveats: everything on port 3000 on this machine is shaped while the rules are
loaded; commands need `sudo`; a reboot clears all of it. Apple's Network Link
Conditioner panel (Xcode Additional Tools) is a GUI alternative but shapes all
traffic system-wide and offers no port scoping, so prefer the recipe above.

## In-editor alternative: callback-delay utility

`Arena.Debugging.NetworkCallbackDelay` (dev-only, default off) defers the
row-callback dispatch that `NetworkCallbackBinder` routes to
`EntityRegistry`/`MatchStateCache`/`LocalCombatState` by a configurable number
of milliseconds, FIFO, on the main thread. No sudo, works in the editor:

- Launch with the environment variable `ARENA_CALLBACK_DELAY_MS=120`, or set
  `NetworkCallbackDelay.DelayMs = 120;` from debug code at runtime
  (`0` disables; queued callbacks still drain in order).

Know what it is not: only binder-routed **row** callbacks are delayed. The SDK
client cache still applies rows immediately (a deferred handler that reads
`conn.Db` sees newer state than its row arguments), outbound reducer calls and
reducer-result callbacks are not delayed (so the overlay's RTT line does not
move — by design, it reports real wire RTT), and self-bound presentation
callbacks (`CombatVFXDispatcher`) fire undelayed. Use it for quick
interpolation/prediction presentation checks; use the dnctl/pfctl recipe for
faithful end-to-end behavior, jitter, and loss.
