# Unranked 2v2 Bot Match — Compact Design

Date: 2026-08-11

Status: APPROVED AND IMPLEMENTED (manual play acceptance pending)

## 1. Purpose

Make the existing Hub launch one real, server-authoritative match:

```text
Team 0: local player + stationary ally dummy
Team 1: stationary enemy dummy + stationary enemy dummy
```

The request is immediate and unranked. It is not matchmaking: there is no
queue, search, reservation service, or second human client in this slice.

## 2. Scope

Included:

- remove the runtime-created in-game `LobbyController` menu;
- make the Hub's 2v2 flow call the server;
- create and join the local player to a logical arena instance atomically;
- create a four-slot, two-team roster;
- spawn three stationary player-shaped dummy participants;
- run the existing three-second countdown and arena scene transition;
- enforce ally/hostile targeting from the match roster;
- end the match when one team has no living participants;
- show local victory/defeat and the existing match statistics;
- return to Hub and remove all transient bot-match state;
- abort and clean up the match if the human disconnects.

Excluded:

- physical SpacetimeDB creation, routing, or deletion;
- real matchmaking or additional human participants;
- ranked play, ratings, rewards, or progression writes;
- 3v3 and 10v10;
- bot movement, attacks, decision-making, or loadout AI;
- team/party social UI and new team frames;
- rematch-in-place, reconnect-to-match, spectators, surrender, or backfill;
- networking, replication-rate, commitlog, or spatial-index optimization.

The first implementation continues to use one logical `ArenaInstance` inside
the current development database. Disposable physical databases are a later
infrastructure layer and must not be mixed into this gameplay slice.

## 3. Existing pieces to retain

- `ArenaInstance`, `PlayerWorld`, the `COUNTDOWN -> IN_PROGRESS -> ENDED`
  lifecycle, and the authoritative transition to `Arena_Map_01` remain.
- `spawn_actor_bundle` / `despawn_actor_bundle` remain the only owners of a
  dummy's player, physics, input, state, resource, and transient rows.
- `PlayerState.is_dummy` keeps stationary actors out of live-player prediction
  writes; unchanged dummy physics is already write-gated.
- `MatchParticipantStats` remains the scoreboard source.
- The existing three-second countdown remains unchanged.

Training actors and playground targets are references for deterministic actor
identity and lifecycle handling only. A competitive participant must not be
recorded as a `PlaygroundTarget` or inserted into a social `Party`.

## 4. Data model

### 4.1 `ArenaMatch`

Add one public row per competitive arena:

```text
ArenaMatch
  instance_id: u64              primary key
  queue_kind: string            "UNRANKED"
  format: string                "2V2"
  ruleset: string               "TEAM_ELIMINATION"
  team_size: u8                 2
  human_owner: Identity         caller that owns this bot session
  winner_team_id: Option<u8>    None until ended; None also represents a draw
```

`ArenaInstance` continues to own phase, seed, participant count, countdown
time, and ended time. Its legacy `winner_id` remains available for legacy
free-for-all matches and is `None` for team matches.

### 4.2 `MatchParticipant`

Add one public row per roster slot:

```text
MatchParticipant
  identity: Identity       primary key
  instance_id: u64         btree index
  team_id: u8              0 or 1
  team_slot: u8            0 or 1
  is_bot: bool
```

The server validates uniqueness of `(instance_id, team_id, team_slot)` before
insertion. The identity primary key prevents one actor from belonging to two
matches. Elimination is not duplicated here; `PlayerState.alive` and
`PlayerState.eliminated` remain canonical.

The instance-scoped subscription includes both new tables. Client combat
relations consult the roster before the generic `is_dummy => hostile` rule.

## 5. Server contract

Expose one fixed-purpose reducer with no client-authored roster data:

```text
start_unranked_2v2_bot_match()
```

Preconditions:

- caller has the normal connected player/physics/state rows;
- caller is not already in an instance;
- caller has no existing `MatchParticipant` row.

Within one transaction the reducer:

1. creates an `ARENA` instance with `max_players = 4`;
2. inserts its `ArenaMatch` row;
3. assigns the caller to team 0, slot 0;
4. moves and resets the caller in the new instance;
5. spawns team 0 slot 1 and team 1 slots 0 and 1 as dummy actor bundles;
6. inserts all four `MatchParticipant` rows;
7. sets `ArenaInstance.player_count = 4`;
8. changes the phase directly to `COUNTDOWN` and stamps
   `countdown_started_at`;
9. commits, allowing the existing subscriptions and world coordinator to load
   `ArenaMatch`.

Any validation or spawn failure returns `Err`. Reducer atomicity then leaves no
arena, roster, bot, or partial player move behind.

Dummy identities are deterministic from `(instance_id, team_id, team_slot)`
under a bot-specific identity namespace. Each candidate is collision-checked
against existing `Player` and `MatchParticipant` rows before use.

## 6. Spawn and actor rules

Use four deterministic slots in the current central flat area:

| Team | Slot | Desired X/Z | Facing |
|---|---:|---:|---:|
| 0 | 0 (human) | `(-5, -2)` | toward +X |
| 0 | 1 (ally bot) | `(-5, 2)` | toward +X |
| 1 | 0 (enemy bot) | `(5, -2)` | toward -X |
| 1 | 1 (enemy bot) | `(5, 2)` | toward -X |

All desired points pass through the existing authoritative spawn/collision
resolver. Bots use normal player hit dimensions and `DEFAULT_MAX_HP`, zero
velocity, no commands, and `is_dummy = true`. They receive ordinary replicated
player presentation and can be damaged, healed, killed, and included in match
statistics. They never move, attack, cast, or respawn.

## 7. Relations and match result

For two rostered actors in the same `ArenaMatch`:

- same identity => `Self`;
- same `team_id` => `PartyAlly`;
- different `team_id` => `Hostile`.

This roster rule precedes playground, generic dummy, arena-hostile, and social
party fallbacks on both server and client. Friendly fire therefore remains
disabled through the existing target-audience rules; assistable effects can
target the ally dummy.

After a roster participant is eliminated, determine which teams still have at
least one participant whose `PlayerState` is alive and not eliminated:

- both teams alive: continue;
- exactly one team alive: set `ArenaInstance.phase = ENDED` and write that team
  to `ArenaMatch.winner_team_id`;
- neither team alive in the resolving transaction: end as a draw with
  `winner_team_id = None`.

Snapshot HP/stat rows for all four roster identities at match end. Team matches
must not use the legacy last-surviving-identity conclusion path.

## 8. Hub and scene flow

The Hub becomes honest about the implemented capability:

- force the queue label to `UNRANKED`;
- enable 2v2 only;
- disable 3v3 and 10v10 with `COMING SOON` presentation;
- replace fake searching/cancel behavior with a pending request state;
- label the confirmation action `START 2V2 BOT MATCH`;
- while pending, disable the relevant controls;
- on reducer failure/out-of-energy, show the reason and restore the controls;
- on commit, close the dialog and wait for the authoritative `PlayerWorld`
  update to trigger the `Arena_Map_01` map transition.

`LobbyController.cs` and its `.meta` file are removed. This removal applies to
the runtime in-game menu only. The existing create/list/join/start reducers are
not removed by this slice, although the Hub no longer calls them.

## 9. End, return, and cleanup

The end overlay uses the local participant's team and
`winner_team_id` to display `VICTORY`, `DEFEAT`, or `DRAW`. Winning status in
the statistics list applies to every member of the winning team rather than a
single identity.

For this first slice the overlay exposes one action: `RETURN TO HUB`.
`Play Again` is removed or hidden; starting another match happens from Hub.

Return flow:

1. set a client-side return-to-Hub transition guard;
2. call the existing `leave_instance` reducer;
3. server recognizes that the instance is an owned bot match and atomically:
   - restores the human to open-world state;
   - despawns all three bot actor bundles with delete-only world cleanup;
   - deletes all `MatchParticipant`, `MatchParticipantStats`, and `ArenaMatch`
     rows for the instance;
   - deletes the `ArenaInstance` row;
4. on reducer commit, queue the `Hub` scene;
5. on failure, clear the guard, remain on the end overlay, and re-enable return.

The guard prevents the intermediate authoritative `OPEN` `PlayerWorld` update
from queueing an open-world scene before the explicit Hub transition.

If the human disconnects at any phase, `client_disconnected` invokes the same
server teardown before removing the human actor bundle. Disconnect aborts the
session: no winner, rating, reward, or persistent match result is written.
Cleanup helpers must be idempotent so repeated leave/disconnect paths are safe.

## 10. Validation and exit gate

Automated checks:

- roster validation rejects duplicate identity and duplicate team slot;
- deterministic bot identities are distinct across all four slots and matches;
- server and client relation tests cover self, ally bot, and both enemy bots;
- team outcome tests cover continue, team 0 win, team 1 win, and draw;
- failed construction leaves no partial instance or roster state;
- return and disconnect cleanup remove all bot-owned/transient rows while
  preserving the human's persistent progression;
- Hub request-state tests cover committed, failed, out-of-energy, and reconnect;
- scene-decision tests prove return-to-Hub cannot be replaced by an open-world
  transition;
- generated bindings and ordinary C#/Rust builds pass;
- no Unity batch-mode command is used.

Manual acceptance:

1. From Hub, only Unranked 2v2 can be started.
2. One click creates the match and loads `ArenaMatch` after the countdown.
3. The ally dummy is assistable and not damageable by hostile-only actions.
4. Both enemy dummies are hostile and independently targetable.
5. Eliminating both enemies ends in Victory and shows all four stat rows.
6. Return to Hub removes the instance and all three bots.
7. Disconnecting during countdown, play, or the result screen leaves no orphan
   arena, roster, bot, or scoped presentation rows.
8. The old in-game Lobby button and window never appear.

Passing this gate completes the approved slice. It does not authorize 3v3,
10v10, bot AI, real matchmaking, physical database orchestration, or any other
follow-on phase.

## 11. Implementation verification

Implemented on 2026-08-11 within the scope above.

- Rust formatting and compilation pass with `projectile_load_harness` enabled.
- The three focused roster/outcome/identity tests pass.
- The ordinary C# runtime assembly and Edit Mode test assembly compile with the
  regenerated SpacetimeDB bindings.
- The broader Rust suite passes 663 of 668 tests. Its five failures are existing,
  out-of-scope inventory/melee authoring and random-dungeon content checks.
- Unity batch mode was not used. The eight manual play checks above remain the
  final in-Editor acceptance pass.
