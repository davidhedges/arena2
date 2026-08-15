# Spell Cast-Animation Migration — living checklist

Companion to `docs/spell-cast-animation-stitching-2026-07-09.md`. Tracks moving each spell off its
hand-authored per-weapon `WeaponSpellAnimationEntry` onto a weapon-agnostic flavor family.
**Generated 2026-07-09** from the live `CombatAnimationSet` assets. Regenerate the "delete from"
column whenever a set changes (it reflects current explicit-entry membership).

## Per-spell workflow (order matters)

For each spell you want on the family system:
1. **Map** it: add `spellId → baseName` to `Assets/Arena/Resources/SpellCastAnimationMap.asset`
   (pick the flavor family that fits the spell's feel).
2. **Set the hand** (once per weapon set, not per spell): `oneHandedCastHand` (Left/Right) on each
   `CombatAnimationSet`. 2H flavors (Omni/Special/Direct2H) ignore this; 1H flavors use it.
3. **Delete** the spell's explicit entry from the sets listed below — *after* mapping, so it never
   goes silent (delete before map = no animation until mapped).
4. **Test** in Play Mode (re-enter play to reload the asset; no publish/resync — client-side only).

Leave a spell's explicit entry in place to **opt it out** or to give one weapon/class a bespoke look
(explicit always wins over the family). This is the escape hatch for the four-set spells below.

## Nuance: the four-set spells (one flavor for all vs. per-class look)

`GLACIAL_SPIKE`, `BLINDING_LIGHT`, `FROZEN_GRASP` are cast by casters *and* paladins, and today carry
a different clip per class. Under the flavor model a spell maps to **one** family, so every weapon
plays that flavor (with its own hand). If you want the paladin look to stay distinct, **keep an
explicit entry in S&S/Daggers** and only delete the caster (Staff/2H) entries.

## Weapon-set hand — set once

| set | profile | suggested `oneHandedCastHand` |
|---|---|---|
| Staff | STAFF | (your call) |
| TwoHandedSword | TWO_HANDED_SWORD | Left (frees the left hand — your ICICLE test) |
| SwordAndShield | SWORD_AND_SHIELD | (your call — sword hand vs shield hand) |
| Daggers | DAGGERS | (your call) |

### Left-hand 1H mask now covers the whole cast (2026-07-09)

A **left-hand one-handed** cast (1H flavor + `oneHandedCastHand=Left`, e.g. the greatsword) plays on
the masked `LeftGesture` layer, keeping the weapon-bearing **right arm on its base pose** (gripping
the sword) for **all three archetypes** — instant, charged, and channel. Previously only the instant
one-shot was masked; the charged/channel hold-loop and the charged release now stay masked too (new
loop-capable `LeftGestureSpellCastHoldAction1..4` animator states). No authoring change: it's derived
from the family's hand-style + the weapon's cast hand. 2H flavors and right-hand 1H are unaffected
(no right-arm mask exists). To opt a spell's charged release back to a full-body finish, that's a
one-line composer revert — ask.

## Checklist — CHARGED (proven; do these first)

- [ ] `GLACIAL_SPIKE` — delete: Staff, 2H, S&S, Dag *(four-set: consider keeping S&S/Dag explicit)*
- [x] `ICICLE` — delete: Staff *(2H already migrated in the first test)*
- [ ] `INSTANT_BEAM` — delete: Staff, 2H
- [ ] `METEOR` — delete: Staff, 2H

## Checklist — CHANNEL

- [ ] `MAGIC_MISSILE` — delete: Staff, 2H
- [ ] `FROZEN_SPLINTERS` — delete: Staff, 2H
- [ ] `ELECTROCUTE` — delete: Staff, 2H *(parked legacy relic — may skip)*

## Checklist — INSTANT (the bulk; elemental projectiles/AoE first, self-buffs last or never)

Projectiles / AoE (want a real cast gesture):
- [x] `FIREBALL` — Staff, 2H
- [ ] `VAMPIRIC_ORB` — Staff, 2H
- [ ] `WITHERING_ORB` — Staff, 2H
- [ ] `ORBITING_BLADES` — Staff, 2H
- [ ] `LIGHTNING` — Staff, 2H
- [ ] `ERUPTION` — Staff, 2H
- [ ] `FROST_NEEDLE` — Staff, 2H
- [ ] `FROST_NOVA` — Staff, 2H
- [ ] `ICE_SPIKES` — Staff, 2H
- [ ] `FROZEN_GRASP` — Staff, 2H, S&S, Dag *(four-set)*
- [ ] `GROUND_SLASH` — Staff, 2H
- [ ] `NEGATE` — Staff, 2H
- [ ] `BLINDING_LIGHT` — Staff, 2H, S&S, Dag *(four-set)*
- [ ] `BLESSED_SHIELD` — S&S *(set the map entry's `animatedProp` override — copy it from the explicit entry — so the shield visual survives)*
- [x] `BLADE_BARRIER` — S&S *(uses the standard left-hand `MagicAttackCall1H02` one-shot; the replaced target-field spell no longer launches or hides an animated sword prop)*
- [ ] `SACRED_FLAME` — S&S, Dag
- [ ] `CONSECRATE` — S&S, Dag
- [ ] `CLEANSING_TOUCH` — S&S, Dag
- [ ] `ABSOLUTION` — S&S, Dag

Self-buffs / shouts / auras (may keep bespoke baked clips — e.g. warrior shouts):
- [ ] `INTIMIDATE`, `SHOCKWAVE`, `BATTLE_CRY`, `ENRAGE`, `DEFIANCE`, `FORTIFY`, `MOMENTUM`,
  `IRON_WILL` — Staff, 2H
- [ ] `BATTLE_TRANCE`, `BERSERKING`, `FEAST`, `FRENZY`, `SECOND_WIND` — 2H
- [ ] `FERVOR` — S&S, Dag
- [ ] `AURA_OF_VENGEANCE`, `MANA_FONT`, `STAMINA_FONT`, `THORNS_AURA`, `WARDING_AURA`,
  `SERRATED_BLADES` — S&S
