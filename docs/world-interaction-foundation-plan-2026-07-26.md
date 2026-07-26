# World Interaction Foundation Plan

Date: 2026-07-26

Status: proposed implementation plan; no implementation is authorized by this
document

## Goal

Right-clicking a nearby dungeon door in play mode opens or closes it. An action
may be instant or may require a server-timed use/channel period with a progress
bar and actor animation. The same selection and request path must later support
chests, levers, shrines, and other world props without putting input handling
into every prop script.

This plan covers:

- right-click arbitration with the controls that already use that button;
- first-party authoring around third-party art;
- reusable client interaction discovery;
- instant and timed interaction lifecycle, cancellation, and progress UI;
- profile-driven actor use animations;
- server-authoritative door state and validation;
- client/server movement and line-of-sight collision parity;
- swing presentation for single- and double-leaf gateways;
- the extension seam for future interactable props.

This plan does not authorize implementation, package modification, new loot
rules, the gameplay rules for keys/locks, quests, traps, destructible doors,
automatic NPC door use, or runtime dungeon generation. A timed action can
represent a future unlock attempt, but key ownership, lock state, and success
rules require a separate approved design.

## Decisions

1. **One router owns world right-clicks.** Doors must not independently poll the
   mouse. Existing spell-aim cancel, camera orbit, combat targeting, and corpse
   looting are routed or consulted through the same arbitration path.
2. **A right-click is an action only on release after click classification.**
   Holding or moving the mouse beyond tunable time/pixel thresholds is a camera
   drag and never interacts.
3. **The generic layer stops at selection and request dispatch.** A door, chest,
   corpse, and lever can all be selected through the same client contract, but
   they keep type-specific authority, state, and reducer rules. Do not introduce
   one opaque generic target-state table. A shared transient row for the actor's
   currently running interaction is allowed; it records timing, not prop state.
4. **One lifecycle supports instant and timed use.** A duration of zero commits
   an accepted action immediately. A positive duration creates one
   server-authoritative active interaction, exposes its timestamps for UI and
   animation, and commits the target-specific effect only on completion.
5. **Timed world use and combat casting are mutually exclusive in V1.** A
   character has at most one authoritative channel. This prevents two progress
   bars, conflicting locomotion rules, and competing full-body animations.
6. **Interaction animations use their own profile/request seam.** They may share
   playback arbitration and layers with `PlayerAnimator`, but are not disguised
   as spells or `CombatAnimationRequest` events.
7. **Third-party assets remain immutable.** Arena-owned prefab variants or
   wrappers reference the package prefabs and add hitboxes, serialized leaf
   references, collision authoring, and runtime components.
8. **Doors are server-authoritative shared world state.** Right-click sends the
   desired state; clients animate only accepted/replicated state.
9. **Door collision is dynamic and binary in V1.** The server changes the
   logical doorway blocker atomically when the target-specific open/close effect
   commits. Swinging the visual leaf is presentation, not continuously simulated
   collision.
10. **Generated doors default open in V1.** This preserves the current traversable
   dungeon and makes initial rollout non-blocking. The exported definition owns
   the default so authored exceptions can be added later.
11. **Scripted transform motion is preferred over Animator Controllers.** The
   existing gateway leaves already have working pivots and reviewed open angles.
   A parameterized motor is simpler to reverse, synchronize, and reuse across
   single- and double-leaf doors.

## Existing constraints

Right-click currently has four world/gameplay meanings:

- hold: orbit the camera and align movement to camera yaw;
- release over a living combat target: select it and arm auto-attack;
- press during point-targeted spell aim: cancel aim;
- press over a corpse: request/open loot.

UI also owns right-click for inventory item actions. Independent listeners would
therefore cause double actions and door toggles after camera drags.

The random dungeon is generated at editor time and exports matching static
collision payloads to Unity Resources and `server/src/world_data`. The current
gateway renderer rotates leaves open and disables their colliders before that
export. A closeable door cannot be added back only in Unity: it needs a shared
definition and dynamic collision on both client and server.

The existing replicated `ActiveWorldObstacle` path proves that oriented dynamic
boxes can participate in client prediction, server movement, NPC movement, and
combat line queries. Door state should share/refactor that geometry machinery,
not reuse the spell-specific expiring obstacle table.

## Runtime shape

### Pointer routing

Add a scene/runtime `WorldPointerInteractionRouter` that receives a logical
secondary-world-action press, hold delta, release, and pointer position from
`LocalPlayerInputSource`. Its current desktop binding is right mouse; prop code
must not depend directly on that physical binding.

On right-button press:

1. UI gets first refusal.
2. Active spell aim cancels and marks the gesture consumed.
3. Otherwise the router begins a possible click and accumulates duration and
   pointer delta while the button is held.

On release:

1. A consumed, over-time, or over-distance gesture ends with no world action.
2. Candidate providers resolve visible objects under the cursor and return
   world depth, interaction point, and requested action.
3. The nearest unobscured candidate wins. Explicit tie-breaking preserves
   corpse/combat behavior when an entity overlaps a prop on screen.
4. The router rejects candidates outside the local interaction distance before
   dispatch. The server independently repeats range and world checks.

`TargetSelector` and `InventoryScreen` should expose candidate/action methods and
stop polling right-click for world actions in their own `Update` methods. UI
element right-click remains inside UI.

### Generic client contract

Use a small contract under `Assets/Arena/Runtime/Interaction`, conceptually:

- `IWorldInteractable`: supplies stable ID, interaction point, current verb,
  local eligibility, and request dispatch;
- `WorldInteractionHitbox`: maps a simple pick collider back to an interactable
  root;
- `WorldInteractionCandidate`: target, hit/depth data, verb, and priority;
- `WorldPointerInteractionRouter`: gesture classification, UI/aim arbitration,
  candidate choice, range checks, and single dispatch.

Unity serialization should remain on concrete `MonoBehaviour` components.
Interfaces are runtime contracts, not serialized asset references.

Each prop type supplies its own adapter:

- `DoorInteractable` requests the desired open/closed state;
- a future `ChestInteractable` requests container access;
- corpse looting adapts the current `OpenLootNpc` path;
- combat targeting adapts the current select/auto-attack path.

### Instant and timed interaction lifecycle

Each concrete verb resolves a shared `WorldInteractionProfile`, identified by a
stable profile ID. Its authority-facing data includes:

- duration in milliseconds (`0` means instant);
- localized progress label key, such as `OPENING` or `UNLOCKING`;
- animation presentation profile ID;
- whether the actor must remain grounded and stationary;
- cancellation flags for movement/displacement, damage, death/world change,
  range or line-of-access loss, and conflicting combat actions.

The paired client/server export owns duration and cancellation rules. A
first-party Unity presentation asset with the same profile ID owns clips,
layers/masks, facing behavior, and progress-bar styling. Server rules must never
be inferred from an animation clip's length.

Normal unlocked open/close profiles default to duration `0` for responsive door
use. A configured timed profile proves the same path without adding lock rules.
A future locked door would advertise an `UNLOCK` verb/profile and apply its
door-specific permission checks before the timed action begins.

V1 timed-use defaults are deliberately strict: the actor must be alive,
grounded, stationary, in range, and have line of access. Movement, displacement,
damage, death, world change, a conflicting combat action, or a changed target
revision cancels the action. The server rechecks all eligibility at completion.
Specific future profiles can relax a rule only when the corresponding gameplay
design explicitly requires it.

Add a scoped public `ActiveWorldInteraction` row with at most one row per actor:

- actor identity as the lookup/uniqueness key;
- unique action instance ID;
- target kind plus definition/state identity;
- requested verb/desired state and observed target revision;
- interaction profile ID and world/instance scope;
- authoritative `started_at` and `completes_at`.

This is transient actor action state, not a generic replacement for
`WorldDoorState`, container data, or any other prop's state. A type-specific
begin reducer validates the request. Subscriptions expose only interactions in
the client's relevant world/instance, which is sufficient to animate nearby
remote actors. For a door, conceptually:

```text
begin_world_door_action(
  door_definition_id,
  desired_open,
  observed_revision
)
```

An instant accepted request applies the idempotent door state change without
creating a progress row. A timed request creates the row but does not change the
door or collision. The authoritative game tick revalidates and applies the
target-specific effect exactly once when time expires, then removes the row.
Cancellation removes it without applying the effect. An explicit cancel request
is available to Escape/UI; movement and other server-observed conditions do not
depend on a client cancel message.

Starting a world interaction while combat casting is rejected. Movement or a
new combat action cancels a timed world interaction before the new action is
accepted. A second prop request while one is active is rejected as busy in V1,
avoiding accidental target switching from a stray right-click.

### Shared progress presentation

Reuse the existing HUD cast-bar visual shell, but do not put world interaction
state into `LocalCombatState` or pretend it is an `ActiveCast`. Extract a small
timed-action presentation contract containing start time, end time, label, and
style. Combat casting and `LocalInteractionState` supply adapters to that
contract, and a single HUD presenter renders the active snapshot.

The server's timestamps determine progress; local time only interpolates the
display. A zero-duration action never flashes a progress bar. Completion,
cancellation, or rejection clears the interaction bar, while the existing spell
cast bar retains its current behavior. Mutual exclusion is enforced by the
server as well as the presenter, so UI arbitration cannot hide an illegal
overlap.

### Actor interaction animation

Create a first-party `WorldInteractionAnimationProfile` per reusable motion,
with optional start, looping use, end, and cancellation clips plus full-body or
upper-body playback, target-facing, blend, and movement-lock settings. Other
verbs select different profiles without changing the generic interaction code.

For the initial humanoid use profile, extract standalone Arena-owned `.anim`
clips from:

```text
Assets/ThirdParty/AssetStore/Characters/StylizedCharacter/Animations/Character/
Human/Male/A_Hu_M_BasePack.fbx
```

The FBX contains the intended phased set: `Emote_Use_Start` is non-looping,
`Emote_Use_Loop` is looping, and `Emote_Use_End` is non-looping. Do not edit the
FBX, its `.meta`, or embedded clips. The existing extractor's default source
root does not include this character directory, so the implementation must add
a targeted extraction path or extend that tool and write results under
`Assets/Arena/Content/Animation/Extracted/`. Retarget/import warnings on the FBX
make visual review on the production avatar an exit gate.

Replicated active-interaction timestamps drive animation for local and remote
actors:

1. accepted start: optionally face the target and play the start clip;
2. active duration: enter/hold the loop after start;
3. successful completion: play the end clip and let the prop effect replicate;
4. cancellation: play the profile's cancel/end transition or blend out.

Short actions may omit the loop; instant actions may use a brief one-shot
without showing the progress bar. Animation never authorizes completion and
uses no authoritative root motion. A dedicated interaction animation request
must participate in the existing player animation priority/arbitration so
death, hit reaction, locomotion, and combat do not stomp each other
unpredictably. Late subscribers derive the appropriate phase from the replicated
timestamps rather than replaying the start from zero.

## Asset ownership and authoring

Do not move, duplicate, or edit the FBX files and prefabs in
`Assets/ThirdParty/AssetStore/Environments/FantasticDungeonPack`.

Create Arena-owned variants under:

```text
Assets/Arena/Content/Prefabs/Dungeons/FantasticDungeon/Interactables/Gateways/
```

Use a Prefab Variant when the package hierarchy and pivot are sufficient. Use an
Arena wrapper with the vendor prefab nested inside only when a corrected pivot,
physics hierarchy, or stable root is required. Suggested names end in
`_Arena.prefab`.

An interactive gateway variant owns:

- `DoorAuthoring`/`DoorInteractable` on its Arena root;
- serialized leaf transforms rather than runtime name searches;
- closed and open local rotations for every leaf;
- a simple dedicated interaction hitbox;
- a simple closed-door blocker box used for shared export;
- configurable interaction anchor/range and default state;
- open and close interaction profile IDs;
- optional audio references/hooks, with no audio required for the first exit
  gate.

Keep the masonry/frame static. Clear static flags on every animated renderer and
leaf. Do not rely on the vendor large-door non-convex mesh colliders as moving
gameplay collision. Any leaf collider retained for picking or presentation must
be on a non-gameplay layer; the exported binary blocker is the only movement,
LOS, and projectile collision for the door leaf.

Initial supported styles:

- medium metal: one leaf, current reviewed `+95` degree opening;
- medium wood: one leaf, current reviewed `+95` degree opening;
- large wood: two leaves, current reviewed `+100/-100` degree openings;
- barred gateway: one leaf, current reviewed `-75` degree opening.

Open arches remain non-interactable. Large-door barricade planks remain hidden;
locked/barricaded behavior is future scope.

## Door definition export and identity

Environment asset extraction is not required, but generated door metadata
**does** need an export because the authoritative server cannot inspect Unity
prefabs. The separate character-animation extraction described above creates
first-party clips without relocating the source FBX.

Extend the editor-time dungeon output with a versioned, deterministic shared
door-definition manifest written byte-identically to:

```text
Assets/Arena/Resources/SharedData/Worlds/random_dungeon.doors.shared.json
server/src/world_data/random_dungeon.doors.shared.json
```

Also export the authority-facing `WorldInteractionProfile` catalog
byte-identically to the corresponding client Resources and server world-data
locations. Unity-only presentation assets reference those records by stable
profile ID rather than being copied to the server.

Each definition contains only authority/collision data:

- stable `door_definition_id`;
- world/scene definition key;
- authoritative interaction anchor and maximum distance;
- closed blocker center, size, and rotation;
- default-open state;
- open and close interaction profile IDs;
- optional definition revision/version.

The definition ID comes from logical placement identity (scene/layout identity plus
doorway edge or connection identity), not visual style or hierarchy name. A
material/style change must not create a different logical door.

The generated leaf collider is always excluded from immutable movement/query
collision. The frame remains in normal static collision. Export validation fails
if:

- an interactive door lacks a stable unique ID;
- its interaction anchor or blocker is non-finite/degenerate;
- an animated leaf is still marked static;
- its leaf appears in the immutable collision payload;
- client/server manifests differ.

## Server authority and state

Add a scoped public `WorldDoorState` table with, at minimum:

- instance-qualified `door_state_id` primary key;
- `door_definition_id`;
- world scope (`OPEN` scene or instance identity);
- desired/current `is_open`;
- monotonically increasing `revision`;
- `updated_at`.

The manifest identifies an authored placement. The state ID additionally
includes the resolved world instance when the same definition can appear in
multiple instances. For the current open-world random dungeon there is one state
per definition.

Expose an idempotent desired-state begin reducer:

```text
begin_world_door_action(door_definition_id, desired_open, observed_revision)
```

instead of a blind toggle. The server:

1. resolves the caller's authoritative world and player position;
2. resolves `door_definition_id` from the compiled shared manifest and derives
   the caller's scoped state ID;
3. verifies same world, interaction distance, and line of access;
4. rejects a stale revision or returns success when the requested state already
   matches;
5. resolves the open/close interaction profile and rejects a conflicting combat
   cast or active world interaction;
6. for an instant profile, applies the state change immediately;
7. for a timed profile, creates `ActiveWorldInteraction` without changing the
   door, then revalidates and applies the state on authoritative completion;
8. before every close commit, rejects if a player or NPC capsule occupies the
   doorway clearance volume;
9. materializes the manifest's default state if no scoped row exists, then
   updates the row and revision exactly once.

V1 collision changes atomically with the committed state, never at the start of
a timed interaction:

- open: no door blocker;
- closed: closed blocker participates in player movement, local prediction, NPC
  movement, projectile/world queries, and line of sight.

This deliberately avoids pretending that the server simulates the rotating
leaf. The client swing lasts a short authored duration and is visual feedback.

Add scope-filtered door-state subscriptions. A late join or scene reconnect
snaps each registered door to the current authoritative state; subsequent row
updates animate.

## Door presentation

`DoorMotor` receives authoritative target state and drives one or more leaf
transforms between cached local rotations with a configurable duration and
easing curve.

Required behavior:

- starts at the replicated state without playing a startup animation;
- opens/closes once per new revision;
- can reverse cleanly if a newer accepted state arrives mid-animation;
- never changes server state itself;
- keeps interaction available while moving, but the server revision check
  resolves races;
- exposes hooks for later open, close, latch, and denial sounds.

The local click may show immediate pending feedback, but V1 does not mutate
logical collision or commit the visual target until the authoritative update
arrives. For timed actions, the actor animation and progress bar begin from the
replicated active-interaction row; the leaf starts moving only after successful
completion updates `WorldDoorState`. Prediction can be added later only if
latency warrants it.

## Future props

The reusable parts for a chest are already present after this foundation:

- right-click gesture arbitration;
- candidate selection and hitboxes;
- interaction range/world validation pattern;
- instant/timed lifecycle, cancellation, progress bar, and animation profiles;
- scoped server subscriptions;
- authoritative request/replication boundary;
- parameterized transform motor pattern for a lid.

Chest contents can build on the existing `InventoryContainer` and
`InventorySlot` tables. Chest-specific work still needs an explicit choice
between shared loot, per-player loot, respawn/reset behavior, and whether lid
state is shared independently from contents. Those choices should not be baked
into the generic interaction interface or door table.

Levers, shrines, switches, and similar props add concrete interactable adapters
and type-specific state/reducers while reusing the router, hitbox, timing, UI,
and actor-animation contracts.

## Implementation slices

Implement and review one slice per commit. Do not enable production closeable
doors until the authoritative collision slice is present.

### Slice 1 — right-click routing foundation

- Add gesture classification and the central router.
- Add shared UI pointer blocking for uGUI and UI Toolkit.
- Adapt aim cancel, combat target/auto-attack, and corpse loot dispatch.
- Preserve camera orbit/alignment and inventory item right-click behavior.
- Add unit tests for click/drag thresholds, consumption, depth/priority, range,
  and exactly-one-dispatch behavior.

Exit gate: existing right-click behaviors pass automated tests and manual play
checks; a camera drag never dispatches a world action.

### Slice 2 — door authoring and deterministic export

- Add concrete door authoring, interaction hitbox, and motor components.
- Author first-party variants for the reviewed gateway styles.
- Add first-party interaction/animation profile authoring and deterministic
  authority-data export.
- Extract the three humanoid use clips to first-party `.anim` assets through a
  targeted, repeatable editor tool; do not modify the vendor FBX or `.meta`.
- Give generated gateway placements stable logical IDs.
- Add the paired shared door-definition manifest and validation.
- Explicitly exclude dynamic leaves from static movement/query export while
  retaining frame collision.
- Keep generated doors open/non-interactive in the production scene until Slice
  3 authority exists.

Exit gate: identical client/server manifests and interaction profile data,
stable IDs across rebuilds of the same seed, no vendor asset modifications, no
door leaves in immutable collision, and the extracted humanoid use profile
retargets correctly on the production avatar.

### Slice 3 — server timing, door state, and dynamic collision

- Add manifest/profile loading, `WorldDoorState`, `ActiveWorldInteraction`, the
  idempotent begin/cancel/completion lifecycle, and tests.
- Add world/range/revision, cancellation, mutual-exclusion, and occupied-doorway
  validation.
- Refactor/share oriented-box collision helpers with the existing dynamic
  obstacle implementation.
- Apply closed door blockers to server/client player movement, NPC movement,
  LOS, and projectile queries.
- Add generated bindings and scope-filtered client subscription.

Exit gate: instant actions commit immediately, timed actions commit once only
after completion, cancellation never applies the effect, and server/prediction
tests agree on open/closed traversal and line hits. Stale, distant, wrong-world,
busy, and occupied close requests are rejected.

### Slice 4 — generated-door activation and presentation

- Bind generated instances to replicated state by stable ID.
- Replace the current permanent `OpenStaticGatewayLeaf` behavior for supported
  styles with authoritative `DoorInteractable`/`DoorMotor` behavior.
- Route a valid right-click candidate to `BeginWorldDoorAction`.
- Refactor the existing cast-bar visuals behind the shared timed-action
  presenter and feed replicated interaction timestamps through
  `LocalInteractionState`.
- Add the dedicated interaction animation request/profile path and integrate it
  with player animation priority, remote replication, cancellation, and late
  subscription.
- Add minimal hover/denial feedback if needed to make selection legible.
- Leave open arches unchanged.

Exit gate: two clients see the same door and actor interaction state;
reconnect/late join is correct; instant actions do not flash a bar; timed
actions show authoritative progress and start/loop/end or cancel animation;
single and double doors animate; rapid/concurrent clicks do not desynchronize.

### Slice 5 — end-to-end validation

Automated checks:

- router consumption and exactly-one action;
- interaction duration-zero fast path, authoritative timing, completion
  idempotence, and every default cancellation condition;
- combat-cast/world-interaction mutual exclusion;
- stable manifest identity and duplicate rejection;
- door state reducer validation and idempotence;
- client/server dynamic blocker parity;
- leaf static/collision-export validation;
- state registration, late join, revision ordering, and motor reversal;
- timed-action presenter selection and cleanup;
- animation phase selection for start, loop, completion, cancellation, remote
  actors, and late subscription.

Manual Unity play-mode matrix:

- right-click tap on each supported gateway opens/closes;
- right-button drag only orbits and aligns movement;
- spell aim right-click only cancels aim;
- hostile target right-click still selects/arms attack;
- corpse right-click still opens loot;
- UI right-click never leaks to the world;
- too-distant and occluded door clicks do nothing or show denial;
- instant door use shows no progress bar;
- a configured timed door use shows the correct label and server-timed progress;
- movement, displacement, damage, death, range/LOS loss, and a conflicting
  combat action cancel timed use without changing the door;
- the humanoid use start/loop/end sequence plays on local and remote avatars,
  cancels cleanly, and does not override death/hit/combat incorrectly;
- player/NPC doorway occupancy prevents closing;
- movement, projectiles, and LOS agree with open/closed state;
- two clients, reconnect, and scene reload remain synchronized.

Unity validation is interactive Editor work. Do not run Unity with
`-batchmode`.

## Definition of done

The foundation is complete when:

- every world right-click has one owner and one result;
- camera drag and UI interaction cannot toggle props;
- package assets remain untouched and Arena-owned variants are reviewable;
- generated door identity and authority geometry are deterministic and shared;
- the server validates and replicates door state;
- instant and timed use share one authoritative lifecycle, with correct
  cancellation and exactly-once completion;
- the HUD presents either a combat cast or world interaction from authoritative
  timing without conflating their state;
- actor use animations are profile-driven, replicated, and kept outside vendor
  asset metadata;
- client prediction and server collision agree;
- all supported dungeon gateways animate correctly for local and remote clients;
- a future chest can plug into the router without changing door code or creating
  another mouse listener.
