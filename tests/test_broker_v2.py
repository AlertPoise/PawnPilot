"""Focused v2 broker tests using deterministic snapshots and a fake clock."""
from pathlib import Path
import sys
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bridge.protocol import request_fingerprint
from bridge.server import Broker, ProtocolError


class FakeClock:
    def __init__(self, value=100.0):
        self.value = value
        self.monotonic_value = value

    def __call__(self):
        return self.value

    def monotonic(self):
        return self.monotonic_value

    def advance(self, seconds):
        self.value += seconds
        self.monotonic_value += seconds


def make_state(revision=1, menu_open=True, can_close_menu=True, cash=550):
    legal_actions = [{"action": "close_menu", "arguments": {}}] if can_close_menu else []
    return {"schema_version": 2, "world_epoch": "broker-test-world", "revision": revision,
            "version": f"broker-test-world:{revision}", "scene": "InventoryScene", "loaded": True,
            "ready": True, "can_close_menu": can_close_menu, "not_ready_reason": None,
            "cash": cash, "phase": "morning", "menu_open": menu_open,
            "interaction": {"phase": "morning", "consistency": "unverified",
                            "reason": None, "operable": True},
            "business": {"support": "unsupported", "customer_ref": None, "item_refs": None,
                         "offer_ref": None, "collection_fingerprint": None},
            "legal_actions": legal_actions}


class BrokerV2Tests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.broker = Broker("unit-test", clock=self.clock, monotonic=self.clock.monotonic,
                             heartbeat_timeout=60)
        self.state = make_state()
        self.session = "test-mod-session"
        self.exchange()

    def exchange(self, result=None, progress=None, state=None, session=None):
        return self.broker.exchange({"schema_version": 2, "session_id": session or self.session,
                                     "broker_epoch": self.broker.broker_epoch,
                                     "state": state or self.state,
                                     "has_result": result is not None, "result": result,
                                     "progress": progress})

    def request(self, command_id=None, action="close_menu", arguments=None, **changes):
        request = {"schema_version": 2, "id": command_id or uuid.uuid4().hex,
                   "broker_epoch": self.broker.broker_epoch, "action": action,
                   "expected_world_epoch": self.state["world_epoch"],
                   "expected_revision": self.state["revision"],
                   "expected_version": self.state["version"],
                   "arguments": {} if arguments is None else arguments}
        request.update(changes)
        return request

    def queue(self, command_id=None, **kwargs):
        return self.broker.enqueue(self.request(command_id=command_id, **kwargs))

    def deliver(self, command_id=None):
        queued = self.queue(command_id)
        delivery = self.exchange()
        self.assertTrue(delivery["has_command"])
        return queued, delivery["command"]

    @staticmethod
    def progress(command, stage, sequence, effects, started=None, deadline=None):
        return {"id": command["id"], "broker_epoch": command["broker_epoch"],
                "stage": stage, "sequence": sequence, "may_have_side_effects": effects,
                "started_at_unix_ms": started, "execution_deadline_unix_ms": deadline}

    def rejection_result(self, command, sequence=1, reason="precondition"):
        return {"schema_version": 2, "id": command["id"],
                "broker_epoch": command["broker_epoch"], "status": "rejected",
                "reason": reason, "before": None, "after": None,
                "execution": self.progress(command, "completed", sequence, False)}

    def test_request_fingerprint_covers_arguments_and_preserves_item_order(self):
        base = self.request(command_id="same-id")
        with self.assertRaises(ProtocolError):
            self.broker.enqueue(dict(base, action="observe"))
        self.broker.enqueue(base)

        changed_args = dict(base, arguments={"customer_ref": "customer-1"})
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(changed_args)
        self.assertEqual(caught.exception.reason, "command_id_conflict")

        first = dict(base, arguments={"item_refs": ["item-a", "item-b"]})
        reversed_items = dict(base, arguments={"item_refs": ["item-b", "item-a"]})
        self.assertNotEqual(request_fingerprint(first), request_fingerprint(reversed_items))

    def test_generic_argument_shape_is_checked_before_id_dedupe(self):
        original = self.request(command_id="strict-id")
        self.broker.enqueue(original)
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(dict(original, extra="not-allowed"))
        self.assertEqual(caught.exception.reason, "invalid_command")
        with self.assertRaises(ProtocolError) as caught:
            self.broker.enqueue(dict(original, arguments={"item_refs": ["x", "x"]}))
        self.assertEqual(caught.exception.reason, "invalid_command")

    def test_only_close_menu_with_empty_arguments_can_be_queued(self):
        with self.assertRaises(ProtocolError) as caught:
            self.queue(action="observe")
        self.assertEqual(caught.exception.reason, "action_not_allowed")
        with self.assertRaises(ProtocolError) as caught:
            self.queue(arguments={"customer_ref": "customer-1"})
        self.assertEqual(caught.exception.reason, "action_not_allowed")
        self.assertEqual(len(self.broker.records), 0)

    def test_pending_expires_at_exact_start_deadline(self):
        queued = self.queue(command_id="pending-boundary")
        self.assertIsNone(queued["result"])
        command = self.broker.records["pending-boundary"]["command"]
        self.clock.value = command["start_deadline_unix_ms"] / 1000
        status = self.broker.result("pending-boundary")
        self.assertEqual(status["command_status"], "rejected")
        self.assertEqual(status["result"]["reason"], "expired_before_delivery")

    def test_delivered_without_invocation_becomes_unknown_at_start_deadline(self):
        _, command = self.deliver("delivery-boundary")
        self.clock.value = command["start_deadline_unix_ms"] / 1000
        status = self.broker.result(command["id"])
        self.assertEqual(status["command_status"], "outcome_unknown")
        self.assertIsNone(status["execution"])
        self.assertIsNone(status["result"]["execution"])

    def test_invocation_execution_deadline_is_inclusive_and_progress_is_monotonic(self):
        _, command = self.deliver("execution-boundary")
        started = int(self.clock() * 1000)
        self.assertLess(started, command["start_deadline_unix_ms"])
        deadline = started + command["execution_timeout_ms"]
        invoking = self.progress(command, "invoking", 1, True, started, deadline)
        self.exchange(progress=invoking)
        verifying = self.progress(command, "verifying", 2, True, started, deadline)
        self.exchange(progress=verifying)
        duplicate = self.exchange(progress=verifying)
        self.assertEqual(duplicate["schema_version"], 2)
        self.clock.value = deadline / 1000
        status = self.broker.result(command["id"])
        self.assertEqual(status["command_status"], "outcome_unknown")
        self.assertEqual(status["execution"], verifying)

    def test_wall_clock_rollback_does_not_extend_reported_execution_deadline(self):
        _, command = self.deliver("wall-clock-rollback")
        started = int(self.clock() * 1000)
        deadline = started + command["execution_timeout_ms"]
        invoking = self.progress(command, "invoking", 1, True, started, deadline)
        self.exchange(progress=invoking)
        self.clock.value -= 3600
        self.clock.advance(command["execution_timeout_ms"] / 1000)
        status = self.broker.result(command["id"])
        self.assertEqual(status["command_status"], "outcome_unknown")

    def test_reconcile_requires_latest_monotonic_execution_bound(self):
        _, command = self.deliver("reconcile-dual-clock")
        aligned = make_state(revision=2, menu_open=False, can_close_menu=False)
        self.clock.value = (command["start_deadline_unix_ms"]
                            + command["execution_timeout_ms"]) / 1000 + 10
        self.exchange(session="fresh-mod-session", state=aligned)
        self.assertEqual(self.broker.result(command["id"])["command_status"], "outcome_unknown")
        body = {"schema_version": 2, "id": command["id"],
                "broker_epoch": self.broker.broker_epoch,
                "world_epoch": aligned["world_epoch"], "revision": aligned["revision"],
                "expected_version": aligned["version"], "operator_note": "aligned after restart",
                "confirm_no_replay": True}
        with self.assertRaises(ProtocolError) as early:
            self.broker.reconcile(body)
        self.assertEqual(early.exception.reason, "command_not_expired")

        local_deadline = (self.broker.start_monotonic_deadlines[command["id"]]
                          + command["execution_timeout_ms"] / 1000)
        self.clock.monotonic_value = local_deadline
        reply = self.broker.reconcile(body)
        self.assertTrue(reply["resolved"])

    def test_late_no_invocation_rejection_resolves_delivery_unknown(self):
        _, command = self.deliver("late-reject")
        self.clock.value = command["start_deadline_unix_ms"] / 1000
        self.assertEqual(self.broker.result(command["id"])["command_status"], "outcome_unknown")

        result = self.rejection_result(command)
        reply = self.exchange(result=result, progress=result["execution"])
        self.assertEqual(reply["acknowledged_result_id"], command["id"])
        status = self.broker.result(command["id"])
        self.assertEqual(status["command_status"], "rejected")
        self.assertIsNone(self.broker.snapshot()["blocked_command_id"])

    def test_rejection_cannot_downgrade_prior_invoking_evidence(self):
        _, command = self.deliver("never-downgrade")
        started = int(self.clock() * 1000)
        deadline = started + command["execution_timeout_ms"]
        invoking = self.progress(command, "invoking", 1, True, started, deadline)
        self.exchange(progress=invoking)
        self.clock.value = deadline / 1000
        self.assertEqual(self.broker.result(command["id"])["command_status"], "outcome_unknown")

        result = self.rejection_result(command, sequence=2)
        with self.assertRaises(ProtocolError) as caught:
            self.exchange(result=result)
        self.assertEqual(caught.exception.reason, "invalid_result_evidence")
        self.assertEqual(self.broker.result(command["id"])["command_status"], "outcome_unknown")
        self.assertEqual(self.broker.records[command["id"]]["execution"], invoking)

    def test_terminal_progress_requires_and_cannot_replace_terminal_result(self):
        _, command = self.deliver("terminal-progress")
        rejected = self.progress(command, "completed", 1, False)
        with self.assertRaises(ProtocolError) as caught:
            self.exchange(progress=rejected)
        self.assertEqual(caught.exception.reason, "terminal_progress_requires_result")
        self.assertIsNone(self.broker.records[command["id"]]["execution"])

        result = self.rejection_result(command)
        self.exchange(result=result, progress=result["execution"])
        saved = self.broker.records[command["id"]]["execution"]
        uncertain = self.progress(command, "uncertain", 2, True,
                                  int(self.clock() * 1000),
                                  int(self.clock() * 1000) + command["execution_timeout_ms"])
        with self.assertRaises(ProtocolError):
            self.exchange(progress=uncertain)
        self.assertEqual(self.broker.records[command["id"]]["execution"], saved)
        self.assertEqual(self.broker.result(command["id"])["command_status"], "rejected")

    def test_success_requires_close_menu_evidence_and_unchanged_cash(self):
        _, command = self.deliver("cash-predicate")
        started = int(self.clock() * 1000)
        execution = self.progress(command, "completed", 1, True, started,
                                  started + command["execution_timeout_ms"])
        after = make_state(revision=2, menu_open=False, can_close_menu=False, cash=550)
        succeeded = {"schema_version": 2, "id": command["id"],
                     "broker_epoch": command["broker_epoch"], "status": "succeeded",
                     "reason": "verified", "before": self.state, "after": after,
                     "execution": execution}
        bad = dict(succeeded, after=make_state(revision=2, menu_open=False,
                                               can_close_menu=False, cash=549))
        with self.assertRaises(ProtocolError) as caught:
            self.exchange(result=bad, progress=execution, state=bad["after"])
        self.assertEqual(caught.exception.reason, "invalid_result_evidence")
        self.exchange(result=succeeded, progress=execution, state=after)
        self.assertEqual(self.broker.result(command["id"])["command_status"], "succeeded")


if __name__ == "__main__":
    unittest.main(verbosity=2)
