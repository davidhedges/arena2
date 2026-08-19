#!/usr/bin/env python3
"""Reclaim SpacetimeDB replica directories whose database no longer exists.

`spacetime delete` (and therefore the match provisioner's disposal path in
match_provisioner/worker.py) removes a database from the control plane but
leaves `<data-dir>/replicas/<replica-id>/` on disk forever. Disposable open
worlds and matches are a control-plane fact, not a disk fact: measured
2026-08-19, 141 replica directories were still resident against 2 live
databases, holding 11 GB.

Liveness is decided per replica against the running server, so this never
depends on which identity owns a database:

  1. read the replica id and database identity out of the replica's snapshot,
  2. ask the server whether that identity still resolves,
  3. delete only when the server positively reports it gone.

Every ambiguity keeps the directory. A replica with no snapshot yet, an
unparseable snapshot, a directory the server currently holds open, or one
younger than --min-age-seconds is always retained.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

IDENTITY_BYTES = 32
# The identity/replica-id pair sits in the snapshot preamble; cap the scan so a
# malformed or truncated file cannot turn into a long read.
SNAPSHOT_SCAN_LIMIT = 4096
DEFAULT_MIN_AGE_SECONDS = 300
DEFAULT_CONFIRM_DELAY_SECONDS = 3.0


@dataclass
class Verdict:
    replica_id: int
    path: Path
    size_bytes: int
    state: str  # "orphaned" | "suspect" | "live" | "retained"
    detail: str
    identities: list[str] = field(default_factory=list)
    held_open: bool = False


def parse_args() -> argparse.Namespace:
    default_data = Path(
        os.environ.get("SPACETIME_DATA")
        or Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share"))
        / "spacetime/data"
    )
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--data-dir", type=Path, default=default_data)
    parser.add_argument(
        "--server-url",
        default=os.environ.get("ARENA_SPACETIME_URL", "http://127.0.0.1:3000"),
        help="running SpacetimeDB whose control plane decides liveness",
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="delete orphaned replicas (default: report only)",
    )
    parser.add_argument(
        "--min-age-seconds",
        type=int,
        default=DEFAULT_MIN_AGE_SECONDS,
        help=(
            "retain recently modified replicas that carry no identity to check "
            f"against the control plane (default: {DEFAULT_MIN_AGE_SECONDS})"
        ),
    )
    parser.add_argument(
        "--confirm-delay-seconds",
        type=float,
        default=DEFAULT_CONFIRM_DELAY_SECONDS,
        help=(
            "pause before re-checking a database that looks gone, so one still "
            f"registering is never removed (default: {DEFAULT_CONFIRM_DELAY_SECONDS})"
        ),
    )
    return parser.parse_args()


def snapshot_files(replica_dir: Path) -> list[Path]:
    snapshots = replica_dir / "snapshots"
    if not snapshots.is_dir():
        return []
    return sorted(snapshots.rglob("*.snapshot_bsatn"))


def candidate_identities(snapshot: Path, replica_id: int) -> list[str]:
    """Identities stored next to `replica_id` in the snapshot preamble.

    The replica id is read from the directory name and used as the anchor, so
    this does not hard-code a field offset: any 32 bytes immediately preceding
    a u64 that equals this replica's own id is a candidate. Both byte orders
    are offered because only the server can confirm which one is real.
    """
    try:
        with snapshot.open("rb") as handle:
            head = handle.read(SNAPSHOT_SCAN_LIMIT)
    except OSError:
        return []

    anchor = struct.pack("<Q", replica_id)
    found: list[str] = []
    start = IDENTITY_BYTES
    while True:
        at = head.find(anchor, start)
        if at < 0 or at < IDENTITY_BYTES:
            break
        raw = head[at - IDENTITY_BYTES : at]
        for candidate in (raw[::-1].hex(), raw.hex()):
            if candidate not in found:
                found.append(candidate)
        start = at + 1
    return found


def database_exists(server_url: str, identity: str) -> bool | None:
    """True/False from the control plane, or None when it could not answer."""
    url = f"{server_url.rstrip('/')}/v1/database/{identity}"
    try:
        with urllib.request.urlopen(url, timeout=15) as response:
            return 200 <= response.status < 300
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        return None
    except (urllib.error.URLError, TimeoutError, OSError):
        return None


def server_open_paths() -> set[str] | None:
    """Files the running server holds open, or None if that cannot be listed."""
    try:
        pids = subprocess.run(
            ["pgrep", "-f", "spacetimedb-standalone"],
            capture_output=True,
            text=True,
            timeout=15,
        ).stdout.split()
        if not pids:
            return set()
        listing = subprocess.run(
            ["lsof", "-p", ",".join(pids), "-Fn"],
            capture_output=True,
            text=True,
            timeout=60,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return {line[1:] for line in listing.splitlines() if line.startswith("n/")}


def directory_size(path: Path) -> int:
    total = 0
    for root, _dirs, files in os.walk(path):
        for name in files:
            try:
                total += (Path(root) / name).stat().st_size
            except OSError:
                continue
    return total


def newest_mtime(path: Path) -> float:
    newest = path.stat().st_mtime
    for root, dirs, files in os.walk(path):
        for name in dirs + files:
            try:
                newest = max(newest, (Path(root) / name).stat().st_mtime)
            except OSError:
                continue
    return newest


def classify(
    replica_dir: Path,
    replica_id: int,
    args: argparse.Namespace,
    open_paths: set[str] | None,
    now: float,
) -> Verdict:
    size = directory_size(replica_dir)

    def keep(detail: str) -> Verdict:
        return Verdict(replica_id, replica_dir, size, "retained", detail)

    prefix = f"{replica_dir.resolve()}/"
    held_open = open_paths is not None and any(
        path.startswith(prefix) for path in open_paths
    )

    def unidentified(detail: str) -> Verdict:
        # Nothing identifies this directory, so fall back to weaker signals.
        if held_open:
            return Verdict(replica_id, replica_dir, size, "live", "server holds it open")
        age = now - newest_mtime(replica_dir)
        if age < args.min_age_seconds:
            return keep(f"{detail}; modified {int(age)}s ago")
        return keep(detail)

    snapshots = snapshot_files(replica_dir)
    if not snapshots:
        return unidentified("no snapshot to identify it by")

    identities: list[str] = []
    for snapshot in snapshots:
        for identity in candidate_identities(snapshot, replica_id):
            if identity not in identities:
                identities.append(identity)
    if not identities:
        return unidentified("no identity found in snapshot")

    unknown = False
    for identity in identities:
        exists = database_exists(args.server_url, identity)
        if exists is True:
            return Verdict(
                replica_id, replica_dir, size, "live", f"database {identity[:16]}… exists"
            )
        if exists is None:
            unknown = True
    if unknown:
        return keep("server did not answer for every candidate identity")

    # Not deleted yet: a database whose registration is still in flight also
    # answers 404. main() re-checks after a pause before anything is removed.
    verdict = Verdict(
        replica_id, replica_dir, size, "suspect", f"database {identities[0][:16]}… is gone"
    )
    verdict.identities = identities
    verdict.held_open = held_open
    return verdict


def human(size: int) -> str:
    value = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.1f} {unit}"
        value /= 1024
    return f"{value:.1f} GB"


def main() -> int:
    args = parse_args()
    data_dir: Path = args.data_dir.expanduser()
    replicas = data_dir / "replicas"

    if data_dir.resolve() in (Path("/"), Path.home().resolve()):
        print(f"Refusing unsafe data directory: {data_dir}", file=sys.stderr)
        return 2
    if not replicas.is_dir():
        print(f"No replica directory at {replicas}", file=sys.stderr)
        return 2

    # Without a control plane to ask, nothing can be proven dead.
    probe = database_exists(args.server_url, "00" * IDENTITY_BYTES)
    if probe is None:
        print(
            f"SpacetimeDB at {args.server_url} is not answering; start it before "
            "running this GC so liveness can be verified.",
            file=sys.stderr,
        )
        return 2

    open_paths = server_open_paths()
    now = time.time()

    verdicts: list[Verdict] = []
    for child in sorted(replicas.iterdir()):
        if not child.is_dir() or not re.fullmatch(r"\d+", child.name):
            continue
        verdicts.append(classify(child, int(child.name), args, open_paths, now))

    print(f"Replica GC  ({'delete' if args.apply else 'report only'})")
    print(f"  data dir: {data_dir}")
    print(f"  server:   {args.server_url}")
    print()

    # Confirm every suspect once more before it can be deleted.
    suspects = [v for v in verdicts if v.state == "suspect"]
    if suspects:
        time.sleep(args.confirm_delay_seconds)
        for verdict in suspects:
            still_gone, unknown = True, False
            for identity in verdict.identities:
                exists = database_exists(args.server_url, identity)
                if exists is True:
                    still_gone = False
                    verdict.state = "live"
                    verdict.detail = f"database {identity[:16]}… exists"
                    break
                if exists is None:
                    unknown = True
            if verdict.state == "live":
                continue
            if unknown or not still_gone:
                verdict.state = "retained"
                verdict.detail = "server did not confirm the database is gone"
                continue
            verdict.state = "orphaned"
            if verdict.held_open:
                verdict.detail += " (commitlog still open; frees on server restart)"

    orphans = [v for v in verdicts if v.state == "orphaned"]
    live = [v for v in verdicts if v.state == "live"]
    retained = [v for v in verdicts if v.state == "retained"]

    for verdict in verdicts:
        mark = {"orphaned": "ORPHAN", "live": "live  ", "retained": "keep  "}[verdict.state]
        print(f"  {mark} {verdict.replica_id:>9}  {human(verdict.size_bytes):>9}  {verdict.detail}")
    if not verdicts:
        print("  (no replica directories)")
    print()

    reclaimable = sum(v.size_bytes for v in orphans)
    print(
        f"{len(live)} live, {len(retained)} retained, {len(orphans)} orphaned "
        f"({human(reclaimable)} reclaimable)"
    )

    if not args.apply:
        if orphans:
            print("Re-run with --apply to delete the orphaned replicas.")
        return 0

    freed = 0
    for verdict in orphans:
        try:
            shutil.rmtree(verdict.path)
        except OSError as error:
            print(f"  failed to remove {verdict.path}: {error}", file=sys.stderr)
            continue
        freed += verdict.size_bytes
    print(f"Reclaimed {human(freed)} from {len(orphans)} replica directories.")
    if any("commitlog still open" in v.detail for v in orphans):
        print(
            "Some commitlogs were still held open by the running server; that "
            "portion returns when it restarts."
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
