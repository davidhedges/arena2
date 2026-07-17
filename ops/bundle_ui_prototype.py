#!/usr/bin/env python3
"""Bundle a docs/ui-prototypes screen into one self-contained HTML file.

Prototype screens are authored as index.html + linked stylesheets referencing
repo assets (fonts, kit sprites) by relative path. Hosting them as a Claude
artifact requires a single file with no external requests, so this inlines:
  - <link rel="stylesheet"> -> <style> blocks
  - url(...) references (css + inline <style>) -> data: URIs
    (PNGs larger than 512px are downscaled to keep the bundle light)
and emits only <title> + styles + body content (the artifact host supplies the
document skeleton).

Usage:
  ops/bundle_ui_prototype.py docs/ui-prototypes/system-menu --out /tmp/system-menu.html
"""

from __future__ import annotations

import argparse
import base64
import io
import re
import sys
from pathlib import Path

from PIL import Image

MIME = {".png": "image/png", ".jpg": "image/jpeg", ".ttf": "font/ttf", ".otf": "font/otf"}
MAX_IMAGE_PX = 512


def data_uri(path: Path) -> str:
    suffix = path.suffix.lower()
    mime = MIME.get(suffix)
    if mime is None:
        raise ValueError(f"no MIME mapping for {path}")
    raw = path.read_bytes()
    if suffix in (".png", ".jpg"):
        image = Image.open(io.BytesIO(raw))
        if max(image.size) > MAX_IMAGE_PX:
            image.thumbnail((MAX_IMAGE_PX, MAX_IMAGE_PX))
            buffer = io.BytesIO()
            image.save(buffer, format="PNG")
            raw = buffer.getvalue()
            mime = "image/png"
    return f"data:{mime};base64,{base64.b64encode(raw).decode()}"


def inline_urls(css: str, base: Path) -> str:
    def replace(match: re.Match[str]) -> str:
        ref = match.group(1).strip("'\"")
        if ref.startswith(("data:", "http")):
            return match.group(0)
        target = (base / ref).resolve()
        if not target.exists():
            print(f"warning: missing url ref {target}", file=sys.stderr)
            return match.group(0)
        # Single quotes: safe inside double-quoted style="" attributes too.
        return f"url('{data_uri(target)}')"

    return re.sub(r"url\(([^)]+)\)", replace, css)


def bundle(screen_dir: Path) -> str:
    html = (screen_dir / "index.html").read_text()

    def inline_link(match: re.Match[str]) -> str:
        href = match.group(1)
        css_path = (screen_dir / href).resolve()
        return "<style>\n" + inline_urls(css_path.read_text(), css_path.parent) + "\n</style>"

    html = re.sub(r'<link rel="stylesheet" href="([^"]+)">', inline_link, html)
    html = re.sub(
        r"<style>(.*?)</style>",
        lambda m: "<style>" + inline_urls(m.group(1), screen_dir) + "</style>",
        html,
        flags=re.S,
    )
    # Catch url() refs outside stylesheets too (inline style="" attributes);
    # already-inlined data: URIs are skipped by inline_urls.
    html = inline_urls(html, screen_dir)

    title = re.search(r"<title>.*?</title>", html, re.S)
    styles = re.findall(r"<style>.*?</style>", html, re.S)
    body = re.search(r"<body>(.*)</body>", html, re.S)
    if body is None:
        raise ValueError("index.html has no <body>")
    return "\n".join(([title.group(0)] if title else []) + styles + [body.group(1)])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("screen_dir", type=Path, help="e.g. docs/ui-prototypes/system-menu")
    parser.add_argument("--out", type=Path, required=True, help="bundled html output path")
    args = parser.parse_args()

    args.out.write_text(bundle(args.screen_dir))
    size_kb = args.out.stat().st_size // 1024
    print(f"wrote {args.out} ({size_kb} KB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
