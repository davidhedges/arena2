# UI Toolkit Workflow & Policy

Owner decisions (2026-07-15/16): new UI defaults to **UI Toolkit**; the visual
source of truth is the **web prototype**, iterated in a browser, then translated
to UXML/USS; the look follows **"Arena UI Art Direction & Design System" (v2)**
(PDF + master style sheet at the repo root) — forged iron/steel, aged leather,
brass/ember gold, serif small-caps, restrained ornament.

## Technology policy

- **UI Toolkit (default):** every new screen-space surface — windows, menus,
  HUD chrome, tooltips, settings, inventory, scoreboards.
- **uGUI/TMP (only where clearly better):**
  - World-space, per-entity UI at scale: nameplates, overhead cast/health bars,
    floating combat text (FCT should be pooled TMP, arguably not "UI" at all).
  - Elements needing per-element custom shaders/materials (dissolve, distortion,
    shader-driven cooldown sweeps) — UI Toolkit has no per-element material.
  - UI that must interleave with world rendering/particles or receive scene
    lighting.
  - Existing stable uGUI surfaces: leave them; migrate only when a screen is
    being substantially reworked anyway (System Menu was the first).
- The legacy uGUI kit (`ArenaUiKit`/`ArenaUiTheme`, Canon* palette) is a
  separate older system; do not extend it for new screens.

## Pipeline (prototype → spec → translation → controller)

1. **Tokens** — `ops/gen_ui_tokens.py` emits the v2 palette/type/metrics as
   identical custom-property blocks: `docs/ui-prototypes/tokens.css` and
   `Assets/Arena/Resources/UI/Toolkit/tokens.uss` (loaded panel-wide via
   `ArenaTheme.tss`). Edit the token table in the script, never the outputs.
2. **Theme art** — `ops/slice_ui_style_sheet.py` carves the modular forged
   pieces out of the authored master style sheet (repo-root PNG) into
   `Assets/Arena/Content/UI/Art/`: window corner + rails, title/header plates,
   button plates per state (labels inpainted out), divider, section rule,
   leather fill. THE STYLE SHEET IS THE ASSET SOURCE — never synthesize
   placeholder art procedurally; if a piece is missing, the owner generates a
   new sheet revision and the slicer gains a cut. Re-run after any sheet
   update.
3. **Prototype** — `docs/ui-prototypes/<screen>/` holds `index.html`
   (scaffold: 1920×1080 stage scaled to fit, fonts, throwaway JS) and
   `<screen>.css` (THE SPEC — must pass the dialect lint). Iterate with
   headless Chrome screenshots (`?open`-style URL params drive states).
   Publish for review: `ops/bundle_ui_prototype.py <dir> --out <file>` then
   host as a Claude artifact.
4. **Lint** — `ops/uss_dialect_lint.py` holds prototype CSS and shipped USS to
   the USS-translatable dialect: flexbox only (no grid/gap/float), px/% only,
   no z-index (paint order = document order), no box-shadow/filters/
   line-height, limited selectors. `.css` files get a small scaffolding
   allowance where a manual mapping exists.
5. **Translate (one-way)** — UXML/USS live in
   `Assets/Arena/Resources/UI/Toolkit/`. Mechanical mappings:
   | prototype CSS | USS |
   |---|---|
   | `font-family` / `font-weight` | `-unity-font-definition` (Cinzel display, Alegreya body — `Assets/Arena/Content/UI/Fonts/`) |
   | `text-align` | `-unity-text-align` |
   | `pointer-events: none` | `picking-mode="Ignore"` in UXML |
   | `border-image` 9-slice | `background-image` + `-unity-slice-*` |
   Once translated, USS is the source of truth for tweaks; the prototype is a
   historical spec — never round-trip.
6. **Binding contract** — prototype `id` == UXML `name` == what the controller
   `Q`s; interactive states are classes (`is-open`, `is-selected`) toggled by
   prototype JS and by the C# controller identically.
7. **Controller** — thin MonoBehaviour: `ArenaPanel.CreateDocument(...)`
   (PanelSettings mirroring the uGUI scaler: 1920×1080, match 0.5, ArenaTheme),
   `Q` by name, wire callbacks, toggle classes, persist settings. No layout or
   styling from C#. Self-bootstraps via `RuntimeInitializeOnLoadMethod` gated
   by `ArenaRuntimeSceneGate`, same as the uGUI surfaces.
8. **Headless preview** — `ops/ui-preview.py` renders any UXML to PNG through
   a throwaway Unity project in `.ui-preview/` (git-ignored), so it works while
   the main editor holds the project lock. `--classes "Name:class,..."` drives
   states (System Menu needs `SystemMenu:is-open,Window:is-open`).

## Hard-won constraints

- **No runtime 9-slicing of textured plates** (browser `border-image` or USS
  `-unity-slice-*`): each slice zone resamples independently, drawing visible
  seam lines through painterly art. Buttons/plates ship **pre-baked at their
  exact display sizes** (`PLATE_BAKES` table in `ops/slice_ui_style_sheet.py`;
  caps scaled uniformly, center stretched once, joints cross-faded at 2x). A
  new button width = one new entry in that table. Runtime slicing remains fine
  for untextured/blurry images (window_shadow).
- **No runtime stretching of textured rails either** — stretch smears the
  steel grain into streaks (the QUIT GAME "bottom rail" bug). Rails are baked
  at exact display length by mirror-bounce TILING (`bake_rail`), cut from a
  calm span (rivets fine; keep medallions/ornaments out of tiled sources per
  "stretchable surfaces remain visually calm").
- **Transparent pixels must be premultiply-safe**: Unity's bilinear filtering
  bleeds the RGB of alpha-0 texels across edges (white fringes the browser
  never shows). Any keyed/faded pixel gets its RGB zeroed/scaled too.

- **No z-index anywhere**: popups/menus must open in a direction that only
  covers elements painted earlier (the quality dropdown opens upward), or use
  a top-level overlay layer.
- **Sorting across systems**: UITK `PanelSettings.sortingOrder` draws from the
  same allocation pool as uGUI canvases (`RuntimeUiLayer.NextSortingOrder()`),
  but real interleaving between UIDocument panels and Screen-Space-Overlay
  canvases must be verified in game — first owner playtest item.
- **Variable fonts**: Cinzel/Alegreya ship as variable TTFs; Unity uses the
  default instance. Don't rely on `font-weight` for emphasis in specs — use
  color/size/letter-spacing (browser prototypes must follow this too).
- **Dropdown/tooltip content built by controllers** must use the same classes
  the spec defines (`dropdown-item`, `is-selected`).
