# Disposable open-world instances — design of record (2026-08-18)

Owner decision 2026-08-18: open-world destinations must be **provisioned and
disposed like PvP matches**. Rationale: persistent open-world state piles up in
the local database, commitlogs grow without bound, and the cleanup-script
treadmill is not acceptable. Progress made in an open world is **ephemeral for
now** — you enter with your Hub loadout and nothing is written back.

## 1. The bug this replaces

Open-world travel is currently dead code from the Hub.

`NetworkManager.Start()` (`Assets/Arena/Runtime/Network/NetworkManager.cs:187`)
deliberately skips the gameplay connection while the Hub is the active scene:

```csharp
// Hub owns its own small control-plane connection. The gameplay
// connection is opened only after an assignment is ready.
if (string.Equals(SceneManager.GetActiveScene().name, "Hub", ...))
    return;
```

But `HubController.RequestTravel` (`Assets/Arena/Runtime/UI/HubController.cs:294`)
resolves its connection as `NetworkManager.Instance?.Conn`, and
`Conn => IsConnected ? _conn : null`. In the Hub that is always null, so every
destination click stops at `HubController.cs:336` with
`"Cannot travel while disconnected."` and never reaches `SceneManager.LoadScene`.

Introduced by `06d7a1a8` ("feat: add disposable PvP match provisioning",
2026-08-12), which replaced the old unconditional `ConnectToResolvedEndpoint()`
in `Start()` with the Hub early-return. PvP gained a provisioning flow to
replace it; open-world travel did not. `RequestSurvival` has the same defect —
`StartSurvivalRun` is sent on the same null connection — but survival mode is
deprecated (owner, 2026-08-18), so that path is being retired, not repaired.

The scene itself is fine: `Giant_Skeleton.unity` is in Build Settings enabled,
and the destination buttons are wired correctly by name (`Travel_<SceneName>`).

## 2. What already generalizes (do not rebuild these)

- **`match-server` is not a PvP fork.** It is a ~300-line build/schema boundary
  that compiles the authoritative `server/src/` tree through `#[path]` includes.
  It already pulls in `open_world_scene.rs` and `open_world_terrain.rs`.
- **Disposal already exists.** `match_provisioner/worker.py` has
  `_delete_exact_database` and calls it on teardown paths. This is the behavior
  the owner wants and it is inherited for free.
- **Assignment/handoff already exists.** `MatchTicket` -> `MatchAssignment` ->
  `MatchHandoffCoordinator` -> `NetworkManager.ConnectToProvisionedMatch` is a
  working pipeline with lease, expiry, and failure codes.
- **Loadout carry-in already exists.** `freeze_player_loadout_for_ticket`
  snapshots the Hub build onto the ticket, and the disposable module resolves
  authored definition ids into fresh item instances.

## 3. Key decision: the instance runs the MAIN module, not `match-server`

`set_open_world_scene` lives at `server/src/arena.rs:290` behind
`#[cfg(not(feature = "pvp_match"))]`, so it is **compiled out** of
`match-server` (whose default feature is `pvp_match`). That is why it is absent
from `Assets/Arena/Runtime/Generated/MatchSpacetimeDB/`.

Rather than add an open-world feature flavor and a third crate, publish the
**existing main `server/` wasm** as the disposable open-world instance:

- it already contains every open-world reducer,
- it already matches the client's existing `SpacetimeDB.Types` bindings, which
  is exactly what the pre-`06d7a1a8` travel path connected with,
- no new crate, no `cfg` surgery, no third binding set.

Cost: the main module wasm is larger than `arena_match.opt.wasm`. The
provisioner publishes a **prebuilt** artifact and never compiles per session,
so this is an upload-size cost, not a build cost. Watch it against
`ops/check-match-wasm-size.sh`-style limits.

## 4. Work plan

### 4.1 hub-server (Rust)

Add an open-world request reducer modeled on `request_unranked_2v2_bot_match`
(`hub-server/src/lib.rs:605`):

```rust
#[reducer]
pub fn request_open_world_instance(
    ctx: &ReducerContext,
    client_request_id: String,
    destination: String,   // authored scene name, e.g. "Giant_Skeleton"
) -> Result<(), String>
```

- Reuse the existing `MatchTicket` row shape: `queue_kind = "open_world"`,
  `format = <destination>`. **This avoids a `MatchTicket` schema change**, which
  matters because republishing the hub module risks the durable
  `HubPlayerLoadout` rows.
- Validate `destination` against an allow-list so a client cannot ask the
  provisioner to publish arbitrary strings.
- Reuse `request_decision`, `freeze_player_loadout_for_ticket`, and
  `bump_provisioner_wakeup` unchanged.

### 4.1b server (Rust) — open-world bootstrap reducer  **[SETTLED — option 1]**

The provisioner does not merely publish a database; it calls a bootstrap
reducer on the fresh instance —
`bootstrap_unranked_2_v_2_bot_match` (`worker.py:1162`), defined at
`server/src/match_contract.rs:312`. Open world needs an analogous
`bootstrap_open_world_instance` that claims the module owner, refuses a database
that already has gameplay rows, records the reservation, and seats the caller's
frozen Hub loadout.

**The wrinkle:** `MatchBootstrapConfig` is PvP-shaped. Its `map_id` is validated
through `require_arena_map_id` (authored *arena* maps only), and it carries
`ruleset`, `seed`, team-oriented `MatchReservation` rows, and an elimination
phase machine (`PHASE_WAITING` -> ...). An open-world destination is none of
those things. Pick one before writing 4.1b:

1. **Reuse `MatchBootstrapConfig`** with `queue_kind = "OPEN_WORLD"`,
   `format = <destination>`, and relax `require_arena_map_id` for that kind.
   Least new schema, but overloads a PvP-shaped row and its phase machine.
2. **Add an `OpenWorldBootstrapConfig` singleton** beside it and branch the
   lifecycle on `deployment_mode`. Cleaner separation, more surface, and the
   match phase/teardown paths must learn about a second config.

Recommendation: (1) for the first cut, because teardown, allocation expiry, and
`_validate_existing_bootstrap` already key off that singleton and would
otherwise need parallel implementations. Revisit if the phase machine fights it.

**Decision (built 2026-08-18): option 1.** The phase machine did not fight it.
`bootstrap_open_world_instance` (`server/src/match_contract.rs`, `#[cfg(not(feature
= "pvp_match"))]` for the same reason `set_open_world_scene` is) shares one
`claim_provisioned_database` helper with the 2v2 bootstrap, so both queue kinds
provably get the same owner check, one-shot latch, gameplay-row refusal,
loadout validation, and allocation bound. Only three things differ:

- **destination vocabulary** — `require_arena_map_id` for a match, the authored
  scene list for a world. The destination occupies `map_id` (so the provisioner
  and Hub assignment carry it unchanged) *and* `format` (mirroring the ticket).
- **what bootstrap builds** — nothing. A match builds its arena instance and
  roster up front; an authored world has none, so the reducer only records the
  config and reservation.
- **phase** — both bootstrap to `WAITING`. On the reserved player's connect a
  match goes to `COUNTDOWN` and joins the roster, while a world goes straight to
  `IN_PROGRESS` (it is live on arrival) and enters the ordinary open-world
  lifecycle via `set_player_open_world`. On disconnect a match reports
  `ABORTED/PLAYER_DISCONNECTED`; leaving a world *is* its ending, so it reports
  `ENDED/PLAYER_LEFT`. Both are terminal, which is all the provisioner reads.

One further rule had to bend: `leave_instance` refuses to run in a provisioned
database (a reserved PvP player must not leave the match). A disposable world is
exempt, because leaving a private instance there returns you to that world, not
out of the database.

### 4.2 match_provisioner/worker.py

Today the destination is process-global: `ARENA_PROVISIONER_MAP_ID` is read from
env (`worker.py:155`), stored on `config.map_id`, and a mismatch is actively
rejected at `worker.py:1192`. Two changes:

1. **Destination per ticket.** For `queue_kind == "open_world"`, take the
   destination from the ticket's `format` instead of `config.map_id`, and relax
   the `worker.py:1192` equality check for that kind.
2. **Second artifact.** Add `ARENA_PROVISIONER_OPENWORLD_WASM` (+ manifest)
   pointing at the main server module build, and select the artifact by
   `queue_kind`.

Disposal needs no change.

### 4.3 Client (C#)

- `HubController.RequestTravel` stops calling `SetOpenWorldScene` on the (null)
  gameplay connection. Instead it calls the new hub reducer on the **hub**
  connection, which is already connected in the Hub.
- Reuse/extend `MatchHandoffCoordinator` to await the `MatchAssignment`, call
  `ConnectToProvisionedMatch`, then request the scene and `LoadScene`.
- Keep `OpenWorldTravelCatalog.IsRegisteredOpenWorldScene` as the client-side
  guard, and keep `SetTravelButtonsInteractable(false)` while a ticket is live
  so the button visibly does something.
- Regenerate hub bindings after 4.1 — canonical command is a harness-featured
  cargo build plus `spacetime generate --bin-path` (never `--module-path`).

### 4.4 Verification

Both legs are automated; neither needs a human at the keyboard.

- **Provisioner leg** — `ops/open-world-travel-probe.py`. Opens an anonymous Hub
  websocket (a fresh non-owner identity, the same shape a real client has),
  calls `request_open_world_instance`, waits for READY, reconnects to the
  assigned database with the *same* identity token, and asserts the player was
  seated in the requested scene; then disconnects and asserts the database is
  deleted.
- **Client leg** — `ops/open-world-travel-client-leg.sh`, a batchmode editor run
  that presses the Hub's real `Travel_<Scene>` button and exits nonzero unless
  the destination scene becomes active.

## 6. Measured cost of running the main module per instance

The main server module is **~424 MB** of wasm against the PvP module's 3.5 MB,
because `server/src/world_data` embeds ~150 MB of heightfield/collision JSON and
`const X: &str = include_str!(..)` is *inlined at every use site* — each payload
lands in the binary 3–5 times. Measured consequences, local loopback:

- publishing one instance takes **~15 s**, which is the dominant term in travel
  latency (ticket to READY measured at 14.5 s);
- each live instance database carries that module on disk;
- the provisioner's management-call timeout had to grow a publish-specific
  budget, because 30 s was uncomfortably close to the upload time.

The fix is not to trim content: it is to stop the duplication. Flipping the
payload chain in `open_world_scene.rs` from `const` to `static` (payload, then
profile, then the profile table) should remove 2–4 copies and take the artifact
to roughly its ~190 MB floor. That refactor touches the LOS/collision data path,
so it was deliberately left out of this change.

## 7. Known follow-ups, deliberately out of scope

- **Nothing persists out of an open world.** `hub-server` stores identity,
  display name, and loadout *choices* only — its own comment
  (`hub-server/src/lib.rs:92`) states that item instances and simulation state
  deliberately stay out of the Hub. XP, inventory, currency, and loot earned in
  an instance are destroyed with it. Accepted for now; revisit if open world
  becomes progression-bearing.
- **Survival mode is deprecated and going away** (owner, 2026-08-18). It has
  the identical defect (`RequestSurvival` -> `StartSurvivalRun` on the null
  gameplay connection), and it is deliberately NOT being put on this ticket
  path. Do not spend work on it.
- `ARENA_PROVISIONER_MAP_ID` remains the PvP default; only the open-world kind
  reads its destination from the ticket.
