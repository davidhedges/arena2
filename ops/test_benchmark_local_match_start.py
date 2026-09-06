#!/usr/bin/env python3
"""Regression tests for persistent benchmark identity and SpacetimeDB rows."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import stat
import subprocess
import sys
import tempfile
import types
import unittest
from pathlib import Path
from typing import Any
from unittest.mock import Mock, patch


SCRIPT = Path(__file__).with_name("benchmark-local-match-start.py")
SPEC = importlib.util.spec_from_file_location("benchmark_local_match_start", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
BENCHMARK = vars(MODULE)


class BenchmarkIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name) / "credentials"
        self.credential = {"identity": "a" * 64, "token": "private-benchmark-token"}
        self.issue = self.enterContext(patch.object(
            MODULE.urllib.request, "urlopen",
            side_effect=lambda *args, **kwargs: io.BytesIO(json.dumps(self.credential).encode()),
        ))
        self.args = types.SimpleNamespace(
            server_uri="ws://127.0.0.1:3000", hub_database="arena-hub-local",
            timeout_seconds=1, samples=1, ledger=Path("unused.sqlite"),
            cleanup_timeout_seconds=1,
        )

    def identity(self, server="ws://127.0.0.1:3000", hub="arena-hub-local"):
        return MODULE.benchmark_identity(server, hub, 1, directory=self.directory)

    def prepare(self):
        with self.identity():
            pass
        return next(self.directory.glob("*.json"))

    def test_separate_main_runs_persist_before_connect_and_reuse_authorization(self):
        sockets = []

        def connect(*args, **kwargs):
            saved = json.loads(next(self.directory.glob("*.json")).read_text())
            self.assertEqual(saved["token"], self.credential["token"])
            self.assertEqual(kwargs["header"], {
                "Authorization": "Bearer " + self.credential["token"],
            })
            socket = Mock()
            socket.recv.return_value = json.dumps({"IdentityToken": self.credential})
            sockets.append(socket)
            return socket

        real_identity = MODULE.benchmark_identity

        def identity(*args):
            return real_identity(*args, directory=self.directory)

        def cleanup(*args):
            with self.assertRaisesRegex(RuntimeError, "already using"):
                with real_identity(self.args.server_uri, self.args.hub_database, 1, self.directory):
                    self.fail("The identity lock was released before match cleanup")
            return {"ticket": "CLEANED"}

        output = io.StringIO()
        with patch.object(MODULE, "parse_args", return_value=self.args), \
             patch.object(MODULE, "benchmark_identity", side_effect=identity), \
             patch.object(MODULE.websocket, "create_connection", side_effect=connect), \
             patch.object(MODULE.Benchmark, "read_hub_build"), \
             patch.object(MODULE.Benchmark, "sample", return_value={"ticket": "ticket"}), \
             patch.object(MODULE, "summarize", return_value={}), \
             patch.object(MODULE, "wait_for_cleanup", side_effect=cleanup), \
             contextlib.redirect_stdout(output):
            self.assertEqual(MODULE.main(), 0)
            self.assertEqual(MODULE.main(), 0)
        self.issue.assert_called_once()
        request = self.issue.call_args.args[0]
        self.assertEqual(request.full_url, "http://127.0.0.1:3000/v1/identity")
        self.assertEqual(request.method, "POST")
        self.assertEqual(len(sockets), 2)
        for socket in sockets:
            socket.close.assert_called_once()
        path = next(self.directory.glob("*.json"))
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(self.directory.stat().st_mode), 0o700)
        self.assertNotIn(self.credential["token"], output.getvalue())

    def test_corrupt_missing_token_and_wrong_scope_never_issue_a_replacement(self):
        path = self.prepare()
        saved = json.loads(path.read_text())
        for payload in (
            "truncated", "null", "[]", json.dumps({**saved, "token": ""}),
            json.dumps({**saved, "identity": "bad"}),
            json.dumps({**saved, "hub_database": "another-hub"}),
            json.dumps({**saved, "server_uri": "ws://127.0.0.1:3999"}),
        ):
            with self.subTest(payload=payload):
                path.write_text(payload)
                with self.assertRaises(ValueError):
                    with self.identity():
                        self.fail("Invalid credentials were accepted")
                self.assertEqual(path.read_text(), payload)
        self.issue.assert_called_once()

    def test_authentication_failure_does_not_retry_anonymously_or_replace_saved_file(self):
        path = self.prepare()
        original = path.read_bytes()
        with self.identity() as credential, \
             patch.object(MODULE.websocket, "create_connection", side_effect=RuntimeError(
                 "rejected " + self.credential["token"]
             )) as connect:
            with self.assertRaisesRegex(RuntimeError, "no replacement identity") as caught:
                MODULE.Benchmark(self.args.server_uri, self.args.hub_database, 1, credential=credential)
            self.assertNotIn(self.credential["token"], str(caught.exception))
        connect.assert_called_once()
        self.assertEqual(connect.call_args.kwargs["header"], {
            "Authorization": "Bearer " + self.credential["token"],
        })
        self.assertEqual(path.read_bytes(), original)
        with self.identity() as credential:
            self.assertEqual(credential, self.credential)
        self.issue.assert_called_once()

    def test_save_failure_never_reaches_the_hub_and_allows_safe_retry(self):
        with patch.object(MODULE.os, "replace", side_effect=OSError("disk full")), \
             patch.object(MODULE, "Connection") as connect:
            with self.assertRaisesRegex(OSError, "disk full"):
                with self.identity() as credential:
                    MODULE.Benchmark(self.args.server_uri, self.args.hub_database, 1, credential=credential)
            connect.assert_not_called()
        self.assertEqual([p.suffix for p in self.directory.iterdir()], [".lock"])
        with self.identity() as credential:
            self.assertEqual(credential, self.credential)

    def test_concurrent_process_is_rejected_and_exception_releases_lock(self):
        code = (
            "import pathlib, runpy, sys\n"
            "m = runpy.run_path(sys.argv[1])\n"
            "try:\n"
            " with m['benchmark_identity']('ws://127.0.0.1:3000', 'arena-hub-local', 1, pathlib.Path(sys.argv[2])):\n"
            "  sys.exit(2)\n"
            "except RuntimeError as error:\n"
            " sys.exit(0 if 'already using' in str(error) else 3)\n"
        )
        with self.assertRaisesRegex(RuntimeError, "sample failed"):
            with self.identity():
                result = subprocess.run(
                    [sys.executable, "-c", code, str(SCRIPT), str(self.directory)],
                    capture_output=True, text=True, timeout=10,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                raise RuntimeError("sample failed")
        with self.identity() as credential:
            self.assertEqual(credential, self.credential)
        self.issue.assert_called_once()

    def test_origin_aliases_reuse_identity_but_other_hubs_and_ports_are_isolated(self):
        self.prepare()
        with self.identity(server="ws://localhost:3000/"):
            pass
        self.issue.assert_called_once()
        with self.identity(hub="another-local-hub"):
            pass
        with self.identity(server="ws://127.0.0.1:3999"):
            pass
        self.assertEqual(self.issue.call_count, 3)
        self.assertEqual(len(list(self.directory.glob("*.json"))), 3)
        for origin in ("ws://example.com:3000", "ws://127.0.0.1:3000/path", "ws://user@localhost:3000"):
            with self.assertRaises(ValueError):
                with self.identity(server=origin):
                    self.fail("Invalid origin accepted")
        self.assertEqual(self.issue.call_count, 3)

    def test_identity_mismatch_and_invalid_handshake_close_socket_before_subscription(self):
        for frame in ({"IdentityToken": {**self.credential, "identity": "b" * 64}}, {}):
            with self.subTest(frame=frame):
                socket = Mock()
                socket.recv.return_value = json.dumps(frame)
                with patch.object(MODULE.websocket, "create_connection", return_value=socket):
                    with self.assertRaises(RuntimeError):
                        MODULE.Benchmark(self.args.server_uri, self.args.hub_database, 1,
                                         credential=self.credential)
                socket.close.assert_called_once()
                socket.send.assert_not_called()


class BenchmarkRowDecodingTests(unittest.TestCase):
    def test_empty_weapon_color_resolves_to_authored_default(self) -> None:
        effective_color = BENCHMARK["effective_weapon_color_id"]
        self.assertEqual(
            effective_color("TRAINING_DAGGER_PAIR", ""),
            "DEFAULT",
        )
        self.assertEqual(
            effective_color("TRAINING_DAGGER_PAIR", "default"),
            "DEFAULT",
        )
        self.assertEqual(effective_color("", ""), "")

    def test_option_none_accepts_both_protocol_encodings(self) -> None:
        option_value = BENCHMARK["option_value"]
        self.assertIsNone(option_value([1]))
        self.assertIsNone(option_value([1, {}]))
        self.assertEqual(option_value([0, "ITEM"]), "ITEM")

    def test_inserted_rows_accepts_view_arrays_and_table_objects(self) -> None:
        inserted_rows = BENCHMARK["inserted_rows"]
        update = {
            "tables": [
                {
                    "table_name": "my_view",
                    "updates": [{"inserts": [json.dumps(["value", 1])]}],
                },
                {
                    "table_name": "ordinary_table",
                    "updates": [{"inserts": [json.dumps({"key": "value"})]}],
                },
            ]
        }
        self.assertEqual(inserted_rows(update, "my_view"), [["value", 1]])
        self.assertEqual(inserted_rows(update, "ordinary_table"), [{"key": "value"}])

    def test_hub_combat_build_decodes_v2_selected_configuration(self) -> None:
        parse_hub = BENCHMARK["parse_hub_combat_build"]
        owner = {"__identity__": "0xabc"}
        build = parse_hub(
            {
                "owner": owner,
                "schema_version": 2,
                "revision": 4,
                "starting_discipline_id": [1, {}],
                "selected_specializations": [
                    {"slot_index": 0, "specialization_id": "DAGGERS_BLADEDANCER"}
                ],
                "dormant_specializations": [],
                "discipline_configurations": [
                    {
                        "combat_discipline_id": "DAGGERS",
                        "main_hand_item_def_id": "TRAINING_DAGGER_PAIR",
                        "main_hand_color_id": "",
                        "off_hand_item_def_id": "",
                        "off_hand_color_id": "",
                    }
                ],
                "selected_features": [
                    {
                        "specialization_id": "DAGGERS_BLADEDANCER",
                        "ability_id": "DAGGER_QUICK_CUT",
                        "preferred_bar_order": [0, 0],
                    }
                ],
                "selected_traits": ["MASTERY"],
            }
        )

        self.assertEqual(build["owner"], "abc")
        self.assertEqual(build["starting_discipline_id"], "DAGGERS")
        self.assertEqual(build["revision"], 4)
        self.assertEqual(
            build["selected_features"],
            [
                {
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "ability_id": "DAGGER_QUICK_CUT",
                    "preferred_bar_order": 0,
                }
            ],
        )
        self.assertEqual(build["selected_traits"], ["MASTERY"])

    def test_applied_match_combat_build_decodes_object_rows(self) -> None:
        parse_applied = BENCHMARK["parse_applied_match_combat_build"]
        owner = {"__identity__": "0xabc"}
        rows: dict[str, list[dict[str, Any]]] = {
            "match_combat_build_v_2": [
                {
                    "owner": owner,
                    "contract_schema_version": 2,
                    "revision": 4,
                    "starting_discipline_id": "DAGGERS",
                    "mastery_active": True,
                }
            ],
            "match_selected_specialization_v_2": [
                {
                    "key": "abc:0",
                    "owner": owner,
                    "slot_index": 0,
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "combat_discipline_id": "DAGGERS",
                    "specialization_kind": "FORM",
                }
            ],
            "match_discipline_configuration_v_2": [
                {
                    "key": "abc:DAGGERS",
                    "owner": owner,
                    "combat_discipline_id": "DAGGERS",
                    "main_hand_item_def_id": "TRAINING_DAGGER_PAIR",
                    "main_hand_color_id": "",
                    "off_hand_item_def_id": "",
                    "off_hand_color_id": "",
                    "main_hand_item_id": [0, "starter-daggers"],
                    "off_hand_item_id": [1, {}],
                }
            ],
            "match_technique_selection_v_2": [
                {
                    "key": "abc:DAGGER_QUICK_CUT",
                    "owner": owner,
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "combat_discipline_id": "DAGGERS",
                    "ability_id": "DAGGER_QUICK_CUT",
                    "bar_order": 0,
                }
            ],
            "match_spell_selection_v_2": [],
            "match_perk_selection_v_2": [],
            "match_trait_selection_v_2": [
                {"key": "abc:MASTERY", "owner": owner, "ability_id": "MASTERY"}
            ],
            "active_armor_set": [{"owner": owner, "armor_set_id": "PEASANT"}],
            "player_equipment_presentation": [
                {
                    "owner": owner,
                    "main_hand_item_def_id": [0, "TRAINING_DAGGER_PAIR"],
                    "off_hand_item_def_id": [1, {}],
                    "main_hand_color_id": "",
                    "off_hand_color_id": "",
                }
            ],
        }
        applied = parse_applied(rows)
        self.assertIsNotNone(applied)
        assert applied is not None
        self.assertEqual(applied["build_owner"], "abc")
        self.assertEqual(applied["canonical_owners"], {"abc"})
        self.assertEqual(
            applied["equipped_main_hand_item_def_id"], "TRAINING_DAGGER_PAIR"
        )
        self.assertEqual(applied["equipped_off_hand_item_def_id"], "")
        self.assertEqual(
            applied["selected_features"],
            [
                {
                    "specialization_id": "DAGGERS_BLADEDANCER",
                    "ability_id": "DAGGER_QUICK_CUT",
                    "preferred_bar_order": 0,
                }
            ],
        )
        self.assertTrue(applied["mastery_active"])
        self.assertEqual(applied["selected_traits"], ["MASTERY"])


if __name__ == "__main__":
    unittest.main()
