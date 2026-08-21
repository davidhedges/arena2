# Spell Cast-Animation Migration

**Status:** Complete 2026-08-22.

The legacy spell-level family and CombatAnimationSet per-spell override paths have been removed.
This file is now the cutover record and the checklist for future spell additions.

## Completed cutover

- [x] Added semantic SpellCastMotion values: Direct, Raise, Call, Omni, Special.
- [x] Changed SpellCastAnimationMap to spell -> motion or spell -> fixed presentation.
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

## Current classification inventory

The global map contains:

- 34 Direct spells.
- 3 Raise spells.
- 47 Call spells.
- 3 Omni spells.
- 1 Special spell.
- 5 fixed exceptions.

These counts are guarded by review and asset-contract tests; semantic choices may be adjusted later
without reintroducing per-set spell rows.

## Future normal-spell checklist

- [ ] Add the spell's gameplay and presentation rows through the normal spell pipeline.
- [ ] Add exactly one Motion entry in Assets/Arena/Resources/SpellCastAnimationMap.asset.
- [ ] Choose motion by visible movement, not by the source pack's ambiguous folder name.
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

## Prohibited regression paths

- Do not add a spell id or spell animation array to CombatAnimationSet.
- Do not put a raw family base name on a spell map entry.
- Do not add resolver precedence ahead of the global fixed/motion assignment.
- Do not use a fixed exception for a motion that should vary with weapon pose.
- Do not preserve stale serialized spell rows as a compatibility fallback.
