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
   confirm the client connects clean, and note baseline overlay numbers
   (loopback RTT should be ~0–2 ms).

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

   The curl timing (any HTTP endpoint on port 3000 works) should jump by
   roughly the configured RTT, and the `dnctl list` packet counters should be
   climbing while the client runs.

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
