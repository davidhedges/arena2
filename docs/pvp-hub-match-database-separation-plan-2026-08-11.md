# PvP Hub / Match Database Separation Plan

Date: 2026-08-11

Status: **APPROVED — Phases 1–3 locally verified; Phase 4 implemented and build-verified with its interactive Unity gate pending; Phases 5–6 and remote deployment remain unapproved**

## 1. Goal

Move the multiplayer PvP path toward the topology recommended by SpacetimeDB:
one persistent Hub database and one disposable SpacetimeDB database per live
match.

The first vertical slice remains the existing unranked 2v2 bot match:

```text
Team 0: requesting human + ally dummy
Team 1: enemy dummy + enemy dummy
```

This plan covers only the PvP foundation needed to provision that match, hand
the Unity client into it, return the client to the Hub, and delete the match
database. It does not add 3v3, 10v10, another human, ranked play, survival,
random dungeons, training/open-world sessions, rewards, or progression.

SpacetimeDB calls a deployed module with its own state a database. Its current
room/match guidance recommends that an external orchestration service create
and destroy a separate database for each room or match:

- <https://spacetimedb.com/docs/databases/>
- <https://spacetimedb.com/docs/intro/faq/#how-do-i-build-a-room-based-or-match-based-game>

## 2. Decisions

1. **The Hub is persistent and event-driven.** It does not compile or schedule
   the 30.3 Hz arena simulation.
2. **Each PvP match gets one ephemeral database.** A database is not a VM; many
   match databases may run on one SpacetimeDB host.
3. **The existing `server/` module is the starting match module.** It already
   owns authoritative movement, physics, combat, bots, and match conclusion.
4. **A new small `hub-server/` module owns durable/control-plane state.** It is
   generated into a separate C# namespace so Hub and match connections can
   coexist during handoff.
5. **A small external provisioner creates and deletes match databases.** It
   runs beside the self-hosted SpacetimeDB process and uses only the host-local
   management interface. No database-owner credential reaches Unity.
6. **The match receives a frozen player snapshot.** It never reads the Hub
   during combat and never directly changes durable Hub state.
7. **The initial bot match writes no durable result.** The database can be
   deleted after the result screen or abort. Trusted result transfer is added
   only when rewards, ranking, or match history are approved.
8. **One row has one authority.** Durable player state is writable only in the
   Hub. Runtime combat state is writable only in the match database.

## 3. Why the current module cannot also be the Hub

The current module is intentionally simulation-first:

- `server/src/game_loop.rs` schedules `game_tick` in `init` and runs at 30.3 Hz;
- `server/src/player.rs` creates a complete combat actor when any client
  connects;
- the module includes physics, combat, status effects, spells, projectiles,
  NPCs, world state, and transient presentation events;
- `Assets/Arena/Runtime/Network/NetworkManager.cs` assumes one fixed generated
  database connection for the whole runtime;
- `Assets/Arena/Runtime/Network/NetworkEnvironmentConfig.cs` currently keys an
  identity token by both server and database name, which would accidentally
  give the same person a different identity when moving into a match database.

Putting this module in the persistent Hub would pay for an idle simulation
tick forever and preserve match-only state where it is not needed. The Hub
must therefore be a separate module, not a mode flag inside the existing one.

## 4. Authority and data ownership

### 4.1 Persistent Hub database

The Hub owns anything that must survive deletion of a match database:

| Area | Hub-owned state |
|---|---|
| Identity | player identity, display name, account/profile timestamps |
| Presentation | saved appearance and cosmetics |
| Build choices | saved discipline, action bar, equipment/loadout selections |
| Economy | inventory, owned item instances, unlocks and currencies when retained for PvP |
| Social | parties and invitations when multiplayer parties are implemented |
| Match control | requests, provisioning status, assignments, cancellation and expiry |
| Competition | ratings, penalties, rewards and compact match history when approved later |

The Hub may use infrequent scheduled reducers to expire tickets or recover
stale assignments. It must not contain `GameLoopTimer`, player physics,
projectiles, combat resolution, bot AI, or any other fixed-rate simulation.

### 4.2 Ephemeral match database

The match database owns everything needed to conduct exactly one match:

| Area | Match-owned state |
|---|---|
| Admission | private player reservations and the provisioner identity |
| Configuration | match ID, module build ID, mode, format, ruleset, seed and deadlines |
| Roster | participants, teams, slots and bot markers |
| Frozen player data | display/appearance/loadout snapshot used for this match only |
| Simulation | input buffers, position, velocity, collision, health and resources |
| Combat | casts, cooldowns, statuses, effects, projectiles and combat events |
| Lifecycle | waiting, countdown, in-progress, ended/aborted and winner |
| Presentation | short-lived events and scoreboard rows required by connected clients |

Authored combat definitions remain source-controlled inputs to the match
module. They may be compiled into the WASM or seeded as read-only catalog rows
when clients need to subscribe to them. The Hub may hold display-only copies,
but it is not the authority for live combat rules.

### 4.3 External provisioner

The provisioner owns infrastructure lifecycle, not gameplay:

- claim one pending Hub request with a lease;
- publish an already-built, versioned match WASM into a new database;
- invoke the match database's owner-only bootstrap reducer;
- write the resulting database identity into the Hub assignment;
- observe completion, abort, and timeouts;
- delete only the exact database identity it created;
- retry failed cleanup and report orphan counts.

It must not decide hits, winners, loadouts, rewards, or matchmaking skill.

## 5. Minimal contracts

Names below are working contract names. Exact Rust field syntax is deferred to
the implementation slice, but their authority and semantics are fixed here.

### 5.1 Hub private tables

```text
HubPlayer
  identity                primary key
  display_name
  created_at
  updated_at

MatchTicket
  ticket_id               primary key; server-derived from player identity
                          plus the client's stable request ID
  player_identity         indexed; at most one active bot-match ticket
  queue_kind              UNRANKED
  format                  2V2
  status                  PENDING | CLAIMED | PROVISIONING | READY |
                          FAILED | CLOSED
  created_at
  updated_at
  expires_at
  lease_owner             optional provisioner identity
  lease_until             optional
  failure_code            stable machine-readable code, no secret details

MatchAssignment
  ticket_id               primary key
  player_identity         indexed
  match_id                stable logical correlation ID
  server_uri              public client endpoint
  database_identity       exact ephemeral database to connect to
  match_build_id          deployed contract/content version
  ready_at
  expires_at
```

Durable appearance and loadout tables can reuse the semantic shapes already in
`appearance.rs`, `progression.rs`, and `inventory.rs`, but they move only as a
separate approved migration. The first orchestration slice may create a
default snapshot so it does not have to migrate disposable local development
data.

`MatchTicket` and `MatchAssignment` remain private. Unity subscribes to a
caller-aware public view such as `my_match_status`, which returns only the
calling player's current ticket and assignment. SpacetimeDB recommends private
tables plus caller-filtered views for this sort of data:
<https://spacetimedb.com/docs/tables/access-permissions/>.

### 5.2 Hub reducers and views

```text
request_unranked_2v2_bot_match(client_request_id)
cancel_match_ticket(ticket_id)
my_hub_player()                         public caller-filtered view
my_match_status()                       public caller-filtered view

service_claim_ticket(ticket_id, lease_id, lease_until)
service_mark_ready(ticket_id, assignment)
service_mark_failed(ticket_id, failure_code)
service_close_ticket(ticket_id)
```

The first two reducers authorize against `ctx.sender()`. Service reducers
authorize against a provisioner identity stored by the database owner. The
client-supplied request ID makes a repeated click or retry idempotent instead
of creating multiple databases.

### 5.3 Match private tables

```text
ModuleOwner
  identity                captured from ctx.sender() during database init

MatchBootstrapConfig
  singleton_id            primary key
  match_id
  match_build_id
  queue_kind              UNRANKED
  format                  2V2
  ruleset                 TEAM_ELIMINATION
  seed
  phase                   BOOTSTRAPPING | WAITING | COUNTDOWN |
                          IN_PROGRESS | ENDED | ABORTED
  allocation_expires_at
  ended_at                optional

MatchReservation
  player_identity         primary key
  team_id
  team_slot
  frozen player snapshot fields
```

The existing `ArenaInstance`, `ArenaMatch`, `MatchParticipant`, physics,
combat, and scoreboard tables continue as match-runtime state initially. A
physical match database contains exactly one competitive `ArenaMatch`; the
logical `instance_id` remains temporarily to avoid an unrelated combat
refactor.

### 5.4 Match reducers and connection rules

```text
bootstrap_unranked_2v2_bot_match(config, reservations)
abort_match(reason)
```

- `init` records the database owner but does **not** start the 30 Hz loop.
- Only the stored owner identity may bootstrap or administratively abort.
- Bootstrap is one-shot and creates the arena, roster, bots, and reservations.
- An owner/service connection is allowed but never spawns a player actor.
- A reserved human connection creates that human's actor from the frozen
  snapshot. An unreserved connection is rejected before any public actor row
  is created.
- The bot-match countdown begins only after its required human is connected.
- Gameplay reducers require a reserved participant in an active phase.
- The simulation chain runs only during countdown/in-progress work and stops
  rescheduling after `ENDED` or `ABORTED`.
- Disconnect keeps the current bot-match rule: abort immediately. Reconnect
  grace is explicitly deferred.

The database address is routing information, not a password. Admission is
always enforced from `ctx.sender()` and the private reservation table.

## 6. Provisioning and handoff sequence

```text
Unity/Hub         Hub DB          Provisioner       Match DB
    | request       |                  |                |
    |-------------->| PENDING          |                |
    |                |<--claim---------|                |
    |                | PROVISIONING     |--publish WASM->|
    |                |                  |--bootstrap---->|
    |                |<--READY----------|                |
    |<--my status----|                  |                |
    |---------------------connect with same identity---->|
    |<--------------------reservation accepted-----------|
    | disconnect Hub |                  |                |
    | play match     |                  |                |
    |<--------------------------------------ENDED---------|
    | disconnect match; reconnect Hub     |--delete DB-->|
    |-------------->| close/expire      |                |
```

Handoff rules:

1. Unity keeps the Hub connection until the match connection is authenticated,
   its contract version is accepted, and its initial subscription is applied.
2. It then disconnects from the Hub and loads the selected map, currently
   `Arena_Map_01` (`ARENA_MAP_01`).
3. On match end, abort, or connection failure, it disposes match callbacks and
   caches before reconnecting to the configured Hub database.
4. The same host-issued identity token is reused across databases on the same
   SpacetimeDB cluster. The credential key must therefore be host/cluster
   scoped rather than database scoped. Host-issued tokens are not assumed to
   work across different clusters:
   <https://spacetimedb.com/docs/http/authorization/>.
5. Unity never receives the provisioner's owner token, local management URL,
   filesystem paths, or delete capability.

## 7. Repository shape

Keep the existing module path stable during the first migration:

```text
server/                                      existing match module
hub-server/                                  new persistent Hub module
services/match-provisioner/                  small host-local process

Assets/Arena/Runtime/Generated/SpacetimeDB/  existing match C# bindings
Assets/Arena/Runtime/Generated/HubSpacetimeDB/
                                             Hub bindings in Arena.HubDb
```

The existing match namespace stays unchanged to avoid a broad generated-code
rename. Hub bindings are generated with a distinct `--namespace`.

Client responsibilities split as follows:

- `HubNetworkManager`: Hub connection, Hub subscriptions, requests and profile
  view models;
- existing `NetworkManager`: dynamic match endpoint, match subscriptions,
  clocks, callbacks and simulation caches;
- `MatchHandoffCoordinator`: the short overlap between the two connections and
  all rollback/return behavior;
- `NetworkEnvironmentConfig`: one Hub endpoint per environment plus an
  ephemeral match endpoint supplied by `MatchAssignment`.

The Hub UI must consume a small local profile/view-model abstraction rather
than directly reading types from the match bindings. This prevents generated
Hub and match schemas from leaking into each other's screens.

## 8. Self-hosted deployment shape

The existing Nginx configuration already permits WebSocket subscriptions for
any database name while keeping all other management routes local-only. That
is the right boundary for dynamic match databases.

The minimal self-hosted provisioner runs as a restricted systemd service on the
same Hetzner VM:

- read-only access to a versioned match WASM directory;
- a dedicated SpacetimeDB owner/service token supplied through a protected
  environment file;
- host-local access to `127.0.0.1:3000`;
- no inbound public HTTP route;
- a configurable maximum number of concurrent matches;
- a configurable maximum database lifetime;
- structured logs without tokens or frozen player details.

Deployment builds each module once. The match provisioner repeatedly publishes
the same immutable WASM bytes; it must **not compile the Rust module per
match**. The existing `ops/deploy-spacetimedb.sh` already demonstrates copying
a prebuilt WASM and publishing from the host. SpacetimeDB's management API also
supports creating and deleting databases:
<https://spacetimedb.com/docs/http/database/>.

Maincloud can use the same logical contracts, but its quotas and orchestration
mechanism must be validated separately before selecting it for production.
This plan does not silently switch the existing self-hosted deployment.

## 9. Failure and cleanup rules

| Failure | Required behavior |
|---|---|
| Repeated Play click/client retry | Return the caller's existing active ticket; never allocate twice |
| Provisioner dies while claiming | Lease expires and a later worker safely resumes or fails the ticket |
| Publish fails | Mark ticket `FAILED`; do not send a database address to Unity |
| Bootstrap fails after publish | Delete that exact database identity, then mark `FAILED` |
| Unity cannot connect | Keep assignment retryable until its short expiry; then abort/delete |
| Unreserved client connects | Reject connection without spawning an actor |
| Human disconnects during match | Mark match `ABORTED`; stop tick; provisioner deletes it |
| Match ends normally | Stop tick immediately; allow a short result-screen window; delete afterward |
| Provisioner restarts | Reconcile leased tickets and databases it owns before accepting new work |
| Delete fails | Retry with backoff and expose an orphan metric; never broaden the delete target |
| Hub is unavailable after match | Client can retry Hub connection; match database still expires by TTL |

The sweeper may delete a database only when all of these match:

- it has a Hub ticket created by this provisioner;
- the recorded exact database identity still resolves;
- the database is owned by the configured service identity;
- the match is ended/aborted or its hard TTL elapsed.

Database names use a dedicated prefix plus a random match ID for debugging,
but the name is never treated as authorization.

## 10. Cost and growth guardrails

- No fixed-rate Hub tick.
- No match tick before successful bootstrap/admission.
- Stop the match tick at terminal state rather than waiting for deletion.
- Build once per release; publish the cached WASM per match.
- Enforce one active bot-match ticket per player.
- Enforce a hard, configuration-driven concurrent-match ceiling.
- Reject new requests when the ceiling or a cost circuit breaker is reached.
- Give provisioning, waiting, result-screen, and total database lifetime
  separate TTLs.
- Delete the whole match database instead of retaining historical physics,
  combat events, position history, or scoreboard rows.
- Keep only the current ticket/assignment per player in the Hub; expire closed
  control-plane rows unless a future product requirement needs compact history.
- Record creation latency, active database count, match lifetime, terminal
  reason, cleanup latency, cleanup failures and orphan count.
- Measure cold-start time and resource use before considering database pooling.
  Pooling is deferred because safe reset/tenant isolation is more complex than
  create/delete.

## 11. Delivery phases and exit gates

Each phase is a separate approval and reviewable commit. Passing a phase does
not authorize the next one.

### Phase 1 — Hub module foundation

- Add `hub-server/` with `HubPlayer`, private ticket/assignment tables,
  caller-filtered views and authorized reducers.
- Generate Hub C# bindings into the separate `Arena.HubDb` namespace.
- Add local publish/build scripts without changing the current bot-match path.
- Add Rust tests for idempotent requests, one-active-ticket enforcement,
  caller-only views, service authorization and expiry.

Exit gate:

- a local `arena-hub-local` database builds and publishes;
- its schema contains no simulation/tick/combat tables;
- two identities cannot read each other's match status;
- the existing direct bot match still works unchanged.

Phase 1 completion record (2026-08-11):

- Published `arena-hub-local` locally with database identity
  `c200b256869f4ebd7cb6febed6798ce6b6deb5d60f19ecb6bda3b696009f1f18`.
- Verified the five underlying Hub tables are private and only
  `my_hub_player` and `my_match_status` are public caller-filtered views.
- Verified a second identity sees no first-identity match status, cannot query
  `match_ticket`, and cannot invoke a service-only reducer.
- Verified retry idempotency, one-active-ticket rejection, cancellation, the
  60-second housekeeping schedule, seven Hub unit tests, and the three focused
  existing 2v2 bot-roster/outcome tests.
- Kept the existing match module, direct bot-match reducer, and active Unity
  connection path outside this phase's implementation boundary.

### Phase 2 — Provisionable match contract

- Add module-owner capture, one-shot bootstrap configuration and private
  reservations to the existing match module.
- Make owner/service connections non-player connections.
- Reject unreserved gameplay clients before actor creation.
- Create the existing 2v2 bot roster from the bootstrap contract.
- Gate tick startup/shutdown on match lifecycle.
- Preserve the old local direct-start path only as a temporary fallback until
  the client handoff phase passes.

Exit gate:

- a fresh local database can be published, bootstrapped by its owner, joined by
  exactly the reserved identity, played, ended and left idle with no tick;
- a different identity is rejected;
- a second bootstrap is rejected;
- no durable result or cross-database dependency exists.

Phase 2 completion record (2026-08-11):

- Published the fresh, inert local database `arena-match-contract-local` with
  database identity
  `c2006c25db7b50e1366e6dcd24bc0e6dac2dad29f711a459270e3988df292237`.
- Verified `match_module_owner`, `match_bootstrap_config`, and
  `match_reservation` are private; the unconfigured database has no player,
  arena, game-loop timer, or watchdog rows.
- Bootstrapped the fixed unranked 2v2 contract once with an explicit seed and
  one reserved human. The waiting roster contained the reserved participant
  plus three bots, but only the three bot actor rows and no active tick.
- Verified a second bootstrap fails and an unreserved local identity is
  rejected before actor creation.
- Connected the exact reserved identity and verified the frozen display name,
  four-actor roster, `COUNTDOWN` to `IN_PROGRESS` transition, and active tick
  and watchdog. Disconnect then recorded `ABORTED` with
  `PLAYER_DISCONNECTED`, removed the match runtime rows, and left no scheduled
  tick or watchdog.
- Wired normal team elimination to `ENDED` and the same tick-stop gate; focused
  contract, roster/outcome, and game-loop tests pass. The initial bot slice
  writes no durable result and makes no Hub or other database call.
- Preserved the current direct-connect workflow behind the explicit,
  owner-only `enable_local_direct_mode` compatibility reducer. The local
  republish script enables it by default and can publish an inert provisioned
  template with `ARENA_ENABLE_LOCAL_DIRECT_MODE=0`.

### Phase 3 — Local provisioner

- Add the restricted host-local provisioner and configuration.
- Claim Hub tickets with leases.
- Publish the already-built match WASM, bootstrap it, mark the assignment
  ready, and delete it after end/abort/TTL.
- Add reconciliation for restarts, partial publishes and failed deletes.
- Add concurrency and lifetime caps before remote use.

Exit gate:

- an API-level Hub request creates exactly one playable local match database;
- repeated requests do not create duplicates;
- success, bootstrap failure, timeout and restart recovery all leave no orphan;
- no management credential or endpoint is publicly exposed.

Phase 3 completion record (2026-08-11):

- Added the standard-library Python worker in `match_provisioner/`, its
  local-only runner `ops/run-local-match-provisioner.sh`, documented
  environment configuration, process lock, and SQLite allocation ledger.
- Restricted management traffic to an explicit HTTP loopback address. The
  bearer token is process-only; it is never written to the ledger, logs, Hub
  assignment, or Unity-facing state. The match WASM is read once at process
  startup and never compiled per allocation.
- Added deterministic, ticket-derived database and match IDs; one-worker
  leasing; a configurable concurrent-match ceiling; allocation, assignment,
  and hard-lifetime bounds; exact database identity and owner checks before
  deletion; cleanup retry/backoff; and sticky `ORPHANED` reporting for safety
  mismatches.
- Extended the Hub lease contract so the same work attempt can renew and an
  expired `PROVISIONING` attempt can be reclaimed after restart, without
  allowing renewal after the ticket itself expires.
- Verified an ordinary API-level request published exactly one database,
  exposed a `READY` assignment, admitted the reserved identity, created the
  four-actor roster, and advanced `COUNTDOWN` to `IN_PROGRESS`. Disconnect
  caused `ABORTED`; the worker deleted exact identity
  `c20004440a16c94d22b459bd5db4eafb6c43002812a1ee53c870314e5fe8be48`,
  removed the assignment, and closed the ticket.
- Verified a reducer-level bootstrap rejection deleted exact partial database
  `c200120c5657a52560326dfb39c706914acb3f6ff5d869a013b2590015f24e51`
  before exposing `FAILED / BOOTSTRAP_FAILED` in the Hub.
- Verified a 30-second unjoined allocation timeout aborted and deleted exact
  database
  `c200dd7d75e33348b805177327408b136aa5b7466197a41544d3aa48e8769b98`
  and removed its assignment.
- Verified process restart recovery by stopping after bootstrap with Hub state
  `PROVISIONING`, then starting a fresh process. It reused exact identity
  `c20052ccb2936392a28866864cd7e16b07bf61b00430a1ffd021430237539aba`,
  marked that same database `READY`, and later deleted it cleanly.
- Verified repeated requests are idempotent, partial publish responses are
  reconciled, failed deletes retry without closing early, the capacity ceiling
  leaves excess tickets pending, and ownership mismatches are never deleted.
  Thirteen provisioner tests and eight Hub tests pass.
- Final local state has five retained `CLEANED` diagnostic ledger rows, zero
  active/orphaned rows, zero Hub assignments, and no Phase 3-created match
  database. The persistent local Hub remains
  `c200b256869f4ebd7cb6febed6798ce6b6deb5d60f19ecb6bda3b696009f1f18`.

### Phase 4 — Unity dual connection and handoff

- Add `HubNetworkManager` and `MatchHandoffCoordinator`.
- Scope identity-token storage to the SpacetimeDB host/cluster and migrate the
  existing local credential key safely.
- Bind the Hub UI to Hub views and submit the idempotent bot-match request.
- Connect to the assigned database, validate contract/subscriptions, then
  disconnect Hub and load the match scene.
- Return or roll back to Hub on success, match failure, timeout or transport
  failure without retaining stale match callbacks/caches.

Exit gate:

- one Hub click provisions and enters the current 2v2 bot match;
- the Unity identity is the same in Hub and match databases;
- failure at every handoff boundary returns a usable Hub UI;
- normal match completion returns to Hub and the database is deleted;
- ordinary C#/Rust builds and focused tests pass without Unity batch mode.

Phase 4 implementation record (2026-08-11):

- Added a persistent `HubNetworkManager` which connects only to the selected
  environment's Hub database, subscribes only to `my_hub_player` and
  `my_match_status`, owns stable idempotency keys, and exposes schema-free
  profile/status snapshots to the Hub UI.
- Added `MatchHandoffCoordinator` as the single dual-connection state owner.
  It validates READY assignments, rejects cross-cluster addresses before a
  token can be sent, keeps Hub connected through match authentication plus
  contract/local-subscription acceptance, then disconnects Hub and enters the
  arena.
- Changed credential storage from database-scoped to host/cluster-scoped,
  including `http`/`ws`, `https`/`wss`, and local `localhost`/`127.0.0.1`
  equivalence. Existing gameplay-module keychain/session/plaintext keys migrate
  to the new scope without deleting the secure fallback unless the new secure
  save succeeds.
- The real Hub matchmaking button now submits the Hub request and presents
  PENDING/CLAIMED/PROVISIONING/READY/connection errors. The display name comes
  from the Hub profile snapshot; generated Hub bindings do not leak into the
  screen.
- Provisioned matches return by disconnecting, not by invoking
  `leave_instance`. Match timeout, identity mismatch, contract/subscription
  failure, Hub loss during overlap, and match transport loss all tear down
  gameplay caches/callback ownership, reconnect Hub, cancel the visible ticket
  when possible, and defer a return to the Hub scene. The legacy leave reducer
  remains only for non-provisioned/direct instance flows.
- `Assembly-CSharp` and `Arena.EditModeTests` compile with zero errors without
  Unity batch mode. Both Rust modules pass `cargo check`; the persistent local
  Hub was republished without deleting data and the local provisioner starts
  with zero active/orphaned allocations.
- The remaining Phase 4 exit check is an ordinary interactive Unity play-mode
  pass: click once in Hub, enter the provisioned 2v2, return, and confirm the
  worker deletes the database. Unity batch mode was intentionally not used.

### Phase 5 — Remote staging and operational proof

This phase requires a fresh explicit approval because it changes remote state.

- deploy the Hub module, versioned match WASM and provisioner to one staging
  host;
- keep publish/delete management routes host-local;
- run the bot flow from an external Unity client;
- verify TLS, identity reuse, admission rejection, TTL cleanup and restart
  reconciliation;
- collect creation latency, lifetime and resource/cost measurements.

Exit gate:

- repeated remote matches create and delete cleanly;
- no active simulation remains after terminal state;
- the orphan count returns to zero after forced provisioner and client
  failures;
- the configured concurrency ceiling is enforced.

### Phase 6 — Cutover and measured slimming

- Remove the temporary direct `StartUnranked2V2BotMatch` Hub-to-match path only
  after the provisioned path passes its manual gate.
- Remove persistent/open-world initialization from the PvP match build where
  it is no longer required, without implementing those excluded game modes.
- Decide from measured database creation time, WASM size and memory whether a
  dedicated slim PvP crate is worth the refactor.
- Keep the old local `arena` database available until the new path has an
  explicit cleanup approval; do not destructively clear it as part of cutover.

Exit gate:

- Hub contains no combat simulation;
- each PvP database contains one match and no durable player authority;
- no obsolete direct-start path can bypass reservations;
- deletion, not historical-row pruning, is the normal match cleanup mechanism.

## 12. Verification matrix

Automated coverage must include:

- Hub request idempotency and per-player uniqueness;
- caller-filtered Hub views;
- provisioner-only Hub mutations;
- match owner-only bootstrap and one-shot enforcement;
- reservation admission and hostile unreserved rejection;
- same identity across Hub and match connections;
- simulation scheduling only in active phases;
- match conclusion and abort stop scheduling;
- provisioner lease recovery and exact-identity deletion guards;
- Unity handoff success, timeout, rejected admission and return-to-Hub cache
  reset;
- generated bindings for both modules.

Manual acceptance for the first complete vertical slice:

1. Start in Hub connected only to the Hub database.
2. Press **Play 2v2** once.
3. See a bounded provisioning state rather than entering an in-database logical
   match immediately.
4. Join the newly created database with the same identity.
5. Play the existing one-human/three-dummy match unchanged.
6. Finish or abort and return to a working Hub.
7. Confirm the match database is deleted and the Hub remains alive.
8. Repeat after killing/restarting the provisioner and after interrupting the
   client during handoff.

Unity Editor batch mode is prohibited. Verification uses Rust tests, ordinary
builds, generated-binding compilation where available, and interactive Unity
acceptance.

## 13. Deferred decisions

These are intentionally not prerequisites for the bot-match orchestration
slice:

- real human matchmaking, parties and multi-seat readiness;
- 3v3 and 10v10;
- ranked results, rewards and trusted result ingestion;
- reconnect grace, spectators, backfill and rematch;
- OIDC/account-provider selection;
- cross-region placement and multiple SpacetimeDB clusters;
- warm database pools;
- migration of disposable local inventory/progression data;
- infrastructure for survival, random dungeon, training or open-world modes.

## 14. First approval boundary

The next implementable item is **Phase 1 — Hub module foundation only**. It
adds the independent persistent Hub module and contracts while deliberately
leaving the working 2v2 bot-match route and all remote infrastructure
unchanged.
