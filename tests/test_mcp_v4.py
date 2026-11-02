"""MCP v4 models route the reviewed action version and validate interaction receipts."""
import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from pawnpilot_mcp import server as mcp_server
from tests.test_protocol_v4 import command_v4, progress, result_for, snapshot_v4, transitioned_after

RUNTIME_FIXTURE = json.loads(
    (ROOT / "protocol/fixtures/v4-interaction-fixtures.json").read_text(encoding="utf-8"))


class McpV4Tests(unittest.TestCase):
    def envelope(self, state=None):
        return {"schema_version": 4, "broker_epoch": "broker-v4", "session_id": "module-v4",
                "connected": True, "state": snapshot_v4() if state is None else state,
                "blocking_reason": None, "blocked_command_id": None, "can_act": True, "execution": None}

    def receipt(self):
        return {"input_receipt": "returned", "verification": "confirmed",
                "effect_contract": "presentation", "observed_effects": ["dialogue_transition"],
                "unverified_effects": ["presentation"], "cash_delta": 0,
                "added_item_refs": [], "removed_item_refs": []}

    def test_snapshot_models_include_surface_and_observe_routes_schema_four(self):
        snapshot = mcp_server._parse_snapshot(self.envelope())
        self.assertIsInstance(snapshot, mcp_server.SnapshotV4)
        self.assertIsInstance(snapshot.state, mcp_server.GameStateV4)
        self.assertIsInstance(snapshot.state.surface, mcp_server.SurfaceObservationV4)
        self.assertEqual(snapshot.state.surface.controls[0].control_ref, "control-1")
        with patch.object(mcp_server, "_snapshot", return_value=snapshot):
            observed = mcp_server.observe()
        self.assertEqual(observed.action_schema_version, 4)
        self.assertIn("choose_dialogue_option", observed.legal_actions)

    def test_act_sends_exact_generic_v4_arguments_and_rejects_refs_under_v3(self):
        captured = {}
        pending = {"schema_version": 4, "id": "mcp-v4", "command_status": "pending",
                   "status": "pending", "result": None, "resolved": False, "resolution": None,
                   "execution": None, "record_schema_version": 4}

        def fake_call(path, body=None):
            captured["path"], captured["body"] = path, copy.deepcopy(body)
            return 200, pending

        args = mcp_server.ActionArgumentsV4(customer_ref="customer-1", dialogue_ref="dialogue-1",
                                            option_ref="option-1", surface_ref="surface-1",
                                            control_ref="control-1")
        with patch.object(mcp_server, "_call", fake_call):
            reply = mcp_server.act(4, "mcp-v4", "choose_dialogue_option", args,
                                   "broker-v4", "world-1", 1, "world-1:1")
        self.assertTrue(reply.ok)
        self.assertEqual(captured["path"], "/commands")
        self.assertEqual(captured["body"]["schema_version"], 4)
        self.assertEqual(captured["body"]["arguments"], {
            "customer_ref": "customer-1", "dialogue_ref": "dialogue-1", "option_ref": "option-1",
            "surface_ref": "surface-1", "control_ref": "control-1"})

        with patch.object(mcp_server, "_call") as call:
            rejected = mcp_server.act(3, "legacy-v3", "choose_dialogue_option", args,
                                      "broker-v4", "world-1", 1, "world-1:1")
        self.assertFalse(rejected.ok)
        self.assertEqual(rejected.error, "action_not_allowed")
        call.assert_not_called()

    def test_v4_history_accepts_verified_receipt_and_rejects_failed_postcondition(self):
        before = snapshot_v4()
        command = command_v4(before)
        after = transitioned_after(before)
        evidence = result_for(command, before, after, self.receipt())
        typed = mcp_server.ActionEvidenceV4.model_validate(evidence)
        self.assertEqual(typed.interaction_result.verification, "confirmed")
        record = {"schema_version": 4, "id": command["id"], "command_status": "succeeded",
                  "status": "succeeded", "result": evidence, "resolved": False,
                  "resolution": None, "execution": evidence["execution"], "record_schema_version": 4}
        history = mcp_server.CommandReply.model_validate(record)
        self.assertEqual(history.result.schema_version, 4)

        changed_cash = copy.deepcopy(after)
        changed_cash["cash"] += 1
        forged = result_for(command, before, changed_cash, dict(self.receipt(), cash_delta=1))
        with self.assertRaises(Exception):
            mcp_server.ActionEvidenceV4.model_validate(forged)

    def test_mcp_history_parses_every_managed_runtime_receipt_case(self):
        for name, case in RUNTIME_FIXTURE["cases"].items():
            with self.subTest(case=name):
                result = case["result"]
                typed_result = mcp_server.ActionEvidenceV4.model_validate(result)
                history = mcp_server.CommandReply.model_validate({
                    "schema_version": 4,
                    "id": result["id"],
                    "command_status": result["status"],
                    "status": result["status"],
                    "result": result,
                    "resolved": False,
                    "resolution": None,
                    "execution": result["execution"],
                    "record_schema_version": 4,
                })
                self.assertEqual(history.result, typed_result)


if __name__ == "__main__":
    unittest.main()
