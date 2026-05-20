# Combat Animation Visual Interrupt Plan

Status update, 2026-05-04: the V1 phased-melee deferral in this older plan is superseded by `docs/archive/2026-05-stale-plans/combat-animation-migration-plan-2026-05-04.md`. Phased melee now uses segmented runtime playback on the combat action layer and applies `visualInterruptibleAtSeconds` against runtime segmented elapsed time.

## Issue

The combat animation arbiter currently preserves too many displaced melee presentations as ghosts.

This happens in both directions:

- An auto-attack request arrives while a non-auto melee animation is still playing, so the auto-attack is shown as a visual-only ghost.
- A non-auto melee request arrives while an auto-attack animation is still playing, so the current auto-attack pose is captured as a ghost before the new melee animation takes over.

The rule is too coarse because it only asks "which request wins?" It does not ask whether the currently active animation has already shown enough of its strike to be visually disposable.

That creates noisy presentation when the current animation is already in its settle/recovery phase. At that point the attack has visually connected, and the better result is often to drop the tail of the old animation and let the incoming animation play directly. This is presentation-only and must not change impact timing, damage, cooldowns, auto-attack cadence, or server combat authority.

This is still a visible presentation change. In the auto-attack-during-melee case, the player may see the auto-attack move onto the real character body after the active melee animation becomes visually disposable. Today that same auto-attack can appear on a separate ghost while the real body continues the older melee animation.

## Proposed Authoring Field

Use an authored visual timing field on melee animation entries:

```csharp
visualInterruptibleAtSeconds
```

Meaning for V1 single-clip melee and auto-attack strike presentation:

- Before this timestamp, the animation is visually protected. If another combat animation wins arbitration, preserve the displaced presentation with the existing ghost behavior.
- At or after this timestamp, the animation is visually disposable. If another combat animation wins arbitration, discard the displaced presentation without creating a ghost.

This name is preferred over `canBeDisposedAfterTime` because it describes the designer-facing behavior: the character body may be visually interrupted after this point. It also avoids implying that gameplay events, hit windows, or attack commitment are being disposed.

Phased melee was explicitly deferred in the original V1 plan. That deferral is now closed: phased melee uses segmented start/loop/end playback, and the scalar applies to runtime segmented elapsed time.

Resolved V1 decisions:

- Phased melee visual interruption uses segmented playback timing.
- Initial values are manually tuned by authors.
- Invalid values are treated as unset, which means they fall back to the timing reference length.

## Default Behavior

Missing or unset `visualInterruptibleAtSeconds` must preserve existing behavior.

Recommended fallback:

```csharp
visualInterruptibleAtSeconds = ResolveTimingReferenceLengthSeconds()
```

That means legacy animations are not visually disposable until they are over. Existing ghost behavior remains intact until an author explicitly opts an attack into earlier visual interruption.

For attacks with known visual contact timing, authors can set:

```csharp
visualInterruptibleAtSeconds = visualContactSeconds + 0.08f
```

The `0.08s` hold gives the hit a chance to read before another animation takes over. This is a recommended authoring starting point, not a gameplay rule. Authors can tune the value per attack.

Invalid values should also preserve existing behavior. If the authored value is missing, zero, negative, or greater than `ResolveTimingReferenceLengthSeconds()`, the resolver should return `ResolveTimingReferenceLengthSeconds()`.

## Runtime Rule

When a combat animation request arrives and the arbiter decides it conflicts with the active single-clip melee presentation:

```csharp
if (activeMeleePresentation.ElapsedSeconds >= activeMeleePresentation.VisualInterruptibleAtSeconds)
{
    DisposeActiveVisualPresentationWithoutGhost();
    PlayIncomingPresentationNormally();
}
else
{
    PreserveExistingGhostBehavior();
}
```

Applied to the two current noisy cases:

- Auto-attack arrives during melee skill:
  - if the active melee skill is visually interruptible, play the auto-attack normally on the real character body
  - otherwise keep suppressing the auto-attack and show its visual-only ghost
- Melee skill arrives during auto-attack:
  - if the active auto-attack is visually interruptible, clear it without capturing an interrupted melee ghost
  - otherwise keep capturing the current pose as a ghost before playing the melee skill

The rule evaluates the currently active animation. If the active animation is past its visual-interrupt threshold, the incoming animation may take over the real body without creating a ghost. If not, existing ghost/suppression behavior runs.

## Data Placement

Add the field to Unity-authored melee presentation data, not to server gameplay tuning.

Likely home:

- `WeaponMeleeAttackAuthoring` in `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs`

Expose it in the combat animation set editor near hit/recovery timing:

- label: `Visual Interruptible At`
- units: seconds
- tooltip: "After this time, another combat animation may replace this visual without creating a ghost. Presentation only; does not affect hit timing."

Auto-attacks should use the same field because their visual source is already a melee strike in the combat animation set. Runtime lookup should resolve the incoming or active action id to the strike index first, then resolve the visual interrupt threshold from that strike. For auto-attacks, this follows the existing `autoAttackVisualSourceActionId` / authored auto-attack resolution path before reading the strike timing.

## Combo Follow-Up Policy

A combo follow-up is presentation-only continuity, not an interrupt. When the incoming melee skill's authored `comboFrom` matches the currently active strike's authored id, single-clip strike presentation hands off directly without ghosting and without clearing the melee layer to `Empty`.

Rationale:

- The player is intentionally chaining within the same combo flow. The animator should transition strike to strike directly; ghosting the predecessor would visually duplicate the same fighter, and clearing to `Empty` can expose a one-frame bad pose.
- Combo windows typically open well before `visualInterruptibleAtSeconds`, so without this rule every combo follow-up would ghost.
- The check uses the same authored `comboFrom` field that drives gameplay combo windows, so visual continuity and gameplay combo recognition can't drift.

Rule (evaluated for non-auto-attack incoming requests while a melee presentation is active):

```csharp
// Priority: combo follow-up handoff > visual-interrupt threshold > default ghost-and-play.
if (incoming.comboFrom == active.authoredStrikeId && active.singleClip) return HandoffComboFollowUpAndPlay; // no Empty state
if (incoming.comboFrom == active.authoredStrikeId && active.phased) return InterruptCurrentWithoutGhostAndPlay; // phased base layer still needs clearing
if (gateMet) return InterruptCurrentWithoutGhostAndPlay; // threshold reached
return InterruptCurrentAndPlay; // genuine interrupt — keep ghost
```

Auto-attacks are unchanged: they go through the threshold-based suppression rules in the auto-attack branch.

A non-combo melee skill replacing the active strike (no matching `comboFrom`, e.g., interrupting Hew with Whirlwind) still ghosts the predecessor as before.

## Phased Melee Policy

Current policy applies `visualInterruptibleAtSeconds` to phased melee presentation using runtime segmented elapsed time.

Reason:

- Phased entries can have start, loop, and end clips.
- Runtime playback now advances start/loop/end as explicit segments.
- A value like `0.50s` is action-relative across the resolved segment sequence.

Implementation rule:

- If the active presentation is phased melee, evaluate the same threshold policy as single-clip melee.
- Before the threshold, keep ghost/suppression behavior.
- At or after the threshold, interrupt without creating a ghost.
- Add per-phase authoring later only if total action-relative seconds prove too coarse.

Possible later shape:

```csharp
visualInterruptiblePhase = Start | Loop | End | Never
visualInterruptibleAtSeconds = phaseRelativeSeconds
```

That later shape is intentionally separate from the V1 interpretation. For phased actions, `visualInterruptibleAtSeconds` would only become meaningful together with an explicit phase selector.

## Implementation Plan

1. Add `visualInterruptibleAtSeconds` to melee attack authoring data.
2. Add a resolver on `CombatAnimationSet`, for example:

```csharp
public float GetVisualInterruptibleAtSeconds(int strikeIndex)
```

3. Make the resolver return `ResolveTimingReferenceLengthSeconds()` when the authored value is missing, zero, negative, or greater than the timing reference length.
4. Track active melee presentation identity in `PlayerAnimator` with enough data to evaluate the rule:
   - action id
   - category
   - strike index
   - visual interruptible timestamp
   - whether the presentation is single-clip or phased
5. Do not cache elapsed time as a parallel timer. Read elapsed from the relevant Animator state when possible, using cached identity only to interpret which authored strike the active state represents. Clear cached identity whenever the presentation is cleared, canceled, or preempted.
6. Extract a pure visual-interrupt decision helper, for example:

```csharp
private static CombatVisualInterruptDecision DecideVisualInterrupt(
    CombatAnimationCategory activeCategory,
    CombatAnimationCategory incomingCategory,
    bool activeIsPhased,
    float activeElapsedSeconds,
    float activeVisualInterruptibleAtSeconds)
```

7. Use that helper from `DecideCombatAnimationRequest(...)` or the preemption path so the runtime can distinguish:
   - interrupt and ghost
   - interrupt without ghost
   - suppress incoming auto-attack as ghost
   - allow incoming auto-attack to play because the current visual is disposable
8. Update `PreemptMeleeAnimationIfActive()` so ghost capture is conditional instead of unconditional.
9. Update `CaptureSuppressedAutoAttackGhost(...)` call sites so suppressed auto-attacks are only ghosted when the active higher-priority presentation is not visually disposable.
10. Add tracing that records the visual decision:

```text
visualInterrupt=ghost elapsed=0.31 threshold=0.46 current=COMBO_ATTACK_...
visualInterrupt=dispose elapsed=0.58 threshold=0.46 current=COMBO_ATTACK_...
visualInterrupt=dispose elapsed=1.20 threshold=1.10 current=SKYFALL_...
```

## Tests And Verification

Add focused coverage around the pure decision helper, then keep animator wiring coverage in PlayMode.

Minimum cases:

- Melee skill active before `visualInterruptibleAtSeconds`; incoming auto-attack is suppressed and ghosted.
- Melee skill active after `visualInterruptibleAtSeconds`; incoming auto-attack plays normally and no auto-attack ghost is created.
- Auto-attack active before `visualInterruptibleAtSeconds`; incoming melee skill captures the interrupted visual ghost.
- Auto-attack active after `visualInterruptibleAtSeconds`; incoming melee skill plays without creating an auto-attack ghost.
- Missing authored value falls back to animation end and preserves existing behavior.
- Invalid authored value greater than the timing reference length falls back to the timing reference length.
- Phased melee uses the same threshold behavior over runtime segmented elapsed time.

Manual verification should use one short auto-attack and one longer melee skill with an obvious settle phase, then test both interruption directions around the authored threshold.

## Non-Goals

- Do not change server hit timing.
- Do not change melee manifest gameplay semantics.
- Do not cancel or reschedule pending impacts.
- Do not change auto-attack cadence.
- Do not add category-specific hardcoded settle windows in `PlayerAnimator`.
- Do not infer the value from damage timing unless that is added later as an editor convenience.
- Do not add extra phase-relative visual interruption fields until runtime segmented elapsed time proves insufficient.

## Acceptance

- Existing attacks behave as they do today when `visualInterruptibleAtSeconds` is not authored.
- Authors can mark an attack's settle phase as visually disposable.
- Ghosts are still used before the visual interruption threshold.
- Ghosts are not created after the visual interruption threshold.
- The rule works symmetrically for auto-attack replacing melee skill and melee skill replacing auto-attack.
- Phased melee uses segmented threshold behavior without relying on Base Layer legacy states.
- Traces make it clear whether a displaced animation was ghosted or disposed.
