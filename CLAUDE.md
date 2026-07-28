# Repository Standards

## Tests

Do not write tests unless explicitly asked. This project is in design flux; a
test written today is a refactor tax tomorrow.

When an existing test fails during a change, the default is to **delete or
loosen the assertion, not to repin it.** Stop and investigate only if the
assertion names a runtime invariant — something that would break the game, the
server contract, or a hard validation gate. Report the rest as a list and move
on; do not fix them unless asked.

Never assert on:

- **seed-derived values** — coordinates, cell counts, node indices, edge ids,
  planner-version strings. The generator rebaselines by design.
- **third-party asset state** — imported prefabs, FBX sub-asset ids, collider
  components on art packages.
- **counts that are properties of content, not code** — recipe totals, topology
  counts, catalog sizes.

## Directory Structure

First-party Unity code and content live under `Assets/Arena`. Imported packages live under `Assets/ThirdParty`. Runtime-loaded first-party assets live under `Assets/Arena/Resources`; only place assets there when code uses `Resources.Load` or `Resources.LoadAll`. Editor-only generator inputs and their ScriptableObject types belong under `Assets/Arena/Editor`, even when they reference runtime assets.

Use `docs/project-structure.md` as the canonical folder map before adding new top-level directories.

## Animation Ownership

`PlayerAnimator` is the central low-level adapter for the shared player-combat Animator states, override banks, layers, and weights. It may own Animator parameter/state hashes and thin coordination needed to apply controller decisions. Focused components may own a disjoint Animator property when that ownership is explicit and exclusive (for example, `MeleeContactHitstop` owns `Animator.speed`). `PlayerAnimator` must not become the source of animation selection, timing, lifecycle, preemption, or gameplay policy; put those responsibilities in `CombatAnimationSet` data or focused controllers. New public APIs or durable state require an explicit ownership reason, not a ceremonial one-field-for-one-extraction trade.

Hit reactions, stagger, knockdown, and hard crowd-control presentation belong in `CombatStatusReactionController`. `PlayerAnimator` may coordinate cross-system cancellation for those reactions, but it should not own their Animator parameter names, trigger selection, or loop override policy.

`CombatActionPlaybackController` is the shared substrate for melee, spell, and phased bank slots, layer recovery, lower-body unlock, visual interrupts, preemption, and spell-hold lifecycle. Extend that substrate or another coherent controller instead of adding a parallel playback state machine to `PlayerAnimator`. Extract only at a real ownership boundary; do not create pass-through classes merely to satisfy a file-size or field-count proxy.

Combat actions must dispatch through loadout/action-bar resolution or an explicitly owned fixed-action dispatcher. Do not add hidden combat keybinds that bypass the action bar.

## Combat Geometry Contract (LOS / query raycasts)

Line of sight is a targeting rule: caster→target versus world geometry only, checked at press time, default-on per action (`requires_target_los`), never re-checked at impact. Bodies (players/NPCs) never block LOS; probes that reach the target's personal space count as seeing them.

Query raycasts (LOS + projectile impact) test **authored query geometry only** — terrain, arena layout, query boxes, query meshes (owner ruling, 2026-07-04). Movement collision NEVER blocks sight or projectiles: it is authored oversized to keep capsules out. A prop that should block sight must author `ArenaGameplayQueryCollision`; do not reintroduce movement boxes into `raycast_world_with_layout_for_scene_with_stats` or into the client mirror (`ServerLosCollisionData`). Seeded arenas currently have no authored query geometry, so only their layout raycast blocks sight there.
