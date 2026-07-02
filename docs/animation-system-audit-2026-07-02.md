# Animation System Architecture Audit - 2026-07-02

Senior audit of the animation system **as built**. Focus, per observed gameplay: presentation stability — stuttering, stuck/looping animations, and full-body vs half-body conflicts. Gameplay hit timing is reported healthy and is treated as lower priority (§7).

All claims verified against source at the cited lines.

## 0. What is healthy — do not "improve" these

- The controller extraction is real and non-duplicated: `CombatAnimationSetBinder` (slot binding), `CombatStatusReactionController` (reaction triggers/slots), `CombatActionPlaybackController` (playback decisions/state), thin delegation from `PlayerAnimator` (`PlayerAnimator.cs:2409-2458`).
- Playback *decisions* are pure and unit-tested without an Animator (`CombatAnimationVisualInterruptTests.cs`). Preserve this property.
- Root motion off, documented at the call site (`PlayerAnimator.cs:286-292`). One player controller. Server-authoritative timing boundary respected. `CombatAnimationSetProtection` guards authored assets.

## 1. The core finding: no arbiter over a six-layer Override stack

`Arena_Character.controller` runs six always-on Override layers (Base, UpperBody[masked], HitReaction[full-body], MeleeAttack, SpellAction, LeftGesture[masked]). On top of that graph, **at least ten independent code subsystems write animator state/weights directly, several of them every frame, with no coordination point**:

`PlayerAnimator.Update()` (`:2478-2545`) runs per frame: `UpdateLocomotion`, `UpdateMovementOneShots` (stop/turn triggers), `UpdateWeaponVisualHandoff`, `UpdatePhasedMeleePlayback`, `UpdateSpellCastHoldPlayback`, `UpdateSpellCastHoldFadeOut`, `UpdateMeleeLowerBodyUnlock`, `UpdateSpellLowerBodyUnlock`, presentation-entered latching, then `RecoverLocomotionFromTransientStates` — plus event-driven writers (`SetInCombat`, block/parry, dodge, reactions, preemption clears).

Writer inventory (state writes `Play`/`CrossFade`, weight writes `SetLayerWeight`):

| Layer | State writers | Weight writers |
|---|---|---|
| 0 Base | `SetCombatVisualImmediate:406`, `SetInCombat` ×4 (`:462,477,501,516`), `SetDead:543`, block loop/exit (`:1974,1990`), watchdog `RecoverLocomotionFromTransientStates:2632`, rejump `:2878`, **plus the controller graph's own transitions**, plus CC/knockdown/death trigger-driven states | — (always 1) |
| 1 UpperBody | `PlayUpperBodyState:2642` (**hard `Play`, no blend**) called from stance handoff, block, spell overlay, melee recovery handoff, clears ×6 | — |
| 2 HitReaction | reaction triggers + `Play(Empty)` (`CombatStatusReactionController.cs:225`) | — |
| 3 MeleeAttack | strike triggers, `Play(Empty)` ×3 (`:1737,1835,2367`) | unlock fader `:842`, snap-to-1 `:876` |
| 4 SpellAction | spell triggers, hold crossfade `:1279`, `Play(Empty)` ×6 (`:1075,1090,1115,1370,2451,2469`) | **three subsystems**: unlock fader `:925`, hold-exit fader `:1086`, snap-to-1 ×5 (`:1074,1091,1103,1277`, reset paths) |
| 5 LeftGesture | `:2650-2659` | set-to-1 only, never faded |

Entry into action states uses **triggers + graph transitions**; exit uses a mix of **graph transitions, explicit `Play(Empty)`, and a per-frame code watchdog**. State liveness is detected by **polling state hashes**, which races the animator's end-of-frame trigger processing — a race the code already documents and half-patches with an "entered" latch (`PlayerAnimator.cs:2500-2506` comment).

`docs/combat-animation-current-concerns-2026-05-06.md` §5 records that dual ownership of transitions "was the source of replay/hiccup behavior" and set the rule "code owns entry, controller owns exits." As built, **both** own exits (watchdog + graph), **both** own entries (triggers routed through graph transitions), and weights have up to three owners. The stutter/loop/half-body symptoms are the predictable output of this arrangement.

## 2. Symptom → mechanism map (verified)

### S1. Stutter / one-frame pops

1. **Competing weight programs on SpellAction (layer 4).** `UpdateSpellCastHoldFadeOut` (`:1080-1093`) and `UpdateSpellLowerBodyUnlock` (`:895-926`) both `SetLayerWeight(4, …)` per frame with different curves; last writer wins while both are active, and when one finishes the other resumes mid-curve — a weight jump. Worse, the Update clear block (`:2530-2536`) calls `ClearActiveSpellPresentation(resetLayerWeight: true)` — **snapping weight to 1** (`:1103`) — then starts the hold fade (`softFullBodyClear`), so the character pops to full cast pose for a frame before fading. Every moving cast release crosses this path.
2. **Hard `Play` handoffs at mask seams.** Lower-body unlock starts upper-body continuation with `Play(state, layer1, normalizedTime)` — a snap, no crossfade (`:2642`, callers `:862, :917`) — while layer 3/4 weight fades. Arms crossfade between two copies of the same clip whose times were matched by hand math; any mismatch judders. Left-gesture clears are also snaps (`:2659`).
3. **The base-layer watchdog vs everything else.** `RecoverLocomotionFromTransientStates` (`:2609-2635`) crossfades layer 0 to locomotion whenever a state it classifies "transient" passes a min normalized time — a shadow exit state machine in code, running every frame, on the same layer the graph, `SetInCombat`, block, dodge, and rejump also drive. Two exit owners on one layer = periodic double-transition stutter.
4. **Trigger entry racing trigger processing.** Bank entry via `SetTrigger` is consumed at animator evaluation, after all Updates; state-hash polling in the same frame sees stale state (documented race, `:2500-2506`). Latched triggers that miss their transition window fire later — a spurious replay reads as stutter or a repeated strike.
5. **AnimatorOverrideController writes mid-gameplay.** `ApplyHitClipOverrides` rebinding four slots on grounded/combat change (`:2094-2112`), hard-CC loop override swaps (`CombatStatusReactionController.cs:168-178`), and per-strike bank writes each trigger a controller rebind — a known hitch source at exactly the moments the user notices (getting hit, status changes). Needs profiler confirmation, but the write pattern is the risky one.

### S2. Stuck / looping animations

1. **No loop-flag guardrail exists.** Nothing validates `clip.isLooping` against the slot's role — not `CombatAnimationSetEditor`, not `CombatVFXAuthoringValidator` (only the one-off Intimidate tool ever touches `loopTime`, `IntimidateAnimationAuthoring.cs:91-140`). Third-party clips import with arbitrary loop flags. A looping clip in a one-shot slot (strike, hit, stagger, get-up, death) loops in place until some other writer rescues it; a non-looping clip in a loop slot (cast hold idle, CC loop, block loop) freezes on the last frame. This is **bad clip data with no guardrail** — cheap to fence, high symptom yield.
2. **Presentations that never "enter" are never cleared.** The Update cleanup only clears a presentation after its Entered latch is set (`:2505-2536`). If a trigger is swallowed (transition interruption, same-frame preemption), Entered stays false and the stale presentation — including a hold loop — persists indefinitely. There is no timeout. The latch itself is a patch over the S1.4 race.
3. **Trigger latching** (S1.4) replays one-shots after the fact.

### S3. Full-body vs half-body wrongness

1. **Unlock is gated on movement at one instant.** `ShouldReleaseLowerBodyToLocomotion` (`:845-848`) checks locomotion magnitude at threshold-crossing time. Move for one frame → unlock commits → weight fades → then stand still: half-body recovery over idle legs (contract says a stationary character should keep full-body settling). Inverse: stand still through the window → full-body lock for the whole clip even if you start moving late.
2. **`UpperBodyWhileMoving` decides its layer once, at cast start** (`CombatAnimationSet.cs:550-556`): start a cast stationary → full-body spell layer locks legs even if you move immediately after; start moving → upper-body overlay even if you stop. Presented as a policy field, but it is a snapshot, not a policy.
3. **HitReaction layer (2) is full-body** (no mask — `HitReactionMask.mask` exists but its GUID appears **zero** times in the controller) and sits **below** MeleeAttack/SpellAction: reactions take the whole body when idle but are entirely invisible during your own actions. Meanwhile hard CC and knockdown play on **Base (0)**, below everything, and their entry clears only hit-reaction presentation (`CombatStatusReactionController.cs:59-128`) — unlike stagger, which clears action presentation via callback (`:149` → `PlayerAnimator.cs:2460-2476`). A stun/knockdown mid-swing leaves the swing playing over the CC loop. Priority is implemented by static layer order plus ad-hoc clears, with these two holes.

### Secondary (unchanged findings, lower priority)
- NPCs are a second animation system in C# switch statements (`NpcAnimationController.cs:18-29, 312-334`), with a second status→clip mapping (`NpcVisualCatalog`); active growth area.
- Silent failure economy: catalog caches `null` per profile (`CombatProfileIds.cs:37-48`), binder no-ops on missing slots, missing-event errors dev-only, manifest export manual with no drift detection. No validator runs in CI.
- Timing sources are a three-mechanism maze (events/fields/constants with per-concept precedence; 9 of 13 event names never read at runtime; manifest export prefers events over the hit-window fields the editor requires, `CombatAnimationSet.cs:1737-1761`). **Gameplay impact currently low by observation** — this is drift risk, not present pain.

## 3. Target contract

**One writer per layer, one mechanism per lifecycle edge.**

1. **Every animator write goes through one gate.** All `Play`/`CrossFade*`/`SetLayerWeight`/`SetTrigger`/`ResetTrigger` calls in `PlayerAnimator` and `CombatStatusReactionController` route through a single `AnimatorWriteGate` facade that records (frame, layer, kind, owner-tag). Dev builds surface conflicts: two owners writing the same layer's weight or state in one frame; triggers set but unconsumed after N frames; presentations never Entered after N seconds. This is first because with stutter bugs, **instrumentation beats speculation** — it converts "it stutters sometimes" into "owner A fought owner B on layer 4 at frame N" during a live repro.
2. **One weight program per layer.** `CombatActionPlaybackController` already stores both spell fade states; it exposes exactly one `ResolveSpellActionLayerWeight(now)` (precedence: hold-exit fade > unlock fade > 1), and `PlayerAnimator` applies it from exactly one call site per frame. Snap-to-1 happens only when no program is active. Same for MeleeAttack.
3. **One exit owner per state family, declared.** Action layers (1,3,4,5): code owns entry *and* exit via direct `CrossFadeInFixedTime` (no triggers, no graph exit dependence). Base-layer transients (stops, turns, stance, dodge, jump-land): pick graph *or* watchdog per family and delete the other; the choice is recorded in the contract doc and pinned by the controller-contract test.
4. **Loop flags are role-validated.** One-shot slots require non-looping clips; loop slots require looping clips; enforced across every clip referenced by every `CombatAnimationSet` and `NpcVisualCatalog` entry.
5. **Priority has no holes:** hard CC and knockdown entry clear action presentation exactly as stagger does.
6. **Destination (staged): a Playables action stage.** `AnimationLayerMixerPlayable` with input 0 = `AnimatorControllerPlayable(Arena_Character.controller)` (locomotion/stance/reactions/death unchanged) and code-owned masked action inputs replacing layers 3/4/5 and the action states on 1. This makes the target contract true *by construction*: one writer, explicit weights, no triggers, no state polling, no watchdogs, no override-rebind hitches for banks, interruption = pause-and-fade an input, remote catch-up = `SetTime(t)`. Migrate one action category at a time behind a flag, deleting each retired state/trigger cohort. The bank-slot architecture (21 bank states, ~16 triggers, phased melee borrowing other strikes' slots `CombatActionPlaybackController.cs:225-237`, two mesh-baking ghost systems, normalized-time constants) is the structural cause of §2; the tooling that polices the controller is its scar tissue.

## 4. Recommendations (priority order)

### R1. `AnimatorWriteGate`: instrument every animator write, flag same-frame conflicts
**Class:** validation/diagnosis (zero behavior change). **Symptoms:** all of §2, especially S1.
**Slice:** facade class + mechanical call-site replacement in `PlayerAnimator.cs` and `CombatStatusReactionController.cs`; dev-only ring buffer + `Debug.LogWarning` on: multi-owner same-frame layer writes, unconsumed triggers (>5 frames), never-Entered presentations (>0.5s). Optional on-screen overlay toggle.
**Files:** new `Assets/Arena/Runtime/Presentation/Animation/AnimatorWriteGate.cs`; call-site edits in the two files.
**Verification:** compile + play; reproduce a stutter and read the log. No visual change expected.
**Risks:** none material; keep the gate allocation-free (pre-sized buffers) since it sits on the hot path.

### R2. Single weight program per action layer; kill the snap-then-fade pop
**Class:** correctness fix. **Symptoms:** S1.1 (moving cast release pop; dual-fader oscillation).
**Evidence:** `PlayerAnimator.cs:895-926, 1080-1093, 1103, 1074-1091, 2530-2536`.
**Slice:** merge hold-exit and unlock weight resolution into one `CombatActionPlaybackController` method with declared precedence; single application site in `Update`; remove `resetLayerWeight: true` snaps when a fade program is pending. Mirror for MeleeAttack (`:842, :876`).
**Verification:** Unity manual — moving cast release (Icicle while strafing): no full-body pop at release; melee lower-body unlock while strafing: smooth. Existing `LowerBodyUnlockPlaybackState` unit tests extended for precedence.
**Risks:** low; logic consolidation of code that already exists.

### R3. Loop-flag role validation + stuck-presentation timeout
**Class:** validation (clip data) + correctness fix (runtime). **Symptoms:** S2.
**Slice:** (a) extend `CombatAnimationSetEditor` validation + a new editor test: every referenced clip's `isLooping` matches its slot's role (one-shots: strikes, phased start/end, hits, stagger, knockdown start, get-up, death, draw/sheath, dodge, jump; loops: cast-hold idle, CC/status loops, block loop, knockdown loop, phased loop, locomotion). Include `NpcVisualCatalog` reaction loops. (b) runtime: if a presentation is set but not Entered within 0.5s, force-clear it through the existing clear paths with a dev log (removes the "stuck hold loop forever" class).
**Files:** `CombatAnimationSetEditor.cs`, new test, `PlayerAnimator.cs` (Update cleanup block), `CombatActionPlaybackController.cs` (timestamp on presentation set).
**Verification:** run validation — expect real findings across 2,264 imported clips; fix flags on referenced clips only (import-setting edits, content-preserving). Unity: spam-cast/attack while dodging to exercise the timeout path.
**Risks:** flipping a loop flag changes that clip's visual behavior by definition — review each finding rather than bulk-apply.

### R4. Close the priority holes: hard CC / knockdown clear action presentation
**Class:** correctness fix. **Symptoms:** S3.3. **Reproduce in Unity first.**
**Slice:** second injected callback on `CombatStatusReactionController` (CC entry + knockdown entry, guarded by existing already-active/kind-changed checks) wired to the existing stagger-style clear. ~20 lines.
**Verification:** stun and knockdown mid-swing/mid-cast, local + remote; stagger/death unchanged.

### R5. One exit owner per base-layer state family
**Class:** architecture improvement. **Symptoms:** S1.3, and the historical "replay/hiccup" class the concerns doc records.
**Slice:** enumerate the watchdog's recovery table (`TryResolveLocomotionRecovery`) against the controller's exit transitions for the same states; for each family, delete one side (recommendation: keep the code watchdog — it already encodes the gates — and strip the graph's competing exit transitions); pin the result in the controller-contract test. Use R1 telemetry to order the worst offenders first.
**Verification:** per-family Unity checks (stops, turns, stance enter/exit, dodge recovery, jump-land, block exit) at standstill and while moving.
**Risks:** medium; do one family per commit.

### R6. Playables action stage (the destination)
**Class:** architecture improvement, staged. **Symptoms:** §2 root cause; makes R1's contract structural.
**Prereqs:** R1-R3 landed (instrumentation + tests pin behavior through the swap).
**Slices:** (1) driver skeleton (mixer over `AnimatorControllerPlayable`), flag on, nothing migrated — prove locomotion/reactions/death untouched; (2) phased melee (most workaround-laden: slot borrowing + three normalized-time constants die); (3) single-clip strikes (Strike1-4 cohort + melee triggers die; remote catch-up becomes `SetTime`); (4) spells incl. hold/overlay/left-gesture (layers 4-5 cohorts die; the S1.1/S1.2 machinery is replaced by mixer weights; S3.1/S3.2 become continuous policies — re-evaluable per frame — instead of one-shot snapshots); (5) re-evaluate ghost layers (pause-and-fade may replace mesh baking).
**Verification:** per-category §5 sweep + rapid-input spam + latency-simulated remote view.
**Risks:** the only recommendation with real regression surface; hence flag, category staging, and tests-first ordering. The controller keeps locomotion/stance/reactions/death permanently — this is not a controller rewrite.

### Held at lower priority (still real)
- **NPC data-driving + `CombatStatusKinds` consolidation** — schedule with the next NPC content batch.
- **Contract tests in CI** (profile resolution, manifest drift, controller contract) — fold into R3's test file work.
- **Timing-source consolidation to entry-level fields** — hygiene now, not pain; do opportunistically during R6 slice 4 when spell timing reads get touched anyway. The one near-term piece worth keeping: a manifest drift test, since the export silently prefers stamped events over the hit-window fields the editor requires.

## 5. Unity / manual verification checklist

1. With R1 in, reproduce each recurring stutter/loop/half-body case and capture the conflict log — this both validates the diagnosis in §2 and orders R2/R5 work by observed frequency.
2. Moving cast release (before/after R2): strafe-cast Icicle; watch for the full-body pop at release.
3. Melee lower-body unlock: strike then move mid-recovery; strike while moving then stop (S3.1 both directions).
4. R4 reproduction: stun/knockdown mid-swing and mid-cast-hold; local + remote.
5. Loop-flag findings (R3): eyeball each flipped clip in its slot before committing.
6. R5 per-family: stops/turns/stance/dodge/jump-land/block exits, standing and moving.
7. Profiler capture during hit-reaction moments to confirm/deny override-rebind hitches (S1.5) before spending any effort on it.

## 6. Smaller-model implementation checklist (ordered)

1. `AnimatorWriteGate` + call-site routing + dev conflict log (R1). One PR, no behavior change.
2. Weight-program consolidation for SpellAction, then MeleeAttack (R2).
3. Loop-flag validation in editor + editor test; report first, apply reviewed fixes to referenced clips (R3a).
4. Never-Entered presentation timeout (R3b).
5. Hard-CC/knockdown clear callback (R4), after the §5.4 repro is recorded.
6. Exit-owner dedup, one state family per commit, ordered by R1 telemetry (R5).
7. Contract tests (profile resolution, manifest drift, controller contract, NPC state names) — mergeable any time after step 3.
8. R6 slices 1-5 behind a flag, each ending in cohort deletion.

## 7. Explicitly deprioritized / rejected

- **Completing the 2026-05-10 field→event timing migration:** rejected as a priority. Runtime reads events with silent constant fallbacks; 9 of 13 event names have no runtime reader; adoption stalled at 56/2,264 clips — but per gameplay observation this is not where bugs are. Long-term direction (entry-level timing) folds into R6 slice 4. Mark the plan superseded.
- **NPCs onto CombatAnimationSet / player controller:** rejected; data-drive the catalog instead.
- **Splitting the `CombatAnimationSet` god-asset:** tolerable with Protection + custom editor; revisit after R6.
- **Deleting ghost machinery now:** decide during R6 slice 5 with eyes on screen.
- **Re-masking the HitReaction layer:** visual tuning; delete the orphaned `HitReactionMask.mask` as hygiene, change behavior only as a deliberate presentation decision.
