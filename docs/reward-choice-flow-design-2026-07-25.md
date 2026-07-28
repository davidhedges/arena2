# Level Up Reward — Web Prototype Specification

**Status:** Current approved web proof of concept as of 2026-07-29.

This specification supersedes the earlier staged parchment-card reward flow.
The earlier reward-type, school, and weapon-style screens are not retained.

## Goal

Present one cohesive Level Up screen where the player can:

- review and allocate available ability points;
- choose either one spellcasting school or one combat discipline;
- confirm the provisional choices and begin the existing dice-roll
  presentation.

The web prototype is the visual and interaction source of truth for this
screen. It does not apply progression or authorize a gameplay outcome.

## Scope

The current proof of concept includes:

- the full-screen gothic presentation;
- host-supplied ability-stat rows and point budget;
- host-supplied spellcasting-school choices;
- combat disciplines derived from the progression catalog;
- mutually exclusive school/discipline selection;
- point increment, decrement, and reset behavior;
- selection persistence while the screen remains open;
- a Confirm & Roll presentation preview;
- pointer, keyboard focus, accessible labels, and reduced-motion handling.

It does not include:

- a runtime UI Toolkit translation;
- a reward trigger or host lifecycle;
- progression mutation or persistence;
- eligibility filtering;
- multiplayer pause or coordination behavior;
- server-authoritative reward resolution;
- changes to the existing dice assets, animation, presenter, reducer, or
  motion profiles.

## Canonical prototype

The approved prototype lives at:

- `docs/ui-prototypes/reward-choice/index.html`
- `docs/ui-prototypes/reward-choice/reward-choice.css`
- `docs/ui-prototypes/reward-choice/reward-choice.js`
- `docs/ui-prototypes/reward-choice/assets/`

The prototype follows the project's 1920×1080 browser-stage convention and
scales the complete composition to the available viewport.

## Presentation

The screen is one authored gothic composition, not a sequence of cards or
modal windows:

- a monumental black-stone and oxidized-metal outer frame;
- hooded architectural guardians at the extreme sides;
- an open central field with no rectangular content cards;
- a floating Level Up title and instruction;
- ability allocation on the left;
- spellcasting schools in a blue radial arrangement;
- combat disciplines in an amber radial arrangement;
- a small OR medallion between the two paths;
- one centered Confirm & Roll control.

The ability and radial compositions float directly over the background. The
old parchment surfaces, card borders, corner sprites, and inset panel frames
are intentionally absent.

### Radial centers and connectors

The spellcasting hub uses an illustrated silver-and-sapphire astrolabe with a
crystalline star core. The combat hub uses an illustrated forged war seal with
crossed blades.

Connectors:

- begin at the illustrated hub rim;
- end before the option-medallion rim;
- use alternating linked ellipses over a dark rail;
- never pass under a selectable medallion;
- use blue for spellcasting and amber for combat disciplines.

## Ability points

The current allocatable stat collection reflects Arena's supported allocation
model:

1. Might
2. Insight
3. Finesse
4. Quickness
5. Fortitude

Rows are generated from a collection rather than individually wired markup.
The proof of concept supplies a five-point demo budget and representative base
values. A future host supplies the real point budget and current character
values.

Behavior:

- increment consumes one remaining point;
- decrement returns one point allocated during this interaction;
- values never fall below their supplied base value;
- allocation cannot exceed the supplied budget;
- Reset Points clears only allocations made during the interaction;
- remaining points are always derived from budget minus provisional
  allocations.

Spending every point is not required to preview Confirm & Roll.

## Spellcasting schools

The current player-facing presentation collection contains:

- Air
- Arcane
- Frost (`COLD` internally)
- Fire
- Holy
- Lightning
- Necromancy
- Shadow

The layout derives from the supplied collection and does not depend on fixed
DOM positions. A future authoritative reward-school source may replace this
prototype collection without changing the screen structure.

## Combat disciplines

Combat disciplines are loaded from
`server/src/progression_catalog.shared.json` when the prototype is served from
the repository root:

- Subtlety
- War
- Zeal
- Precision
- Arcana

Each discipline keeps its canonical stable ID and progression-catalog sort
order. Its associated combat profile selects the current presentation art.
The embedded fallback mirrors the same catalog so the prototype also opens
directly from disk.

## Path selection

The player chooses exactly one provisional path:

- selecting a school clears the selected combat discipline;
- selecting a discipline clears the selected school;
- the selected medallion receives a persistent illustrated halo;
- selection copy names the current choice;
- Confirm & Roll remains unavailable until a path choice exists.

Ability-point allocation is independent of the chosen path.

## Confirm & Roll

The web prototype demonstrates the presentation contract used by Arena's
existing dice work:

1. resolved roll input;
2. anticipation;
3. tumbling;
4. settling;
5. held result;
6. click-to-skip while moving;
7. click-to-dismiss while held.

The browser preview mirrors the existing d20 motion-profile timing and state
shape. It is not gameplay-authoritative randomness.

The runtime implementation must call the existing
`Arena.Presentation.Dice.DiceOverlayPresenter` with an already resolved roll.
It must not create a reward-specific dice implementation or modify the
existing dice authoring/runtime pipeline.

## Acceptance criteria

- The old staged parchment-card reward prototype is absent.
- The screen presents ability allocation and both reward paths together.
- Ability rows, schools, and disciplines render from collections.
- School and discipline selection are mutually exclusive.
- Selected state persists independently from pointer hover.
- Connectors terminate cleanly at hub and medallion rims.
- No rectangular content cards or legacy corner ornaments appear.
- Confirm & Roll uses the existing dice presentation contract.
- The prototype performs no progression mutation.
- Existing dice source, assets, plans, and server behavior remain unchanged.
