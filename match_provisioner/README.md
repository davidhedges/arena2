# Local match provisioner

## Recommended local setup

From the repository root, use the canonical one-command environment setup:

```bash
ops/setup-local-multiplayer.sh
```

It publishes the local Hub, rebuilds the cached disposable-match artifact, and
runs this worker in the background. On macOS it delegates ownership to
`launchd`, allowing the worker to survive the shell or Codex command that
performed setup. Use the same entry point for lifecycle operations:

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
- both WASM artifacts are read once at process start and are never compiled
  per session: the small PvP module for match tickets, and the main server
  module for open-world tickets, whose destination scene comes from the ticket
  rather than from `ARENA_PROVISIONER_MAP_ID`;
- if source inputs change after startup, the worker claims and immediately
  fails the next affected ticket as `ARTIFACT_STALE` without publishing a
  database, so clients receive an actionable error instead of waiting on a
  permanently pending request;
- wakeup updates are coalesced, so a burst of requests causes one authoritative
  Hub snapshot instead of one query set per notification;
- a 30-second reconciliation sweep recovers subscription interruptions,
  restarts, leases, terminal matches, and cleanup work;
- the SQLite ledger stores the exact created database identity before
  bootstrap/assignment so a restart can safely reconcile partial work;
- deletion requires both that recorded identity and ownership by the Hub's
  configured provisioner identity;
- ownership, identity, or match-build mismatches become visible `ORPHANED`
  ledger rows and are never deleted automatically; their Hub tickets and
  client-facing assignments are closed immediately so quarantine never blocks
  another matchmaking request, and quarantined rows do not consume match
  capacity while reconciliation continues monitoring them.

Build the dedicated match module once:

```bash
ops/build-match-spacetimedb.sh
```

That command also applies the canonical Binaryen size pass, enforces the PvP
artifact ceiling, and regenerates the separate `Arena.MatchDb` schema bindings.

Build the disposable open-world module the same way:

```bash
ops/build-openworld-spacetimedb.sh
```

Start the provisioner after the cached optimized artifacts exist:

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

The probe uses the public Hub and match WebSocket APIs, reuses one dedicated
identity across invocations, applies the production 36-query PvP initial
subscription, cancels every sampled ticket, and verifies the provisioner ledger
reports every database `CLEANED`.
Its ticket hashes correlate with the provisioner's `match_startup_timing`
events for stage-level p50/p95 calculations. It requires the
`websocket-client` Python package and intentionally does not measure Unity scene
loading.

The first run saves a private credential under the repository's ignored
`.arena-local/match-benchmark/` directory **before connecting to the Hub**.
Later runs authenticate with that credential, including when Unity's `Library/`
has been regenerated. Credentials are scoped to the local server origin and Hub;
`localhost` and `127.0.0.1` share a scope. One run holds that scope's lock through
match cleanup, so concurrent invocations fail before opening another Hub session.
The benchmark accepts loopback server origins only.

Keep this directory: deleting its credential file means the next run creates a
new player. Corrupt, mismatched or rejected credentials cause an error instead of
an anonymous retry. Restore the same credential to recover that benchmark player;
the script never uses Unity's or the provisioner's credentials, resets profiles,
or deletes old saved players. This benchmark measures match startup with a reused
player; it does not exercise fresh-profile creation on every invocation.

Configuration defaults are documented in `local.env.example`. The default
ledger lives under Unity's ignored `Library/` directory. Cleaned rows are kept
for one day for local diagnosis and then pruned; active and orphaned rows are
never age-pruned.
