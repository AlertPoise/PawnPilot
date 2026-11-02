"""Managed-run authorization and crash recovery through the production Broker."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import shutil
import threading
import unittest
import uuid

from bridge.journal import Journal
from bridge.protocol import request_fingerprint
from bridge.run_store import RunStore, RunStoreError
from bridge.server import Broker, ProtocolError
from tests.test_run_store import day_snapshot_v4
from tests.test_protocol_v4 import (V4_INTERACTION_FIXTURE, command_v4, progress, result_for,
                                    snapshot_v4, transitioned_after)


ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT / "artifacts" / "m5c"
LIMIT_KEYS = {"customers", "writes", "trades", "buy_spend", "bargains", "day_fees", "days"}


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now

    def monotonic(self):
        return self.now


def managed_snapshot():
    state = snapshot_v4()
    state["business"]["details"]["day"] = {
        "day_number": 1,
        "remaining_customers": 2,
        "stage": "open",
        "reason": None,
        "can_close": False,
        "can_sleep": False,
        "can_confirm": False,
        "report_open": False,
        "transition_pending": False,
        "settlement": None,
    }
    return state


class RunBrokerTests(unittest.TestCase):
    def setUp(self):
        self.temp = TEST_ROOT / ("test-run-broker-" + uuid.uuid4().hex)
        self.temp.mkdir(parents=True, exist_ok=False)
        self.journal_path = self.temp / "broker-journal.json"
        self.run_path = self.temp / "run-context.json"
        self.clock = FakeClock()
        self.session = "managed-mod-session"
        self.owner = "supervisor-test-owner"
        self.state = managed_snapshot()
        self.broker = None
        self.store = None

    def tearDown(self):
        if self.broker is not None:
            self.broker.close()
            self.broker = None
        shutil.rmtree(self.temp, ignore_errors=True)

    def _new_store(self, *, limits=None, allowed_actions=None, state=None, session=None,
                   journal_path=None, run_path=None):
        state = state or self.state
        session = session or self.session
        journal_path = journal_path or self.journal_path
        run_path = run_path or self.run_path
        limits = limits or {key: 20 for key in LIMIT_KEYS}
        allowed_actions = allowed_actions or ["choose_dialogue_option"]
        self.assertEqual(set(limits), LIMIT_KEYS)
        day = state["business"]["details"]["day"]["day_number"]
        store = RunStore(run_path)
        store.create("managed-test-run", "fixture-save", str(journal_path), state["world_epoch"],
                     session, day, day + 1, limits, allowed_actions, "Complete the authorized day.")
        return store

    def _start_managed_broker(self, *, limits=None, allowed_actions=None):
        self.store = self._new_store(limits=limits, allowed_actions=allowed_actions)
        self.broker = Broker("run-test-token", clock=self.clock, monotonic=self.clock.monotonic,
                             heartbeat_timeout=60, journal_path=self.journal_path, run_path=self.run_path)
        self._exchange(self.state)
        self.broker.snapshot()  # Establish the Broker/run binding and reconcile the Journal.
        context = self.store.snapshot()
        context = self.store.control("resume", context["revision"])
        self.broker.snapshot()  # Observe the resumed control state before granting a dispatch.
        return context

    def _exchange(self, state, *, result=None, session=None, broker=None):
        broker = broker or self.broker
        session = session or self.session
        return broker.exchange({
            "schema_version": 4,
            "session_id": session,
            "broker_epoch": broker.broker_epoch,
            "state": state,
            "has_result": result is not None,
            "result": result,
            "progress": result["execution"] if result is not None else None,
        })

    def _request(self, command_id="managed-command-1", state=None, broker=None):
        broker = broker or self.broker
        state = state or self.state
        request = {key: value for key, value in command_v4(state, command_id=command_id).items()
                   if key not in {"start_deadline_unix_ms", "execution_timeout_ms"}}
        request["broker_epoch"] = broker.broker_epoch
        return request

    def _grant(self):
        return self.store.grant(self.owner, ttl_seconds=300)

    def _enqueue(self, command_id="managed-command-1", *, token=None, state=None, broker=None):
        broker = broker or self.broker
        return broker.enqueue(self._request(command_id, state, broker), dispatch_token=token)

    def test_managed_write_requires_dispatch_token_and_unmanaged_v4_remains_available(self):
        self._start_managed_broker()
        with self.assertRaises(ProtocolError) as blocked:
            self._enqueue()
        self.assertEqual(blocked.exception.status, 409)
        self.assertEqual(blocked.exception.reason, "run_store_invalid_dispatch_token")
        self.assertEqual(self.broker.records, {})
        self.assertEqual(self.store.snapshot()["budget"]["writes"], 0)

        self.broker.close()
        self.broker = Broker("unmanaged-v4", clock=self.clock, monotonic=self.clock.monotonic,
                             heartbeat_timeout=60)
        self._exchange(self.state)
        accepted = self._enqueue("unmanaged-v4-command")
        self.assertEqual(accepted["command_status"], "pending")

    def test_one_dispatch_authorizes_one_new_command_and_duplicate_id_is_idempotent(self):
        self._start_managed_broker()
        token = self._grant()
        first = self._enqueue(token=token)
        self.assertEqual(first["command_status"], "pending")
        budget_after_first = self.store.snapshot()["budget"]

        duplicate = self._enqueue(token=None)
        self.assertEqual(duplicate["id"], first["id"])
        self.assertEqual(self.store.snapshot()["budget"], budget_after_first)
        self.assertEqual(len(self.store.snapshot()["commands"]), 1)

        with self.assertRaises(ProtocolError) as second_write:
            self._enqueue("managed-command-2", token=token)
        self.assertIn("dispatch", second_write.exception.reason)
        self.assertNotIn("managed-command-2", self.broker.records)
        self.assertEqual(self.store.snapshot()["budget"]["writes"], 1)

    def test_write_budget_prevents_a_second_grant_after_one_durable_write(self):
        limits = {key: 20 for key in LIMIT_KEYS}
        limits["writes"] = 1
        self._start_managed_broker(limits=limits)
        self._enqueue(token=self._grant())
        context = self.store.snapshot()
        self.assertEqual(context["budget"]["writes"], 1)
        self.assertEqual(context["authorization"]["limits"]["writes"], 1)
        with self.assertRaises(RunStoreError) as exhausted:
            self.store.grant(self.owner, ttl_seconds=300)
        self.assertEqual(exhausted.exception.reason, "run_store_budget_exceeded")

    def test_end_of_day_budget_reserves_fee_and_day_once(self):
        self.state = day_snapshot_v4("confirm_end_of_day", 1, 1, 100)
        limits = {key: 20 for key in LIMIT_KEYS}
        limits.update(writes=1, day_fees=40, days=1)
        self._start_managed_broker(limits=limits, allowed_actions=["confirm_end_of_day"])
        action = self.state["legal_actions"][0]
        request = {
            "schema_version": 4,
            "id": "managed-day-confirm",
            "broker_epoch": self.broker.broker_epoch,
            "action": action["action"],
            "expected_world_epoch": self.state["world_epoch"],
            "expected_revision": self.state["revision"],
            "expected_version": self.state["version"],
            "arguments": copy.deepcopy(action["arguments"]),
        }
        self.broker.enqueue(request, dispatch_token=self._grant())
        budget = self.store.snapshot()["budget"]
        self.assertEqual(budget["writes"], 1)
        self.assertEqual(budget["day_fees"], 40)
        self.assertEqual(budget["days"], 1)

    def test_pause_before_mod_delivery_rejects_pending_command(self):
        self._start_managed_broker()
        request = self._request()
        queued = self.broker.enqueue(request, dispatch_token=self._grant())
        self.assertEqual(queued["command_status"], "pending")

        context = self.store.snapshot()
        self.store.control("pause", context["revision"])
        context = self.store.snapshot()
        self.store.control("resume", context["revision"])
        delivery = self._exchange(self.state)
        self.assertFalse(delivery["has_command"])
        self.assertEqual(self.broker.records[request["id"]]["command_status"], "rejected")
        self.assertEqual(self.broker.records[request["id"]]["result"]["reason"], "run_delivery_blocked")

    def test_pause_waits_for_already_claimed_delivery_to_commit(self):
        self._start_managed_broker()
        request = self._request("pause-delivery-race")
        self.broker.enqueue(request, dispatch_token=self._grant())
        entered_commit = threading.Event()
        allow_commit = threading.Event()
        pause_started = threading.Event()
        pause_done = threading.Event()
        errors = []
        broker_store = self.broker.run_store
        original_deliver = broker_store.deliver

        def block_inside_claim(command_id, commit_callback):
            def wrapped_commit():
                entered_commit.set()
                if not allow_commit.wait(timeout=3):
                    raise AssertionError("test did not release the delivery commit")
                return commit_callback()
            return original_deliver(command_id, wrapped_commit)

        broker_store.deliver = block_inside_claim
        delivery_result = []

        def exchange():
            try:
                delivery_result.append(self._exchange(self.state))
            except Exception as exc:  # surfaced in the test thread below
                errors.append(exc)

        exchange_thread = threading.Thread(target=exchange, daemon=True)
        exchange_thread.start()
        self.assertTrue(entered_commit.wait(timeout=3), "Broker never claimed the pending delivery")

        def pause():
            pause_started.set()
            try:
                revision = self.store.snapshot()["revision"]
                self.store.control("pause", revision)
            except Exception as exc:
                errors.append(exc)
            finally:
                pause_done.set()

        pause_thread = threading.Thread(target=pause, daemon=True)
        pause_thread.start()
        self.assertTrue(pause_started.wait(timeout=3))
        self.assertFalse(pause_done.wait(timeout=.05), "pause should wait behind the claimed delivery lock")
        allow_commit.set()
        exchange_thread.join(timeout=3)
        pause_thread.join(timeout=3)
        self.assertFalse(exchange_thread.is_alive())
        self.assertFalse(pause_thread.is_alive())
        self.assertEqual(errors, [])
        self.assertTrue(delivery_result[0]["has_command"])
        self.assertEqual(self.broker.records[request["id"]]["command_status"], "running")
        self.assertTrue(self.store.snapshot()["controls"]["paused"])

    def test_pause_then_resume_does_not_revive_an_unconsumed_grant(self):
        self._start_managed_broker()
        token = self._grant()
        context = self.store.snapshot()
        self.store.control("pause", context["revision"])
        context = self.store.snapshot()
        self.store.control("resume", context["revision"])

        with self.assertRaises(ProtocolError):
            self._enqueue("pre-pause-grant-command", token=token)
        self.assertNotIn("pre-pause-grant-command", self.broker.records)

    def test_world_change_blocks_resume_and_new_dispatch_until_explicit_rebind(self):
        self._start_managed_broker()
        changed = copy.deepcopy(self.state)
        changed["world_epoch"] = "different-world"
        changed["version"] = f'{changed["world_epoch"]}:{changed["revision"]}'
        self.assertTrue(__import__("bridge.protocol", fromlist=["valid_snapshot_v4"]).valid_snapshot_v4(changed))
        self._exchange(changed)
        broker_state = self.broker.snapshot()
        context = self.store.snapshot()
        self.assertFalse(broker_state["can_act"])
        self.assertEqual(context["run"]["status"], "world_mismatch")
        with self.assertRaises(RunStoreError) as blocked:
            self.store.grant(self.owner, ttl_seconds=300)
        self.assertEqual(blocked.exception.reason, "run_store_dispatch_blocked")

    def test_orphan_reservation_after_crash_fails_closed_without_new_command(self):
        self._start_managed_broker()
        token = self._grant()
        request = self._request("orphaned-command")
        # This is the exact crash cut between durable budget reservation and
        # Journal enqueue. No game command exists in the Journal.
        self.store.reserve(request, self.state, self.broker.records, self.session, token)
        self.assertNotIn(request["id"], self.broker.records)
        self.broker.close()
        self.broker = Broker("run-test-token", clock=self.clock, monotonic=self.clock.monotonic,
                             heartbeat_timeout=60, journal_path=self.journal_path, run_path=self.run_path)
        self.broker.snapshot()
        context = self.store.snapshot()
        self.assertEqual(context["run"]["status"], "blocked_unknown")
        entry = next(command for command in context["commands"] if command["command_id"] == request["id"])
        self.assertEqual(entry["reason"], "reservation_missing_from_journal")
        with self.assertRaises(RunStoreError):
            self.store.grant(self.owner, ttl_seconds=300)
        self.assertNotIn("different-command-id", self.broker.records)

    def test_delivered_unknown_stays_blocked_after_restart_and_rejects_new_id(self):
        self._start_managed_broker()
        token = self._grant()
        request = self._request("unknown-managed-command")
        self.broker.enqueue(request, dispatch_token=token)
        delivered = self._exchange(self.state)
        self.assertTrue(delivered["has_command"])
        self.broker.close()  # Shutdown after delivery durably leaves outcome_unknown.

        self.broker = Broker("run-test-token", clock=self.clock, monotonic=self.clock.monotonic,
                             heartbeat_timeout=60, journal_path=self.journal_path, run_path=self.run_path)
        self._exchange(self.state)
        current = self.broker.snapshot()
        context = self.store.snapshot()
        self.assertFalse(current["can_act"])
        self.assertEqual(self.broker.records[request["id"]]["command_status"], "outcome_unknown")
        self.assertEqual(context["run"]["status"], "blocked_unknown")
        # A fresh game snapshot lets the test exercise the managed blocker rather
        # than merely failing because a restarted Broker has no state yet.
        self._exchange(self.state)
        self.broker.snapshot()
        with self.assertRaises(RunStoreError):
            self.store.grant(self.owner, ttl_seconds=300)
        with self.assertRaises(ProtocolError):
            self._enqueue("replacement-command-id", token=token, broker=self.broker)
        self.assertNotIn("replacement-command-id", self.broker.records)

    def test_unknown_inventory_delta_fixture_survives_managed_recovery(self):
        # This checked-in v4 fixture preserves the important observed split:
        # native input returned, inventory changed unexpectedly, verification
        # stayed unknown. It makes the regression portable without reading or
        # writing the ignored machine-only M5-B journal evidence.
        case = V4_INTERACTION_FIXTURE["cases"]["cash_and_inventory_delta_unknown"]
        source_command, source_result = case["command"], case["result"]
        self.assertEqual(source_result["status"], "outcome_unknown")
        self.assertEqual(source_result["interaction_result"]["input_receipt"], "returned")
        self.assertEqual(source_result["interaction_result"]["verification"], "unknown")
        self.assertEqual(len(source_result["interaction_result"]["removed_item_refs"]), 1)
        self.assertEqual(len(source_result["interaction_result"]["added_item_refs"]), 1)

        command_id = source_command["id"]
        request = {key: source_command[key] for key in (
            "schema_version", "id", "broker_epoch", "action", "expected_world_epoch",
            "expected_revision", "expected_version", "arguments")}
        source_record = {
            "command_id": command_id,
            "request_fingerprint": request_fingerprint(request),
            "request": request,
            "command": copy.deepcopy(source_command),
            "command_status": "outcome_unknown",
            "result": copy.deepcopy(source_result),
            "delivery_session_id": "fixture-mod-session",
            "created_at_unix_ms": 100_000,
            "updated_at_unix_ms": 100_000,
            "execution": copy.deepcopy(source_result["execution"]),
        }
        source_journal = {"journal_format_version": 2, "broker_epoch": source_command["broker_epoch"],
                          "records": [copy.deepcopy(source_record)]}
        self.assertTrue(Journal.valid_v2(source_journal))

        copy_path = self.temp / "unknown-fixture-journal.json"
        copied_run_path = self.temp / "unknown-fixture-run.json"
        copy_path.write_text(json.dumps(source_journal, separators=(",", ":")), encoding="utf-8")
        state = copy.deepcopy(source_result["before"])
        if "day" not in state["business"]["details"]:
            state["business"]["details"]["day"] = copy.deepcopy(self.state["business"]["details"]["day"])
        session = source_record["delivery_session_id"]
        day = state["business"]["details"]["day"]["day_number"]
        copied_store = RunStore(copied_run_path)
        copied_store.create("unknown-copy-run", "isolated-fixture", str(copy_path), state["world_epoch"], session,
                            day, day + 1, {key: 20 for key in LIMIT_KEYS}, [source_command["action"]],
                            "Do not replay the old unknown command.")
        copied_broker = Broker("unknown-copy-token", clock=self.clock, monotonic=self.clock.monotonic,
                               journal_path=copy_path, run_path=copied_run_path)
        try:
            copied_broker.snapshot()
            self.assertFalse(copied_broker.snapshot()["can_act"])
            retained = copied_broker.records[command_id]
            self.assertEqual(retained, source_record)
            self.assertEqual(copied_store.snapshot()["run"]["status"], "blocked_unknown")
        finally:
            copied_broker.close()
        self.assertTrue(Journal.valid_v2(json.loads(copy_path.read_text(encoding="utf-8"))))


if __name__ == "__main__":
    unittest.main()
