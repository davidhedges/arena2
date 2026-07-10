# Arena UI Art Direction & Design System

Version 2 — Design System Foundation (owner, 2026-07-10). Canonical reference
for all menu/interface art. The practical sprite pipeline that implements this
lives in `ui-sprite-authoring-guide.md`.

## Vision

The UI should feel like it belongs inside the world. Every panel, button,
action bar, and tooltip should read as forged by master craftsmen — not
rendered by software. The interface is an extension of the player's equipment
and discipline.

Identity: dark fantasy, rugged, handcrafted, practical, readable, timeless.
Avoid excessive ornamentation, glowing magic, or gothic excess. The interface
supports gameplay rather than competing with it.

## Core Principles

1. **Readability first.** Arena is an action game; combat readability beats
   decoration. Hierarchy draws attention to gameplay, loot, abilities, health,
   resources. The UI frames information — it is not the information.
2. **Crafted, not manufactured.** Materials: forged iron, tempered steel,
   bronze, brass, worn leather, dark wood (sparingly). Surfaces carry subtle
   hammer marks, scratches, softened edges, worn corners, age. Maintained —
   not pristine, not ruined.
3. **Restrained ornament.** Decoration lives in corners, end caps, medallions,
   brackets, decorative plates. Stretchable surfaces stay visually calm. No
   oversized runes, giant gems, spikes, excessive filigree, or dramatic
   magical effects.
4. **Cohesive materials.** Palette: charcoal, forged steel, gunmetal, brass,
   ember gold. Gameplay colors are reserved for gameplay: red = danger,
   green = poison/healing, blue = frost/mana, purple = arcane,
   orange = legendary. Color communicates mechanics, not decoration.

**One blacksmith:** every component looks made by the same guild. Different
components are different tools, not different themes. A tooltip should not
look identical to an inventory window, but both clearly belong to one world.

## Frame Hierarchy

- **Hero Frame** — major windows (Inventory, Character, Spellbook, Merchant,
  Crafting, Talents). Substantial, forged, architectural, visually important.
  Establishes Arena's identity.
- **Standard Frame** — panels within larger windows (equipment panel,
  statistics, inventory sections, minimap container, spell lists). Thinner
  rails, lighter construction, fewer decorative elements. Related to the Hero
  Frame without competing with it.
- **Tooltip Frame** — information (item tooltips, spell descriptions, NPC
  dialogue, floating panels). Thin, restrained, elegant, almost invisible.

## Modular Construction

The UI is assembled from reusable components, not large finished textures.
Foundation library: Hero corner, Hero horizontal rail, Hero vertical rail,
window fill, header plate, button, divider. Larger windows are compositions of
these elements. New UI is assembled — not redrawn.

- **Window fill:** dark leather, subtle steel beneath, gentle wear, low visual
  noise, calm center (the center stretches).
- **Header plate:** a defining Arena motif — embossed leather, forged steel,
  brass trim, symmetrical. Nearly every major window uses it.
- **Buttons:** physical — forged steel, tactile bevels, restrained wear.
  Authored artwork stays largely neutral; gameplay states come from runtime
  tinting.

## Inventory System

Crafted storage rather than floating boxes. Connected grid = one forged
object: shared lattice, heavy outer rim, recessed wells. Item rarity is an
interior glow, not colored borders.

## Discipline Language

Arena has combat disciplines, not classes. The UI subtly acknowledges the
current discipline through craftsmanship, not color; only a small percentage
of the UI changes (action bar end caps, decorative brackets, engraved
medallions, corner motifs). Layout never changes.

- **Greatsword:** heavy forged plates, broad geometry, crossguard-inspired
  forms, weight.
- **Daggers:** blackened steel, leather wrapping, slim proportions, hooked
  details, precision.
- **Staff:** brass fittings, engraved rings, contained arcane motifs, ancient
  craftsmanship; magic restrained rather than explosive.
- **Sword & Shield:** layered steel, shield geometry, military craftsmanship,
  balanced symmetry.
- **Bow:** iron bound to ash wood, leather bindings, subtle recurved forms,
  natural craftsmanship.

Future disciplines introduce new craftsmanship languages, not new UI themes.

## Variants

Variants are expected (Hero Frame Heavy/Light, Wide/Narrow Header Plate,
Large/Small Inventory Rim). They inherit the same design language —
evolutions, not replacements.

## Responsiveness

Corners never stretch; rails repeat or tile cleanly; centers stay visually
calm; ornament lives outside stretch regions; layouts adapt independently from
decoration. Artwork supports 1280×720 through 4K without losing readability.

## Emotional Goal

"I am carrying finely crafted equipment into a dangerous place" — not "I am
operating fantasy software." The UI quietly reinforces the fantasy through
materials, craftsmanship, and restraint.
