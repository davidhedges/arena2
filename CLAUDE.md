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

**OPEN OWNER RULING — do not resolve one side without the other:** the server's query raycast (LOS + projectile impact, `raycast_world_with_layout_for_scene_with_stats` in `server/src/world_collision.rs`) currently tests fat MOVEMENT boxes alongside the tight QUERY set, so wide props block sight beyond their visuals (tree movement boxes vs their thin authored LOS boxes). The clean contract is query-geometry-only, but the playground arena has zero authored query geometry, so flipping it silently removes arena-wall LOS blocking until arena query boxes are authored. Full context: the S4 near-wall entry in `docs/netcode-design-review-2026-07-03.md`.
