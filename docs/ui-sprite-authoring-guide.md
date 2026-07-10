# UI Sprite Authoring Guide (2026-07-10)

The menu/interface kit (`Assets/Arena/Runtime/UI/Kit/`) renders every window,
button, tooltip, and slot from a small set of surface sprites. Each surface has
a **procedural fallback** (generated at runtime) and an **authored override**:
drop a PNG into `Assets/Arena/Resources/UI/Kit/` with the exact filename below
and the kit swaps it in everywhere on next play. Files are independent — ship
them one at a time.

**Import:** just drop the PNG in that folder. Texture Type "Sprite (2D and UI)"
(Unity's default for UI-sized PNGs is fine either way — the kit loads the
texture directly). Do **not** configure 9-slice borders in the importer; the
kit defines them in code (`ArenaUiSprites.AuthoredBorders`).

**Global art direction for prompts:** dark fantasy; charcoal `#0E0F11`
surfaces; ember-gold `#EBB840` accents; red reserved for danger. PNG with
transparent background where noted. No baked drop shadows (the kit adds its
own). Keep edge rails straight/axis-aligned — edge regions stretch.

| File | Canvas | Slicing (code-defined) | What to ask for |
|---|---|---|---|
| `window_fill.png` | 512×512, opaque | 48px border | Dark panel backdrop texture (aged parchment-over-steel / dark leather). Subtle vignette allowed in the outer 48px only; the center stretches, so keep it flat and even. Used by every window + tooltip. |
| `window_frame.png` | 512×512, transparent center | 64px border | Ornate frame ring (forged metal / gilded trim). All ornament inside the outer 64px. Corners can be elaborate (they never stretch); edge rails must be straight repeating trim. |
| `header_plate.png` | 512×96, opaque or alpha edges | 24px border | Title band plate for window headers/footers — darker inset metal or embossed leather. End-cap detail within 24px of each edge; center stretches. |
| `button.png` | 256×80, opaque | 16px border | **Neutral desaturated** button plate (gray steel bevel) — the theme tints it per style (gold primary, red danger), so author it near-grayscale, mid-brightness. Bevel within the outer 16px. |
| `button_glow.png` | 256×80, transparent center | 16px border | Soft luminous border ring matching the button silhouette, white/near-white (tinted at runtime). Fades in on hover. |
| `divider.png` | 512×24, transparent | none (stretched whole) | Horizontal flourish under section headings — center ornament with rails fading to transparent at the ends. |

## Slot grids (connected slots — shared thin borders)

Design rule (owner, 2026-07-10): adjacent slots share ONE thin border; only the
grid's outer perimeter gets the thick rim. That means **no per-slot frame
sprite**. Borders are drawn once by the grid renderer, which composes three
pieces per container (any shape — a cell with neighbors on all four sides gets
four thin edges; an isolated slot, e.g. an equipment-doll piece, gets rim on
all four sides):

| File | Canvas | Slicing | What to ask for |
|---|---|---|---|
| `slot_well.png` | 256×256, opaque | none (stretched whole) | The recessed cell interior ONLY — dark textured well with a soft inner shadow. **Absolutely no border/frame**; edges must run clean to the canvas edge (rim/lattice overlay the seams). |
| `grid_rim.png` | 512×512, transparent center | 32px border | The thick outer plate frame around a whole grid block (the mock's outer edge). Also frames isolated single slots, so it must read well when shrunk to one 68px cell. Straight edge rails; corners never stretch. |
| `grid_line.png` | 128×16, opaque or alpha ends | none (stretched along length) | The thin internal lattice strip — one shared border between two adjacent slots. Drawn horizontally and rotated 90° for verticals, so it must be symmetric along its length. |
| `slot_rarity_glow.png` *(optional)* | 256×256, transparent | none | Soft radial glow, white/near-white — tinted per item rarity and drawn inside the well behind the icon. Replaces border-tinting for rarity, since shared borders can't belong to one item. |

**Consistency trick:** don't generate the three pieces separately — ChatGPT
won't keep them consistent. Generate ONE full plate mock (like the reference
grid already produced: outer rim + thin lattice + wells, ideally 3×3 cells or
larger at 256px per cell) and the pieces get cropped out of it with a script.
Hand the plate PNG over and the crop + import can be automated.

**Status:** the connected-grid renderer in the kit is designed but not yet
built (waiting on the art + a go). Until then, cells render standalone frames
borrowed from `UI/ActionBar/slot.png`. The same connected treatment applies to
the action bar rows later, during the HUD pass. Rarity feedback moves from
border tint to the well glow when the switch happens.

## Tinting rules (matters for prompting)

- `window_fill`, `window_frame`, `header_plate`, `divider`: drawn **as authored**
  (no tint) — paint them in final colors.
- `button`: **always tinted** by the theme per state — author neutral/desaturated.
- `button_glow`: tinted (white for filled styles, gold for neutral styles).
- `slot_frame`: tinted by item rarity (white = as authored for common/empty).

## How the existing good-looking pieces work (for reference)

- Action bar: `ActionBarSlot.prefab` draws `Resources/UI/ActionBar/slot.png`
  whole (no slicing) over each 68px cell; painted ability/item icons render in
  `Resources/UI/AbilityIcons/...` / `ItemIcons/...` via the icon resolvers.
- Unit frame: single pre-composed painting (`UI/UnitFrame/UnitFrame.png`,
  720×224 shown at 360×112) with bar fills placed at hand-measured pixel rects
  in `HUDController`. Fixed-size compositions like this don't need slicing —
  that technique stays right for the upcoming unit-frame work.

## Later candidates (not wired yet)

Scrollbar handle, toggle/checkbox faces, dropdown chevron (when the settings
menu grows), cast-bar frame (HUD pass), decorative corner ornaments for the
character window showcase.
