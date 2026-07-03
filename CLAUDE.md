# Repository Standards

## Directory Structure

First-party Unity code and content live under `Assets/Arena`. Imported packages live under `Assets/ThirdParty`. Runtime-loaded first-party assets live under `Assets/Arena/Resources`; only place assets there when code uses `Resources.Load` or `Resources.LoadAll`.

Use `docs/project-structure.md` as the canonical folder map before adding new top-level directories.

## Animation Ownership

`PlayerAnimator` is in maintenance mode. New animation features must add data to `CombatAnimationSet` entries or compose focused controllers. Do not add new private fields, new public methods, or new responsibilities to `PlayerAnimator` without first extracting an existing responsibility into its own class.

Hit reactions, stagger, knockdown, and hard crowd-control presentation belong in `CombatStatusReactionController`. `PlayerAnimator` may coordinate cross-system cancellation for those reactions, but it should not own their Animator parameter names, trigger selection, or loop override policy.

Melee, spell, and phased playback share bank slots, layer recovery, lower-body unlock, visual interrupts, and preemption rules. Extract that shared substrate as one action playback component before splitting by action type. Do not add new melee/spell/phased playback machinery directly to `PlayerAnimator`.

Combat actions must dispatch through loadout/action-bar resolution or an explicitly owned fixed-action dispatcher. Do not add hidden combat keybinds that bypass the action bar.

## Combat Geometry Contract (LOS / query raycasts)

Line of sight is a targeting rule: caster→target versus world geometry only, checked at press time, default-on per action (`requires_target_los`), never re-checked at impact. Bodies (players/NPCs) never block LOS; probes that reach the target's personal space count as seeing them.

Query raycasts (LOS + projectile impact) test **authored query geometry only** — terrain, arena layout, query boxes, query meshes (owner ruling, 2026-07-04). Movement collision NEVER blocks sight or projectiles: it is authored oversized to keep capsules out. A prop that should block sight must author `ArenaGameplayQueryCollision`; do not reintroduce movement boxes into `raycast_world_with_layout_for_scene_with_stats` or into the client mirror (`ServerLosCollisionData`). Seeded arenas currently have no authored query geometry, so only their layout raycast blocks sight there.
