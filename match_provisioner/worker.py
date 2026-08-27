#!/usr/bin/env python3
"""Restricted, local-only SpacetimeDB match provisioner.

The worker deliberately uses only Python's standard library. It publishes an
already-built match WASM through SpacetimeDB's loopback management API, keeps
an exact database-identity ledger in SQLite, and never exposes its bearer token
or management endpoint to the game client.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import sqlite3
import subprocess
import sys
import threading
import time
from typing import Any, Callable, Protocol
import urllib.error
import urllib.parse
import urllib.request
import uuid

from .artifact_provenance import ArtifactProvenanceError, verify_artifact_manifest


DATABASE_NAME_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
IDENTIFIER_RE = re.compile(r"^[A-Za-z0-9_-]+$")
IDENTITY_RE = re.compile(r"^[0-9a-f]{64}$")
ACTIVE_TICKET_STATUSES = {"PENDING", "CLAIMED", "PROVISIONING", "READY"}
# Open worlds are provisioned and disposed exactly like matches; only their
# destination and their module artifact differ
# (docs/open-world-disposable-instances-2026-08-18.md).
QUEUE_KIND_OPEN_WORLD = "OPEN_WORLD"
DEFAULT_QUEUE_KIND = "UNRANKED"
LIVE_MATCH_PHASES = {"WAITING", "COUNTDOWN", "IN_PROGRESS"}
TERMINAL_MATCH_PHASES = {"ENDED", "ABORTED"}
ACTIVE_LEDGER_STATES = {
    "CLAIMED",
    "PROVISIONING",
    "BOOTSTRAPPED",
    "READY",
    "CLEANUP",
    "FAILURE_CLEANUP",
    "ORPHANED",
}
CAPACITY_LEDGER_STATES = ACTIVE_LEDGER_STATES - {"ORPHANED"}


class ProvisionerError(RuntimeError):
    """Expected operational failure that is safe to log without credentials."""


class ArtifactStaleError(ProvisionerError):
    """The cached server artifact no longer matches its source inputs."""


class SafetyError(ProvisionerError):
    """A destructive action failed its exact-target safety checks."""


def _env_int(name: str, default: int, minimum: int, maximum: int) -> int:
    raw = os.environ.get(name, str(default))
    try:
        value = int(raw)
    except ValueError as error:
        raise ProvisionerError(f"{name} must be an integer") from error
    if value < minimum or value > maximum:
        raise ProvisionerError(f"{name} must be between {minimum} and {maximum}")
    return value


def _validate_database_name(label: str, value: str) -> str:
    value = value.strip()
    if not value or len(value) > 63 or not DATABASE_NAME_RE.fullmatch(value):
        raise ProvisionerError(f"{label} is not a valid SpacetimeDB database name")
    return value


def _validate_management_url(value: str) -> str:
    parsed = urllib.parse.urlparse(value.strip())
    if parsed.scheme != "http" or parsed.username or parsed.password:
        raise ProvisionerError("management URL must be an unauthenticated http:// loopback URL")
    if parsed.hostname not in {"127.0.0.1", "localhost", "::1"}:
        raise ProvisionerError("management URL must resolve explicitly to a loopback host")
    if parsed.path not in {"", "/"} or parsed.query or parsed.fragment:
        raise ProvisionerError("management URL must not contain a path, query, or fragment")
    return value.rstrip("/")


def _validate_client_uri(value: str) -> str:
    parsed = urllib.parse.urlparse(value.strip())
    if parsed.scheme not in {"ws", "wss"} or not parsed.hostname:
        raise ProvisionerError("client URI must be a ws:// or wss:// endpoint")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ProvisionerError("client URI must not contain credentials, a query, or fragment")
    return value.rstrip("/")


@dataclasses.dataclass(frozen=True)
class Config:
    token: str
    management_url: str
    client_uri: str
    hub_database: str
    database_prefix: str
    wasm_path: Path
    artifact_manifest_path: Path
    state_path: Path
    max_concurrent_matches: int
    lease_seconds: int
    allocation_seconds: int
    hard_ttl_seconds: int
    reconcile_seconds: int
    cleanup_retry_seconds: int
    cleaned_retention_seconds: int
    map_id: str = "ARENA_MAP_01"
    match_build_id: str | None = None
    # The disposable open world runs the main server module, which is the one
    # that still compiles the open-world reducers. Optional so a PvP-only
    # environment keeps starting; open-world tickets then fail loudly.
    openworld_wasm_path: Path | None = None
    openworld_artifact_manifest_path: Path | None = None
    openworld_build_id: str | None = None
    # Deleting a database only unregisters it: SpacetimeDB leaves
    # replicas/<id>/ on disk forever, so a disposable instance is disposable in
    # the control plane and permanent on disk. Set to a local data directory to
    # reclaim that space after each disposal. Unset (the default) when the
    # server's storage is not on this machine.
    replica_gc_data_dir: Path | None = None

    @classmethod
    def from_environment(cls) -> "Config":
        root = Path(__file__).resolve().parents[1]
        token = os.environ.get("ARENA_PROVISIONER_TOKEN", "").strip()
        if not token:
            raise ProvisionerError("ARENA_PROVISIONER_TOKEN is required")
        wasm_path = Path(
            os.environ.get(
                "ARENA_PROVISIONER_MATCH_WASM",
                root / "match-server/target/wasm32-unknown-unknown/release/arena_match.opt.wasm",
            )
        ).resolve()
        if not wasm_path.is_file():
            raise ProvisionerError(
                f"prebuilt match WASM does not exist: {wasm_path}; build it before provisioning"
            )
        artifact_manifest_path = Path(
            os.environ.get(
                "ARENA_PROVISIONER_MATCH_MANIFEST",
                f"{wasm_path}.inputs.json",
            )
        ).resolve()
        state_path = Path(
            os.environ.get(
                "ARENA_PROVISIONER_STATE_DB",
                root / "Library/ArenaMatchProvisioner/state.sqlite3",
            )
        ).resolve()
        build_id = os.environ.get("ARENA_PROVISIONER_MATCH_BUILD_ID", "").strip() or None
        if build_id is not None and (
            len(build_id) > 96 or not IDENTIFIER_RE.fullmatch(build_id)
        ):
            raise ProvisionerError(
                "ARENA_PROVISIONER_MATCH_BUILD_ID must be a 1-96 character safe identifier"
            )
        map_id = os.environ.get("ARENA_PROVISIONER_MAP_ID", "ARENA_MAP_01").strip().upper()
        if not map_id or len(map_id) > 64 or not IDENTIFIER_RE.fullmatch(map_id):
            raise ProvisionerError(
                "ARENA_PROVISIONER_MAP_ID must be a 1-64 character safe identifier"
            )
        # An explicitly configured open-world artifact must exist; a missing
        # DEFAULT one only disables open-world tickets, so a PvP-only
        # environment still starts.
        configured_openworld = os.environ.get("ARENA_PROVISIONER_OPENWORLD_WASM", "").strip()
        openworld_wasm_path: Path | None = Path(
            configured_openworld
            or root / "server/target/wasm32-unknown-unknown/release/arena.opt.wasm"
        ).resolve()
        if not openworld_wasm_path.is_file():
            if configured_openworld:
                raise ProvisionerError(
                    f"prebuilt open-world WASM does not exist: {openworld_wasm_path}; "
                    "build it with ops/build-openworld-spacetimedb.sh"
                )
            openworld_wasm_path = None
        openworld_manifest_path: Path | None = None
        if openworld_wasm_path is not None:
            openworld_manifest_path = Path(
                os.environ.get(
                    "ARENA_PROVISIONER_OPENWORLD_MANIFEST",
                    f"{openworld_wasm_path}.inputs.json",
                )
            ).resolve()
        configured_gc_dir = os.environ.get(
            "ARENA_PROVISIONER_REPLICA_GC_DATA_DIR", ""
        ).strip()
        replica_gc_data_dir: Path | None = None
        if configured_gc_dir:
            replica_gc_data_dir = Path(configured_gc_dir)
            if not (replica_gc_data_dir / "replicas").is_dir():
                raise ProvisionerError(
                    "ARENA_PROVISIONER_REPLICA_GC_DATA_DIR does not look like a "
                    f"SpacetimeDB data directory: {replica_gc_data_dir}"
                )

        openworld_build_id = (
            os.environ.get("ARENA_PROVISIONER_OPENWORLD_BUILD_ID", "").strip() or None
        )
        if openworld_build_id is not None and (
            len(openworld_build_id) > 96 or not IDENTIFIER_RE.fullmatch(openworld_build_id)
        ):
            raise ProvisionerError(
                "ARENA_PROVISIONER_OPENWORLD_BUILD_ID must be a 1-96 character safe identifier"
            )

        config = cls(
            token=token,
            management_url=_validate_management_url(
                os.environ.get("ARENA_PROVISIONER_MANAGEMENT_URL", "http://127.0.0.1:3000")
            ),
            client_uri=_validate_client_uri(
                os.environ.get("ARENA_PROVISIONER_CLIENT_URI", "ws://127.0.0.1:3000")
            ),
            hub_database=_validate_database_name(
                "Hub database",
                os.environ.get("ARENA_PROVISIONER_HUB_DATABASE", "arena-hub-local"),
            ),
            database_prefix=_validate_database_name(
                "match database prefix",
                os.environ.get("ARENA_PROVISIONER_DATABASE_PREFIX", "arena-match"),
            ),
            wasm_path=wasm_path,
            artifact_manifest_path=artifact_manifest_path,
            state_path=state_path,
            max_concurrent_matches=_env_int(
                "ARENA_PROVISIONER_MAX_CONCURRENT_MATCHES", 4, 1, 128
            ),
            lease_seconds=_env_int("ARENA_PROVISIONER_LEASE_SECONDS", 90, 15, 110),
            allocation_seconds=_env_int(
                "ARENA_PROVISIONER_ALLOCATION_SECONDS", 120, 30, 900
            ),
            hard_ttl_seconds=_env_int(
                "ARENA_PROVISIONER_HARD_TTL_SECONDS", 1800, 120, 14_400
            ),
            reconcile_seconds=_env_int(
                "ARENA_PROVISIONER_RECONCILE_SECONDS", 30, 5, 300
            ),
            cleanup_retry_seconds=_env_int(
                "ARENA_PROVISIONER_CLEANUP_RETRY_SECONDS", 5, 1, 300
            ),
            cleaned_retention_seconds=_env_int(
                "ARENA_PROVISIONER_CLEANED_RETENTION_SECONDS", 86_400, 60, 604_800
            ),
            map_id=map_id,
            match_build_id=build_id,
            openworld_wasm_path=openworld_wasm_path,
            openworld_artifact_manifest_path=openworld_manifest_path,
            openworld_build_id=openworld_build_id,
            replica_gc_data_dir=replica_gc_data_dir,
        )
        if config.hard_ttl_seconds < config.allocation_seconds:
            raise ProvisionerError(
                "ARENA_PROVISIONER_HARD_TTL_SECONDS must not be shorter than the allocation window"
            )
        if config.reconcile_seconds >= config.lease_seconds:
            raise ProvisionerError(
                "ARENA_PROVISIONER_RECONCILE_SECONDS must be shorter than the lease duration"
            )
        return config


def normalize_identity(value: Any) -> str:
    value = unwrap_option(value)
    if isinstance(value, list) and len(value) == 1:
        value = value[0]
    if isinstance(value, dict) and "__identity__" in value:
        value = value["__identity__"]
    if not isinstance(value, str):
        raise ProvisionerError("SpacetimeDB identity has an unexpected representation")
    value = value.removeprefix("0x").lower()
    if not IDENTITY_RE.fullmatch(value):
        raise ProvisionerError("SpacetimeDB identity is malformed")
    return value


def identity_arg(identity: str) -> dict[str, str]:
    return {"__identity__": f"0x{normalize_identity(identity)}"}


def timestamp_arg(epoch_seconds: int) -> dict[str, int]:
    return {"__timestamp_micros_since_unix_epoch__": int(epoch_seconds) * 1_000_000}


def timestamp_microseconds(value: Any) -> int:
    value = unwrap_option(value)
    if isinstance(value, list) and len(value) == 1:
        return timestamp_microseconds(value[0])
    if isinstance(value, dict) and "__timestamp_micros_since_unix_epoch__" in value:
        raw = value["__timestamp_micros_since_unix_epoch__"]
        if isinstance(raw, (int, float)):
            return int(raw)
        value = raw
    if isinstance(value, (int, float)):
        return int(value)
    if isinstance(value, str):
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        return int(parsed.timestamp() * 1_000_000)
    raise ProvisionerError("SpacetimeDB timestamp has an unexpected representation")


def timestamp_seconds(value: Any) -> int:
    return timestamp_microseconds(value) // 1_000_000


def unwrap_option(value: Any) -> Any:
    # SpacetimeDB 2.1 SQL encodes sum values as [variant_index, payload].
    # The SDK Option schema orders `some` first and `none` second.
    if isinstance(value, list) and len(value) == 2 and value[0] in {0, 1}:
        return value[1] if value[0] == 0 else None
    if isinstance(value, dict) and set(value) == {"some"}:
        return value["some"]
    if isinstance(value, dict) and set(value) == {"none"}:
        return None
    return value


def decode_sql_rows(payload: Any) -> list[dict[str, Any]]:
    if not isinstance(payload, list):
        raise ProvisionerError("SpacetimeDB SQL response is not a statement list")
    decoded: list[dict[str, Any]] = []
    for statement in payload:
        if not isinstance(statement, dict):
            raise ProvisionerError("SpacetimeDB SQL statement response is malformed")
        elements = statement.get("schema", {}).get("elements", [])
        names = []
        for element in elements:
            name = element.get("name", {}).get("some")
            if not isinstance(name, str):
                raise ProvisionerError("SpacetimeDB SQL column is unnamed")
            names.append(name)
        for row in statement.get("rows", []):
            if not isinstance(row, list) or len(row) != len(names):
                raise ProvisionerError("SpacetimeDB SQL row does not match its schema")
            decoded.append(dict(zip(names, row, strict=True)))
    return decoded


class Api(Protocol):
    def sql(self, database: str, query: str) -> list[dict[str, Any]]: ...

    def call(self, database: str, reducer: str, arguments: list[Any]) -> None: ...

    def publish(self, database_name: str, wasm: bytes) -> dict[str, Any]: ...

    def database_info(self, name_or_identity: str) -> dict[str, Any] | None: ...

    def delete(self, database_identity: str) -> None: ...


class HttpApi:
    # The open-world module is two orders of magnitude larger than the match
    # module, so uploading it needs its own budget; every other management call
    # is small and should still fail fast.
    PUBLISH_TIMEOUT_SECONDS = 180

    def __init__(self, management_url: str, token: str, timeout_seconds: int = 30):
        self.management_url = management_url.rstrip("/")
        self.token = token
        self.timeout_seconds = timeout_seconds

    def _request(
        self,
        method: str,
        path: str,
        body: bytes | None = None,
        content_type: str | None = None,
        not_found_is_none: bool = False,
        timeout_seconds: int | None = None,
    ) -> bytes | None:
        headers = {"Authorization": f"Bearer {self.token}"}
        if content_type:
            headers["Content-Type"] = content_type
        request = urllib.request.Request(
            f"{self.management_url}{path}",
            data=body,
            headers=headers,
            method=method,
        )
        try:
            with urllib.request.urlopen(
                request, timeout=timeout_seconds or self.timeout_seconds
            ) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            response_body = error.read().decode("utf-8", errors="replace").strip()
            if error.code == 404 and not_found_is_none:
                return None
            detail = response_body[:600] or error.reason
            raise ProvisionerError(f"{method} {path} failed ({error.code}): {detail}") from error
        except urllib.error.URLError as error:
            raise ProvisionerError(f"{method} {path} could not reach the local server") from error

    @staticmethod
    def _database_path(database: str) -> str:
        return urllib.parse.quote(database, safe="")

    def sql(self, database: str, query: str) -> list[dict[str, Any]]:
        body = self._request(
            "POST",
            f"/v1/database/{self._database_path(database)}/sql",
            query.encode("utf-8"),
            "text/plain",
        )
        return decode_sql_rows(json.loads((body or b"[]").decode("utf-8")))

    def call(self, database: str, reducer: str, arguments: list[Any]) -> None:
        reducer_path = urllib.parse.quote(reducer, safe="")
        self._request(
            "POST",
            f"/v1/database/{self._database_path(database)}/call/{reducer_path}",
            json.dumps(arguments, separators=(",", ":")).encode("utf-8"),
            "application/json",
        )

    def publish(self, database_name: str, wasm: bytes) -> dict[str, Any]:
        body = self._request(
            "PUT",
            f"/v1/database/{self._database_path(database_name)}?clear=false",
            wasm,
            "application/wasm",
            timeout_seconds=self.PUBLISH_TIMEOUT_SECONDS,
        )
        payload = json.loads((body or b"{}").decode("utf-8"))
        success = payload.get("Success") if isinstance(payload, dict) else None
        if not isinstance(success, dict):
            raise ProvisionerError("database publish returned no Success payload")
        return success

    def database_info(self, name_or_identity: str) -> dict[str, Any] | None:
        body = self._request(
            "GET",
            f"/v1/database/{self._database_path(name_or_identity)}",
            not_found_is_none=True,
        )
        if body is None:
            return None
        payload = json.loads(body.decode("utf-8"))
        if not isinstance(payload, dict):
            raise ProvisionerError("database info response is malformed")
        return payload

    def delete(self, database_identity: str) -> None:
        self._request(
            "DELETE",
            f"/v1/database/{self._database_path(database_identity)}",
        )


@dataclasses.dataclass(frozen=True)
class Allocation:
    ticket_id: str
    player_identity: str
    lease_id: str
    match_id: str
    database_name: str
    database_identity: str | None
    state: str
    wasm_sha256: str
    created_at: int
    updated_at: int
    hard_expires_at: int
    ready_at: int | None
    terminal_phase: str | None
    failure_code: str | None
    cleanup_attempts: int
    next_retry_at: int
    last_error: str | None
    # Frozen from the Hub ticket at claim time. Cleanup and reconciliation run
    # long after a ticket disappears, so the ledger — not the Hub — decides
    # which module artifact and which destination this database belongs to.
    queue_kind: str = DEFAULT_QUEUE_KIND
    map_id: str = ""


class AllocationStore:
    _UPDATE_COLUMNS = {
        "database_identity",
        "state",
        "updated_at",
        "ready_at",
        "terminal_phase",
        "failure_code",
        "cleanup_attempts",
        "next_retry_at",
        "last_error",
    }

    def __init__(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.connection = sqlite3.connect(path)
        self.connection.row_factory = sqlite3.Row
        self.connection.execute("PRAGMA journal_mode=WAL")
        self.connection.execute("PRAGMA synchronous=FULL")
        self.connection.execute(
            """
            CREATE TABLE IF NOT EXISTS allocations (
                ticket_id TEXT PRIMARY KEY,
                player_identity TEXT NOT NULL,
                lease_id TEXT NOT NULL,
                match_id TEXT NOT NULL,
                database_name TEXT NOT NULL UNIQUE,
                database_identity TEXT UNIQUE,
                state TEXT NOT NULL,
                wasm_sha256 TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                hard_expires_at INTEGER NOT NULL,
                ready_at INTEGER,
                terminal_phase TEXT,
                failure_code TEXT,
                cleanup_attempts INTEGER NOT NULL DEFAULT 0,
                next_retry_at INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                queue_kind TEXT NOT NULL DEFAULT 'UNRANKED',
                map_id TEXT NOT NULL DEFAULT ''
            )
            """
        )
        existing_columns = {
            str(row["name"])
            for row in self.connection.execute("PRAGMA table_info(allocations)").fetchall()
        }
        # Ledgers written before open-world provisioning predate these columns.
        # Their rows are PvP by construction, which is exactly the default.
        if "queue_kind" not in existing_columns:
            self.connection.execute(
                "ALTER TABLE allocations ADD COLUMN queue_kind TEXT NOT NULL DEFAULT 'UNRANKED'"
            )
        if "map_id" not in existing_columns:
            self.connection.execute(
                "ALTER TABLE allocations ADD COLUMN map_id TEXT NOT NULL DEFAULT ''"
            )
        self.connection.commit()

    @staticmethod
    def _from_row(row: sqlite3.Row | None) -> Allocation | None:
        return Allocation(**dict(row)) if row is not None else None

    def get(self, ticket_id: str) -> Allocation | None:
        row = self.connection.execute(
            "SELECT * FROM allocations WHERE ticket_id = ?", (ticket_id,)
        ).fetchone()
        return self._from_row(row)

    def create(self, allocation: Allocation) -> Allocation:
        self.connection.execute(
            """
            INSERT OR IGNORE INTO allocations (
                ticket_id, player_identity, lease_id, match_id, database_name,
                database_identity, state, wasm_sha256, created_at, updated_at,
                hard_expires_at, ready_at, terminal_phase, failure_code,
                cleanup_attempts, next_retry_at, last_error, queue_kind, map_id
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            dataclasses.astuple(allocation),
        )
        self.connection.commit()
        existing = self.get(allocation.ticket_id)
        if existing is None:
            raise ProvisionerError("could not persist allocation ledger row")
        return existing

    def update(self, ticket_id: str, **values: Any) -> Allocation:
        unknown = set(values) - self._UPDATE_COLUMNS
        if unknown:
            raise ProvisionerError(f"unsupported allocation update columns: {sorted(unknown)}")
        if not values:
            existing = self.get(ticket_id)
            if existing is None:
                raise ProvisionerError("allocation ledger row disappeared")
            return existing
        assignments = ", ".join(f"{column} = ?" for column in values)
        self.connection.execute(
            f"UPDATE allocations SET {assignments} WHERE ticket_id = ?",
            [*values.values(), ticket_id],
        )
        self.connection.commit()
        updated = self.get(ticket_id)
        if updated is None:
            raise ProvisionerError("allocation ledger row disappeared")
        return updated

    def active(self) -> list[Allocation]:
        placeholders = ",".join("?" for _ in ACTIVE_LEDGER_STATES)
        rows = self.connection.execute(
            f"SELECT * FROM allocations WHERE state IN ({placeholders}) ORDER BY created_at",
            tuple(sorted(ACTIVE_LEDGER_STATES)),
        ).fetchall()
        return [self._from_row(row) for row in rows if row is not None]

    def prune_cleaned(self, cutoff: int) -> int:
        cursor = self.connection.execute(
            "DELETE FROM allocations WHERE state = 'CLEANED' AND updated_at < ?", (cutoff,)
        )
        self.connection.commit()
        return cursor.rowcount

    def counts(self) -> dict[str, int]:
        rows = self.connection.execute(
            "SELECT state, COUNT(*) AS count FROM allocations GROUP BY state ORDER BY state"
        ).fetchall()
        return {str(row["state"]): int(row["count"]) for row in rows}


def allocation_keys(ticket_id: str, database_prefix: str) -> tuple[str, str, int]:
    digest = hashlib.sha256(ticket_id.encode("utf-8")).hexdigest()
    database_name = f"{database_prefix}-{digest[:24]}"
    match_id = f"match-{digest[:24]}"
    seed = int(digest[24:40], 16)
    return database_name, match_id, seed


def ticket_log_id(ticket_id: str) -> str:
    return hashlib.sha256(ticket_id.encode("utf-8")).hexdigest()[:12]


def log_event(event: str, **fields: Any) -> None:
    payload = {"event": event, "time": int(time.time()), **fields}
    print(json.dumps(payload, sort_keys=True, separators=(",", ":")), flush=True)


class HubWakeupSubscriber:
    """Coalescing event source backed by a SpacetimeDB CLI subscription."""

    RESTART_SECONDS = 5.0
    QUERY = "SELECT * FROM provisioner_wakeup"

    def __init__(self, server: str, database: str):
        self.server = server
        self.database = database
        self._wakeup = threading.Event()
        self._stop = threading.Event()
        self._process_lock = threading.Lock()
        self._process: subprocess.Popen[str] | None = None
        self._thread = threading.Thread(
            target=self._run,
            name="arena-hub-wakeup",
            daemon=True,
        )

    def command(self) -> list[str]:
        return [
            "spacetime",
            "subscribe",
            "--yes",
            "--confirmed",
            "true",
            "--print-initial-update",
            "--server",
            self.server,
            self.database,
            self.QUERY,
        ]

    def start(self) -> None:
        self._thread.start()

    def wait(self, timeout_seconds: float, stopping: threading.Event) -> bool:
        deadline = time.monotonic() + timeout_seconds
        while not stopping.is_set():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return False
            if self._wakeup.wait(min(0.25, remaining)):
                self._wakeup.clear()
                return True
        return False

    def request_stop(self) -> None:
        self._stop.set()

    def close(self) -> None:
        self.request_stop()
        with self._process_lock:
            process = self._process
        if process is not None and process.poll() is None:
            process.terminate()
        self._thread.join(timeout=2.0)
        if self._thread.is_alive():
            with self._process_lock:
                process = self._process
            if process is not None and process.poll() is None:
                process.kill()
            self._thread.join(timeout=2.0)

    def _run(self) -> None:
        while not self._stop.is_set():
            process: subprocess.Popen[str] | None = None
            try:
                process = subprocess.Popen(
                    self.command(),
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    bufsize=1,
                )
                with self._process_lock:
                    self._process = process
                log_event("wakeup_subscription_started", database=self.database)
                if process.stdout is None:
                    raise ProvisionerError("wakeup subscription has no output stream")
                for line in process.stdout:
                    if self._stop.is_set():
                        break
                    # Subscription table updates are emitted as one-line JSON.
                    # CLI notices and warnings are intentionally not wakeups.
                    if line.lstrip().startswith("{"):
                        self._wakeup.set()
                exit_code = process.wait()
                if not self._stop.is_set():
                    log_event(
                        "wakeup_subscription_disconnected",
                        exit_code=exit_code,
                        retry_seconds=self.RESTART_SECONDS,
                    )
            except (OSError, ProvisionerError) as error:
                if not self._stop.is_set():
                    log_event(
                        "wakeup_subscription_failed",
                        error=str(error)[:400],
                        retry_seconds=self.RESTART_SECONDS,
                    )
            finally:
                with self._process_lock:
                    if self._process is process:
                        self._process = None
            self._stop.wait(self.RESTART_SECONDS)


@dataclasses.dataclass(frozen=True)
class Artifact:
    """One prebuilt module the worker may publish, plus its provenance."""

    label: str
    wasm_path: Path
    manifest_path: Path
    wasm: bytes
    sha256: str
    build_id: str

    @classmethod
    def load(
        cls,
        label: str,
        wasm_path: Path,
        manifest_path: Path,
        build_id: str | None,
    ) -> "Artifact":
        wasm = wasm_path.read_bytes()
        sha256 = hashlib.sha256(wasm).hexdigest()
        return cls(
            label=label,
            wasm_path=wasm_path,
            manifest_path=manifest_path,
            wasm=wasm,
            sha256=sha256,
            build_id=build_id or f"sha256-{sha256[:20]}",
        )


class Provisioner:
    def __init__(
        self,
        config: Config,
        api: Api,
        store: AllocationStore,
        clock: Callable[[], float] = time.time,
        lease_factory: Callable[[], str] | None = None,
    ):
        self.config = config
        self.api = api
        self.store = store
        self.clock = clock
        self.lease_factory = lease_factory or (
            lambda: f"lease-{uuid.uuid4().hex[:24]}"
        )
        self.match_artifact = Artifact.load(
            "match",
            config.wasm_path,
            config.artifact_manifest_path,
            config.match_build_id,
        )
        self.openworld_artifact: Artifact | None = None
        if config.openworld_wasm_path is not None:
            if config.openworld_artifact_manifest_path is None:
                raise ProvisionerError("open-world artifact has no provenance manifest")
            self.openworld_artifact = Artifact.load(
                "open-world",
                config.openworld_wasm_path,
                config.openworld_artifact_manifest_path,
                config.openworld_build_id,
            )
        for artifact in self._configured_artifacts():
            self._ensure_artifact_fresh(artifact)

    def _configured_artifacts(self) -> list[Artifact]:
        artifacts = [self.match_artifact]
        if self.openworld_artifact is not None:
            artifacts.append(self.openworld_artifact)
        return artifacts

    def _artifact_for(self, queue_kind: str) -> Artifact:
        if queue_kind != QUEUE_KIND_OPEN_WORLD:
            return self.match_artifact
        if self.openworld_artifact is None:
            raise ProvisionerError(
                "open-world provisioning requires ARENA_PROVISIONER_OPENWORLD_WASM; "
                "build the main server module before requesting a destination"
            )
        return self.openworld_artifact

    def _ensure_artifact_fresh(self, artifact: Artifact) -> None:
        try:
            verify_artifact_manifest(
                artifact.wasm_path,
                artifact.manifest_path,
                expected_wasm_sha256=artifact.sha256,
            )
        except ArtifactProvenanceError as error:
            raise ArtifactStaleError(f"{artifact.label} artifact: {error}") from error

    def run_once(self) -> dict[str, int]:
        now = int(self.clock())
        self.store.prune_cleaned(now - self.config.cleaned_retention_seconds)
        snapshot = self._hub_snapshot()
        service_identity = snapshot["service_identity"]

        for allocation in self.store.active():
            if allocation.next_retry_at > now:
                continue
            ticket = snapshot["tickets"].get(allocation.ticket_id)
            assignment = snapshot["assignments"].get(allocation.ticket_id)
            player = snapshot["players"].get(allocation.player_identity)
            combat_build_snapshot = snapshot["combat_build_snapshots"].get(
                allocation.ticket_id
            )
            try:
                self._reconcile_allocation(
                    allocation,
                    ticket,
                    assignment,
                    player,
                    combat_build_snapshot,
                    service_identity,
                    now,
                )
            except ProvisionerError as error:
                self._record_retry(allocation, error, now)

        active_count = sum(
            allocation.state in CAPACITY_LEDGER_STATES
            for allocation in self.store.active()
        )
        capacity = max(0, self.config.max_concurrent_matches - active_count)
        if capacity:
            pending = sorted(
                (
                    ticket
                    for ticket in snapshot["tickets"].values()
                    if ticket.get("status") == "PENDING"
                ),
                key=lambda row: timestamp_seconds(row["created_at"]),
            )
            for ticket in pending[:capacity]:
                player_identity = normalize_identity(ticket["player_identity"])
                player = snapshot["players"].get(player_identity)
                combat_build_snapshot = snapshot["combat_build_snapshots"].get(
                    str(ticket["ticket_id"])
                )
                try:
                    self._claim_and_provision(
                        ticket, player, combat_build_snapshot, service_identity, now
                    )
                except ProvisionerError as error:
                    log_event(
                        "claim_or_provision_failed",
                        ticket=ticket_log_id(str(ticket["ticket_id"])),
                        error=str(error)[:400],
                    )

        return self.store.counts()

    def _hub_snapshot(self) -> dict[str, Any]:
        config_rows = self.api.sql(self.config.hub_database, "SELECT * FROM hub_service_config")
        if len(config_rows) != 1:
            raise ProvisionerError("Hub must contain exactly one service configuration row")
        service_identity = normalize_identity(config_rows[0]["provisioner_identity"])
        tickets = {
            str(row["ticket_id"]): row
            for row in self.api.sql(self.config.hub_database, "SELECT * FROM match_ticket")
        }
        assignments = {
            str(row["ticket_id"]): row
            for row in self.api.sql(self.config.hub_database, "SELECT * FROM match_assignment")
        }
        players = {
            normalize_identity(row["identity"]): row
            for row in self.api.sql(self.config.hub_database, "SELECT * FROM hub_player")
        }
        combat_build_snapshots = {
            str(row["ticket_id"]): row
            for row in self.api.sql(
                self.config.hub_database,
                "SELECT * FROM match_player_combat_build_snapshot",
            )
        }
        return {
            "service_identity": service_identity,
            "tickets": tickets,
            "assignments": assignments,
            "players": players,
            "combat_build_snapshots": combat_build_snapshots,
        }

    def _ticket_destination(self, ticket: dict[str, Any], queue_kind: str) -> str:
        """The map or scene this ticket asks for.

        A match is always the process-wide authored arena map. An open world
        instead carries its destination per ticket, in the ticket column the
        Hub already had, so the pipeline needed no schema change.
        """
        if queue_kind != QUEUE_KIND_OPEN_WORLD:
            return self.config.map_id
        destination = str(ticket.get("format", "")).strip()
        if not destination or len(destination) > 64 or not IDENTIFIER_RE.fullmatch(destination):
            raise ProvisionerError("open-world ticket has no usable destination")
        return destination

    def _new_allocation(self, ticket: dict[str, Any], now: int) -> Allocation:
        ticket_id = str(ticket["ticket_id"])
        player_identity = normalize_identity(ticket["player_identity"])
        database_name, match_id, _ = allocation_keys(
            ticket_id, self.config.database_prefix
        )
        queue_kind = str(ticket.get("queue_kind", DEFAULT_QUEUE_KIND)).strip() or DEFAULT_QUEUE_KIND
        return Allocation(
            ticket_id=ticket_id,
            player_identity=player_identity,
            lease_id=self.lease_factory(),
            match_id=match_id,
            database_name=database_name,
            database_identity=None,
            state="CLAIMED",
            wasm_sha256=self._artifact_for(queue_kind).sha256,
            created_at=now,
            updated_at=now,
            hard_expires_at=now + self.config.hard_ttl_seconds,
            ready_at=None,
            terminal_phase=None,
            failure_code=None,
            cleanup_attempts=0,
            next_retry_at=0,
            last_error=None,
            queue_kind=queue_kind,
            map_id=self._ticket_destination(ticket, queue_kind),
        )

    def _claim_and_provision(
        self,
        ticket: dict[str, Any],
        player: dict[str, Any] | None,
        combat_build_snapshot: dict[str, Any] | None,
        service_identity: str,
        now: int,
    ) -> None:
        startup_started = time.perf_counter()
        ticket_created_micros = timestamp_microseconds(ticket["created_at"])
        timings_ms: dict[str, float] = {}
        allocation = self.store.get(str(ticket["ticket_id"]))
        if allocation is None:
            allocation = self.store.create(self._new_allocation(ticket, now))
        claim_started = time.perf_counter()
        try:
            self._claim_lease(allocation, now)
            timings_ms["hub_claim"] = self._elapsed_ms(claim_started)
            timings_ms["ticket_to_claim"] = max(
                0.0,
                round(
                    (self.clock() * 1_000_000 - ticket_created_micros) / 1_000.0,
                    3,
                ),
            )
        except ProvisionerError:
            timings_ms["hub_claim"] = self._elapsed_ms(claim_started)
            timings_ms["ticket_to_claim"] = max(
                0.0,
                round(
                    (self.clock() * 1_000_000 - ticket_created_micros) / 1_000.0,
                    3,
                ),
            )
            self.store.update(
                allocation.ticket_id,
                state="CLEANED",
                updated_at=now,
                last_error="ticket claim rejected",
            )
            self._log_startup_timing(
                allocation,
                ticket_created_micros,
                startup_started,
                timings_ms,
                outcome="failed",
                final_stage="hub_claim",
                database_published=False,
                bootstrap_called=False,
            )
            raise
        self._provision(
            allocation,
            ticket,
            player,
            combat_build_snapshot,
            service_identity,
            now,
            ticket_created_micros,
            startup_started,
            timings_ms,
        )

    @staticmethod
    def _elapsed_ms(started: float) -> float:
        return round((time.perf_counter() - started) * 1_000.0, 3)

    def _log_startup_timing(
        self,
        allocation: Allocation,
        ticket_created_micros: int,
        startup_started: float,
        timings_ms: dict[str, float],
        *,
        outcome: str,
        final_stage: str,
        database_published: bool,
        bootstrap_called: bool,
    ) -> None:
        ticket_elapsed_ms = max(
            0.0,
            round((self.clock() * 1_000_000 - ticket_created_micros) / 1_000.0, 3),
        )
        try:
            artifact = self._artifact_for(allocation.queue_kind)
        except ProvisionerError:
            artifact = self.match_artifact
        log_event(
            "match_startup_timing",
            ticket=ticket_log_id(allocation.ticket_id),
            match=allocation.match_id,
            outcome=outcome,
            final_stage=final_stage,
            queue_kind=allocation.queue_kind,
            match_build_id=artifact.build_id,
            ticket_elapsed_ms=ticket_elapsed_ms,
            provisioner_elapsed_ms=self._elapsed_ms(startup_started),
            wasm_bytes=len(artifact.wasm),
            database_published=database_published,
            bootstrap_called=bootstrap_called,
            timings_ms={key: round(value, 3) for key, value in timings_ms.items()},
        )

    def _claim_lease(self, allocation: Allocation, now: int) -> None:
        self.api.call(
            self.config.hub_database,
            "service_claim_ticket",
            [
                allocation.ticket_id,
                allocation.lease_id,
                timestamp_arg(now + self.config.lease_seconds),
            ],
        )

    def _mark_provisioning(self, allocation: Allocation, now: int) -> Allocation:
        self.api.call(
            self.config.hub_database,
            "service_mark_provisioning",
            [allocation.ticket_id, allocation.lease_id],
        )
        return self.store.update(
            allocation.ticket_id,
            state="PROVISIONING",
            updated_at=now,
            last_error=None,
            next_retry_at=0,
        )

    def _provision(
        self,
        allocation: Allocation,
        ticket: dict[str, Any],
        player: dict[str, Any] | None,
        combat_build_snapshot: dict[str, Any] | None,
        service_identity: str,
        now: int,
        ticket_created_micros: int,
        startup_started: float,
        timings_ms: dict[str, float],
    ) -> None:
        final_stage = "hub_mark_provisioning"
        database_published = False
        bootstrap_called = False
        try:
            stage_started = time.perf_counter()
            allocation = self._mark_provisioning(allocation, now)
            timings_ms["hub_mark_provisioning"] = self._elapsed_ms(stage_started)

            final_stage = "database_prepare"
            allocation, _, database_published = self._ensure_database(
                allocation,
                service_identity,
                now,
                timings_ms,
            )

            final_stage = "match_bootstrap"
            bootstrap_called = self._ensure_bootstrap(
                allocation,
                ticket,
                player,
                combat_build_snapshot,
                now,
                timings_ms,
            )
            stage_started = time.perf_counter()
            allocation = self.store.update(
                allocation.ticket_id,
                state="BOOTSTRAPPED",
                updated_at=now,
                last_error=None,
            )
            timings_ms["ledger_mark_bootstrapped"] = self._elapsed_ms(stage_started)
            # Publishing/bootstrap may consume most of a short lease. The Hub
            # reducer treats this same-work-attempt call as a renewal.
            final_stage = "hub_lease_renewal"
            stage_started = time.perf_counter()
            self._claim_lease(allocation, int(self.clock()))
            timings_ms["hub_lease_renewal"] = self._elapsed_ms(stage_started)
            final_stage = "hub_mark_ready"
            stage_started = time.perf_counter()
            self.api.call(
                self.config.hub_database,
                "service_mark_ready",
                [
                    allocation.ticket_id,
                    allocation.lease_id,
                    allocation.match_id,
                    self.config.client_uri,
                    allocation.database_identity,
                    self._artifact_for(allocation.queue_kind).build_id,
                    allocation.map_id or self.config.map_id,
                    timestamp_arg(allocation.hard_expires_at),
                ],
            )
            timings_ms["hub_mark_ready"] = self._elapsed_ms(stage_started)
            now = int(self.clock())
            stage_started = time.perf_counter()
            self.store.update(
                allocation.ticket_id,
                state="READY",
                updated_at=now,
                ready_at=now,
                next_retry_at=0,
                last_error=None,
            )
            timings_ms["ledger_mark_ready"] = self._elapsed_ms(stage_started)
            log_event(
                "match_ready",
                ticket=ticket_log_id(allocation.ticket_id),
                match=allocation.match_id,
                database_identity=allocation.database_identity,
            )
            self._log_startup_timing(
                allocation,
                ticket_created_micros,
                startup_started,
                timings_ms,
                outcome="ready",
                final_stage="ready",
                database_published=database_published,
                bootstrap_called=bootstrap_called,
            )
        except SafetyError as error:
            self._log_startup_timing(
                allocation,
                ticket_created_micros,
                startup_started,
                timings_ms,
                outcome="orphaned",
                final_stage=final_stage,
                database_published=database_published,
                bootstrap_called=bootstrap_called,
            )
            current = self.store.get(allocation.ticket_id) or allocation
            self._mark_orphaned(current, ticket, error, int(self.clock()))
            raise
        except ProvisionerError as error:
            self._log_startup_timing(
                allocation,
                ticket_created_micros,
                startup_started,
                timings_ms,
                outcome="failed",
                final_stage=final_stage,
                database_published=database_published,
                bootstrap_called=bootstrap_called,
            )
            current = self.store.get(allocation.ticket_id) or allocation
            if isinstance(error, ArtifactStaleError):
                failure_code = "ARTIFACT_STALE"
            else:
                failure_code = (
                    "PUBLISH_FAILED"
                    if current.database_identity is None
                    else "BOOTSTRAP_FAILED"
                )
            self.store.update(
                allocation.ticket_id,
                state="FAILURE_CLEANUP",
                failure_code=failure_code,
                updated_at=int(self.clock()),
                last_error=str(error)[:600],
                next_retry_at=0,
            )
            self._finish_failure_cleanup(
                self.store.get(allocation.ticket_id) or allocation,
                ticket,
                service_identity,
                int(self.clock()),
            )
            raise

    def _ensure_database(
        self,
        allocation: Allocation,
        service_identity: str,
        now: int,
        timings_ms: dict[str, float],
    ) -> tuple[Allocation, dict[str, Any], bool]:
        lookup_started = time.perf_counter()
        try:
            info = self._resolve_database(allocation, service_identity, now)
        finally:
            timings_ms["database_lookup"] = self._elapsed_ms(lookup_started)
        published = False
        if info is None:
            # Recheck after the Hub claim and immediately before publishing.
            # This closes the window where an edit could stale the artifact
            # while a ticket was moving from PENDING to PROVISIONING.
            artifact = self._artifact_for(allocation.queue_kind)
            self._ensure_artifact_fresh(artifact)
            publish_started = time.perf_counter()
            try:
                success = self.api.publish(allocation.database_name, artifact.wasm)
                published = True
            finally:
                timings_ms["database_publish"] = self._elapsed_ms(publish_started)
            published_identity = normalize_identity(success.get("database_identity"))
            allocation = self.store.update(
                allocation.ticket_id,
                database_identity=published_identity,
                updated_at=now,
            )
            verify_started = time.perf_counter()
            try:
                info = self._resolve_database(allocation, service_identity, now)
            finally:
                timings_ms["database_verify"] = self._elapsed_ms(verify_started)
            if info is None:
                raise ProvisionerError("published database cannot be resolved by identity")
        return allocation, info, published

    def _resolve_database(
        self, allocation: Allocation, service_identity: str, now: int
    ) -> dict[str, Any] | None:
        lookup = allocation.database_identity or allocation.database_name
        info = self.api.database_info(lookup)
        if info is None:
            return None
        actual_identity = normalize_identity(info.get("database_identity"))
        actual_owner = normalize_identity(info.get("owner_identity"))
        if allocation.database_identity is not None and (
            actual_identity != normalize_identity(allocation.database_identity)
        ):
            raise SafetyError("database name/identity no longer resolves to the recorded target")
        if actual_owner != normalize_identity(service_identity):
            raise SafetyError("database is not owned by the configured Hub provisioner identity")
        if allocation.database_identity is None:
            allocation = self.store.update(
                allocation.ticket_id,
                database_identity=actual_identity,
                updated_at=now,
            )
        return info

    def _ensure_bootstrap(
        self,
        allocation: Allocation,
        ticket: dict[str, Any],
        player: dict[str, Any] | None,
        combat_build_snapshot: dict[str, Any] | None,
        now: int,
        timings_ms: dict[str, float],
    ) -> bool:
        if allocation.database_identity is None:
            raise ProvisionerError("cannot bootstrap without an exact database identity")
        if player is None:
            raise ProvisionerError("Hub player snapshot is missing")
        if combat_build_snapshot is None:
            raise ProvisionerError("Hub combat-build snapshot is missing")
        if str(combat_build_snapshot.get("ticket_id", "")) != allocation.ticket_id:
            raise ProvisionerError("Hub combat-build snapshot belongs to another ticket")
        if normalize_identity(combat_build_snapshot.get("player_identity")) != (
            allocation.player_identity
        ):
            raise ProvisionerError("Hub combat-build snapshot belongs to another player")
        frozen_combat_build_json = self._frozen_combat_build_json(
            combat_build_snapshot
        )
        armor_set_id = str(combat_build_snapshot.get("armor_set_id", "")).strip()
        if not armor_set_id:
            raise ProvisionerError("Hub combat-build snapshot armor set is empty")
        display_name = str(player.get("display_name", "")).strip()
        if not display_name:
            raise ProvisionerError("Hub player display name is empty")
        lookup_started = time.perf_counter()
        try:
            config_rows = self.api.sql(
                allocation.database_identity, "SELECT * FROM match_bootstrap_config"
            )
        finally:
            timings_ms["bootstrap_lookup"] = self._elapsed_ms(lookup_started)
        if config_rows:
            self._validate_existing_bootstrap(
                allocation, config_rows[0], combat_build_snapshot
            )
            return False
        _, _, seed = allocation_keys(allocation.ticket_id, self.config.database_prefix)
        # Both bootstraps take the same frozen Hub build in the same order;
        # only the destination argument's vocabulary differs.
        # SpacetimeDB exposes the Rust `2v2` token as `2_v_2` on the wire.
        reducer = (
            "bootstrap_open_world_instance"
            if allocation.queue_kind == QUEUE_KIND_OPEN_WORLD
            else "bootstrap_unranked_2_v_2_bot_match"
        )
        arguments = [
            allocation.match_id,
            self._artifact_for(allocation.queue_kind).build_id,
            allocation.map_id or self.config.map_id,
            seed,
            timestamp_arg(now + self.config.allocation_seconds),
            identity_arg(allocation.player_identity),
            display_name,
            frozen_combat_build_json,
            armor_set_id,
        ]
        bootstrap_started = time.perf_counter()
        try:
            self.api.call(allocation.database_identity, reducer, arguments)
        finally:
            timings_ms["bootstrap_call"] = self._elapsed_ms(bootstrap_started)
        return True

    @staticmethod
    def _frozen_combat_build_json(snapshot: dict[str, Any]) -> str:
        try:
            schema_version = int(snapshot.get("contract_schema_version", 0))
            revision = int(snapshot.get("combat_build_revision", 0))
        except (TypeError, ValueError) as error:
            raise ProvisionerError(
                "Hub combat-build snapshot metadata is malformed"
            ) from error
        frozen_json = str(snapshot.get("combat_build_snapshot_json", ""))
        if schema_version <= 0 or revision <= 0 or not frozen_json:
            raise ProvisionerError("Hub combat-build snapshot metadata is incomplete")
        return frozen_json

    def _validate_existing_bootstrap(
        self,
        allocation: Allocation,
        match_config: dict[str, Any],
        combat_build_snapshot: dict[str, Any] | None,
    ) -> None:
        if (
            str(match_config.get("match_id")) != allocation.match_id
            or str(match_config.get("match_build_id"))
            != self._artifact_for(allocation.queue_kind).build_id
            or str(match_config.get("map_id")) != (allocation.map_id or self.config.map_id)
        ):
            raise SafetyError("existing database bootstrap belongs to different match work")
        if allocation.database_identity is None:
            raise SafetyError("existing bootstrap has no recorded database identity")
        if combat_build_snapshot is None:
            raise SafetyError("frozen Hub combat-build snapshot is missing")
        reservations = self.api.sql(
            allocation.database_identity, "SELECT * FROM match_reservation"
        )
        if len(reservations) != 1 or normalize_identity(
            reservations[0].get("player_identity")
        ) != allocation.player_identity:
            raise SafetyError("existing database reservation does not match the Hub ticket")
        reservation = reservations[0]
        try:
            frozen_json = self._frozen_combat_build_json(combat_build_snapshot)
            schema_version = int(combat_build_snapshot.get("contract_schema_version", 0))
            revision = int(combat_build_snapshot.get("combat_build_revision", 0))
            reserved_schema_version = int(
                reservation.get("contract_schema_version", 0)
            )
            reserved_revision = int(reservation.get("combat_build_revision", 0))
        except (ProvisionerError, TypeError, ValueError) as error:
            raise SafetyError("frozen Hub combat-build snapshot is invalid") from error
        if (
            str(reservation.get("combat_build_snapshot_json", "")) != frozen_json
            or reserved_schema_version != schema_version
            or reserved_revision != revision
            or str(reservation.get("armor_set_id", ""))
            != str(combat_build_snapshot.get("armor_set_id", ""))
        ):
            raise SafetyError(
                "existing database reservation combat build differs from frozen Hub snapshot"
            )

    def _reconcile_allocation(
        self,
        allocation: Allocation,
        ticket: dict[str, Any] | None,
        assignment: dict[str, Any] | None,
        player: dict[str, Any] | None,
        combat_build_snapshot: dict[str, Any] | None,
        service_identity: str,
        now: int,
    ) -> None:
        if allocation.wasm_sha256 != self._artifact_for(allocation.queue_kind).sha256:
            self._mark_orphaned(
                allocation,
                ticket,
                SafetyError("active allocation was created by a different match WASM build"),
                now,
            )
            return
        if allocation.state == "ORPHANED":
            # Safety failures are intentionally sticky. Automatic deletion or
            # reassignment after an ownership/identity mismatch would defeat
            # the exact-target guard the ledger exists to provide. The Hub
            # ticket is independent client-facing state, though, and must be
            # terminal so a quarantined database cannot block matchmaking.
            self._close_orphaned_ticket(allocation, ticket)
            info = self.api.database_info(
                allocation.database_identity or allocation.database_name
            )
            if info is None:
                self.store.update(
                    allocation.ticket_id,
                    state="CLEANED",
                    updated_at=now,
                    terminal_phase="ORPHAN_DISAPPEARED",
                    next_retry_at=0,
                )
            return
        if allocation.state == "FAILURE_CLEANUP":
            self._finish_failure_cleanup(allocation, ticket, service_identity, now)
            return

        try:
            info = self._resolve_database(allocation, service_identity, now)
        except SafetyError as error:
            self._mark_orphaned(allocation, ticket, error, now)
            return

        if info is None:
            if ticket is not None and str(ticket.get("status")) in {
                "PENDING",
                "CLAIMED",
                "PROVISIONING",
            }:
                self._claim_lease(allocation, now)
                startup_started = time.perf_counter()
                self._provision(
                    allocation,
                    ticket,
                    player,
                    combat_build_snapshot,
                    service_identity,
                    now,
                    timestamp_microseconds(ticket["created_at"]),
                    startup_started,
                    {},
                )
                return
            if ticket is not None and str(ticket.get("status")) == "READY":
                self.api.call(
                    self.config.hub_database,
                    "service_close_ticket",
                    [allocation.ticket_id],
                )
            self.store.update(
                allocation.ticket_id,
                state="CLEANED",
                updated_at=now,
                terminal_phase="MISSING",
                next_retry_at=0,
            )
            return


        if allocation.database_identity is None:
            allocation = self.store.get(allocation.ticket_id) or allocation
        config_rows = self.api.sql(
            allocation.database_identity or allocation.database_name,
            "SELECT * FROM match_bootstrap_config",
        )
        if not config_rows:
            if ticket is not None and str(ticket.get("status")) in {
                "PENDING",
                "CLAIMED",
                "PROVISIONING",
            } and now < allocation.hard_expires_at:
                self._claim_lease(allocation, now)
                startup_started = time.perf_counter()
                self._provision(
                    allocation,
                    ticket,
                    player,
                    combat_build_snapshot,
                    service_identity,
                    now,
                    timestamp_microseconds(ticket["created_at"]),
                    startup_started,
                    {},
                )
            else:
                self._cleanup_normal(
                    allocation, ticket, service_identity, now, "UNBOOTSTRAPPED"
                )
            return

        match_config = config_rows[0]
        try:
            self._validate_existing_bootstrap(
                allocation, match_config, combat_build_snapshot
            )
        except SafetyError as error:
            self._mark_orphaned(allocation, ticket, error, now)
            return
        phase = str(match_config.get("phase"))
        if phase in TERMINAL_MATCH_PHASES:
            self._cleanup_normal(allocation, ticket, service_identity, now, phase)
            return
        if phase not in LIVE_MATCH_PHASES:
            self._mark_orphaned(
                allocation,
                ticket,
                SafetyError(f"unknown match phase {phase}"),
                now,
            )
            return

        allocation_expired = (
            phase == "WAITING"
            and timestamp_seconds(match_config["allocation_expires_at"]) <= now
        )
        ticket_status = str(ticket.get("status")) if ticket is not None else "MISSING"
        if now >= allocation.hard_expires_at or allocation_expired:
            reason = "HARD_TTL_EXPIRED" if now >= allocation.hard_expires_at else "ALLOCATION_EXPIRED"
            self._abort_if_live(allocation, phase, reason)
            self._cleanup_normal(allocation, ticket, service_identity, now, reason)
            return
        if ticket_status not in ACTIVE_TICKET_STATUSES:
            self._abort_if_live(allocation, phase, "HUB_TICKET_TERMINAL")
            self._cleanup_normal(
                allocation, ticket, service_identity, now, "HUB_TICKET_TERMINAL"
            )
            return

        if ticket_status == "READY":
            if assignment is None:
                self._mark_orphaned(
                    allocation,
                    ticket,
                    SafetyError("ready ticket has no assignment"),
                    now,
                )
                return
            assigned_identity = normalize_identity(assignment.get("database_identity"))
            if assigned_identity != normalize_identity(allocation.database_identity):
                self._mark_orphaned(
                    allocation,
                    ticket,
                    SafetyError("Hub assignment targets a different database"),
                    now,
                )
                return
            if str(assignment.get("match_id")) != allocation.match_id:
                self._mark_orphaned(
                    allocation,
                    ticket,
                    SafetyError("Hub assignment has a different match id"),
                    now,
                )
                return
            self.store.update(
                allocation.ticket_id,
                state="READY",
                updated_at=now,
                ready_at=allocation.ready_at or now,
                next_retry_at=0,
                last_error=None,
            )
            return

        self._claim_lease(allocation, now)
        self.api.call(
            self.config.hub_database,
            "service_mark_provisioning",
            [allocation.ticket_id, allocation.lease_id],
        )
        self.api.call(
            self.config.hub_database,
            "service_mark_ready",
            [
                allocation.ticket_id,
                allocation.lease_id,
                allocation.match_id,
                self.config.client_uri,
                allocation.database_identity,
                self._artifact_for(allocation.queue_kind).build_id,
                allocation.map_id or self.config.map_id,
                timestamp_arg(allocation.hard_expires_at),
            ],
        )
        self.store.update(
            allocation.ticket_id,
            state="READY",
            updated_at=now,
            ready_at=allocation.ready_at or now,
            next_retry_at=0,
            last_error=None,
        )

    def _abort_if_live(self, allocation: Allocation, phase: str, reason: str) -> None:
        if phase in TERMINAL_MATCH_PHASES or allocation.database_identity is None:
            return
        self.api.call(
            allocation.database_identity,
            "abort_match",
            [reason],
        )

    def _delete_exact_database(
        self, allocation: Allocation, service_identity: str, now: int
    ) -> None:
        info = self._resolve_database(allocation, service_identity, now)
        if info is None:
            return
        if allocation.database_identity is None:
            allocation = self.store.get(allocation.ticket_id) or allocation
        exact_identity = allocation.database_identity
        if exact_identity is None:
            raise SafetyError("cleanup has no exact database identity")
        self.api.delete(exact_identity)
        if self.api.database_info(exact_identity) is not None:
            raise ProvisionerError("database still resolves after delete")
        self._reclaim_replica_disk()

    def _reclaim_replica_disk(self) -> None:
        """Return the deleted instance's bytes to the filesystem.

        Best effort by design: the database is already gone, so a GC that fails
        must never turn a completed disposal into a retry. It only ever removes
        replicas whose database the server no longer resolves.
        """
        data_dir = self.config.replica_gc_data_dir
        if data_dir is None:
            return
        script = Path(__file__).resolve().parents[1] / "ops" / "gc-orphaned-replicas.py"
        if not script.is_file():
            return
        try:
            result = subprocess.run(
                [
                    sys.executable,
                    str(script),
                    "--data-dir",
                    str(data_dir),
                    "--server-url",
                    self.config.management_url,
                    "--apply",
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )
        except (OSError, subprocess.SubprocessError) as error:
            log_event("replica_gc_failed", error=str(error))
            return
        if result.returncode != 0:
            log_event("replica_gc_failed", error=result.stderr.strip()[:400])
            return
        summary = next(
            (line for line in result.stdout.splitlines() if line.startswith("Reclaimed ")),
            None,
        )
        if summary:
            log_event("replica_gc_reclaimed", detail=summary)

    def _cleanup_normal(
        self,
        allocation: Allocation,
        ticket: dict[str, Any] | None,
        service_identity: str,
        now: int,
        terminal_phase: str,
    ) -> None:
        try:
            self.store.update(
                allocation.ticket_id,
                state="CLEANUP",
                updated_at=now,
                terminal_phase=terminal_phase,
            )
            self._delete_exact_database(allocation, service_identity, now)
            if ticket is not None and str(ticket.get("status")) != "FAILED":
                self.api.call(
                    self.config.hub_database,
                    "service_close_ticket",
                    [allocation.ticket_id],
                )
            self.store.update(
                allocation.ticket_id,
                state="CLEANED",
                updated_at=now,
                next_retry_at=0,
                last_error=None,
            )
            log_event(
                "match_cleaned",
                ticket=ticket_log_id(allocation.ticket_id),
                match=allocation.match_id,
                terminal_phase=terminal_phase,
            )
        except SafetyError as error:
            self._mark_orphaned(allocation, ticket, error, now)
        except ProvisionerError as error:
            self._record_retry(allocation, error, now, state="CLEANUP")

    def _finish_failure_cleanup(
        self,
        allocation: Allocation,
        ticket: dict[str, Any] | None,
        service_identity: str,
        now: int,
    ) -> None:
        failure_code = allocation.failure_code or "PROVISIONING_FAILED"
        try:
            self._delete_exact_database(allocation, service_identity, now)
            ticket_status = str(ticket.get("status")) if ticket is not None else "MISSING"
            if ticket_status in {"PENDING", "CLAIMED", "PROVISIONING"}:
                self._claim_lease(allocation, now)
                self.api.call(
                    self.config.hub_database,
                    "service_mark_failed",
                    [allocation.ticket_id, allocation.lease_id, failure_code],
                )
            elif ticket_status == "READY":
                self.api.call(
                    self.config.hub_database,
                    "service_close_ticket",
                    [allocation.ticket_id],
                )
            self.store.update(
                allocation.ticket_id,
                state="CLEANED",
                updated_at=now,
                terminal_phase="FAILED",
                next_retry_at=0,
            )
        except SafetyError as error:
            self._mark_orphaned(allocation, ticket, error, now)
        except ProvisionerError as error:
            self._record_retry(allocation, error, now, state="FAILURE_CLEANUP")

    def _mark_orphaned(
        self,
        allocation: Allocation,
        ticket: dict[str, Any] | None,
        error: Exception,
        now: int,
    ) -> None:
        self.store.update(
            allocation.ticket_id,
            state="ORPHANED",
            updated_at=now,
            cleanup_attempts=allocation.cleanup_attempts + 1,
            next_retry_at=now + self.config.cleanup_retry_seconds,
            last_error=str(error)[:600],
        )
        log_event(
            "orphan_detected",
            ticket=ticket_log_id(allocation.ticket_id),
            match=allocation.match_id,
            error=str(error)[:400],
        )
        self._close_orphaned_ticket(allocation, ticket)

    def _close_orphaned_ticket(
        self, allocation: Allocation, ticket: dict[str, Any] | None
    ) -> None:
        if ticket is None or str(ticket.get("status")) not in ACTIVE_TICKET_STATUSES:
            return

        try:
            self.api.call(
                self.config.hub_database,
                "service_close_ticket",
                [allocation.ticket_id],
            )
            log_event(
                "orphan_ticket_closed",
                ticket=ticket_log_id(allocation.ticket_id),
                match=allocation.match_id,
            )
        except ProvisionerError as error:
            # Keep the allocation quarantined and retry on the next reconciliation
            # pass. Database deletion remains forbidden while the safety failure is
            # unresolved, but a transient Hub failure must not make that ticket
            # permanently client-visible.
            log_event(
                "orphan_ticket_close_failed",
                ticket=ticket_log_id(allocation.ticket_id),
                match=allocation.match_id,
                error=str(error)[:400],
            )

    def _record_retry(
        self,
        allocation: Allocation,
        error: Exception,
        now: int,
        state: str | None = None,
    ) -> None:
        self.store.update(
            allocation.ticket_id,
            state=state or allocation.state,
            updated_at=now,
            cleanup_attempts=allocation.cleanup_attempts + 1,
            next_retry_at=now + self.config.cleanup_retry_seconds,
            last_error=str(error)[:600],
        )
        log_event(
            "operation_retry_scheduled",
            ticket=ticket_log_id(allocation.ticket_id),
            match=allocation.match_id,
            state=state or allocation.state,
            error=str(error)[:400],
        )


class ProcessLock:
    def __init__(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.file = path.open("a+", encoding="utf-8")
        try:
            fcntl.flock(self.file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ProvisionerError("another local match provisioner already owns the lock") from error

    def close(self) -> None:
        fcntl.flock(self.file.fileno(), fcntl.LOCK_UN)
        self.file.close()


def _run(config: Config, once: bool) -> int:
    lock = ProcessLock(config.state_path.with_suffix(".lock"))
    store = AllocationStore(config.state_path)
    api = HttpApi(config.management_url, config.token)
    provisioner = Provisioner(config, api, store)
    stopping = threading.Event()
    wakeup = None if once else HubWakeupSubscriber(
        config.management_url,
        config.hub_database,
    )

    def request_stop(_signum: int, _frame: Any) -> None:
        stopping.set()
        if wakeup is not None:
            wakeup.request_stop()

    signal.signal(signal.SIGINT, request_stop)
    signal.signal(signal.SIGTERM, request_stop)
    try:
        if wakeup is not None:
            wakeup.start()
        trigger = "startup"
        while not stopping.is_set():
            try:
                counts = provisioner.run_once()
                log_event("provisioner_cycle", counts=counts, trigger=trigger)
            except ProvisionerError as error:
                log_event(
                    "provisioner_cycle_failed",
                    error=str(error)[:400],
                    trigger=trigger,
                )
                if once:
                    return 1
            if once:
                return 0
            if wakeup is None:
                return 1
            received_wakeup = wakeup.wait(config.reconcile_seconds, stopping)
            if stopping.is_set():
                break
            trigger = "subscription" if received_wakeup else "reconciliation"
            if received_wakeup:
                log_event("provisioner_wakeup_received")
    finally:
        if wakeup is not None:
            wakeup.close()
        lock.close()
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command")
    run_parser = subparsers.add_parser("run", help="subscribe and reconcile match work")
    run_parser.add_argument("--once", action="store_true", help="run one cycle and exit")
    subparsers.add_parser("status", help="print local allocation-ledger counts")
    args = parser.parse_args(argv)
    command = args.command or "run"
    try:
        config = Config.from_environment()
        if command == "status":
            print(json.dumps(AllocationStore(config.state_path).counts(), sort_keys=True))
            return 0
        return _run(config, bool(getattr(args, "once", False)))
    except ProvisionerError as error:
        print(f"match provisioner: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
