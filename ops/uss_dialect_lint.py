#!/usr/bin/env python3
"""Lint stylesheets against the USS-translatable dialect.

Web UI prototypes are specs for UXML/USS translation (docs/ui-toolkit-workflow.md).
A prototype that uses CSS features USS cannot express is a spec that lies, so both
the prototype .css and the shipped .uss are held to Unity's USS feature set:

  flexbox only - no grid, float, gap, or inline layout
  px / % / unitless only - no rem, em, vh, vw, calc()
  class/name/type selectors with descendant/child combinators - no siblings,
    attributes, pseudo-elements, or nth-*
  paint order is document order - no z-index
  no box-shadow, filters, line-height, or keyframe animations (transitions OK)

.css files get a small browser-scaffolding allowance (@font-face, font-family,
box-sizing, ...) for things with a known manual mapping to USS.

Usage:
  ops/uss_dialect_lint.py [paths...]   # default: docs/ui-prototypes + Assets/Arena/Content/UI
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEFAULT_ROOTS = [REPO / "docs/ui-prototypes", REPO / "Assets/Arena/Resources/UI/Toolkit"]

# Documented USS properties (Unity 6), plus custom properties (--*) handled separately.
USS_PROPERTIES = {
    "align-content", "align-items", "align-self", "all",
    "background-color", "background-image", "background-position",
    "background-position-x", "background-position-y", "background-repeat",
    "background-size",
    "border-bottom-color", "border-bottom-left-radius", "border-bottom-right-radius",
    "border-bottom-width", "border-color", "border-left-color", "border-left-width",
    "border-radius", "border-right-color", "border-right-width", "border-top-color",
    "border-top-left-radius", "border-top-right-radius", "border-top-width",
    "border-width",
    "bottom", "color", "cursor", "display",
    "flex", "flex-basis", "flex-direction", "flex-grow", "flex-shrink", "flex-wrap",
    "font-size", "height", "justify-content", "left", "letter-spacing",
    "margin", "margin-bottom", "margin-left", "margin-right", "margin-top",
    "max-height", "max-width", "min-height", "min-width",
    "opacity", "overflow",
    "padding", "padding-bottom", "padding-left", "padding-right", "padding-top",
    "position", "right", "rotate", "scale", "text-overflow", "text-shadow", "top",
    "transform-origin",
    "transition", "transition-delay", "transition-duration", "transition-property",
    "transition-timing-function",
    "translate", "visibility", "white-space", "width", "word-spacing",
    "-unity-background-image-tint-color", "-unity-background-scale-mode",
    "-unity-font", "-unity-font-definition", "-unity-font-style",
    "-unity-overflow-clip-box", "-unity-paragraph-spacing",
    "-unity-slice-bottom", "-unity-slice-left", "-unity-slice-right",
    "-unity-slice-scale", "-unity-slice-top",
    "-unity-text-align", "-unity-text-outline", "-unity-text-outline-color",
    "-unity-text-outline-width", "-unity-text-overflow-position",
}

# Browser scaffolding tolerated in prototype .css with a known manual USS mapping.
CSS_PROTO_PROPERTIES = {
    "font-family",   # -> -unity-font-definition (pick the matching font asset)
    "font-weight",   # -> a distinct -unity-font-definition per weight, or -unity-font-style
    "font-style",    # -> -unity-font-style
    "text-align",    # -> -unity-text-align
    "box-sizing",    # prototypes must set border-box; USS is always border-box
    "user-select",   # browser-only nicety, no visual meaning in-game
    "pointer-events",  # -> picking-mode="Ignore" on the UXML element
    # 9-slice plate frames -> USS background-image + -unity-slice-*
    "border-image",
    "border-image-source",
    "border-image-slice",
    "border-image-width",
    "border-image-repeat",
    "border-style",  # CSS needs it for border-image area; USS borders are always solid
}

# Targeted advice for the most tempting unsupported properties.
PROPERTY_ADVICE = {
    "gap": "USS has no gap - space children with margins",
    "row-gap": "USS has no gap - space children with margins",
    "column-gap": "USS has no gap - space children with margins",
    "z-index": "USS paints in document order - reorder elements instead",
    "box-shadow": "USS has no box-shadow - fake it with a border or a backing element",
    "line-height": "USS has no line-height - size text with font-size and padding",
    "border": "USS has no border shorthand and borders are always solid - use border-width + border-color",
    "border-top": "USS has no border-* side shorthand - use border-top-width + border-top-color",
    "border-right": "USS has no border-* side shorthand - use border-right-width + border-right-color",
    "border-bottom": "USS has no border-* side shorthand - use border-bottom-width + border-bottom-color",
    "border-left": "USS has no border-* side shorthand - use border-left-width + border-left-color",
    "border-style": "USS borders are always solid - remove",
    "outline": "USS has no outline - style :focus with border-* or background-color",
    "transform": "USS splits transforms - use translate / rotate / scale properties",
    "grid-template-columns": "USS is flexbox-only - restructure as nested flex rows",
    "grid-template-rows": "USS is flexbox-only - restructure as nested flex rows",
    "float": "USS is flexbox-only",
    "animation": "USS has no @keyframes - use transition or drive from C#",
    "backdrop-filter": "USS has no filters",
    "filter": "USS has no filters",
    "text-decoration": "USS has no text-decoration - use color/weight or a hairline element",
}

ALLOWED_PSEUDO_CLASSES = {"hover", "active", "focus", "disabled", "enabled", "checked", "root"}
BANNED_UNITS = re.compile(r"(?<![\w-])-?[\d.]+(rem|em|vh|vw|vmin|vmax|pt|ch|ex)\b")
DISPLAY_VALUES = {"flex", "none"}
POSITION_VALUES = {"relative", "absolute"}


class Linter:
    def __init__(self, path: Path, is_proto_css: bool):
        self.path = path
        self.proto = is_proto_css
        self.errors: list[tuple[int, str]] = []

    def error(self, line: int, message: str) -> None:
        self.errors.append((line, message))

    def lint(self, text: str) -> None:
        text = self._blank_comments(text)
        self._check_at_rules(text)
        # `selector { declarations }` — at-rule bodies (@font-face) skipped by selector check.
        for match in re.finditer(r"([^{}]+)\{([^{}]*)\}", text):
            selector, body = match.group(1), match.group(2)
            line = text.count("\n", 0, match.start()) + 1
            if not selector.strip().startswith("@"):
                self._check_selector(selector.strip(), line)
            self._check_declarations(body, text.count("\n", 0, match.start(2)) + 1, selector.strip())

    @staticmethod
    def _blank_comments(text: str) -> str:
        # Preserve newlines so reported line numbers stay true.
        return re.sub(
            r"/\*.*?\*/",
            lambda m: re.sub(r"[^\n]", " ", m.group(0)),
            text,
            flags=re.S,
        )

    def _check_at_rules(self, text: str) -> None:
        for match in re.finditer(r"@([\w-]+)", text):
            name = match.group(1)
            line = text.count("\n", 0, match.start()) + 1
            allowed = {"import"} | ({"font-face"} if self.proto else set())
            if name not in allowed:
                self.error(line, f"@{name} is not translatable to USS")

    def _check_selector(self, selector: str, line: int) -> None:
        for part in selector.split(","):
            part = part.strip()
            if "::" in part:
                self.error(line, f"pseudo-element in {part!r} - decorations must be real elements")
            if "[" in part:
                self.error(line, f"attribute selector in {part!r} - use a class")
            if re.search(r"[+~]", part):
                self.error(line, f"sibling combinator in {part!r} - use a class on the target")
            for pseudo in re.findall(r"(?<!:):([\w-]+)", part):
                if pseudo.startswith("nth"):
                    self.error(line, f":{pseudo} - USS has no structural pseudo-classes; use a class")
                elif pseudo not in ALLOWED_PSEUDO_CLASSES:
                    self.error(line, f":{pseudo} is not supported in USS")
            if self.proto and re.match(r"^(html|body)\b", part):
                continue  # browser scaffolding; maps to the UXML root element

    def _check_declarations(self, body: str, start_line: int, selector: str) -> None:
        offset = 0
        for decl in body.split(";"):
            line = start_line + body.count("\n", 0, offset)
            offset += len(decl) + 1
            decl = decl.strip()
            if not decl:
                continue
            if ":" not in decl:
                self.error(line, f"unparseable declaration {decl!r}")
                continue
            prop, value = (s.strip() for s in decl.split(":", 1))
            prop = prop.lower()
            self._check_property(prop, line, selector)
            self._check_value(prop, value, line)

    def _check_property(self, prop: str, line: int, selector: str) -> None:
        if prop.startswith("--"):
            return
        if prop in USS_PROPERTIES:
            return
        if self.proto and prop in CSS_PROTO_PROPERTIES:
            return
        if self.proto and re.match(r"^(html|body|\*)$", selector) and prop in {"margin", "padding", "background-color", "min-height", "display", "color"}:
            return
        advice = PROPERTY_ADVICE.get(prop)
        self.error(line, f"{prop}: {advice}" if advice else f"{prop} is not a USS property")

    def _check_value(self, prop: str, value: str, line: int) -> None:
        lowered = value.lower()
        if "calc(" in lowered:
            self.error(line, "calc() is not supported in USS - compute in C# or restructure")
        unit = BANNED_UNITS.search(lowered)
        if unit:
            self.error(line, f"unit {unit.group(1)!r} is not supported in USS - use px or %")
        if prop == "display":
            values = set(lowered.split())
            if not values <= DISPLAY_VALUES:
                self.error(line, f"display: {value} - USS only supports flex | none")
        if prop == "position" and lowered not in POSITION_VALUES:
            self.error(line, f"position: {value} - USS only supports relative | absolute")


def main(argv: list[str]) -> int:
    targets: list[Path] = []
    roots = [Path(a) for a in argv] if argv else DEFAULT_ROOTS
    for root in roots:
        if root.is_dir():
            targets += sorted(root.rglob("*.css")) + sorted(root.rglob("*.uss"))
        elif root.exists():
            targets.append(root)
        else:
            print(f"warning: {root} does not exist", file=sys.stderr)

    failed = False
    for target in targets:
        linter = Linter(target, is_proto_css=target.suffix == ".css")
        linter.lint(target.read_text())
        for line, message in linter.errors:
            failed = True
            try:
                shown = target.relative_to(REPO)
            except ValueError:
                shown = target
            print(f"{shown}:{line}: {message}")

    if not targets:
        print("no .css/.uss files found", file=sys.stderr)
        return 1
    if not failed:
        print(f"OK: {len(targets)} file(s) clean")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
