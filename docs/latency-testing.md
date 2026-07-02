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
   extrapolation ratio and hard snaps rising with loss; pending-command tick
   lag growing; under Profile B, visible catch-up on remote actions. On the
   server, `MOVE_FALLBACK` per profile window should rise with loss.

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
