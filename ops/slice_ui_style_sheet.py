#!/usr/bin/env python3
"""Slice the authored UI master style sheet into game-ready theme art.

The visual source of truth is the owner-provided style sheet
(docs/ui-design/ui-master-style-sheet.png) — painterly forged components.
This script carves the reusable pieces out of it:

  - flood-keys the near-black sheet background to alpha around silhouettes
  - removes baked-in labels (BUTTON/DANGER/PRIMARY) by rebuilding each plate
    as [left cap | clean center strip | right cap], which is exactly what a
    9-slice stretches anyway
  - cuts one authored window corner + rail; the UI mirrors them per side
  - builds a tileable leather fill from a clean interior strip
  - renders the soft window drop shadow (the only synthesized piece)

Outputs -> Assets/Arena/Content/UI/Art/ (same filenames the USS/prototype use).
Re-run after replacing the style sheet with a new revision:

  python3 ops/slice_ui_style_sheet.py
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter

REPO = Path(__file__).resolve().parent.parent
SHEET = REPO / "docs/ui-design/ui-master-style-sheet.png"
OUT = REPO / "Assets/Arena/Content/UI/Art"


def flood_key(image: Image.Image, tolerance: int = 26) -> Image.Image:
    """Set background to transparent by flood-filling from the four corners."""
    rgba = image.convert("RGBA")
    w, h = rgba.size
    pixels = rgba.load()
    seeds = [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]
    visited = bytearray(w * h)
    for seed in seeds:
        base = pixels[seed[0], seed[1]]
        stack = [seed]
        while stack:
            x, y = stack.pop()
            index = y * w + x
            if visited[index]:
                continue
            visited[index] = 1
            r, g, b, a = pixels[x, y]
            if abs(r - base[0]) + abs(g - base[1]) + abs(b - base[2]) > tolerance * 3:
                continue
            # Zero RGB too: Unity's bilinear filtering bleeds the RGB of
            # transparent pixels across the alpha edge (white fringes).
            pixels[x, y] = (0, 0, 0, 0)
            for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                if 0 <= nx < w and 0 <= ny < h and not visited[ny * w + nx]:
                    stack.append((nx, ny))
    return rgba


def save(image: Image.Image, name: str) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    image.save(OUT / name)
    print(f"wrote {name}  {image.size[0]}x{image.size[1]}")


def cut(box: tuple, key: bool = True, tolerance: int = 26) -> Image.Image:
    piece = SHEET_IMAGE.crop(box)
    return flood_key(piece, tolerance) if key else piece.convert("RGBA")


def tight_cut(box: tuple, scan_pad: int = 4, threshold: int = 40) -> Image.Image:
    """Cut with extra margin, then trim to the component silhouette + 1px.

    Guarantees the full outline is captured and the margins are symmetric, so
    the art's visual center matches the element center and no half-clipped
    bright rim pixels ride the image edge.
    """
    x0, y0, x1, y1 = box
    piece = SHEET_IMAGE.crop((x0 - scan_pad, y0 - scan_pad, x1 + scan_pad, y1 + scan_pad)).convert("RGBA")
    w, h = piece.size
    pixels = piece.load()

    def bright(x: int, y: int) -> bool:
        r, g, b, a = pixels[x, y]
        return 0.299 * r + 0.587 * g + 0.114 * b > threshold

    rows = [y for y in range(h) if any(bright(x, y) for x in range(0, w, 2))]
    cols = [x for x in range(w) if any(bright(x, y) for y in range(0, h, 2))]
    if not rows or not cols:
        return piece
    return piece.crop((max(0, cols[0] - 1), max(0, rows[0] - 1),
                       min(w, cols[-1] + 2), min(h, rows[-1] + 2)))


def erase_label_h(plate: Image.Image, frame_x: int = 15,
                  text_span: tuple = (0.24, 0.78), text_band: tuple = (0.24, 0.80)) -> Image.Image:
    """Remove a baked-in label by same-row horizontal fill (wide plates).

    For every row in the text band, the clean face segment between the frame
    and the text start is mirror-tiled across the text zone. Copying within
    the row preserves all horizontal features (frame rules, vignette) exactly.
    """
    w, h = plate.size
    y0, y1 = round(h * text_band[0]), round(h * text_band[1])
    text_left, text_right = round(w * text_span[0]), round(w * text_span[1])
    source_width = max(4, text_left - frame_x - 2)
    pixels = plate.load()
    for y in range(y0, y1):
        for step, x in enumerate(range(text_left, text_right)):
            cycle, offset = divmod(step, source_width)
            sx = frame_x + (offset if cycle % 2 == 0 else source_width - 1 - offset)
            pixels[x, y] = pixels[sx, y]
    return feather_seams(plate, rows=(y0, y1), cols=(text_left, text_right),
                         x_range=(text_left, text_right), y_range=(y0, y1))


def erase_label_inpaint(plate: Image.Image, luma_delta: int = 36,
                        face: tuple = (0.12, 0.22, 0.88, 0.84)) -> Image.Image:
    """Remove a baked-in label by masking bright glyph pixels and diffusing
    the surrounding face into them (narrow plates, where no clean fill band
    exists). Glyphs are cream-on-dark, so luminance separates them from any
    face color; the mask is dilated to catch anti-aliased halos.
    """
    w, h = plate.size
    x0, y0 = round(w * face[0]), round(h * face[1])
    x1, y1 = round(w * face[2]), round(h * face[3])
    pixels = plate.load()

    def luma(p: tuple) -> float:
        return 0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]

    samples = sorted(luma(pixels[x, y]) for y in range(y0, y1, 2) for x in range(x0, x1, 2))
    threshold = samples[len(samples) // 2] + luma_delta

    masked: set = set()
    for y in range(y0, y1):
        for x in range(x0, x1):
            if luma(pixels[x, y]) > threshold:
                masked.add((x, y))
    for _ in range(2):  # dilate to catch anti-aliased edges
        grown = set(masked)
        for x, y in masked:
            for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
                if x0 <= nx < x1 and y0 <= ny < y1:
                    grown.add((nx, ny))
        masked = grown

    remaining = set(masked)
    while remaining:
        filled = []
        for x, y in remaining:
            neighbors = [pixels[nx, ny] for nx, ny in
                         ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1),
                          (x + 1, y + 1), (x - 1, y - 1), (x + 1, y - 1), (x - 1, y + 1))
                         if (nx, ny) not in remaining]
            if neighbors:
                pixels[x, y] = tuple(sum(c[i] for c in neighbors) // len(neighbors) for i in range(4))
                filled.append((x, y))
        if not filled:
            break
        remaining -= set(filled)
    return plate


def feather_seams(image: Image.Image, rows: tuple, cols: tuple,
                  x_range: tuple, y_range: tuple) -> Image.Image:
    """Cross-fade the hard boundaries a fill band leaves behind: each seam
    row/column becomes the average of its two neighbors."""
    pixels = image.load()
    for y in rows:
        for x in range(*x_range):
            above, below = pixels[x, y - 1], pixels[x, y + 1]
            pixels[x, y] = tuple((above[i] + below[i]) // 2 for i in range(4))
    for x in cols:
        for y in range(*y_range):
            left, right = pixels[x - 1, y], pixels[x + 1, y]
            pixels[x, y] = tuple((left[i] + right[i]) // 2 for i in range(4))
    return image


def plate_asset(box: tuple, frame_x: int = 15) -> Image.Image:
    """Cut a labeled plate (silhouette-trimmed, symmetric margins, opaque —
    the 1px margin ring is sheet-dark and vanishes on the leather fill) and
    erase the label. Full-width 9-slice source."""
    return erase_label_h(tight_cut(box), frame_x=frame_x)


def narrow_plate_asset(box: tuple, **kwargs) -> Image.Image:
    """Same as plate_asset, but inpaint-based label removal for narrow plates."""
    return erase_label_inpaint(tight_cut(box), **kwargs)


def fade_edges_h(image: Image.Image, fade: int = 10) -> Image.Image:
    """Linear alpha ramp on the left/right edges so a rail-riding cut blends
    into the stretched rail behind it."""
    w, h = image.size
    pixels = image.load()
    for dx in range(fade):
        factor = dx / fade
        for y in range(h):
            for x in (dx, w - 1 - dx):
                r, g, b, a = pixels[x, y]
                # Premultiply the fade so Unity's filtering can't bleed RGB.
                pixels[x, y] = (round(r * factor), round(g * factor), round(b * factor), round(a * factor))
    return image


def bake_plate(source: Image.Image, width: int, height: int, cap: int) -> Image.Image:
    """Pre-compose a plate at its exact display size (no runtime 9-slice).

    Runtime slicing resamples each of the nine zones independently, which
    draws visible seam lines through textured art. Instead: caps scale
    uniformly, the center strip stretches once, and the joints cross-fade
    over a few pixels — assembled at 2x and downsampled.
    """
    w2, h2 = width * 2, height * 2
    src_w, src_h = source.size
    cap_w = round(cap * (h2 / src_h))
    overlap = 6

    left = source.crop((0, 0, cap, src_h)).resize((cap_w, h2), Image.LANCZOS)
    right = source.crop((src_w - cap, 0, src_w, src_h)).resize((cap_w, h2), Image.LANCZOS)
    center = source.crop((cap, 0, src_w - cap, src_h)).resize(
        (w2 - 2 * cap_w + 2 * overlap, h2), Image.LANCZOS)

    ramp = Image.new("L", (cap_w, h2), 255)
    ramp_px = ramp.load()
    for dx in range(overlap):
        alpha = round(255 * (dx + 1) / (overlap + 1))
        for y in range(h2):
            ramp_px[cap_w - 1 - dx, y] = alpha

    baked = Image.new("RGBA", (w2, h2))
    baked.paste(center, (cap_w - overlap, 0))
    baked.paste(left, (0, 0), ramp)
    baked.paste(right, (w2 - cap_w, 0), ramp.transpose(Image.FLIP_LEFT_RIGHT))
    return baked.resize((width, height), Image.LANCZOS)


# (family base name, cap px in source, [(width, height), ...])
PLATE_BAKES = [
    ("button_plate", 30, [(340, 36), (187, 36)]),
    ("button_plate_hover", 32, [(340, 36), (187, 36)]),
    ("button_plate_pressed", 30, [(340, 36), (187, 36)]),
    ("button_plate_ember", 26, [(340, 36)]),
    ("button_plate_ember_hover", 28, [(340, 36)]),
    # Danger (QUIT) is inset 30px per side so it clears the bottom corner plates.
    ("button_plate_danger", 24, [(280, 36)]),
    ("button_plate_danger_hover", 25, [(280, 36)]),
    ("button_plate_danger_pressed", 24, [(280, 36)]),
]


def bake_rail(source: Image.Image, length: int, thickness: int, vertical: bool = False) -> Image.Image:
    """Pre-compose a frame rail at its exact display length by mirror-bounce
    TILING (never stretching — stretch smears the steel grain into streaks).
    Joints cross-fade; the ends alpha-fade (premultiplied) to tuck under the
    corner pieces. Assembled at 2x and downsampled.
    """
    if vertical:
        source = source.transpose(Image.ROTATE_90)
    length2, thick2 = length * 2, thickness * 2
    src_w, src_h = source.size
    seg_w = max(8, round(src_w * (thick2 / src_h)))
    segment = source.resize((seg_w, thick2), Image.LANCZOS)
    overlap = 8

    ramp = Image.new("L", (seg_w, thick2), 255)
    ramp_px = ramp.load()
    for dx in range(overlap):
        alpha = round(255 * (dx + 1) / (overlap + 1))
        for y in range(thick2):
            ramp_px[dx, y] = alpha

    rail = Image.new("RGBA", (length2, thick2))
    x, flip = 0, False
    while x < length2:
        piece = segment.transpose(Image.FLIP_LEFT_RIGHT) if flip else segment
        rail.paste(piece, (x, 0), ramp if x > 0 else None)
        x += seg_w - overlap
        flip = not flip

    pixels = rail.load()
    for dx in range(12):  # premultiplied end fades under the corners
        factor = dx / 12
        for y in range(thick2):
            for px in (dx, length2 - 1 - dx):
                r, g, b, a = pixels[px, y]
                pixels[px, y] = (round(r * factor), round(g * factor), round(b * factor), round(a * factor))

    rail = rail.resize((length, thickness), Image.LANCZOS)
    return rail.transpose(Image.ROTATE_270) if vertical else rail


def bake_all_plates() -> None:
    for name, cap, sizes in PLATE_BAKES:
        source = Image.open(OUT / f"{name}.png")
        for width, height in sizes:
            save(bake_plate(source, width, height, cap), f"{name}_{width}x{height}.png")


def make_tileable(strip: Image.Image, horizontal: bool, fade: int = 8) -> Image.Image:
    """Wrap cross-fade a strip's ends so background-repeat tiles it seamlessly
    at native scale (runtime REPEAT of texture is safe — runtime STRETCH is not)."""
    if not horizontal:
        return make_tileable(strip.transpose(Image.ROTATE_90), True, fade).transpose(Image.ROTATE_270)
    w, h = strip.size
    out = strip.copy()
    pixels, src = out.load(), strip.load()
    for dx in range(fade):
        t = (dx + 1) / (fade + 1)
        for y in range(h):
            a = src[dx, y]
            b = src[w - fade + dx, y]
            blended = tuple(round(b[i] * (1 - t) + a[i] * t) for i in range(4))
            pixels[dx, y] = blended
            pixels[w - fade + dx, y] = blended
    return out


def gen_inventory_kit() -> None:
    """Connected-slot grid, rarity glows, tooltip + subpanel (Standard) frames,
    close button, demo icons, currency coin."""
    # One lattice pitch, junction-to-junction (measured ~59.5x61), shown at the
    # game's 68px cell. Tiling per-cell reconstructs shared lines + diamonds.
    cell = cut((511, 706, 570, 767), key=False).resize((68, 68), Image.LANCZOS)
    save(cell, "grid_cell.png")

    # Closing lattice lines for the grid's outer edges (the cell tile carries
    # half-lines; interior edges complete via neighbors, outer edges via these).
    # No ornate rim — owner feedback 2026-07-17: no nested corners on the grid.
    line_h = cut((516, 702, 564, 711), key=False).resize((55, 10), Image.LANCZOS)
    save(make_tileable(line_h, horizontal=True), "grid_line_h.png")
    line_v = cut((506, 716, 515, 762), key=False).resize((10, 52), Image.LANCZOS)
    save(make_tileable(line_v, horizontal=False), "grid_line_v.png")

    # Rarity glows: authored well interiors converted to alpha glows
    # (brightness -> alpha, color normalized) so they can overlay item icons —
    # the game's item icons are opaque full-bleed squares, so an under-glow
    # would be invisible. Overlay reads as light on the item.
    glows = {
        "common": (817, 658, 876, 712), "uncommon": (899, 658, 961, 712),
        "rare": (981, 658, 1042, 712), "epic": (817, 737, 876, 799),
        "legendary": (899, 737, 961, 799), "red": (981, 737, 1042, 799),
    }
    for name, box in glows.items():
        well = cut(box, key=False).resize((68, 68), Image.LANCZOS)
        pixels = well.load()
        baseline = sorted(max(pixels[x, y][:3]) for x, y in
                          [(3, 3), (64, 3), (3, 64), (64, 64), (34, 3), (3, 34)])[2]
        for y in range(68):
            for x in range(68):
                r, g, b, _ = pixels[x, y]
                v = max(r, g, b)
                alpha = min(255, max(0, round((v - baseline * 1.35) * 2.2)))
                if v > 0 and alpha > 0:
                    scale = min(255 / v, 2.0)
                    pixels[x, y] = (min(255, round(r * scale)), min(255, round(g * scale)),
                                    min(255, round(b * scale)), alpha)
                else:
                    pixels[x, y] = (0, 0, 0, 0)
        save(well, f"rarity_glow_{name}.png")

    # Tooltip frame (thin, elegant): corners + native-scale repeating edges.
    save(cut((1071, 466, 1097, 492), key=False), "tooltip_corner.png")
    save(make_tileable(cut((1100, 466, 1200, 476), key=False), horizontal=True), "tooltip_edge_h.png")
    save(make_tileable(cut((1071, 495, 1081, 590), key=False), horizontal=False), "tooltip_edge_v.png")

    # Standard (subpanel) frame: gold filigree corner. The authored edges are
    # near-invisible in the sheet; panels use hairline CSS edges instead.
    save(cut((858, 121, 908, 171), key=False), "subpanel_corner.png")

    # (Close button dropped — owner feedback 2026-07-17: no red X plates;
    # screens close via their toggle key / Escape.)

    # Standalone slot box (framed well) for sparse layouts like the paper doll.
    # The sheet only shows these boxes WITH demo icons, so the empty box is
    # synthesized: sword-box frame + a clean well interior from the lattice
    # cell, cross-faded at the joint.
    box = tight_cut((356, 380, 447, 471))
    well = cut((511, 706, 570, 767), key=False).crop((10, 10, 50, 52))
    inset = 9
    interior = well.resize((box.width - 2 * inset, box.height - 2 * inset), Image.LANCZOS)
    mask = Image.new("L", interior.size, 255)
    mask_px = mask.load()
    for d in range(4):
        alpha = round(255 * (d + 1) / 5)
        for x in range(interior.width):
            mask_px[x, d] = min(mask_px[x, d], alpha)
            mask_px[x, interior.height - 1 - d] = min(mask_px[x, interior.height - 1 - d], alpha)
        for y in range(interior.height):
            mask_px[d, y] = min(mask_px[d, y], alpha)
            mask_px[interior.width - 1 - d, y] = min(mask_px[interior.width - 1 - d, y], alpha)
    box.paste(interior, (inset, inset), mask)
    save(box, "slot_box.png")

    # Demo item art (icon + well interior) and the gold coin.
    save(cut((368, 391, 436, 459), key=False), "demo_item_sword.png")
    save(cut((466, 390, 534, 458), key=False), "demo_item_shield.png")
    save(cut((564, 390, 632, 458), key=False), "demo_item_helm.png")
    save(cut((716, 386, 738, 408)), "coin_gold.png")


def gen_leather_tile() -> None:
    # Clean interior strip between the icon row and the CONFIRM plate.
    strip = SHEET_IMAGE.crop((376, 468, 600, 496)).convert("RGBA")
    w, h = strip.size
    tile = Image.new("RGBA", (w, h * 8))
    for i in range(8):
        row = strip if i % 2 == 0 else strip.transpose(Image.FLIP_TOP_BOTTOM)
        tile.paste(row, (0, i * h))
    save(ImageEnhance.Brightness(tile).enhance(1.10), "leather_fill.png")


def gen_window_shadow() -> None:
    size, inset, radius, blur = 160, 44, 10, 16
    image = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((inset, inset, size - inset, size - inset), radius=radius, fill=(0, 0, 0, 255))
    save(image.filter(ImageFilter.GaussianBlur(blur)), "window_shadow.png")


SHEET_IMAGE = Image.open(SHEET)


def main() -> None:
    # Header plate: the empty authored example (top-right of the sheet),
    # lifted slightly so the maroon band reads over the dark top rail.
    header = cut((995, 44, 1534, 129))
    save(ImageEnhance.Brightness(header).enhance(1.16), "header_plate.png")

    # Window title plate: the arrow-ended plate that rides the top rail on the
    # sheet's window. Cut with its rail flanks (they blend into our stretched
    # rail via the edge fade); label inpainted out.
    title_plate = erase_label_inpaint(
        cut((430, 22, 750, 102), key=False), luma_delta=38, face=(0.14, 0.30, 0.88, 0.74))
    save(fade_edges_h(title_plate), "window_title_plate.png")

    # Standard button states (labels removed, 9-slice-ready).
    save(plate_asset((987, 185, 1107, 234)), "button_plate.png")
    save(plate_asset((1116, 181, 1244, 236), frame_x=19), "button_plate_hover.png")
    save(plate_asset((1249, 184, 1375, 235)), "button_plate_pressed.png")
    save(plate_asset((1382, 186, 1505, 233)), "button_plate_disabled.png")

    # Danger family (narrow plates: vertical fill).
    save(narrow_plate_asset((972, 297, 1062, 344)), "button_plate_danger.png")
    save(narrow_plate_asset((1066, 294, 1156, 346), face=(0.14, 0.28, 0.86, 0.80)), "button_plate_danger_hover.png")
    save(narrow_plate_asset((1159, 297, 1249, 344)), "button_plate_danger_pressed.png")

    # Primary family (no authored pressed state; UI falls back to default).
    save(narrow_plate_asset((1260, 295, 1379, 346)), "button_plate_ember.png")
    save(narrow_plate_asset((1386, 292, 1505, 347), face=(0.14, 0.26, 0.82, 0.80)), "button_plate_ember_hover.png")

    # Window frame: authored top-left corner + rail segments, pre-baked at
    # exact display length by tiling (stretch smears the grain — see the
    # QUIT GAME rail bug, 2026-07-16). Window is 400x442, corners inset 52.
    save(cut((279, 32, 370, 122), key=False), "window_corner.png")
    # Calm rail span between the title plate's arrow tail (~773) and the
    # mid-rail diamond (~855): rivets only, per "stretchable surfaces remain
    # visually calm". Lengths = window dimension - 2*52 (corner inset); one
    # entry per window size (System Menu 400x442, Character/Inventory 640-660).
    rail_h = cut((776, 41, 852, 79), key=False)
    rail_v = cut((279, 130, 317, 235), key=False)
    # The window is painted lit from the top-left: right/bottom rails are the
    # AUTHORED shaded rails, never the lit left/top rails mirrored (mirroring
    # flips the highlight to the wrong side — owner-flagged 2026-07-17).
    # Rails ship as native-scale TILEABLE strips (background-repeat) so windows
    # can be any size at runtime; the clipped tile at each end hides under the
    # corner pieces (rails inset 52, corners 56).
    rail_bottom = cut((394, 545, 450, 583), key=False)
    rail_right = cut((909, 130, 947, 235), key=False)

    def rail_strip(source: Image.Image, vertical: bool = False) -> Image.Image:
        if vertical:
            scaled = source.resize((23, round(source.height * (23 / source.width))), Image.LANCZOS)
        else:
            scaled = source.resize((round(source.width * (23 / source.height)), 23), Image.LANCZOS)
        return make_tileable(scaled, horizontal=not vertical)

    save(rail_strip(rail_h), "window_rail_top.png")
    save(rail_strip(rail_bottom), "window_rail_bottom.png")
    save(rail_strip(rail_v, vertical=True), "window_rail_left.png")
    save(rail_strip(rail_right, vertical=True), "window_rail_right.png")

    # Ornaments.
    save(cut((977, 396, 1531, 429)), "divider.png")
    save(cut((340, 151, 680, 167), tolerance=20), "section_rule.png")

    bake_all_plates()
    gen_inventory_kit()
    gen_leather_tile()
    gen_window_shadow()

    # Reference swatches for token tuning (printed, not saved).
    for label, xy in [("title-gold", (585, 68)), ("section-gold", (400, 128)),
                      ("body-text", (400, 205)), ("leather", (450, 480))]:
        print(f"sample {label}: #%02X%02X%02X" % SHEET_IMAGE.getpixel(xy)[:3])


if __name__ == "__main__":
    main()
