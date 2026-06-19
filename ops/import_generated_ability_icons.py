#!/usr/bin/env python3
"""Import generated ability icon sheets into Unity Resources sprites."""

from __future__ import annotations

import hashlib
import json
import os
import struct
import zlib
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "Assets/Arena/Content/Art/UI/AbilityIconSheets/ability_icon_sheet_manifest.json"
CATALOG = ROOT / "server/src/progression_catalog.shared.json"
ICON_SIZE = 128


def guid_for(path: Path) -> str:
    rel = path.relative_to(ROOT).as_posix()
    return hashlib.md5(f"arena2-ability-icon:{rel}".encode("utf-8")).hexdigest()


def read_png(path: Path) -> tuple[int, int, bytearray]:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} is not a PNG")

    pos = 8
    width = height = 0
    color_type = -1
    bit_depth = -1
    interlace = -1
    idat = bytearray()

    while pos < len(data):
        length = struct.unpack(">I", data[pos : pos + 4])[0]
        kind = data[pos + 4 : pos + 8]
        payload = data[pos + 8 : pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            width, height, bit_depth, color_type, _, _, interlace = struct.unpack(">IIBBBBB", payload)
        elif kind == b"IDAT":
            idat.extend(payload)
        elif kind == b"IEND":
            break

    if bit_depth != 8 or color_type not in (2, 6) or interlace != 0:
        raise ValueError(f"{path} must be an 8-bit non-interlaced RGB/RGBA PNG")

    channels = 4 if color_type == 6 else 3
    stride = width * channels
    raw = zlib.decompress(bytes(idat))
    rows: list[bytearray] = []
    i = 0
    previous = bytearray(stride)
    bpp = channels
    for _ in range(height):
        filter_type = raw[i]
        i += 1
        row = bytearray(raw[i : i + stride])
        i += stride
        for x in range(stride):
            left = row[x - bpp] if x >= bpp else 0
            up = previous[x]
            up_left = previous[x - bpp] if x >= bpp else 0
            if filter_type == 1:
                row[x] = (row[x] + left) & 0xFF
            elif filter_type == 2:
                row[x] = (row[x] + up) & 0xFF
            elif filter_type == 3:
                row[x] = (row[x] + ((left + up) >> 1)) & 0xFF
            elif filter_type == 4:
                row[x] = (row[x] + paeth(left, up, up_left)) & 0xFF
            elif filter_type != 0:
                raise ValueError(f"Unsupported PNG filter {filter_type} in {path}")
        rows.append(row)
        previous = row

    rgba = bytearray(width * height * 4)
    for y, row in enumerate(rows):
        for x in range(width):
            src = x * channels
            dst = (y * width + x) * 4
            rgba[dst] = row[src]
            rgba[dst + 1] = row[src + 1]
            rgba[dst + 2] = row[src + 2]
            rgba[dst + 3] = row[src + 3] if channels == 4 else 255
    return width, height, rgba


def paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa = abs(p - a)
    pb = abs(p - b)
    pc = abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def write_png(path: Path, width: int, height: int, rgba: bytes | bytearray) -> None:
    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    rows = bytearray()
    stride = width * 4
    for y in range(height):
        rows.append(0)
        rows.extend(rgba[y * stride : (y + 1) * stride])
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(rows), 9))
        + chunk(b"IEND", b"")
    )


def crop_resize(
    src_width: int,
    src_height: int,
    src: bytearray,
    x0: int,
    y0: int,
    x1: int,
    y1: int,
    size: int,
) -> bytearray:
    out = bytearray(size * size * 4)
    crop_w = max(1, x1 - x0)
    crop_h = max(1, y1 - y0)
    for y in range(size):
        sy = min(src_height - 1, y0 + int((y + 0.5) * crop_h / size))
        for x in range(size):
            sx = min(src_width - 1, x0 + int((x + 0.5) * crop_w / size))
            src_idx = (sy * src_width + sx) * 4
            dst_idx = (y * size + x) * 4
            out[dst_idx : dst_idx + 4] = src[src_idx : src_idx + 4]
    return out


def folder_meta(path: Path) -> str:
    return f"""fileFormatVersion: 2
guid: {guid_for(path)}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def texture_meta(path: Path, *, sprite: bool) -> str:
    guid = guid_for(path)
    texture_type = 8 if sprite else 0
    sprite_mode = 1 if sprite else 0
    alpha_is_transparency = 1 if sprite else 0
    return f"""fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 11
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: {sprite_mode}
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: {alpha_is_transparency}
  spriteTessellationDetail: -1
  textureType: {texture_type}
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  platformSettings:
  - serializedVersion: 3
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: 4
    textureCompression: 0
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID: 5{guid[:31]}
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
  spritePackingTag:
  pSDRemoveMatte: 0
  pSDShowRemoveMatteOption: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def default_meta(path: Path) -> str:
    return f"""fileFormatVersion: 2
guid: {guid_for(path)}
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def write_meta(path: Path, content: str) -> None:
    path.with_name(path.name + ".meta").write_text(content, encoding="utf-8")


def ensure_folder(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    write_meta(path, folder_meta(path))


def validate_manifest(manifest: dict[str, Any]) -> None:
    with CATALOG.open("r", encoding="utf-8") as handle:
        catalog = json.load(handle)
    expected = {
        (row["presentation_kind"], row["presentation_id"])
        for row in catalog["action_presentations"]
        if row["presentation_kind"] in {"ABILITY", "FIXED"}
    }
    authored: set[tuple[str, str]] = set()
    for sheet in manifest["sheets"]:
        capacity = int(sheet["columns"]) * int(sheet["rows"])
        if len(sheet["cells"]) > capacity:
            raise ValueError(f"{sheet['source']} has more cells than grid capacity")
        for cell in sheet["cells"]:
            key = (cell["kind"], cell["id"])
            if key in authored:
                raise ValueError(f"Duplicate icon mapping for {key[0]}:{key[1]}")
            authored.add(key)
    missing = sorted(expected - authored)
    if missing:
        raise ValueError("Missing icon mappings: " + ", ".join(f"{kind}:{id_}" for kind, id_ in missing))


def main() -> None:
    with MANIFEST.open("r", encoding="utf-8") as handle:
        manifest = json.load(handle)
    validate_manifest(manifest)

    output_root = ROOT / manifest["output_root"]
    ensure_folder(output_root)
    ensure_folder(output_root / "ABILITY")
    ensure_folder(output_root / "FIXED")
    ensure_folder(MANIFEST.parent)
    ensure_folder(MANIFEST.parent.parent)
    write_meta(MANIFEST, default_meta(MANIFEST))

    generated = 0
    for sheet in manifest["sheets"]:
        source = ROOT / sheet["source"]
        write_meta(source, texture_meta(source, sprite=False))
        src_width, src_height, src = read_png(source)
        columns = int(sheet["columns"])
        rows = int(sheet["rows"])
        for index, cell in enumerate(sheet["cells"]):
            col = index % columns
            row = index // columns
            x0 = round(col * src_width / columns)
            x1 = round((col + 1) * src_width / columns)
            y0 = round(row * src_height / rows)
            y1 = round((row + 1) * src_height / rows)
            icon = crop_resize(src_width, src_height, src, x0, y0, x1, y1, ICON_SIZE)
            out = output_root / cell["kind"] / f"{cell['id']}.png"
            write_png(out, ICON_SIZE, ICON_SIZE, icon)
            write_meta(out, texture_meta(out, sprite=True))
            generated += 1

    print(f"Imported {generated} icons from {len(manifest['sheets'])} sheets")


if __name__ == "__main__":
    os.chdir(ROOT)
    main()
