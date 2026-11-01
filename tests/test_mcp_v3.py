"""MCP version unions preserve v2 history and route explicit v3 parameters."""
import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from pawnpilot_mcp import server as mcp_server
from bridge.client import ResponseError, read_response
from bridge.protocol import V3_ACTION_ARGUMENTS, V3_PRODUCTION_ACTIONS

V2 = json.loads((ROOT / "protocol/fixtures/v2-close-menu.json").read_text(encoding="utf-8"))
V3 = json.loads((ROOT / "protocol/fixtures/v3-business-observation.json").read_text(encoding="utf-8"))
V3_DIALOGUE = json.loads((ROOT / "protocol/fixtures/v3-dialogue-snapshot.json").read_text(encoding="utf-8"))


class McpV3Tests(unittest.TestCase):
    def test_observed_action_version_routes_v2_and_v3_without_using_reply_version(self):
        for version, state in ((2, V2["before"]), (3, V3["before"])):
            with self.subTest(version=version):
                snapshot = mcp_server._parse_snapshot({"schema_version": version,
                    "broker_epoch": "broker", "session_id": "mod", "connected": True,
                    "state": state, "blocking_reason": None, "blocked_command_id": None,
                    "can_act": True, "execution": None})
                with patch.object(mcp_server, "_snapshot", return_value=snapshot):
                    observed = mcp_server.observe().model_dump(mode="json")
                    health = mcp_server.health_check().model_dump(mode="json")
                self.assertEqual(observed["schema_version"], 2)  # Unchanged MCP envelope.
                self.assertEqual(observed["action_schema_version"], version)
                self.assertEqual(health["action_schema_version"], version)
                self.assertEqual(observed["action_schema_version"], observed["snapshot"]["state"]["schema_version"])
                pending = {"schema_version": version, "id": "version-copy", "command_status": "pending",
                    "status": "pending", "result": None, "resolved": False, "resolution": None,
                    "execution": None, "record_schema_version": version}
                with patch.object(mcp_server, "_call", return_value=(200, pending)) as call:
                    reply = mcp_server.act(observed["action_schema_version"], "version-copy", "close_menu",
                        mcp_server.CloseMenuArguments(), "broker", state["world_epoch"], state["revision"], state["version"])
                self.assertTrue(reply.ok)
                self.assertEqual(call.call_count, 1)
                self.assertEqual(call.call_args.args[1]["schema_version"], version)

    def test_action_version_is_unknown_without_connected_state(self):
        for connected, state in ((False, V3["before"]), (True, None)):
            snapshot = mcp_server._parse_snapshot({"schema_version": 3,
                "broker_epoch": "broker", "session_id": "mod", "connected": connected,
                "state": state, "blocking_reason": None, "blocked_command_id": None,
                "can_act": False, "execution": None})
            with patch.object(mcp_server, "_snapshot", return_value=snapshot):
                self.assertIsNone(mcp_server.observe().action_schema_version)
                self.assertIsNone(mcp_server.health_check().action_schema_version)
        with patch.object(mcp_server, "_snapshot", side_effect=mcp_server.AdapterError("connection", "offline")):
            self.assertIsNone(mcp_server.observe().action_schema_version)
            self.assertIsNone(mcp_server.health_check().action_schema_version)

    def test_observe_reply_accepts_all_production_action_names(self):
        # The nested snapshot already accepted purchase, but its top-level projection
        # used to throw validation errors exactly when a real purchase became legal.
        response = mcp_server.ObserveReply(ok=True, error_source="none",
            broker_connected=True, bridge_connected=True,
            legal_actions=sorted(V3_PRODUCTION_ACTIONS))
        self.assertIn("purchase_visible_offer", response.legal_actions)


    def test_v3_broker_response_may_exceed_legacy_limit_but_v2_cannot(self):
        class Headers:
            def get_content_type(self): return "application/json"
        class Response:
            headers = Headers()
            def __init__(self, raw): self.raw = raw
            def read(self, count): return self.raw[:count]
        body = json.dumps({"schema_version": 3, "padding": "x" * 70_000}, separators=(",", ":")).encode()
        self.assertGreater(len(body), 64 * 1024)
        self.assertEqual(read_response(Response(body))["schema_version"], 3)
        legacy = json.dumps({"schema_version": 2, "padding": "x" * 70_000}, separators=(",", ":")).encode()
        with self.assertRaises(ResponseError) as caught:
            read_response(Response(legacy))
        self.assertEqual(str(caught.exception), "response_too_large")

    def test_explicit_snapshot_and_result_unions(self):
        v3_snapshot = {"schema_version": 3, "broker_epoch": "broker", "session_id": "mod",
            "connected": True, "state": V3["before"], "blocking_reason": None,
            "blocked_command_id": None, "can_act": True, "execution": None}
        parsed = mcp_server._parse_snapshot(v3_snapshot)
        self.assertIsInstance(parsed, mcp_server.SnapshotV3)
        self.assertEqual(parsed.state.business["details"]["schema_version"], 1)
        dialogue = mcp_server._parse_snapshot({"schema_version": 3, "broker_epoch": "broker",
            "session_id": "mod", "connected": True, "state": V3_DIALOGUE,
            "blocking_reason": None, "blocked_command_id": None, "can_act": True, "execution": None})
        self.assertEqual(dialogue.state.business["details"]["customer"]["options"][0]["text"], "Yes, please")
        self.assertEqual(dialogue.state.legal_actions[0].action, "choose_dialogue_option")
        self.assertEqual(mcp_server.GameState.model_validate(V2["before"]).schema_version, 2)
        self.assertEqual(mcp_server.ActionEvidenceV3.model_validate(V3["result"]).status, "succeeded")
        self.assertEqual(mcp_server.ActionEvidenceV2.model_validate(V2["result"]).schema_version, 2)

    def test_command_history_keeps_the_record_schema_version(self):
        v3 = {"schema_version": 3, "id": V3["result"]["id"], "command_status": "succeeded",
              "status": "succeeded", "result": V3["result"], "resolved": False, "resolution": None,
              "execution": V3["progress"], "record_schema_version": 3}
        self.assertEqual(mcp_server.CommandReply.model_validate(v3).record_schema_version, 3)
        v2 = {"schema_version": 2, "id": V2["result"]["id"], "command_status": "succeeded",
              "status": "succeeded", "result": V2["result"], "resolved": False, "resolution": None,
              "execution": V2["progress"], "record_schema_version": 2}
        self.assertEqual(mcp_server.CommandReply.model_validate(v2).record_schema_version, 2)
        relabeled = dict(v2, schema_version=3)
        with self.assertRaises(Exception):
            mcp_server.CommandReply.model_validate(relabeled)

    def test_act_sends_exact_v3_version_and_omits_unset_null_arguments(self):
        captured = {}
        pending_reply = {"schema_version": 3, "id": "v3-act", "command_status": "pending",
                         "status": "pending", "result": None, "resolved": False, "resolution": None,
                         "execution": None, "record_schema_version": 3}
        def fake_call(path, body=None):
            captured["path"], captured["body"] = path, copy.deepcopy(body)
            return 200, pending_reply
        with patch.object(mcp_server, "_call", fake_call):
            response = mcp_server.act(3, "v3-act", "close_menu", mcp_server.CloseMenuArguments(),
                                      "broker", "world", 1, "world:1")
        self.assertTrue(response.ok)
        self.assertEqual(captured["path"], "/commands")
        self.assertEqual(captured["body"]["schema_version"], 3)
        self.assertEqual(captured["body"]["arguments"], {})

    def test_act_can_submit_enabled_business_action_without_a_new_tool(self):
        captured = {}
        pending_reply = {"schema_version": 3, "id": "v3-open", "command_status": "pending",
                         "status": "pending", "result": None, "resolved": False, "resolution": None,
                         "execution": None, "record_schema_version": 3}
        def fake_call(path, body=None):
            captured["path"], captured["body"] = path, copy.deepcopy(body)
            return 200, pending_reply
        with patch.object(mcp_server, "_call", fake_call):
            response = mcp_server.act(3, "v3-open", "start_business", mcp_server.ActionArgumentsV3(),
                                      "broker", "world", 1, "world:1")
        self.assertTrue(response.ok)
        self.assertEqual(captured["path"], "/commands")
        self.assertEqual(captured["body"]["schema_version"], 3)
        self.assertEqual(captured["body"]["action"], "start_business")
        self.assertEqual(captured["body"]["arguments"], {})

    def test_purchase_arguments_have_no_authorization_token(self):
        self.assertEqual(V3_ACTION_ARGUMENTS["purchase_visible_offer"], frozenset({
            "customer_ref", "offer_ref", "item_refs", "collection_fingerprint", "mode", "amount", "max_spend"}))
        self.assertNotIn("authorization_ref", mcp_server.ActionArgumentsV3.model_fields)

    def test_v2_act_cannot_silently_upgrade_into_v3_action_space(self):
        with patch.object(mcp_server, "_call") as call:
            response = mcp_server.act(2, "v2-start", "start_business", mcp_server.ActionArgumentsV3(),
                                      "broker", "world", 1, "world:1")
        call.assert_not_called()
        self.assertFalse(response.ok)
        self.assertEqual(response.error_source, "protocol")
        self.assertEqual(response.reason, "action_not_allowed")


if __name__ == "__main__": unittest.main()
