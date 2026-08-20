#!/usr/bin/env python3
"""Derive animation VFX slash tracks for every melee attack from the animation itself.

A CombatAnimationSet stores two separate things:

  animationVfxTracks    per (clip, slot): WHEN the effect spawns and HOW it sits in
                        character space. Element-agnostic calibration.
  animationVfxBindings  per attack: WHICH registered effect fills the slot. Owned by
                        gameplay/spec, and overridable per request at runtime.

Only the first is tedious and mechanical, so only the first is generated here. The
timing and orientation are measured off the humanoid hand-goal curves baked into each
clip, so the result follows the animation rather than an eyeballed guess.

Method
  * The strike is located from the entry's authored impactNormalized, cross-checked
    against the clip's OnStrikeHit events.
  * The swinging hand is whichever goal curve peaks fastest near that strike.
  * start  = the wind-up reversal, i.e. the last speed minimum before the peak.
  * The swing plane comes from an SVD over the arc; its normal is signed by the
    angular velocity so the facing is unambiguous.
  * rotation = archetype correction * LookRotation(sweep direction, plane normal),
    so the per-clip half is measured and the per-archetype half is tuned by hand.

Usage
  ops/slash-vfx-author.py                    # dry run: report what would be written
  ops/slash-vfx-author.py --apply            # write animationVfxTracks into the assets
  ops/slash-vfx-author.py --set Daggers      # restrict to one animation set
  ops/slash-vfx-author.py --clusters         # dump measured plane/sweep vectors
"""
from __future__ import annotations

import argparse
import glob
import json
import math
import os
import re
import sys
from dataclasses import dataclass, field

import numpy as np

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SETS_DIR = os.path.join(REPO, "Assets/Arena/Resources/CombatAnimationSets")
PRESETS_PATH = os.path.join(REPO, "ops/slash-vfx-presets.json")
SLOT_ID = "SLASH_PRIMARY"

# ---------------------------------------------------------------------------
# Unity conventions: left-handed, Y up, +Z forward, Quaternion.Euler order ZXY.
# ---------------------------------------------------------------------------


def _rx(t):
    c, s = math.cos(t), math.sin(t)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def _ry(t):
    c, s = math.cos(t), math.sin(t)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def _rz(t):
    c, s = math.cos(t), math.sin(t)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


def euler_to_matrix(x, y, z):
    return _ry(math.radians(y)) @ _rx(math.radians(x)) @ _rz(math.radians(z))


def matrix_to_euler(R):
    sx = max(-1.0, min(1.0, -R[1][2]))
    x = math.asin(sx)
    if abs(math.cos(x)) < 1e-6:
        y, z = math.atan2(-R[2][0], R[0][0]), 0.0
    else:
        z = math.atan2(R[1][0], R[1][1])
        y = math.atan2(R[0][2], R[2][2])
    return tuple(math.degrees(v) % 360.0 for v in (x, y, z))


def look_rotation(forward, up):
    z = np.asarray(forward, float)
    z = z / np.linalg.norm(z)
    x = np.cross(np.asarray(up, float), z)
    if np.linalg.norm(x) < 1e-8:
        alt = np.array([1.0, 0.0, 0.0]) if abs(z[0]) < 0.9 else np.array([0.0, 1.0, 0.0])
        x = np.cross(alt, z)
    x = x / np.linalg.norm(x)
    return np.column_stack([x, np.cross(z, x), z])


# ---------------------------------------------------------------------------
# Unity .anim reader. Unity YAML carries custom tags, so a real YAML parser is
# more trouble than a line reader for the handful of sections that matter.
# ---------------------------------------------------------------------------

CURVE_SECTIONS = ("m_RotationCurves", "m_PositionCurves", "m_ScaleCurves", "m_FloatCurves")


@dataclass
class Curve:
    attribute: str = ""
    times: list = field(default_factory=list)
    values: list = field(default_factory=list)
    in_slopes: list = field(default_factory=list)
    out_slopes: list = field(default_factory=list)

    def sample(self, t: float) -> float:
        """Cubic Hermite, matching how Unity evaluates an AnimationCurve."""
        ts = self.times
        if not ts:
            return 0.0
        if t <= ts[0]:
            return self.values[0]
        if t >= ts[-1]:
            return self.values[-1]
        lo, hi = 0, len(ts) - 1
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if ts[mid] <= t:
                lo = mid
            else:
                hi = mid
        dt = ts[hi] - ts[lo]
        if dt <= 0:
            return self.values[lo]
        u = (t - ts[lo]) / dt
        u2, u3 = u * u, u * u * u
        return (
            (2 * u3 - 3 * u2 + 1) * self.values[lo]
            + (u3 - 2 * u2 + u) * dt * self.out_slopes[lo]
            + (-2 * u3 + 3 * u2) * self.values[hi]
            + (u3 - u2) * dt * self.in_slopes[hi]
        )


def read_clip(path: str):
    """Return (float curves by attribute, metadata) for one .anim asset."""
    curves, meta = {}, {"events": [], "stopTime": 0.0, "name": ""}
    section, cur = None, None
    with open(path, "r", errors="replace") as fh:
        for line in fh:
            body = line.strip()
            if line.startswith("  m_") and line.rstrip().endswith(":"):
                if cur is not None and section == "m_FloatCurves":
                    curves[cur.attribute] = cur
                cur = None
                key = body.rstrip(":")
                section = key if key in CURVE_SECTIONS else None
                continue
            if body.startswith("m_Name:") and not meta["name"]:
                meta["name"] = body.split(":", 1)[1].strip()
            elif body.startswith("m_StopTime:"):
                meta["stopTime"] = float(body.split(":", 1)[1])
            if section != "m_FloatCurves":
                continue
            if line.startswith("  - "):
                if cur is not None:
                    curves[cur.attribute] = cur
                cur = Curve()
                body = line[4:].strip()
            if cur is None:
                continue
            if body.startswith("time:"):
                cur.times.append(float(body.split(":", 1)[1]))
            elif body.startswith("value:"):
                raw = body.split(":", 1)[1].strip()
                if not raw.startswith("{"):
                    cur.values.append(float(raw))
            elif body.startswith("inSlope:"):
                cur.in_slopes.append(float(body.split(":", 1)[1]))
            elif body.startswith("outSlope:"):
                cur.out_slopes.append(float(body.split(":", 1)[1]))
            elif body.startswith("attribute:"):
                cur.attribute = body.split(":", 1)[1].strip()
    if cur is not None and section == "m_FloatCurves":
        curves[cur.attribute] = cur

    text = open(path, "r", errors="replace").read()
    tail = text.split("\n  m_Events:", 1)
    if len(tail) > 1:
        for m in re.finditer(r"- time: (-?[\d.eE+-]+)\n\s+functionName: (\S+)", tail[1]):
            meta["events"].append((float(m.group(1)), m.group(2)))
    return curves, meta


# ---------------------------------------------------------------------------
# Swing measurement
# ---------------------------------------------------------------------------


@dataclass
class Swing:
    hand: str
    start: float
    peak: float
    settle: float
    peak_speed: float
    planarity: float
    normal: np.ndarray
    sweep: np.ndarray
    centroid: np.ndarray
    arc_length: float
    chord: float
    turn_degrees: float
    tilt_degrees: float
    confidence: float


def _goal_track(curves, hand, times):
    return np.array([[curves[f"{hand}T.{k}"].sample(t) for k in "xyz"] for t in times])


def measure_swing(curves, meta, impact_seconds: float | None) -> Swing | None:
    stop = meta["stopTime"]
    if stop <= 0:
        return None
    dt = 1.0 / 60.0
    times = np.arange(0.0, stop + 1e-6, dt)
    if len(times) < 5:
        return None

    hits = [t for t, n in meta["events"] if n == "OnStrikeHit"]
    anchor = impact_seconds if impact_seconds is not None else (hits[0] if hits else None)

    best = None
    for hand in ("RightHand", "LeftHand"):
        if f"{hand}T.x" not in curves:
            continue
        pos = _goal_track(curves, hand, times)
        speed = np.linalg.norm(np.gradient(pos, dt, axis=0), axis=1)

        # Anchor the search to the authored strike so a later flourish in the same
        # clip cannot steal the peak from the actual attack.
        if anchor is None:
            peak = int(np.argmax(speed))
        else:
            window = np.abs(times - anchor) <= 0.20
            if not window.any():
                window = np.abs(times - anchor) <= 0.40
            local = np.where(window, speed, -1.0)
            peak = int(np.argmax(local))

        i = peak
        while i > 0 and speed[i - 1] < speed[i]:
            i -= 1
        if i == peak:
            lookback = max(0, peak - int(round(0.25 / dt)))
            if lookback < peak:
                i = lookback + int(np.argmin(speed[lookback:peak]))
        j = peak
        while j < len(speed) - 1 and speed[j + 1] < speed[j]:
            j += 1
        if j <= i:
            continue

        # Fit the plane over the fast part of the swing only. The wind-up and the
        # recovery tail bend away from the strike and would tilt the fit.
        lo, hi = peak, peak
        floor = speed[peak] * 0.4
        while lo > i and speed[lo - 1] >= floor:
            lo -= 1
        while hi < j and speed[hi + 1] >= floor:
            hi += 1
        span_start, span_end = times[lo], times[max(hi, lo + 1)]
        fine = np.arange(span_start, span_end + 1e-6, 1.0 / 240.0)
        if len(fine) < 4:
            continue
        pts = _goal_track(curves, hand, fine)
        centroid = pts.mean(axis=0)
        _, sv, vt = np.linalg.svd(pts - centroid)
        normal = vt[2]

        # SVD normals are sign-ambiguous. Signing by the mean angular velocity
        # r x v makes the swing's facing deterministic and physically meaningful.
        rel = pts - centroid
        vel = np.gradient(pts, 1.0 / 240.0, axis=0)
        spin = np.cross(rel, vel).mean(axis=0)
        if np.dot(spin, normal) < 0:
            normal = -normal
        normal = normal / np.linalg.norm(normal)

        # How far the velocity vector turns over the swing separates an arcing
        # slash from a straight thrust far more reliably than path length does.
        unit = vel / np.maximum(np.linalg.norm(vel, axis=1, keepdims=True), 1e-9)
        dots = np.clip(np.sum(unit[:-1] * unit[1:], axis=1), -1.0, 1.0)
        turn = float(np.degrees(np.arccos(dots)).sum())

        chord_vec = pts[-1] - pts[0]
        chord = float(np.linalg.norm(chord_vec))
        if chord < 1e-6:
            continue
        sweep = chord_vec - normal * np.dot(chord_vec, normal)
        if np.linalg.norm(sweep) < 1e-6:
            continue
        sweep = sweep / np.linalg.norm(sweep)

        cand = Swing(
            hand=hand,
            start=float(times[i]),
            peak=float(times[peak]),
            settle=float(times[j]),
            peak_speed=float(speed[peak]),
            planarity=float(sv[2] / sv[0]) if sv[0] > 0 else 1.0,
            normal=normal,
            sweep=sweep,
            centroid=centroid,
            arc_length=float(np.sum(np.linalg.norm(np.diff(pts, axis=0), axis=1))),
            chord=chord,
            turn_degrees=turn,
            tilt_degrees=float(np.degrees(np.arccos(min(1.0, abs(normal[1]))))),
            # A straight-line motion has no second in-plane axis, so its plane
            # normal is arbitrary. This ratio says how much to trust the fit.
            confidence=float(sv[1] / sv[0]) if sv[0] > 0 else 0.0,
        )
        if best is None or cand.peak_speed > best.peak_speed:
            best = cand
    return best


def classify(swing: Swing) -> str:
    """Bucket a measured swing into an archetype the owner tunes once.

    Only the plane's tilt from horizontal matters here. Which way the arc sweeps
    inside that plane is already carried per clip by the measured rotation, so a
    left-handed and a right-handed diagonal share one preset.
    """
    if swing.turn_degrees < 55.0 and float(swing.sweep[2]) > 0.5:
        return "THRUST"
    if swing.tilt_degrees < 30.0:
        return "HORIZONTAL"
    if swing.tilt_degrees < 65.0:
        return "DIAGONAL"
    return "VERTICAL"


# ---------------------------------------------------------------------------
# CombatAnimationSet reading / writing
# ---------------------------------------------------------------------------


@dataclass
class Entry:
    action_id: str
    clip_guid: str
    impact_normalized: float


def guid_to_anim_path():
    index = {}
    for meta in glob.glob(os.path.join(REPO, "Assets/**/*.anim.meta"), recursive=True):
        with open(meta, errors="replace") as fh:
            for line in fh:
                if line.startswith("guid:"):
                    index[line.split(":", 1)[1].strip()] = meta[:-5]
                    break
    return index


def read_set(path: str):
    text = open(path, errors="replace").read()
    head = text.split("\n  meleeAttacks:\n", 1)
    if len(head) < 2:
        return text, []
    region = head[1]
    for m in re.finditer(r"^  [A-Za-z_]", region, re.M):
        region = region[:m.start()]
        break
    entries = []
    for block in re.split(r"\n  - clip: ", region):
        guid = re.match(r"\{fileID: \d+, guid: ([0-9a-f]+)", block.strip())
        aid = re.search(r"^      id: (\S+)", block, re.M)
        imp = re.search(r"^      impactNormalized: (-?[\d.eE+-]+)", block, re.M)
        if guid and aid:
            entries.append(Entry(aid.group(1), guid.group(1),
                                 float(imp.group(1)) if imp else 0.0))
    return text, entries


def format_track(t) -> str:
    lines = [f"  - clip: {{fileID: 7400000, guid: {t['guid']}, type: 2}}",
             f"    slotId: {t['slotId']}",
             f"    startTimeSeconds: {t['startTimeSeconds']:g}",
             f"    endTimeSeconds: {t['endTimeSeconds']:g}",
             f"    anchor: {t['anchor']}",
             f"    attachment: {t['attachment']}"]
    for key in ("localPosition", "localEulerAngles", "localScale"):
        x, y, z = t[key]
        lines.append(f"    {key}: {{x: {x:g}, y: {y:g}, z: {z:g}}}")
    return "\n".join(lines)


def split_track_entries(text: str):
    """Existing track entries as raw text, so anything hand-authored survives verbatim."""
    marker = "\n  animationVfxTracks:"
    if marker not in text:
        return []
    start = text.index(marker) + 1
    rest = text[start + len("  animationVfxTracks:"):]
    end = len(rest)
    for m in re.finditer(r"^  [A-Za-z_]", rest, re.M):
        end = m.start()
        break
    body = rest[:end]
    entries = []
    for chunk in re.split(r"^  - (?=clip:)", body, flags=re.M)[1:]:
        raw = "  - " + chunk.rstrip("\n")
        guid = re.search(r"clip: \{fileID: \d+, guid: ([0-9a-f]+)", raw)
        slot = re.search(r"^    slotId: (\S*)", raw, re.M)
        key = (guid.group(1), normalize_slot(slot.group(1) if slot else "")) if guid else None
        entries.append((key, raw))
    return entries


def normalize_slot(value: str) -> str:
    return value.strip().upper()


def merge_tracks(text: str, generated) -> tuple[str, int, int]:
    """Overwrite only the (clip, slot) pairs this tool owns; keep every other entry.

    Hand-authored tracks -- a second slot on a clip, a one-off tuned by hand -- must
    survive --apply, so they are carried through as their original bytes.
    """
    produced = {(t["guid"], normalize_slot(t["slotId"])): t for t in generated}
    out, used, kept = [], set(), 0
    for key, raw in split_track_entries(text):
        if key is None:                     # malformed placeholder with no clip
            continue
        if key in produced:
            out.append(format_track(produced[key]))
            used.add(key)
        else:
            out.append(raw)
            kept += 1
    for key, track in produced.items():
        if key not in used:
            out.append(format_track(track))
    block = "  animationVfxTracks:\n" + "\n".join(out) if out else "  animationVfxTracks: []"
    return block, kept, len(produced)


def region_bounds(text: str, key: str) -> tuple[int, int]:
    """Byte range of a top-level key's block, or a zero-width insertion point.

    Three of the five sets predate `animationVfxTracks` and never serialised it, so
    the block has to be inserted rather than replaced.
    """
    marker = f"\n  {key}:"
    if marker in text:
        start = text.index(marker)
        rest = text[start + 1:]
        for m in re.finditer(r"^  [A-Za-z_]", rest, re.M):
            if m.start() > 0:
                return start, start + 1 + m.start()
        return start, len(text)

    anchor = text.index("\n  meleeAttacks:")
    rest = text[anchor + 1:]
    for m in re.finditer(r"^  [A-Za-z_]", rest, re.M):
        if m.start() > 0:
            pos = anchor + 1 + m.start()
            return pos, pos
    raise ValueError("could not locate where animationVfxTracks belongs")


def replace_tracks_block(text: str, block: str) -> str:
    """Swap or insert the animationVfxTracks list, leaving every other byte alone."""
    start, end = region_bounds(text, "animationVfxTracks")
    if start == end:
        return text[:start] + block + "\n" + text[start:]
    return text[:start] + "\n" + block + "\n" + text[end:]


def write_atomically(path: str, text: str) -> None:
    """Build first, then swap. Truncating before the content exists loses the asset."""
    tmp = f"{path}.slashvfx.tmp"
    with open(tmp, "w") as fh:
        fh.write(text)
    os.replace(tmp, path)


def parse_track_entry(raw: str):
    """Parse one raw track entry into the fields calibration needs."""
    def vec(name):
        m = re.search(r"^    %s: \{x: ([-\d.eE+]+), y: ([-\d.eE+]+), z: ([-\d.eE+]+)\}" % name, raw, re.M)
        return [float(m.group(i)) for i in (1, 2, 3)] if m else [0.0, 0.0, 0.0]

    def num(name, cast=float):
        m = re.search(r"^    %s: ([-\d.eE+]+)" % name, raw, re.M)
        return cast(m.group(1)) if m else cast(0)

    guid = re.search(r"clip: \{fileID: \d+, guid: ([0-9a-f]+)", raw)
    slot = re.search(r"^    slotId: (\S*)", raw, re.M)
    if not guid:
        return None
    return {
        "guid": guid.group(1),
        "slotId": normalize_slot(slot.group(1) if slot else ""),
        "startTimeSeconds": num("startTimeSeconds"),
        "endTimeSeconds": num("endTimeSeconds"),
        "anchor": num("anchor", int),
        "attachment": num("attachment", int),
        "localPosition": vec("localPosition"),
        "localEulerAngles": vec("localEulerAngles"),
        "localScale": vec("localScale"),
    }


def read_authored_tracks(text: str):
    """Existing tracks, which are the only evidence of the look actually wanted."""
    out = []
    for _key, raw in split_track_entries(text):
        parsed = parse_track_entry(raw)
        if parsed is not None:
            out.append(parsed)
    return out


def calibrate(clip_name: str, archetypes, index) -> int:
    """Learn the per-archetype half from a track that was tuned by hand.

    Hand-tune one clip in the CombatAnimationSet inspector until it looks right,
    point this at that clip, and every other clip inherits the same relationship
    between the effect and the swing it belongs to.
    """
    for asset in sorted(glob.glob(os.path.join(SETS_DIR, "*.asset"))):
        text, entries = read_set(asset)
        impact_by_guid = {e.clip_guid: e.impact_normalized for e in entries}
        for track in read_authored_tracks(text):
            anim = index.get(track["guid"])
            if not anim or not os.path.exists(anim):
                continue
            curves, meta = read_clip(anim)
            if meta["name"] != clip_name:
                continue

            impact = impact_by_guid.get(track["guid"], 0.0) * meta["stopTime"] or None
            swing = measure_swing(curves, meta, impact)
            if swing is None:
                print(f"! {clip_name}: no hand goal curves to calibrate against")
                return 1

            start_offset = round(track["startTimeSeconds"] - swing.start, 4)
            authored = euler_to_matrix(*track["localEulerAngles"])
            correction = authored @ look_rotation(swing.sweep, swing.normal).T
            euler = tuple(round(v, 4) for v in matrix_to_euler(correction))
            measured = classify(swing)

            print(f"calibrating from {clip_name} in {os.path.basename(asset)[:-6]}")
            print(f"  authored rotation : {tuple(track['localEulerAngles'])}")
            print(f"  measured swing    : tilt {swing.tilt_degrees:.1f} deg, "
                  f"turn {swing.turn_degrees:.1f} deg -> {measured}")
            print(f"  start time        : authored {track['startTimeSeconds']:.3f}s "
                  f"vs measured {swing.start:.3f}s")
            print(f"  correction        : {euler}")
            print(f"  start offset      : {start_offset:+.4f}s after the reversal")

            for name in archetypes:
                preset = presets_for(name)
                preset["correctionEuler"] = list(euler)
                preset["localPosition"] = list(track["localPosition"])
                preset["localScale"] = list(track["localScale"])
                preset["anchor"] = track["anchor"]
                preset["attachment"] = track["attachment"]
                preset["startOffsetSeconds"] = start_offset
                print(f"  -> wrote preset {name}")
            save_presets()
            return 0

    print(f"! no authored track found for clip '{clip_name}'")
    return 1


_PRESETS = None


def presets_for(name: str):
    return _PRESETS["archetypes"].setdefault(name, {})


def save_presets():
    with open(PRESETS_PATH, "w") as fh:
        json.dump(_PRESETS, fh, indent=2)
        fh.write("\n")


def load_presets():
    global _PRESETS
    with open(PRESETS_PATH) as fh:
        _PRESETS = json.load(fh)
    return _PRESETS


def resolve_end_time(swing: Swing, preset) -> float:
    """0 lets the prefab finish naturally; a positive offset cuts it after the swing.

    Worth setting when the effect carries long-lived smoke or dust: during a fast
    combo each strike spawns another copy, and they stack.
    """
    offset = float(preset.get("endOffsetSeconds", 0) or 0)
    if offset <= 0:
        return 0
    spawn = swing.start + float(preset.get("startOffsetSeconds", 0) or 0)
    return round(max(0.0, spawn) + offset, 3)


def compose(swing: Swing, preset) -> dict:
    correction = euler_to_matrix(*preset["correctionEuler"])
    R = correction @ look_rotation(swing.sweep, swing.normal)
    ex, ey, ez = matrix_to_euler(R)
    return {
        "localEulerAngles": (round(ex, 3), round(ey, 3), round(ez, 3)),
        "localPosition": tuple(preset["localPosition"]),
        "localScale": tuple(preset["localScale"]),
    }


MELEE_REGION = "\n  meleeAttacks:\n"
BINDINGS_KEY = "    animationVfxBindings:"


def format_bindings(pairs) -> str:
    """`pairs` is [(slotId, vfxId)]; an empty list serialises as the YAML empty list."""
    if not pairs:
        return BINDINGS_KEY + " []"
    lines = [BINDINGS_KEY]
    for slot, vfx in pairs:
        lines.append(f"    - slotId: {slot}")
        lines.append(f"      vfxId: {vfx}")
    return "\n".join(lines)


def read_entry_bindings(chunk: str):
    out = []
    tail = chunk[chunk.index(BINDINGS_KEY):] if BINDINGS_KEY in chunk else ""
    for m in re.finditer(r"^    - slotId: (\S*)\n      vfxId: (\S*)", tail, re.M):
        out.append((normalize_slot(m.group(1)), m.group(2).strip().upper()))
    return out


def apply_bindings(text: str, action_ids, slot: str, vfx_id: str):
    """Set or clear one slot's binding on the named attack entries.

    Bindings are per attack entry, so abilities that share a clip stay independent.
    An empty vfx_id removes the slot's binding rather than authoring an empty one,
    which the validator rejects.
    """
    head, _, rest = text.partition(MELEE_REGION)
    region, sep, tail = rest.partition("\n  animationVfxTracks:")
    chunks = region.split("\n  - clip: ")
    wanted = {a.upper() for a in action_ids}
    touched, missing = [], set(wanted)

    for index, chunk in enumerate(chunks):
        aid = re.search(r"^      id: (\S+)", chunk, re.M)
        if not aid or aid.group(1).upper() not in wanted:
            continue
        missing.discard(aid.group(1).upper())
        if BINDINGS_KEY not in chunk:
            continue

        pairs = [(s, v) for s, v in read_entry_bindings(chunk) if s != slot]
        if vfx_id:
            pairs.append((slot, vfx_id))
        pairs.sort()
        # the split consumed the separator newline, so the chunk must not gain one
        chunks[index] = chunk[:chunk.index(BINDINGS_KEY)] + format_bindings(pairs)
        touched.append(aid.group(1))

    return head + MELEE_REGION + "\n  - clip: ".join(chunks) + sep + tail, touched, sorted(missing)


def bind(action_ids, slot: str, vfx_id: str, only_set, do_apply: bool) -> int:
    slot = normalize_slot(slot)
    vfx_id = vfx_id.strip().upper()
    outstanding = {a.upper() for a in action_ids}
    for asset in sorted(glob.glob(os.path.join(SETS_DIR, "*.asset"))):
        name = os.path.basename(asset)[:-6]
        if only_set and only_set.lower() != name.lower():
            continue
        text = open(asset, errors="replace").read()
        updated, touched, _ = apply_bindings(text, action_ids, slot, vfx_id)
        if not touched:
            continue
        verb = f"{slot} -> {vfx_id}" if vfx_id else f"cleared {slot}"
        print(f"{name}: {verb}")
        for aid in touched:
            print(f"  {aid}")
            outstanding.discard(aid.upper())
        if do_apply:
            write_atomically(asset, updated)

    for aid in sorted(outstanding):
        print(f"! no melee attack entry named '{aid}'")
    if not do_apply:
        print("\n(dry run; pass --apply to write)")
    return 1 if outstanding else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="write tracks into the assets")
    ap.add_argument("--set", dest="only_set", help="restrict to one animation set name")
    ap.add_argument("--clusters", action="store_true", help="dump measured swing vectors")
    ap.add_argument("--calibrate", metavar="CLIP",
                    help="learn the archetype correction from an already hand-tuned clip")
    ap.add_argument("--archetype", default="all",
                    help="which archetype --calibrate writes to (default: all)")
    ap.add_argument("--bind", metavar="VFX_ID",
                    help="author a slot binding on --attacks; empty string clears it")
    ap.add_argument("--attacks", help="comma-separated melee attack ids for --bind")
    ap.add_argument("--slot", default=SLOT_ID, help=f"slot id to bind (default: {SLOT_ID})")
    args = ap.parse_args()

    presets = load_presets()
    index = guid_to_anim_path()

    if args.bind is not None:
        if not args.attacks:
            ap.error("--bind needs --attacks")
        return bind([a.strip() for a in args.attacks.split(",") if a.strip()],
                    args.slot, args.bind, args.only_set, args.apply)

    if args.calibrate:
        targets = (list(presets["archetypes"]) if args.archetype == "all"
                   else [args.archetype.upper()])
        return calibrate(args.calibrate, targets, index)
    total, written = 0, 0

    for asset in sorted(glob.glob(os.path.join(SETS_DIR, "*.asset"))):
        name = os.path.basename(asset)[:-6]
        if args.only_set and args.only_set.lower() != name.lower():
            continue
        text, entries = read_set(asset)
        if not entries:
            continue

        print(f"\n=== {name} ===")
        by_clip = {}
        for entry in entries:
            by_clip.setdefault(entry.clip_guid, []).append(entry)
        shared = []
        for guid, group in by_clip.items():
            impacts = [e.impact_normalized for e in group]
            if max(impacts) - min(impacts) > 0.02:      # ~1 frame; below that nobody sees it
                shared.append((guid, group))

        previous = {t["guid"]: t for t in read_authored_tracks(text)}
        tracks, seen, skipped = [], set(), []
        for entry in entries:
            anim = index.get(entry.clip_guid)
            if not anim or not os.path.exists(anim):
                skipped.append((entry.action_id, "clip not found"))
                continue
            if entry.clip_guid in seen:
                continue          # one calibration per clip; attacks sharing it reuse it
            curves, meta = read_clip(anim)
            impact = entry.impact_normalized * meta["stopTime"] if entry.impact_normalized else None
            swing = measure_swing(curves, meta, impact)
            if swing is None:
                skipped.append((entry.action_id, "no hand goal curves"))
                continue
            seen.add(entry.clip_guid)
            total += 1

            archetype = classify(swing)
            preset = presets["archetypes"][archetype]
            xform = compose(swing, preset)
            tracks.append({
                "guid": entry.clip_guid,
                "slotId": SLOT_ID,
                "startTimeSeconds": round(
                    max(0.0, swing.start + float(preset.get("startOffsetSeconds", 0) or 0)), 3),
                "endTimeSeconds": resolve_end_time(swing, preset),
                "anchor": preset["anchor"],
                "attachment": preset["attachment"],
                **xform,
            })

            if args.clusters:
                print(f"  {meta['name']:32} n={np.round(swing.normal, 3)} "
                      f"d={np.round(swing.sweep, 3)} flat={swing.planarity:.3f} "
                      f"tilt={swing.tilt_degrees:5.1f} turn={swing.turn_degrees:6.1f} -> {archetype}")
            else:
                ex, ey, ez = xform["localEulerAngles"]
                weak = "  <- straight motion, plane is a guess" if swing.confidence < 0.12 else ""
                print(f"  {meta['name']:32} {archetype:11} "
                      f"spawn={tracks[-1]['startTimeSeconds']:.3f} peak={swing.peak:.3f} "
                      f"rot=({ex:.0f}, {ey:.0f}, {ez:.0f}){weak}")

        for aid, why in skipped:
            print(f"  ! {aid}: {why}")
        for guid, group in shared:
            ids = ", ".join(f"{e.action_id}@{e.impact_normalized:.3f}" for e in group)
            print(f"  ! attacks share one clip but land at different times, so they "
                  f"share one spawn time: {ids}")
        for track in tracks:
            old = previous.get(track["guid"])
            if old is None:
                continue
            same_rotation = np.allclose(
                euler_to_matrix(*old["localEulerAngles"]),
                euler_to_matrix(*track["localEulerAngles"]), atol=1e-4)
            if not same_rotation or abs(old["startTimeSeconds"] - track["startTimeSeconds"]) > 1e-3:
                print(f"  * replacing a hand-authored track: start "
                      f"{old['startTimeSeconds']:.3f} -> {track['startTimeSeconds']:.3f}, "
                      f"rotation {'unchanged' if same_rotation else 'CHANGED'}")

        if tracks:
            block, kept, produced = merge_tracks(text, tracks)
            if kept:
                print(f"  kept {kept} hand-authored track(s) this tool does not own")
            if args.apply:
                write_atomically(asset, replace_tracks_block(text, block))
                written += produced
                print(f"  -> wrote {produced} tracks")

    print(f"\n{total} clips measured" + (f", {written} tracks written" if args.apply else " (dry run)"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
