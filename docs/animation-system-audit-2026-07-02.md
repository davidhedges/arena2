# Animation System Architecture Audit - 2026-07-02

Senior audit of player/NPC animation architecture. Scope: combat action playback, hit/status reactions, weapon/profile binding, locomotion/stance, VFX timing, animator layers, and editor/validator tooling. Goal: reduce future animation bugs by fixing ownership and source-of-truth contracts, not by patching individual clips.

Companion contracts: `docs/combat-animation-authoring-contract.md`, `docs/animation-event-timing-migration-plan-2026-05-10.md`, `docs/combat-animation-current-concerns-2026-05-06.md`.

## What is healthy (do not "fix" these)

- The extraction sequence from the 2026-05 concerns doc **landed and is real**: `CombatAnimationSetBinder` owns slot binding, `CombatStatusReactionController` owns reaction parameter/trigger/slot policy, `CombatActionPlaybackController` owns shared playback state (bank slots, preemption decisions, lower-body unlock state). `PlayerAnimator` delegates via thin proxies (`PlayerAnimator.cs:2409-2458`) and no duplicated playback machinery was found.
- `Assets/Arena/Tests/Editor/CombatAnimationVisualInterruptTests.cs` pins the runtime contract well: layer order, HitReaction layer shape, bank-slot ownership, event-reading behavior, remote catch-up math. This test file is the right enforcement pattern to extend.
- `CombatAnimationSetProtection` (asset postprocessor) guards authored sets against import-time data loss.
- Root motion is correctly disabled with an explanatory comment (`PlayerAnimator.cs:286-292`), matching the contract.
- One canonical animator controller (`Arena_Character.controller`) for players; no parallel controllers found.

## State of the event-timing migration (load-bearing context)

The 2026-05-10 plan is **stranded in a state the plan explicitly warned against**:

- Phase 2 (runtime reader) landed, but not as designed. `CombatAnimationEvents` lives in `CombatAnimationSet.cs:10-98`. The plan specified an intermediate phase where missing events **fall back to the serialized field values**. The shipped code instead falls back to constants: release → `0f`, lower-body unlock / visual interrupt → clip length (`CombatAnimationSet.cs:604-639`, same for melee at `WeaponMeleeAttackAuthoring`). Errors log only in `UNITY_EDITOR || DEVELOPMENT_BUILD` (`CombatAnimationSet.cs:64-68`).
- Phase 3 (bulk migrator `AnimationEventMigrator.cs`) was **never built**. Only 56 of 2,264 extracted `.anim` files carry events.
- Phase 5 (field deletion) never ran: `groundEffectTime`, `airEffectTime`, `lowerBodyUnlockAtSeconds`, `lowerBodyBlendOutSeconds`, `visualInterruptibleAtSeconds` still exist, still hold non-zero values in 4 of 5 sets (SwordAndShield, Daggers, Staff, TwoHandedSword; ArcherBow is clean), and are **dead** — the inspector shows knobs that do nothing.
- The only event validator, `CombatVFXAuthoringValidator`, gates its melee/spell event checks on `TWO_HANDED_SWORD` only, and is a manual menu item. No validator runs in CI or tests (no `executeMethod`/test-runner wiring under `ops/` or `infrastructure/`).
- `PlayerAnimator` still carries the constants the plan slated for replacement: `SpellCastHoldEnterToIdleNormalizedTime = 0.85`, `SpellCastHoldExitDelaySeconds = 0.35`, `SpellCastHoldExitCrossFadeDurationSeconds = 0.28` (`PlayerAnimator.cs:153-160`), plus phased-melee normalized-time trio `0.82/0.84/0.88` (`PlayerAnimator.cs:164-166`). `SpellCastHoldProfile.exitDelaySeconds/exitBlendOutSeconds` remain **live** field timing (`CombatAnimationSet.cs:499-508`) — the same asset now mixes dead timing fields with live ones.

Consequence: the runtime source of truth is **clip events, full stop** — but authoring, validation, and the asset inspector have not caught up. Any new spell/strike wired without stamped events silently releases at t=0 or becomes uninterruptible until clip end, with no error in player builds and no validator coverage outside greatsword.

---

## 1. Top improvement opportunities (priority order)

### R1. Finish the event-timing migration: one source of truth for in-clip timing, validated for every profile

**Classification:** correctness fix + validation/test improvement (completing an approved migration, not a redesign).

**Evidence**
- Runtime event-only reads with constant fallbacks: `CombatAnimationSet.cs:604-639` (spell), melee equivalents in `WeaponMeleeAttackAuthoring`; dev-only error at `CombatAnimationSet.cs:64-68`.
- Dead-but-authored fields: `CombatAnimationSet.cs:527-536` (tooltips say "Obsolete"), non-zero values in `Assets/Arena/Resources/CombatAnimationSets/{SwordAndShield,Daggers,Staff,TwoHandedSword}.asset`.
- Validator scoped to one profile: `CombatVFXAuthoringValidator.cs` (~line 422 gate on `TWO_HANDED_SWORD`; event checks at ~403-528).
- Plan of record: `docs/animation-event-timing-migration-plan-2026-05-10.md` Phases 3-5 + its "Red Flags" section, which this state violates ("Do not delete the timing fields without running Phase 3 migrator first" — fields weren't deleted, but the fallback path was, which is behaviorally identical).

**Current problem.** Dual authoring surfaces where only one works; per-weapon validator coverage; silent conservative fallbacks in player builds. This is the single largest generator of future "animation played wrong / VFX fired at the wrong time" bugs as the roster grows.

**Better contract.** Clip events are the sole in-clip timing source (already true at runtime). Authoring and validation must match: every referenced clip in a role that requires events has them; dead fields are gone from the inspector; the validator covers **all** combat profiles and runs as an editor test, not a menu item.

**Why this improves the system.** It collapses "where does this number come from?" to one answer per timing concept, makes missing timing an authoring-time error instead of a runtime guess, and unblocks Phase 5 cleanup that removes the misleading knobs authors currently edit to no effect.

**Implementation slices** (in order; each independently shippable)
1. **Validator generalization:** in `CombatVFXAuthoringValidator.cs`, remove the `TWO_HANDED_SWORD` gate so melee/spell/phased/stagger event checks run for every `CombatAnimationSet`. Report-only first run; expect findings on Daggers/Staff/SwordAndShield/ArcherBow.
2. **Editor test wrapper:** new `Assets/Arena/Tests/Editor/CombatAnimationEventContractTests.cs` that invokes the validator's core (refactor its `Validate()` into a testable static returning errors list) and asserts zero errors. This makes the contract enforceable by the existing test runner without new infrastructure.
3. **Stamp missing events:** using `AnimationEventStamperWindow` (or a small batch utility reusing `CombatClipEventTemplates` defaults seeded from the still-present field values — this is the deferred Phase 3 migrator, scoped to referenced clips only, skip-if-exists). Field values are the best available migration source; use `field value` (seconds) or `groundEffectTime * clip.length` for release, per the original plan's conversion table.
4. **Phase 5 cleanup:** delete the five dead fields from `WeaponSpellAnimationEntry`/`WeaponMeleeAttackAuthoring`, update `CombatAnimationSetEditor`/`SpellAuthoringWindow` write sites. Only after slices 1-3 are green.
5. **Hold-exit timing:** migrate `SpellCastHoldProfile.exitDelaySeconds/exitBlendOutSeconds` and the `PlayerAnimator` hold constants (`0.85/0.35/0.28`) to `OnEnterComplete`/`OnHoldFadeStart`/`OnHoldFadeEnd` events with the constants demoted to fallback defaults — exactly as the plan's `PlayerAnimator` section specifies. Phased-melee normalized-time trio (`0.82/0.84/0.88`) similarly moves to per-clip events (`OnPhaseLoopReady` already exists in templates) in a later pass.

**Likely files/assets touched.** `Assets/Arena/Editor/CombatVFXAuthoringValidator.cs`, new test file, `Assets/Arena/Runtime/Presentation/Animation/CombatAnimationSet.cs`, `Assets/Arena/Editor/CombatAnimationSetEditor.cs`, `Assets/Arena/Editor/SpellAuthoringWindow.cs`, stamped `.anim` files under `Assets/Arena/Content/Animation/Extracted/`, the five `CombatAnimationSets/*.asset` (field removal re-serialization only — do not touch clip references).

**Verification.** Editor test suite green; validator silent across roster; Unity manual: Icicle cast-hold → release identical before/after (plan's own acceptance criterion), one melee strike per weapon plays with unchanged hit/unlock feel. Slice 3 requires in-Unity visual spot checks — clip-length changes between the field era and now could make migrated values wrong.

**Risks / non-goals.** Do not regenerate or flatten set assets (protected by `CombatAnimationSetProtection` and the authoring contract). Do not migrate server-read timing (hit windows, cast_time_ms) to events. Stamping wrong times is the main risk — that is why the validator+test land first and stamping is report-then-apply.

---

### R2. Make binding/resolution failures loud, and add a manifest drift check

**Classification:** correctness fix + validation/test improvement.

**Evidence**
- `CombatAnimationSetCatalog.Resolve` caches `null` and returns it silently forever (`CombatProfileIds.cs:37-48`); no combatProfileId uniqueness check (last-loaded wins).
- Server-side profile resolution silently falls back to `SWORD_AND_SHIELD` (`GameplayContracts.cs:549-580`).
- `CombatAnimationSetBinder.Bind` assigns override slots by string; a missing slot state is a silent no-op.
- Melee timing export to `server/src/melee_manifest.shared.json` is a manual inspector button (`CombatAnimationSetEditor.cs:77, 279-282`); the Rust server compiles the JSON in (`server/src/melee.rs:119`). Nothing detects a stale export, so server gameplay timing and client presentation timing drift invisibly after any strike edit.

**Current problem.** The weapon → profile → set → slots chain has four silent failure points; the server/client timing bridge has zero drift detection. These produce the worst class of bug: everything runs, presentation is subtly wrong.

**Better contract.** Resolution failures log once per key (dev builds) at the layer that failed; an editor test enforces (a) every `combat_profile_id` referenced by `server/src/progression_catalog.shared.json` resolves to exactly one `CombatAnimationSet`, (b) `combatProfileId` uniqueness across sets, (c) **manifest drift**: `BuildMeleeExport()` (`CombatAnimationSet.cs:1649-1667`) re-serialized from current assets equals the committed `melee_manifest.shared.json` byte-for-byte (or semantically). Stale export becomes a red test instead of a gameplay-feel mystery.

**Implementation slice.** New `Assets/Arena/Tests/Editor/CombatBindingContractTests.cs` (three asserts above, reusing existing export serialization); one-line `Debug.LogError` in `CombatAnimationSetCatalog.Resolve` null path; one-time log in `CombatProfileResolver.ResolveForOwner` fallback. No behavior changes.

**Likely files touched.** `CombatProfileIds.cs`, `GameplayContracts.cs`, new test file. Read-only use of `CombatAnimationSetEditor` export code (may need a small refactor to expose serialization outside the inspector).

**Verification.** Tests pass on current tree (or immediately expose an existing stale manifest — either result is valuable). No Unity manual check needed.

**Risks / non-goals.** Don't auto-export the manifest on save (hidden writes to server source are worse than a red test). Don't change the default-profile fallback behavior itself — logging only.

---

### R3. Data-drive the NPC animation path before NPC content growth hardens it

**Classification:** architecture improvement + authoring/tooling improvement.

**Evidence**
- `NpcAnimationController.cs` is a parallel hardcoded stack: kobold template IDs as C# constants (`:18-21`), hit-state candidate arrays (`:22-29`), per-template ready/attack state switches (`:312-334`), timer-based return-to-idle with clip-length clamp `0.5-6s` (`:369-382`), its own `AnimatorOverrideController` swap for status loops (`:253-277`). No knockdown, no stagger, no directional hits, no `CombatAnimationSet`.
- Status→clip mapping exists **twice**: `CombatAnimationSet.statusReactions[]` (players) and `NpcVisualCatalog` entries (NPCs).
- Recent commits ("npc initial work", "kobolds spawning", "fixed hit animations", "npm animations") show this is the active growth area — every new NPC archetype currently means editing C# switch statements.

**Current problem.** NPC animation meaning lives in code, violating the project's own core rule ("combat animation meaning must live in authored data"). Player and NPC reaction policy will drift (they already do: NPCs ignore knockdown/stagger).

**Better contract.** Keep NPCs on their simpler crossfade runtime (full unification onto the player slot/bank system is **not** justified — different rigs, different controllers), but move all *selection data* into `NpcVisualCatalog` entries: `readyStates[]`, `attackStates[]`, `hitStates[]`, locomotion state names, run-speed threshold. `NpcAnimationController` becomes a generic interpreter with zero template-specific code. Share the two small pure-policy pieces with players by extracting them to static helpers: directional trigger resolution (`CombatStatusReactionController.ResolveDirectionalHitTrigger`, `:247-268`) and status-kind normalization/suppression precedence (currently duplicated string sets across `EntityRegistry.cs:37-42`, `PlayerEntity.cs`, `NpcEntity.cs`, `CombatStatusReactionController.cs:189-190` — one `CombatStatusKinds` static class).

**Implementation slice.** (1) Add the state-list fields to `NpcVisualCatalog` entry type + author current kobold values into `Assets/Arena/Resources/NpcVisualCatalog.asset`; (2) replace the switches in `NpcAnimationController` with catalog lookups (behavior-identical); (3) extract `CombatStatusKinds` and replace the scattered string literals.

**Likely files touched.** `NpcAnimationController.cs`, `NpcVisualCatalog.cs`, `NpcVisualCatalog.asset`, `EntityRegistry.cs`, `NpcEntity.cs`, `PlayerEntity.cs`, `CombatStatusReactionController.cs`, new `Assets/Arena/Runtime/Combat/CombatStatusKinds.cs`.

**Verification.** Editor test: every catalog entry's state names exist in that template's animator controller (loadable in editor). Unity manual: each kobold template idles/walks/runs/attacks/gets hit/gets stunned exactly as before.

**Risks / non-goals.** Do not put NPCs on `CombatAnimationSet` or the player controller. Do not add NPC knockdown/stagger in this slice (behavior-preserving refactor first; new reactions are a follow-up with authored clips).

---

### R4. Single declared animator contract + drift test; delete the orphaned mask

**Classification:** validation/test improvement (with a small architecture improvement).

**Evidence**
- Layer indices duplicated: `PlayerAnimator.cs:169-173` (0,1,3,4,5) and `CombatStatusReactionController.cs:24` (2). Slot-name strings scattered: binder slot map, `slot_hard_crowd_control_loop` (`CombatStatusReactionController.cs:25`), `slot_upper_body_recovery_1` (`PlayerAnimator.cs:174`).
- `HitReactionMask.mask` is **orphaned**: its GUID appears zero times in `Arena_Character.controller` (verified); the HitReaction layer is full-body override, and `CombatAnimationVisualInterruptTests` pins that as intended. The mask asset is misleading authoring surface.
- `CombatAnimatorControllerInventory` audits controller states/params against an ownership manifest but is menu-only. The authoring contract's "Target Validation" list (every parameter referenced by code/validation/allowlist, no stale states) is unimplemented.

**Current problem.** The controller/code contract is enforced by discipline plus a partial test. Reordering layers or renaming a slot state fails silently at runtime (binder no-ops, `Play()` on wrong layer).

**Better contract.** One runtime-visible static class `ArenaCharacterAnimatorContract` (in `Assets/Arena/Runtime/Presentation/Animation/`) declaring layer name→index pairs, required parameters, and required slot-state names. `PlayerAnimator`, `CombatStatusReactionController`, and `CombatAnimationSetBinder` consume it. An editor test walks `Arena_Character.controller` and asserts the contract (extending the existing inventory/test logic). Delete `HitReactionMask.mask` (or wire it deliberately — decision is presentation-tuning; deleting matches current pinned behavior).

**Implementation slice.** Mechanical: introduce the contract class, replace the duplicated constants, extend `CombatAnimationVisualInterruptTests` (or a new file) to assert every declared layer/parameter/slot exists at the declared index/name, and that every controller parameter is declared or allowlisted.

**Likely files touched.** New contract class; `PlayerAnimator.cs`, `CombatStatusReactionController.cs`, `CombatAnimationSetBinder.cs`, `CombatAnimatorControllerInventory.cs` (share the manifest), test file; delete `HitReactionMask.mask(.meta)`.

**Verification.** Tests green; compile-only otherwise. No Unity manual check needed beyond entering play mode once.

**Risks / non-goals.** No controller edits in this slice. Do not "fix" the HitReaction layer to use the mask — full-body is the pinned, tested behavior; a masked variant is a separate visual-tuning decision.

---

### R5. Implement the contract's priority rule: hard CC preempts committed action presentation

**Classification:** correctness fix — **requires Unity runtime confirmation before and after**.

**Evidence**
- Contract priority table (`docs/combat-animation-authoring-contract.md`, "Priority And Interruption"): stun/hard CC outranks committed melee or cast.
- Stagger cancels action presentation via injected callback → `PlayerAnimator.ClearPresentationForStagger` (`PlayerAnimator.cs:2460-2476`). Hard CC does **not**: `CombatStatusReactionController.SetHardCrowdControl` (`:85-128`) clears only hit-reaction presentation. The MeleeAttack (3) and SpellAction (4) layers sit *above* Base, where `HardCrowdControlLoop` plays — so a strike or cast mid-flight should visually override the stun loop until it ends.
- Knockdown (`SetKnockedDown`, `:59-83`) has the same gap.

**Current problem.** Getting stunned/knocked down mid-swing likely shows the swing finishing over the CC loop — contradicting the authored priority table. (Server correctly rejects new actions; this is presentation-only.)

**Better contract.** `CombatStatusReactionController` gains one more injected callback (mirroring the stagger pattern — no new ownership in `PlayerAnimator`): `clearInterruptiblePresentationForHardControl`, invoked on hard-CC entry and knockdown entry, wired to the existing `ClearNonDeathPresentation`-style cancellation (`PlayerAnimator.cs:2431-2452` already contains exactly the needed sequence — reuse, don't duplicate).

**Implementation slice.** Constructor parameter + two call sites in `CombatStatusReactionController`; wiring in `PlayerAnimator`'s lazy controller construction. ~20 lines.

**Verification.** **Unity manual, mandatory:** (1) reproduce the bug first — stun a player mid-greatsword-swing and mid-cast-hold, confirm the swing/cast visually completes; (2) after fix, confirm CC loop takes over immediately, and CC exit restores locomotion cleanly; (3) confirm stagger and death behavior unchanged. Also verify remote-player view.

**Risks / non-goals.** Do not clear presentation when the same status refreshes (guard on `alreadyHardCrowdControlled`/`statusKindChanged`, which the method already computes). Do not touch server logic.

---

## 2. Safest first slice

**R2's editor tests + R1 slice 1-2 (validator generalization behind a test), report-only.** Zero runtime behavior change, zero asset mutation, and it converts the two biggest silent-failure classes (missing events, stale manifest, broken profile chain) into red tests. It also produces the authoritative worklist for R1 slice 3 (exactly which clips need stamping) before anyone edits an asset. Everything later in R1 depends on this inventory existing.

## 3. Required Unity / manual verification checklist

Static analysis cannot confirm these; they need play-mode or scene checks:

1. **Baseline the validator findings visually.** After generalizing the validator (R1.1), for each reported missing-event clip, confirm in play mode what the conservative fallback currently looks like (release at t=0, uninterruptible-until-end) so post-stamping changes are attributable.
2. **Icicle regression** (plan's own acceptance case): cast-hold → release fade identical after each R1 slice.
3. **Per-weapon strike check** after event stamping: one melee strike per profile (SwordAndShield, Daggers, Staff, ArcherBow phased draw/loose, TwoHandedSword) — hit moment, lower-body unlock while strafing, interrupt-by-dodge ghosting.
4. **R5 bug reproduction and fix confirmation:** stun mid-swing and mid-cast; knockdown mid-swing; local and remote player views.
5. **Kobold parity** after R3: idle/walk/run/attack/hit/stun per template, plus one template with a deliberately wrong state name to confirm the new validation catches it.
6. **HitReactionMask deletion** (R4): take a hit in and out of combat, grounded and airborne — confirm full-body reaction unchanged.

## 4. Smaller-model implementation checklist (ordered)

1. Refactor `CombatVFXAuthoringValidator` core into a static method returning `List<string>` errors; keep menu item as a thin wrapper. Remove the `TWO_HANDED_SWORD` gate on event checks.
2. Add `CombatAnimationEventContractTests` (asserts validator errors empty — initially `[Explicit]`/allowlisted while findings are triaged) and `CombatBindingContractTests` (profile resolution completeness, combatProfileId uniqueness, melee manifest drift).
3. Add one-time error logs: `CombatAnimationSetCatalog.Resolve` null path; `CombatProfileResolver.ResolveForOwner` default fallback.
4. Build the scoped Phase 3 stamper (referenced clips only, seed from field values, skip-if-exists, per-asset summary log) as `Assets/Arena/Editor/AnimationEventMigrator.cs` per the 2026-05-10 plan; run report-only, then apply after human review of the report.
5. After manual verification (§3.2-3): delete the five obsolete timing fields + editor write sites; flip validator warnings to errors; remove the test allowlist.
6. R5: add `clearInterruptiblePresentationForHardControl` callback to `CombatStatusReactionController` (CC entry + knockdown entry), wired in `PlayerAnimator`. Gate behind the existing already-active/kind-changed checks.
7. R4: introduce `ArenaCharacterAnimatorContract`; replace duplicated layer indices/slot strings; extend controller-contract test; delete `HitReactionMask.mask`.
8. R3: add state-list fields to `NpcVisualCatalog`; author kobold values; replace `NpcAnimationController` switches with lookups; extract `CombatStatusKinds`; add catalog↔controller state-name test.
9. R1 slice 5 (hold-exit + phased-melee constants → events) as a separate later PR, with §3.2/3.3 re-verification.

Each step is a separate commit/PR; none should mix asset edits with code edits except step 4's stamping (assets only).

## 5. Deferred / speculative — do not implement now

- **Playables-based segment player for phased melee.** The contract already names the bank-state workaround as acceptable until it generates transition bugs. No current evidence it does. Revisit only if phased-melee transition bugs recur after R1 slice 5.
- **Unifying NPCs onto CombatAnimationSet / the player controller.** Different rigs and third-party controllers; R3's data-driving delivers most of the value at a fraction of the risk.
- **Build-time event→catalog extraction (server reads clip events).** Plan's open question 3; premature below ~50 strikes. The R2 drift test covers the actual current risk.
- **Removing ghost machinery / `OnVisualInterruptible`.** Plan's open question 1 — requires visual A/B in Unity, not a code decision.
- **Re-masking the HitReaction layer.** Visual tuning; current full-body behavior is pinned by tests and presumably accepted.
- **Renaming `aerialExecutionMode` serialization.** Label-only churn; the contract already handles it via editor labels.
- **Auto-exporting the melee manifest on asset save.** Hidden writes into `server/src/` are worse than a red drift test.
