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
from bridge.journal import Journal

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

    def test_dispatch_rejects_action_missing_from_exact_current_legal_actions(self):
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(self.request("start_business"))
        self.assertEqual(caught.exception.reason, "precondition")
        queued = self.broker.enqueue(self.request())
        self.assertEqual(queued["schema_version"], 3)
        self.assertEqual(queued["record_schema_version"], 3)
        delivered = self.exchange()
        self.assertEqual(delivered["command"]["action"], "close_menu")

    def test_start_business_is_legal_from_morning_without_complete_business_observation(self):
        state = copy.deepcopy(FIXTURE["before"])
        state.update(menu_open=False, can_close_menu=False,
                     legal_actions=[{"action": "start_business", "arguments": {}}])
        state["interaction"].update(phase="idle", consistency="unverified", operable=True)
        self.state = state
        self.exchange(state=state)
        queued = self.broker.enqueue(self.request("start_business", command_id="open-store"))
        self.assertEqual(queued["command_status"], "pending")
        self.assertEqual(self.broker.records["open-store"]["command"]["execution_timeout_ms"], 15_000)
        delivered = self.exchange()
        command = delivered["command"]
        self.assertEqual(command["action"], "start_business")
        progress = {"id": command["id"], "broker_epoch": command["broker_epoch"],
            "stage": "completed", "sequence": 4, "may_have_side_effects": True,
            "started_at_unix_ms": 100100, "execution_deadline_unix_ms": 115100}
        after = copy.deepcopy(state)
        after.update(revision=2, version=f"{after['world_epoch']}:2", phase="OPEN", legal_actions=[])
        result = {"schema_version": 3, "id": command["id"], "broker_epoch": command["broker_epoch"],
            "status": "succeeded", "reason": "store_open", "before": state, "after": after,
            "execution": progress}
        ack = self.exchange(state=after, result=result, progress=progress)
        self.assertEqual(ack["acknowledged_result_id"], "open-store")
        self.assertEqual(self.broker.result("open-store")["result"]["status"], "succeeded")

    def test_receive_customer_requires_open_store_and_produces_long_enough_command(self):
        state = copy.deepcopy(FIXTURE["before"])
        state.update(phase="OPEN", menu_open=False, can_close_menu=False,
                     legal_actions=[{"action": "receive_next_customer", "arguments": {}}])
        state["interaction"].update(phase="idle", consistency="unverified", operable=True)
        customer = state["business"]["details"]["customer"]
        customer.update(presence="absent", arrived=None, trade_type="unknown", phase="absent",
                        dialogue_ref=None, dialogue_stage="absent", option_refs=[])
        self.state = state
        self.exchange(state=state)
        queued = self.broker.enqueue(self.request("receive_next_customer", command_id="ring-bell"))
        self.assertEqual(queued["command_status"], "pending")
        self.assertEqual(self.broker.records["ring-bell"]["command"]["execution_timeout_ms"], 30_000)
        delivered = self.exchange()
        self.assertEqual(delivered["command"]["action"], "receive_next_customer")

    def test_purchase_action_is_rejected_when_absent_from_current_legal_actions(self):
        request = self.request("purchase_visible_offer", {"customer_ref": "c", "offer_ref": "o",
            "item_refs": ["x"], "collection_fingerprint": "f", "mode": "normal", "amount": 1,
            "max_spend": 2, "authorization_ref": "auth"})
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(request)
        self.assertEqual(caught.exception.reason, "precondition")

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

    def test_only_pure_bargain_panels_support_durable_manual_alignment(self):
        from test_bargain import state as bargain_state
        parent = ROOT / 'artifacts' / 'm3b'
        parent.mkdir(parents=True, exist_ok=True)
        for action in ('open_bargain_panel', 'close_bargain_panel', 'choose_bargain_option', 'sell_visible_offer'):
            with self.subTest(action=action), tempfile.TemporaryDirectory(dir=parent) as directory:
                journal = Path(directory) / 'journal.json'
                state = (json.loads((ROOT / 'protocol/fixtures/m3a-sell.json').read_text(encoding='utf-8'))
                         if action == 'sell_visible_offer' else bargain_state(
                             phase='closed' if action == 'open_bargain_panel' else 'choosing', action=action))
                first = Broker('panel-alignment', clock=self.clock, monotonic=self.clock.monotonic,
                    heartbeat_timeout=60, journal_path=journal)
                def exchange(broker, snapshot, session):
                    return broker.exchange({'schema_version':3, 'session_id':session,
                        'broker_epoch':broker.broker_epoch, 'state':snapshot,
                        'has_result':False, 'result':None, 'progress':None})
                exchange(first, state, 'delivered-session')
                request = {'schema_version':3, 'id':f'unknown-{action}', 'broker_epoch':first.broker_epoch,
                    **state['legal_actions'][0], 'expected_world_epoch':state['world_epoch'],
                    'expected_revision':state['revision'], 'expected_version':state['version']}
                first.enqueue(request)
                delivered = exchange(first, state, 'delivered-session')
                self.assertEqual(delivered['command']['action'], action)
                first.close()
                restarted = Broker('panel-alignment', clock=self.clock, monotonic=self.clock.monotonic,
                    heartbeat_timeout=60, journal_path=journal)
                try:
                    self.assertEqual(restarted.result(request['id'])['status'], 'outcome_unknown')
                    advanced = copy.deepcopy(state)
                    advanced.update(revision=state['revision']+1,
                        version=f"{state['world_epoch']}:{state['revision']+1}", legal_actions=[])
                    exchange(restarted, advanced, 'delivered-session')
                    body = {'schema_version':3, 'id':request['id'], 'broker_epoch':restarted.broker_epoch,
                        'world_epoch':advanced['world_epoch'], 'revision':advanced['revision'],
                        'expected_version':advanced['version'], 'operator_note':'Fresh session reviewed; do not replay panel action.',
                        'confirm_no_replay':True}
                    if action not in {'open_bargain_panel', 'close_bargain_panel'}:
                        exchange(restarted, advanced, 'fresh-session')
                        self.clock.now += 60
                        exchange(restarted, advanced, 'fresh-session')
                        with self.assertRaises(ProtocolError) as caught:
                            restarted.reconcile(body)
                        self.assertEqual(caught.exception.reason, 'command_not_reconcilable')
                        self.assertEqual(restarted.result(request['id'])['status'], 'outcome_unknown')
                        self.assertFalse(restarted.result(request['id'])['resolved'])
                        record = copy.deepcopy(restarted.records[request['id']])
                        forbidden_resolution = {'schema_version':3, 'method':'manual_state_alignment',
                            'command_id':request['id'], 'broker_epoch':restarted.broker_epoch,
                            'command_expires_at_unix_ms':record['command']['start_deadline_unix_ms']
                                + record['command']['execution_timeout_ms'],
                            'session_id':'fresh-session', 'world_epoch':advanced['world_epoch'],
                            'revision':advanced['revision'], 'version':advanced['version'],
                            'snapshot':advanced, 'operator_note':body['operator_note'],
                            'confirm_no_replay':True, 'recorded_at_unix_ms':int(self.clock.now*1000)}
                        record['updated_at_unix_ms'] = int(self.clock.now*1000)
                        self.assertFalse(Journal._valid_resolution(record, forbidden_resolution, 3))
                        continue
                    with self.assertRaises(ProtocolError) as caught:
                        restarted.reconcile(body)
                    self.assertEqual(caught.exception.reason, 'fresh_mod_session_required')
                    exchange(restarted, advanced, 'fresh-session')
                    with self.assertRaises(ProtocolError) as caught:
                        restarted.reconcile(body)
                    self.assertEqual(caught.exception.reason, 'command_not_expired')
                    self.clock.now += 60
                    exchange(restarted, advanced, 'fresh-session')
                    with self.assertRaises(ProtocolError) as caught:
                        restarted.reconcile({**body, 'expected_version':'stale:1'})
                    self.assertEqual(caught.exception.reason, 'snapshot_mismatch_or_not_aligned')
                    exchange(restarted, state, 'fresh-session')
                    with self.assertRaises(ProtocolError) as caught:
                        restarted.reconcile({**body, 'revision':state['revision'], 'expected_version':state['version']})
                    self.assertEqual(caught.exception.reason, 'snapshot_not_advanced')
                    exchange(restarted, advanced, 'fresh-session')
                    reply = restarted.reconcile(body)
                    self.assertTrue(reply['resolved'])
                    self.assertEqual(reply['status'], 'outcome_unknown')
                    self.assertEqual(reply['result']['status'], 'outcome_unknown')
                    self.assertEqual(reply['resolution']['snapshot'], advanced)
                    self.assertIsNone(restarted.snapshot()['blocking_reason'])
                    self.assertFalse(exchange(restarted, advanced, 'fresh-session')['has_command'])
                    persisted = json.loads(journal.read_text(encoding='utf-8'))
                    self.assertTrue(Journal.valid_v2(persisted))
                    restarted.close()
                    restarted = Broker('panel-alignment', clock=self.clock, monotonic=self.clock.monotonic,
                        heartbeat_timeout=60, journal_path=journal)
                    restored = restarted.result(request['id'])
                    self.assertEqual(restored['status'], 'outcome_unknown')
                    self.assertTrue(restored['resolved'])
                    self.assertEqual(restored['resolution'], reply['resolution'])
                    self.assertFalse(exchange(restarted, advanced, 'third-session')['has_command'])
                finally:
                    restarted.close()
                    journal.unlink(missing_ok=True)
                    journal.with_suffix(journal.suffix + '.lock').unlink(missing_ok=True)

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
            def post(raw, wire_version=None, declared_length=None):
                conn = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                try:
                    headers = {"Authorization": "Bearer http-size-test", "Content-Type": "application/json"}
                    if wire_version is not None:
                        headers["X-PawnPilot-Wire-Version"] = str(wire_version)
                    if declared_length is not None:
                        # Oversize requests must be rejected from headers, before reading
                        # a body. Sending that body races the early close on Windows.
                        headers["Content-Length"] = str(declared_length)
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
            self.assertEqual(post(b"", declared_length=len(v2_padded))[0], 413)

            small_v3 = json.dumps(body, separators=(",", ":")).encode("utf-8")
            self.assertEqual(post(small_v3, 2)[0], 400)
            self.assertEqual(post(v2_raw, 3)[0], 400)

            too_large = raw + b" " * (MAX_V3_MESSAGE_BYTES + 1 - len(raw))
            self.assertEqual(len(too_large), MAX_V3_MESSAGE_BYTES + 1)
            self.assertEqual(post(b"", 3, declared_length=len(too_large))[0], 413)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


if __name__ == "__main__": unittest.main()
