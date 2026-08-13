# Spell Presentation Pipeline — DRY Redesign (Design Doc)

**Status:** Implemented authoring architecture; this document remains the contract record.
**Date:** 2026-07-07 · **Rev 2026-07-07b:** incorporated external review — fixed `vfx_school` precedence (§2.3), specified slot identity + legacy inference (§3.4, decision 6), gave the rule table a concrete Class A/B shape (§4.3, decision 5), fenced step 0 to cue-row cleanup only (decision 7). · **Rev 2026-07-07c:** added generator wiring table (Appendix B); fixed `scale`-in-palette bug (§3.1); resolved CHANNEL beam lifecycle → `UNTIL_CAST_END` (§B.7, decision 8). · **Rev 2026-07-07d:** parked ELECTROCUTE as legacy relic (decision 9, downgrades decision 8 to deferred); added animation template table (Appendix C). · **Rev 2026-07-07e (implementation begins):** step 0 verified NO-OP (ICE_SPIKES SPELL cue is a tested spell-kind fallback, not an orphan — decision 7 corrected); logged `CombatVfxCueResolver` prior art (VFX resolver + override model already exist; step-1 work is the animation face). · **Rev 2026-07-07f (end-state alignment):** registry+palette merge into per-school VFX sets + generator=Unity editor tool (decision 10); **all spell types supported**, AURA un-deferred via new `UNTIL_STATUS_END` lifecycle (decision 11); full `delivery.kind`→archetype coverage table + projectile-body no-drop guarantee (Rule 18) in B.9. · **Rev 2026-07-08a (implementation):** Class-A single-cue rules unified behind one shared checker (`vfx_generation::check_cue_field_rules`), consumed by both the generator's `validate_wiring` and the server contract (decisions 5/10, Rust half); corrected Appendix A — the server contract is a **test-time** check (`#[cfg(test)] mod tests`), not a catalog-load guard, and no separate production relational validator exists. Editor-side (C#) Class-A consumption remains pending the Unity generator tool (decision 10). · **Rev 2026-07-13:** every spell archetype now supports `cast_glow` plus repeatable caster-root `character_fx`; projectile archetypes now request optional `muzzle` and `projectile_trail` slots directly.
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
| Resolver first, then templates, then palettes, then validator+inspector, then migrate | **Right** | Keep the ordering. (A pre-templates "cue-owner cleanup" was proposed but **verified unnecessary** — the catalog already passes owner resolution; §5 step 0.) |

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
  × SCHOOL(vfx_school ?? damage_type ?? default)    # which vfx_id fills each slot
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

Derived from `delivery.kind` + `targeting` + a few delivery sub-fields (`motion`/`sky_origin`, `projectile` presence, `target_audience`). Every archetype begins with optional `cast_glow` and repeatable `character_fx` cast slots; the table lists its delivery-specific slots:

| VFX archetype | Derivation | Slot set (trigger/anchor/role/lifecycle) | Examples |
|---|---|---|---|
| **`PROJECTILE`** | `delivery.kind==PROJECTILE`, or `CHANNEL` w/ `delivery.projectile` | optional `muzzle` + `projectile_body` + optional `projectile_trail` + `impact` | FIREBALL, ICICLE, VAMPIRIC_ORB, MAGIC_MISSILE, BLESSED_SHIELD, GROUND_SLASH |
| **`SKY_DROP`** | `delivery.kind==AREA && sky_origin` | `travel_body` (SPELL_RELEASE / ORIGIN / TRAVEL_BODY / UNTIL_TERMINAL_EVENT, `duration_ms=0`) + `impact` | METEOR |
| **`GROUND_AOE`** | `delivery.kind==AREA && targeting∈{POINT,TARGET}` | `impact` at IMPACT_POINT/AREA_ORIGIN (ONE_SHOT / DURATION\|PARTICLE_SYSTEM) | LIGHTNING, ERUPTION, FROST_NEEDLE, NEGATE |
| **`SELF_NOVA`** | `delivery.kind==AREA && targeting==SELF` | `burst` at CASTER (ONE_SHOT / PARTICLE_SYSTEM) | FROST_NOVA, ICE_SPIKES, SHOCKWAVE, INTIMIDATE |
| **`BEAM`** | `delivery.kind∈{INSTANT_BEAM, CHANNEL-no-projectile}` | `beam` (SPELL_RELEASE / hand / ATTACHED / UNTIL_TERMINAL_EVENT or DURATION) | ELECTROCUTE, INSTANT_BEAM |
| **`TARGET_HIT`** | `delivery.kind==DIRECT_TARGET`, or `APPLY_STATUS`/`REMOVE_STATUS` w/ `targeting==TARGET` | detached `impact` at IMPACT_POINT (ONE_SHOT / PARTICLE_SYSTEM); TARGET is reserved for FOLLOW_ANCHOR effects that intentionally track the entity | GLACIAL_SPIKE, SACRED_FLAME, CLEANSING_TOUCH, ABSOLUTION |
| **`AURA`** | `delivery.kind==AURA` | finite `aura_ground` flourish; persistent gameplay aura has no mirrored persistent VFX | WARDING_AURA |
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

A palette is a map `slot → PaletteEntry`, where `PaletteEntry = { vfx_id, self_terminating?: bool, duration_ms?: int, variant_id?: string }`. `character_fx` is the one repeatable slot; multiple entries require unique `variant_id` values so materialized identities such as `character_fx/body_rings` remain stable. **No `scale`** — Rule 1/E1 forbid authoring `scale` in the JSON (it lives in `CombatVFXRegistry`). Lifecycle remains computed from the slot and archetype (Appendix B).

| Slot | Used by archetypes | Fills |
|---|---|---|
| `cast_glow` | all spell archetypes (optional look) | hand charge/cast effect |
| `character_fx` | all spell archetypes (optional, repeatable) | effect surrounding and following the caster root during the cast |
| `muzzle` | PROJECTILE (optional) | release flash at the hand |
| `projectile_body` | PROJECTILE | the flying body (rendered by projectile controller) |
| `projectile_trail` | PROJECTILE (optional) | second visual composed on the projectile runtime root |
| `travel_body` | SKY_DROP | falling/travelling body |
| `impact` | PROJECTILE, GROUND_AOE, SKY_DROP, TARGET_HIT | hit burst |
| `burst` | SELF_NOVA | self-centered nova |
| `beam` | BEAM | sustained beam |
| `aura_ground` | AURA | finite caster-following ground flourish |

A school need not fill every slot; the archetype declares which slots it *requests*, and generation only emits cues for `slots requested ∩ slots the school provides`. Missing a *required* slot is a validator warning (see §4).

Example (ARCANE): `{cast_glow: VFX_ARCANE_CAST_HAND_01, projectile_body: VFX_ARCANE_BOLT_01, impact: VFX_ARCANE_HIT_01, burst: VFX_ARCANE_NOVA_01, beam: VFX_ARCANE_BEAM_01}`. Fixes the FROST/ARCANE drift for free.

### 3.2 How cues are generated

`generate_cues(spell) = archetype.slots.map(slot -> Cue{ ... })` where the archetype owns the **wiring** and the palette owns the **look**:

```
MODE = animation_archetype(spell)                  # INSTANT | CHARGED | CHANNEL
for slot in ARCHETYPE(spell).requested_slots:
    entry = SCHOOL(spell).palette[slot]            # look — PaletteEntry {vfx_id, self_terminating?, duration_ms?}
    if entry is None: continue                     # school opts out of this slot
    emit Cue{                                       # every field below defined in Appendix B
        owner_kind = "ABILITY", owner_id = spell.ability_id,
        trigger    = ARCHETYPE.trigger_for(slot, spell),   # wiring — from archetype (+ DEFERRED/MODE)
        anchor     = ARCHETYPE.anchor_for(slot, spell),
        attach_mode= ARCHETYPE.attach_for(slot),
        vfx_role   = ARCHETYPE.role_for(slot),
        lifecycle  = ARCHETYPE.lifecycle_for(slot, MODE, entry.self_terminating),  # computed, never authored raw
        duration_ms= ARCHETYPE.duration_for(slot, MODE, entry),
        vfx_id     = entry.vfx_id,
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

**(a) Generated + override rows carry an explicit `slot` string** — an **author-time-only** field in the JSON. It is consumed by the generator, resolver, validator, and inspector, and is **not** synced to the runtime `CombatVfxCueCatalog` **table** (`sync_combat_vfx_cue_catalog` ignores it) — so there is **no runtime table or wire change**. Overrides key on this string; matching is exact, not positional.

> **Correction (2026-07-08, verified live):** the server's `CombatVfxCueDefinition` deserialize struct is `#[serde(deny_unknown_fields)]` (`progression.rs:788`), so the JSON does **not** transparently "see and ignore" `slot` — an undeclared key is a hard parse error that fails the *entire* catalog load (empirically: adding `slot` to FIREBALL broke ~28 catalog tests with `unknown field \`slot\``). The field must therefore be **declared** on the struct (`#[serde(default)] #[allow(dead_code)] slot: String`) so serde accepts and ignores it. This is a one-line **deserialize-schema** addition, but still **no runtime-table or wire change** (the runtime `CombatVfxCueCatalog` and its sync are untouched — `slot` is dropped at sync). Any future author-time-only key added to the JSON needs the same declaration.

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

> **Prior art (verified 2026-07-07d):** the VFX resolution above *already exists at runtime* — `CombatVfxCueResolver` (`Assets/Arena/Runtime/Presentation/CombatVfxCueResolver.cs`) matches cues by `(owner_kind, owner_id, trigger)` and implements an **ABILITY-cue-overrides-SPELL-kind-cue** layer via a `CueOverrideKey = (trigger, role, anchor, attach_mode, hit_index, projectile_sequence_index)`. That `CueOverrideKey` is a de-facto slot identity and strong validation of §3.4's inference table — the explicit `slot` key can align with (or replace) it. Consequence: the VFX *resolver* is largely built; the missing VFX piece is the **generator** (step 3, Rust), not the resolver. The genuinely-new resolver work in step 1 is the **animation** face, which today is a flat explicit lookup (`CombatAnimationSet.TryGetSpellAnimation`).

### 4.2 Validator

Runs in the editor (author-time) and ideally as a `spacetime`-side check on export. Rules:

- **Redundancy (the delete-list):** flag any explicit per-spell animation entry or cue whose every field equals the resolved template/generated default. This is the migration burndown list. Must compare *resolved* values (clip refs, lifecycle, anchor), not raw serialized bytes, because obsolete fields differ harmlessly.
- **Archetype/gameplay desync:** flag any explicit `presentationMode` that disagrees with `derive(cast_time, behavior)` — these are latent bugs today.
- **School coverage:** flag a spell whose resolved archetype requests a slot the resolved school doesn't provide (e.g. ARCANE with no `impact`).
- **School drift lint:** flag an explicit cue whose `vfx_id` isn't in the resolved school's palette (catches the ORBITING_BLADES-in-ice-clothes case). Warning, not error. **Suppressed for spells marked `presentation_legacy: true`** — parked relics (e.g. ELECTROCUTE, decision 9) are deliberately off-palette and should not nag.
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

0. **Cue-owner cleanup — VERIFIED NO-OP (2026-07-07d, corrected).** The premise was wrong: the `SPELL`/`ICE_SPIKES` cue is **not** an orphan. `owner_id: ICE_SPIKES` resolves as a valid **spell-kind** owner (server Rule 3 checks `known_spells`, not just `ability_id`s), and it is a *deliberate* "spell-owned area-impact fallback" — asserted by the test `warrior_ice_spikes_authors_self_area_cone_vfx` (`progression.rs`). The lone `owner_kind: SPELL` row is that fallback, not an outlier to collapse. The whole catalog already passes owner-resolution validation (server contract + 72 passing tests). **There is nothing to clean; skip to step 1.** Prior mistake logged so we don't repeat it: never diagnose a cue "orphan" by checking `owner_id` against `ability_id`s alone — SPELL and MELEE_STRIKE owners resolve against different tables. (The `ability_id` rename / `SPELL_*` convention question remains deferred to its own gated migration, but it was never a step-0 item.)
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

1. **AURA — supported (un-deferred 2026-07-07f, corrected by decision 11).** Aura gameplay is persistent, but its visual is a finite one-shot cast flourish. `WARDING_AURA` is the live exemplar. All spell types are in scope; nothing is deferred.
2. **`muzzle` slot — optional per-school, off by default.** Not emitted unless a school's palette explicitly provides `muzzle`. Faithful to today (launch blends off the hand anchor); a school opting in adds a *new* effect on the normal spawn path.
3. **`presentationMode` — validate-agreement-first this cycle; derive next cycle.** This cycle the validator's desync check (§4.2) runs and its list is resolved, but the field stays hand-authored and the runtime keeps reading it. A later cycle flips the runtime to `derive(cast_time, behavior)` once the desync list is empty — so behavior never flips on an unreviewed spell.
4. **Generated cues — materialized at export into the JSON.** The generator writes `combat_vfx_cues[]` into `progression_catalog.shared.json` at export time (checked in, diffable), and the existing server + editor validators run over the materialized output unchanged. No in-memory-at-load generation.
5. **Rule table — unified, with a concrete shape (§4.3).** Split Appendix-A rules into **Class A** (cue-field relational rules → one declarative table, codegen'd Rust→C#, consumed by generator + server validator + editor validator) and **Class B** (engine-asset rules E3/E7/E8/E9 → editor-only, *not* divergence). Author Class A once; the generator is correct-by-construction from it. The table's exact form (JSON vs codegen'd const) is an implementation-shaping item for the first coding session, resolved **before** the generator (step 3) is written.
6. **Slot identity — explicit `slot` key + legacy inference (§3.4).** Generated/overridden cues carry an author-time-only `slot` string (not synced to the runtime table, so no runtime-table/wire change — but it **must** be declared on the `deny_unknown_fields` deserialize struct or catalog parsing hard-fails; see the §3.4a correction, verified live 2026-07-08). Overrides key on it exactly. Legacy rows get a slot from the strict, collision-free `(trigger, role, anchor-class)` inference table; ambiguous inference is a hard validator error. Inference is a migration-window concern only.
7. **Step 0 turned out to be a no-op (corrected 2026-07-07d).** The `ICE_SPIKES`/`owner_kind: SPELL` cue is a legitimate, tested spell-kind *fallback*, not an orphan — the catalog already passes owner-resolution validation, so there is nothing to clean. See §5 step 0. (The `ability_id` rename / `SPELL_*` convention remains deferred to its own gated migration, but it was never a step-0 item.)
8. **CHANNEL beam lifecycle = `UNTIL_CAST_END` when generated (§B.7).** The correct target for a channel sustained visual (persists to cast-stop, not first impact); unifies all CHANNEL sustained visuals (`cast_glow` + `beam`) on ActiveCast-delete. **Deferred, not blocking:** the sole channel-beam spell (ELECTROCUTE) is parked (decision 9), so nothing generates a channel beam yet. The `UNTIL_CAST_END`→`ActiveCast` binding check on `SPELL_RELEASE` cues reactivates only when a real channel beam is authored.
9. **ELECTROCUTE parked as a legacy relic.** It's an old-project relic that doesn't behave as wanted, not on any default action bar (already effectively library-only), and not being re-authored now. It stays in the legacy layer: the resolver serves its existing cues verbatim (legacy wins per-slot, §4.1), it is never migrated, and it is marked `presentation_legacy: true` so the school-drift lint ignores it (§4.2). Consequence: the CHANNEL-beam VFX cell (B.7) has **no live exemplar** — it joins `muzzle` on the "author a first exemplar before relying on it" list (B.9). The CHANNEL *animation* archetype is unaffected (MAGIC_MISSILE/FROZEN_SPLINTERS remain exemplars). Retiring/deleting ELECTROCUTE is a separate gated task (touches `ability_id` refs) for whenever a proper channel beam replaces it.

10. **Registry + palette merge into one per-school VFX set; generator = Unity editor tool (2026-07-07f).** The `CombatVFXRegistry` (`vfx_id → prefab + scale`) can't be deleted — it's the client half of a client↔server boundary (the server catalog holds `vfx_id` *strings*; only Unity holds prefabs), so *something* in Unity must map string→prefab. But it merges with the school palette into one asset: **a per-school VFX set** = `slot → { vfx_id, prefab, scale }`, serving as both the registry (id→prefab) and the palette (school×slot→id). The cue **generator becomes a Unity editor tool** that reads gameplay from the catalog + the school sets and writes generated cues back into `progression_catalog.shared.json` (decision 4 unchanged — still materialized into JSON; only the tool's *location* moves from Rust to Unity C#). Bonus: the archetype/mode derivation collapses to **one C# function** (`SpellAnimationArchetypes.Derive`) instead of duplicated Rust + C#; the Rust `vfx_generation` module retires to being the server-side validator's Class-A rule source (decision 5). **End-state authoring goal:** a typical new spell touches **one place** — its gameplay row. VFX generate, animation inherits an archetype template, the prefab already lives in its school set. The other surfaces are per-*category* (per school / weapon×archetype / new asset), authored once and inherited.

11. **AURA gameplay persists; AURA visuals do not (owner correction 2026-07-10).** The authoritative `ActiveAura` row remains server-only and has no expiry. While it exists, the server refreshes short recipient status leases for targets in range; those buffs are removed when recipients leave range. Casting another aura replaces the row, and invalid/dead casters lose it. Presentation is independent: an aura cast emits only the finite `aura_ground` one-shot and never mirrors `ActiveAura`. There is no `UNTIL_AURA_END` lifecycle, sustained `aura` slot, client `active_aura` subscription, or reconnect hydration. An explicit caster-controlled toggle-off action is a separate gameplay contract and is not currently implemented.

12. **Universal cast surfaces (owner direction 2026-07-13).** Every VFX archetype requests optional `cast_glow` and repeatable `character_fx`. `character_fx` follows `CASTER`, uses the same INSTANT/CHARGED/CHANNEL lifecycle as the hand glow, and uses explicit variant identities when more than one is authored. Projectile archetypes also request optional `muzzle` and `projectile_trail` entries directly.

---

## Appendix A — Contract every generated/overridden cue must satisfy

Server-side contract (`progression.rs`), enforced as a **test-time** check — `validate_combat_authoring_graph`, asserted by the `combat_authoring_graph_validates_first_pass_contract` test inside `#[cfg(test)] mod tests`. It is **not** a runtime catalog-load guard, and there is no separate production relational validator (verified 2026-07-08). The Class-A subset below is now delegated to one shared checker, `vfx_generation::check_cue_field_rules`, which the VFX generator's `validate_wiring` also calls — so the generator and this contract cannot diverge (unified 2026-07-08; decisions 5/10). The rules:
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

---

## Appendix B — Generator wiring table (`ARCHETYPE.*_for(slot)`)

This is the concrete definition of the `trigger_for` / `anchor_for` / `attach_for` / `role_for` / `lifecycle_for` / `duration_for` functions the §3.2 generator calls. It is the Class-A rule table's *authoring-facing* face — every row is correct-by-construction against Appendix A, and every populated cell is grounded in a real catalog row. Migrating a spell should reproduce its current cues from this table (modulo the school retint); where it wouldn't, that's a bug in the current data, listed in the "grounding / drift" column.

**Shared modifiers** (computed once per spell, feed multiple cells):
- `MODE ∈ {INSTANT, CHARGED, CHANNEL}` = the animation archetype (§2.1), from `cast_time_ms` + `behavior`.
- `HAND` = the resolved cast hand (E7 inference from animation/playback layer; default `LEFT_HAND` — 14 of 15 hand cues use LEFT today).
- `DEFERRED` = delivery resolves its area on a delay (`impact_delay_ms`/deferred-area) rather than at release. Selects `AREA_IMPACT@AREA_ORIGIN` over `SPELL_RELEASE@IMPACT_POINT` for ground/nova impacts.
- `PS?` = the slot's `PaletteEntry.self_terminating` (→ `PARTICLE_SYSTEM`, else `DURATION`+`duration_ms`).

### B.1 `cast_glow` (all spell archetypes)

| field | value | rule / grounding |
|---|---|---|
| trigger | `SPELL_CAST` | — |
| anchor | `HAND` | E7 must match inferred cast hand |
| attach_mode | `FOLLOW_ANCHOR` | follows the hand through the cast |
| vfx_role | `ATTACHED` | — |
| lifecycle | `INSTANT→DURATION` · `CHARGED→UNTIL_RELEASE_EVENT` · `CHANNEL→UNTIL_CAST_END` | **exactly** FIREBALL(350)/ICICLE/MAGIC_MISSILE. Rule 11 *forces* UNTIL_RELEASE_EVENT for CHARGED (cast_time>0); Rule 11 antecedent is false for INSTANT/CHANNEL (cast_time 0) so DURATION / UNTIL_CAST_END are legal. Rule 9: UNTIL_RELEASE_EVENT only on SPELL_CAST ✓ |
| duration_ms | `INSTANT → PaletteEntry.duration_ms (>0)` · else `0` | CHARGED/CHANNEL ignore duration |

### B.1b `character_fx` (all spell archetypes, optional and repeatable)

Same trigger, role, and lifecycle rules as `cast_glow`, but anchored to `CASTER` with `FOLLOW_ANCHOR`. This is a body-root effect that surrounds and moves with the character; it is not constrained to the ground. Multiple entries use distinct `variant_id` values and materialize as distinct author-time slot keys.

### B.2 `muzzle` (PROJECTILE — opt-in, off by default, decisions #2/#12)

| field | value | rule / grounding |
|---|---|---|
| trigger | `SPELL_RELEASE` | not emitted unless palette provides `muzzle` |
| anchor | `HAND` | same resolved hand |
| attach_mode | `SPAWN_WORLD` | flash at hand, not attached |
| vfx_role | `ONE_SHOT` | — |
| lifecycle | `PS? → PARTICLE_SYSTEM` else `DURATION` | Rule 10 (PARTICLE_SYSTEM ⇒ ONE_SHOT ✓) |
| duration_ms | `PS? → 0` else `PaletteEntry.duration_ms (>0)` | Rule 10b / Rule 14 |

### B.3 `projectile_body` (PROJECTILE)

| field | value | rule / grounding |
|---|---|---|
| trigger | `SPELL_RELEASE` | Rule 12a |
| anchor | `HAND` (default) · `CASTER` for body-origin | client reads it for hand-launch blend; GROUND_SLASH uses CASTER — expose as a per-spell slot override |
| attach_mode | `SPAWN_WORLD` | Rule 12b (never FOLLOW_ANCHOR) |
| vfx_role | `PROJECTILE_BODY` | early-returns in DispatchCue; drawn by projectile controller |
| lifecycle | `UNTIL_TERMINAL_EVENT` | — |
| duration_ms / start_delay | `0` / *unset* | Rule 12c (no start_delay) |
| projectile_sequence_index | `0` | Rules 12d/18: exactly one selected at index 0. **Multi-projectile channels (MAGIC_MISSILE/FROZEN_SPLINTERS) fire N bodies from one row at runtime — index stays 0; do not emit index 1..N** (current data confirms only index 0 exists). |

### B.4 `travel_body` (SKY_DROP)

| field | value | rule / grounding |
|---|---|---|
| trigger | `SPELL_RELEASE` | Rule 13a |
| anchor | `ORIGIN` | METEOR sky origin |
| attach_mode | `SPAWN_WORLD` | Rule 13b |
| vfx_role | `TRAVEL_BODY` | routes to TravelVisuals |
| lifecycle | `UNTIL_TERMINAL_EVENT` | Rule 13c (forced) |
| duration_ms | `0` | Rule 13d |

### B.5 `impact` (PROJECTILE, SKY_DROP, GROUND_AOE, TARGET_HIT)

| field | value | rule / grounding |
|---|---|---|
| trigger | PROJECTILE/SKY_DROP → `SPELL_IMPACT` · GROUND_AOE → `DEFERRED ? AREA_IMPACT : SPELL_RELEASE` · TARGET_HIT → `SPELL_IMPACT` | FIREBALL(SPELL_IMPACT) / LIGHTNING(SPELL_RELEASE) / ICE_SPIKES(AREA_IMPACT) / GLACIAL_SPIKE(SPELL_IMPACT) |
| anchor | PROJECTILE/SKY_DROP → `IMPACT_POINT` · GROUND_AOE → `DEFERRED ? AREA_ORIGIN : IMPACT_POINT` · TARGET_HIT → `IMPACT_POINT` | Rule 15: `TARGET` is never legal on SPELL_CAST/RELEASE. Rule 16: detached world-spawned terminal hit cues use `IMPACT_POINT`; `TARGET` is reserved for FOLLOW_ANCHOR effects. |
| attach_mode | `SPAWN_WORLD` | — |
| vfx_role | `ONE_SHOT` | — |
| lifecycle | `PS? → PARTICLE_SYSTEM` else `DURATION` | Rule 10 / Rule 14 |
| duration_ms | `PS? → 0` else `PaletteEntry.duration_ms (>0)` | Rule 10b / Rule 14 |

### B.6 `burst` (SELF_NOVA)

| field | value | rule / grounding |
|---|---|---|
| trigger | `DEFERRED ? AREA_IMPACT : SPELL_RELEASE` | FROST_NOVA(SPELL_RELEASE) / ICE_SPIKES(AREA_IMPACT) |
| anchor | `DEFERRED ? AREA_ORIGIN : CASTER` | — |
| attach_mode | `DEFERRED ? WORLD_ALIGNED_TO_FACING : SPAWN_WORLD` | Deferred self-origin areas preserve the cast-facing direction (ICE_SPIKES / GUST_OF_WIND). |
| vfx_role | `ONE_SHOT` | — |
| lifecycle / duration | as `impact` (B.5) | FROST_NOVA/SHOCKWAVE/INTIMIDATE all PARTICLE_SYSTEM |

### B.7 `beam` (BEAM)

| field | value | rule / grounding |
|---|---|---|
| trigger | `SPELL_RELEASE` | — |
| anchor | `HAND` | ELECTROCUTE/INSTANT_BEAM use LEFT_HAND |
| attach_mode | `FOLLOW_ANCHOR` | tracks the hand |
| vfx_role | `ATTACHED` | — |
| lifecycle | `CHANNEL → UNTIL_CAST_END` · `CHARGED → DURATION` | INSTANT_BEAM(CHARGED, DURATION 500). CHANNEL beam ends on ActiveCast-delete (decision, 2026-07-07 — see below), superseding the shipped `UNTIL_TERMINAL_EVENT`. Beam visuals remain *scripted* templates (BeamVFX, procedural geometry) — but that is now geometry-driven, **not** lifecycle-driven (E4 only fires on `UNTIL_TERMINAL_EVENT`, which we no longer emit). |
| duration_ms | `CHARGED → PaletteEntry.duration_ms (>0)` · `CHANNEL → 0` | CHANNEL ignores duration (ActiveCast-driven) |

> **Decided (2026-07-07): CHANNEL beam lifecycle = `UNTIL_CAST_END`, superseding the shipped `UNTIL_TERMINAL_EVENT`.** `UNTIL_TERMINAL_EVENT` dies on the first terminal (impact/miss/fizzle); a channel should persist until the channel *stops* (ActiveCast deleted). This unifies the channel family: **all CHANNEL sustained visuals — `cast_glow` (B.1) and `beam` (B.7) — end on ActiveCast-delete**, one rule. Validation is clean: `UNTIL_CAST_END` is allow-listed with no trigger restriction (only `UNTIL_RELEASE_EVENT` is pinned to `SPELL_CAST`, Rule 9), so it is legal on a `SPELL_RELEASE`/`ATTACHED` beam.
>
> **Verify when a channel beam is first generated (deferred — ELECTROCUTE is parked, decision 9, so no channel beam generates today):** the client `UNTIL_CAST_END` teardown binds a cue to its `ActiveCast` via `action_instance_id` (`CombatVFXDispatcher`/`CombatVFXLifecycleRegistry`); the only *proven* binding today is a `SPELL_CAST` `cast_glow` (MAGIC_MISSILE). Confirm the identical binding fires for a `SPELL_RELEASE`-triggered beam cue — otherwise the beam never receives the delete and leaks past cast-stop. If binding is `SPELL_CAST`-only, either extend it to release cues or move the channel beam to a `SPELL_CAST` trigger.

### B.8 `self_flash` (SELF_FX — opt-in) / `aura_ground` (AURA — supported, decision 11)

| field | value | rule / grounding |
|---|---|---|
| `self_flash` trigger/anchor | `SPELL_RELEASE` / `CASTER` or `CASTER_OVERHEAD` | BLINDING_LIGHT(CASTER_OVERHEAD, PARTICLE_SYSTEM); most buffs emit **nothing** |
| `self_flash` role/lifecycle | `ONE_SHOT` / as B.5 | — |
| `aura_ground` (**default**) trigger/anchor | `SPELL_RELEASE` / `CASTER` | the common aura visual — a flourish at the caster's feet that **follows** them while it plays (an aura is attached to the caster). `CASTER`, not `GROUND_UNDER_CASTER`, because `FOLLOW_ANCHOR` needs a real transform (the computed ground point isn't followable). |
| `aura_ground` attach / role / lifecycle | `FOLLOW_ANCHOR` / `ONE_SHOT` / `PARTICLE_SYSTEM` or `DURATION` (by `self_terminating`) | first exemplar: `WARDING_AURA` → `VFX_HOLY_AURA_GROUND_01` (`aura_1`, DURATION 2000 — 2 looping child systems, so PARTICLE_SYSTEM would linger). **Prefab must be ground-pivoted** (base at the pivot) or it sinks below the feet. |
> **Auras are the DRYest archetype:** an aura spell is just a brief ground flourish — **no animation, no cast glow, no muzzle, no projectile, and no persistent visual**. The AURA archetype requests only `aura_ground`, and the animation resolver returns no animation for it (no explicit entry, no template clip → nothing plays). The persistent buff is exclusively authoritative gameplay state.

### B.9 Coverage check

**Every `delivery.kind` maps to an archetype — all spell types are covered (no deferrals):**

| `delivery.kind` (+ discriminator) | archetype | slots |
|---|---|---|
| `PROJECTILE` | Projectile | cast_glow, character_fx*, muzzle*, projectile_body, projectile_trail*, impact |
| `CHANNEL` + fires projectiles | Projectile | cast_glow, character_fx*, muzzle*, projectile_body, projectile_trail*, impact |
| `CHANNEL` + beam | Beam | cast_glow, character_fx*, beam |
| `INSTANT_BEAM` | Beam (charged) | cast_glow, character_fx*, beam |
| `DIRECT_TARGET` | TargetHit | cast_glow, character_fx*, impact @ IMPACT_POINT |
| `AREA` + sky_origin | SkyDrop | cast_glow, character_fx*, travel_body, impact |
| `AREA` + targeting SELF | SelfNova | cast_glow, character_fx*, burst |
| `AREA` + targeting POINT/TARGET | GroundAoe | cast_glow, character_fx*, impact |
| `APPLY_STATUS` / `SELF_RESOURCE` (SELF) | SelfFx | cast_glow, character_fx*, self_flash* |
| `APPLY_STATUS` (TARGET) / `CONSUME_STATUS` | TargetHit | cast_glow, character_fx*, impact @ IMPACT_POINT |
| `REMOVE_STATUS` | TargetHit / SelfFx | cast_glow, character_fx*, cleanse flash |
| `AURA` | Aura | cast_glow, character_fx*, `aura_ground` (brief feet flourish) |

`*` denotes an optional look: the archetype supports the slot, but generation omits it when neither the school palette nor a per-spell override provides an entry.

**The projectile body is never dropped.** For a projectile spell the `projectile_body` slot is filled by the school VFX set's generic body **or** a per-spell signature override — either way a registered `vfx_id`. And it cannot be silently forgotten: **server Rule 18 hard-fails any projectile spell that resolves to zero `PROJECTILE_BODY` cues** (exactly one required at index 0). A missing projectile prefab is a build-time contract error, not a broken spell at runtime.

**Cells still needing a checked-in visual exemplar:** `muzzle`, `character_fx`, `beam × CHANNEL` (only ELECTROCUTE, parked — decision 9), and `cast_glow` on SKY_DROP/BEAM. The authoring/runtime contracts support them; they simply have no current palette entry to inspect in play. `projectile_body` index >0 remains intentionally unsupported because multi-projectile channels reuse index 0.

---

## Appendix C — Animation template table (per-`(weapon × animation-archetype)` defaults)

The animation-side twin of Appendix B: what a `(weaponProfile × animation_archetype)` template supplies, so the step-2 templates encode against a fixed contract. The animation archetype is just the 3 buckets from §2.1 (`INSTANT`/`CHARGED`/`CHANNEL`); the VFX archetype (Appendix B) does **not** enter here — a Fireball and a Frost-Needle share one INSTANT animation template and differ only in VFX.

**What lives where (the DRY split):**
- **Template (weapon-agnostic archetype default):** `requiresCombatStance`, `combatEntryMode`, derived `presentationMode`, default `playbackLayer`, and hold-state routing. These are archetype constants, authored once.
- **Per-weapon override:** the hold *clips* (`defaultSpellCastHold` is already per-set — a staff channel pose ≠ a sword one) and rare layer/stance tweaks (e.g. a shield-bearer can't two-hand a staff channel).
- **Per-spell:** the gesture/release *clip* (each spell's motion), a signature `holdOverride`, `animatedProp`, and any `playbackLayer` override. The clip is the one thing the template can't supply — but for the profile-less `SPELL_*` set it's currently authored **identically in two weapon sets**; a weapon-agnostic archetype template + one shared clip per spell is exactly what collapses that duplication.

**Shared modifiers:** `MODE` (INSTANT/CHARGED/CHANNEL), `MOBILITY` = `cast_mobility` (MOBILE / GROUNDED_STATIONARY), `HAND` (same resolved cast hand as Appendix B / E7).

| field | INSTANT | CHARGED | CHANNEL | grounding / rule |
|---|---|---|---|---|
| `presentationMode` (derived) | `ReleaseOnly` | `HoldThenRelease` | `HoldOnly` | derived from `cast_time`+`behavior`; **this cycle validated-not-flipped** (decision 3) — template computes it, validator checks agreement, field stays authored |
| `requiresCombatStance` | `true` | `true` | `true` | constant — 87/87 entries today |
| `combatEntryMode` (default) | `ImmediateForFullBodyAnimatedAfterUpperBody` (3) | `AnimatedAfterCast` (2) | `AnimatedAfterCast` (2) | 3 keeps moving instant casts mobile; 2 lets the charge/channel animate into stance. Tunable per weapon. |
| active `playbackLayer` (default) | `UpperBodyWhileMoving` (0) | *hold layer, below* | *hold layer, below* | 0 is the dominant instant default (preserve locomotion while moving); overrides → `FullBody` for committed casts, `LeftGesture` for subtle gestures |
| **hold `playbackLayer`** | n/a | `GROUNDED_STATIONARY → FullBody (1)` · `MOBILE → UpperBody (2)` | `UpperBody (2)` default · `FullBody (1)` for full-commit | CHARGED grounded = brief full commit → FullBody (METEOR/ICICLE/INSTANT_BEAM); GLACIAL_SPIKE (MOBILE) → UpperBody. CHANNEL defaults UpperBody even when grounded so facing/aim stay responsive while sustaining (MAGIC_MISSILE) |
| **hold-state routing** | n/a | `FullBody→SpellCastHoldAction{1-4}` · `UpperBody→UpperBodySpellCastHoldAction{1-4}` | same | **must** resolve to a loop-capable state; `LeftGesture`/`UpperBodyWhileMoving` auto-exit at 0.9 and silently freeze the hold (§6.8) — enforce as a validator rule, not a convention |
| hold clips (`enter`/`idleLoop`) | n/a | `holdOverride` (per-spell) ?? `defaultSpellCastHold` (per-weapon) | same | fallback already exists (`TryResolveHoldProfile`, `CombatAnimationSet.cs:579`) |
| release clip | per-spell gesture (`ground`/`air`); `OnReleaseFrame` drives timing | per-spell release gesture, played after the hold | **none** (HoldOnly) | clip stays per-spell; obsolete float fields ignored (§6.14) |
| exit timing | n/a | `exitDelaySeconds`/`exitBlendOutSeconds` (hold profile, with defaults) | same, or immediate on cast-stop | — |
| COMBAT_CAST / timing driver | client **prediction** path (excludes holds) | `ActiveCast` **scheduled release** (`ComputeReleaseStartMs`) | `ActiveCast` enter→loop; **release COMBAT_CAST suppressed** (`OnCombatCast`, gated `!PlaysSpellReleasePresentation`, §6.9) | changing how `PlaysSpellReleasePresentation` is computed must keep all three paths in lockstep (§6.9, `reference_spell_hold_loop_states.md`) |

**Coverage / exemplars:** INSTANT = ~40 spells (well-exercised). CHARGED = METEOR/ICICLE/INSTANT_BEAM (grounded, FullBody hold) + GLACIAL_SPIKE (mobile, UpperBody hold) — both hold-layer branches have an exemplar. CHANNEL = MAGIC_MISSILE/FROZEN_SPLINTERS (UpperBody hold, channel-projectile) — exercised; the channel-*beam* animation would reuse the same CHANNEL template (only its VFX differs), so ELECTROCUTE being parked (decision 9) costs the animation side nothing.
