"""Behavioral checks for the M5-C persistent Run Context boundary."""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import unittest
import uuid

from bridge.journal import Journal
from bridge.protocol import request_fingerprint, valid_snapshot_v4, valid_success_result_v4
from bridge.run_store import RunStore, RunStoreError
from bridge.server import Broker


ROOT = Path(__file__).resolve().parents[1]
V3_DIALOGUE = json.loads((ROOT / "protocol/fixtures/v3-dialogue-snapshot.json").read_text(encoding="utf-8"))
V3_SALE = json.loads((ROOT / "protocol/fixtures/m3a-prepare_sale.json").read_text(encoding="utf-8"))
LIMITS = {"customers": 12, "writes": 32, "trades": 8, "buy_spend": 10000,
          "bargains": 8, "day_fees": 5000, "days": 1}


class FakeClock:
    def __init__(self):
        self.now = 1000.0

    def __call__(self):
        return self.now

    def monotonic(self):
        return self.now


def snapshot_v4():
    state = copy.deepcopy(V3_DIALOGUE)
    state["schema_version"] = 4
    details = state["business"]["details"]
    details["containers"] = [
        {"container": name, "completeness": "complete", "reason": None, "item_refs": []}
        for name in ("front", "back", "player")
    ]
    details["day"] = {
        "day_number": 3, "remaining_customers": 12, "stage": "open", "reason": None,
        "can_close": False, "can_sleep": False, "can_confirm": False,
        "report_open": False, "transition_pending": False, "settlement": None,
    }
    state["surface"] = {
        "schema_version": 1, "surface_ref": "surface-1", "kind": "dialogue",
        "phase": "awaiting_choice", "status": "awaiting_agent", "completeness": "complete",
        "reason": "surface_observed", "text": "Would you like to sell anything today?",
        "customer_ref": "customer-1", "dialogue_ref": "dialogue-1",
        "controls": [{
            "control_ref": "control-1", "action": "choose_dialogue_option", "mode": None,
            "option_ref": "option-1", "text": "Yes, please", "enabled": True,
            "effect_contract": "presentation", "effects": ["dialogue_transition", "presentation"],
            "reason": "native_control_enabled",
        }],
    }
    state["legal_actions"] = [{"action": "choose_dialogue_option", "arguments": {
        "customer_ref": "customer-1", "dialogue_ref": "dialogue-1", "option_ref": "option-1",
        "surface_ref": "surface-1", "control_ref": "control-1",
    }}]
    if not valid_snapshot_v4(state):
        raise AssertionError("test v4 snapshot must satisfy production protocol validator")
    return state


def waiting_handoff_v4(state=None, *, handoff_ref="handoff-1", item_refs=("item-24",)):
    result = copy.deepcopy(state if state is not None else snapshot_v4())
    details = result["business"]["details"]
    details["inspection_handoff"] = {
        "requires_manual_inspection": True, "status": "waiting_for_user",
        "handoff_ref": handoff_ref, "customer_ref": result["business"]["customer_ref"],
        "items": [{"item_ref": item_ref, "name": "Hemingway cigarette pack"} for item_ref in item_refs],
        "reason": "manual_item_inspection", "last_resumed_handoff_ref": None,
    }
    result["can_close_menu"] = False
    result["interaction"].update(consistency="blocked", operable=False, reason="inspection_handoff")
    details["ui"].update(status="blocked", blockers=["inspection_handoff"])
    result["legal_actions"] = [{"action": "resume_inspection_handoff",
                                "arguments": {"handoff_ref": handoff_ref}}]
    result["surface"]["status"] = "awaiting_user"
    if not valid_snapshot_v4(result):
        raise AssertionError("test pending handoff must satisfy production v4 validator")
    return result


def not_ready_handoff_v4(state=None):
    result = waiting_handoff_v4(state)
    result.update(ready=False, not_ready_reason="game_not_ready", legal_actions=[])
    result["interaction"]["operable"] = False
    if not valid_snapshot_v4(result):
        raise AssertionError("test not-ready handoff must satisfy production v4 validator")
    return result


def revised(state, *, surface_suffix, option_ref=None, option_text=None, day_number=None):
    result = copy.deepcopy(state)
    result["revision"] += 1
    result["version"] = f'{result["world_epoch"]}:{result["revision"]}'
    result["surface"]["surface_ref"] = f"surface-{surface_suffix}"
    result["surface"]["controls"][0]["control_ref"] = f"control-{surface_suffix}"
    legal = result["legal_actions"][0]["arguments"]
    legal["surface_ref"] = result["surface"]["surface_ref"]
    legal["control_ref"] = result["surface"]["controls"][0]["control_ref"]
    if option_ref is not None:
        result["surface"]["controls"][0]["option_ref"] = option_ref
        result["surface"]["controls"][0]["text"] = option_text
        legal["option_ref"] = option_ref
        customer = result["business"]["details"]["customer"]
        customer["option_refs"] = [option_ref]
        customer["options"] = [{"option_ref": option_ref, "text": option_text}]
    if day_number is not None:
        result["business"]["details"]["day"]["day_number"] = day_number
    if not valid_snapshot_v4(result):
        raise AssertionError("revised test snapshot must satisfy production protocol validator")
    return result


def request_for(state, broker_epoch, command_id):
    args = copy.deepcopy(state["legal_actions"][0]["arguments"])
    return {"schema_version": 4, "id": command_id, "broker_epoch": broker_epoch,
            "action": state["legal_actions"][0]["action"], "expected_world_epoch": state["world_epoch"],
            "expected_revision": state["revision"], "expected_version": state["version"],
            "arguments": args}


def day_snapshot_v4(action, day_number, revision, cash):
    state = copy.deepcopy(V3_SALE)
    state["schema_version"] = 4
    state["revision"] = revision
    state["version"] = f'{state["world_epoch"]}:{revision}'
    state["cash"] = cash
    state["business"].update(customer_ref=None, item_refs=[], offer_ref=None, collection_fingerprint=None)
    details = state["business"]["details"]
    customer = details["customer"]
    customer.update(presence="absent", arrived=None, trade_type="unknown", phase="absent",
                    dialogue_ref=None, dialogue_stage="absent", option_refs=[], dialogue_text=None,
                    is_typing=None, waiting_for_advance=None, options=[], request_text=None,
                    trade_panel=None)
    details["day"] = {"day_number": day_number, "remaining_customers": 0,
                       "stage": "morning", "reason": None, "can_close": False,
                       "can_sleep": False, "can_confirm": False, "report_open": False,
                       "transition_pending": False, "settlement": None}
    details["candidates"] = [{"action": candidate, "status": "candidate", "reason": "day chain test"}
                             for candidate in ("start_business", "close_business", "open_end_of_day",
                                               "confirm_end_of_day", "close_day_report")]
    args = {}
    day = details["day"]
    if action == "close_business":
        state["phase"] = "OPEN"
        day.update(stage="open", can_close=True)
    elif action == "open_end_of_day":
        state["phase"] = "CLOSED"
        day.update(stage="closed", can_sleep=True)
    elif action == "confirm_end_of_day":
        state["phase"] = "CLOSED"
        day.update(stage="settlement", can_confirm=True, settlement={
            "settlement_ref": f"settlement-{day_number}", "total": 40,
            "services": [{"label": "Power", "cost_text": "$40", "cost": 40, "selected": True}],
        })
        args = {"settlement_ref": f"settlement-{day_number}", "amount": 40, "max_spend": 40}
    elif action == "close_day_report":
        state["phase"] = "MORNING"
        day.update(stage="next_morning", report_open=True)
    elif action == "start_business":
        state["phase"] = "MORNING"
        day.update(stage="next_morning", report_open=False)
    else:
        raise AssertionError(action)
    state["legal_actions"] = [{"action": action, "arguments": args}]
    state["surface"] = {"schema_version": 1, "surface_ref": None, "kind": "none", "phase": "absent",
                         "status": "awaiting_agent", "completeness": "complete", "reason": "day_action",
                         "text": None, "customer_ref": None, "dialogue_ref": None, "controls": []}
    if not valid_snapshot_v4(state):
        raise AssertionError(f"invalid day snapshot for {action}")
    return state


def success_record(request, before, after, command_id, broker_epoch, session_id, timestamp):
    command = {**copy.deepcopy(request), "start_deadline_unix_ms": timestamp + 5000,
               "execution_timeout_ms": 5000}
    progress = {"id": command_id, "broker_epoch": broker_epoch, "stage": "completed", "sequence": 3,
                "may_have_side_effects": True, "started_at_unix_ms": timestamp,
                "execution_deadline_unix_ms": timestamp + 5000}
    result = {"schema_version": 4, "id": command_id, "broker_epoch": broker_epoch,
              "status": "succeeded", "reason": "verified", "before": copy.deepcopy(before),
              "after": copy.deepcopy(after), "execution": progress}
    if not valid_success_result_v4(command, result):
        raise AssertionError(f"invalid v4 success result for {command['action']}")
    request_copy = copy.deepcopy(request)
    record = {"command_id": command_id, "request_fingerprint": request_fingerprint(request_copy),
              "request": request_copy, "command": command, "command_status": "succeeded",
              "result": result, "delivery_session_id": session_id, "created_at_unix_ms": timestamp,
              "updated_at_unix_ms": timestamp + 1, "execution": progress}
    if not Journal._valid_v4_record(record):
        raise AssertionError(f"invalid v4 journal record for {command['action']}")
    return record


class RunStoreTests(unittest.TestCase):
    def setUp(self):
        self.tag = ".run-store-test-" + uuid.uuid4().hex
        self.run_path = ROOT / "tests" / (self.tag + ".json")
        self.journal_path = ROOT / "tests" / (self.tag + "-journal.json")
        self.extra_paths = []
        self.clock = FakeClock()
        self.state = snapshot_v4()
        self.store = RunStore(self.run_path, clock=self.clock)
        self.store.create("run-test", "save-test", self.journal_path, self.state["world_epoch"],
                          "session-1", 3, 4, dict(LIMITS), ["choose_dialogue_option"], "finish the day")
        self.broker = Broker("run-store-test", clock=self.clock, monotonic=self.clock.monotonic,
                             journal_path=self.journal_path, run_path=self.run_path)
        self.broker.run_store._clock = self.clock
        self.broker.exchange({"schema_version": 4, "session_id": "session-1",
                              "broker_epoch": self.broker.broker_epoch, "state": self.state,
                              "has_result": False, "result": None, "progress": None})
        # Broker sync binds its epoch and seeds the durable Inbox from the actual v4 state.
        self.broker.snapshot()

    def tearDown(self):
        self.broker.close()
        for path in (self.run_path, self.run_path.with_suffix(self.run_path.suffix + ".lock"),
                     self.journal_path, self.journal_path.with_suffix(self.journal_path.suffix + ".lock")):
            try:
                path.unlink()
            except FileNotFoundError:
                pass
        for path in self.extra_paths:
            try:
                path.unlink()
            except FileNotFoundError:
                pass

    def resume(self):
        current = self.store.snapshot()
        return self.store.control("resume", current["revision"])

    def test_create_pause_owner_token_budget_reservation_and_delivery_gate(self):
        self.assertEqual(self.store.snapshot()["run"]["status"], "paused")
        self.resume()
        token = self.store.grant("supervisor-abc", ttl_seconds=120)
        request = request_for(self.state, self.broker.broker_epoch, "cmd-one")
        self.broker.enqueue(request, dispatch_token=token)

        current = self.store.snapshot()
        self.assertEqual(current["budget"]["writes"], 1)
        self.assertEqual(current["commands"][0]["budget_cost"]["writes"], 1)
        self.assertEqual(current["commands"][0]["reservation_status"], "reserved")
        self.assertEqual(current["commands"][0]["journal_status"], "pending")
        self.assertEqual(current["inbox"][0]["status"], "submitted")
        self.assertTrue(self.store.delivery_allowed("cmd-one"))

        self.store.control("pause", current["revision"])
        self.assertFalse(self.store.delivery_allowed("cmd-one"))
        paused = self.store.snapshot()
        self.store.control("resume", paused["revision"])
        self.assertFalse(self.store.delivery_allowed("cmd-one"), "resume cannot restore a consumed dispatch")

    def test_pause_serializes_against_durable_delivery_commit(self):
        self.resume()
        token = self.store.grant("supervisor-race")
        request = request_for(self.state, self.broker.broker_epoch, "cmd-delivery-race")
        self.store.reserve(request, self.state, self.broker.records, "session-1", token)
        self.broker._enqueue_command(request)

        callback_entered = threading.Event()
        release_callback = threading.Event()
        pause_started = threading.Event()
        pause_finished = threading.Event()
        outcomes = {}

        def commit_running_record():
            record = self.broker.records[request["id"]]
            record["command_status"] = "running"
            record["delivery_session_id"] = "session-1"
            record["updated_at_unix_ms"] = int(self.clock() * 1000)
            callback_entered.set()
            if not release_callback.wait(3):
                raise TimeoutError("test did not release durable delivery callback")
            self.broker._persist()

        def deliver():
            try:
                outcomes["delivered"] = self.store.deliver(request["id"], commit_running_record)
            except Exception as exc:  # Surface worker errors in the assertion thread.
                outcomes["delivery_error"] = exc

        def pause_after_delivery_lock():
            pause_started.set()
            try:
                current = self.store.snapshot()
                self.store.control("pause", current["revision"])
            except Exception as exc:
                outcomes["pause_error"] = exc
            finally:
                pause_finished.set()

        delivery_thread = threading.Thread(target=deliver)
        delivery_thread.start()
        self.assertTrue(callback_entered.wait(2), "delivery callback never acquired the RunStore lock")
        pause_thread = threading.Thread(target=pause_after_delivery_lock)
        pause_thread.start()
        self.assertTrue(pause_started.wait(2))
        pause_waited_for_commit = not pause_finished.wait(0.05)
        release_callback.set()
        delivery_thread.join(3)
        pause_thread.join(3)

        self.assertTrue(pause_waited_for_commit, "pause must wait for the durable delivery claim")
        self.assertFalse(delivery_thread.is_alive())
        self.assertFalse(pause_thread.is_alive())
        self.assertNotIn("delivery_error", outcomes)
        self.assertNotIn("pause_error", outcomes)
        self.assertTrue(outcomes["delivered"])
        current = self.store.snapshot()
        command = current["commands"][0]
        self.assertTrue(command["delivered"])
        self.assertEqual(command["journal_status"], "running")
        self.assertEqual(self.broker.records[request["id"]]["command_status"], "running")
        self.assertEqual(current["run"]["status"], "paused")
        self.assertFalse(self.store.delivery_allowed(request["id"]))

    def test_windows_empty_lock_file_is_safe_under_process_contention(self):
        if os.name != "nt":
            self.skipTest("this regression covers Windows byte-range lock initialization")
        lock_path = self.run_path.with_suffix(".process-lock")
        ready_path = self.run_path.with_suffix(".holder-ready")
        release_path = self.run_path.with_suffix(".holder-release")
        attempting_path = self.run_path.with_suffix(".competitor-attempting")
        outcome_path = self.run_path.with_suffix(".competitor-outcome")
        log_paths = (self.run_path.with_suffix(".holder.log"), self.run_path.with_suffix(".competitor.log"))
        self.extra_paths.extend((lock_path, ready_path, release_path, attempting_path, outcome_path, *log_paths))
        for path in (ready_path, release_path, attempting_path, outcome_path, *log_paths):
            try:
                path.unlink()
            except FileNotFoundError:
                pass
        holder_code = (
            "import sys,time\n"
            "from pathlib import Path\n"
            "from bridge.run_store import _FileLock\n"
            "lock,ready,release=map(Path,sys.argv[1:])\n"
            "with _FileLock(lock):\n"
            " ready.write_text('ready',encoding='utf-8')\n"
            " deadline=time.monotonic()+10\n"
            " while not release.exists() and time.monotonic()<deadline: time.sleep(.01)\n"
            " if not release.exists(): raise SystemExit('holder timeout')\n"
        )
        competitor_code = (
            "import sys\n"
            "from pathlib import Path\n"
            "from bridge.run_store import _FileLock\n"
            "lock,attempting,outcome=map(Path,sys.argv[1:])\n"
            "attempting.write_text('attempting',encoding='utf-8')\n"
            "try:\n"
            " with _FileLock(lock): outcome.write_text('acquired',encoding='utf-8')\n"
            "except BaseException as exc:\n"
            " outcome.write_text('error:'+type(exc).__name__+':'+str(exc),encoding='utf-8')\n"
        )
        log_files = [path.open("wb") for path in log_paths]
        holder = subprocess.Popen([sys.executable, "-c", holder_code, str(lock_path),
                                   str(ready_path), str(release_path)], cwd=ROOT,
                                  stdout=log_files[0], stderr=subprocess.STDOUT)
        competitor = None
        try:
            self.assertTrue(self.wait_for_path(ready_path, holder, 10),
                            "first process did not acquire an empty lock file")
            competitor = subprocess.Popen([sys.executable, "-c", competitor_code, str(lock_path),
                                           str(attempting_path), str(outcome_path)], cwd=ROOT,
                                          stdout=log_files[1], stderr=subprocess.STDOUT)
            self.assertTrue(self.wait_for_path(attempting_path, competitor, 10),
                            "second process did not begin lock acquisition")
            try:
                competitor.wait(timeout=0.5)
                completed_while_locked = True
            except subprocess.TimeoutExpired:
                completed_while_locked = False
            self.assertFalse(completed_while_locked,
                             "second process must wait for the byte lock instead of failing during a read")
        finally:
            release_path.write_text("release", encoding="utf-8")
            try:
                holder.wait(timeout=10)
            except subprocess.TimeoutExpired:
                holder.kill()
                holder.wait(timeout=2)
            if competitor is not None:
                try:
                    competitor.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    competitor.kill()
                    competitor.wait(timeout=2)
            for log_file in log_files:
                log_file.close()
        self.assertEqual(holder.returncode, 0)
        self.assertIsNotNone(competitor)
        self.assertEqual(competitor.returncode, 0)
        self.assertEqual(outcome_path.read_text(encoding="utf-8"), "acquired")

    @staticmethod
    def wait_for_path(path, process, timeout):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if path.exists():
                return True
            if process.poll() is not None:
                return False
            time.sleep(0.01)
        return path.exists()

    def test_inbox_deduplicates_surface_resigning_but_supersedes_new_semantics(self):
        self.assertEqual(len(self.store.snapshot()["inbox"]), 1)
        same = revised(self.state, surface_suffix=2)
        self.store.sync(same, self.broker.records, "session-1", True, self.broker.broker_epoch)
        self.assertEqual(len(self.store.snapshot()["inbox"]), 1)

        different = revised(same, surface_suffix=3, option_ref="option-2", option_text="No, thank you")
        self.store.sync(different, self.broker.records, "session-1", True, self.broker.broker_epoch)
        events = self.store.snapshot()["inbox"]
        self.assertEqual(len(events), 2)
        self.assertEqual(events[0]["status"], "superseded")
        self.assertEqual(events[0]["superseded_by"], events[1]["event_id"])
        self.assertEqual(events[1]["status"], "pending")

        returned = revised(self.state, surface_suffix=4)
        self.store.sync(returned, self.broker.records, "session-1", True, self.broker.broker_epoch)
        events = self.store.snapshot()["inbox"]
        occurrences = [event["occurrence"] for event in events
                       if event["semantic_fingerprint"] == events[0]["semantic_fingerprint"]]
        self.assertEqual(occurrences, [1, 2])
        self.assertEqual(events[1]["status"], "superseded")
        self.assertEqual(events[2]["status"], "pending")

        next_day = revised(returned, surface_suffix=5, day_number=4)
        self.store.sync(next_day, self.broker.records, "session-1", True, self.broker.broker_epoch)
        events = self.store.snapshot()["inbox"]
        self.assertNotEqual(events[2]["dedupe_key"], events[3]["dedupe_key"])
        self.assertEqual(events[2]["status"], "superseded")

    def test_missing_journal_link_is_permanent_unknown_and_budget_is_not_released(self):
        self.resume()
        token = self.store.grant("supervisor-owner")
        request = request_for(self.state, self.broker.broker_epoch, "cmd-orphan")
        reservation = self.store.reserve(request, self.state, self.broker.records, "session-1", token)
        self.assertEqual(reservation["reservation_status"], "reserved")

        context = self.store.sync(self.state, self.broker.records, "session-1", True,
                                  self.broker.broker_epoch)
        self.assertEqual(context["run"]["status"], "blocked_unknown")
        self.assertEqual(context["commands"][0]["reservation_status"], "unknown")
        self.assertEqual(context["budget"]["writes"], 1)
        self.assertFalse(self.store.delivery_allowed("cmd-orphan"))

        repeated = self.store.sync(self.state, self.broker.records, "session-1", True,
                                   self.broker.broker_epoch)
        self.assertEqual(repeated["run"]["status"], "blocked_unknown")
        changed = revised(self.state, surface_suffix=2, option_ref="option-2", option_text="No, thank you")
        after_semantic_change = self.store.sync(changed, self.broker.records, "session-1", True,
                                                self.broker.broker_epoch)
        self.assertEqual(after_semantic_change["run"]["status"], "blocked_unknown")
        self.assertEqual(after_semantic_change["inbox"][0]["status"], "blocked_unknown")
        with self.assertRaises(RunStoreError):
            self.store.grant("supervisor-new")

    def test_semantic_change_does_not_supersede_a_submitted_event(self):
        self.resume()
        token = self.store.grant("supervisor-submitted")
        request = request_for(self.state, self.broker.broker_epoch, "cmd-submitted-inbox")
        self.broker.enqueue(request, dispatch_token=token)
        before = self.store.snapshot()
        submitted = next(event for event in before["inbox"] if event["status"] == "submitted")

        changed = revised(self.state, surface_suffix=2, option_ref="option-2", option_text="No, thank you")
        after = self.store.sync(changed, self.broker.records, "session-1", True,
                                self.broker.broker_epoch)
        retained = next(event for event in after["inbox"] if event["event_id"] == submitted["event_id"])
        self.assertEqual(retained["status"], "submitted")
        self.assertNotEqual(after["inbox"][-1]["event_id"], submitted["event_id"])

    def test_journal_delivery_from_a_different_session_is_permanent_unknown(self):
        self.resume()
        token = self.store.grant("supervisor-session-mismatch")
        request = request_for(self.state, self.broker.broker_epoch, "cmd-wrong-session")
        self.store.reserve(request, self.state, self.broker.records, "session-1", token)
        self.broker._enqueue_command(request)
        record = self.broker.records[request["id"]]
        record["command_status"] = "running"
        record["delivery_session_id"] = "session-2"
        record["updated_at_unix_ms"] += 1
        self.broker._persist()

        context = self.store.sync(self.state, self.broker.records, "session-1", True,
                                  self.broker.broker_epoch)
        self.assertEqual(context["run"]["status"], "blocked_unknown")
        self.assertEqual(context["commands"][0]["journal_status"], "outcome_unknown")
        self.assertEqual(context["budget"]["writes"], 1)

    def test_stale_prequeue_attempt_counts_one_write_and_explicit_reject_does_not_refund(self):
        self.resume()
        token = self.store.grant("supervisor-owner")
        request = request_for(self.state, self.broker.broker_epoch, "cmd-stale")
        request["expected_revision"] += 1
        request["expected_version"] = f'{request["expected_world_epoch"]}:{request["expected_revision"]}'
        with self.assertRaisesRegex(RunStoreError, "run_store_stale_snapshot"):
            self.store.reserve(request, self.state, self.broker.records, "session-1", token)

        current = self.store.snapshot()
        self.assertEqual(current["budget"]["writes"], 1)
        self.assertEqual(current["commands"][0]["reservation_status"], "rejected_prequeue")
        self.store.reject("cmd-stale", "stale_snapshot")
        self.assertEqual(self.store.snapshot()["budget"]["writes"], 1)

    def test_mod_session_change_requires_explicit_rebind_without_resetting_budget(self):
        self.resume()
        token = self.store.grant("supervisor-owner")
        request = request_for(self.state, self.broker.broker_epoch, "cmd-orphan")
        request["expected_revision"] += 1
        request["expected_version"] = f'{request["expected_world_epoch"]}:{request["expected_revision"]}'
        with self.assertRaisesRegex(RunStoreError, "run_store_stale_snapshot"):
            self.store.reserve(request, self.state, self.broker.records, "session-1", token)
        self.store.reject("cmd-orphan", "stale_snapshot")
        self.store.sync(self.state, self.broker.records, "session-2", True, self.broker.broker_epoch)
        current = self.store.snapshot()
        self.assertEqual(current["run"]["status"], "awaiting_user")
        self.assertEqual(current["budget"]["writes"], 1)
        with self.assertRaises(RunStoreError):
            self.store.control("resume", current["revision"])
        rebound = self.store.control("rebind", current["revision"], world_epoch="dialogue-world",
                                     session_id="session-2", day_number=3)
        self.assertEqual(rebound["budget"]["writes"], 1)
        self.assertEqual(rebound["run"]["status"], "running")
        self.assertEqual(rebound["authorization"]["version"], 1)

    def test_operator_inspection_confirmation_binds_once_to_current_handoff(self):
        handoff = waiting_handoff_v4(self.state)
        self.store.sync(handoff, self.broker.records, "session-1", True, self.broker.broker_epoch)
        before = self.store.snapshot()
        self.assertEqual(before["run"]["status"], "awaiting_user")
        manual_event = next(event for event in before["inbox"] if event["kind"] == "manual")
        self.assertEqual(manual_event["status"], "awaiting_user")
        with self.assertRaisesRegex(RunStoreError, "run_store_blocked"):
            self.store.control("resume", before["revision"])
        with self.assertRaisesRegex(RunStoreError, "run_store_dispatch_blocked"):
            self.store.grant("supervisor-without-confirmation")

        instruction = "直接当正品一样买入这款香烟"
        confirmed = self.store.control("confirm_inspection", before["revision"], state=handoff,
                                       session_id="session-1", instruction=instruction)
        confirmation = confirmed["operator_confirmation"]
        self.assertEqual(confirmation, {
            "world_epoch": handoff["world_epoch"], "session_id": "session-1",
            "customer_ref": handoff["business"]["details"]["inspection_handoff"]["customer_ref"],
            "handoff_ref": "handoff-1", "item_refs": ["item-24"],
            "instruction": instruction, "command_id": None,
        })
        self.assertEqual(confirmed["budget"], before["budget"])
        self.assertEqual(confirmed["run"]["target_day"], before["run"]["target_day"])
        self.assertEqual(confirmed["run"]["goal"], before["run"]["goal"])
        self.assertNotIn("resume_inspection_handoff", confirmed["authorization"]["allowed_actions"])
        self.assertEqual(next(event for event in confirmed["inbox"] if event["kind"] == "manual")["status"],
                         "pending")

        still_waiting = self.store.sync(handoff, self.broker.records, "session-1", True,
                                        self.broker.broker_epoch)
        self.assertFalse(still_waiting["controls"]["awaiting_user"])
        self.assertTrue(any(event["kind"] == "manual" for event in still_waiting["inbox"]))
        running = self.store.control("resume", still_waiting["revision"])
        token = self.store.grant("supervisor-inspection")
        request = request_for(handoff, self.broker.broker_epoch, "resume-approved-once")
        reserved = self.store.reserve(request, handoff, self.broker.records, "session-1", token)
        self.assertEqual(reserved["action"], "resume_inspection_handoff")
        after_reserve = self.store.snapshot()
        self.assertEqual(after_reserve["operator_confirmation"]["command_id"], request["id"])
        self.assertEqual(after_reserve["budget"]["writes"], running["budget"]["writes"] + 1)
        self.assertEqual(next(event for event in after_reserve["inbox"] if event["kind"] == "manual")["status"],
                         "submitted")

        # 已预留但尚未入Journal的拒绝仍消耗这次人工授权，不能换新ID再试。
        self.store.reject(request["id"], "simulated_prequeue_reject")
        second_token = self.store.grant("supervisor-inspection-retry")
        second_request = request_for(handoff, self.broker.broker_epoch, "resume-must-not-retry")
        with self.assertRaisesRegex(RunStoreError, "run_store_inspection_confirmation_required"):
            self.store.reserve(second_request, handoff, self.broker.records, "session-1", second_token)
        final = self.store.snapshot()
        self.assertEqual(final["operator_confirmation"]["command_id"], request["id"])
        self.assertEqual(final["budget"]["writes"], running["budget"]["writes"] + 2)

    def test_unconsumed_inspection_confirmation_expires_when_handoff_changes_and_rebind_clears(self):
        handoff = waiting_handoff_v4(self.state)
        self.store.sync(handoff, self.broker.records, "session-1", True, self.broker.broker_epoch)
        current = self.store.snapshot()
        confirmed = self.store.control("confirm_inspection", current["revision"], state=handoff,
                                       session_id="session-1", instruction="continue by user request")

        changed_handoff = waiting_handoff_v4(self.state, item_refs=("item-25",))
        changed = self.store.sync(changed_handoff, self.broker.records, "session-1", True,
                                  self.broker.broker_epoch)
        self.assertIsNone(changed["operator_confirmation"])
        self.assertEqual(changed["run"]["status"], "awaiting_user")
        self.assertTrue(any(event["kind"] == "manual" and event["status"] == "awaiting_user"
                            for event in changed["inbox"]))

        # A fresh operator confirmation is also invalidated by explicit world/session rebind.
        current = self.store.snapshot()
        confirmed = self.store.control("confirm_inspection", current["revision"], state=changed_handoff,
                                       session_id="session-1", instruction="confirm current handoff")
        rebound = self.store.control("rebind", confirmed["revision"], world_epoch="new-world",
                                     session_id="session-2", day_number=3)
        self.assertIsNone(rebound["operator_confirmation"])

    def test_manual_gate_clears_when_current_surface_becomes_unsupported(self):
        handoff = waiting_handoff_v4(self.state)
        self.store.sync(handoff, self.broker.records, "session-1", True, self.broker.broker_epoch)
        before = self.store.snapshot()
        self.assertTrue(before["controls"]["awaiting_user"])
        manual = next(event for event in before["inbox"] if event["kind"] == "manual")

        unsupported = copy.deepcopy(self.state)
        unsupported["legal_actions"] = []
        unsupported["surface"].update(kind="unknown", phase="unsupported", status="unsupported",
                                       completeness="unavailable", surface_ref=None,
                                       customer_ref=None, dialogue_ref=None, controls=[])
        self.assertTrue(valid_snapshot_v4(unsupported))
        after = self.store.sync(unsupported, self.broker.records, "session-1", True,
                                self.broker.broker_epoch)

        self.assertFalse(after["controls"]["awaiting_user"])
        self.assertTrue(after["controls"]["unsupported"])
        self.assertEqual(after["controls"]["reason"], "unsupported_interaction")
        self.assertEqual(after["run"]["status"], "unsupported")
        old_manual = next(event for event in after["inbox"] if event["event_id"] == manual["event_id"])
        self.assertEqual(old_manual["status"], "superseded")
        self.assertTrue(any(event["kind"] == "unsupported" and event["status"] == "unsupported"
                            for event in after["inbox"]))

    def test_confirmed_resume_still_requires_resume_in_current_legal_actions(self):
        handoff = not_ready_handoff_v4(self.state)
        self.store.sync(handoff, self.broker.records, "session-1", True, self.broker.broker_epoch)
        current = self.store.snapshot()
        confirmed = self.store.control("confirm_inspection", current["revision"], state=handoff,
                                       session_id="session-1", instruction="continue by user request")
        running = self.store.control("resume", confirmed["revision"])
        token = self.store.grant("supervisor-not-ready")
        request = {"schema_version": 4, "id": "resume-not-legal", "broker_epoch": self.broker.broker_epoch,
                   "action": "resume_inspection_handoff", "expected_world_epoch": handoff["world_epoch"],
                   "expected_revision": handoff["revision"], "expected_version": handoff["version"],
                   "arguments": {"handoff_ref": "handoff-1"}}
        with self.assertRaisesRegex(RunStoreError, "run_store_action_not_legal"):
            self.store.reserve(request, handoff, self.broker.records, "session-1", token)
        after = self.store.snapshot()
        self.assertEqual(after["commands"][0]["reason"], "run_store_action_not_legal")
        self.assertEqual(after["operator_confirmation"]["command_id"], request["id"])
        self.assertEqual(after["budget"]["writes"], running["budget"]["writes"] + 1)

    def test_confirmed_resume_remains_blocked_on_target_day(self):
        handoff = waiting_handoff_v4(self.state)
        handoff["business"]["details"]["day"]["day_number"] = 4
        self.assertTrue(valid_snapshot_v4(handoff))
        self.store.sync(handoff, self.broker.records, "session-1", True, self.broker.broker_epoch)
        current = self.store.snapshot()
        confirmed = self.store.control("confirm_inspection", current["revision"], state=handoff,
                                       session_id="session-1", instruction="continue by user request")
        running = self.store.control("resume", confirmed["revision"])
        token = self.store.grant("supervisor-target-day")
        request = request_for(handoff, self.broker.broker_epoch, "resume-at-target-day")
        with self.assertRaisesRegex(RunStoreError, "run_store_target_day_write_forbidden"):
            self.store.reserve(request, handoff, self.broker.records, "session-1", token)
        after = self.store.snapshot()
        self.assertEqual(after["commands"][0]["reason"], "run_store_target_day_write_forbidden")
        self.assertEqual(after["operator_confirmation"]["command_id"], request["id"])
        self.assertEqual(after["budget"]["writes"], running["budget"]["writes"] + 1)

    def test_legacy_run_document_loads_only_known_old_top_level_shape(self):
        old_document = self.store.snapshot()
        del old_document["operator_confirmation"]
        self.run_path.write_text(json.dumps(old_document, separators=(",", ":")), encoding="utf-8")
        loaded = self.store.snapshot()
        self.assertIsNone(loaded["operator_confirmation"])
        self.assertNotIn("operator_confirmation", json.loads(self.run_path.read_text(encoding="utf-8")))

        self.store.control("plan", loaded["revision"], text="persist migrated nullable field")
        persisted = json.loads(self.run_path.read_text(encoding="utf-8"))
        self.assertIsNone(persisted["operator_confirmation"])

        persisted["unexpected"] = "never migrate arbitrary fields"
        self.run_path.write_text(json.dumps(persisted, separators=(",", ":")), encoding="utf-8")
        with self.assertRaisesRegex(RunStoreError, "run_store_corrupt"):
            self.store.snapshot()

    def test_mark_completed_accepts_a_real_v4_closed_day_chain(self):
        self._run_completion_chain(include_optional_sort=False, suffix="completion")

    def test_mark_completed_accepts_target_morning_start_and_optional_sort(self):
        self._run_completion_chain(include_optional_sort=True, suffix="completion-sort")

    def test_old_unsupported_inbox_event_does_not_block_completion(self):
        self._run_completion_chain(include_optional_sort=False, suffix="completion-unsupported",
                                   seed_stale_unsupported=True)

    def test_stale_manual_handoff_is_superseded_and_confirmed_resume_allows_completion(self):
        run_path = ROOT / "tests" / (self.tag + "-manual-completion.json")
        journal_path = ROOT / "tests" / (self.tag + "-manual-completion-journal.json")
        self.extra_paths.extend((run_path, run_path.with_suffix(run_path.suffix + ".lock"),
                                 journal_path, journal_path.with_suffix(journal_path.suffix + ".lock")))
        base = snapshot_v4()
        handoff_a = waiting_handoff_v4(base, handoff_ref="handoff-a", item_refs=("item-24",))
        handoff_b = waiting_handoff_v4(base, handoff_ref="handoff-b", item_refs=("item-25",))
        handoff_b["business"]["customer_ref"] = "customer-2"
        handoff_b["business"]["details"]["customer"]["dialogue_ref"] = "dialogue-2"
        handoff_b["business"]["details"]["inspection_handoff"]["customer_ref"] = "customer-2"
        handoff_b["surface"]["customer_ref"] = "customer-2"
        handoff_b["surface"]["dialogue_ref"] = "dialogue-2"
        self.assertTrue(valid_snapshot_v4(handoff_b))

        limits = {"customers": 12, "writes": 16, "trades": 4, "buy_spend": 5000,
                  "bargains": 4, "day_fees": 500, "days": 1}
        actions = ["close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"]
        store = RunStore(run_path, clock=self.clock)
        store.create("run-manual-completion", "save-test", journal_path, base["world_epoch"],
                     "session-1", 3, 4, limits, actions, "finish one day")
        journal = {"journal_format_version": 2, "broker_epoch": "broker-manual-completion", "records": []}
        store.recover(journal)
        store.sync(handoff_a, journal, "session-1", True, journal["broker_epoch"])
        event_a = next(event for event in store.snapshot()["inbox"] if event["kind"] == "manual")
        self.assertEqual(event_a["status"], "awaiting_user")

        store.sync(handoff_b, journal, "session-1", True, journal["broker_epoch"])
        context = store.snapshot()
        event_a = next(event for event in context["inbox"] if event["event_id"] == event_a["event_id"])
        event_b = next(event for event in context["inbox"] if event["kind"] == "manual"
                        and event["event_id"] != event_a["event_id"])
        self.assertEqual(event_a["status"], "superseded")
        self.assertEqual(event_a["superseded_by"], event_b["event_id"])
        self.assertEqual(event_b["status"], "awaiting_user")

        # 复现旧版本落盘状态：当前B事件已经存在，但陈旧人工事件仍停在人工门禁。
        seeded = copy.deepcopy(context)
        stale = next(event for event in seeded["inbox"] if event["event_id"] == event_a["event_id"])
        stale.update(status="awaiting_user", reason="manual_inspection_required", superseded_by=None)
        run_path.write_text(json.dumps(seeded, separators=(",", ":")), encoding="utf-8")
        # 以新实例重启后再次观察同一语义，必须仍清理陈旧事件。
        store = RunStore(run_path, clock=self.clock)
        context = store.sync(handoff_b, journal, "session-1", True, journal["broker_epoch"])
        event_a = next(event for event in context["inbox"] if event["event_id"] == event_a["event_id"])
        event_b = next(event for event in context["inbox"] if event["event_id"] == event_b["event_id"])
        self.assertEqual(event_a["status"], "superseded")
        self.assertEqual(event_a["superseded_by"], event_b["event_id"])
        self.assertEqual(event_b["status"], "awaiting_user")

        confirmed = store.control("confirm_inspection", context["revision"], state=handoff_b,
                                  session_id="session-1", instruction="continue after the user's request")
        store.control("resume", confirmed["revision"])
        resume_token = store.grant("supervisor-resume-handoff")
        resume_request = request_for(handoff_b, journal["broker_epoch"], "resume-handoff-b")
        store.reserve(resume_request, handoff_b, journal, "session-1", resume_token)

        resumed = day_snapshot_v4("close_business", 3, 2, 100)
        resumed["world_epoch"] = base["world_epoch"]
        resumed["version"] = f'{resumed["world_epoch"]}:{resumed["revision"]}'
        resumed_handoff = copy.deepcopy(handoff_b["business"]["details"]["inspection_handoff"])
        resumed_handoff.update(requires_manual_inspection=False, status="resumed",
                               last_resumed_handoff_ref="handoff-b")
        resumed["business"]["details"]["inspection_handoff"] = resumed_handoff
        self.assertTrue(valid_snapshot_v4(resumed))
        resume_record = success_record(resume_request, handoff_b, resumed, resume_request["id"],
                                       journal["broker_epoch"], "session-1", 1_000_000)
        journal["records"].append(resume_record)
        store.sync(resumed, journal, "session-1", True, journal["broker_epoch"])
        events_after_resume = store.snapshot()["inbox"]
        event_a = next(event for event in events_after_resume if event["event_id"] == event_a["event_id"])
        event_b = next(event for event in events_after_resume if event["event_id"] == event_b["event_id"])
        self.assertEqual(event_a["status"], "superseded")
        self.assertEqual(event_b["status"], "resolved")

        states = [resumed,
                  day_snapshot_v4("open_end_of_day", 3, 3, 100),
                  day_snapshot_v4("confirm_end_of_day", 3, 4, 100),
                  day_snapshot_v4("close_day_report", 4, 5, 60)]
        next_states = [states[1], states[2], states[3], day_snapshot_v4("start_business", 4, 6, 60)]
        for state in states + next_states:
            state["world_epoch"] = base["world_epoch"]
            state["version"] = f'{state["world_epoch"]}:{state["revision"]}'
        for index, (before, after, action) in enumerate(zip(states, next_states, actions)):
            token = store.grant("supervisor-day-chain")
            request = request_for(before, journal["broker_epoch"], f"manual-day-command-{index}")
            self.assertEqual(request["action"], action)
            store.reserve(request, before, journal, "session-1", token)
            journal["records"].append(success_record(
                request, before, after, request["id"], journal["broker_epoch"], "session-1",
                1_100_000 + index * 10_000))
            store.sync(after, journal, "session-1", True, journal["broker_epoch"])

        self.assertTrue(store.mark_completed(journal, next_states[-1], "session-1"))
        completed = store.snapshot()
        self.assertEqual(completed["run"]["status"], "completed")
        event_a = next(event for event in completed["inbox"] if event["event_id"] == event_a["event_id"])
        self.assertEqual(event_a["status"], "superseded")

    def test_optional_sort_remains_forbidden_as_a_target_day_write(self):
        run_path = ROOT / "tests" / (self.tag + "-target-sort.json")
        journal_path = ROOT / "tests" / (self.tag + "-target-sort-journal.json")
        self.extra_paths.extend((run_path, run_path.with_suffix(run_path.suffix + ".lock"),
                                 journal_path, journal_path.with_suffix(journal_path.suffix + ".lock")))
        state = day_snapshot_v4("start_business", 4, 5, 60)
        state["legal_actions"].append({
            "action": "sort_inventory_default", "arguments": {"container": "player"}})
        state["business"]["details"]["candidates"].append({
            "action": "sort_inventory_default", "status": "candidate", "reason": "optional morning sort"})
        self.assertTrue(valid_snapshot_v4(state))

        store = RunStore(run_path, clock=self.clock)
        limits = {"customers": 12, "writes": 16, "trades": 4, "buy_spend": 5000,
                  "bargains": 4, "day_fees": 500, "days": 1}
        store.create("run-target-sort", "save-test", journal_path, state["world_epoch"],
                     "session-1", 3, 4, limits, ["start_business", "sort_inventory_default"],
                     "finish one day")
        journal = {"journal_format_version": 2, "broker_epoch": "broker-target-sort", "records": []}
        store.recover(journal)
        store.sync(state, journal, "session-1", True, journal["broker_epoch"])
        current = store.snapshot()
        store.control("resume", current["revision"])
        token = store.grant("supervisor-target-sort")
        request = {"schema_version": 4, "id": "sort-at-target-day", "broker_epoch": journal["broker_epoch"],
                   "action": "sort_inventory_default", "expected_world_epoch": state["world_epoch"],
                   "expected_revision": state["revision"], "expected_version": state["version"],
                   "arguments": {"container": "player"}}
        with self.assertRaisesRegex(RunStoreError, "run_store_target_day_write_forbidden"):
            store.reserve(request, state, journal, "session-1", token)

    def _run_completion_chain(self, *, include_optional_sort, suffix, seed_stale_unsupported=False):
        run_path = ROOT / "tests" / (self.tag + "-" + suffix + ".json")
        journal_path = ROOT / "tests" / (self.tag + "-" + suffix + "-journal.json")
        self.extra_paths.extend((run_path, run_path.with_suffix(run_path.suffix + ".lock"),
                                 journal_path, journal_path.with_suffix(journal_path.suffix + ".lock")))
        store = RunStore(run_path, clock=self.clock)
        initial = day_snapshot_v4("close_business", 3, 1, 100)
        limits = {"customers": 12, "writes": 16, "trades": 4, "buy_spend": 5000,
                  "bargains": 4, "day_fees": 500, "days": 1}
        actions = ["close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"]
        store.create("run-completion", "save-test", journal_path, initial["world_epoch"],
                     "session-1", 3, 4, limits, actions, "finish one day")
        journal = {"journal_format_version": 2, "broker_epoch": "broker-completion", "records": []}
        store.recover(journal)
        stale_unsupported = None
        if seed_stale_unsupported:
            unsupported = copy.deepcopy(initial)
            unsupported["legal_actions"] = []
            unsupported["surface"].update(kind="unknown", phase="unsupported", status="unsupported",
                                           completeness="unavailable", surface_ref=None,
                                           customer_ref=None, dialogue_ref=None, controls=[])
            self.assertTrue(valid_snapshot_v4(unsupported))
            store.sync(unsupported, journal, "session-1", True, journal["broker_epoch"])
            stale_unsupported = next(event for event in store.snapshot()["inbox"]
                                    if event["kind"] == "unsupported")
            self.assertEqual(stale_unsupported["status"], "unsupported")
        store.sync(initial, journal, "session-1", True, journal["broker_epoch"])
        if stale_unsupported is not None:
            after_new_decision = store.snapshot()
            stale_unsupported = next(event for event in after_new_decision["inbox"]
                                     if event["event_id"] == stale_unsupported["event_id"])
            self.assertEqual(stale_unsupported["status"], "superseded")
            self.assertEqual(stale_unsupported["superseded_by"], after_new_decision["inbox"][-1]["event_id"])
        self.resume_store(store)

        states = [initial,
                  day_snapshot_v4("open_end_of_day", 3, 2, 100),
                  day_snapshot_v4("confirm_end_of_day", 3, 3, 100),
                  day_snapshot_v4("close_day_report", 4, 4, 60)]
        next_states = [states[1], states[2], states[3], day_snapshot_v4("start_business", 4, 5, 60)]
        if include_optional_sort:
            target_morning = next_states[-1]
            target_morning["legal_actions"].append({
                "action": "sort_inventory_default", "arguments": {"container": "player"}})
            target_morning["business"]["details"]["candidates"].append({
                "action": "sort_inventory_default", "status": "candidate", "reason": "optional morning sort"})
            self.assertTrue(valid_snapshot_v4(target_morning))
        for index, (before, after, action) in enumerate(zip(states, next_states, actions)):
            token = store.grant("supervisor-completion")
            request = request_for(before, journal["broker_epoch"], f"day-command-{index}")
            store.reserve(request, before, journal, "session-1", token)
            record = success_record(request, before, after, request["id"], journal["broker_epoch"],
                                    "session-1", 1_000_000 + index * 10_000)
            self.assertEqual(record["command"]["action"], action)
            journal["records"].append(record)
            self.assertTrue(Journal.valid_v2(journal))
            store.sync(after, journal, "session-1", True, journal["broker_epoch"])

        final_state = next_states[-1]
        self.assertEqual(final_state["surface"]["status"], "awaiting_agent")
        expected_actions = [{"action": "start_business", "arguments": {}}]
        if include_optional_sort:
            expected_actions.append({"action": "sort_inventory_default", "arguments": {"container": "player"}})
        self.assertEqual(final_state["legal_actions"], expected_actions)
        self.assertTrue(store.mark_completed(journal, final_state, "session-1"))
        completed = store.snapshot()
        self.assertEqual(completed["run"]["status"], "completed")
        self.assertEqual(len(completed["completion_evidence"]["command_ids"]), 4)
        self.assertEqual(len(completed["confirmed_history"]), 4)
        if seed_stale_unsupported:
            self.assertTrue(all(event["status"] in {"resolved", "superseded"}
                                for event in completed["inbox"]))
            self.assertTrue(any(event["kind"] == "unsupported" and event["status"] == "superseded"
                                for event in completed["inbox"]))
        else:
            self.assertTrue(all(event["status"] == "resolved" for event in completed["inbox"]))

        # Full, otherwise valid JSON must fail closed when its cross-field evidence is altered.
        bad_budget = copy.deepcopy(completed)
        bad_budget["budget"]["writes"] += 1
        self.assert_persisted_document_rejected(bad_budget, "budget-tampered")

        bad_history = copy.deepcopy(completed)
        bad_history["confirmed_history"][0]["command_id"] = "unlinked-success"
        self.assert_persisted_document_rejected(bad_history, "history-unlinked")

        bad_resolution = copy.deepcopy(completed)
        resolved_event = next(event for event in bad_resolution["inbox"] if event["status"] == "resolved")
        resolved_event["resolution_evidence"]["evidence_hash"] = "0" * 64
        self.assert_persisted_document_rejected(bad_resolution, "resolution-mismatched")

    def assert_persisted_document_rejected(self, document, suffix):
        path = ROOT / "tests" / (self.tag + "-" + suffix + ".json")
        self.extra_paths.extend((path, path.with_suffix(path.suffix + ".lock")))
        path.write_text(json.dumps(document, separators=(",", ":")), encoding="utf-8")
        with self.assertRaisesRegex(RunStoreError, "run_store_corrupt"):
            RunStore(path, clock=self.clock).snapshot()

    @staticmethod
    def resume_store(store):
        current = store.snapshot()
        return store.control("resume", current["revision"])


if __name__ == "__main__":
    unittest.main()
