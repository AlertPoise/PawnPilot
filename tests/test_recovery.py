"""V2 broker/journal crash recovery and legacy-history migration coverage."""
import hashlib
import json
from pathlib import Path
import sys
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.journal import Journal, JournalError, MAX_JOURNAL_BYTES
from bridge.server import Broker, ProtocolError
from bridge.protocol import request_fingerprint


def snapshot(revision=9, menu_open=True, world="world-a", cash=50):
    return {"schema_version": 2, "version": f"{world}:{revision}", "world_epoch": world,
            "revision": revision, "scene": "InventoryScene", "loaded": True, "ready": True,
            "not_ready_reason": None, "cash": cash, "phase": "MORNING", "menu_open": menu_open,
            "can_close_menu": menu_open,
            "interaction": {"phase": "menu" if menu_open else "store", "consistency": "unverified",
                            "reason": None, "operable": True},
            "business": {"support": "unsupported", "customer_ref": None, "item_refs": None,
                          "offer_ref": None, "collection_fingerprint": None},
            "legal_actions": ([{"action": "close_menu", "arguments": {}}] if menu_open else [])}


def legacy_v1_journal():
    request = {"schema_version": 1, "id": "old-pending", "broker_epoch": "legacy-broker",
               "action": "close_menu", "expected_world_epoch": "legacy-world",
               "expected_revision": 3, "expected_version": "legacy-world:3"}
    fingerprint = hashlib.sha256(json.dumps(request, sort_keys=True, separators=(",", ":"),
                                                ensure_ascii=False).encode("utf-8")).hexdigest()
    command = {"schema_version": 1, "id": request["id"], "action": request["action"],
               "broker_epoch": request["broker_epoch"], "expected_world_epoch": request["expected_world_epoch"],
               "expected_revision": request["expected_revision"], "expected_version": request["expected_version"],
               "expires_at_unix_ms": 12_000}
    record = {"command_id": request["id"], "request_fingerprint": fingerprint,
              "request": request, "command": command, "command_status": "pending", "result": None,
              "delivery_session_id": None, "created_at_unix_ms": 10_000, "updated_at_unix_ms": 10_000}
    return {"schema_version": 1, "broker_epoch": "legacy-broker", "records": [record]}


class BrokerRecoveryTests(unittest.TestCase):
    def setUp(self):
        temp_root = ROOT / ".test-tmp"
        temp_root.mkdir(parents=True, exist_ok=True)
        self.path = temp_root / f"m2b-recovery-{uuid.uuid4().hex}.json"
        self.now = 100.0
        self.broker = Broker("token", clock=lambda: self.now, monotonic=lambda: self.now,
                             journal_path=self.path)
        self.epochs = {id(self.broker): None}

    def tearDown(self):
        if self.broker:
            self.broker.close()
        for path in (self.path, self.path.with_suffix(self.path.suffix + ".lock"),
                     self.path.with_suffix(self.path.suffix + ".v1.bak")):
            path.unlink(missing_ok=True)

    def exchange(self, broker=None, session="game-session", result=None, state=None, progress=None, epoch=None):
        broker = broker or self.broker
        self.epochs.setdefault(id(broker), None)
        body_epoch = self.epochs[id(broker)] if epoch is None else epoch
        reply = broker.exchange({"schema_version": 2, "broker_epoch": body_epoch,
                                 "session_id": session, "state": state or snapshot(),
                                 "has_result": result is not None, "result": result,
                                 "progress": progress if progress is not None else
                                 (result["execution"] if result is not None else None)})
        self.epochs[id(broker)] = reply["broker_epoch"]
        return reply

    @staticmethod
    def state(revision=9, menu_open=True):
        return snapshot(revision, menu_open)

    def result(self, command, status="succeeded", reason="game_confirmed"):
        started = command["start_deadline_unix_ms"] - 1
        execution = {"id": command["id"], "broker_epoch": command["broker_epoch"],
                     "stage": "completed", "sequence": 1, "may_have_side_effects": True,
                     "started_at_unix_ms": started,
                     "execution_deadline_unix_ms": started + command["execution_timeout_ms"]}
        return {"schema_version": 2, "id": command["id"], "broker_epoch": command["broker_epoch"],
                "status": status, "reason": reason, "before": snapshot(command["expected_revision"], True),
                "after": snapshot(command["expected_revision"] + 1, False), "execution": execution}

    def enqueue(self, broker=None, command_id=None, arguments=None, revision=9):
        broker = broker or self.broker
        return broker.enqueue({"schema_version": 2, "id": command_id or uuid.uuid4().hex,
                               "broker_epoch": broker.broker_epoch, "action": "close_menu",
                               "expected_world_epoch": "world-a", "expected_revision": revision,
                               "expected_version": f"world-a:{revision}",
                               "arguments": arguments or {}})

    def restart(self, broker, prior_epoch):
        broker.journal.release()
        recovered = Broker("token", clock=lambda: self.now, monotonic=lambda: self.now,
                            journal_path=self.path)
        self.epochs[id(recovered)] = prior_epoch
        return recovered

    def test_pending_record_is_rejected_on_restart_and_never_delivered(self):
        self.exchange()
        queued = self.enqueue()
        old_epoch = self.broker.broker_epoch
        recovered = self.restart(self.broker, old_epoch)
        try:
            self.assertNotEqual(recovered.broker_epoch, old_epoch)
            self.assertEqual(recovered.result(queued["id"])["command_status"], "rejected")
            self.assertEqual(recovered.result(queued["id"])["result"]["reason"],
                             "broker_restart_before_delivery")
            self.assertIsNone(self.exchange(recovered)["command"])
            self.assertIsNone(recovered.pending)
        finally:
            recovered.close()
            self.broker = None

    def test_v1_pending_migrates_from_verified_backup_and_keeps_v1_evidence(self):
        legacy = legacy_v1_journal()
        raw = json.dumps(legacy, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        self.broker.close()
        self.broker = None
        self.path.write_bytes(raw)
        recovered = Broker("token", clock=lambda: self.now, monotonic=lambda: self.now,
                            journal_path=self.path)
        try:
            record = recovered.result("old-pending")
            self.assertEqual(record["command_status"], "rejected")
            self.assertEqual(record["record_schema_version"], 1)
            self.assertEqual(record["result"]["schema_version"], 1)
            self.assertEqual(self.path.with_suffix(self.path.suffix + ".v1.bak").read_bytes(), raw)
            stored = json.loads(self.path.read_text(encoding="utf-8"))
            self.assertEqual(stored["journal_format_version"], 2)
            self.assertNotIn("execution", stored["records"][0])
        finally:
            recovered.close()

    def test_running_restart_blocks_until_matching_retained_result_reconciles(self):
        self.exchange()
        queued = self.enqueue()
        delivered = self.exchange()["command"]
        old_epoch = self.broker.broker_epoch
        recovered = self.restart(self.broker, old_epoch)
        try:
            self.assertEqual(recovered.result(queued["id"])["command_status"], "outcome_unknown")
            self.assertFalse(recovered.snapshot()["can_act"])
            self.exchange(recovered, session="fresh-mod-session", state=self.state())
            with self.assertRaises(ProtocolError) as blocked:
                self.enqueue(recovered)
            self.assertEqual(blocked.exception.reason, "command_in_flight")
            result = self.result(delivered)
            reply = self.exchange(recovered, result=result, epoch=old_epoch)
            self.assertEqual(reply["acknowledged_result_id"], delivered["id"])
            self.assertEqual(recovered.result(queued["id"])["command_status"], "succeeded")
            self.assertTrue(recovered.snapshot()["can_act"])
        finally:
            recovered.close()
            self.broker = None

    def test_unknown_record_survives_second_restart_before_reconciliation(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        first = self.restart(self.broker, self.broker.broker_epoch)
        try:
            self.assertEqual(first.result(queued["id"])["command_status"], "outcome_unknown")
            first_epoch = first.broker_epoch
            first.journal.release()
            second = Broker("token", clock=lambda: self.now, monotonic=lambda: self.now,
                            journal_path=self.path)
            try:
                retained = second.result(queued["id"])
                self.assertNotEqual(second.broker_epoch, first_epoch)
                self.assertEqual(retained["command_status"], "outcome_unknown")
                self.assertEqual(retained["result"]["reason"], "broker_restart_during_execution")
                self.assertFalse(second.snapshot()["can_act"])
                self.assertIsNone(self.exchange(second)["command"])
            finally:
                second.close()
        finally:
            self.broker = None

    def test_same_identity_is_idempotent_but_modified_arguments_conflict(self):
        self.exchange()
        command_id = uuid.uuid4().hex
        first = self.enqueue(command_id=command_id)
        self.assertEqual(self.enqueue(command_id=command_id), first)
        with self.assertRaises(ProtocolError) as conflict:
            self.enqueue(command_id=command_id, arguments={"customer_ref": "customer-1"})
        self.assertEqual(conflict.exception.reason, "command_id_conflict")

    def test_result_requires_originating_session_and_epoch(self):
        self.exchange()
        command = self.enqueue()
        delivered = self.exchange()["command"]
        result = self.result(delivered)
        with self.assertRaises(ProtocolError):
            self.exchange(session="different-session", result=result)
        self.assertEqual(self.exchange(result=result)["acknowledged_result_id"], delivered["id"])

    def test_corrupt_journal_fails_closed_at_startup(self):
        self.broker.journal.release()
        self.path.write_text("{truncated", encoding="utf-8")
        with self.assertRaises(JournalError):
            Broker("token", journal_path=self.path)
        self.broker.journal = None

    def test_second_broker_owner_is_rejected(self):
        with self.assertRaises(JournalError):
            Broker("token", journal_path=self.path)

    def test_persistence_error_poisoning_prevents_followup_mutations(self):
        self.exchange()
        self.broker.journal.save = lambda _: (_ for _ in ()).throw(JournalError("disk full"))
        with self.assertRaises(ProtocolError) as failed:
            self.enqueue()
        self.assertEqual(failed.exception.reason, "broker_persistence_failed")
        with self.assertRaises(ProtocolError) as poisoned:
            self.enqueue(command_id=uuid.uuid4().hex)
        self.assertEqual(poisoned.exception.reason, "broker_persistence_failed")

    def test_bounded_journal_fails_closed_without_eviction(self):
        self.broker.max_records = 1
        self.exchange()
        first = self.enqueue()
        delivered = self.exchange()["command"]
        self.exchange(result=self.result(delivered))
        with self.assertRaises(ProtocolError) as full:
            self.enqueue()
        self.assertEqual(full.exception.reason, "command_journal_full")
        self.assertEqual(self.broker.result(first["id"])["command_status"], "succeeded")

    def _unknown_on_fresh_session(self, queued):
        command = self.broker.records[queued["id"]]["command"]
        self.now = (command["start_deadline_unix_ms"] + command["execution_timeout_ms"]) / 1000
        self.exchange(session="fresh-mod-session", state=self.state(10, False))
        self.assertEqual(self.broker.result(queued["id"])["command_status"], "outcome_unknown")

    def _resolution_body(self, queued, revision=10, confirm=True):
        return {"schema_version": 2, "id": queued["id"], "broker_epoch": self.broker.broker_epoch,
                "world_epoch": "world-a", "revision": revision, "expected_version": f"world-a:{revision}",
                "operator_note": "Reviewed live state and confirmed no replay.",
                "confirm_no_replay": confirm}

    def test_manual_reconcile_keeps_unknown_and_records_alignment_evidence(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        self._unknown_on_fresh_session(queued)
        answer = self.broker.reconcile(self._resolution_body(queued))
        self.assertEqual(answer["command_status"], "outcome_unknown")
        self.assertTrue(answer["resolved"])
        self.assertTrue(self.broker.snapshot()["can_act"])
        # A late result from the original session may resolve history, but cannot re-open a lease.
        original = self.broker.records[queued["id"]]["command"]
        late = self.result(original)
        reply = self.exchange(session="game-session", result=late, state=self.state(10, False), epoch=original["broker_epoch"])
        self.assertEqual(reply["acknowledged_result_id"], queued["id"])
        self.assertEqual(self.broker.result(queued["id"])["command_status"], "succeeded")
        self.assertTrue(self.broker.result(queued["id"])["resolved"])
        self.broker.journal.release()
        recovered = Broker("token", clock=lambda: self.now, monotonic=lambda: self.now,
                            journal_path=self.path)
        try:
            record = recovered.result(queued["id"])
            self.assertEqual(record["command_status"], "succeeded")
            self.assertTrue(record["resolution"]["confirm_no_replay"])
        finally:
            recovered.close()
            self.broker = None

    def test_manual_reconcile_requires_elapsed_deadline_fresh_session_alignment_and_ack(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        command = self.broker.records[queued["id"]]["command"]
        self.now = (command["start_deadline_unix_ms"] + command["execution_timeout_ms"]) / 1000 - 0.001
        self.exchange(session="game-session", state=self.state(10, False))
        self.assertEqual(self.broker.result(queued["id"])["command_status"], "outcome_unknown")
        with self.assertRaises(ProtocolError) as same_session:
            self.broker.reconcile(self._resolution_body(queued))
        self.assertEqual(same_session.exception.reason, "fresh_mod_session_required")
        self.exchange(session="fresh-mod-session", state=self.state(10, False))
        with self.assertRaises(ProtocolError) as early:
            self.broker.reconcile(self._resolution_body(queued))
        self.assertEqual(early.exception.reason, "command_not_expired")
        self.now += 0.002
        self.exchange(session="fresh-mod-session", state=self.state(10, False))
        body = self._resolution_body(queued, confirm=False)
        with self.assertRaises(ProtocolError):
            self.broker.reconcile(body)
        body["confirm_no_replay"] = True
        body["revision"] = 9
        body["expected_version"] = "world-a:9"
        with self.assertRaises(ProtocolError):
            self.broker.reconcile(body)
        self.assertFalse(self.broker.snapshot()["can_act"])

    def test_reconcile_does_not_release_on_forward_wall_clock_jump(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        self.broker.monotonic = lambda: 100.1  # The game process clock has barely advanced.
        command = self.broker.records[queued["id"]]["command"]
        self.now = (command["start_deadline_unix_ms"] + command["execution_timeout_ms"]) / 1000 + 10
        self.exchange(session="fresh-mod-session", state=self.state(10, False))
        self.assertEqual(self.broker.result(queued["id"])["command_status"], "outcome_unknown")
        with self.assertRaises(ProtocolError) as early:
            self.broker.reconcile(self._resolution_body(queued))
        self.assertEqual(early.exception.reason, "command_not_expired")

    def test_resolved_unknown_survives_restart_and_stays_unknown_history(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        self._unknown_on_fresh_session(queued)
        self.broker.reconcile(self._resolution_body(queued))
        self.broker.journal.release()
        recovered = Broker("token", clock=lambda: self.now, monotonic=lambda: self.now,
                            journal_path=self.path)
        try:
            status = recovered.result(queued["id"])
            self.assertEqual(status["command_status"], "outcome_unknown")
            self.assertTrue(status["resolved"])
            self.assertFalse(recovered.snapshot()["can_act"])  # A fresh Bridge snapshot is still required.
            self.exchange(recovered, session="fresh-mod-session", state=self.state(10, False))
            self.assertTrue(recovered.snapshot()["can_act"])
            self.exchange(recovered, session="fresh-mod-session", state=self.state(11, True))
            next_command = self.enqueue(recovered, revision=11)
            self.assertEqual(next_command["command_status"], "pending")
            retained = recovered.result(queued["id"])
            self.assertEqual(retained["command_status"], "outcome_unknown")
            self.assertTrue(retained["resolved"])
        finally:
            recovered.close()
            self.broker = None

    def test_oversize_journal_write_preserves_previous_durable_file(self):
        original = self.path.read_bytes()
        with self.assertRaises(JournalError):
            self.broker.journal.save({"payload": "x" * MAX_JOURNAL_BYTES})
        self.assertEqual(self.path.read_bytes(), original)


if __name__ == "__main__":
    unittest.main(verbosity=2)
