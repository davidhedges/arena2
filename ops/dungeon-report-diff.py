#!/usr/bin/env python3
"""Diff two Dungeon Lab batch reports LEAF BY LEAF.

`ops/dungeon-port-ab.sh` compares a per-seed geometry vector — canonical hash,
accepted set, failure codes — which answers "is this the same dungeon". It does
NOT answer "what else moved", and every slice of the layered-topology work has
had to answer that by hand: a `resultHash` that moved says nothing about
whether a metric was added or a validation message changed, and a hash that
held says nothing about the fields outside the vector.

So this walks both reports as trees and reports every leaf that changed, was
added, or was removed, grouped by its path with the seed count and a couple of
examples. A slice whose only rows are "added: <the new metric>" is neutral in
the strongest sense available: no field present on both legs differs anywhere.

Usage:
    ops/dungeon-report-diff.py PRE.json POST.json [--max-examples 3]
                               [--ignore PATH ...]

Exit: 0 when every leaf present on both legs is equal, 1 otherwise. Added and
removed leaves do not fail it — a new metric is the normal reason to run this —
they are printed, and the caller decides.
"""
import argparse
import json
import sys
from collections import defaultdict

# Moves on every run and describes nothing about the dungeon. Ignored by
# default so a clean verdict means what it says; still printed, so it can
# never be silently ignored.
DEFAULT_IGNORED = ("report.generatedAtUtc",)


def leaves(node, path, out):
    """Flatten to {dotted path: value}. Lists index by position, which is what
    makes a re-ordered array visible rather than silently equal."""
    if isinstance(node, dict):
        for key, value in node.items():
            leaves(value, f"{path}.{key}" if path else key, out)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            leaves(value, f"{path}[{index}]", out)
    else:
        out[path] = node


def flatten(report):
    """Per-seed trees keyed by seed, plus the report-level remainder."""
    per_seed = {}
    for seed_report in report.get("seeds") or []:
        out = {}
        leaves(seed_report, "", out)
        per_seed[seed_report.get("seed")] = out

    top = dict(report)
    top.pop("seeds", None)
    out = {}
    leaves(top, "", out)
    return per_seed, out


def compare(pre, post, label, changed, added, removed):
    for key in pre.keys() | post.keys():
        if key not in post:
            removed[f"{label}{key}" if label else key].append(None)
        elif key not in pre:
            added[f"{label}{key}" if label else key].append(post[key])
        elif pre[key] != post[key]:
            changed[f"{label}{key}" if label else key].append((pre[key], post[key]))


def report_group(title, group, max_examples, show_values=True):
    if not group:
        print(f"  {title}: none")
        return
    print(f"  {title}: {len(group)} distinct path(s)")
    for path in sorted(group):
        hits = group[path]
        print(f"    {path}  ({len(hits)} occurrence(s))")
        if not show_values:
            continue
        for example in hits[:max_examples]:
            if isinstance(example, tuple):
                print(f"        {example[0]!r} -> {example[1]!r}")
            else:
                print(f"        {example!r}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("pre")
    parser.add_argument("post")
    parser.add_argument("--max-examples", type=int, default=3)
    parser.add_argument(
        "--ignore",
        action="append",
        default=[],
        help=f"leaf path that may differ without failing (default: {', '.join(DEFAULT_IGNORED)})",
    )
    args = parser.parse_args()
    ignored = set(DEFAULT_IGNORED) | set(args.ignore)

    with open(args.pre) as handle:
        pre_report = json.load(handle)
    with open(args.post) as handle:
        post_report = json.load(handle)

    pre_seeds, pre_top = flatten(pre_report)
    post_seeds, post_top = flatten(post_report)

    changed, added, removed = defaultdict(list), defaultdict(list), defaultdict(list)
    compare(pre_top, post_top, "report.", changed, added, removed)

    seed_leaves = 0
    for seed in sorted(pre_seeds.keys() | post_seeds.keys()):
        a = pre_seeds.get(seed, {})
        b = post_seeds.get(seed, {})
        seed_leaves += len(b or a)
        compare(a, b, "seed.", changed, added, removed)

    print(f"pre  {args.pre}")
    print(f"post {args.post}")
    print(f"seeds {len(post_seeds)}  per-seed leaves {seed_leaves}")
    print()
    report_group("CHANGED IN VALUE", changed, args.max_examples)
    report_group("ADDED", added, args.max_examples)
    report_group("REMOVED", removed, args.max_examples, show_values=False)

    material = {path: hits for path, hits in changed.items() if path not in ignored}
    if material:
        print(f"\nVERDICT: {len(material)} field(s) present on both legs differ.")
        return 1

    print(
        "\nVERDICT: zero leaves changed in value "
        f"(outside {', '.join(sorted(ignored))}). "
        f"{len(added)} added path(s), {len(removed)} removed path(s)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
