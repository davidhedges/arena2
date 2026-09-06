"""Regression coverage for the local movement probe's actual wire requests."""
import importlib.util
import json
from pathlib import Path
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "special_movement_handoff_probe", Path(__file__).with_name("special-movement-handoff-probe.py")
)
probe_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe_module)
IDENTITY = "a" * 64


class Socket:
    def __init__(self, first):
        self.first = first
        self.sent = []
        self.closed = False

    def recv(self):
        return json.dumps(self.first)

    def send(self, frame):
        self.sent.append(json.loads(frame))

    def settimeout(self, timeout):
        pass

    def close(self):
        self.closed = True


class MovementProbeTests(unittest.TestCase):
    def connect(self, first=None):
        socket = Socket(first if first is not None else {
            "IdentityToken": {"identity": {"__identity__": "0x" + IDENTITY}}
        })
        with patch.object(probe_module.websocket, "create_connection", return_value=socket), \
             patch.object(probe_module.threading.Thread, "start"):
            probe = probe_module.Probe("test-local", "127.0.0.1:3999")
        return probe, socket

    def test_dodge_serializes_unique_nonempty_prediction_tokens(self):
        probe, socket = self.connect()
        physics = {"pos": (1.0, 2.0, 3.0), "yaw": 0.5, "tick": 42}
        returned = [probe.start_dodge(physics, 1.0, 0.0) for _ in range(2)]
        self.assertNotEqual(returned[0][0], returned[1][0])
        for sequence, (frame, token) in enumerate(zip(socket.sent, returned), 1):
            call = frame["CallReducer"]
            args = json.loads(call["args"])
            self.assertEqual(call["reducer"], "start_dodge")
            self.assertEqual(args[:8], [42, 42, 1.0, 2.0, 3.0, 0.5, 1.0, 0.0])
            self.assertEqual(tuple(args[8:]), token)
            self.assertRegex(token[0], r"^[A-Za-z0-9_-]{1,64}$")
            self.assertEqual(token[1], sequence)

    def test_missing_or_malformed_identity_stops_before_any_action(self):
        for first in [{}, {"IdentityToken": {"identity": "invalid"}}]:
            socket = Socket(first)
            with patch.object(probe_module.websocket, "create_connection", return_value=socket), \
                 patch.object(probe_module.threading.Thread, "start") as start:
                with self.assertRaisesRegex(RuntimeError, "authenticated identity"):
                    probe_module.Probe("test-local", "127.0.0.1:3999")
                start.assert_not_called()
            self.assertTrue(socket.closed)
            self.assertEqual(socket.sent, [])

    def test_sql_uses_the_socket_host_instead_of_cli_default(self):
        probe, _ = self.connect()
        with patch.object(probe_module.subprocess, "run", return_value=types.SimpleNamespace(
            returncode=0, stdout="", stderr=""
        )) as run:
            self.assertEqual(probe.sql("SELECT * FROM player_physics"), [])
        self.assertEqual(run.call_args.args[0], [
            "spacetime", "sql", "--server", "http://127.0.0.1:3999", "test-local",
            "SELECT * FROM player_physics",
        ])

    def test_prediction_ack_requires_matching_owner_token_sequence_and_family(self):
        probe, _ = self.connect()
        token = "smh-probe-dodge-1"
        good = [IDENTITY, token, "1", "(movement = ())", "(accepted = ())"]
        rows = []
        for index, wrong in [(0, "b" * 64), (1, "different"), (2, "2"), (3, "(Melee = ())")]:
            row = good.copy()
            row[index] = wrong
            rows.append(row)
        with patch.object(probe, "sql", return_value=rows):
            self.assertIsNone(probe.prediction_result(token, 1))
        with patch.object(probe, "sql", return_value=[*rows, good]):
            self.assertEqual(probe.prediction_result(token, 1), "accepted")
        rejected = [*good[:4], "(Rejected = ())"]
        with patch.object(probe, "sql", return_value=[rejected]):
            self.assertEqual(probe.prediction_result(token, 1), "rejected")


if __name__ == "__main__":
    unittest.main()
