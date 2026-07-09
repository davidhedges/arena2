# Branch audit — `spell-presentation-redesign` (2026-07-10)

Multi-agent code review of `git diff main...HEAD` (~50 commits, 36 substantive code files, ~5,800 lines: Rust server, Unity runtime/editor C#, ops). 8 finder angles → dedup → per-candidate adversarial verification. Every finding below survived verification with the verdict shown; refuted candidates are listed at the end so they aren't re-chased.

**Working convention for the fix chat:** check items off as they land; each finding is independently fixable unless grouped.

---

## P0 — Aura VFX lifecycle (decision 11) is broken end-to-end

Three independent defects; the feature cannot work until all three are fixed. Latent today only because no `UNTIL_AURA_END` cue exists in the catalog and no SchoolVfxSet authors the Aura slot yet. **Fix as one work item.**

- [x] **A. Client never subscribes to `active_aura`** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/CombatVFXDispatcher.cs:185` hooks `ActiveAura.OnDelete`, but `GameplaySubscriptionPlanner` has no `active_aura` query and all three `NetworkManager` Subscribe calls go through the planner. The branch made the table public (`server/src/combat.rs:526`) but never subscribed, so no rows enter the client cache and `OnActiveAuraDeleteForVfx` is dead code. Aura VFX would persist until scene teardown.

- [x] **B. Aura switch/re-cast never fires teardown** — CONFIRMED
  `server/src/combat.rs:1421-1422`: `set_active_aura` UPDATEs the one-row-per-owner in place when an aura already exists; the client registers no `ActiveAura.OnUpdate`. Switching THORNS_AURA → WARDING_AURA stacks the old looping VFX with the new (per-cue keys differ, so they only die together on full toggle-off). Fix: handle OnUpdate client-side, or delete+insert server-side — decide deliberately.

- [x] **C. Prefab path never registers `UNTIL_AURA_END`** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/CombatVFXLifecycleRegistry.cs:293`: `SpawnPrefab` branches on PARTICLE_SYSTEM / UNTIL_RELEASE_EVENT / UNTIL_CAST_END only; an UNTIL_AURA_END prefab cue (generator authors duration_ms 0) falls to the fallback — destroyed after `FallbackDurationSeconds` (3s), never registered in `_prefabs`, so `DestroyForAuraEnd` can't match it. Generated Aura-slot cues use school-palette prefabs, i.e. exactly this path.

## P1 — Reachable today

- [x] **1. Hold exit fade stomps follow-up UpperBody actions** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs:1130-1140` (`UpdateSpellCastHoldFadeOut`). The fade owns the layer for ExitDelay+ExitBlendOut and is only cancelled by a new hold (:1303) or a same-layer spell release (:1469-1476). Block raise (:2064/2083/2098), weapon draw/sheath (:498/537), and upper-body phased melee (:901/2367) all enter via guard-free `PlayUpperBodyState` (:2818) on the UpperBody layer — the composer's default hold layer for non-left-hand-1H casts — and get dragged to weight 0 then stomped to Empty mid-motion.

- [x] **2. Write gate wedges on SelfFlash/AuraGround/Aura slots** — CONFIRMED
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:530` (`BuildCatalogBySlot`). Round-trip is inference-only: the editor `CombatVfxCueDefinition` model (SpellAuthoringWindow.cs:761) never reads the `slot` key the writer itself inserts, and `TryInferLegacySlot` (:830-881) can't represent SelfFlash/AuraGround/Aura. After writing such a slot (RequestedSlots emits them for real APPLY_STATUS/AURA spells), reopening the spell shows false CATALOG-ONLY/uninferrable diffs and `writable` stays false forever. Fix at the right altitude: read back the authored `slot` key instead of extending the inference table.

- [x] **3. Illegal zero-duration cue survives publish** — CONFIRMED
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:398`: `PalettePositive` materializes `entry.DurationMs` with no positive guard; `ValidateWiring` checks only the policy enum and has no non-test call sites; `sync_combat_vfx_cue_catalog` (progression.rs:3383) does no rule validation and the shared Rule-14 checker is invoked only inside `#[cfg(test)]`. A SchoolVfxSet slot with `selfTerminating=false, durationMs=0` (fresh-entry default) writes an illegal ONE_SHOT/DURATION/0 row; only a later `cargo test` catches it; runtime plays the 3s fallback. Fix: guard at generation time (and/or surface in ValidateWiring wired into the preview).

- [x] **4. Runtime animation-resolution failures are now silent** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/CombatActionPlaybackController.cs:998`: the per-cast "no spell animation entry" warning was deleted deferring to the author-time validator, but `CombatVFXAuthoringValidator.cs:251` still resolves via explicit-entry-only `TryGetSpellAnimation`, skips non-selectable (:238) and profile-less (:242) abilities, and never validates map baseNames against the library. Zero LogWarning/LogError anywhere in the new resolver/composer/library. This is the safety net for the ~90-spell migration — restore a runtime warning or teach the validator the map/library path **before migrating more spells**.

## P2 — Confirmed/plausible, latent (gated on content or timing that doesn't exist yet)

- [ ] **5. `DeriveArchetype` defaults to Instant on missing SpellDefinition** — PLAUSIBLE
  `Assets/Arena/Runtime/Presentation/Animation/SpellCastAnimationResolver.cs:98-100`. Conn null / rows not yet synced → channel spell composes as ReleaseOnly → `PlaysSpellReleasePresentation` flips true → `EntityRegistry.OnCombatCast` stops suppressing the release — the exact hold-preemption desync this branch fixed, reintroduced timing-dependently. No channel spell is map-migrated yet; becomes real as migration proceeds. Consider: fail resolution (fall back to explicit entry path) instead of guessing Instant.

- [x] **6. Hold-fade preserve guard only exists for LeftGesture** — PLAUSIBLE
  `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs:1153` / guard at :2844. `ClearActiveSpellPresentation → ResetSpellLowerBodyUnlockState` can hard-play Empty on a fading UpperBody layer via a contingent chain (full-body charged release keeps fade alive → movement marks unlock → second clear in window). Snap-to-default-pose artifact the reorder was meant to remove. Fixing #1 properly (fade cancellation policy) likely subsumes this.

- [x] **7. JSON escape corruption in the catalog writer** — CONFIRMED (mechanism)
  `Assets/Arena/Editor/SpellCueCatalogWriter.cs:585-596`: `ReadJsonString` decodes `\uXXXX` to literal `u`+hex (backslash dropped); `EscapeJsonString` (:415) escapes only backslash+quote, so decoded `\n`/`\t` re-serialize as raw control chars (invalid JSON). Current catalog has zero backslash escapes, but the writer's byte-preservation promise is false the day one appears. Fix: handle `\u`/`\n`/`\t`/`\r` in both directions.

- [x] **8. Editor cast-hand inference ignores the composed path** — CONFIRMED
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:348-366` + SpellAuthoringWindow.cs:457: hand inference reads only explicit `animationSet.spells` entries; a map-migrated spell (Fireball today) has `hasAnimationEntry=false` and falls back to LEFT_HAND, never consulting `set.OneHandedCastHand` (which runtime honors, possibly Right). Latent: only Left is serialized anywhere today.

- [x] **9. `oneHandedCastHand=Right` silently composes unmasked** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/Animation/SpellCastAnimationComposer.cs:36-37`: all three layer resolvers key on `OneHand && Left`; only LeftGesture layer/states exist; `CombatAnimationSet.cs:1209` collapses TwoHand→Left. Authoring Right compiles and resolves but drops all weapon-arm masking with no validator/warning (limitation acknowledged only in a comment). Either validate-and-reject Right at authoring time or don't expose it until a RightGesture substrate exists. (Fix together with #8.)

- [ ] **10. NPC-cast projectile spells would lose impact VFX** — PLAUSIBLE
  `Assets/Arena/Runtime/Presentation/CombatVFXDispatcher.cs:490`: SPELL_IMPACT suppression for projectile-delivered spells relies on `projectile_presentation_event`, which is subscribed only via the PlayerWorld semijoin (GameplaySubscriptionPlanner.cs:605), while combat_event has a dedicated NPC-caster query (:583). No NPC casts spells today (the FIREBALL_TURRET practice actor is registered in player_world, so it's covered). Add the NPC semijoin when NPC spellcasting arrives — or now, cheaply.
  Related altitude note (CONFIRMED): `IsProjectileDeliveredSpellImpact` (:499-515) re-derives "fires projectiles" as `PROJECTILE || (CHANNEL && Speed>0)`, a divergent copy of the generator's `firesProjectiles`; they agree today only because catalog.rs copies projectile speed into definition.speed. Prefer carrying the fact on the wire (flag on SpellDefinition or cue row).

## Cleanup (all verified CONFIRMED)

- [x] **C1. Delete dead `SpellAnimationResolver.cs` + its grep-test**
  `Assets/Arena/Runtime/Presentation/Animation/SpellAnimationResolver.cs` — all five types have zero production callers (runtime uses `SpellCastAnimationResolver`); abandoned template-layering design. Its test `Resolver_TriesExplicitEntryFirst…` (SpellAnimationResolverTests.cs:72-88) literally `File.ReadAllText`s the source and asserts substrings. Keep `SpellAnimationArchetype.cs` (used).

- [x] **C2. Retire the generator half of `server/src/vfx_generation.rs` (~600 lines)**
  Design-of-record (docs/spell-presentation-dry-redesign-2026-07-07.md, decision 10, line 311) says the Rust module "retires to being the server-side validator's Class-A rule source", but `derive_anim_mode`/`derive_vfx_archetype`/`requested_slots`/`wire`/`validate_wiring` + their types survive with only their own `#[cfg(test)]` callers; progression.rs uses only `check_cue_field_rules`/`CueFields`/`CueFieldViolation`. Cut to the checker (~200 lines).

- [x] **C3. Kill the seed `SchoolPalettes` fallback**
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:51` (seed), :497-510 (`TryResolvePaletteEntry` asset-then-seed with no warning on fallback), :441-487 (`ExternalizeSchoolPalettes` unconditionally overwrites hand-edited assets via the `updated++` path). Two live sources of truth; a typo'd/renamed asset silently reverts generation to stale seed values. Delete seed + fallback + menu item; make a missing school a loud error.

- [ ] **C4. CLAUDE.md: PlayerAnimator maintenance-mode violation**
  Branch adds 4 private static fields (`LeftGestureSpellCastHoldAction1-4StateHash`, PlayerAnimator.cs:127-130), private `ResolveLeftGestureSpellCastHoldStateHash` (:1601), and hold fade-out policy (:1156-1165, :1463-1477, :2840-2846) with nothing extracted out. Extract the hold/fade policy into its own controller when fixing P1 #1 — that pays both debts at once.

- [ ] **C5. CLAUDE.md: SchoolVfxSet assets in Resources/ with no Resources.Load consumer**
  `Assets/Arena/Resources/SchoolVfxSets/*.asset` — only consumer is editor `AssetDatabase.FindAssets` (CueGeneration.cs:423). They ship in every player build. Move out of Resources/ (or wire the planned runtime registry merge that would justify the location).

- [x] **C6. `"UNTIL_AURA_END"` literal defined 3× in one assembly**
  `SpellVfxGenerator.cs:276` (public const), `CombatVFXDispatcher.cs:36`, `CombatVFXLifecycleRegistry.cs:20` — spawn-side tag and teardown filter compare independent copies. Reference the public const. (Fold into the P0 aura work.)

- [ ] **C7. Editor tool duplication**
  `SpellCastAnimationResolvedWindow.cs`: third copy of the catalog-path constant (vs SpellAuthoringWindow.cs:17, CoreAbilityAuthoringWindow.cs:14), private JsonUtility models, hardcoded `SPELL_/PALADIN_/WARRIOR_` prefix list (:147-153) — an unmatched future prefix (MAGE_) silently derives those spells as Instant in the migration-critical view. Byte-identical `FindFirst<T>` in SpellCastAnimationMapEditor.cs:117 and ResolvedWindow:155; the scan-all-CombatAnimationSets loop rewritten divergently in both. Hoist into one shared editor helper.

- [ ] **C8. Per-spell authoring data compiled into editor source**
  `SpellAuthoringWindow.CueGeneration.cs:89` (`SignatureOverrides`, 19 spells) and :203 (`CastHandOverrides`, "WINS over inference"). The school half of the same data just became assets (decision 10); signatures/hand overrides should follow (reuse the SchoolVfxSlotEntry shape as a per-spell VFX-set) — every new bespoke spell currently requires editing generator source + domain reload, and a stale hand override silently outranks later-corrected inference.

- [x] **C9. `_assetSchoolPalettes` static-as-hidden-parameter**
  `SpellAuthoringWindow.CueGeneration.cs:418` — assigned at top of `GenerateCues`, read in `TryResolvePaletteEntry` same call. Make it a local passed as a parameter. (Dies naturally with C3.)

## Efficiency (all verified CONFIRMED)

- [ ] **E1. Memoize `TryResolveComposed`** — `SpellCastAnimationResolver.cs:40`: full pipeline (map scan, library scan, Db.Find, compose, overrides) re-runs per query; a single cast queries 3-5× (SpellInputHandler:666/705, EntityRegistry:646/1357, SpellCastPresentationController:146/182/223, CombatActionPlaybackController:998, PlayerAnimator:1268) plus per ActiveCast update. Memoize per (spellId, set, hand) next to `_library/_map`, cleared by `InvalidateCache`. **Caution: interacts with finding #5 — don't make a pre-sync Instant answer sticky.**
- [ ] **E2. Pre-normalized dictionary in `SpellCastAnimationMap`** — :67 re-normalizes every entry per linear-scan lookup (O(n) string allocs per query).
- [ ] **E3. Cache `IsProjectileDeliveredSpellImpact`** — `CombatVFXDispatcher.cs:499-515`: 2× Normalize + Db.Find per terminal spell-impact event; memoize by ActionKind, invalidate on catalog update.
- [ ] **E4. Stop regenerating cues per repaint** — `SpellAuthoringWindow.cs:60 → CueGeneration.cs:298/382`: every OnGUI repaint re-runs GenerateCues incl. `AssetDatabase.FindAssets` + per-asset loads. Cache keyed by selected ability; invalidate on selection change/focus/reload.
- [ ] **E5. Precompute resolved rows in `SpellCastAnimationResolvedWindow.Reload()`** — :88: per-repaint LINQ scans with fresh closures + TryCompose per hand.

## Refuted — do not re-chase

| Candidate | Why refuted |
|---|---|
| Hyphenated-id normalization divergence (map/writer/school Normalize vs WireIdentifier.Normalize) | Server ingest replaces `-`→`_` (`server/src/action_ids.rs:43-44`); catalog has zero hyphenated ids; a hypothetical hyphen causes a uniform miss, not split-layer divergence. Still fine to unify on WireIdentifier.Normalize as hygiene. |
| Boomerang self-return suppression drops legit caster impacts | Server emits hit==caster only on the zero-damage return catch (`projectiles.rs:920-941`, `:754-765`; enemy loops skip caster); world-impact visual still plays (`CombatVFXDispatcher.cs:568`), only the hit cue is skipped. A `BoomerangReturning` field exists if we ever want data instead of the identity heuristic. |
| SPELL/ABILITY owner-kind double-dispatch after migration write | For the sole SPELL-kind row (ICE_SPIKES) the slot infers ambiguous → write button disabled; and even if written, the CueOverrideKey matches and `CombatVfxCueResolver` suppresses the SPELL row. No double dispatch. Writer matching by owner_id-only is still worth tightening someday. |
| combat_profile_id Error→Info downgrade lost a guard | Design-of-record endorses profile-less shared spells as the norm (redesign doc line 21, decision 10 line 291); the validator skip pre-exists the branch. Intended. |

## Suggested fix order

1. **P0 aura trio** (one work item; include C6) — decision 11 is inert until then.
2. **P1 #4** (restore resolution-failure visibility) — before any further spell migrations.
3. **P1 #1 + P2 #6** (hold-fade policy; extract per C4 while in there).
4. **P1 #2 + #3** (write-gate round-trip + duration guard) — unblocks Aura/SelfFlash authoring, which P0 makes meaningful.
5. Cleanup C1-C3, C7-C9, then efficiency E1-E5 opportunistically (E1 with #5 in mind).
