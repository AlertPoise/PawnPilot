"""Crash/restart behavior of the durable broker command journal."""
from pathlib import Path
import sys
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.journal import JournalError, MAX_JOURNAL_BYTES
from bridge.server import Broker, ProtocolError


class BrokerRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.path = ROOT / f".recovery-test-{uuid.uuid4().hex}.json"
        self.now = 100.0
        self.broker = Broker("token", clock=lambda: self.now, journal_path=self.path)
        self.epochs = {id(self.broker): None}

    def tearDown(self):
        self.broker.close()
        self.path.unlink(missing_ok=True)
        self.path.with_suffix(self.path.suffix + ".lock").unlink(missing_ok=True)

    def exchange(self, broker=None, session="game-session", result=None, state=None):
        broker = broker or self.broker
        self.epochs.setdefault(id(broker), None)
        reply = broker.exchange({"schema_version": 1, "broker_epoch": self.epochs[id(broker)],
                                 "session_id": session, "state": state or self.state(),
                                 "has_result": result is not None, "result": result})
        self.epochs[id(broker)] = reply["broker_epoch"]
        return reply

    @staticmethod
    def state(revision=9, menu_open=True):
        return {"schema_version": 1, "world_epoch": "world-a", "revision": revision,
                "version": f"world-a:{revision}", "scene": "InventoryScene", "loaded": True,
                "ready": True, "can_close_menu": menu_open, "not_ready_reason": None,
                "cash": 50, "phase": "morning", "menu_open": menu_open}

    def result(self, command, status="success", reason="game_confirmed"):
        return {"schema_version": 1, "id": command["id"],
                "broker_epoch": command["broker_epoch"], "status": status, "reason": reason,
                "before": self.state(), "after": self.state(10, False)}

    def enqueue(self, broker=None, command_id=None, action="close_menu", revision=9):
        broker = broker or self.broker
        return broker.enqueue({"schema_version": 1, "id": command_id or uuid.uuid4().hex,
                               "broker_epoch": broker.broker_epoch, "action": action,
                               "expected_world_epoch": "world-a", "expected_revision": revision,
                               "expected_version": f"world-a:{revision}"})

    def test_pending_record_is_rejected_on_restart_and_never_delivered(self):
        self.exchange()
        queued = self.enqueue()
        old_epoch = self.broker.broker_epoch
        self.broker.journal.release()  # Simulate abrupt process loss before server shutdown handling.
        recovered = Broker("token", clock=lambda: self.now, journal_path=self.path)
        self.epochs[id(recovered)] = old_epoch
        try:
            self.assertNotEqual(recovered.broker_epoch, old_epoch)
            self.assertEqual(recovered.result(queued["id"])["command_status"], "rejected")
            self.assertEqual(recovered.result(queued["id"])["result"]["reason"],
                             "broker_restart_before_delivery")
            self.assertIsNone(self.exchange(recovered)["command"])
            self.assertIsNone(recovered.pending)
        finally:
            recovered.close()
            self.broker.journal = None

    def test_running_restart_blocks_until_matching_retained_result_reconciles(self):
        self.exchange()
        queued = self.enqueue()
        delivered = self.exchange()["command"]
        old_epoch = self.broker.broker_epoch
        self.broker.journal.release()  # Crash after durable running transition.
        recovered = Broker("token", clock=lambda: self.now, journal_path=self.path)
        self.epochs[id(recovered)] = old_epoch
        try:
            self.assertNotEqual(recovered.broker_epoch, old_epoch)
            self.assertEqual(recovered.result(queued["id"])["command_status"], "outcome_unknown")
            self.assertFalse(recovered.snapshot()["can_act"])
            with self.assertRaises(ProtocolError) as blocked:
                self.enqueue(recovered)
            self.assertEqual(blocked.exception.reason, "game_disconnected")
            result = self.result(delivered)
            self.assertEqual(self.exchange(recovered, result=result)["acknowledged_result_id"], delivered["id"])
            self.assertEqual(recovered.result(queued["id"])["command_status"], "succeeded")
            self.assertTrue(recovered.snapshot()["can_act"])
        finally:
            recovered.close()
            self.broker.journal = None

    def test_unknown_record_survives_second_restart_before_reconciliation(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        self.broker.journal.release()
        first_recovery = Broker("token", clock=lambda: self.now, journal_path=self.path)
        try:
            self.assertEqual(first_recovery.result(queued["id"])["command_status"], "outcome_unknown")
            first_recovery.journal.release()
            second_recovery = Broker("token", clock=lambda: self.now, journal_path=self.path)
            try:
                retained = second_recovery.result(queued["id"])
                self.assertEqual(retained["command_status"], "outcome_unknown")
                self.assertEqual(retained["result"]["reason"], "broker_restart_during_execution")
                self.assertFalse(second_recovery.snapshot()["can_act"])
            finally:
                second_recovery.close()
        finally:
            first_recovery.journal = None
            self.broker.journal = None

    def test_same_identity_is_idempotent_but_modified_request_conflicts(self):
        self.exchange()
        command_id = uuid.uuid4().hex
        first = self.enqueue(command_id=command_id)
        self.assertEqual(self.enqueue(command_id=command_id), first)
        body = {"schema_version": 1, "id": command_id, "broker_epoch": self.broker.broker_epoch,
                "action": "observe", "expected_world_epoch": "world-a",
                "expected_revision": 9, "expected_version": "world-a:9"}
        with self.assertRaises(ProtocolError) as conflict:
            self.broker.enqueue(body)
        self.assertEqual(conflict.exception.reason, "command_id_conflict")

    def test_result_requires_originating_session_and_epoch(self):
        self.exchange()
        command = self.enqueue()
        delivered = self.exchange()["command"]
        result = self.result(self.broker.records[command["id"]]["command"])
        with self.assertRaises(ProtocolError):
            self.exchange(session="different-session", result=result)
        self.assertEqual(self.exchange(result=result)["acknowledged_result_id"], delivered["id"])

    def test_corrupt_journal_fails_closed_at_startup(self):
        self.broker.journal.release()
        self.path.write_text('{truncated', encoding="utf-8")
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
        result = self.result(delivered, reason="ok")
        self.exchange(result=result)
        with self.assertRaises(ProtocolError) as full:
            self.enqueue()
        self.assertEqual(full.exception.reason, "command_journal_full")
        self.assertEqual(self.broker.result(first["id"])["command_status"], "succeeded")

    def test_manual_reconcile_keeps_unknown_status_and_records_alignment_evidence(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        self.now += 6
        self.exchange(state=self.state(10, False))
        self.assertEqual(self.broker.result(queued["id"])["command_status"], "outcome_unknown")
        body = {"schema_version": 1, "id": queued["id"], "broker_epoch": self.broker.broker_epoch,
                "world_epoch": "world-a", "revision": 10, "expected_version": "world-a:10",
                "operator_note": "I reviewed the live game and will not replay the action.",
                "confirm_no_replay": True}
        answer = self.broker.reconcile(body)
        self.assertEqual(answer["command_status"], "outcome_unknown")
        self.assertTrue(answer["resolved"])
        self.assertTrue(self.broker.snapshot()["can_act"])
        next_command = self.enqueue(action="observe", revision=10)
        late_result = self.result(self.broker.records[queued["id"]]["command"])
        reply = self.exchange(result=late_result, state=self.state(10, False))
        self.assertEqual(reply["acknowledged_result_id"], queued["id"])
        self.assertEqual(reply["command"]["id"], next_command["id"])
        self.assertEqual(self.broker.result(queued["id"])["command_status"], "succeeded")
        self.assertTrue(self.broker.result(queued["id"])["resolved"])
        self.broker.journal.release()
        recovered = Broker("token", clock=lambda: self.now, journal_path=self.path)
        try:
            record = recovered.result(queued["id"])
            self.assertEqual(record["command_status"], "succeeded")
            self.assertTrue(record["resolution"]["confirm_no_replay"])
            self.exchange(recovered, state=self.state(10, False))
            self.assertEqual(recovered.result(next_command["id"])["command_status"], "outcome_unknown")
            self.assertFalse(recovered.snapshot()["can_act"])
        finally:
            recovered.close()
            self.broker.journal = None

    def test_manual_reconcile_rejects_missing_ack_stale_tuple_and_non_unknown(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        self.now += 6
        self.exchange(state=self.state(10, False))
        body = {"schema_version": 1, "id": queued["id"], "broker_epoch": self.broker.broker_epoch,
                "world_epoch": "world-a", "revision": 10, "expected_version": "world-a:10",
                "operator_note": "Reviewed live state before continuing.", "confirm_no_replay": False}
        with self.assertRaises(ProtocolError) as no_ack:
            self.broker.reconcile(body)
        self.assertEqual(no_ack.exception.status, 400)
        body["confirm_no_replay"] = True
        body["revision"] = 9
        body["expected_version"] = "world-a:9"
        with self.assertRaises(ProtocolError) as stale:
            self.broker.reconcile(body)
        self.assertEqual(stale.exception.reason, "snapshot_mismatch_or_not_aligned")

    def test_manual_resolved_unknown_survives_restart_before_any_late_result(self):
        self.exchange()
        queued = self.enqueue()
        self.exchange()
        self.now += 6
        self.exchange(state=self.state(10, False))
        self.broker.reconcile({"schema_version": 1, "id": queued["id"],
                               "broker_epoch": self.broker.broker_epoch,
                               "world_epoch": "world-a", "revision": 10,
                               "expected_version": "world-a:10",
                               "operator_note": "Reviewed the current game and will not replay.",
                               "confirm_no_replay": True})
        self.broker.journal.release()
        recovered = Broker("token", clock=lambda: self.now, journal_path=self.path)
        try:
            status = recovered.result(queued["id"])
            self.assertEqual(status["command_status"], "outcome_unknown")
            self.assertTrue(status["resolved"])
            self.assertEqual(status["resolution"]["revision"], 10)
            self.assertFalse(recovered.snapshot()["can_act"])  # A fresh Bridge snapshot is still required.
            self.exchange(recovered, state=self.state(10, False))
            self.assertTrue(recovered.snapshot()["can_act"])
            observed = self.enqueue(recovered, action="observe", revision=10)
            self.assertEqual(observed["command_status"], "pending")
            retained = recovered.result(queued["id"])
            self.assertEqual(retained["command_status"], "outcome_unknown")
            self.assertTrue(retained["resolved"])
        finally:
            recovered.close()
            self.broker.journal = None

    def test_oversize_journal_write_preserves_previous_durable_file(self):
        original = self.path.read_bytes()
        with self.assertRaises(JournalError):
            self.broker.journal.save({"payload": "x" * MAX_JOURNAL_BYTES})
        self.assertEqual(self.path.read_bytes(), original)


if __name__ == "__main__":
    unittest.main(verbosity=2)
