#!/usr/bin/env python3
"""Generate a flow map (RG-encoded 2D direction field) from a grayscale
height/noise map, for the Arena/Environment animated surface shaders.

The flow field is the 90-degree-rotated gradient (curl) of the blurred height
field: flow circulates around height features like convecting liquid, is
divergence-free (no visual pileup at sinks), and tiles seamlessly if the input
tiles. Output is an uncompressed 24-bit TGA with R = flow.x, G = flow.y
(0.5 = still, matching DecodeFlowVector in Common/AnimatedSurface.hlsl).
Import into Unity with sRGB OFF (the checked-in .meta already does this).

Stdlib only. Run with no arguments to regenerate every checked-in flow map:
  python3 ops/generate_flow_map.py
"""
import argparse
import os
import struct
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACK_TEXTURES = os.path.join(
    REPO, "Assets/ThirdParty/AssetStore/Environments/StylizedMaterialsBundle",
    "Textures")
ENVIRONMENT_ART = os.path.join(REPO, "Assets/Arena/Content/Art/Environment")

# (height-map input, flow-map output) pairs regenerated when run with no
# arguments. Add a row here when a new animated surface gets a flow map.
MAPS = [
    (os.path.join(PACK_TEXTURES, "FireShore/FireShore_Magma/T_FireShore_Magma_H.tga"),
     os.path.join(ENVIRONMENT_ART, "Lava/T_Lava_FireShoreMagma_Flow.tga")),
    (os.path.join(PACK_TEXTURES, "Alien/Alien_SpiderAcid/T_Alien_SpiderAcid_H.tga"),
     os.path.join(ENVIRONMENT_ART, "Poison/T_Poison_AlienSpiderAcid_Flow.tga")),
    # MoltenRockSurface materials: flow generated from each source's own height
    # map, so the field circulates around that material's stones/cracks and the
    # molten channels between them carry the motion.
    (os.path.join(PACK_TEXTURES, "FireShore/FireShore_Rock/T_FireShore_Rock_H.tga"),
     os.path.join(ENVIRONMENT_ART, "Lava/T_Lava_FireShoreRock_Flow.tga")),
    (os.path.join(PACK_TEXTURES, "FireShore/FireShore_CrackedCliff/T_FireShore_CrackedCliff_H.tga"),
     os.path.join(ENVIRONMENT_ART, "Lava/T_Lava_FireShoreCrackedCliff_Flow.tga")),
    (os.path.join(PACK_TEXTURES, "FireShore/FireShore_MoltenObsidian/T_FireShore_MoltenObsidian_H.tga"),
     os.path.join(ENVIRONMENT_ART, "Lava/T_Lava_FireShoreMoltenObsidian_Flow.tga")),
    (os.path.join(PACK_TEXTURES, "FireShore/FireShore_Cobblestone/T_FireShore_Cobblestone_H.tga"),
     os.path.join(ENVIRONMENT_ART, "Lava/T_Lava_FireShoreCobblestone_Flow.tga")),
]


def read_tga_gray(path):
    with open(path, "rb") as f:
        header = f.read(18)
        id_len, cmap_type, img_type = header[0], header[1], header[2]
        w, h = struct.unpack("<HH", header[12:16])
        bpp = header[16]
        if img_type != 3 or bpp != 8 or cmap_type != 0:
            sys.exit(f"{path}: expected uncompressed 8-bit grayscale TGA "
                     f"(type 3), got type {img_type} bpp {bpp}")
        f.read(id_len)
        data = f.read(w * h)
    return w, h, [b / 255.0 for b in data]


def write_tga_rg(path, w, h, fx, fy):
    header = struct.pack("<BBBHHBHHHHBB", 0, 0, 2, 0, 0, 0, 0, 0, w, h, 24, 0)
    out = bytearray(header)
    for i in range(w * h):
        r = max(0, min(255, round((fx[i] * 0.5 + 0.5) * 255.0)))
        g = max(0, min(255, round((fy[i] * 0.5 + 0.5) * 255.0)))
        out += bytes((128, g, r))  # TGA stores BGR
    with open(path, "wb") as f:
        f.write(out)


def downsample(vals, w, h, factor):
    nw, nh = w // factor, h // factor
    inv = 1.0 / (factor * factor)
    out = [0.0] * (nw * nh)
    for y in range(nh):
        for x in range(nw):
            s = 0.0
            for dy in range(factor):
                row = (y * factor + dy) * w + x * factor
                s += sum(vals[row:row + factor])
            out[y * nw + x] = s * inv
    return nw, nh, out


def box_blur_wrap(vals, w, h, radius, passes):
    span = 2 * radius + 1
    inv = 1.0 / span
    for _ in range(passes):
        # horizontal
        out = [0.0] * (w * h)
        for y in range(h):
            base = y * w
            s = sum(vals[base + (x % w)] for x in range(-radius, radius + 1))
            for x in range(w):
                out[base + x] = s * inv
                s += vals[base + ((x + radius + 1) % w)]
                s -= vals[base + ((x - radius) % w)]
        vals = out
        # vertical
        out = [0.0] * (w * h)
        for x in range(w):
            s = sum(vals[(y % h) * w + x] for y in range(-radius, radius + 1))
            for y in range(h):
                out[y * w + x] = s * inv
                s += vals[((y + radius + 1) % h) * w + x]
                s -= vals[((y - radius) % h) * w + x]
        vals = out
    return vals


def curl_field(vals, w, h):
    # 90-degree-rotated central-difference gradient, wrapped for tiling.
    fx = [0.0] * (w * h)
    fy = [0.0] * (w * h)
    for y in range(h):
        up, dn = ((y + 1) % h) * w, ((y - 1) % h) * w
        row = y * w
        for x in range(w):
            gx = (vals[row + (x + 1) % w] - vals[row + (x - 1) % w]) * 0.5
            gy = (vals[up + x] - vals[dn + x]) * 0.5
            fx[row + x] = gy
            fy[row + x] = -gx
    return fx, fy


def normalize(fx, fy):
    mags = sorted((x * x + y * y) ** 0.5 for x, y in zip(fx, fy))
    p99 = mags[int(len(mags) * 0.99)] or 1.0
    scale = 1.0 / p99
    for i in range(len(fx)):
        x, y = fx[i] * scale, fy[i] * scale
        m = (x * x + y * y) ** 0.5
        if m > 1.0:  # clamp magnitude, keep direction
            x, y = x / m, y / m
        fx[i], fy[i] = x, y
    return fx, fy


def generate(input_path, output_path, size, blur_radius, blur_passes):
    w, h, vals = read_tga_gray(input_path)
    if w % size or h % size:
        sys.exit(f"input {w}x{h} is not a multiple of --size {size}")
    w, h, vals = downsample(vals, w, h, w // size)
    vals = box_blur_wrap(vals, w, h, blur_radius, blur_passes)
    fx, fy = curl_field(vals, w, h)
    fx, fy = normalize(fx, fy)
    write_tga_rg(output_path, w, h, fx, fy)
    print(f"wrote {output_path} ({w}x{h})")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--input", help="height map (with --output: one map only)")
    ap.add_argument("--output", help="flow map to write")
    ap.add_argument("--size", type=int, default=512,
                    help="output resolution (input must be a multiple)")
    ap.add_argument("--blur-radius", type=int, default=10,
                    help="box blur radius in output texels (bigger = broader swirls)")
    ap.add_argument("--blur-passes", type=int, default=3,
                    help="box blur passes (3 approximates a gaussian)")
    args = ap.parse_args()

    if bool(args.input) != bool(args.output):
        sys.exit("--input and --output must be given together")
    maps = [(args.input, args.output)] if args.input else MAPS
    for input_path, output_path in maps:
        generate(input_path, output_path,
                 args.size, args.blur_radius, args.blur_passes)


if __name__ == "__main__":
    main()
