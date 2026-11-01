"""M4 CLI session audit, cumulative budgets, handoff, and one-day stop rules."""
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
from uuid import uuid4
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("verify_codex", ROOT / "scripts" / "verify-codex.py")
verify_codex = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verify_codex)


def result(payload):
    return {"content": [{"type": "text", "text": json.dumps(payload)}]}


def call(event_type, item_id, tool, arguments=None, payload=None):
    item = {"id": item_id, "type": "mcp_tool_call", "server": "pawnpilot", "tool": tool,
            "arguments": arguments or {}, "result": result(payload) if payload is not None else None}
    return {"type": f"item.{event_type}", "item": item}


def observation(day, revision=1, handoff=None):
    details = {"day": {"day_number": day, "remaining_customers": 4, "stage": "SERVING",
                       "can_close": False, "can_sleep": False, "can_confirm": False,
                       "report_open": False, "transition_pending": False, "reason": None,
                       "settlement": {"settlement_ref": None, "total": None, "services": []}}}
    if handoff is not None:
        details["inspection_handoff"] = handoff
    return {"snapshot": {"state": {"schema_version": 3, "world_epoch": "world-1",
            "revision": revision, "version": f"world-1:{revision}", "cash": 500,
            "business": {"details": details}}}}


def act_pair(command_id, action, arguments=None, item_id="item_1", direct_status="pending"):
    payload = {"command_id": command_id, "command_status": direct_status, "status": direct_status}
    args = {"command_id": command_id, "action": action, "arguments": arguments or {}}
    return [call("started", item_id, "act", args), call("completed", item_id, "act", args, payload)]


def status_pair(command_id, status, before_cash=500, after_cash=500, item_id="item_2"):
    payload = {"command_id": command_id, "command_status": status, "status": status,
               "result": {"before": {"cash": before_cash}, "after": {"cash": after_cash}} if status == "succeeded" else None}
    args = {"command_id": command_id}
    return [call("started", item_id, "get_action_status", args),
            call("completed", item_id, "get_action_status", args, payload)]


def budgets(**updates):
    value = {"max_customers": 3, "max_writes": 8, "max_spend": 500,
             "max_trades": 2, "max_bargains": 1, "max_night_fee": 200}
    value.update(updates)
    return value


class M4SessionTests(unittest.TestCase):
    def write_evidence(self, events):
        path = ROOT / "tests" / f".m4-test-{uuid4().hex}.jsonl"
        path.write_text("".join(json.dumps(event) + "\n" for event in events), encoding="utf-8")
        self.addCleanup(lambda p=path: p.unlink(missing_ok=True))
        return path

    def test_item_ids_restart_per_run_but_act_and_status_history_accumulates(self):
        first = self.write_evidence([
            call("completed", "item_0", "observe", payload=observation(7, 1)),
            *act_pair("receive-1", "receive_next_customer", item_id="item_1"),
            *status_pair("receive-1", "succeeded", item_id="item_2"),
        ])
        second = self.write_evidence([
            call("completed", "item_0", "observe", payload=observation(7, 5)),
            *act_pair("buy-1", "purchase_visible_offer", {"amount": 100}, item_id="item_1"),
            *status_pair("buy-1", "succeeded", 500, 400, item_id="item_2"),
        ])
        audit = verify_codex.analyze_m4_evidence([first, second])
        self.assertEqual(audit["act_attempts"], 2)
        self.assertEqual(audit["customer_attempts"], 1)
        self.assertEqual(audit["trade_attempts"], 1)
        self.assertEqual(audit["initial_day"], 7)
        self.assertTrue(audit["baseline_valid"])
        self.assertEqual(audit["purchase_reserved"], 100)
        self.assertEqual(audit["actual_paid"], 100)
        self.assertEqual(audit["unresolved_command_ids"], [])

    def test_rejected_trade_attempt_counts_and_reserves_requested_amount(self):
        path = self.write_evidence([
            call("completed", "item_0", "observe", payload=observation(3)),
            *act_pair("buy-rejected", "purchase_visible_offer", {"amount": 120}, item_id="item_1"),
            *status_pair("buy-rejected", "rejected", item_id="item_2"),
        ])
        audit = verify_codex.analyze_m4_evidence([path])
        self.assertEqual(audit["trade_attempts"], 1)
        self.assertEqual(audit["purchase_reserved"], 120)
        self.assertEqual(audit["actual_paid"], 0)
        self.assertEqual(audit["rejected_command_ids"], ["buy-rejected"])
        self.assertEqual(audit["unresolved_command_ids"], [])

    def test_negative_settlement_total_is_a_credit_with_zero_fee_reservation(self):
        path = self.write_evidence([
            call("completed", "item_0", "observe", payload=observation(3)),
            *act_pair("settlement-credit", "confirm_end_of_day",
                      {"settlement_ref": "settle-1", "amount": -75, "max_spend": 100}, item_id="item_1"),
            *status_pair("settlement-credit", "succeeded", 500, 575, item_id="item_2"),
        ])
        audit = verify_codex.analyze_m4_evidence([path])
        self.assertEqual(audit["night_fee_reserved"], 0)
        self.assertEqual(audit["actual_night_fee"], -75)

    def test_interrupted_command_requires_original_id_recovery_only(self):
        path = self.write_evidence([
            call("completed", "item_0", "observe", payload=observation(4)),
            *act_pair("old-command", "receive_next_customer", item_id="item_1"),
        ])
        audit = verify_codex.analyze_m4_evidence([path])
        self.assertEqual(audit["unresolved_command_ids"], ["old-command"])
        prompt = verify_codex.build_m4_prompt(SimpleNamespace(label="recovery", session_id="s1", confirmed_handoff=None), audit, {
            "max_writes": 7, "max_customers": 2, "max_spend": 500, "max_trades": 2,
            "max_bargains": 1, "max_night_fee": 200,
        }, None)
        self.assertIn("get_action_status for each listed ORIGINAL command ID", prompt)
        self.assertIn("RECOVERY-ONLY INVOCATION", prompt)
        self.assertIn("Do not submit any act", prompt)

    def test_day_plus_one_is_report_only_and_never_reopens_business(self):
        path = self.write_evidence([
            call("completed", "item_0", "observe", payload=observation(5)),
            call("completed", "item_0", "observe", payload=observation(6, 20)),
        ])
        audit = verify_codex.analyze_m4_evidence([path])
        prompt = verify_codex.build_m4_prompt(SimpleNamespace(label="day-end", session_id="s2", confirmed_handoff=None), audit, {
            "max_writes": 8, "max_customers": 3, "max_spend": 500, "max_trades": 2,
            "max_bargains": 1, "max_night_fee": 200,
        }, None)
        self.assertIn("day_number == initial_day + 1, NEVER start_business", prompt)
        self.assertIn("This invocation is report-only", prompt)
        self.assertIn("close_day_report is the sole possible write", prompt)
        self.assertIn("known item costs/profit information", prompt)
        self.assertIn("Do not hardcode a maximum bargain discount", prompt)
        self.assertIn("Leave day settlement services at their default selection", prompt)

    def test_status_not_found_or_failed_does_not_release_unknown_command(self):
        for status in ("not_found", "failed"):
            with self.subTest(status=status):
                path = self.write_evidence([
                    call("completed", "item_0", "observe", payload=observation(4)),
                    *act_pair("uncertain-command", "receive_next_customer", item_id="item_1"),
                    *status_pair("uncertain-command", status, item_id="item_2"),
                ])
                audit = verify_codex.analyze_m4_evidence([path])
                self.assertEqual(audit["unresolved_command_ids"], ["uncertain-command"])

    def test_handoff_resume_requires_exact_latest_unresumed_ref(self):
        handoff = {"requires_manual_inspection": True, "status": "waiting_for_user", "handoff_ref": "handoff-77"}
        original_root, original_dir = verify_codex.ROOT, verify_codex.M4_DIR
        session_id = f"m4-test-{uuid4().hex}"
        label = f"m4-{uuid4().hex[:10]}"
        verify_codex.ROOT = ROOT
        verify_codex.M4_DIR = ROOT / "tests"
        prior_label = f"prior-{label}"
        evidence = verify_codex.M4_DIR / f"{prior_label}.jsonl"
        manifest_path = verify_codex.M4_DIR / f"session-{session_id}.json"
        self.addCleanup(lambda p=evidence: p.unlink(missing_ok=True))
        self.addCleanup(lambda p=manifest_path: p.unlink(missing_ok=True))
        try:
            evidence.write_text(json.dumps(call("completed", "item_0", "observe",
                payload=observation(9, handoff=handoff))) + "\n", encoding="utf-8")
            session = {"schema_version": 1, "session_id": session_id, "budgets": budgets(),
                       "initial_day": 9, "runs": [{"label": prior_label, "evidence": str(evidence.relative_to(ROOT).as_posix()),
                                                   "status": "completed"}], "audit": {}}
            manifest_path.write_text(json.dumps(session), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "exactly match"):
                verify_codex.load_or_create_m4_session(session_id, budgets(), f"{label}-wrong", "wrong-ref")
            _, _, _, audit, _, current = verify_codex.load_or_create_m4_session(
                session_id, budgets(), label, "handoff-77")
            self.assertEqual(current, "handoff-77")
            self.assertEqual(audit["latest_handoff"]["status"], "waiting_for_user")
        finally:
            verify_codex.ROOT, verify_codex.M4_DIR = original_root, original_dir

    def test_session_budgets_are_cumulative_and_fixed_across_restarts(self):
        original_root, original_dir = verify_codex.ROOT, verify_codex.M4_DIR
        session_id = f"m4-test-{uuid4().hex}"
        first_label, second_label = f"m4-{uuid4().hex[:10]}", f"m4-{uuid4().hex[:10]}"
        verify_codex.ROOT = ROOT
        verify_codex.M4_DIR = ROOT / "tests"
        manifest_path = verify_codex.M4_DIR / f"session-{session_id}.json"
        first_output = verify_codex.M4_DIR / f"{first_label}.jsonl"
        self.addCleanup(lambda p=manifest_path: p.unlink(missing_ok=True))
        self.addCleanup(lambda p=first_output: p.unlink(missing_ok=True))
        self.addCleanup(lambda p=verify_codex.M4_DIR / f"{second_label}.jsonl": p.unlink(missing_ok=True))
        try:
            _, first_output, manifest, _, _, _ = verify_codex.load_or_create_m4_session(
                session_id, budgets(), first_label)
            first_output.write_text("".join(json.dumps(event) + "\n" for event in [
                call("completed", "item_0", "observe", payload=observation(2)),
                *act_pair("receive-1", "receive_next_customer", item_id="item_1"),
                *status_pair("receive-1", "succeeded", item_id="item_2"),
                *act_pair("buy-1", "purchase_visible_offer", {"amount": 90}, item_id="item_3"),
                *status_pair("buy-1", "succeeded", 500, 410, item_id="item_4"),
            ]), encoding="utf-8")
            verify_codex.finish_m4_session(manifest_path, manifest, 0)
            _, _, _, audit, remaining, _ = verify_codex.load_or_create_m4_session(
                session_id, budgets(), second_label)
            self.assertEqual(audit["act_attempts"], 2)
            self.assertEqual(remaining["max_writes"], 6)
            self.assertEqual(remaining["max_customers"], 2)
            self.assertEqual(remaining["max_spend"], 410)
            self.assertEqual(remaining["max_trades"], 1)
            with self.assertRaisesRegex(ValueError, "fixed at session creation"):
                verify_codex.load_or_create_m4_session(session_id, budgets(max_spend=600), f"{second_label}-more")
        finally:
            verify_codex.ROOT, verify_codex.M4_DIR = original_root, original_dir

    def test_malformed_or_truncated_jsonl_is_fail_closed(self):
        path = ROOT / "tests" / f".m4-test-{uuid4().hex}.jsonl"
        path.write_text('{"type":"item.started"}\n{"type":', encoding="utf-8")
        self.addCleanup(lambda p=path: p.unlink(missing_ok=True))
        with self.assertRaisesRegex(ValueError, "malformed M4 evidence"):
            verify_codex.analyze_m4_evidence([path])


if __name__ == "__main__":
    unittest.main()
