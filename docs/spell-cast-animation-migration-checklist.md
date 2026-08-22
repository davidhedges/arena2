# Spell Cast-Animation Migration

**Status:** Complete 2026-08-22.

The legacy spell-level family and CombatAnimationSet per-spell override paths have been removed.
This file is now the cutover record and the checklist for future spell additions.

## Completed cutover

- [x] Added semantic SpellCastMotion values: Direct1H, Direct2H, Raise, Call, Ground, Omni, Special.
- [x] Changed SpellCastAnimationMap to spell -> motion, spell -> fixed presentation, or an explicit
  no-animation assignment.
- [x] Added spellCastMotionBindings to every runtime CombatAnimationSet.
- [x] Set Greatsword Raise -> MagicAttackCall1H01.
- [x] Set Greatsword Call -> MagicAttackCall1H02.
- [x] Classified UPHEAVAL as Raise.
- [x] Migrated all 93 previously mapped or explicitly-authored spell ids into the global map.
- [x] Moved BATTLE_CRY to a fixed GreatSword Buff/Buff_Air presentation that ignores combat set.
- [x] Preserved bespoke fixed animations for GROUND_SLASH, RAIN_OF_ARROWS, BLESSED_SHIELD, and
  RADIANT_BURST.
- [x] Preserved the Blessed Shield animated-prop handoff in its fixed assignment.
- [x] Removed all 101 legacy per-set spell rows from the five CombatAnimationSet assets.
- [x] Removed CombatAnimationSet.spells and TryGetSpellAnimation.
- [x] Removed the explicit-wins resolver branch and spell-level baseName lookup.
- [x] Removed the obsolete CastAnimationMap compatibility stub.
- [x] Updated editor inspectors, resolved preview, clip-role inference, spell authoring, validators,
  tests, and server-side asset contract checks.
- [x] Added masked RightGesture one-shot and hold playback.
- [x] Set ArcherBow/Precision one-hand casting to Left and SwordAndShield to Right.
- [x] Bound Daggers and Staff Direct2H to MagicAttackDirect2H02; all other sets fall back to their
  Direct1H family and assigned hand.
- [x] Prohibited MagicAttackDirect2H01.
- [x] Bound Ground to MagicAttackGround01 on every combat animation set.
- [x] Recorded 17 intentionally unanimated spells as NoAnimation instead of leaving them ambiguous.
- [x] Applied the reviewed Direct1H, Direct2H, Raise, Call, and Ground classifications.

## Current classification inventory

The global map contains:

- 29 Direct1H spells.
- 4 Direct2H spells.
- 3 Ground spells.
- 19 Raise spells.
- 48 Call spells.
- 3 Omni spells.
- 1 Special spell.
- 5 fixed exceptions.
- 17 explicit NoAnimation spells.
- 0 player spells pending classification review.

These counts are guarded by review and asset-contract tests; semantic choices may be adjusted later
without reintroducing per-set spell rows.

## Future normal-spell checklist

- [ ] Add the spell's gameplay and presentation rows through the normal spell pipeline.
- [ ] Add exactly one Motion entry in Assets/Arena/Resources/SpellCastAnimationMap.asset.
- [ ] Choose motion by visible movement, not by the source pack's ambiguous folder name.
- [ ] For a direct cast, choose Direct1H or Direct2H by the spell's intended gesture. Direct2H may
  fall back to the set's Direct1H family/hand when the weapon pose cannot free both hands.
- [ ] Confirm every CombatAnimationSet binds that motion to a valid family.
- [ ] Confirm the family supports the spell's Instant, Charged, or Channel archetype.
- [ ] Stamp required clip events.
- [ ] Check Arena/Spell Animation/Resolved View.
- [ ] Run Combat VFX validation and server/editor tests.

## Future fixed-exception checklist

- [ ] Confirm the animation must be identical across all combat sets.
- [ ] Add exactly one Fixed entry in SpellCastAnimationMap.asset.
- [ ] Author all ground/air, layer, entry-mode, hold, and animated-prop data in that entry.
- [ ] Leave motion as None and do not also author motion overrides.
- [ ] Test resolution with at least two unrelated combat sets.

## Future no-animation checklist

- [ ] Add exactly one NoAnimation entry in SpellCastAnimationMap.asset.
- [ ] Leave motion, fixed presentation, playback overrides, and animated prop empty.
- [ ] Confirm cast release dispatch is intentionally suppressed while gameplay and VFX remain intact.

## Prohibited regression paths

- Do not add a spell id or spell animation array to CombatAnimationSet.
- Do not put a raw family base name on a spell map entry.
- Do not add resolver precedence ahead of the global fixed/motion assignment.
- Do not use a fixed exception for a motion that should vary with weapon pose.
- Do not preserve stale serialized spell rows as a compatibility fallback.
- Do not bind MagicAttackDirect2H01.
