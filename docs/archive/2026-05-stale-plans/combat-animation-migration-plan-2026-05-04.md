# Combat Animation Migration Plan - 2026-05-04

This plan migrates the current combat animation system toward the contract in `docs/combat-animation-authoring-contract.md` without destroying existing `CombatAnimationSet` assets or breaking working locomotion, jumping, draw/stow, dodge, charge, block, and reaction behavior.

## Goals

- Preserve current `CombatAnimationSet` authoring work.
- Keep Hit Windows as gameplay contact/release timing.
- Add lower-body unlock as a separate presentation concept.
- Keep visual interruption as presentation cleanup/replacement timing.
- Make melee, casts, and movement actions understandable to future developers and LLMs.
- Treat gameplay-owned displacement as an explicit action capability instead of forcing attack-first versus movement-first taxonomy decisions.
- Reduce reliance on Animator Controller graph spaghetti as the source of combat meaning.

## Non-Goals

- Do not replace all combat animation playback in one pass.
- Do not regenerate or flatten existing animation set assets.
- Do not change server hit timing while adding presentation phase metadata.
- Do not change auto-attack cadence, pending impact scheduling, or projectile release timing as part of lower-body unlock.
- Do not enable root motion as an authority model.
- Do not remove existing draw/stow, locomotion, jump, dodge, charge, block, hit, stun, knockdown, or death behavior until replacement behavior is proven.

## Current Findings

- `PlayerAnimator` is a large orchestration hub for locomotion, melee, spells, movement actions, reactions, weapon handoff, ghosting, and replay suppression.
- The Animator Controller has too many flat states and transitions on the Base Layer, making the graph unreadable.
- `CombatAnimationSet` is the correct source of authored combat presentation data and already contains substantial work.
- `MeleeAttack` and `SpellAction` layers are currently unmasked/full-body action layers.
- Existing melee visual interruption exists, but lower-body unlock is not represented separately.
- Phased melee no longer plays directly on Base Layer. Runtime playback advances start/loop/end clips as segments on the combat action layer, then continues on the upper-body recovery layer after lower-body unlock.
- The controller currently has a layer at index 2 with no named constant in `PlayerAnimator`; inventory must classify it before any layer renumbering, deletion, or ownership rewrite.
- Hit Windows export into `server/src/melee_manifest.shared.json` and drive server `PendingMeleeImpact` or `PendingProjectileRelease` scheduling.
- The editor already has asset protection for combat animation set losses.

## Phase 1 - Lock The Contract

Status: implemented.

Tasks:

- Completed: add `docs/combat-animation-authoring-contract.md`.
- Completed: link it from `docs/combat-authoring-contract.md`.
- Completed: document action taxonomy, layer ownership, Hit Windows, recovery, lower-body unlock, visual interruption, gameplay-owned displacement, cast actions, root motion, reaction priority, and LLM guardrails.
- Completed: name `CombatAnimationSet` as the preserved source of truth for existing animation authoring.

Acceptance:

- Future animation work has one stable contract to cite.
- The contract clearly states that Hit Windows are not visual interruption or lower-body unlock timing.

## Phase 2 - Inventory And Validation Before Runtime Changes

Status: implemented for contract-critical checks. Full stale-state cleanup was scoped in Phase 10; remaining low-value legacy graph cleanup is explicitly parked.

Tasks:

- Completed: add editor validation and editor tests for Animator layer order, recovery state, recovery slot, and lower-body recovery readiness.
- Completed: record current Animator layer ownership in the contract and tests:
  - `Base Layer`
  - `UpperBody`
  - `HitReaction`
  - `MeleeAttack`
  - `SpellAction`
- Completed in Phase 10: stale parameter/state deletion inventory and ownership classification.
- Completed: validate `CombatAnimationSet` melee timing:
  - strike id exists
  - timing reference exists
  - at least one resolved hit window exists
  - hit window normalized times are inside `[0, 1]`
  - exported impact delays are non-negative
- Completed: add validation for phase fields:
  - `lowerBodyUnlockAtSeconds <= timingReferenceLength`
  - `visualInterruptibleAtSeconds <= timingReferenceLength`
  - `lowerBodyUnlockAtSeconds <= visualInterruptibleAtSeconds` when both are set
- Completed: mark future checks clearly as target validation until implemented.

Acceptance:

- Validation can identify stale layers/params without changing runtime behavior.
- Existing assets pass or produce explicit legacy warnings.

## Phase 3 - Preserve Assets And Rename Confusing Authoring UI

Status: implemented.

Tasks:

- Completed: keep serialized `aerialExecutionMode` for compatibility.
- Completed: rename editor section label `Execution` to `Caster Requirement`.
- Completed: rename editor label `Attack Environment` to `Caster Movement State`.
- Completed: update tooltips to explain:
  - `Grounded Only`
  - `Grounded Or Airborne`
  - `Airborne Only`
- Completed: keep server wire value `aerial_execution_mode` until a separate schema migration is justified.

Acceptance:

- Existing asset data is unchanged.
- New UI stops reinforcing the misleading execution/environment terminology.

## Phase 4 - Add Presentation Phase Metadata

Status: implemented.

Tasks:

- Completed: add optional fields to `WeaponMeleeAttackAuthoring`:
  - `lowerBodyUnlockAtSeconds`
  - `lowerBodyBlendOutSeconds`
  - keep existing `visualInterruptibleAtSeconds`
- Completed: add resolver methods on `CombatAnimationSet`:
  - `GetLowerBodyUnlockAtSeconds(int strikeIndex)`
  - `GetLowerBodyBlendOutSeconds(int strikeIndex)`
  - existing `GetVisualInterruptibleAtSeconds(int strikeIndex)` remains
- Completed: default unset lower-body unlock to timing reference length.
- Completed: default invalid lower-body blend-out to `0.12s`.
- Completed: update inspector UI near visual interruption.
- Completed: do not export lower-body unlock into the melee manifest. This remains presentation-only.

Acceptance:

- Existing attacks behave the same when fields are unset.
- Designers can configure lower-body unlock independently from hit timing and visual interruption.

## Phase 5 - Narrow Melee Runtime Prototype

Status: implemented and manually proven on the first single-clip melee path.

Scope:

- Single-clip melee only.
- One combat profile first, preferably one visible melee clip with obvious recovery sliding.
- Phased melee remained on existing behavior during the narrow prototype only; it is migrated in Phase 6.

Prototype action:

- Authored strike id: `COMBO_ATTACK_1_1_HIGH_TO_LOW`
- Current Hit Window: `timeNormalized = 0.148`
- Initial `lowerBodyUnlockAtSeconds`: `1.4`; tune per asset after visual review.
- Current `visualInterruptibleAtSeconds`: `1.5`; tune per asset after visual review.
- Initial `lowerBodyBlendOutSeconds`: `0.12`; tune per asset after visual review, or set `0` if blending feels worse.
- Lower-body unlock should not occur before the hit/release timing by default. If an author needs that later, make it explicit and warn in validation.

Tasks:

- Completed: spiked same-source-clip playback through full-body melee plus masked upper-body recovery using `Animator.Play` with captured normalized time.
- Completed: kept Base Layer locomotion running underneath the combat action layer path.
- Completed: used the `MeleeAttack` layer for committed full-body melee.
- Completed: added a dedicated `UpperBodyRecoveryAction1` state and `slot_upper_body_recovery_1` motion on the existing masked `UpperBody` layer.
- Completed: stopped using `UpperBodySpellAction4` as a melee recovery slot.
- Runtime behavior:
  - play the clip full-body from time 0
  - schedule lower-body unlock from `CombatAnimationSet`
  - once the unlock timestamp has elapsed and locomotion is demanded, blend full-body action layer down
  - continue same source clip/time on upper-body masked recovery layer
- Completed: preserve existing `visualInterruptibleAtSeconds` behavior for whether replacement creates a ghost.
- Completed: add tracing for:
  - action id
  - strike index
  - lower-body unlock time
  - visual interruption time
  - active layer/body mode

Acceptance:

- A configured melee attack regains locomotion in the legs during recovery.
- Hit Windows and server impact timing do not change.
- Incoming eligible attacks can still replace recovery after their visual threshold.
- Running, jumping, draw/stow, dodge, charge, and block still behave as before outside the prototype path.

## Phase 6 - Expand Melee Coverage

Status: implemented for the reusable single-clip and phased melee runtime paths. Content tuning remains future work.

Tasks:

- Completed: apply lower-body unlock runtime support to single-clip melee attacks when authored.
- Completed: add editor fields and fallback rules for `lowerBodyUnlockAtSeconds`, `lowerBodyBlendOutSeconds`, and `visualInterruptibleAtSeconds`.
- Completed: add validation for configured lower-body unlock values:
  - after timing reference resolves to Auto
  - after visual interruption warns
  - before first Hit Window warns
  - authored lower-body unlock requires the upper-body recovery controller state/slot
- Completed: add editor coverage summary showing which single-clip melee attacks still use legacy full-body fallback.
- Completed: tune representative proof points. Current tuned examples:
  - `COMBO_ATTACK_1_1_HIGH_TO_LOW`
  - `COMBO_ATTACK_1_3_GROUND_TO_AIR`
- Completed: migrate phased melee off Base Layer. Phased melee uses the same general combat full-body and combat upper-body layers as single-clip melee, with start/loop/end represented as runtime action segments.
- Do not create a permanent bespoke phased-melee layer unless the same layer abstraction also serves other segmented combat actions. The preferred model is segmented data over special-purpose layers.
- Completed: phase semantics are explicit in runtime orchestration:
  - `lowerBodyUnlockAtSeconds` is measured against runtime segmented elapsed time.
  - `visualInterruptibleAtSeconds` is measured against runtime segmented elapsed time.
  - after lower-body unlock, remaining phased segments continue on the upper-body recovery layer.
- Implementation note: current phased playback rotates start/loop/end through different reusable strike-bank states on the `MeleeAttack` layer. This is an Animator-controller workaround for the existing strike states' exit-to-Empty transitions; replaying the same state for every segment can abruptly fall out after the first clip. Do not treat strike-bank rotation as the ideal long-term segment player. If phased or segmented action needs grow, replace this with an Animation Playables combat segment player that does not depend on Animator state topology.

Acceptance:

- Single-clip melee has consistent phase behavior as each attack is explicitly tuned or intentionally left on legacy fallback.
- Phased melee no longer requires Base Layer ownership for combat action sequencing.
- Phased melee has segment-aware timing and is no longer behind an explicit temporary legacy flag.

## Phase 7 - Reaction Priority

Status: implemented.

Tasks:

- Implement and verify reaction priority from the contract:
  - Completed: stun preempts active hit reaction by clearing the `HitReaction` layer before triggering stun.
  - Completed: knockdown clears active hit reaction before triggering knockdown.
  - Completed: knockdown preempts active stun, including the same-refresh case where both statuses are present.
  - Completed: death clears non-death combat/reaction presentation before entering death.
- Completed: coalesce same-hit outcomes by deferring ordinary hit reactions by one frame and suppressing them if stun/knockdown status is present.
- Completed: add editor regression tests for hit reaction already playing when stun/knockdown/death arrives.
- Completed: keep existing "already stunned suppresses incoming normal hit" behavior.

Acceptance:

- Desired reaction priority is implemented, not just documented.
- Server event ordering does not cause hit flinch to fight stun/knockdown/death presentation.

## Phase 8 - Cast Actions

Status: runtime lower-body unlock for full-body spell actions is implemented. Broader cast policy fields such as channel loops, explicit cancel clips, and rooted/non-rooted gameplay rules remain future work.

Tasks:

- Completed: extend spell animation entries with presentation-only lower-body recovery fields beside the existing ground/air clip and effect-time axis:
  - `lowerBodyUnlockAtSeconds`
  - `lowerBodyBlendOutSeconds`
  - `visualInterruptibleAtSeconds`
- Completed: add lower-body unlock to full-body spell actions by fading the `SpellAction` layer down once locomotion is demanded, and continuing the same spell bank clip/time on the existing masked `UpperBodySpellActionN` state.
- Completed: preserve moving cast behavior that already uses upper-body presentation when `playbackLayer = UpperBody`.
- Completed: active spell presentations participate in the visual-interrupt gate; auto-attacks are suppressed before the authored threshold and allowed to replace the spell presentation after it.
- Future: extend spell animation entries with broader cast presentation policy:
  - standing full-body
  - moving upper-body
  - channeled upper-body
  - rooted full-body
  - interrupt/cancel behavior
- Preserve the existing ground/air clip and effect-time axis. Cast presentation policy is additive and orthogonal to grounded versus airborne clip selection.
- Future: add explicit priority rules for cast recovery versus hit reaction, stun, knockdown, death, and movement actions beyond the current death/stagger clearing behavior.

Acceptance:

- Existing spell entries are preserved.
- Moving casts can continue to preserve locomotion.
- Full-body casts can release lower body during recovery when configured.

## Phase 9 - Gameplay-Owned Displacement

Status: prototype started for dodge only. Gameplay-side gap closer data, `melee_gap_close_catalog`, `MovementActionState`, and `SpecialMovementRuntime` already exist. The first proof point is to make dodge presentation recover from the authoritative gameplay movement phase instead of the Animator state's clip exit time.

Tasks:

- Completed for dodge prototype: keep gameplay displacement server/client authored; Unity presentation metadata does not move the character.
- Completed for dodge prototype: copy authoritative `MovementActionState` phase times into presentation when the dodge starts, because the row is deleted when recovery ends.
- Completed for dodge prototype: allow grounded moving dodge presentation to recover to locomotion when `activeUntil` has elapsed instead of waiting for either `recoveryUntil` or the long dodge clip exit time. `activeUntil` is the end of gameplay-owned displacement; `recoveryUntil` remains the action lockout for fixed actions and spells.
- Future: treat displacement as an explicit capability/policy that can appear on melee, cast, or movement actions.
- Future: define movement-action presentation authoring shape before migrating broader runtime behavior.
- Future: define capability fields instead of relying on broad labels:
  - `displacement = none | gameplayOwned`
  - `impactModel = none | meleeHitWindows | projectileRelease | spellEffect`
  - `defenseModel = none | melee | spell | custom`
  - `targetingModel = none | self | target | direction | location`
- Future: add explicit segment model where needed:
  - start
  - loop
  - impact
  - recover
- Future: use `nonInterruptible` or equivalent policy in priority decisions after gameplay rules are explicit.
- Future: support lower-body unlock only in recovery segments where it makes visual sense.
- Future: align impact/release moments with server movement action timing and Hit Windows where applicable.
- Future: migrate charge/dash/gap-closer presentation to segment-aware playback if the current Animator graph remains hard to reason about.

Acceptance:

- Charge, dash, gap closer, and leaping attack presentation all use explicit displacement policy instead of ambiguous action identity labels.
- Movement authority remains gameplay-owned.
- Stitched movement actions are represented as segments instead of opaque Animator graph behavior.

## Phase 10 - Animator Controller Cleanup

Status: complete for this refactor. Cleanup was limited to inventory, validation, removal of proven-dead graph paths, and deprecation of automatic controller mutation. Remaining low-value legacy graph cleanup is parked.

Tasks:

- Completed: add a Phase 10 controller inventory utility that reports layers, parameters, states, and basic hygiene issues.
- Completed: add editor regression coverage that the controller inventory has no unnamed layers, unnamed parameters, or duplicate state names within a layer.
- Completed: deprecate the legacy `CombatAnimatorControllerUpgrader` auto-run path. The old upgrader is disabled because it mutates `Arena_Character.controller` and still knows about legacy Base Layer combat states. Phase 10 controller edits must be explicit, reviewed, and backed by inventory/validation.
- Completed: add Phase 10 ownership classification to the inventory. Current reusable spell banks are classified as owned; old generic cast states/params and `BlockHitBreak` are legacy-retained until runtime references and future spell/block needs are reviewed.
- Completed: remove stale phased-melee runtime writes to the old Base Layer graph parameters.
- Completed: delete the old Base Layer phased-melee controller states and parameters:
  - `PhasedMeleeStart`
  - `PhasedMeleeLoop`
  - `PhasedMeleeEnd`
  - `TriggerPhasedMeleeStart`
  - `IsPhasedMeleeActive`
  - `TriggerPhasedMeleeEnd`
- Completed: delete the stale Base Layer parry-start controller path:
  - `ParryStart`
  - `TriggerParry`
- Parked: keep old generic cast states/params as legacy-retained until real spell authoring proves whether they are useful or obsolete:
  - `UpperBody/CastDefault`
  - `UpperBody/CastUp`
  - `IsCasting`
  - `TriggerCastDefault`
  - `TriggerCastUp`
  - `TriggerCastInterrupt`
- Parked: keep `BlockHitBreak` as legacy-retained until block-break gameplay/presentation is reviewed.
- Parked: reorganizing the Animator graph into sub-state machines or replacing more sequencing with Playables is out of scope for this refactor.
- Completed: locomotion, jumping, stance, dodge, charge, block, and reaction behavior were preserved outside the scoped stale-path deletions.
- Rule for future cleanup: remove stale states/parameters only after code and validation prove they are unused.

Acceptance:

- Animator graph is readable enough to inventory and classify.
- Combat action meaning remains in `CombatAnimationSet` plus runtime orchestration, not graph spaghetti.
- No automatic editor script mutates `Arena_Character.controller` on load.
- Proven-dead Base Layer phased-melee and parry-start graph paths are deleted.

## Test Plan

Editor tests:

- `CombatAnimationSet` phase defaults preserve existing behavior.
- lower-body unlock and visual interruption validation catches impossible values.
- hit windows remain required and separate from phase fields.
- confusing execution/environment labels are no longer used in the custom drawer.
- Animator layer/parameter inventory validation can distinguish owned from legacy.

Runtime or PlayMode tests:

- melee before lower-body unlock remains full-body.
- melee after lower-body unlock restores locomotion lower body.
- melee before visual interruption still preserves existing ghost behavior.
- melee after visual interruption can be replaced without ghost.
- predicted local melee phase timestamps anchor to the predicted start time; authoritative duplicate replay does not re-anchor lower-body unlock or visual interruption.
- stun preempts hit reaction.
- death preempts all combat presentation.
- movement action presentation does not change authoritative displacement.

Server tests:

- existing melee manifest tests still pass.
- multi-hit damage still splits across Hit Windows.
- projectile release still schedules from Hit Windows.
- melee impact area `hit_index` validation still uses Hit Window count.

Manual verification:

- idle, walk, run, stop, turn
- jump, fall, land
- draw weapon, stow weapon
- single melee, combo melee, auto-attack
- moving during melee recovery
- cast while stationary and moving
- dodge
- charge or gap closer
- block, parry, guard hit
- hit reaction, stagger, stun, knockdown, get-up, death

## Rollback Plan

Every runtime phase should have an opt-out or unset-data fallback:

- unset lower-body unlock means old full-body behavior
- unset visual interruption keeps current fallback
- phased melee uses segmented playback; rollback would disable the phased segment player per action/profile
- movement actions keep current presentation until segment migration

If a runtime change regresses core movement or combat, disable the new phase path per action/profile and keep the additive data fields.
