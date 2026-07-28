# Animation ownership

Which type owns which piece of the player-combat animation stack. `CLAUDE.md`
carries the short form; this is the elaboration it points at. Read it before
adding state, a public API, or a playback path to `PlayerAnimator`.

Related: [`combat-animation-authoring-contract.md`](combat-animation-authoring-contract.md)
for how a combat action's animation is authored, and
[`animation-system-audit-2026-07-02.md`](animation-system-audit-2026-07-02.md)
for how the current split came about.

## `PlayerAnimator` — the low-level adapter

`PlayerAnimator` is the central low-level adapter for the shared player-combat
Animator states, override banks, layers, and weights. It may own Animator
parameter/state hashes and thin coordination needed to apply controller
decisions.

Focused components may own a disjoint Animator property when that ownership is
explicit and exclusive — for example, `MeleeContactHitstop` owns
`Animator.speed`.

`PlayerAnimator` must **not** become the source of animation selection, timing,
lifecycle, preemption, or gameplay policy. Those belong in `CombatAnimationSet`
data or in focused controllers.

New public APIs or durable state on `PlayerAnimator` need an explicit ownership
reason.

## `CombatStatusReactionController` — reactions and hard CC

Hit reactions, stagger, knockdown, and hard crowd-control presentation belong
here. `PlayerAnimator` may coordinate cross-system cancellation for those
reactions, but it should not own their Animator parameter names, trigger
selection, or loop override policy.

## `CombatActionPlaybackController` — the shared playback substrate

This is the shared substrate for melee, spell, and phased bank slots, layer
recovery, lower-body unlock, visual interrupts, preemption, and spell-hold
lifecycle.

Extend that substrate, or another coherent controller, instead of adding a
parallel playback state machine to `PlayerAnimator`. Extract only at a real
ownership boundary.
