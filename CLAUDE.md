# Repository Standards

## Directory Structure

First-party Unity code and content live under `Assets/Arena`. Imported packages live under `Assets/ThirdParty`. Runtime-loaded first-party assets live under `Assets/Arena/Resources`; only place assets there when code uses `Resources.Load` or `Resources.LoadAll`.

Use `docs/project-structure.md` as the canonical folder map before adding new top-level directories.

## Animation Ownership

`PlayerAnimator` is in maintenance mode. New animation features must add data to `CombatAnimationSet` entries or compose focused controllers. Do not add new private fields, new public methods, or new responsibilities to `PlayerAnimator` without first extracting an existing responsibility into its own class.

Hit reactions, stagger, knockdown, and hard crowd-control presentation belong in `CombatStatusReactionController`. `PlayerAnimator` may coordinate cross-system cancellation for those reactions, but it should not own their Animator parameter names, trigger selection, or loop override policy.

Melee, spell, and phased playback share bank slots, layer recovery, lower-body unlock, visual interrupts, and preemption rules. Extract that shared substrate as one action playback component before splitting by action type. Do not add new melee/spell/phased playback machinery directly to `PlayerAnimator`.

Combat actions must dispatch through loadout/action-bar resolution or an explicitly owned fixed-action dispatcher. Do not add hidden combat keybinds that bypass the action bar.
