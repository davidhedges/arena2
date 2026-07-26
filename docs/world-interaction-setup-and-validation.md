# World Interaction Setup and Validation

This is the operator handoff for the world-interaction foundation described in
`docs/world-interaction-foundation-plan-2026-07-26.md`.

The runtime and authority code are implemented. The checked-in `RandomDungeon`
scene still needs one normal Unity Editor rebuild before its generated gateway
instances contain the new components and before the extracted humanoid-use
assets exist. This project must not be rebuilt with Unity batch mode.

## Asset ownership

Do not move, duplicate, or edit the source assets under:

```text
Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack/
Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/
```

Interactive gateway variants, extracted clips, profiles, scene components, and
manifests are Arena-owned. The editor pipeline keeps them under
`Assets/Arena/Content` or `Assets/Arena/Resources` and references vendor source
assets by GUID. A package update can therefore replace vendor content without
mixing gameplay state into the package directory.

## One-time Unity activation

Before running this operation, leave Play Mode and save unrelated scene work.
In the Unity Editor, run:

```text
Arena > Interaction > Rebuild Approved Foundation Assets
```

The command:

1. reads the checked-in random-dungeon seed;
2. extracts `Emote_Use_Start`, `Emote_Use_Loop`, and `Emote_Use_End`;
3. creates the `HUMANOID_USE` presentation profile under Resources;
4. builds four Arena-owned single/double gateway templates;
5. rebuilds `RandomDungeon` with production-enabled `DoorAuthoring`,
   `DoorInteractable`, `DoorMotor`, and trigger hitboxes;
6. regenerates paired client/server door, interaction, and collision data; and
7. runs `WorldInteractionFoundationValidator`.

The validator is also available independently:

```text
Arena > Interaction > Validate Checked-In Foundation
```

It verifies paired manifest equality/schema/IDs, required profiles and clips,
all four Arena gateway templates, production scene IDs/components, leaf static
flags, trigger hitboxes, and the absence of enabled solid leaf colliders.

## Server publication

Door definitions and collision data are compiled into the SpacetimeDB module.
After rebuilding the Unity assets, rebuild and republish the server before
testing. The repository workflow is:

```bash
ops/republish-local-clear.sh
```

That command clears the local `arena` database by default. To request a
data-preserving publish instead:

```bash
ARENA_DELETE_DATA=never ops/republish-local-clear.sh
```

Choose the data policy deliberately. The script also regenerates canonical C#
bindings; let Unity import/recompile the result before entering Play Mode. Do
not publish to a remote database as part of this setup unless that environment
is explicitly in scope.

## Automated checks

Run the following Edit Mode fixtures from Unity Test Runner:

```text
Arena.EditModeTests.WorldPointerInteractionTests
Arena.EditModeTests.WorldInteractionManifestTests
Arena.EditModeTests.WorldInteractionPresentationTests
```

They cover gesture consumption/exactly-one dispatch, depth/priority/range,
stable deterministic manifests, duplicate rejection, client blocker behavior,
authoritative progress selection, zero-duration suppression, start/loop
late-subscription timing, door revision ordering/reversal, single/double leaves,
and door snapshot-versus-live replication.

Server coverage is run from `server/`:

```bash
cargo test world_interactions
cargo test actor_lifecycle
cargo test game_loop::tests::tick_profile
```

The full server suite may also be run with `cargo test`. At implementation
handoff, the focused interaction tests pass; known unrelated full-suite
failures are recorded in the final implementation report rather than hidden.

## Manual play matrix

Use two clients connected to the same freshly published database for the
replication rows. One can be the Unity Editor and the other a normal
development player.

### Instant door behavior

- Tap right-click on every supported medium, large/double, and barred gateway.
  Both clients should see one swing and the same final state.
- Close and reopen each door. Movement, LOS, and projectiles should block only
  while the authoritative state is closed; the visual swing itself never owns
  collision.
- Rapid-click and concurrent-click from both clients. Revisions may reject a
  stale request, but clients must converge without a visual or collision split.
- Join/reconnect while a door is closed. The door must snap to the replicated
  baseline instead of replaying an old swing.
- Try to close a doorway occupied by a player and by an NPC. The server should
  reject it and show the denial text.
- Try from outside interaction range. No reducer effect should occur and local
  denial feedback should be visible.

### Right-click arbitration regressions

- Hold/drag right-click: only camera orbit/alignment occurs.
- Right-click during point-targeted spell aim: only aim cancellation occurs.
- Right-click a living combat target overlapping a prop: priority/depth chooses
  one action.
- Right-click a corpse: loot still opens exactly once.
- Right-click an inventory/UI element: UI gets first refusal and no world action
  fires.

### Timed proof configuration

Normal unlocked doors intentionally use `WORLD_DOOR_INSTANT`. To exercise the
timed path without inventing lock/key rules:

1. choose one generated production door in `RandomDungeon`;
2. temporarily set either its open or close interaction profile ID to
   `TIMED_HUMANOID_USE`;
3. run `Arena > Dungeons > Export Active World Interactions`;
4. republish the local server; and
5. test, then restore the door to `WORLD_DOOR_INSTANT`, export, and republish.

For that temporary timed door:

- the existing cast-bar shell should show `USING` for authoritative server time;
- local and remote actors should enter the current start/loop phase;
- successful completion should play `Emote_Use_End` and then replicate the door;
- Escape, movement/displacement, damage, death/world change, lost range/access,
  a conflicting combat action, or a target revision change should remove the
  bar and cancel/blend out without changing the target;
- joining mid-use should start at the current phase, not replay the start; and
- an instant action must never flash the progress bar.

Visually review the extracted clips on the production avatar. In particular,
confirm humanoid retargeting, the `Emote_Use_Loop` import loop setting, full-body
layer behavior, target-facing, start-to-loop and loop-to-end blends, and that
combat/death/hit/dodge presentation preempts use animation.

## Adding future props

Future chests, levers, shrines, and similar props should reuse:

- `IWorldInteractable`, `WorldInteractionHitbox`, and the central pointer router;
- an exported `WorldInteractionProfile` for timing/cancellation;
- `LocalInteractionState` and the shared timed-action HUD;
- `WorldInteractionAnimationProfile` for optional actor motion.

Each prop still needs its own state table and authoritative reducer. Do not put
input polling on the prop, add state to the third-party prefab, or turn
`ActiveWorldInteraction` into an opaque generic prop-state table.
