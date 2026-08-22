# Spell Cast-Animation Motion Contract

**Status:** Implemented and fully cut over 2026-08-22.

Spell identity, combat-set pose, and cast timing are separate concerns:

    spell id -> semantic motion, fixed exception, or explicit no-animation assignment
    combat animation set + semantic motion -> animation family
    family + cast hand + gameplay archetype -> concrete clips and presentation mode

There is no per-spell animation array on CombatAnimationSet and no spell-level raw family name.

## Authoring ownership

### Spell classification

Assets/Arena/Resources/SpellCastAnimationMap.asset is the only spell-level cast-animation
authority. Every entry is one of:

- Motion: a semantic SpellCastMotion (Direct1H, Direct2H, Raise, Call, Ground, Omni, or Special).
- Fixed: a complete WeaponSpellAnimationEntry that ignores the active combat animation set.
- NoAnimation: an explicit declaration that the spell plays no cast animation.

Use Fixed only when the spell's animation is part of the spell's identity rather than a weapon-pose
choice. Current fixed exceptions are BATTLE_CRY, GROUND_SLASH, RAIN_OF_ARROWS,
BLESSED_SHIELD, and RADIANT_BURST. Battle Cry therefore uses the GreatSword Buff/Buff_Air
clips with every combat set.

UPHEAVAL is classified as Raise.

### Combat animation set bindings

Each CombatAnimationSet owns spellCastMotionBindings. A binding maps one semantic motion to a
base name in SpellCastAnimationLibrary.

The current Greatsword (TwoHandedSword) bindings include:

| motion | family |
|---|---|
| Direct1H | MagicAttackDirect1H01 |
| Raise | MagicAttackCall1H01 |
| Call | MagicAttackCall1H02 |
| Ground | MagicAttackGround01 |
| Omni | MagicAttackOmni01 |
| Special | SpecialMagicAttack01 |

oneHandedCastHand selects _L or _R for one-handed families. Greatsword currently uses Left,
so UPHEAVAL resolves to the HumanM@MagicAttackCall1H01_L family and a Call spell resolves to
HumanM@MagicAttackCall1H02_L. ArcherBow/Precision uses Left and SwordAndShield uses Right.
Two-handed families ignore this field.

Ground describes a visibly ground-directed casting gesture; it is not inferred from gameplay
targeting. Every current set binds Ground to MagicAttackGround01, and oneHandedCastHand selects its
_L or _R variant.

Direct2H is a preferred gesture, not a hard requirement. Daggers and Staff bind it explicitly to
MagicAttackDirect2H02. Sets that cannot free both hands omit the Direct2H binding; the resolver then
falls back to that set's Direct1H family and oneHandedCastHand. MagicAttackDirect2H01 is prohibited.

### Family library

Assets/Arena/Resources/SpellCastAnimationLibrary.asset contains the real clip references. Each
family contains:

- oneShot: Base — held-cast enter/wind-up.
- load: Base - Load — charge/channel loop.
- cast: Base - Cast — release, and the preferred instant gesture.

Run Arena/Spell Animation/Rescan Cast Families after adding or renaming source clips.

## Archetype composition

SpellAnimationArchetypes derives the archetype from authoritative gameplay. The composer remains
the only stitch-selection implementation:

| archetype | concrete presentation |
|---|---|
| Instant | cast (fallback oneShot), ReleaseOnly |
| Charged | oneShot -> load -> cast, HoldThenRelease |
| Channel | oneShot -> load, HoldOnly |

One-hand families use the loop-capable LeftGesture or RightGesture states for instant, hold, and
charged release, keeping the weapon-bearing arm on its base pose. Two-hand families use the
existing upper-body/full-body policies in SpellCastAnimationComposer.

Timing still comes from animation events:

- OnEnterComplete: enter-to-loop handoff.
- OnReleaseFrame: gameplay/VFX release alignment.
- OnInstantCastStart: optional instant-only startup trim.
- OnLowerBodyUnlock and OnVisualInterruptible: full-body recovery policy.

## Resolution contract

SpellCastAnimationResolver.TryResolve is the only runtime lookup:

1. Normalize the spell id and find its global map entry.
2. If the entry is NoAnimation, suppress cast animation without reporting a resolution failure.
3. If the entry is Fixed, return its fixed presentation without consulting the combat set.
4. Otherwise require an active CombatAnimationSet.
5. Resolve the entry's motion through that set's spellCastMotionBindings.
6. Resolve the family from SpellCastAnimationLibrary.
7. Compose family + set hand + authoritative archetype.
8. Apply the motion entry's optional layer, combat-entry-mode, or animated-prop overrides.

Successful motion resolutions are cached. Validation and asset OnValidate callbacks invalidate the
cache after authoring changes.

Missing map entries, missing required set bindings, missing families, and incomplete archetype
variants fail closed and produce a targeted runtime/editor diagnostic. The only semantic fallback
is Direct2H -> Direct1H when a set intentionally omits Direct2H. There is no fallback to a deleted
legacy spell row or to a spell-level family.

## Authoring workflow

For a normal spell:

1. Add one Motion entry to SpellCastAnimationMap.asset.
2. Ensure every runtime-loadable combat set binds that motion.
3. Ensure the selected families contain the variants required by the spell's archetype.
4. Stamp required animation events.
5. Inspect Arena/Spell Animation/Resolved View.
6. Run Combat VFX validation and the server/editor test suites.

For a fixed exception:

1. Add one Fixed entry to SpellCastAnimationMap.asset.
2. Author its complete ground/air, layer, entry mode, hold, and prop data there.
3. Verify it resolves to the same clips with at least two different combat sets.

For a spell that intentionally has no cast animation:

1. Add one NoAnimation entry to SpellCastAnimationMap.asset.
2. Leave motion, fixed presentation, playback overrides, and animated prop empty.
3. Do not omit the entry; omission means the spell still needs classification review.

Do not add spell-specific animation fields back to CombatAnimationSet. If a new distinction is
shared by multiple spells and legitimately varies by combat set, add a semantic motion and bind it
per set. If it must never vary by set, use a fixed exception.
