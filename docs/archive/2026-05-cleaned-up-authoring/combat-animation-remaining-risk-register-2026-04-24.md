# Combat Animation Remaining Risk Register

This document is historical. Use `docs/combat-authoring-contract.md` for the current combat authoring contract.

Date: 2026-04-24

## Purpose

This document records the main older assumptions and remaining edge-risk seams in the combat animation system after the 2026-04-24 cleanup pass. The goal is to keep these risks explicit so they do not turn back into invisible drift or repeated debugging cost.

## Current State

The combat animation architecture is materially cleaner than before:

- one canonical `CombatAnimationRequest` path exists
- phased melee no longer piggybacks on charge in the main runtime path
- replay suppression is more centralized
- old public wrapper entry points were removed
- melee/phased authoring is cleaner than before

However, the system is not yet free of older assumptions. The remaining risk is concentrated in a few narrow seams.

## Highest Remaining Risks

### 1. Authored Id vs Runtime Id Translation

Risk:
- The system still translates between authored action ids and runtime action ids in multiple places.
- This is the most likely source of "why does this one move behave differently?" bugs.

Current seams:
- [GameplayContracts.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Combat/GameplayContracts.cs)
  - `ResolveRuntimeActionId(...)`
  - `NormalizeRuntimeActionReference(...)`
- [CombatAnimationSet.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs)
  - `ResolveRuntimeSlotIdForStrikeReference(...)`
  - `ResolveAuthoredStrikeIdForRuntimeAction(...)`
  - `TryGetPhasedMeleeEntry(...)`

Why it matters:
- prediction can remember one id while authoritative replay arrives with another
- phased presentation lookup can succeed under one identifier path and fail under another
- new attacks with custom slot ids are more vulnerable than attacks that follow the default combo-slot patterns

Assessment:
- acceptable short-term debt
- highest remaining debugging-risk seam

### 2. Same-Frame Base-Layer Arbitration

Risk:
- Some interrupt behavior still depends on the interaction of animator state writes in the same frame.
- This is especially fragile when one path uses trigger-driven transitions and another uses direct state play.

Current seams:
- [PlayerAnimator.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Presentation/PlayerAnimator.cs)
  - `RequestCombatAnimation(...)`
  - `DecideCombatAnimationRequest(...)`
  - `PreemptLowerPriorityPresentationFor(...)`
  - `PlayMeleeAnimation(...)`
  - `TryTriggerPhasedMeleeAction(...)`

Known symptom class:
- `Hew` interrupts an auto-attack animation correctly while `Skyfall` does not
- phased melee and strike melee can behave differently even though the priority rule is supposed to be shared

Why it matters:
- same-frame `SetTrigger`, `Play`, and `CrossFade` behavior is order-sensitive
- Unity controller transitions can preserve older assumptions longer than the code shape suggests

Assessment:
- active risk
- should be treated as the main remaining behavior seam

### 3. Prediction / Authoritative Replay Timing Windows

Risk:
- Duplicate suppression still uses time-window heuristics rather than a stronger identity/replay token.

Current seams:
- [MeleeInputHandler.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Input/MeleeInputHandler.cs)
  - `PredictedStrikeVisualRetentionMs = 400`
  - `RememberPredictedStrikeVisual(...)`
  - `ConsumePredictedStrikeVisual(...)`
- [LocalCombatState.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Simulation/LocalCombatState.cs)
  - `PredictSpellVisual(...)`
  - `ConsumePredictedSpellVisual(...)`

Why it matters:
- attacks with unusual startup timing can fall outside the assumed window
- mismatched ids amplify this risk
- long-latency or delayed authoritative echoes can produce duplicate visual playback

Assessment:
- acceptable practical heuristic for now
- likely future token sink if unusual attacks are added

### 4. Reusable Animator Bank Assumption

Risk:
- The system still uses a small reusable strike/spell bank instead of one explicit state per authored action.

Current seams:
- [CombatAnimationSet.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs)
  - `AnimatorStrikeBankCount = 4`
  - `AnimatorSpellBankCount = 4`
- [PlayerAnimator.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Presentation/PlayerAnimator.cs)
  - `ResolveStrikeBankSlot(...)`
  - `TryBindStrikeClip(...)`
  - `ResolveNextSpellBankSlot(...)`
  - `TryBindSpellClip(...)`

Why it matters:
- hot-swapping clips into a few shared banks is more fragile than a one-state-per-action topology
- same-frame state changes are harder to reason about

Assessment:
- accepted architecture tradeoff for now
- not the first cleanup target unless more issues cluster here

### 5. Charge Remains a Live but Deferred Branch

Risk:
- Charge is still fully present in the controller/runtime, but not currently exercised enough to be trusted.

Current seams:
- [PlayerAnimator.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Presentation/PlayerAnimator.cs)
  - `IsChargingHash`
  - `TriggerChargeStartHash`
  - `TriggerChargeEndHash`
  - `ApplyChargeClipOverrides(...)`
- [CombatAnimatorControllerUpgrader.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Editor/CombatAnimatorControllerUpgrader.cs)
  - charge state and transition creation

Why it matters:
- it still influences the mental model
- it is easy to accidentally “trust” a path that has not been validated end to end

Assessment:
- acceptable deferred scope
- should remain explicitly deferred rather than partially “fixed”

### 6. Runtime Naming Still Carries Older Shapes

Risk:
- Some runtime naming still reflects older concepts even though the architecture is cleaner.

Current seams:
- [CombatAnimationSet.cs](/Users/davidhedges/Projects/arena2/Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs)
  - `TryGetPhasedMeleeEntry(...)`

Why it matters:
- this invites mistaken mental models for future engineers or LLMs
- the runtime shape can still read like an older detached staged-action system

Assessment:
- low-to-medium risk
- mostly readability and future-maintenance cost

## Most Likely Future Token Sinks

These are the issues most likely to waste debugging time later:

1. A new melee attack whose authored id and runtime slot id do not line up in a boring way.
2. A phased melee attack that behaves differently from a normal strike under the same interrupt scenario.
3. A prediction/replay mismatch where local predicted visuals are remembered under one identity and replay arrives under another.
4. A same-frame conflict between:
   - `Play(...)`
   - `SetTrigger(...)`
   - `CrossFade(...)`
5. A future attempt to “just wire up charge quickly” without validating the full category end to end.

## Acceptable Debt vs Cleanup Candidates

### Acceptable For Now

- reusable strike/spell bank topology
- heuristic prediction replay windows
- deferred charge validation

### Should Be Cleaned Up If More Bugs Land Here

- authored id vs runtime id translation sprawl
- same-frame phased-melee arbitration fragility
- runtime naming that still implies detached staged-action architecture

## Recommendation

Do not reopen broad architecture work unless real bugs continue to cluster in the same seam.

If new bugs appear, prioritize in this order:

1. authored id vs runtime id mismatch
2. phased-melee same-frame base-layer contention
3. replay suppression timing/identity mismatch
4. runtime naming / surface-area cleanup if two designs start appearing again

The current goal should be:
- stabilize the remaining interrupt behavior seam
- avoid new compatibility layers
- only pay down the next assumption if it produces a real bug
