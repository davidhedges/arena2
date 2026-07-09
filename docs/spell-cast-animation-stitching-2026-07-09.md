# Spell Cast-Animation Stitching (Design Doc)

**Status:** Drafted 2026-07-09. Reframes "step 2 (animation)" of
`docs/spell-presentation-dry-redesign-2026-07-07.md` after owner clarification: the animation
problem is **variant stitching driven by cast type**, not entry de-duplication. (The dedup falls
out of this design for free — see §8.)
**Scope:** author-time surface + resolver for a spell's *cast animation* — which clip variants play
and how they stitch. Reuses the existing runtime hold/release playback unchanged.

---

## 0. What the owner asked (restated to lock understanding)

> Per spell I want to point at **one** animation. The tool figures out — from cast type — which
> variants to use and how/when to stitch them: a one-shot; a one-shot → loop; or a one-shot → loop →
> final-cast. I'll stamp transition-timing events on the clips (already supported). I want to declare
> **per weapon set** whether it casts with the left hand, both hands, etc. A rare spell (warrior
> shouts) has its own baked animation that takes precedence.

So the authored footprint of a normal spell's animation collapses to **one field: its base
animation** — everything else is derived.

---

## 1. The animation asset convention (verified)

Kevin Iglesias magic-cast clips live under
`Assets/Arena/Content/Animation/Extracted/KevinIglesias/Human Animations/Animations/Male/Combat/Spellcasting/MagicAttacks/`
in five folders (`Call`, `Directional`, `Ground`, `Omnidirectional`, `Special`). Every base
animation is a **family of exactly three clips**, by a 100%-regular naming convention:

| variant | filename | role |
|---|---|---|
| **one-shot** (start→middle→end) | `HumanM@{Base}{Hand}.anim` | full instant cast; also the *enter* wind-up for held casts |
| **loop** | `HumanM@{Base}{Hand} - Load.anim` | the sustained charge/channel loop |
| **final cast** | `HumanM@{Base}{Hand} - Cast.anim` | the release finish |

- `{Hand}` = `_L` / `_R` on one-handed bases (`…1H…`, `Ground`); **absent** on two-handed bases
  (`…2H…`, `Omni…`, `Special…`) — those are inherently both-hands.
- The codebase already classifies these by name: `CombatClipRoleNameInference` maps `- Load` →
  `SpellCastHoldIdle`, `- Cast` → `SpellRelease`, unsuffixed → `SpellCastHoldEnter`
  (`CombatClipRoleNameInference.cs:58-68`). The stamper templates already exist per role
  (`CombatClipEventTemplates`).

---

## 2. Three cast archetypes → three stitch patterns

Archetype is **derived** from gameplay (`SpellAnimationArchetypes.Derive(cast_time_ms, behavior)`,
already built). Each maps to one stitch pattern and one presentation mode:

| archetype | derivation | clips played | presentationMode |
|---|---|---|---|
| **Instant** | `cast_time==0 && !channel` | `{Base}` one-shot, full | `ReleaseOnly` |
| **Channel** | `behavior==CHANNEL` | `{Base}` (enter, to `OnEnterComplete`) → `{Base} - Load` (loop until released) | `HoldOnly` |
| **Charged** | `cast_time>0` | `{Base}` (enter) → `{Base} - Load` (loop while charging) → `{Base} - Cast` (release) | `HoldThenRelease` |

The one-shot's *beginning* is the wind-up (enter); for held casts its middle/end are skipped in favor
of the loop, and the finish comes from the `- Cast` clip. Exactly the owner's "stitch the beginning
of the one-shot to the loop, and the final cast to the end of the loop."

---

## 3. It maps onto today's entry shape with **zero runtime change**

The runtime already plays held casts from `SpellCastHoldProfile { enter, idleLoop }` and the release
from `ground`/`air` (`CombatActionPlaybackController`, `SpellCastPresentationController`,
`PlayerAnimator.SpellCastHoldAction1..4`). The resolver just fills those fields from the base family:

| archetype | `ground`/`air` (release) | `holdOverride.enter` | `holdOverride.idleLoop` |
|---|---|---|---|
| Instant | `{Base}{Hand}` | — | — |
| Channel | — (release suppressed, HoldOnly) | `{Base}{Hand}` | `{Base}{Hand} - Load` |
| Charged | `{Base}{Hand} - Cast` | `{Base}{Hand}` | `{Base}{Hand} - Load` |

Release timing keeps coming from the release clip's `OnReleaseFrame`; enter→loop hand-off from the
enter clip's `OnEnterComplete` — both already stamped/read today. **Nothing in the animator, the
presentation controller, or the release scheduler changes.**

---

## 4. Authoring surface (the whole footprint of a normal spell)

**Model confirmed 2026-07-09 (Option 3 — flavor on the spell, hand from the weapon):** the animation
"flavor" (sky→ground, ground→sky, directional, omni, special = the pack's `Call`/`Ground`/
`Directional`/`Omni`/`Special` families) is a **per-spell** choice, authored **weapon-agnostically**.
The hand comes from the weapon. There is **no incompleteness gap**, because the flavor decides
whether the hand even applies:
- **1H flavors** (Call, Ground, Directional-1H — the families with `_L`/`_R`) → cast with the
  weapon's declared hand. Any weapon can free a hand for a one-handed cast (incl. a 2H sword).
- **2H flavors** (Omni, Special, Directional-2H — no hand suffix) → **both hands, always, any
  weapon** (the weapon's hand is ignored; the off/shield arm just participates).

So a spell names **one** flavor family; if it's 1H the weapon supplies L/R, if it's 2H both hands are
used. One assignment per spell, one hand per weapon set — full "author once", no per-weapon or
per-hand-style duplication. (This supersedes the earlier "1H base + 2H base per spell" note — the
2H-is-hand-agnostic rule makes that unnecessary.)

1. **Per spell (weapon-agnostic): one flavor-family reference** — a base name (e.g.
   `MagicAttackGround01`) in a new weapon-agnostic `SpellCastAnimationMap` (`spellId → baseName`).
   This *is* the "point it at an animation" field. The base's own `handStyle` (1H vs 2H, from the
   scan) decides whether the weapon hand applies.
2. **Per weapon set: a one-handed cast hand** — `Left` / `Right` ("this weapon casts one-handed
   spells with the left/right hand"). One enum on `CombatAnimationSet`; used only for 1H flavors,
   ignored for 2H flavors.
3. **Per clip: stamped transition events** (`OnEnterComplete`, `OnReleaseFrame`, …) — already
   supported; owner-accepted. Bounded burden: the one-shot needs `OnEnterComplete` (held-cast
   enter→loop hand-off) + `OnReleaseFrame` (instant release timing); the `- Cast` clip needs
   `OnReleaseFrame`; the `- Load` loop needs nothing.
4. **Rare per-spell override: an explicit baked clip** (warrior shouts, greatsword pack). Wins over
   family resolution — this is today's explicit `WeaponSpellAnimationEntry`, kept as the top
   resolution layer (explicit-wins, already how `SpellAnimationResolver` layers).

Everything else (presentationMode, enter/loop/cast selection, loop-capable layer, timing) is derived.
A spell with no base for the casting weapon's style (never cast by that style in practice) simply
resolves to nothing — harmless.

---

## 5. The family library (auto-built, owner barely touches it)

A `SpellCastAnimationFamily` asset per base holds real clip references (so they land in the build —
Content/ clips can't be `Resources.Load`ed at runtime). One family = one base's variants:
- `handStyle: OneHand` → `{ baseName, left:{oneShot,load,cast}, right:{oneShot,load,cast} }`
- `handStyle: TwoHand` → `{ baseName, twoHand:{oneShot,load,cast} }`

An editor tool **scans the MagicAttacks folders and auto-populates** these from the naming
convention (one "Rescan families" button) — it groups the three `{Base}` / `{Base} - Load` /
`{Base} - Cast` clips per (base, hand) and infers OneHand (has `_L`/`_R`) vs TwoHand (no suffix).
The owner picks families by name per spell (a 1H and/or 2H base); the tool wires the clips.

Resolution then composes at the existing choke point:
`SpellAnimationResolver.Resolve(spell, weapon)` → explicit baked entry (layer 1, wins) → else
`family = map[spellId]`; if `family.handStyle == OneHand` pick `family.left/right` by
`weapon.oneHandedCastHand`, else use `family.twoHand` (weapon-agnostic); × `archetype` → concrete
(enter, idleLoop, release) + derived presentationMode + loop-capable layer.

---

## 6. What this reuses vs. adds

**Reuses (unchanged):** the animator controller + `SpellCastHoldAction` states; `SpellCastHoldProfile`;
`CombatActionPlaybackController` hold phases; `SpellCastPresentationController` release scheduling;
`SpellAnimationArchetypes.Derive`; `SpellAnimationResolver` layering; clip-event stamper + role
templates; `CombatClipRoleNameInference`.

**Adds:** (a) `SpellCastAnimationFamily` asset + auto-scan editor tool; (b) weapon-agnostic
`SpellCastAnimationMap` (`spellId → family`); (c) a `castHand` enum on `CombatAnimationSet`; (d) the
compose step in the resolver (family × hand × archetype → clips); (e) inspector/validator: preview
the resolved stitch + flag families missing a required variant.

---

## 7. Playback layer (the one thing not derivable from cast type)

`playbackLayer` (FullBody / UpperBody / UpperBodyWhileMoving / LeftGesture) is genuinely per-spell/
per-weapon (§Appendix C of the redesign doc). It stays a template/per-spell default, **not** part of
the base-animation family. Channel/charged must resolve to a loop-capable layer
(`SpellCastHoldAction*`) — kept as a validator rule (design doc §6.8).

---

## 8. Bonuses (fall out for free)

- **Fixes the presentationMode desync (decision 3).** Today the 4 charged spells are authored
  `ReleaseOnly` and never stitch; deriving the archetype makes them stitch correctly, and
  presentationMode stops being hand-authored (it becomes derived output).
- **Kills the per-weapon duplication.** The spell→animation mapping becomes weapon-agnostic
  (authored once in `SpellCastAnimationMap`); a weapon set contributes only its hand convention +
  rare baked overrides. This is the *right* mechanism for the DRY win the redesign doc §0 flagged —
  no entry copy-paste to maintain.

---

## 9. Build phases

1. **Family model + auto-scan — ✅ DONE (2026-07-09, compile- + parse-verified; not yet committed).**
   `SpellCastAnimationLibrary.cs` (runtime: `SpellCastHandStyle`/`SpellCastHand` enums,
   `SpellCastClipTriple`, `SpellCastAnimationFamily`, the `SpellCastAnimationLibrary` ScriptableObject)
   + `SpellCastAnimationLibraryBuilder.cs` (editor: `Arena/Spell Animation/Rescan Cast Families`
   menu → scans MagicAttacks → writes `Assets/Arena/Resources/SpellCastAnimationLibrary.asset`).
   Both assemblies compile 0 errors. The parse+group algorithm, replayed against the real 40 clips,
   yields **9 families** (4 one-handed w/ full L+R triples, 5 two-handed), every triple complete,
   the stray unprefixed clip deduped. Non-destructive; nothing consumes the library yet. Owner
   verifies by running the menu item and eyeballing the resulting asset + console summary.
2. **Resolver compose step — ✅ DONE (2026-07-09, compile- + unit-verified; pending Unity import +
   commit).** `oneHandedCastHand` (Left/Right) on `CombatAnimationSet`; `SpellCastAnimationMap.cs`
   (weapon-agnostic spellId→baseName SO, Resources); `SpellCastAnimationComposer.cs` (pure:
   family×hand×archetype → `WeaponSpellAnimationEntry`); `SpellCastAnimationResolver.cs` (runtime
   glue: explicit-wins → composed; loads Resources; derives archetype via
   `conn.Db.SpellDefinition`). Unit test `SpellCastAnimationComposerTests.cs` covers all 3 archetypes
   + 2H-ignores-hand + missing-clip. All assemblies build 0 errors.
3. **Wire the runtime consumers — ✅ DONE (same commit).** 5 seams routed through
   `SpellCastAnimationResolver.TryResolve`: PlayerEntity ×3 (:314/:321/:329),
   CombatActionPlaybackController.TryBindSpellClip (:998), CombatAnimationSet.TryGetSpellCastHoldProfile
   (:1587). **Safe by construction:** with no `SpellCastAnimationMap.asset` in Resources, `TryResolve`
   ≡ `TryGetSpellAnimation` (byte-identical) — nothing changes until a spell is mapped.
4. **Migrate spells to families** — point each elemental spell at its base; delete the redundant
   explicit entries; verify in-editor preview + playtest. Warrior shouts stay explicit (baked
   override).
5. **Inspector + validator** — resolved-stitch preview; missing-variant + non-loop-capable-layer
   warnings.

---

## 10. Forks (resolved 2026-07-09)

1. **Base reference granularity — RESOLVED (Option 3):** the spell names **one** flavor family
   (weapon-agnostic) from an auto-scanned dropdown; the hand comes from the weapon. 2H flavors are
   hand-agnostic (both hands, any weapon), so there is no cross-style/incompleteness gap (§4).
2. **Where the spell→base map lives — RESOLVED:** one weapon-agnostic `SpellCastAnimationMap`
   (`spellId → baseName`). Weapon sets add only a `oneHandedCastHand` (Left/Right). Full author-once.
3. **Instant = full one-shot — RESOLVED:** instant plays the full `{Base}` one-shot; revisit only if
   instants feel too long in playtest.

**Honest acceptance answer (owner's question, 2026-07-09):** with clips stamped, assigning a spell
its **one** flavor family makes the system play the correct one-shot / one-shot→loop /
one-shot→loop→cast with **no further per-spell work** — archetype from cast type, loop-capable layer
auto-derived, hand from the weapon set (1H flavors) or both-hands (2H flavors). Truly one field per
spell + one hand per weapon set.
