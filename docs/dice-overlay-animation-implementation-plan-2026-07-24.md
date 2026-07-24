# Dice Overlay Animation — Implementation Plan

Status: proposed plan, awaiting approval. This document does not authorize any
implementation work by itself.

Design source:
[`dice-overlay-animation-design-spec-2026-07-24.md`](dice-overlay-animation-design-spec-2026-07-24.md)

## Execution rule

The phases below are linear. Each phase is one reviewable implementation and
ends at its exit gate. Stop after each gate for review; do not begin the next
phase automatically.

Phase 3 changes the SpacetimeDB schema and requires a canonical binding
regeneration. Its local publish/reset operation is destructive to the local
development database and requires explicit approval when that phase begins.

Unity must not be run with `-batchmode`. Unity asset generation, EditMode tests,
play-mode review, and capture happen through the open Editor.

## Scope

This plan delivers:

- one polished medieval-fantasy resin dice set
- d4, d6, d8, d10, d12, and d20
- one active die at a time
- authoritative uniform server-generated results
- deterministic presentation of the supplied result
- three visual roll paths
- transparent screen overlay with a fixed camera
- skip-to-result and hold-until-dismiss behavior
- ordinary, positive-maximum, d20 `1`, and d20 `20` treatments
- an editor/development review surface so rolls can be seen without a future
  reward or event screen

This plan does not deliver:

- reward, event, inventory, progression, or combat outcome rules
- integration with a future reward screen
- groups of simultaneous dice
- percentile dice
- additional skins or materials
- sound
- haptics
- reduced-motion behavior
- mobile support
- publicly verifiable or real-money randomness

## Architecture

```text
SpacetimeDB reducer RNG
        |
        v
ActiveDiceRoll row (one per owner)
        |
  local-player subscription
        |
        v
DiceRollNetworkBridge
        |
        v
ResolvedDiceRoll request ----------------------+
        |                                      |
        v                                      |
DiceOverlayPresenter <--- local review harness-+
        |
        +--> authored motion profile
        +--> die definition + face pose
        +--> ordinary/special result effect
        |
        v
fixed 3D camera -> transparent texture -> UI Toolkit overlay
```

The local review harness may supply a forced value directly to the presenter.
That path is explicitly cosmetic and cannot affect game state. Consequential
rolls always enter through the authoritative server path.

### Ownership boundaries

#### Server result authority

The server:

- validates the requested die
- generates the uniform value
- enforces one active roll per player
- makes repeated requests idempotent
- records the resolved value before presentation
- deletes the active row when the owner dismisses it

The server does not know about cameras, motion paths, materials, face
orientation, effects, or overlay layout.

#### Network bridge

The bridge:

- subscribes only to the local player's active roll
- converts the generated binding into a network-neutral presentation request
- shows a row on insert/materialization
- does not replay an unchanged row during the same connection
- forwards dismissal to the server
- never generates or alters a result

#### Presenter

The presenter:

- accepts only an already-resolved die type and value
- chooses cosmetic motion independently of the value
- aligns the correct authored face toward the fixed camera
- owns skip, hold, and dismiss presentation state
- has no dependency on SpacetimeDB types

#### Host UI

No production host screen is added in this plan. The presenter exposes a small
host-facing API for future UI:

- show a resolved roll
- skip to its final pose
- dismiss the current overlay
- query whether the overlay is active

The editor/development review surface exercises that same API.

## Authoritative roll contract

### Active row

Add one public `ActiveDiceRoll` row per owner. The owner identity is the primary
key, preventing abandoned requests from accumulating unbounded rows.

The row contains only what the current single-die scope needs:

- owner
- client request identifier
- die side count
- resolved value
- creation timestamp

Do not add reward context, event type, totals, modifiers, success/failure,
rarity, or multiple-result fields in this plan.

### Preview request

Add a `request_dice_roll_preview(request_id, die_sides)` reducer solely to
exercise the dice system before real outcome-producing callers exist.

Contract:

- `die_sides` must be one of `4, 6, 8, 10, 12, 20`.
- `request_id` is non-empty, bounded, and restricted to the repository's
  established wire-safe identifier characters.
- If the owner already has the same request identifier and die type, return
  success without consuming RNG or changing the value.
- If the owner has a different active roll, reject until it is dismissed.
- Otherwise sample once with SpacetimeDB's bounded reducer-context RNG and
  insert the row.
- Never derive the result from request data, a timestamp, a hash, modulo
  arithmetic, Unity, or physics.
- This preview reducer produces no gameplay effect and must not later be reused
  as a reward/event authorization path.

Add `dismiss_dice_roll()`:

- it can delete only the caller's active row
- it is idempotent when no row exists
- local presentation may hide immediately while the reducer completes
- a failed dismissal leaves the authoritative row available on reconnect

### Future outcome callers

Future event or reward work must call the same bounded server roll helper inside
the reducer that commits the outcome, so the roll and its consequence remain
one transaction. That integration is not part of this plan.

## Dice asset contract

### Definition

Create a `DiceDefinition` asset for each supported shape. It contains:

- stable die identifier and side count
- die prefab
- presentation scale
- one face entry for every legal value
- each face's local outward normal
- each face's local upright direction

At runtime, the pose solver aligns the selected face normal toward the camera
and the selected upright direction toward camera-up. Final pose data lives in
the definition, not in switch statements or per-result animation clips.

Validation rejects:

- missing or duplicate results
- values outside `1..=sides`
- a face count different from the die's side count
- non-normalized or non-orthogonal face vectors
- missing prefab, material, font, or catalog references
- a final pose whose face normal or numeral-up direction misses tolerance

### Geometry

Add a deterministic editor authoring tool under
`Assets/Arena/Editor/Dice/`. It builds first-party mesh and prefab assets rather
than constructing dice every time the game runs.

The tool produces canonical convex geometry:

- d4: tetrahedron, four triangular faces
- d6: cube, six square faces
- d8: octahedron, eight triangular faces
- d10: pentagonal trapezohedron, ten kite faces
- d12: dodecahedron, twelve pentagonal faces
- d20: icosahedron, twenty triangular faces

Each die has:

- consistent overall visual scale
- softened/beveled outer edges
- a shallow recessed numeral area per face
- stable face normals and upright vectors exported into its definition
- no collider or rigidbody

The d20 is generated and approved first. The remaining topologies are not
generated until Phase 5.

### Numerals

- Build a static TextMeshPro SDF font asset from the checked-in Cinzel TTF.
- Include only the numeric glyphs required by this plan.
- Generate one flush face-label child per face.
- Use a shared warm ivory/ember-gold inlay material.
- Cull or disable back-facing labels so translucent resin cannot reveal a
  confusing stack of numerals.
- Keep the d4's numeral centered on each face rather than using corner-reading
  notation.
- Display `10` on the d10's tenth face.

The generated label transforms and definition face vectors share one source of
truth so a numeral cannot disagree with its result pose.

### Resin

Add a focused first-party URP shader and one material for translucent dark-red
resin. It should provide:

- dark-red body tint with controlled translucency
- strong but clean edge/fresnel highlights
- readable specular response under the overlay lights
- subtle internal tonal variation
- a very low-amplitude held-state shimmer
- depth behavior that prevents rear numerals bleeding through the front
- SRP-batcher-compatible material data
- no gameplay-camera or scene-light dependency

Do not turn this into a general-purpose material framework or shader library.

### Catalog

Create one runtime-loaded `DefaultDiceSet` catalog referencing:

- the approved definitions
- the resin and numeral materials
- the three motion profiles
- the ordinary, positive, and negative effect prefabs

Runtime code loads one catalog, then follows direct references. It does not
perform repeated path-based loads when a roll starts.

## Motion contract

Create three `DiceMotionProfile` assets. Curves use normalized overlay/camera
coordinates so the same motion adapts to landscape, portrait, and smaller host
regions.

Each profile defines:

- anticipation duration
- normalized X/Y travel
- depth/scale arc
- spin axis and authored turn count
- settle start
- position and rotation easing

Runtime motion:

1. Select a profile from a stable cosmetic hash of the request identifier.
   The result value is not an input to profile selection.
2. Begin from the profile's authored entry pose.
3. Evaluate position and free tumble through anticipation/roll.
4. During the settle window, smoothly blend rotation toward the computed face
   pose.
5. Finish near the active region's center with the numeral upright.
6. Hold the exact pose with only subtle material shimmer.

Skip evaluates the same request directly at its final state. It does not start
a different clip, choose a new trajectory, or request another result.

## Overlay contract

### 3D isolation

- Add a `DiceOverlay3D` Unity layer.
- Place the dice, lights, and result effects on that layer.
- A dedicated fixed perspective camera renders only that layer.
- The camera clears to transparent and renders into a managed render texture.
- No gameplay scene objects, world lighting, world post-processing, or world
  physics participate.

### UI Toolkit composition

Add `DiceOverlay.uxml` and `DiceOverlay.uss` under the existing runtime UI
Toolkit resources:

- full-screen transparent root
- an `Image` element that displays the 3D render texture
- a picking surface active only while the die is moving
- no frame, result label, button, or reward-screen chrome

Create the document through `ArenaPanel` and allocate front-most ordering
through `RuntimeUiLayer`. The root releases input after reaching the held final
state so the future host remains usable. Host dismissal, rather than clicking
the held die, closes the overlay.

### Lifecycle

- Bootstrap once in Arena runtime scenes.
- Dispose the panel settings, camera target, render texture, instantiated
  catalog content, and material instances on destruction.
- Reuse the presenter rather than instantiate a second presenter per roll.
- Resize the render texture only when the active presentation region changes
  materially.
- Starting a warmed roll performs no asset lookup, shader compilation, or
  prefab load.

## Review and debug surface

Add an editor/development-only dice review panel plus an authoring scene:

- force any legal result without server access
- choose one of the three motion profiles
- request a real server-generated preview roll when connected
- skip at any point
- dismiss the host/overlay
- repeat a request identifier to verify idempotency
- preview full-screen, portrait, landscape, and reduced host regions
- run all results sequentially for face-pose review

Forced results are visibly labeled as local preview data in the review panel.
They never enter the network bridge or modify game state.

## Phased implementation

### Phase 1 — d20 asset and authoring foundation

Boundary:

- Add the definition/catalog/face-pose data contracts.
- Add the deterministic editor builder and authoring validator.
- Add the Cinzel numeral font/material and focused resin shader/material.
- Generate only the d20 mesh, face labels, prefab, and definition.
- Add a static/turntable d20 authoring scene for visual inspection.
- Add EditMode contract tests for d20 face coverage and face-pose math.

Expected new areas:

- `Assets/Arena/Runtime/Presentation/Dice/`
- `Assets/Arena/Editor/Dice/`
- `Assets/Arena/Content/Dice/`
- `Assets/Arena/Content/Shaders/Dice/`
- `Assets/Arena/Resources/Dice/`
- `Assets/Arena/Content/Scenes/Authoring/DiceOverlayLab.unity`
- `Assets/Arena/Tests/Editor/DiceAuthoringContractTests.cs`

Exit gate:

- d20 has exactly one definition entry and numeral for every value `1–20`
- every computed result pose faces the inspection camera and is upright
- rear numerals do not visibly bleed through the front
- bevels, recesses, resin, and inlay read cleanly in close-up
- the user approves the d20 model/material direction
- C# runtime/editor/test projects compile
- focused EditMode tests pass through the Unity Editor
- `git diff --check` passes

Stop after the gate. Do not create motion, overlay runtime, server schema, or
the remaining dice in this phase.

### Phase 2 — d20 overlay presenter and local review

Boundary:

- Add the three authored motion-profile assets.
- Add the network-neutral resolved-roll request and pose solver.
- Add the dedicated camera, render-texture lifecycle, UI Toolkit overlay, and
  `DiceOverlay3D` layer.
- Add d20 play/skip/hold/dismiss state handling.
- Add the editor/development review panel using forced local values.
- Extend the authoring scene to exercise the actual overlay.

Expected new or changed areas:

- `Assets/Arena/Runtime/Presentation/Dice/`
- `Assets/Arena/Runtime/Debug/DicePresentationDebugPanel.cs`
- `Assets/Arena/Resources/UI/Toolkit/DiceOverlay.uxml`
- `Assets/Arena/Resources/UI/Toolkit/DiceOverlay.uss`
- `Assets/Arena/Content/Dice/Motion/`
- `ProjectSettings/TagManager.asset`
- dice EditMode tests

Exit gate:

- all twenty d20 results finish on the correct upright face
- all three paths reach the same correct final pose
- skip works during anticipation, tumble, and settle
- the final die remains until host dismissal
- the overlay is transparent and uses no visible or implied floor
- landscape, portrait, and constrained host-region previews remain framed
- the warmed presenter has no perceptible roll-start loading pause
- no runtime network or server dependency exists in the presenter
- the user approves the overall motion and readability
- C# runtime/editor/test projects compile
- focused EditMode tests pass through the Unity Editor
- `git diff --check` passes

Stop after the gate. Do not add server/schema work, special-result effects, or
the remaining dice in this phase.

### Phase 3 — authoritative fair rolls and client bridge

Boundary:

- Add `server/src/dice.rs` and register it from `server/src/lib.rs`.
- Add the single-row-per-owner `ActiveDiceRoll` schema.
- Add preview-request, idempotency, bounded uniform roll, and dismissal logic.
- Add Rust validation/idempotency tests.
- Regenerate C# bindings through the canonical harness-featured build path.
- Add the local-player `ActiveDiceRoll` subscription and update its query
  contract test.
- Add `DiceRollNetworkBridge`.
- Add connected `Server Roll` and dismissal controls to the development review
  panel.
- Add a focused local live probe for bounds, idempotency, dismissal,
  reconnection, and a broad distribution sanity report.

Expected new or changed areas:

- `server/src/dice.rs`
- `server/src/lib.rs`
- generated C# bindings under
  `Assets/Arena/Runtime/Generated/SpacetimeDB/`
- `Assets/Arena/Runtime/Network/GameplaySubscriptionPlanner.cs`
- `Assets/Arena/Runtime/Presentation/Dice/DiceRollNetworkBridge.cs`
- `Assets/Arena/Tests/Editor/RuntimeOrchestrationRegressionTests.cs`
- `ops/dice-roll-probe.py`

Exit gate:

- every supported side count returns only legal values
- unsupported side counts and malformed request identifiers are rejected
- repeating the same active request never consumes a new roll
- a different request is rejected until the current roll is dismissed
- only the server writes the authoritative value
- the local client presents the inserted value exactly
- reconnecting with an active row presents the same value
- dismissal removes the row and overlay
- the distribution probe shows no obvious face bias and records its sample
  size/histogram without claiming proof from a short sequence
- Rust tests and canonical wasm build pass
- generated bindings are current and not hand-edited
- C# runtime and test projects compile
- focused EditMode tests pass through the Unity Editor
- `git diff --check` passes

Stop after the gate. Do not connect rolls to rewards/events or begin visual
effects/remaining dice.

### Phase 4 — d20 result effects and polish

Boundary:

- Add the ordinary settle pulse.
- Add the positive ember-gold flare, rune halo, and upward sparks.
- Add the negative dark-crimson pulse and inward/downward motes.
- Add subtle held-state resin shimmer.
- Tune camera, lighting, paths, settle, material, and effect timing as one
  presentation.
- Record warm-roll CPU, GPU, allocation, and render-texture observations on a
  representative desktop without turning this into a general optimization
  project.

Expected new or changed areas:

- `Assets/Arena/Content/Dice/VFX/`
- `Assets/Arena/Content/Dice/Prefabs/`
- d20 material/motion/catalog assets
- presenter result-classification logic and focused tests

Exit gate:

- d20 `2–19` use only the ordinary treatment
- d20 `20` is clearly positive and celebratory
- d20 `1` is clearly negative and ominous
- no screen flash, strong luminance jump, camera/UI shake, or distortion occurs
- every effect preserves final numeral readability
- effects remain centered on and clipped with the overlay presentation
- repeated warmed rolls create no visible loading hitch or sustained
  per-frame managed allocation
- the user approves the polished d20 presentation
- C# runtime/editor/test projects compile
- focused EditMode tests pass through the Unity Editor
- `git diff --check` passes

Stop after the gate. Do not generate the remaining dice until the d20 visual
language is approved.

### Phase 5 — complete the six-die set

Boundary:

- Extend the approved builder to d4, d6, d8, d10, and d12.
- Generate each mesh, face labels, prefab, definition, and catalog entry.
- Tune per-shape presentation scale and face-label size while retaining one
  shared material/type language.
- Reuse the three approved motion profiles and effects.
- Classify each non-d20 maximum as positive; all lower results are ordinary.
- Extend the review panel's all-results pass across every supported die.

Exit gate:

- exact unique face coverage:
  - d4: `1–4`
  - d6: `1–6`
  - d8: `1–8`
  - d10: `1–10`
  - d12: `1–12`
  - d20: `1–20`
- d4 uses centered face numerals
- d6 uses numerals, not pips
- d10 presents `10`, not `0`
- every legal result on every die ends upright and readable
- every non-d20 maximum receives the approved positive treatment
- every non-maximum non-d20 result receives the ordinary treatment
- the six silhouettes, bevels, resin, inlay, lighting, motion, and effects read
  as one coherent set
- the full manual aspect/path/skip/dismiss matrix passes
- the user approves the complete dice set
- server tests, C# builds, focused EditMode tests, and `git diff --check` pass

Stop at this final gate. Multiple dice, percentile dice, sound, and reward/event
integration require separate approved plans.

## Verification matrix

### Automated

Server:

- validation accepts exactly the six supported side counts
- result bounds for every die
- idempotent same-request behavior
- active-roll exclusion
- dismissal ownership/idempotency
- pure result classification where shared by tests
- broad live distribution report

Unity EditMode:

- catalog contains exactly the expected definitions
- each definition contains every legal value once
- face normal/up vectors are valid
- pose solver aligns every result face and numeral
- path choice is independent of result value
- every motion profile reaches the exact target pose
- skip returns the exact target pose
- result classification matches the design specification
- local subscription includes only the owner's active dice row
- presenter/runtime code has no dependency from presentation into generated
  SpacetimeDB types

Build/static checks:

- `cargo fmt --check --manifest-path server/Cargo.toml`
- `cargo test --manifest-path server/Cargo.toml`
- canonical harness-featured wasm build and binding regeneration in Phase 3
- `dotnet build Assembly-CSharp.csproj --no-restore`
- `dotnet build Assembly-CSharp-Editor.csproj --no-restore`
- `dotnet build Arena.EditModeTests.csproj --no-restore`
- `git diff --check`

Unity EditMode tests are run from **Arena > Validate Build-Blocking EditMode
Tests** in the open Unity Editor. Do not use Unity batch mode.

### Manual visual review

For each available die:

- every face/result
- each of the three motion profiles
- normal completion
- skip during anticipation
- skip during free tumble
- skip during settle
- held result
- host dismissal
- consecutive rolls after dismissal
- landscape
- portrait
- constrained overlay region

Special review:

- d20 `1`, `2`, `19`, `20`
- d4 `1`, `4`
- d6 `1`, `6`
- d8 `1`, `8`
- d10 `1`, `10`
- d12 `1`, `12`

Network review after Phase 3:

- same request repeated before dismissal
- different request attempted before dismissal
- reconnect with an active roll
- dismiss after reconnect
- local forced preview clearly separated from server roll

## Risks and containment

### Transparent resin and numeral bleed

Risk: translucent geometry exposes labels on rear faces.

Containment: depth-writing resin, back-facing label suppression, shallow
recesses, and the Phase 1 close-up visual gate. Do not proceed to runtime
motion while the static d20 is visually ambiguous.

### Last-moment face snapping

Risk: arbitrary tumble rotation visibly corrects itself near the end.

Containment: begin the deterministic face blend during the authored settle
window, validate all twenty d20 results on every motion profile, and gate the
remaining dice on approved d20 motion.

### UI/3D compositing

Risk: render-texture alpha, sorting, or pointer capture conflicts with existing
UI Toolkit/uGUI surfaces.

Containment: dedicated layer/camera, the existing shared sorting allocator,
input-active-only-while-moving behavior, and Phase 2 aspect/input review before
network work.

### Randomness trust

Risk: visual motion or a client preview is mistaken for authoritative
randomness.

Containment: server-owned bounded RNG, persisted result before presentation,
network-neutral presenter, explicit forced-preview labeling, and no gameplay
effect in the preview reducer.

### Abandoned active rolls

Risk: a disconnected player leaves a presentation row behind.

Containment: one row per owner caps storage. Reconnection restores the same
result so it can be dismissed. Do not add TTL or silent reroll behavior that
would violate hold/reconnect semantics.

### Scope expansion

Risk: the dice foundation grows into reward design, a general VFX framework,
or a multi-dice system.

Containment: phase boundaries prohibit those additions. Each requires a
separate approved design and plan.
