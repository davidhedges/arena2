# Spell Presentation Pipeline — DRY Redesign (Design Doc)

**Status:** Agreed 2026-07-07 (§7 decisions locked). Ready to implement, starting at build-order step 0 + resolver.
**Date:** 2026-07-07 · **Rev 2026-07-07b:** incorporated external review — fixed `vfx_school` precedence (§2.3), specified slot identity + legacy inference (§3.4, decision 6), gave the rule table a concrete Class A/B shape (§4.3, decision 5), fenced step 0 to cue-row cleanup only (decision 7).
**Scope:** Author-time surface for spell *presentation* — animation (Unity `CombatAnimationSet`) and VFX cues (`combat_vfx_cues[]` in `progression_catalog.shared.json`). Gameplay authoring is out of scope except as the *input* we derive from.

This doc is deliberately adversarial about the requested direction. Where the direction is right I say so; where the data contradicts it I push back and propose the corrected version. Everything below was verified against the code and the live catalog, not the prompt summary.

---

## 0. TL;DR — what changes vs. what was requested

| Requested | Verdict | Corrected direction |
|---|---|---|
| Derive archetype from `cast_time_ms × delivery.kind` | **Half right** | `delivery.kind` (+ `targeting` + a couple delivery sub-fields) is the primary axis; `cast_time_ms` is a *binary* modifier (instant vs charged), because **43/47 spells are `cast_time_ms: 0`**. The matrix is real but sparse — don't build a 2-D grid, build `delivery → archetype` with an `instant|charged|channel` modifier. |
| Default + override for animation templates | **Right** | Keep. Generalize `holdOverride` into a whole-entry override with **per-field** fallthrough, not whole-entry replace. |
| VFX by school, `damage_type` ≈ free school, `vfx_school` only for exceptions | **Inverted** | **27/47 spells have no `damage_type`.** Whole class identities (Paladin HOLY, Warrior VOID/DARK) live in spells with no `damage_type`. Resolve school as `vfx_school ?? damage_type ?? profile_default_school` — `vfx_school` **wins** so an off-element spell (FIRE damage, shadow look) can retint; `damage_type` is the free default when `vfx_school` is unset. `vfx_school` is first-class, not an exception. |
| Unify the resolution model, not storage | **Right** | Keep. Two stores stay; one resolver contract spans both. |
| Resolver first, then templates, then palettes, then validator+inspector, then migrate | **Right** | Keep the ordering. Add one thing before templates: a **cue-owner normalization pass** (dup/`owner_kind` cleanup) or the resolver keys on dirty data. |

**The single biggest DRY win** is not archetypes or palettes — it is that the **profile-agnostic elemental spell set (`SPELL_*`, `combat_profile_id: ""`) is authored twice** (once in `Staff.asset`, once in `TwoHandedSword.asset`) and three spells (`BLINDING_LIGHT`, `GLACIAL_SPIKE`, `FROZEN_GRASP`) are authored **four times** identically. Resolver + archetype templates kill that duplication directly.

**The single biggest correctness win** is that deriving presentation mode from gameplay removes an entire class of desync bug: today `presentationMode` is hand-authored in the animation set but only ever consumed to recompute `UsesSpellCastHoldPresentation` / `PlaysSpellReleasePresentation`, which the runtime could derive from `cast_time_ms` + `behavior` directly.

---

## 1. Ground truth (verified, not summarized)

### 1.1 The spell corpus

47 abilities carry `gameplay.delivery` (the "spells"). Distribution:

- **`delivery.kind`:** `APPLY_STATUS` 12, `AREA` 11, `PROJECTILE` 8, `AURA` 6, `CHANNEL` 3, `REMOVE_STATUS` 3, `INSTANT_BEAM` 1, `DIRECT_TARGET` 1, `SELF_RESOURCE` 1, `CONSUME_STATUS` 1.
- **`cast_time_ms`:** `0` ×43, `750` ×1 (METEOR), `1200` ×1 (INSTANT_BEAM), `1500` ×1 (ICICLE), `2000` ×1 (GLACIAL_SPIKE).
- **`cast_mobility`:** `MOBILE` ×40, `GROUNDED_STATIONARY` ×7.
- **`targeting`:** `SELF` ×27, `TARGET` ×15, `POINT` ×5.
- **`damage_type`:** *absent* ×27, `COLD` ×7, `ARCANE` ×4, `FIRE` ×3, `LIGHTNING` ×2, `SHADOW` ×2, `HOLY` ×2.

> Server accepts `delivery.kind ∈ {DIRECT_TARGET, PROJECTILE, AREA, INSTANT_BEAM, CHANNEL}` for the *damage-spell* parse path (`progression.rs:4734`) and `damage_type ∈ {PHYSICAL, FIRE, COLD, LIGHTNING, POISON, HOLY, SHADOW, ARCANE}` (`progression.rs:4758`). The other delivery kinds (`APPLY_STATUS`, `AURA`, `REMOVE_STATUS`, `SELF_RESOURCE`, `CONSUME_STATUS`) are parsed on non-damage paths — the taxonomy is already wider than any single enum in the Rust.

### 1.2 The two backing stores

**Animation** — `CombatAnimationSet` ScriptableObjects, one per weapon profile:
`Staff.asset` (29 spell entries), `TwoHandedSword.asset` (34), `SwordAndShield.asset` (16), `Daggers.asset` (8), `ArcherBow.asset` (0).

Per-entry authoring (`WeaponSpellAnimationEntry`, `CombatAnimationSet.cs:533`): `spellId`, `ground`/`air` clips, `requiresCombatStance`, `combatEntryMode`, `presentationMode`, `holdOverride` (a `SpellCastHoldProfile`), `playbackLayer`, `animatedProp`, plus five fields explicitly marked *"Obsolete serialized compatibility field"* (`groundEffectTime`, `airEffectTime`, `lowerBodyUnlockAtSeconds`, `lowerBodyBlendOutSeconds`, `visualInterruptibleAtSeconds`) — runtime reads clip AnimationEvents instead. Set-level `defaultSpellCastHold` already implements a partial default-fallback (`CombatAnimationSet.cs:1203`; fallback in `WeaponSpellAnimationEntry.TryResolveHoldProfile`, `CombatAnimationSet.cs:579`).

Authoring repetition (measured across all sets):
- `presentationMode`: `ReleaseOnly` dominates; only 19 entries total are `HoldThenRelease`/`HoldOnly`.
- `requiresCombatStance: true` on **every** entry (87/87) — a constant masquerading as a field.
- **`Staff` spell set ⊂ `TwoHandedSword` spell set** (all 29 of Staff's IDs recur). **`Daggers` ⊂ `SwordAndShield`.** `BLINDING_LIGHT`, `GLACIAL_SPIKE`, `FROZEN_GRASP` appear in all four sets. This is literal copy-paste keyed by weapon.

**VFX** — `combat_vfx_cues[]` (43 rows). Per-row: `owner_kind`, `owner_id`, `trigger`, `anchor`, `vfx_id`, `attach_mode`, `vfx_role`, `lifecycle`, `duration_ms`, `projectile_sequence_index`, `hit_index`, `sort_order`. Only ~32 of 47 spells have any cue; 15 (most `AURA`/buff) have none.

### 1.3 Vocabulary as it exists in code (the generator's target)

- **Triggers** (`progression.rs:6116`): `MELEE_CAST, MELEE_ACTIVE_WINDOW, MELEE_IMPACT, MELEE_BLOCK, MELEE_PARRY, AREA_IMPACT, SPELL_CAST, SPELL_RELEASE, SPELL_IMPACT, SPELL_BLOCK, SPELL_PARRY, SPELL_FIZZLE, SPECIAL_MOVEMENT_START, SPECIAL_MOVEMENT_ARRIVAL`.
- **Anchors** (`progression.rs:6132`, resolver `CombatVFXAnchorResolver.cs:13`): `CASTER, CASTER_OVERHEAD, TARGET, ORIGIN, AREA_ORIGIN, IMPACT_POINT, GROUND_UNDER_CASTER, GROUND_UNDER_TARGET, WEAPON_MAIN_HAND, WEAPON_OFF_HAND, WEAPON_BLADE_START, WEAPON_BLADE_END, LEFT_HAND, RIGHT_HAND`.
- **Attach modes:** `SPAWN_WORLD, FOLLOW_ANCHOR, WORLD_ALIGNED_TO_FACING` (`""` → `SPAWN_WORLD`).
- **VFX roles:** `ONE_SHOT, ATTACHED, PROJECTILE_BODY, TRAVEL_BODY` (`""` → `ONE_SHOT`).
- **Lifecycles:** `DURATION, PARTICLE_SYSTEM, UNTIL_RELEASE_EVENT, UNTIL_TERMINAL_EVENT, UNTIL_CAST_END` (`""` → `DURATION`).
- **Render path is *not* uniform per role** (`CombatVFXDispatcher.DispatchCue`, `CombatVFXDispatcher.cs:1074`): `PROJECTILE_BODY` early-returns (`:1077`) — the body is drawn by `CombatProjectileVisualController` from the server spawn origin, blended from a client hand-launch origin read off the cue anchor over `LaunchBlendSeconds=0.15`. `TRAVEL_BODY` routes to `TravelVisuals` (`:1079`). Only `ONE_SHOT`/`ATTACHED` go through the normal spawn/attach path.

### 1.4 Cast presentation is a client↔server protocol, not just animation

Verified in `EntityRegistry.OnCombatCast` (`EntityRegistry.cs:1305`), `SpellCastPresentationController`, `SpellInputHandler`:

- Server exposes a derived **`SpellDefinition.Behavior`** (`INSTANT_BEAM | CHANNEL | CHARGE`) and `CastTimeMs` to the client. `SpellDefinitionContracts.CastsOnRelease(def)` is true for those three behaviors (`GameplayContracts.cs:91`).
- The **three presentation modes map to distinct timing drivers**:
  - `ReleaseOnly` (`cast_time==0`, not casts-on-release) → **prediction path** (`SpellInputHandler.PredictImmediateInstantSpellVisual`, excludes `cast_time>0` and `CastsOnRelease` and hold-only).
  - `HoldThenRelease` (`cast_time>0`) → **`ActiveCast`-row-driven** enter→hold→scheduled release (`SpellCastPresentationController.ComputeReleaseStartMs`).
  - `HoldOnly` (channel) → `ActiveCast`-driven enter→loop; release COMBAT_CAST is **suppressed** so it doesn't preempt the hold (`EntityRegistry.OnCombatCast:1357`, gated on `!PlaysSpellReleasePresentation`).
- **`presentationMode` is consumed only** to compute `UsesSpellCastHoldPresentation` / `PlaysSpellReleasePresentation` (`PlayerEntity`), which are already isomorphic to `(cast_time_ms, behavior)`. → It is derivable and today's hand-authoring is a redundant source of truth that *can* silently disagree with gameplay.

### 1.5 Loop constraint (confirmed)

Only animator states with no exit transition can loop a hold: `SpellCastHoldAction1..4` and `UpperBodySpellCastHoldAction1..4` (8 states confirmed in `Arena_Character.controller`). `UpperBodySpellAction{n}`/`LeftGestureSpellAction{n}` auto-exit at 0.9. Any generated/templated "channel" archetype must resolve to a loop-capable state or it silently freezes after the enter clip. This is a hard constraint on the **channel** animation archetype only.

---

## 2. The real presentation archetypes (data-derived, not guessed)

Presentation is two orthogonal factors that must be resolved separately, then a third that only tints VFX:

```
PRESENTATION(spell, weapon) =
    ANIMATION_ARCHETYPE(cast_time, behavior)        # body motion + timing protocol
  × VFX_ARCHETYPE(delivery.kind, targeting, sub)    # which cue slots fire + how they're wired
  × SCHOOL(damage_type ?? vfx_school ?? default)    # which vfx_id fills each slot
```

### 2.1 Animation archetypes (3 — this is genre-standard, don't add more)

Derivation is total and already latent in the runtime gates:

| Archetype | Derivation | Presentation | Timing driver | Population |
|---|---|---|---|---|
| **`INSTANT`** | `cast_time==0 && !casts_on_release` | one-shot cast gesture (`ReleaseOnly`) | client prediction | ~40 spells |
| **`CHARGED`** | `cast_time>0 && !casts_on_release` (or `CHARGE`/`INSTANT_BEAM` behavior) | enter → hold loop → release (`HoldThenRelease`) | `ActiveCast` scheduled release | 4 (METEOR, ICICLE, INSTANT_BEAM, GLACIAL_SPIKE) |
| **`CHANNEL`** | `behavior==CHANNEL` (`cast_time==0`, casts-on-release) | enter → loop until released (`HoldOnly`), **loop-capable state required** | `ActiveCast` delete | 3 (ELECTROCUTE, FROZEN_SPLINTERS, MAGIC_MISSILE) |

A secondary animation axis is **playback layer + stance**, which is genuinely per-weapon and per-spell-role, *not* derivable from gameplay: `FullBody` vs `UpperBody` vs `UpperBodyWhileMoving` vs `LeftGesture`. Templates carry a per-(weapon × archetype) default here; signature spells override. Everything sets `requiresCombatStance=true` today, so that becomes a template constant.

> **Adversarial note:** the requested "timing × delivery matrix" would have you author a cell per (timing, delivery) pair. But timing only ever takes 2 meaningful values for animation (instant vs charged) and channel is a *behavior* flag, not a timing. So the animation side is 3 buckets, full stop. The delivery dimension does **not** change the *body motion* — a Fireball and a Frost-Needle are the same instant point-cast gesture; they differ only in VFX. Don't let delivery leak into the animation archetype.

### 2.2 VFX archetypes (the "shape" of the cue set)

Derived from `delivery.kind` + `targeting` + a few delivery sub-fields (`motion`/`sky_origin`, `projectile` presence, `target_audience`). Each archetype declares a **slot set** and how each slot is wired. Populated cells, from the live catalog:

| VFX archetype | Derivation | Slot set (trigger/anchor/role/lifecycle) | Examples |
|---|---|---|---|
| **`PROJECTILE`** | `delivery.kind==PROJECTILE`, or `CHANNEL` w/ `delivery.projectile` | `cast_glow` (SPELL_CAST / hand / ATTACHED / *lifecycle-by-archetype*) + `projectile_body` (SPELL_RELEASE / hand-launch / PROJECTILE_BODY / UNTIL_TERMINAL_EVENT) + `impact` (SPELL_IMPACT / IMPACT_POINT / ONE_SHOT / DURATION\|PARTICLE_SYSTEM) | FIREBALL, ICICLE, BOOMERANG_ORB, MAGIC_MISSILE, BLESSED_SHIELD, GROUND_SLASH |
| **`SKY_DROP`** | `delivery.kind==AREA && sky_origin` | `travel_body` (SPELL_RELEASE / ORIGIN / TRAVEL_BODY / UNTIL_TERMINAL_EVENT, `duration_ms=0`) + `impact` | METEOR |
| **`GROUND_AOE`** | `delivery.kind==AREA && targeting∈{POINT,TARGET}` | `impact` at IMPACT_POINT/AREA_ORIGIN (ONE_SHOT / DURATION\|PARTICLE_SYSTEM) [+ optional `cast_glow`] | LIGHTNING, ERUPTION, FROST_NEEDLE, NEGATE |
| **`SELF_NOVA`** | `delivery.kind==AREA && targeting==SELF` | `burst` at CASTER (ONE_SHOT / PARTICLE_SYSTEM) | FROST_NOVA, ICE_SPIKES, SHOCKWAVE, INTIMIDATE |
| **`BEAM`** | `delivery.kind∈{INSTANT_BEAM, CHANNEL-no-projectile}` | `beam` (SPELL_RELEASE / hand / ATTACHED / UNTIL_TERMINAL_EVENT or DURATION) | ELECTROCUTE, INSTANT_BEAM |
| **`TARGET_HIT`** | `delivery.kind==DIRECT_TARGET`, or `APPLY_STATUS`/`REMOVE_STATUS` w/ `targeting==TARGET` | `impact` at TARGET (ONE_SHOT / PARTICLE_SYSTEM) — **TARGET anchor only, never on SPELL_CAST/RELEASE** | GLACIAL_SPIKE, SACRED_FLAME, CLEANSING_TOUCH, ABSOLUTION |
| **`AURA`** | `delivery.kind==AURA` | `aura` at CASTER, sustained — **UNPOPULATED: 0/6 have cues** | (none authored yet) |
| **`SELF_FX` / `NONE`** | `APPLY_STATUS`/`SELF_RESOURCE`, `targeting==SELF` | optional `self_flash`/overhead ONE_SHOT, or nothing | BLINDING_LIGHT (overhead), most WARRIOR buffs (none) |

Cross-checks that matter:

- **The `cast_glow` lifecycle is not a palette property — it's forced by the animation archetype + validators.** Live data proves the current per-spell mess: same slot, three lifecycles — FIREBALL `DURATION 350` (instant projectile), ICICLE `UNTIL_RELEASE_EVENT` (charged; forced by server Rule 11), MAGIC_MISSILE `UNTIL_CAST_END` (channel; `UNTIL_RELEASE_EVENT` would die on the first projectile). So: `cast_glow.lifecycle = INSTANT→DURATION, CHARGED→UNTIL_RELEASE_EVENT, CHANNEL→UNTIL_CAST_END`. The generator computes this; the palette never sees it.
- **Palette drift is real and is the argument for the redesign.** `ORBITING_BLADES` and `MAGIC_MISSILE` are `ARCANE` but wear `VFX_ICE_CAST_HAND_01` / `VFX_ICE_HIT_01` — arcane spells in ice clothing. A school palette makes this impossible by construction.

### 2.3 School (VFX tint)

`SCHOOL = vfx_school ?? damage_type ?? profile_default_school`. **Precedence order is deliberate: `vfx_school` is a true override, not a fallback.** If `damage_type` won, a damage spell could never visually diverge from its element — which defeats the stated "off-element" use case. So `vfx_school` (when set) wins; `damage_type` is the free default for the common case where visual == element; `profile_default_school` is the class backstop.

- `vfx_school` set → explicit visual choice; overrides everything (off-element damage spells + damage-less themed utility). Expected to be sparse.
- `vfx_school` unset, `damage_type` present → 20 spells, visual == element, free.
- `vfx_school` unset, `damage_type` absent → 27 spells. Of the ones that *have* authored VFX, the theme is a **VFX-only school** with no gameplay damage type: Paladin `CONSECRATE`/`CLEANSING_TOUCH`/`SACRED_FLAME` = HOLY; Warrior `INTIMIDATE`=VOID, `BATTLE_CRY`=DARK. These *require* `vfx_school`. This is why the requested "`vfx_school` only for exceptions" is inverted: for two of five classes the entire spell-visual identity is `vfx_school`.
- both unset → `profile_default_school` catches the rest (a caster staff defaults to ARCANE; a paladin defaults to HOLY) so most spells need no per-spell school at all.

---

## 3. VFX school palette — slots, generation, override

### 3.1 Palette definition (one per school)

A palette is a map `slot → vfx_id` (+ optional per-slot `scale`/`duration_ms` hint, subject to validators). Slots, unified across archetypes:

| Slot | Used by archetypes | Fills |
|---|---|---|
| `cast_glow` | PROJECTILE, CHARGED-anything, CHANNEL, BEAM | hand charge/cast effect |
| `muzzle` | PROJECTILE, BEAM (optional) | release flash at the hand (currently folded into launch — see §6) |
| `projectile_body` | PROJECTILE | the flying body (rendered by projectile controller) |
| `travel_body` | SKY_DROP | falling/travelling body |
| `impact` | PROJECTILE, GROUND_AOE, SKY_DROP, TARGET_HIT | hit burst |
| `burst` | SELF_NOVA | self-centered nova |
| `beam` | BEAM | sustained beam |
| `aura` | AURA | sustained caster aura (unimplemented) |

A school need not fill every slot; the archetype declares which slots it *requests*, and generation only emits cues for `slots requested ∩ slots the school provides`. Missing a *required* slot is a validator warning (see §4).

Example (ARCANE): `{cast_glow: VFX_ARCANE_CAST_HAND_01, projectile_body: VFX_ARCANE_BOLT_01, impact: VFX_ARCANE_HIT_01, burst: VFX_ARCANE_NOVA_01, beam: VFX_ARCANE_BEAM_01}`. Fixes the FROST/ARCANE drift for free.

### 3.2 How cues are generated

`generate_cues(spell) = archetype.slots.map(slot -> Cue{ ... })` where the archetype owns the **wiring** and the palette owns the **look**:

```
for slot in ARCHETYPE(spell).requested_slots:
    vfx_id = SCHOOL(spell).palette[slot]           # look — from palette
    if vfx_id is None: continue                    # school opts out of this slot
    emit Cue{
        owner_kind = "ABILITY", owner_id = spell.ability_id,
        trigger    = ARCHETYPE.trigger_for(slot),          # wiring — from archetype
        anchor     = ARCHETYPE.anchor_for(slot),
        attach_mode= ARCHETYPE.attach_for(slot),
        vfx_role   = ARCHETYPE.role_for(slot),
        lifecycle  = ARCHETYPE.lifecycle_for(slot, cast_time, behavior),  # computed, never authored
        duration_ms= ARCHETYPE.duration_for(slot),
        vfx_id     = vfx_id,
        projectile_sequence_index = slot==projectile_body ? 0 : -1,
        slot       = slot,                                 # explicit author-time slot key (see §3.4)
    }
```

Critically, **the generator is where the validator rules become invariant by construction** (rather than checked after the fact): it can only emit `UNTIL_RELEASE_EVENT` for a `SPELL_CAST` slot, only `PROJECTILE_BODY` on `SPELL_RELEASE` at index 0, `TRAVEL_BODY` only with `UNTIL_TERMINAL_EVENT`+`duration_ms=0`, never `TARGET` anchor on cast/release, etc. The full rule set it must satisfy (server `progression.rs:6115–6525` + editor `CombatVFXAuthoringValidator.cs`) is enumerated in Appendix A.

### 3.3 Overrides — per-slot, not per-spell

Generalize `holdOverride` into a spell-level VFX override that is a **partial map** `slot → {vfx_id?, anchor?, lifecycle?, duration_ms?}`. Resolution is per-slot: a spell can override just its `impact` vfx_id while inheriting a generated `cast_glow`. This is strictly more DRY than the current whole-spell cue block, and matches signature-spell reality (Fireball wants a bespoke explosion but a stock fire hand-glow).

`vfx_school` (per spell) is the coarser override: retint every slot without touching wiring.

### 3.4 Slot identity (the key everything hangs on)

Per-slot override and legacy-cue replacement both require a **stable slot key**, but today's `combat_vfx_cues` rows have none — only `trigger`/`anchor`/`vfx_role`/`sort_order`. Without an explicit key, "override the `impact` slot" and "this legacy row replaces the generated `impact`" are undefined. Resolve this two ways:

**(a) Generated + override rows carry an explicit `slot` string** — an **author-time-only** field in the JSON. It is consumed by the generator, resolver, validator, and inspector, and is **not** synced to the runtime `CombatVfxCueCatalog` table (`sync_combat_vfx_cue_catalog` ignores it) — so there is **no runtime schema or wire change**. The server validator, which reads the JSON, still sees and checks it. Overrides key on this string; matching is exact, not positional.

**(b) Legacy rows (pre-migration, no `slot`) get slot by a strict, total, collision-free inference contract** keyed on `(trigger, vfx_role, anchor-class)`, where `anchor-class ∈ {HAND=LEFT/RIGHT_HAND, CASTER=CASTER/CASTER_OVERHEAD, IMPACT=IMPACT_POINT/AREA_ORIGIN/TARGET/GROUND_UNDER_*/ORIGIN}`:

| trigger | role | anchor-class | → slot |
|---|---|---|---|
| `SPELL_CAST` | `ATTACHED` | HAND | `cast_glow` |
| `SPELL_RELEASE` | `ATTACHED` | HAND | `beam` |
| `SPELL_RELEASE` | `ONE_SHOT` | HAND | `muzzle` |
| `SPELL_RELEASE` | `ONE_SHOT` | CASTER | `burst` |
| `SPELL_RELEASE`\|`SPELL_IMPACT`\|`AREA_IMPACT` | `ONE_SHOT` | IMPACT | `impact` |
| any | `PROJECTILE_BODY` | — | `projectile_body` (disambiguate multiples by `projectile_sequence_index`) |
| any | `TRAVEL_BODY` | — | `travel_body` |

The table is verified collision-free against the entire current catalog (the only near-collisions — `cast_glow` vs `beam`, `muzzle` vs `beam`, `muzzle`/`burst`/`impact` — are separated by trigger, role, or anchor-class respectively). **Escape hatch:** if two legacy rows on one spell infer the *same* slot (e.g. a hand-authored multi-burst), the resolver refuses to auto-key them and the validator flags the spell for explicit `slot` authoring. Inference runs **only** for un-migrated rows; once a spell's rows carry explicit `slot`, inference never touches it again. This bounds the inference contract to a migration-window concern, not a permanent runtime dependency.

---

## 4. Resolver + Validator + Inspector

### 4.1 Resolver (build this first — de-risks everything)

One resolver, two faces (animation, VFX), same shape: read today's per-spell data **and** the new defaults, per-slot, explicit-wins. It must be a **pure function** over the catalog + animation sets so the inspector and validator call the exact same code the runtime/exporter does (no second implementation to drift).

**Animation resolution — `resolve_animation(spellId, weaponProfile) -> ResolvedSpellAnimation`:**

Resolution order (first hit wins per field, not per entry):
1. Explicit `WeaponSpellAnimationEntry` in that profile's `spells[]` (today's data).
2. `(weaponProfile × animation_archetype)` template.
3. `animation_archetype` template (weapon-agnostic) — covers profile-less `SPELL_*`.
4. Hard fallback: `defaultSpellCastHold` (holds) + generic release gesture.

Output: resolved `ground`/`air`/hold clips, `playbackLayer`, `combatEntryMode`, derived `presentationMode` (from `cast_time`+`behavior`), and the **provenance of each field** (which layer supplied it) for the inspector and the redundancy validator.

**VFX resolution — `resolve_cues(spellId) -> ResolvedCueSet`:**
1. Start from generated cues (§3.2), each carrying an explicit `slot` (§3.4).
2. Apply per-slot spell overrides, keyed on `slot`.
3. Apply legacy explicit `combat_vfx_cues[]` rows — during migration these *are* the override; each legacy row is assigned a slot via the §3.4 inference contract (explicit `slot` if present, else the inference table), then replaces the generated cue for that slot. Ambiguous inference (two rows → one slot) is a hard error surfaced by the validator, not a silent last-wins. Track provenance per slot.

Coexistence is the whole point: with zero templates/palettes authored, the resolver returns exactly today's behavior (every field resolves at layer 1 / legacy). Templates and palettes then peel authoring away incrementally with no behavior change until a per-spell entry is deleted.

### 4.2 Validator

Runs in the editor (author-time) and ideally as a `spacetime`-side check on export. Rules:

- **Redundancy (the delete-list):** flag any explicit per-spell animation entry or cue whose every field equals the resolved template/generated default. This is the migration burndown list. Must compare *resolved* values (clip refs, lifecycle, anchor), not raw serialized bytes, because obsolete fields differ harmlessly.
- **Archetype/gameplay desync:** flag any explicit `presentationMode` that disagrees with `derive(cast_time, behavior)` — these are latent bugs today.
- **School coverage:** flag a spell whose resolved archetype requests a slot the resolved school doesn't provide (e.g. ARCANE with no `impact`).
- **School drift lint:** flag an explicit cue whose `vfx_id` isn't in the resolved school's palette (catches the ORBITING_BLADES-in-ice-clothes case). Warning, not error.
- **Contract passthrough:** run the generated + overridden output through the existing server contract (Appendix A) and the editor `CombatVFXAuthoringValidator`. See §4.3 for how the shared rule table is actually structured — this is where "generate correct-by-construction" meets "don't spawn a third rule path."

### 4.3 Rule-table shape (decision #5, made concrete)

"Unify the rule table" is an architectural task, not a one-liner — the source must be consumed by **Rust** (server, at catalog load) and **C#** (Unity editor), and a naive "share it" risks becoming a *third* rule path that drifts from both. The resolution is to recognize that the Appendix-A rules are **two disjoint classes**, and only one is shareable:

**Class A — cue-field relational rules** (enum allow-lists + legality of `trigger × role × anchor × lifecycle × trigger`, e.g. "`UNTIL_RELEASE_EVENT` only on `SPELL_CAST`", "`PROJECTILE_BODY` only on `SPELL_RELEASE`, index 0, exactly one", "`TARGET` anchor not on cast/release"). These are pure data — a **declarative table** (checked-in JSON or a Rust `const` that the existing `spacetime generate` codegen path emits to C#). One source, three consumers: the **generator** reads it to emit correct-by-construction cues, the **server validator** reads it to check, the **editor validator** reads the same table to check the identical subset. This is the actual "unification" and it removes the *real* divergence (the server today enforces relational rules the editor doesn't).

**Class B — engine-asset rules** (E3 visual-only prefab, E7 hand-anchor inference from clips, E8 clip-event timing, E9 avatar bone/mount existence). These require Unity asset introspection — prefabs, `AnimationClip` events, the avatar rig. They **cannot** move server-side (the Rust server has no access to Unity assets) and they are **not divergence** — they are a separate validation domain that legitimately lives only in the editor. The doc previously conflated "server lacks E3/E7/E8/E9" with "divergence to close"; it is not. Leave Class B editor-only.

So decision #5 concretely = *"author Class A once as a declarative table, codegen it to C#, consume it from the generator + both validators; leave Class B in the editor."* The shape must exist before step 3 (generator) is coded, or the generator will hardcode Class-A rules and become the third path. **This is an implementation-shaping item flagged for the first coding session, not a settled mechanism.**

### 4.4 Inspector

An editor window (genre-standard "resolved view"), two queries:

- **`show(spellId, weaponProfile)`** → renders: derived animation archetype + VFX archetype + resolved school; the resolved animation (each field + provenance: explicit / weapon-template / archetype-template / fallback); the resolved cue list (each slot: generated vs overridden vs legacy, final `vfx_id`/`anchor`/`role`/`lifecycle`/`duration_ms`); and inline validator status.
- **`redundant()`** → the delete-list from §4.2, grouped by profile, one-click to strip the redundant entry and re-resolve to prove no diff.

This is the tool that makes migration safe and observable; it is not optional polish — build it in the same phase as the validator.

---

## 5. Build order (unchanged from request, one insertion)

0. **Normalize cue-owner *rows only*** (new, cheap, blocking): repoint the orphan `ICE_SPIKES` cue (its `owner_id` resolves to no ability) to `SPELL_ICE_SPIKES` or drop it as a duplicate, and collapse the lone `owner_kind: SPELL` row into `ABILITY`. This touches `combat_vfx_cues` rows exclusively so every cue `owner_id` resolves to an existing `ability_id`. **Non-goals — explicitly out of scope for step 0:** renaming any `ability_id`; the `SPELL_*`-vs-profile-scoped id convention; and anything touching `combat_profile_action_bar_defaults`, saved player action bars/loadouts, spellbook authorization, animation-set `spellId` keys, or VFX ability identity. Changing an `ability_id` is a live-player-state data migration with its own plan and gate — if we ever pursue the id convention, it is a **separate, later** work item, never smuggled into cue cleanup. The resolver keys on `ability_id` *as it exists today*; step 0 only makes the cue rows' foreign keys valid.
1. **Resolver** reading legacy + (empty) defaults → proves byte-identical behavior with no templates.
2. **Animation archetype templates** (per weapon × 3 archetypes + weapon-agnostic) → migrate `SPELL_*` first (biggest duplication), delete redundant entries via the inspector.
3. **School palettes + cue generator** → migrate elemental projectiles first (clearest slot set), validate against Appendix A.
4. **Validator + Inspector** → in practice built alongside 1–3, hardened here.
5. **Incremental migration** → burn down the delete-list; utility `vfx_school` themes (Paladin HOLY, Warrior VOID/DARK) last. **AURA VFX deferred entirely (§7.1).**

---

## 6. Edge cases, risks, and what the direction oversimplifies

1. **Timing axis is degenerate (already covered).** Don't build a 2-D authoring grid; 43/47 are instant. Animation = 3 buckets; charged is 4 spells.
2. **`damage_type` absent on 27/47 (already covered).** `vfx_school` is load-bearing for Paladin/Warrior identity. Resolve `vfx_school ?? damage_type ?? profile_default` (override, not fallback — §2.3).
3. **AURA archetype has zero reference cues. → Decided: out of v1 (§7.1).** Six aura spells, no `combat_vfx_cues`, and no obvious lifecycle (a persistent aura wants to live as long as the *status*, which is neither `UNTIL_CAST_END` for a 0-cast apply nor a fixed `DURATION`). No generator coverage until one aura is hand-authored to define the slot + lifecycle. Do not ship a generator that claims to cover a shape with no exemplar.
4. **"Muzzle" is not currently a distinct cue.** There is no `SPELL_RELEASE` `ONE_SHOT` muzzle flash today; the projectile launch reads the hand anchor and blends from it (`CombatProjectileVisualController`, `LaunchBlendSeconds=0.15`). Adding a `muzzle` slot is a *new* effect, not a refactor — treat it as opt-in per school, and know its render path is the normal spawn path (unlike `projectile_body`).
5. **`projectile_body` / `travel_body` are not ordinary cues.** They early-return in `DispatchCue` and are drawn by separate controllers from server-authoritative origins. The palette supplies their `vfx_id`, but the generator must respect: exactly one `PROJECTILE_BODY` at `projectile_sequence_index 0` per projectile spell (server Rule 18), no `FOLLOW_ANCHOR`, no `start_delay_ms`, editor requires the prefab be visual-only (no MonoBehaviour). Channel spells that fire *multiple* projectiles (MAGIC_MISSILE, FROZEN_SPLINTERS) need the sequence-index story thought through — the current data only ever uses index 0.
6. **Lifecycle is (slot × archetype), and the current data is inconsistent (already covered).** Generator computes lifecycle; palette never stores it. This is a feature — it's how we stop the FIREBALL/ICICLE/MAGIC_MISSILE cast-glow divergence.
7. **`presentationMode` derivation changes the source of truth.** Low blast radius (it's only read to compute two predicates) but non-zero: any spell whose hand-authored mode disagreed with gameplay will *change behavior* when derived. The validator's desync check (§4.2) must run and be resolved **before** we flip the runtime to derive, so we convert surprises into a reviewed list. Also: charged/channel still need real *clips* (enter/idleLoop) — derivation gives the mode, not the animation. Those clips stay in `defaultSpellCastHold` + per-(weapon×archetype) templates and remain legitimately per-weapon.
8. **Channel loop-capable-state constraint.** The CHANNEL archetype template must resolve `holdOverride.playbackLayer` to a loop-capable state (`FullBody`→`SpellCastHoldAction`, `UpperBody`→`UpperBodySpellCastHoldAction`). A template that defaults a channel to `LeftGesture` or `UpperBodyWhileMoving`-without-motion silently freezes. Encode "channel ⇒ loop-capable layer" as a validator rule, not a convention.
9. **Zero-cast channel COMBAT_CAST suppression.** The channel path depends on `EntityRegistry.OnCombatCast` suppressing the release event (gated on `!PlaysSpellReleasePresentation`). If deriving `presentationMode` changes how `PlaysSpellReleasePresentation` is computed, re-verify this gate and the `SpellInputHandler` prediction exclusion — both currently key off the animation entry, and both must key off the derived value identically. (This is the exact bug class documented in `reference_spell_hold_loop_states.md`.)
10. **Two profile-scoping conventions collide.** `SPELL_*` elemental abilities have `combat_profile_id: ""` (shared), while `PALADIN_*`/`WARRIOR_*` are profile-scoped. The animation archetype-template layer must handle *both*: a profile-less spell resolves via the weapon-agnostic archetype template (order step 3) with an optional per-weapon override; a profile-scoped spell resolves via its home weapon template. Don't assume every spell has a home profile.
11. **Cue system spans melee, not just spells.** `PALADIN_AVENGE`/`WARRIOR_EARTHSHATTER`/`WARRIOR_CATACLYSM` own `MELEE_IMPACT`/`AREA_IMPACT` cues and aren't in the spell (`delivery`) list. The spell-cue generator must scope strictly to spell abilities and never touch melee-owned rows. `owner_kind` normalization (step 0) must preserve `MELEE_STRIKE` owners.
12. **Catalog re-sync gotcha (operational).** Editing `combat_vfx_cues` (or generating them) is not live until `spacetime call arena publish_progression_catalogs` — a fast republish preserves data and skips init (`reference_catalog_cue_resync.md`). The generator's build step must invoke this or every test reads stale cues.
13. **Working tree is dirty.** There is an uncommitted Magic-Missile presentation feature and `[HOLDDBG]` debug logging across `CombatVFXDispatcher`/`PlayerAnimator`/`SpellCastPresentationController`/`EntityRegistry`/`SpellInputHandler`. Baseline any "does behavior match?" comparison against a known-good build, not the current tree; strip `[HOLDDBG]` before it calcifies into the templated paths.
14. **Obsolete fields will confuse a naive redundancy diff.** `WeaponSpellAnimationEntry` carries five dead serialized floats; two entries can differ in those bytes yet be presentation-identical. Redundancy detection compares *resolved* values, and migration is a chance to drop the dead fields from the serialized form.

---

## 7. Decisions (agreed 2026-07-07)

1. **AURA — out of v1.** The 6 aura spells get no generated cues this cycle (0/6 have an exemplar and there's no natural lifecycle). Build-order step 5 defers them; revisit only after one aura is hand-authored to define the slot + lifecycle.
2. **`muzzle` slot — optional per-school, off by default.** Not emitted unless a school's palette explicitly provides `muzzle`. Faithful to today (launch blends off the hand anchor); a school opting in adds a *new* effect on the normal spawn path.
3. **`presentationMode` — validate-agreement-first this cycle; derive next cycle.** This cycle the validator's desync check (§4.2) runs and its list is resolved, but the field stays hand-authored and the runtime keeps reading it. A later cycle flips the runtime to `derive(cast_time, behavior)` once the desync list is empty — so behavior never flips on an unreviewed spell.
4. **Generated cues — materialized at export into the JSON.** The generator writes `combat_vfx_cues[]` into `progression_catalog.shared.json` at export time (checked in, diffable), and the existing server + editor validators run over the materialized output unchanged. No in-memory-at-load generation.
5. **Rule table — unified, with a concrete shape (§4.3).** Split Appendix-A rules into **Class A** (cue-field relational rules → one declarative table, codegen'd Rust→C#, consumed by generator + server validator + editor validator) and **Class B** (engine-asset rules E3/E7/E8/E9 → editor-only, *not* divergence). Author Class A once; the generator is correct-by-construction from it. The table's exact form (JSON vs codegen'd const) is an implementation-shaping item for the first coding session, resolved **before** the generator (step 3) is written.
6. **Slot identity — explicit `slot` key + legacy inference (§3.4).** Generated/overridden cues carry an author-time-only `slot` string (not synced to the runtime table, so no schema/wire change). Overrides key on it exactly. Legacy rows get a slot from the strict, collision-free `(trigger, role, anchor-class)` inference table; ambiguous inference is a hard validator error. Inference is a migration-window concern only.
7. **Step 0 is cue-row cleanup only — never an ID migration.** Repoint the orphan `ICE_SPIKES` cue and the `owner_kind: SPELL` outlier so every cue's `owner_id` resolves to an existing `ability_id`. No `ability_id` renames, no action-bar/loadout/spellbook/animation-`spellId` changes. The `SPELL_*` id-convention question is deferred to its own gated migration if ever pursued.

---

## Appendix A — Contract every generated/overridden cue must satisfy

Server (`progression.rs:6115–6525`), enforced at catalog load:
- Enum allow-lists for `owner_kind`, `trigger`, `anchor`, `attach_mode`, `vfx_role`, `lifecycle` (§1.3).
- `owner_kind:owner_id` must resolve to a known ability/spell/melee-strike.
- `scale` must **not** be authored in JSON (belongs in `CombatVFXRegistry`).
- `UNTIL_RELEASE_EVENT` only on `SPELL_CAST`.
- `PARTICLE_SYSTEM` only for `ONE_SHOT`, and `duration_ms==0`.
- Hand-attached cast-time `SPELL_CAST` (`FOLLOW_ANCHOR`, `ATTACHED`, `LEFT/RIGHT_HAND`, owner `cast_time_ms>0`) **must** use `UNTIL_RELEASE_EVENT` with `duration_ms 0`.
- `PROJECTILE_BODY`: `SPELL_RELEASE` only; not `FOLLOW_ANCHOR`; no `start_delay_ms`; owner must be projectile-producing; valid `projectile_sequence_index`; exactly one selected at index 0 per projectile spell.
- `TRAVEL_BODY`: `SPELL_RELEASE` only; not `FOLLOW_ANCHOR`; `UNTIL_TERMINAL_EVENT`; `duration_ms==0`.
- `ONE_SHOT`+`DURATION` requires `duration_ms>0`.
- `TARGET` anchor invalid on `SPELL_CAST`/`SPELL_RELEASE` (only once an impact/block/parry/fizzle target is known).
- `vfx_id` non-empty; `hit_index` only on melee triggers and in range.

Editor (`CombatVFXAuthoringValidator.cs`), additionally:
- E2: `vfx_id` must resolve to a prefab or scripted template (`CombatVFXTemplateRegistry`).
- E3: `PROJECTILE_BODY`/`TRAVEL_BODY` prefabs must be visual-only (no MonoBehaviour).
- E4: prefab (non-scripted) `UNTIL_TERMINAL_EVENT` disallowed — use scripted template or finite `DURATION`.
- E7: hand-attached `SPELL_CAST` anchor must match the cast hand inferred from the animation/playback layer.
- E8: `cast_time_ms` must match the release clip's event timing (±0.05s); release clip must carry `OnReleaseFrame`/`OnLowerBodyUnlock`/`OnVisualInterruptible`.
- E9: runtime avatar must expose the anchor's bone/mount (`LEFT_HAND`/`RIGHT_HAND`/weapon mounts/blade markers).

**Two classes, only one is "divergence" (see §4.3):**
- **Class A (relational, unify):** the enum allow-lists, owner resolution, projectile-count, and every `trigger×role×anchor×lifecycle` legality rule above. These are pure data; today they are server-only (the editor doesn't enforce them) — *that* is the divergence Class A closes by becoming one shared declarative table read by the generator + both validators.
- **Class B (engine-asset, leave editor-only):** E3/E4/E7/E8/E9 need Unity asset introspection (prefabs, clip events, avatar rig) and cannot run server-side. Their absence server-side is correct separation, not divergence — do not try to "unify" them.
- Only `UNTIL_RELEASE_EVENT→SPELL_CAST` and the hand-attached-cast-time rule are enforced on both sides today; after Class A unifies, all Class-A rules are.
