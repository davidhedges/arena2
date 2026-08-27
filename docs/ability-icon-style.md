# Ability Icon Style

## Canonical visual prompt

This prompt is the sole visual style authority for generated ability and spell
icons. Use it verbatim, adding only a short description that identifies the
subject for the specific ability:

> One iconic fantasy object or magical effect, close-up composition, filling
> nearly the entire square frame, painterly Blizzard-style fantasy
> illustration, dramatic directional lighting, high contrast, bold silhouette,
> vibrant saturated colors, dynamic diagonal composition, visible brush
> strokes, exaggerated materials, dark abstract background, glowing magical
> energy, crisp focal point, readable at small icon size, minimal clutter,
> heroic fantasy ability icon.

Do not add a second visual style contract around this prompt. In particular,
do not prescribe a fixed diagonal direction, fixed light direction, mandatory
school palette, exact background recipe, or mandatory white-hot focal point.
Those choices belong to each generated image as long as it satisfies the
canonical prompt.

## Technical contract

- Deliver each selected icon as a **128×128 PNG**. Ability/action-bar slots are
  currently displayed at 68×68.
- File path:
  `Assets/Arena/Resources/UI/AbilityIcons/<KIND>/<ID>.png`, using an uppercase
  id with `_` separators. The client resolves icons by this convention through
  `ActionIconResolver` and `Resources.Load`; there is no icon field in the
  catalog.
- Preserve the Unity `.meta` file for an existing icon. New `.meta` files use
  the deterministic GUID scheme in `ops/import_generated_ability_icons.py`:
  `md5("arena2-ability-icon:" + relpath)`.

## Repository reality

- Runtime icons are always individual sprites, not sprite sheets.
  `ActionIconResolver` loads each one with
  `Resources.Load<Sprite>("UI/AbilityIcons/<KIND>/<ID>")`.
- The files under
  `Assets/Arena/Content/Art/UI/AbilityIconSheets/` are legacy authoring sheets.
  `ops/import_generated_ability_icons.py` crops their manifest cells into
  individual 128×128 runtime PNGs. Those sheets contain the previous bordered
  style and are not visual references for this style.
- Do not generate new-style icons as a sheet and do not run the legacy sheet
  importer to create or replace them. Generate one independent square image
  per ability and save the selected result directly at its runtime path.
- Existing legacy icons may remain until they are deliberately regenerated.
  Do not use their borders, compositions, or palettes as additional prompt
  guidance; the canonical visual prompt above remains the sole style source.

## Choosing the icon set

- Read the requested IDs and display names from the current
  `server/src/progression_catalog.shared.json`; do not rely on a copied list in
  this document.
- For the **J → Spells** section specifically, select `abilities` whose
  `actor_scope` is `PLAYER` or `BOTH`, whose `selection_kind` is `ACTIVE`, and
  whose `gameplay.kind` is `SPELL`. Use each row's `ability_id` for the filename
  and its display name/gameplay meaning to choose the single icon subject.
- Generate each distinct ability with a separate image-generation call. Add
  only a concise subject description to the canonical prompt; do not introduce
  a second style prompt or batch multiple icons into one image.

## Spell-icon workflow

- `ABILITY/SPELL_*` icons are externally generated artwork. Generate from the
  canonical prompt plus the spell's subject, choose the strongest result, and
  downscale the selected square source to 128×128 with a high-quality filter.
- Save or copy every selected final into the workspace. Do not leave a
  project-referenced icon only in an image generator's output directory.
- Review the final 128×128 image at its in-game 68×68 display size before
  accepting it.
- The committed PNG and `.meta` file are the source of truth. There is no
  procedural spell-icon generator.
- Non-spell icons retain their existing workflows.

## Acceptance checklist

- [ ] Exactly one iconic object or magical effect dominates the frame.
- [ ] Close-up square composition with a bold, immediately readable silhouette.
- [ ] Painterly fantasy treatment, dramatic lighting, high contrast, saturated
      color, visible brushwork, and exaggerated materials.
- [ ] Dynamic diagonal composition against a dark abstract background.
- [ ] Glowing magical energy and one crisp focal point without unnecessary
      clutter.
- [ ] Still readable when previewed at 68×68.
- [ ] 128×128 PNG at the convention path with its Unity `.meta` file.
- [ ] Expected catalog IDs, PNG filenames, and `.meta` files match one-for-one.
