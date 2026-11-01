"""Broker v3 dispatch, legal-action gates, ACK and close-menu evidence."""
import copy
import http.client
import json
from pathlib import Path
import sys
import threading
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.protocol import MAX_V2_MESSAGE_BYTES, MAX_V3_MESSAGE_BYTES
from bridge.server import Broker, ProtocolError, Server

FIXTURE = json.loads((ROOT / "protocol/fixtures/v3-business-observation.json").read_text(encoding="utf-8"))


class FakeClock:
    def __init__(self): self.now = 100.0
    def __call__(self): return self.now
    def monotonic(self): return self.now


class BrokerV3Tests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.broker = Broker("v3-test", clock=self.clock, monotonic=self.clock.monotonic, heartbeat_timeout=60)
        self.session = "v3-session"
        self.state = copy.deepcopy(FIXTURE["before"])
        self.exchange()

    def tearDown(self): self.broker.close()

    def exchange(self, state=None, result=None, progress=None, session=None, version=3):
        return self.broker.exchange({"schema_version": version, "session_id": session or self.session,
            "broker_epoch": self.broker.broker_epoch, "state": state or self.state,
            "has_result": result is not None, "result": result, "progress": progress})

    def request(self, action="close_menu", arguments=None, command_id="v3-action"):
        return {"schema_version": 3, "id": command_id, "broker_epoch": self.broker.broker_epoch,
                "action": action, "expected_world_epoch": self.state["world_epoch"],
                "expected_revision": self.state["revision"], "expected_version": self.state["version"],
                "arguments": {} if arguments is None else arguments}

    def test_dispatch_requires_exact_current_legal_action_pair(self):
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(self.request("start_business"))
        self.assertEqual(caught.exception.reason, "action_not_allowed")
        queued = self.broker.enqueue(self.request())
        self.assertEqual(queued["schema_version"], 3)
        self.assertEqual(queued["record_schema_version"], 3)
        delivered = self.exchange()
        self.assertEqual(delivered["command"]["action"], "close_menu")

    def test_purchase_action_is_rejected_even_when_client_names_it(self):
        request = self.request("purchase_visible_offer", {"customer_ref": "c", "offer_ref": "o",
            "item_refs": ["x"], "collection_fingerprint": "f", "mode": "normal", "amount": 1,
            "max_spend": 2, "authorization_ref": "auth"})
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(request)
        self.assertEqual(caught.exception.reason, "action_not_allowed")

    def test_reused_id_with_well_formed_different_v3_arguments_conflicts_first(self):
        request = self.request(command_id="same-v3-id")
        self.broker.enqueue(request)
        changed = self.request("purchase_visible_offer", {"customer_ref": "c", "offer_ref": "o",
            "item_refs": ["x"], "collection_fingerprint": "f", "mode": "normal", "amount": 1,
            "max_spend": 2, "authorization_ref": "auth"}, command_id="same-v3-id")
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(changed)
        self.assertEqual(caught.exception.reason, "command_id_conflict")

    def test_session_cannot_mix_v2_and_v3_snapshots(self):
        old = copy.deepcopy(FIXTURE["before"])
        old["schema_version"] = 2
        old["business"] = {"support": "unsupported", "customer_ref": None, "item_refs": None,
                           "offer_ref": None, "collection_fingerprint": None}
        old["legal_actions"] = [{"action": "close_menu", "arguments": {}}]
        with self.assertRaises(ProtocolError) as caught:
            self.exchange(state=old, version=2)
        self.assertEqual(caught.exception.reason, "session_wire_version_mismatch")

    def test_business_unknown_cannot_use_close_menu_alignment_reconciliation(self):
        request = self.request("start_business", command_id="business-unknown")
        command = dict(request, start_deadline_unix_ms=105_000, execution_timeout_ms=5_000)
        result = {"schema_version": 3, "id": command["id"], "broker_epoch": command["broker_epoch"],
                  "status": "outcome_unknown", "reason": "fixture_unknown", "before": None,
                  "after": None, "execution": None}
        self.broker.records[command["id"]] = {"command_id": command["id"],
            "request_fingerprint": "0" * 64, "request": request, "command": command,
            "command_status": "outcome_unknown", "result": result,
            "delivery_session_id": "old-session", "created_at_unix_ms": 100_000,
            "updated_at_unix_ms": 100_000, "execution": None}
        with self.assertRaises(ProtocolError) as caught:
            self.broker.reconcile({"schema_version": 3, "id": command["id"],
                "broker_epoch": self.broker.broker_epoch, "world_epoch": self.state["world_epoch"],
                "revision": 2, "expected_version": f"{self.state['world_epoch']}:2",
                "operator_note": "no replay confirmed", "confirm_no_replay": True})
        self.assertEqual(caught.exception.reason, "command_not_reconcilable")

    def test_v3_close_menu_result_is_acknowledged_and_kept_as_v3_history(self):
        self.broker.enqueue(self.request(command_id="close-v3"))
        delivered = self.exchange()
        command = delivered["command"]
        progress = {"id": command["id"], "broker_epoch": command["broker_epoch"], "stage": "completed",
                    "sequence": 4, "may_have_side_effects": True, "started_at_unix_ms": 100100,
                    "execution_deadline_unix_ms": 105100}
        after = copy.deepcopy(self.state)
        after.update(revision=2, version=f"{after['world_epoch']}:2", menu_open=False, can_close_menu=False,
                     legal_actions=[])
        result = {"schema_version": 3, "id": command["id"], "broker_epoch": command["broker_epoch"],
                  "status": "succeeded", "reason": "observed", "before": copy.deepcopy(self.state),
                  "after": after, "execution": progress}
        ack = self.exchange(state=after, result=result, progress=progress)
        self.assertEqual(ack["acknowledged_result_id"], "close-v3")
        historic = self.broker.result("close-v3")
        self.assertEqual(historic["schema_version"], 3)
        self.assertEqual(historic["record_schema_version"], 3)
        self.assertEqual(historic["result"]["schema_version"], 3)

    def test_restart_marks_v3_delivery_unknown_then_late_ack_never_replays(self):
        parent = ROOT / "artifacts" / "m2cd"
        parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=parent) as directory:
            journal = Path(directory) / "journal.json"
            first = Broker("restart-v3", clock=self.clock, monotonic=self.clock.monotonic,
                           heartbeat_timeout=60, journal_path=journal)
            session = "restart-v3-session"
            first.exchange({"schema_version": 3, "session_id": session,
                "broker_epoch": first.broker_epoch, "state": self.state,
                "has_result": False, "result": None, "progress": None})
            request = {"schema_version": 3, "id": "restart-v3-close",
                "broker_epoch": first.broker_epoch, "action": "close_menu",
                "expected_world_epoch": self.state["world_epoch"],
                "expected_revision": self.state["revision"],
                "expected_version": self.state["version"], "arguments": {}}
            first.enqueue(request)
            delivery = first.exchange({"schema_version": 3, "session_id": session,
                "broker_epoch": first.broker_epoch, "state": self.state,
                "has_result": False, "result": None, "progress": None})
            self.assertTrue(delivery["has_command"])
            first.close()

            restarted = Broker("restart-v3", clock=self.clock, monotonic=self.clock.monotonic,
                               heartbeat_timeout=60, journal_path=journal)
            try:
                record = restarted.result("restart-v3-close")
                self.assertEqual(record["command_status"], "outcome_unknown")
                self.assertEqual(restarted.snapshot()["blocking_reason"], "outcome_unknown")

                progress = {"id": "restart-v3-close", "broker_epoch": request["broker_epoch"],
                    "stage": "completed", "sequence": 4, "may_have_side_effects": True,
                    "started_at_unix_ms": 100100, "execution_deadline_unix_ms": 105100}
                after = copy.deepcopy(self.state)
                after.update(revision=2, version=f"{after['world_epoch']}:2", menu_open=False,
                             can_close_menu=False, legal_actions=[])
                result = {"schema_version": 3, "id": "restart-v3-close",
                    "broker_epoch": request["broker_epoch"], "status": "succeeded",
                    "reason": "observed_after_restart", "before": copy.deepcopy(self.state),
                    "after": after, "execution": progress}
                ack = restarted.exchange({"schema_version": 3, "session_id": session,
                    "broker_epoch": request["broker_epoch"], "state": after,
                    "has_result": True, "result": result, "progress": progress})
                self.assertEqual(ack["acknowledged_result_id"], "restart-v3-close")
                self.assertFalse(ack["has_command"])
                self.assertEqual(restarted.result("restart-v3-close")["command_status"], "succeeded")
            finally:
                restarted.close()
                journal.unlink(missing_ok=True)
                journal.with_suffix(journal.suffix + ".lock").unlink(missing_ok=True)

    def test_http_keeps_v2_64k_cap_and_accepts_bounded_v3_exchange(self):
        broker = Broker("http-size-test", heartbeat_timeout=60)
        server = Server(broker, port=0)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            def post(raw, wire_version=None):
                conn = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                try:
                    headers = {"Authorization": "Bearer http-size-test", "Content-Type": "application/json"}
                    if wire_version is not None:
                        headers["X-PawnPilot-Wire-Version"] = str(wire_version)
                    conn.request("POST", "/exchange", body=raw, headers=headers)
                    response = conn.getresponse()
                    return response.status, response.read()
                finally:
                    conn.close()

            body = {"schema_version": 3, "session_id": "size-session", "broker_epoch": broker.broker_epoch,
                    "state": copy.deepcopy(FIXTURE["before"]), "has_result": False,
                    "result": None, "progress": None}
            raw = json.dumps(body, separators=(",", ":")).encode("utf-8")
            self.assertLess(len(raw), MAX_V2_MESSAGE_BYTES)
            padded = raw + b" " * (MAX_V2_MESSAGE_BYTES + 1000 - len(raw))
            self.assertEqual(len(padded), MAX_V2_MESSAGE_BYTES + 1000)
            self.assertEqual(post(padded, 3)[0], 200)

            v2_state = json.loads((ROOT / "protocol/fixtures/v2-close-menu.json").read_text(encoding="utf-8"))["before"]
            v2 = {**body, "schema_version": 2, "state": v2_state}
            v2_raw = json.dumps(v2, separators=(",", ":")).encode("utf-8")
            v2_padded = v2_raw + b" " * (MAX_V2_MESSAGE_BYTES + 1000 - len(v2_raw))
            self.assertEqual(post(v2_padded)[0], 413)

            small_v3 = json.dumps(body, separators=(",", ":")).encode("utf-8")
            self.assertEqual(post(small_v3, 2)[0], 400)
            self.assertEqual(post(v2_raw, 3)[0], 400)

            too_large = raw + b" " * (MAX_V3_MESSAGE_BYTES + 1 - len(raw))
            self.assertEqual(len(too_large), MAX_V3_MESSAGE_BYTES + 1)
            self.assertEqual(post(too_large, 3)[0], 413)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


if __name__ == "__main__": unittest.main()
