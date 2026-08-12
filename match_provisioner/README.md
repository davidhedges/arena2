# Local match provisioner

## Recommended local setup

From the repository root, use the canonical one-command environment setup:

```bash
ops/setup-local-multiplayer.sh
```

It publishes the local Hub, rebuilds the cached disposable-match artifact, and
runs this worker in the background. Use the same entry point for lifecycle
operations:

```bash
ops/setup-local-multiplayer.sh status
ops/setup-local-multiplayer.sh stop
```

The lower-level commands below remain useful for debugging individual pieces.

This is the local control-plane worker. It subscribes to a provisioner-only,
data-free Hub wakeup view, reads private tickets through the loopback management
API, publishes the already-built match WASM into one database per ticket,
bootstraps that database, and records the ready assignment in the Hub.
It then deletes the exact database identity after match termination,
cancellation, allocation timeout, or the hard lifetime limit.

The worker is intentionally local-only:

- the management URL must be an explicit `http://` loopback address;
- the bearer token is accepted only through the process environment and is
  never written to SQLite or logs;
- client assignments contain only the public `ws://`/`wss://` endpoint and
  database identity;
- the WASM is read once at process start and is never compiled per match;
- wakeup updates are coalesced, so a burst of requests causes one authoritative
  Hub snapshot instead of one query set per notification;
- a 30-second reconciliation sweep recovers subscription interruptions,
  restarts, leases, terminal matches, and cleanup work;
- the SQLite ledger stores the exact created database identity before
  bootstrap/assignment so a restart can safely reconcile partial work;
- deletion requires both that recorded identity and ownership by the Hub's
  configured provisioner identity;
- ownership or identity mismatches become visible `ORPHANED` rows and are never
  deleted automatically.

Build the dedicated match module once:

```bash
ops/build-match-spacetimedb.sh
```

That command also applies the canonical Binaryen size pass, enforces the PvP
artifact ceiling, and regenerates the separate `Arena.MatchDb` schema bindings.
Start the provisioner after the cached optimized artifact exists:

```bash
ops/run-local-match-provisioner.sh run
```

Useful one-shot commands:

```bash
ops/run-local-match-provisioner.sh run --once
ops/run-local-match-provisioner.sh status
```

Each allocation emits one `match_startup_timing` JSON event. It reports the
ticket-to-claim delay, management-call durations, database lookup/publish and
verification, bootstrap work, Hub-ready work, total provisioner time, and the
published WASM byte count. Unity logs the corresponding client milestones with
the `[MatchStartupTiming]` prefix through the loaded `Arena_Map_01` scene.

Run the repeatable local match-start benchmark while the server and provisioner
are already running:

```bash
python3 ops/benchmark-local-match-start.py --samples 20
```

The probe uses the public Hub and match WebSocket APIs, reuses one identity,
applies the production 44-query PvP initial subscription, cancels every sampled
ticket, and verifies the provisioner ledger reports every database `CLEANED`.
Its ticket hashes correlate with the provisioner's `match_startup_timing`
events for stage-level p50/p95 calculations. It requires the
`websocket-client` Python package and intentionally does not measure Unity scene
loading.

Configuration defaults are documented in `local.env.example`. The default
ledger lives under Unity's ignored `Library/` directory. Cleaned rows are kept
for one day for local diagnosis and then pruned; active and orphaned rows are
never age-pruned.
