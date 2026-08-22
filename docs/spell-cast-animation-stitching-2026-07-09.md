# Spell Cast-Animation Recipe Contract

**Status:** Catalog path implemented 2026-08-23; legacy motions remain migration data.

The primary authoring flow is now:

    reusable recipe -> global spell selection -> optional CombatAnimationSet spell override

A recipe is an exact presentation. It is either a single cast/release clip or an authored
Start/Loop/End sequence. The resolver does not rewrite a catalog recipe from the spell's gameplay
archetype. Archetype is compatibility metadata in the picker and validator.

## Authoring ownership

`Assets/Arena/Resources/SpellCastAnimationCatalog.asset` owns the reusable choices. Each recipe has
a stable `animationId`, picker label/category, compatibility tags, and its exact clip graph:

- `ReleaseOnly`: one movement-state-independent cast/release clip.
- `HoldThenRelease`: hold Enter, hold Loop, then the cast/release/end clip.
- `HoldOnly`: hold Enter and Loop without a release clip.

The catalog may reference any compatible humanoid animation. It is intentionally independent of
weapon/combat profile, so Daggers can select a Mage or Staff-looking recipe. Selected old Magic
Attacks animations also live in this catalog for sparing reuse.

`Assets/Arena/Resources/SpellCastAnimationMap.asset` owns the global selection for each spell. New
entries use `Catalog` plus an `animationId`. Existing entries may remain `LegacyMotion` while they
are reviewed. `Fixed` is retained for bespoke inline presentations, and `NoAnimation` remains an
explicit opt-out.

Each `CombatAnimationSet` has a small `spellCastAnimationOverrides` escape hatch. An override stores
only `{ spellId, animationId }`; it never duplicates clips or timing. Do not add an override unless
the global recipe visibly fails that specific combat pose.

## Resolution order

`SpellCastAnimationResolver.TryResolve` is the only runtime lookup:

1. Find the normalized global spell mapping.
2. `NoAnimation` suppresses playback.
3. If the active combat set has a spell override, resolve that catalog recipe.
4. Otherwise, a `Catalog` mapping resolves its global recipe.
5. `Fixed` resolves its inline migration presentation.
6. `LegacyMotion` uses the old motion -> set family -> hand/archetype composer.
7. A missing or invalid selected recipe fails closed; it does not silently fall back to legacy.

The catalog recipe's phases and playback layer are copied exactly. Spell-level optional layer,
combat-entry, and animated-prop overrides are applied last.

## No spell ground/air axis

`WeaponSpellAnimationEntry` has one `clip`. Grounded state no longer participates in spell clip,
release timing, lower-body unlock, visual-interrupt, prop-handoff, preview, or validation lookup.
The same recipe therefore plays when the avatar is grounded or airborne.

This does not remove ground-directed gestures. A recipe may visibly aim at the ground; it simply is
not selected from a second airborne clip slot. Melee ground/air presentation remains a separate
system and is unchanged.

Existing inline fixed entries migrated their prior ground clip to the single clip. The one fixed
entry that had a distinct airborne alternative (`BATTLE_CRY`) keeps its ground version as the
canonical presentation.

## Mage Animation Pack conventions

The initial catalog exposes a reviewed subset rather than all imported clips:

- Projectile Cast 1 and 2
- Aimed Cast
- phased Skill Cast 1 and 2
- single-shot Skill Cast 3, 4, and 5
- Buff Cast
- one retained legacy Direct Cast sequence

Single shots and sequence Start/End clips must be non-looping. Sequence Loop clips remain looping.
Release/end clips retain `OnReleaseFrame`; use the animation event stamper to tune the physical
release pose after visual review. Other established markers still apply when the chosen layer needs
them:

- `OnEnterComplete`: optional Enter -> Loop handoff tuning.
- `OnInstantCastStart`: optional startup trim for confirmed instant casts.
- `OnLowerBodyUnlock` and `OnVisualInterruptible`: full-body recovery timing.

The initial single-shot Mage recipes are tagged for instant and timed casts. Start/Loop/End recipes
are tagged for timed casts only. Channel spells keep their legacy `HoldOnly` presentations until a
channel-specific recipe is deliberately added; their lifecycle has no completed release/end phase.

## Editor workflow

1. Open `Arena > Spell Authoring > Open Spell Authoring`.
2. Select the spell.
3. Choose `Cast Animation > Global Recipe` from the categorized dropdown.
4. Expand `CombatAnimationSet Overrides` and change only a set that looks wrong.
5. Use the CombatAnimationSet spell preview or `Arena > Spell Animation > Resolved View`.
6. Stamp/tune animation events on the selected clips after visual review.
7. Run Combat VFX validation and the editor/server gates.

`FLAMING_ORB` is the migration pilot and currently selects `MAGE_PROJECTILE_CAST_02`. All other
ordinary spells stay on `LegacyMotion` until they are deliberately handpicked in Spell Authoring.

## Migration rules

- Migrate one spell or tightly related group at a time.
- Prefer the global recipe; overrides are exceptions, not a parallel assignment table.
- Do not bulk-map spells by targeting type or semantic motion.
- Do not expose the entire imported pack as a raw uncurated dropdown.
- Keep `SpellCastAnimationLibrary`, semantic motion bindings, and the composer only while legacy
  mappings still use them.
- Delete a legacy motion/family only after no map entry resolves through it and validation/tests
  cover the replacement.
