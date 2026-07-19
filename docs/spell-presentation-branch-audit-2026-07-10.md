# Branch audit — `spell-presentation-redesign` (2026-07-10)

Multi-agent code review of `git diff main...HEAD` (~50 commits, 36 substantive code files, ~5,800 lines: Rust server, Unity runtime/editor C#, ops). 8 finder angles → dedup → per-candidate adversarial verification. Every finding below survived verification with the verdict shown; refuted candidates are listed at the end so they aren't re-chased.

**Working convention for the fix chat:** check items off as they land; each finding is independently fixable unless grouped.

---

## P0 — Aura VFX lifecycle — RETRACTED after owner clarification

The audit correctly identified that the proposed persistent-visual implementation was incomplete, but
the premise was wrong: only the aura **buff** is persistent. Its visual is intentionally a finite cast
flourish. The speculative `UNTIL_AURA_END` lifecycle, sustained `aura` slot, client `active_aura`
subscription/hydration, and public replication surface have therefore been removed. The server-only
`ActiveAura` gameplay row and its range-based recipient-buff refresh remain authoritative.

## P1 — Reachable today

- [x] **1. Hold exit fade stomps follow-up UpperBody actions** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs:1130-1140` (`UpdateSpellCastHoldFadeOut`). The fade owns the layer for ExitDelay+ExitBlendOut and is only cancelled by a new hold (:1303) or a same-layer spell release (:1469-1476). Block raise (:2064/2083/2098), weapon draw/sheath (:498/537), and upper-body phased melee (:901/2367) all enter via guard-free `PlayUpperBodyState` (:2818) on the UpperBody layer — the composer's default hold layer for non-left-hand-1H casts — and get dragged to weight 0 then stomped to Empty mid-motion.

- [x] **2. Write gate wedges on SelfFlash/AuraGround slots** — CONFIRMED
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:530` (`BuildCatalogBySlot`). Round-trip was inference-only: the editor `CombatVfxCueDefinition` model (SpellAuthoringWindow.cs:761) never read the `slot` key the writer itself inserted, and `TryInferLegacySlot` couldn't represent SelfFlash/AuraGround. After writing such a slot (RequestedSlots emits them for real APPLY_STATUS/AURA spells), reopening the spell showed false CATALOG-ONLY/uninferrable diffs and `writable` stayed false forever. The reader now honors the authored `slot` key.

- [x] **3. Illegal zero-duration cue survives publish** — CONFIRMED
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:398`: `PalettePositive` materializes `entry.DurationMs` with no positive guard; `ValidateWiring` checks only the policy enum and has no non-test call sites; `sync_combat_vfx_cue_catalog` (progression.rs:3383) does no rule validation and the shared Rule-14 checker is invoked only inside `#[cfg(test)]`. A SchoolVfxSet slot with `selfTerminating=false, durationMs=0` (fresh-entry default) writes an illegal ONE_SHOT/DURATION/0 row; only a later `cargo test` catches it; runtime plays the 3s fallback. Fix: guard at generation time (and/or surface in ValidateWiring wired into the preview).

- [x] **4. Runtime animation-resolution failures are now silent** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/CombatActionPlaybackController.cs:998`: the per-cast "no spell animation entry" warning was deleted deferring to the author-time validator, but `CombatVFXAuthoringValidator.cs:251` still resolves via explicit-entry-only `TryGetSpellAnimation`, skips non-selectable (:238) and profile-less (:242) abilities, and never validates map baseNames against the library. Zero LogWarning/LogError anywhere in the new resolver/composer/library. This is the safety net for the ~90-spell migration — restore a runtime warning or teach the validator the map/library path **before migrating more spells**.

## P2 — Confirmed/plausible, latent (gated on content or timing that doesn't exist yet)

- [x] **5. `DeriveArchetype` defaults to Instant on missing SpellDefinition** — PLAUSIBLE
  `Assets/Arena/Runtime/Presentation/Animation/SpellCastAnimationResolver.cs:98-100`. Conn null / rows not yet synced → channel spell composes as ReleaseOnly → `PlaysSpellReleasePresentation` flips true → `EntityRegistry.OnCombatCast` stops suppressing the release — the exact hold-preemption desync this branch fixed, reintroduced timing-dependently. No channel spell is map-migrated yet; becomes real as migration proceeds. Consider: fail resolution (fall back to explicit entry path) instead of guessing Instant.

- [x] **6. Hold-fade preserve guard only exists for LeftGesture** — PLAUSIBLE
  `Assets/Arena/Runtime/Presentation/PlayerAnimator.cs:1153` / guard at :2844. `ClearActiveSpellPresentation → ResetSpellLowerBodyUnlockState` can hard-play Empty on a fading UpperBody layer via a contingent chain (full-body charged release keeps fade alive → movement marks unlock → second clear in window). Snap-to-default-pose artifact the reorder was meant to remove. Fixing #1 properly (fade cancellation policy) likely subsumes this.

- [x] **7. JSON escape corruption in the catalog writer** — CONFIRMED (mechanism)
  `Assets/Arena/Editor/SpellCueCatalogWriter.cs:585-596`: `ReadJsonString` decodes `\uXXXX` to literal `u`+hex (backslash dropped); `EscapeJsonString` (:415) escapes only backslash+quote, so decoded `\n`/`\t` re-serialize as raw control chars (invalid JSON). Current catalog has zero backslash escapes, but the writer's byte-preservation promise is false the day one appears. Fix: handle `\u`/`\n`/`\t`/`\r` in both directions.

- [x] **8. Editor cast-hand inference ignores the composed path** — CONFIRMED
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:348-366` + SpellAuthoringWindow.cs:457: hand inference reads only explicit `animationSet.spells` entries; a map-migrated spell (Fireball today) has `hasAnimationEntry=false` and falls back to LEFT_HAND, never consulting `set.OneHandedCastHand` (which runtime honors, possibly Right). Latent: only Left is serialized anywhere today.

- [x] **9. `oneHandedCastHand=Right` silently composes unmasked** — CONFIRMED
  `Assets/Arena/Runtime/Presentation/Animation/SpellCastAnimationComposer.cs:36-37`: all three layer resolvers key on `OneHand && Left`; only LeftGesture layer/states exist; `CombatAnimationSet.cs:1209` collapses TwoHand→Left. Authoring Right compiles and resolves but drops all weapon-arm masking with no validator/warning (limitation acknowledged only in a comment). Either validate-and-reject Right at authoring time or don't expose it until a RightGesture substrate exists. (Fix together with #8.)

- [x] **10. NPC-cast projectile spells would lose impact VFX** — PLAUSIBLE
  `Assets/Arena/Runtime/Presentation/CombatVFXDispatcher.cs:490`: SPELL_IMPACT suppression for projectile-delivered spells relies on `projectile_presentation_event`, which is subscribed only via the PlayerWorld semijoin (GameplaySubscriptionPlanner.cs:605), while combat_event has a dedicated NPC-caster query (:583). No NPC casts spells today (the FIREBALL_TURRET practice actor is registered in player_world, so it's covered). Add the NPC semijoin when NPC spellcasting arrives — or now, cheaply.
  Related altitude note (CONFIRMED): `IsProjectileDeliveredSpellImpact` (:499-515) re-derives "fires projectiles" as `PROJECTILE || (CHANNEL && Speed>0)`, a divergent copy of the generator's `firesProjectiles`; they agree today only because catalog.rs copies projectile speed into definition.speed. Prefer carrying the fact on the wire (flag on SpellDefinition or cue row).

## Cleanup (all verified CONFIRMED)

- [x] **C1. Delete dead `SpellAnimationResolver.cs` + its grep-test**
  `Assets/Arena/Runtime/Presentation/Animation/SpellAnimationResolver.cs` — all five types have zero production callers (runtime uses `SpellCastAnimationResolver`); abandoned template-layering design. Its test `Resolver_TriesExplicitEntryFirst…` (SpellAnimationResolverTests.cs:72-88) literally `File.ReadAllText`s the source and asserts substrings. Keep `SpellAnimationArchetype.cs` (used).

- [x] **C2. Retire the generator half of `server/src/vfx_generation.rs` (~600 lines)**
  Design-of-record (docs/spell-presentation-dry-redesign-2026-07-07.md, decision 10, line 311) says the Rust module "retires to being the server-side validator's Class-A rule source", but `derive_anim_mode`/`derive_vfx_archetype`/`requested_slots`/`wire`/`validate_wiring` + their types survive with only their own `#[cfg(test)]` callers; progression.rs uses only `check_cue_field_rules`/`CueFields`/`CueFieldViolation`. Cut to the checker (~200 lines).

- [x] **C3. Kill the seed `SchoolPalettes` fallback**
  `Assets/Arena/Editor/SpellAuthoringWindow.CueGeneration.cs:51` (seed), :497-510 (`TryResolvePaletteEntry` asset-then-seed with no warning on fallback), :441-487 (`ExternalizeSchoolPalettes` unconditionally overwrites hand-edited assets via the `updated++` path). Two live sources of truth; a typo'd/renamed asset silently reverts generation to stale seed values. Delete seed + fallback + menu item; make a missing school a loud error.

- [x] **C4. CLAUDE.md: PlayerAnimator maintenance-mode violation — RECLASSIFIED**
  The original rule used private-field count and a mandatory unrelated extraction as proxies for
  ownership. That would push Animator hashes and layer application into pass-through classes without
  improving cohesion. The branch keeps durable spell-hold timing/state in `CombatActionPlaybackController`
  and composition in focused classes; `PlayerAnimator` remains the central adapter for shared combat
  layers and override banks. The repository rule now permits hashes/thin application glue there, allows
  explicitly exclusive orthogonal properties in focused components, and forbids new selection, timing,
  lifecycle, preemption, gameplay policy, or parallel playback state machines.

- [x] **C5. SchoolVfxSet assets in Resources/ with no Resources.Load consumer — FIXED**
  `SchoolVfxSet` and its assets now live under `Assets/Arena/Editor/SpellPresentation/`; their Unity GUIDs
  are preserved. `AssetDatabase.FindAssets` remains the only consumer, and the type/assets no longer enter
  player builds. Wiring an unnecessary runtime consumer merely to justify `Resources/` was rejected.

- [x] **C6. `"UNTIL_AURA_END"` literal defined 3× in one assembly — RETRACTED**
  The lifecycle itself was based on the incorrect persistent-visual premise and has been removed.

- [x] **C7. Editor tool duplication — FIXED**
  `SpellPresentationEditorData` now owns the catalog path, deterministic asset lookup, runtime
  `CombatAnimationSet` enumeration, and the minimal gameplay read model. The resolved view keys gameplay
  by the catalog's real `action_id`; class-prefix stripping is gone, so future `MAGE_*` ability ids do not
  silently fall back to Instant. The map editor and resolved view share the same asset/set discovery.

- [x] **C8. Per-spell authoring data compiled into editor source — FIXED**
  The 19 signature override rows and the then-current Blade Barrier cast-hand exception lived in the editor-only
  `SpellVfxOverrideCatalog` asset. Each ability entry reuses `SchoolVfxSlotEntry` for slot look data and
  may author an explicit hand. The generator contains no per-spell dictionaries; new bespoke looks are
  asset edits and do not require code changes or a domain reload. Blade Barrier's later target-field
  replacement removed that cast-hand exception.

- [x] **C9. `_assetSchoolPalettes` static-as-hidden-parameter**
  `SpellAuthoringWindow.CueGeneration.cs:418` — assigned at top of `GenerateCues`, read in `TryResolvePaletteEntry` same call. Make it a local passed as a parameter. (Dies naturally with C3.)

## Efficiency (all verified CONFIRMED)

- [x] **E1. Memoize `TryResolveComposed`** — successful compositions are cached by normalized spell id,
  animation-set identity, hand, and explicitly derived archetype, next to the map/library cache. Failures
  are never cached, so missing pre-sync rows cannot become sticky. `InvalidateCache`, map/library
  validation, and the library rebuild path clear the composed entries.
- [x] **E2. Pre-normalized dictionary in `SpellCastAnimationMap`** — :67 re-normalizes every entry per linear-scan lookup (O(n) string allocs per query).
- [x] **E3. Cache `IsProjectileDeliveredSpellImpact`** — `CombatVFXDispatcher.cs:499-515`: 2× Normalize + Db.Find per terminal spell-impact event; memoize by ActionKind, invalidate on catalog update.
- [x] **E4. Stop regenerating cues per repaint** — generated previews and editor-only authoring assets are
  cached by selected ability. Focus, project changes, reload, catalog writes, and animation-entry edits
  invalidate the cache; repaint only diffs the cached generated rows against the selected catalog rows.
- [x] **E5. Precompute resolved rows in `SpellCastAnimationResolvedWindow.Reload()`** — gameplay
  archetypes, family composition, explicit-entry shadow sets, and display strings are now materialized
  during reload. `OnGUI` only renders the precomputed rows.

## Refuted — do not re-chase

| Candidate | Why refuted |
|---|---|
| Hyphenated-id normalization divergence (map/writer/school Normalize vs WireIdentifier.Normalize) | Server ingest replaces `-`→`_` (`server/src/action_ids.rs:43-44`); catalog has zero hyphenated ids; a hypothetical hyphen causes a uniform miss, not split-layer divergence. Still fine to unify on WireIdentifier.Normalize as hygiene. |
| Boomerang self-return suppression drops legit caster impacts | Server emits hit==caster only on the zero-damage return catch (`projectiles.rs:920-941`, `:754-765`; enemy loops skip caster); world-impact visual still plays (`CombatVFXDispatcher.cs:568`), only the hit cue is skipped. A `BoomerangReturning` field exists if we ever want data instead of the identity heuristic. |
| SPELL/ABILITY owner-kind double-dispatch after migration write | For the sole SPELL-kind row (ICE_SPIKES) the slot infers ambiguous → write button disabled; and even if written, the CueOverrideKey matches and `CombatVfxCueResolver` suppresses the SPELL row. No double dispatch. Writer matching by owner_id-only is still worth tightening someday. |
| combat_profile_id Error→Info downgrade lost a guard | Design-of-record endorses profile-less shared spells as the norm (redesign doc line 21, decision 10 line 291); the validator skip pre-exists the branch. Intended. |

## Suggested fix order

1. **P0 aura lifecycle — retracted and removed** after the owner clarified that only the buff persists.
2. **P1 #4** (restore resolution-failure visibility) — before any further spell migrations.
3. **P1 #1 + P2 #6** (hold-fade policy) — fixed within the existing shared playback substrate; C4 was reclassified.
4. **P1 #2 + #3** (write-gate round-trip + duration guard) — unblocks Aura/SelfFlash authoring, which P0 makes meaningful.
5. Cleanup C1-C3, C7-C9, then efficiency E1-E5 opportunistically (E1 with #5 in mind).

---

## Re-audit follow-up — current HEAD after the first repair pass

A second current-HEAD audit found five gaps not closed by the checklist above. They are fixed in the
working tree:

- [x] **Data-preserving catalog publish also syncs `SpellDefinition`.** Both republish scripts now
  call `publish_spell_definitions` before `publish_progression_catalogs`, matching the two independent
  init-time sync families consumed by the client. The spell-definition sync now also removes rows for
  spells deleted from the shared catalog, matching the full-sync behavior of progression tables.
- [x] **Projectile-impact classification invalidates from its real dependency.**
  `CombatVFXDispatcher` now listens to `SpellDefinition` insert/update/delete rather than relying on
  unrelated cue-catalog churn to evict cached classifications.
- [x] **The three branch-owned stale Rust assertions match the migrated VFX contract.** Frost Needle
  expects delayed `AREA_IMPACT`, Meteor expects its charged cast glow, and Sacred Flame expects the
  owner-verified `TARGET` anchor. At this stage `cargo test` improved from 440/448 to 443/448. The
  initial attribution of the five inherited failures as unrelated stale inputs was incomplete; the
  later investigation and correction are recorded below.
- [x] **Map validation derives the real catalog archetype offline.** The authoring validator supplies
  `cast_time_ms × delivery.kind` explicitly and composes every unshadowed animation-set hand, including
  profile-neutral mapped spells such as charged ICICLE.
- [x] **The speculative persistent aura visual path is removed.** Owner clarification established that
  only the gameplay buff persists. Aura presentation remains the finite `aura_ground` cue; `ActiveAura`
  remains server-only and is not a client VFX lifecycle signal.
- [x] **Repository standards now express ownership rather than syntactic proxies.** `PlayerAnimator`
  remains the central adapter for shared combat layers while controllers/data own durable policy and
  focused components may own explicitly exclusive orthogonal properties; editor-only VFX palette assets
  and their ScriptableObject type moved out of `Resources/` into the editor surface.
- [ ] **Explicit aura toggle-off — DEFERRED BY OWNER, not a presentation issue.** The server
  replaces the one-row-per-caster `ActiveAura` when another aura is cast and removes it for dead/invalid
  casters, but no caster-request reducer currently deletes it. Out-of-range recipient buffs are already
  removed by `tick_auras`.
- [x] **Branch diff hygiene is clean.** Removed 204 trailing-whitespace errors from 68 newly-added Unity
  metadata files.
- [x] **The five inherited Rust failures exposed real catalog drift.** An earlier broad spell-VFX
  commit had injected `ARROW_STANDARD` projectile delivery into every non-archer melee strike and
  Rain Shot, and had removed two of Whirlwind's four hit windows; the melee manifest is restored to
  its pre-corruption shape. Sword-and-Shield combo timing now keeps the successor opening aligned
  with the predecessor's final hit plus recovery, and selectable action-bar slots exclude the
  separately tagged discipline bar. The full server suite is now 448/448.

Verification: `Assembly-CSharp.csproj`, `Assembly-CSharp-Editor.csproj`, and
`Arena.EditModeTests.csproj` compile; all 448 Rust tests pass; shell scripts pass `bash -n`;
`git diff --check` passes. Unity EditMode execution remains unavailable while another Unity instance
owns the project.
