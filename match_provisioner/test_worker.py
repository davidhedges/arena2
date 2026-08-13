from __future__ import annotations

import contextlib
import dataclasses
import hashlib
import io
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from typing import Any

from match_provisioner.worker import (
    Allocation,
    AllocationStore,
    Config,
    HubWakeupSubscriber,
    Provisioner,
    ProvisionerError,
    allocation_keys,
    decode_sql_rows,
    identity_arg,
    normalize_identity,
    timestamp_arg,
    timestamp_microseconds,
    timestamp_seconds,
    unwrap_option,
    _validate_management_url,
)


SERVICE_IDENTITY = "11" * 32
PLAYER_ONE = "22" * 32
PLAYER_TWO = "33" * 32
OTHER_OWNER = "44" * 32


class FakeApi:
    def __init__(self):
        self.hub_database = "arena-hub-local"
        self.service_identity = SERVICE_IDENTITY
        self.tickets: dict[str, dict[str, Any]] = {}
        self.assignments: dict[str, dict[str, Any]] = {}
        self.players: dict[str, dict[str, Any]] = {}
        self.loadouts: dict[str, dict[str, Any]] = {}
        self.databases: dict[str, dict[str, Any]] = {}
        self.database_names: dict[str, str] = {}
        self.calls: list[tuple[str, str, list[Any]]] = []
        self.publish_count = 0
        self.delete_count = 0
        self.fail_publish_after_create = False
        self.fail_bootstrap = False
        self.delete_failures = 0
        self.close_ticket_failures = 0

    def add_ticket(self, ticket_id: str, player_identity: str, created_at: int = 1_000) -> None:
        self.tickets[ticket_id] = {
            "ticket_id": ticket_id,
            "player_identity": identity_arg(player_identity),
            "client_request_id": "request-0001",
            "queue_kind": "UNRANKED",
            "format": "2V2",
            "status": "PENDING",
            "created_at": timestamp_arg(created_at),
            "updated_at": timestamp_arg(created_at),
            "expires_at": timestamp_arg(created_at + 120),
            "lease_owner": {"none": []},
            "lease_until": {"none": []},
            "failure_code": {"none": []},
        }
        self.players[player_identity] = {
            "identity": identity_arg(player_identity),
            "display_name": f"Player {player_identity[:4]}",
            "created_at": timestamp_arg(created_at),
            "updated_at": timestamp_arg(created_at),
        }
        self.loadouts[ticket_id] = {
            "ticket_id": ticket_id,
            "player_identity": identity_arg(player_identity),
            "primary_discipline_id": "WAR",
            "secondary_discipline_id_1": "RUIN",
            "secondary_discipline_id_2": "",
            "selected_ability_ids": [
                "WARRIOR_HEW",
                "WARRIOR_MAIM",
                "WARRIOR_GROUND_TO_AIR",
                "WARRIOR_AIR_TO_GROUND",
                "WARRIOR_CRUSHING_BLOW",
                "WARRIOR_CATACLYSM",
                "WARRIOR_BUZZSAW",
                "WARRIOR_SKYFALL",
                "SPELL_FIREBALL",
            ],
            "armor_set_id": "IRON",
            "main_hand_item_def_id": "NH_SWORD_2H_NEWBIE_02",
            "main_hand_color_id": "CL",
            "off_hand_item_def_id": "",
            "off_hand_color_id": "",
            "loadout_revision": 4,
            "captured_at": timestamp_arg(created_at),
        }

    def _database(self, name_or_identity: str) -> dict[str, Any] | None:
        identity = self.database_names.get(name_or_identity, name_or_identity)
        return self.databases.get(identity)

    def sql(self, database: str, query: str) -> list[dict[str, Any]]:
        table = query.split("FROM", 1)[1].strip().split()[0]
        if database == self.hub_database:
            if table == "hub_service_config":
                return [
                    {
                        "singleton_id": 0,
                        "module_owner": identity_arg(self.service_identity),
                        "provisioner_identity": identity_arg(self.service_identity),
                        "updated_at": timestamp_arg(1_000),
                    }
                ]
            if table == "match_ticket":
                return list(self.tickets.values())
            if table == "match_assignment":
                return list(self.assignments.values())
            if table == "hub_player":
                return list(self.players.values())
            if table == "match_player_loadout_snapshot":
                return list(self.loadouts.values())
        match_database = self._database(database)
        if match_database is None:
            raise ProvisionerError("match database does not exist")
        if table == "match_bootstrap_config":
            return [match_database["config"]] if match_database.get("config") else []
        if table == "match_reservation":
            return list(match_database.get("reservations", []))
        raise AssertionError(f"unexpected SQL table {table}")

    def call(self, database: str, reducer: str, arguments: list[Any]) -> None:
        self.calls.append((database, reducer, arguments))
        if database == self.hub_database:
            ticket = self.tickets.get(str(arguments[0]))
            if reducer == "service_claim_ticket":
                if ticket is None:
                    raise ProvisionerError("ticket missing")
                ticket["status"] = "CLAIMED"
                ticket["lease_owner"] = {"some": str(arguments[1])}
                ticket["lease_until"] = {"some": arguments[2]}
                return
            if reducer == "service_mark_provisioning":
                if ticket is None:
                    raise ProvisionerError("ticket missing")
                ticket["status"] = "PROVISIONING"
                return
            if reducer == "service_mark_ready":
                if ticket is None:
                    raise ProvisionerError("ticket missing")
                ticket["status"] = "READY"
                ticket["lease_owner"] = {"none": []}
                ticket["lease_until"] = {"none": []}
                self.assignments[str(arguments[0])] = {
                    "ticket_id": str(arguments[0]),
                    "player_identity": ticket["player_identity"],
                    "match_id": str(arguments[2]),
                    "server_uri": str(arguments[3]),
                    "database_identity": str(arguments[4]),
                    "match_build_id": str(arguments[5]),
                    "map_id": str(arguments[6]),
                    "ready_at": timestamp_arg(1_000),
                    "expires_at": arguments[7],
                }
                return
            if reducer == "service_mark_failed":
                if ticket is None:
                    raise ProvisionerError("ticket missing")
                ticket["status"] = "FAILED"
                ticket["failure_code"] = {"some": str(arguments[2])}
                ticket["lease_owner"] = {"none": []}
                ticket["lease_until"] = {"none": []}
                return
            if reducer == "service_close_ticket":
                if self.close_ticket_failures:
                    self.close_ticket_failures -= 1
                    raise ProvisionerError("injected Hub ticket close failure")
                if ticket is not None:
                    ticket["status"] = "CLOSED"
                    self.assignments.pop(str(arguments[0]), None)
                return
            raise AssertionError(f"unexpected Hub reducer {reducer}")

        match_database = self._database(database)
        if match_database is None:
            raise ProvisionerError("match database does not exist")
        if reducer == "bootstrap_unranked_2_v_2_bot_match":
            if self.fail_bootstrap:
                raise ProvisionerError("injected bootstrap failure")
            match_database["config"] = {
                "singleton_id": 0,
                "match_id": str(arguments[0]),
                "match_build_id": str(arguments[1]),
                "map_id": str(arguments[2]),
                "phase": "WAITING",
                "allocation_expires_at": arguments[4],
            }
            match_database["reservations"] = [
                {
                    "player_identity": arguments[5],
                    "display_name": str(arguments[6]),
                    "primary_discipline_id": str(arguments[7]),
                    "secondary_discipline_id_1": str(arguments[8]),
                    "secondary_discipline_id_2": str(arguments[9]),
                    "selected_ability_ids": list(arguments[10]),
                    "armor_set_id": str(arguments[11]),
                    "main_hand_item_def_id": str(arguments[12]),
                    "main_hand_color_id": str(arguments[13]),
                    "off_hand_item_def_id": str(arguments[14]),
                    "off_hand_color_id": str(arguments[15]),
                }
            ]
            return
        if reducer == "abort_match":
            if match_database.get("config"):
                match_database["config"]["phase"] = "ABORTED"
            return
        raise AssertionError(f"unexpected match reducer {reducer}")

    def publish(self, database_name: str, _wasm: bytes) -> dict[str, Any]:
        self.publish_count += 1
        identity = hashlib.sha256(database_name.encode("utf-8")).hexdigest()
        self.databases[identity] = {
            "database_identity": identity,
            "owner_identity": self.service_identity,
            "name": database_name,
            "config": None,
            "reservations": [],
        }
        self.database_names[database_name] = identity
        if self.fail_publish_after_create:
            raise ProvisionerError("injected lost publish response")
        return {"database_identity": identity, "op": "created", "domain": database_name}

    def database_info(self, name_or_identity: str) -> dict[str, Any] | None:
        database = self._database(name_or_identity)
        if database is None:
            return None
        return {
            "database_identity": database["database_identity"],
            "owner_identity": database["owner_identity"],
            "host_type": "wasm",
            "initial_program": "fake-program",
        }

    def delete(self, database_identity: str) -> None:
        if self.delete_failures:
            self.delete_failures -= 1
            raise ProvisionerError("injected delete failure")
        database = self.databases.pop(database_identity, None)
        if database is not None:
            self.database_names.pop(database["name"], None)
        self.delete_count += 1


class ProvisionerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.wasm_path = self.root / "arena.wasm"
        self.wasm_path.write_bytes(b"fake immutable wasm")
        self.now = 1_000
        self.api = FakeApi()
        self.config = Config(
            token="secret-test-token",
            management_url="http://127.0.0.1:3000",
            client_uri="ws://127.0.0.1:3000",
            hub_database="arena-hub-local",
            database_prefix="arena-match-test",
            wasm_path=self.wasm_path,
            state_path=self.root / "state.sqlite3",
            max_concurrent_matches=4,
            lease_seconds=90,
            allocation_seconds=120,
            hard_ttl_seconds=1_800,
            reconcile_seconds=30,
            cleanup_retry_seconds=5,
            cleaned_retention_seconds=86_400,
            map_id="ARENA_MAP_01",
        )
        self.store = AllocationStore(self.config.state_path)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def provisioner(self) -> Provisioner:
        return Provisioner(
            self.config,
            self.api,
            self.store,
            clock=lambda: self.now,
            lease_factory=lambda: "lease-fixed-0001",
        )

    def run_quietly(self, provisioner: Provisioner | None = None) -> dict[str, int]:
        with contextlib.redirect_stdout(io.StringIO()):
            return (provisioner or self.provisioner()).run_once()

    def test_sql_decoder_uses_returned_schema_names(self) -> None:
        payload = [
            {
                "schema": {
                    "elements": [
                        {"name": {"some": "ticket_id"}},
                        {"name": {"some": "status"}},
                    ]
                },
                "rows": [["ticket-1", "PENDING"]],
            }
        ]
        self.assertEqual(
            decode_sql_rows(payload),
            [{"ticket_id": "ticket-1", "status": "PENDING"}],
        )

    def test_spacetimedb_2_1_compact_identity_timestamp_and_option_values(self) -> None:
        self.assertEqual(normalize_identity([f"0x{PLAYER_ONE}"]), PLAYER_ONE)
        self.assertEqual(
            timestamp_microseconds([1_234_567_890_123]),
            1_234_567_890_123,
        )
        self.assertEqual(timestamp_seconds([1_234_567_000_000]), 1_234_567)
        self.assertIsNone(unwrap_option([1, []]))
        self.assertEqual(unwrap_option([0, "LEASE"]), "LEASE")

    def test_management_api_is_restricted_to_explicit_loopback_http(self) -> None:
        self.assertEqual(
            _validate_management_url("http://127.0.0.1:3000"),
            "http://127.0.0.1:3000",
        )
        for unsafe in [
            "https://127.0.0.1:3000",
            "http://192.0.2.10:3000",
            "http://user:secret@127.0.0.1:3000",
            "http://127.0.0.1:3000/admin",
        ]:
            with self.subTest(unsafe=unsafe):
                with self.assertRaises(ProvisionerError):
                    _validate_management_url(unsafe)

    def test_wakeup_subscription_uses_the_authenticated_provisioner_view(self) -> None:
        subscriber = HubWakeupSubscriber(
            "http://127.0.0.1:3000", "arena-hub-local"
        )
        command = subscriber.command()

        self.assertNotIn("--anonymous", command)
        self.assertIn("--print-initial-update", command)
        self.assertEqual(command[-1], "SELECT * FROM provisioner_wakeup")

    def test_wakeup_wait_is_prompt_coalescing_and_has_a_timed_fallback(self) -> None:
        subscriber = HubWakeupSubscriber(
            "http://127.0.0.1:3000", "arena-hub-local"
        )
        stopping = threading.Event()
        subscriber._wakeup.set()
        subscriber._wakeup.set()

        self.assertTrue(subscriber.wait(1.0, stopping))
        started = time.monotonic()
        self.assertFalse(subscriber.wait(0.02, stopping))
        self.assertGreaterEqual(time.monotonic() - started, 0.015)

        stopping.set()
        self.assertFalse(subscriber.wait(1.0, stopping))

    def test_allocation_keys_are_deterministic_safe_and_ticket_distinct(self) -> None:
        first = allocation_keys("ticket-one", "arena-match")
        retry = allocation_keys("ticket-one", "arena-match")
        other = allocation_keys("ticket-two", "arena-match")
        self.assertEqual(first, retry)
        self.assertNotEqual(first, other)
        self.assertRegex(first[0], r"^arena-match-[0-9a-f]{24}$")
        self.assertRegex(first[1], r"^match-[0-9a-f]{24}$")

    def test_one_ticket_publishes_bootstraps_and_becomes_ready_once(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        provisioner = self.provisioner()
        self.run_quietly(provisioner)
        self.run_quietly(provisioner)

        self.assertEqual(self.api.publish_count, 1)
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "READY")
        self.assertEqual(self.store.get("ticket-one").state, "READY")
        bootstrap_calls = [call for call in self.api.calls if call[1].startswith("bootstrap_")]
        self.assertEqual(len(bootstrap_calls), 1)
        bootstrap_args = bootstrap_calls[0][2]
        frozen = self.api.loadouts["ticket-one"]
        self.assertEqual(bootstrap_args[7], frozen["primary_discipline_id"])
        self.assertEqual(bootstrap_args[8], frozen["secondary_discipline_id_1"])
        self.assertEqual(bootstrap_args[9], frozen["secondary_discipline_id_2"])
        self.assertEqual(bootstrap_args[10], frozen["selected_ability_ids"])
        self.assertEqual(bootstrap_args[11], frozen["armor_set_id"])
        self.assertEqual(bootstrap_args[12], frozen["main_hand_item_def_id"])
        self.assertEqual(bootstrap_args[13], frozen["main_hand_color_id"])
        self.assertEqual(bootstrap_args[14], frozen["off_hand_item_def_id"])
        self.assertEqual(bootstrap_args[15], frozen["off_hand_color_id"])

    def test_success_emits_stage_level_startup_timing(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.provisioner().run_once()

        events = [json.loads(line) for line in output.getvalue().splitlines()]
        timing = next(event for event in events if event["event"] == "match_startup_timing")
        self.assertEqual(timing["outcome"], "ready")
        self.assertEqual(timing["final_stage"], "ready")
        self.assertEqual(timing["wasm_bytes"], len(b"fake immutable wasm"))
        self.assertTrue(timing["database_published"])
        self.assertTrue(timing["bootstrap_called"])
        self.assertGreaterEqual(timing["ticket_elapsed_ms"], 0)
        self.assertGreaterEqual(timing["provisioner_elapsed_ms"], 0)
        self.assertEqual(
            set(timing["timings_ms"]),
            {
                "hub_claim",
                "ticket_to_claim",
                "hub_mark_provisioning",
                "database_lookup",
                "database_publish",
                "database_verify",
                "bootstrap_lookup",
                "bootstrap_call",
                "ledger_mark_bootstrapped",
                "hub_lease_renewal",
                "hub_mark_ready",
                "ledger_mark_ready",
            },
        )

    def test_concurrency_cap_leaves_excess_ticket_pending(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        self.api.add_ticket("ticket-two", PLAYER_TWO, created_at=1_001)
        provisioner = Provisioner(
            dataclasses.replace(self.config, max_concurrent_matches=1),
            self.api,
            self.store,
            clock=lambda: self.now,
            lease_factory=lambda: "lease-fixed-0001",
        )
        self.run_quietly(provisioner)

        statuses = {ticket["status"] for ticket in self.api.tickets.values()}
        self.assertEqual(statuses, {"PENDING", "READY"})
        self.assertEqual(self.api.publish_count, 1)

    def test_lost_publish_response_is_recovered_and_deleted_before_failure(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        self.api.fail_publish_after_create = True
        self.run_quietly()

        self.assertEqual(self.api.tickets["ticket-one"]["status"], "FAILED")
        self.assertEqual(
            self.api.tickets["ticket-one"]["failure_code"], {"some": "PUBLISH_FAILED"}
        )
        self.assertEqual(self.store.get("ticket-one").state, "CLEANED")
        self.assertEqual(self.api.databases, {})

    def test_bootstrap_failure_deletes_database_and_marks_ticket_failed(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        self.api.fail_bootstrap = True
        self.run_quietly()

        self.assertEqual(self.api.tickets["ticket-one"]["status"], "FAILED")
        self.assertEqual(
            self.api.tickets["ticket-one"]["failure_code"], {"some": "BOOTSTRAP_FAILED"}
        )
        self.assertEqual(self.api.databases, {})

    def test_restart_recovers_bootstrapped_provisioning_without_republish(self) -> None:
        ticket_id = "ticket-one"
        self.api.add_ticket(ticket_id, PLAYER_ONE)
        database_name, match_id, _ = allocation_keys(ticket_id, self.config.database_prefix)
        database_identity = hashlib.sha256(database_name.encode("utf-8")).hexdigest()
        self.api.database_names[database_name] = database_identity
        self.api.databases[database_identity] = {
            "database_identity": database_identity,
            "owner_identity": SERVICE_IDENTITY,
            "name": database_name,
            "config": {
                "match_id": match_id,
                "match_build_id": f"sha256-{hashlib.sha256(self.wasm_path.read_bytes()).hexdigest()[:20]}",
                "map_id": "ARENA_MAP_01",
                "phase": "WAITING",
                "allocation_expires_at": timestamp_arg(self.now + 120),
            },
            "reservations": [{"player_identity": identity_arg(PLAYER_ONE)}],
        }
        self.api.tickets[ticket_id]["status"] = "PROVISIONING"
        allocation = Allocation(
            ticket_id=ticket_id,
            player_identity=PLAYER_ONE,
            lease_id="lease-fixed-0001",
            match_id=match_id,
            database_name=database_name,
            database_identity=database_identity,
            state="BOOTSTRAPPED",
            wasm_sha256=hashlib.sha256(self.wasm_path.read_bytes()).hexdigest(),
            created_at=self.now - 30,
            updated_at=self.now - 30,
            hard_expires_at=self.now + 1_800,
            ready_at=None,
            terminal_phase=None,
            failure_code=None,
            cleanup_attempts=0,
            next_retry_at=0,
            last_error=None,
        )
        self.store.create(allocation)

        self.run_quietly()

        self.assertEqual(self.api.publish_count, 0)
        self.assertEqual(self.api.tickets[ticket_id]["status"], "READY")
        self.assertEqual(self.store.get(ticket_id).state, "READY")

    def test_terminal_match_deletes_exact_identity_and_closes_ticket(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        provisioner = self.provisioner()
        self.run_quietly(provisioner)
        allocation = self.store.get("ticket-one")
        self.api.databases[allocation.database_identity]["config"]["phase"] = "ENDED"

        self.run_quietly(provisioner)

        self.assertEqual(self.api.delete_count, 1)
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "CLOSED")
        self.assertEqual(self.store.get("ticket-one").state, "CLEANED")

    def test_delete_failure_retries_without_closing_early(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        provisioner = self.provisioner()
        self.run_quietly(provisioner)
        allocation = self.store.get("ticket-one")
        self.api.databases[allocation.database_identity]["config"]["phase"] = "ENDED"
        self.api.delete_failures = 1

        self.run_quietly(provisioner)
        self.assertEqual(self.store.get("ticket-one").state, "CLEANUP")
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "READY")

        self.now += 6
        self.run_quietly(provisioner)
        self.assertEqual(self.store.get("ticket-one").state, "CLEANED")
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "CLOSED")

    def test_owner_mismatch_is_reported_and_never_deleted(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        provisioner = self.provisioner()
        self.run_quietly(provisioner)
        allocation = self.store.get("ticket-one")
        self.api.databases[allocation.database_identity]["owner_identity"] = OTHER_OWNER

        self.run_quietly(provisioner)

        self.assertEqual(self.store.get("ticket-one").state, "ORPHANED")
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "CLOSED")
        self.assertNotIn("ticket-one", self.api.assignments)
        self.assertEqual(self.api.delete_count, 0)
        self.assertIn(allocation.database_identity, self.api.databases)

    def test_changed_match_build_quarantines_database_and_closes_ready_ticket(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        self.run_quietly()
        allocation = self.store.get("ticket-one")
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "READY")

        self.wasm_path.write_bytes(b"replacement immutable wasm")
        self.run_quietly()

        self.assertEqual(self.store.get("ticket-one").state, "ORPHANED")
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "CLOSED")
        self.assertNotIn("ticket-one", self.api.assignments)
        self.assertEqual(self.api.delete_count, 0)
        self.assertIn(allocation.database_identity, self.api.databases)

    def test_orphaned_allocation_retries_client_facing_ticket_close(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        self.run_quietly()
        allocation = self.store.get("ticket-one")
        self.api.databases[allocation.database_identity]["owner_identity"] = OTHER_OWNER
        self.api.close_ticket_failures = 1

        self.run_quietly()
        self.assertEqual(self.store.get("ticket-one").state, "ORPHANED")
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "READY")

        self.now += self.config.cleanup_retry_seconds
        self.run_quietly()

        self.assertEqual(self.api.tickets["ticket-one"]["status"], "CLOSED")
        self.assertNotIn("ticket-one", self.api.assignments)
        self.assertEqual(self.api.delete_count, 0)
        self.assertIn(allocation.database_identity, self.api.databases)

    def test_orphaned_allocation_does_not_consume_matchmaking_capacity(self) -> None:
        capacity_one = dataclasses.replace(self.config, max_concurrent_matches=1)
        provisioner = Provisioner(
            capacity_one,
            self.api,
            self.store,
            clock=lambda: self.now,
            lease_factory=lambda: "lease-fixed-0001",
        )
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        self.run_quietly(provisioner)
        orphan = self.store.get("ticket-one")
        self.api.databases[orphan.database_identity]["owner_identity"] = OTHER_OWNER
        self.api.add_ticket("ticket-two", PLAYER_TWO, created_at=1_001)

        self.run_quietly(provisioner)

        self.assertEqual(self.store.get("ticket-one").state, "ORPHANED")
        self.assertEqual(self.api.tickets["ticket-one"]["status"], "CLOSED")
        self.assertEqual(self.store.get("ticket-two").state, "READY")
        self.assertEqual(self.api.tickets["ticket-two"]["status"], "READY")
        self.assertEqual(self.api.delete_count, 0)
        self.assertIn(orphan.database_identity, self.api.databases)

    def test_token_is_not_written_to_the_ledger(self) -> None:
        self.api.add_ticket("ticket-one", PLAYER_ONE)
        self.run_quietly()
        ledger_bytes = self.config.state_path.read_bytes()
        self.assertNotIn(self.config.token.encode("utf-8"), ledger_bytes)


if __name__ == "__main__":
    unittest.main()
