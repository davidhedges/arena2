# Reward Choice Flow — UX Design Specification

**Status:** Web prototype implemented. No runtime UI, trigger integration,
reward resolution, progression mutation, or server schema is included.

**Owner decisions recorded:** 2026-07-25.

## Goal

Create a reusable reward-choice presentation that lets the player begin one
of two reward paths:

- **Learn a Spell**
- **Learn a Weapon Technique**

The first path asks the player to choose a spell school. The second asks the
player to choose a weapon style. Later screens will choose the exact spell or
weapon technique and confirm the reward, but those screens are intentionally
deferred while spell and reward authoring are still changing.

Floor completion, chest opening, and any other future event may host this
flow. What opens it does not alter its layout or interaction contract.

## Experience goals

- The reward feels valuable without becoming slow or ceremonial.
- The first decision is immediately understandable.
- Cards are satisfying to browse with mouse, keyboard, or controller.
- Random selection is visibly and mathematically honest.
- Spell-school and weapon-style counts can change without redesigning the
  screen.
- The presentation belongs to Arena's restrained medieval dark-fantasy UI.
- The flow never silently discards or commits a reward.

## Scope

This specification defines:

- the shared reward-flow shell
- reward-type selection
- spell-school selection
- weapon-style selection
- card anatomy and interactive states
- direct card progression plus Back and Randomize behavior
- uniform random-selection behavior
- responsive card arrangement
- the presentation data the UI expects from its future host
- accessibility and input behavior for these screens

This specification does not define:

- what event opens the flow
- pausing, multiplayer coordination, or world-state behavior while it is open
- eligibility rules for which spells or techniques may be rewarded
- the exact-spell selection screen
- the exact-technique selection screen
- final confirmation or reward application
- duplicate, upgrade, rarity, replacement, or inventory rules
- persistence, reconnect recovery, or reward deferral
- server tables, reducers, or schema fields
- card artwork production
- sound or haptics

## Terminology

Use these player-facing terms in this flow:

| Concept | Player-facing label |
|---|---|
| Magical reward path | Learn a Spell |
| Martial reward path | Learn a Weapon Technique |
| Magical category | Spell School |
| Weapon category | Weapon Style |

Do not show the combat-discipline names Subtlety, War, Zeal, Precision, or
Arcana on these screens.

Arena still uses **combat discipline** as a canonical game term, but the
requested Daggers, Greatsword, Sword & Shield, Staff, and Bow cards correspond
to the current weapon/combat profiles rather than the player-facing discipline
names. The reward flow therefore stays weapon-first and calls them weapon
styles.

Use **Greatsword**, not “Two-Handed Weapons,” while the underlying profile
represents two-handed swords rather than a general family of axes, hammers, and
other great weapons. Use **Staff** as the card label.

## Flow

```text
Reward earned
└── Choose a reward type
    ├── Learn a Spell
    │   ├── Choose a Spell School
    │   ├── Choose a Spell       [deferred]
    │   └── Confirm               [deferred]
    └── Learn a Weapon Technique
        ├── Choose a Weapon Style
        ├── Choose a Technique   [deferred]
        └── Confirm               [deferred]
```

Choosing either reward-type card immediately plays a short flourish and reveals
the corresponding school or weapon-style choices. There is no Continue action.
On the school and weapon-style screens, choosing a card plays the same flourish
and leaves a persistent selected treatment. The deferred exact-spell or
exact-technique screens will eventually follow that acknowledgement.

Randomize selects and acknowledges a school or weapon style without advancing.

The flow retains each screen's selection while the player moves backward and
forward during the same reward interaction.

## Shared presentation shell

The reward flow is a floating game-layer composition over a dark veil, not a
framed modal or web-style wizard:

- a large uncontained title floating over the choices
- a restrained rule and short instruction
- parchment choice plaques with weathered edges and ink-dark labels
- option artwork inset directly into each parchment
- no close button while the reward has no defined cancel/defer behavior
- only contextual Back and Randomize actions beneath branch choices

Reference layout at the project's 1920×1080 design resolution:

- approximately 1500×960 for the full floating composition
- cards approximately 226×286
- 16–20px between cards
- enough lower separation that Randomize reads as a utility rather than
  confirmation

These are design targets rather than a runtime layout prescription. The
eventual web prototype is the visual source of truth for final dimensions.

The shell follows:

- `docs/ui-art-direction.md`
- `docs/ui-toolkit-workflow.md`
- `docs/ui-prototypes/tokens.css`
- `docs/ui-prototypes/kit.css`

New implementation defaults to UI Toolkit. Its eventual design pipeline is
web prototype → lint → one-way UXML/USS translation → thin controller. That
pipeline is not executed by this specification.

## Screen 1: reward type

### Copy

- Title: **Claim Your Reward**
- Instruction: **Choose how you want to grow.**

### Cards

Two large cards sit side by side:

1. **Learn a Spell**
   - image direction: an open spellbook or focused magical sigil
2. **Learn a Weapon Technique**
   - image direction: a balanced composition of forged weapons

These cards have no supporting subtitles. Their images and labels carry the
choice.

Clicking either card plays a brief centered sigil flourish, settles the chosen
parchment forward, and reveals the corresponding option screen.

### Footer

- no Back action
- no Randomize action
- no Continue action

Randomize is omitted because the owner's requirement applies while choosing a
spell school or weapon style, not while choosing the reward path.

## Screen 2A: spell school

### Copy

- Title: **Choose a Spell School**
- Instruction: **Choose the tradition your new spell will come from.**

### School roster

The screen must not hardcode a permanent school list. It renders the eligible
schools provided for the current reward.

Current presentation direction includes:

- Air
- Arcane
- Fire
- Frost
- Holy
- Lightning
- Necromancy
- Shadow

**Shadow is a player-facing school.**

Use **Frost** as the player-facing label for the current internal `COLD`
category. Internal IDs do not dictate card copy.

Earth is not presumed to be a player-facing school. Stonespire or another
earth-like spell does not justify an Earth card by itself while spell
authoring remains in flux. Such a spell may remain unclassified or ineligible
for this reward path until its authoring is deliberately resolved.

The listed schools are not a frozen launch contract. Future schools appear
through the same option model and layout without screen-specific code.

### Footer

- **Back** on the left
- **Randomize** in the center
- no Continue action

## Screen 2B: weapon style

### Copy

- Title: **Choose a Weapon Style**
- Instruction: **Choose the weapon you want to deepen your mastery with.**

### Cards

The current weapon-style choices are:

| Card | Image direction |
|---|---|
| Daggers | a matched pair of short, hooked blades |
| Greatsword | one broad two-handed sword with a strong crossguard silhouette |
| Sword & Shield | an overlapping sword and shield in balanced composition |
| Staff | one engraved staff with restrained arcane fittings |
| Bow | a recurved bow with one nocked arrow |

The card title is the weapon style. No discipline-name subtitle appears.

The card collection should still be supplied as data rather than constructed
as five individually wired controls. This keeps Bow and future weapon profiles
from requiring layout changes.

### Footer

- **Back** on the left
- **Randomize** in the center
- no Continue action

## Card design

### Anatomy

Each option card contains:

1. a framed square or near-square image region
2. the option name

Do not bake labels or selection states into option artwork. One artwork asset
serves all interaction states.

The card surface is warm, weathered parchment with lightly scorched edges,
subtle folds, and restrained ink or antique-gold linework. School-identifying
color belongs primarily inside the illustration because it communicates
gameplay. Weapon illustrations favor material and silhouette over colored
decoration.

Images should contain one dominant object or magical effect, a bold silhouette,
and minimal background clutter so they remain readable at card size. School
cards are category illustrations, not individual ability icons; they do not
reuse an ability icon merely because that ability happens to belong to the
school.

### Default

- tactile parchment surface
- irregular dark edge and restrained ink border
- fully readable artwork and label
- no gold outline

### Hover and keyboard/controller focus

- lift approximately 7px
- scale to approximately 1.04
- brighten the parchment's antique-gold edge
- subtly increase artwork contrast
- transition over approximately 140–170ms with ease-out timing

Focus must be as visible as hover. Color alone is not sufficient: the lift,
edge treatment, or an additional focus inset must remain perceptible.

### Pressed

- return close to the default scale
- settle the parchment toward the scene
- keep the label readable

### Selected

- persistent ember-gold outline
- slightly raised parchment
- darkened warm-ink label
- one short settle animation
- no checkmark, checkbox, or form-style selected indicator

Selection remains visible after the pointer leaves. Hovering another card does
not weaken the selected card.

There is no continuous pulsing, shimmer, or looping movement. Selection is a
state, not an ongoing alert.

### Selected again

Selecting an already-selected card, including a mathematically valid repeated
Randomize result, replays the short settle animation. This confirms that the
input was received without changing the selection.

## Randomize contract

Randomize is a selection convenience. It does not resolve the reward and is
not gameplay-authoritative randomness because the player is always free to
choose or replace the resulting option before confirmation.

For `N` eligible options supplied to and rendered by the screen, including
options outside the current scroll viewport:

- each option has probability exactly `1 / N` on every press
- each press is independent of selection history
- the current selection remains in the sample set
- consecutive repeated results are valid
- no anti-repeat rule, weighting, pity behavior, shuffle bag, or hidden
  preference is permitted
- the result selects one card but never advances or confirms

If `N = 1`, Randomize selects that sole option. If `N = 0`, the reward flow
must not present an actionable selection screen; the future host should treat
that as invalid reward input rather than showing an empty randomization
control.

When Randomize produces the already-selected option:

- replay the card's selected animation
- replay the Randomize button's pressed feedback
- do not display “no change,” retry automatically, or draw again

If the selected card is outside the current scroll viewport in a future larger
roster, bring it into view without advancing focus past it.

## Layout and responsiveness

Card collections use centered rows and reflow from the supplied option count:

- two reward-type cards: one centered row
- up to eight schools: four columns when space permits
- five weapon styles: five columns when space permits
- narrower presentation: three columns, with the final row centered
- more school rows than fit the content region: vertical scrolling within the
  grid; title and footer remain fixed

Rows with fewer cards are centered rather than left-aligned. Card size remains
stable before gaps are widened; a sparse roster should not produce oversized
cards.

The visual hierarchy and labels must remain readable from 1280×720 through 4K,
following the project UI art direction. Final breakpoints and exact dimensions
belong to the eventual prototype.

## Input and navigation

### Pointer

- hovering previews the card state
- clicking a reward-type card selects, flourishes, and advances directly
- clicking a school or weapon-style card selects it and plays the flourish
- clicking Back returns to the previous screen without clearing selections

### Keyboard and controller

- directional navigation follows visible card order
- focus is always visible
- Submit on a focused reward-type card selects and advances
- Submit on a focused school or weapon-style card selects it
- footer actions enter the same focus order after the card collection
- Cancel/Back returns to the previous screen after screen 1
- Cancel/Back on screen 1 does not discard the unresolved reward

The flow requires a later host-level ruling before screen 1 can be closed,
deferred, or canceled. Until then, it has no close affordance and never
silently consumes the reward.

### Motion safety

- no screen flash, camera shake, or aggressive luminance change
- hover and selection motion affects only the relevant card
- a future reduced-motion setting may replace lift/scale with immediate edge
  and marker changes without altering the interaction states

## Presentation option contract

The future UI consumes a host-provided collection of presentation options.
This is a conceptual UI contract, not a new server schema:

```text
RewardOption
  stable_id
  display_name
  image
  sort_order
  optional_supporting_text
```

The host supplies only options that are valid for the current reward. The UI:

- sorts by `sort_order`, with `stable_id` as a deterministic tie-breaker
- uses `stable_id` for selection identity
- never derives spell school solely from damage type or VFX school
- never derives weapon style from card position
- never assumes a fixed option count
- samples uniformly from the fully filtered option collection

An eligible spell school must have at least one eligible spell behind it when
the downstream spell screen is eventually defined. How that relationship is
authored is intentionally deferred; the current spell catalog, damage types,
and VFX school assets do not yet form one authoritative player-facing school
catalog.

## State owned by this flow

During one active reward interaction, presentation state contains:

- current screen
- selected reward type, if any
- selected spell school, if any
- selected weapon style, if any
- focused option/action
- scroll position per option screen

This state is provisional. It does not teach an ability or mutate progression.
The later final-confirmation design will define the point at which a choice
becomes authoritative.

## Acceptance criteria

- The opening screen offers exactly Learn a Spell and Learn a Weapon
  Technique.
- The spell branch selects a player-facing school.
- Shadow can appear as a player-facing school.
- Earth is not hardcoded or inferred from Stonespire.
- The weapon branch offers Daggers, Greatsword, Sword & Shield, Staff, and Bow.
- No combat-discipline name appears on the option cards.
- School and weapon cards come from collections rather than fixed layout
  positions.
- Future school counts reflow and scroll without redesigning the shell.
- Every eligible option has probability `1 / N` on every Randomize press.
- Randomize may validly repeat the current selection.
- A repeated selection visibly acknowledges the input.
- Randomize never advances or confirms.
- Hover/focus and selected states are visually distinct.
- Selection persists after hover leaves and while navigating backward/forward.
- No Continue action appears on these screens.
- Reward-type selection visibly flourishes and reveals the chosen branch.
- No checkmark or supporting subtitle appears on a choice card.
- The flow has no path that silently commits or discards the reward.
- The visual direction uses floating medieval parchment choices, restrained
  ornament, and gameplay-color rules without a surrounding modal frame.

## Deferred decisions

The following require separate owner-approved design work:

- the authoritative spell-school authoring source
- the launch school roster beyond the current direction
- handling for unclassified spells
- exact spell and technique option screens
- duplicate and already-known rewards
- technique availability by owned/equipped weapon
- final confirmation and reward commitment
- trigger, pause, multiplayer, reconnect, and deferral behavior
- production-ready card illustrations
- sound, haptics, and reduced-motion implementation
- UI Toolkit translation and implementation plan

## Repository grounding

- `docs/ui-art-direction.md` establishes the typography, palette,
  gameplay-color use, and combat-discipline language. Owner feedback for this
  flow deliberately replaces the shared Hero Frame with floating parchment
  choices.
- `docs/ui-toolkit-workflow.md` makes UI Toolkit the default for new
  screen-space UI and the web prototype the visual source of truth.
- `server/src/progression_catalog.shared.json` currently separates combat
  profiles (weapon labels) from named combat disciplines.
- `Assets/Arena/Editor/SpellPresentation/SchoolVfxSets/` contains visual-school
  assets, but those assets are not treated as the permanent player-facing
  reward-school catalog.
