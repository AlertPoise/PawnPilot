"""MCP SDK subprocess tests against an isolated HTTP broker fixture."""

from __future__ import annotations

import asyncio
import os
from pathlib import Path
import sys
import threading
import time
import unittest
from mcp import Client
from mcp.client.stdio import StdioServerParameters
from bridge.server import Broker, Server


ROOT = Path(__file__).resolve().parents[1]
TOKEN = "f" * 64
TERMINAL_STATUSES = ("succeeded", "rejected", "failed")


def fresh_snapshot() -> dict:
    return {
        "schema_version": 1,
        "broker_epoch": "broker-test-1",
        "connected": True,
        "blocking_reason": None,
        "blocked_command_id": None,
        "can_act": True,
        "state": {
            "schema_version": 1,
            "version": "world-test-1:7",
            "loaded": True,
            "ready": True,
            "world_epoch": "world-test-1",
            "revision": 7,
            "phase": "store",
            "scene": "InventoryScene",
            "cash": 125,
            "menu_open": True,
            "can_close_menu": True,
            "not_ready_reason": None,
        },
    }


class McpSubprocessCase(unittest.TestCase):
    def setUp(self):
        self.config_path = ROOT / "tests" / "fixtures" / "mcp-session.json"
        self.state = fresh_snapshot()["state"]
        self.broker = Broker(TOKEN, ttl=60, heartbeat_timeout=60)
        self.broker.exchange({"schema_version": 1, "broker_epoch": self.broker.broker_epoch,
                              "session_id": "mcp-test-session", "state": self.state,
                              "has_result": False})
        self.server = Server(self.broker, port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)

    def parameters(self):
        # Patch only the imported bridge client's test process port. Production
        # configuration continues to use the fixed 18765 loopback port.
        bootstrap = (
            "import bridge.client as c; "
            f"c.PORT={self.server.server_port}; "
            "import runpy; runpy.run_module('pawnpilot_mcp', run_name='__main__')"
        )
        env = os.environ.copy()
        env["PAWNPILOT_CONFIG"] = str(self.config_path)
        env["PYTHONPATH"] = str(ROOT) + os.pathsep + env.get("PYTHONPATH", "")
        return StdioServerParameters(command=sys.executable, args=["-c", bootstrap], cwd=ROOT, env=env)

    async def call(self, tool, args=None, *, list_tools=False):
        async with Client(self.parameters(), read_timeout_seconds=15) as client:
            if list_tools:
                return await client.list_tools()
            result = await client.call_tool(tool, args or {})
            self.assertFalse(result.is_error, result.content)
            self.assertIsInstance(result.structured_content, dict)
            return result.structured_content

    def call_sync(self, tool, args=None, *, list_tools=False):
        return asyncio.run(self.call(tool, args, list_tools=list_tools))

    def test_stdio_initializes_lists_four_tools_and_calls_all_tools(self):
        listed = self.call_sync("", list_tools=True)
        self.assertEqual({tool.name for tool in listed.tools},
                         {"health_check", "observe", "act", "get_action_status"})

        health = self.call_sync("health_check")
        self.assertTrue(health["protocol_compatible"])
        self.assertTrue(health["broker_connected"])
        self.assertTrue(health["bridge_connected"])
        self.assertEqual(health["revision"], 7)

        observed = self.call_sync("observe")
        self.assertEqual(observed["legal_actions"], ["close_menu"])
        self.assertEqual(observed["snapshot"]["state"]["cash"], 125)

        acted = self.call_sync("act", {
            "command_id": "cmd-001", "action": "close_menu", "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1", "expected_revision": 7,
            "expected_version": "world-test-1:7",
        })
        self.assertEqual(acted["command_status"], "pending")
        self.assertEqual(self.broker.records["cmd-001"]["command"], {
            "schema_version": 1, "id": "cmd-001", "action": "close_menu",
            "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1",
            "expected_revision": 7, "expected_version": "world-test-1:7",
            "expires_at_unix_ms": self.broker.records["cmd-001"]["command"]["expires_at_unix_ms"],
        })

        # A retry with an identical envelope returns the durable record, including
        # after the world has changed; the broker owns duplicate-ID semantics.
        self.state["revision"] = 8
        self.state["version"] = "world-test-1:8"
        self.broker.state = self.state
        repeated = self.call_sync("act", {
            "command_id": "cmd-001", "action": "close_menu", "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1", "expected_revision": 7,
            "expected_version": "world-test-1:7",
        })
        self.assertEqual(repeated["command_status"], "pending")

        self.assertEqual(self.broker.records["cmd-001"]["request"], {
            "schema_version": 1, "id": "cmd-001", "action": "close_menu",
            "broker_epoch": self.broker.broker_epoch, "expected_world_epoch": "world-test-1",
            "expected_revision": 7, "expected_version": "world-test-1:7",
        })

        status = self.call_sync("get_action_status", {"command_id": "cmd-001"})
        self.assertTrue(status["found"])
        self.assertEqual(status["command_status"], "pending")

    def test_known_outcome_statuses_and_not_found_are_distinguished(self):
        for index, command_status in enumerate(TERMINAL_STATUSES):
            command_id = f"cmd-{index}"
            queued = self.broker.enqueue({
                "schema_version": 1, "id": command_id, "action": "close_menu",
                "broker_epoch": self.broker.broker_epoch,
                "expected_world_epoch": "world-test-1", "expected_revision": 7,
                "expected_version": "world-test-1:7",
            })
            record = self.broker.records[command_id]
            result = {"schema_version": 1, "id": command_id,
                      "broker_epoch": self.broker.broker_epoch, "status": command_status,
                      "reason": "fixture_terminal", "before": self.state, "after": self.state}
            self.broker._terminal(record, command_status, "fixture_terminal", result)
            result = self.call_sync("get_action_status", {"command_id": command_id})
            self.assertTrue(result["found"])
            self.assertEqual(result["command_status"], command_status)
            self.assertEqual(result["result"]["id"], command_id)
            self.assertEqual(result["broker_epoch"], self.broker.broker_epoch)

        # Broker's actual pending/running transitions remain queryable.
        for command_id, command_status in (("cmd-pending", "pending"), ("cmd-running", "running")):
            self.broker.enqueue({
                "schema_version": 1, "id": command_id, "action": "close_menu",
                "broker_epoch": self.broker.broker_epoch,
                "expected_world_epoch": "world-test-1", "expected_revision": 7,
                "expected_version": "world-test-1:7",
            })
            record = self.broker.records[command_id]
            if command_status == "running":
                record["command_status"] = "running"
                record["delivery_session_id"] = "mcp-test-session"
            result = self.call_sync("get_action_status", {"command_id": command_id})
            self.assertEqual(result["command_status"], command_status)
            self.broker._terminal(record, "succeeded", "fixture_complete")

        command_id = "cmd-outcome-unknown"
        self.broker.enqueue({
            "schema_version": 1, "id": command_id, "action": "close_menu",
            "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1", "expected_revision": 7,
            "expected_version": "world-test-1:7",
        })
        record = self.broker.records[command_id]
        evidence = {"schema_version": 1, "id": command_id,
                    "broker_epoch": self.broker.broker_epoch, "status": "uncertain",
                    "reason": "delivery_outcome_unknown", "before": self.state, "after": self.state}
        self.broker._terminal(record, "outcome_unknown", "delivery_outcome_unknown", evidence)
        unknown = self.call_sync("get_action_status", {"command_id": command_id})
        self.assertEqual(unknown["command_status"], "outcome_unknown")

        missing = self.call_sync("get_action_status", {"command_id": "missing"})
        self.assertFalse(missing["found"])
        self.assertEqual(missing["command_status"], "not_found")

    def test_broker_rejects_pending_command_when_observed_state_advances(self):
        command_id = "cmd-stale-before-delivery"
        self.broker.enqueue({
            "schema_version": 1, "id": command_id, "action": "close_menu",
            "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1", "expected_revision": 7,
            "expected_version": "world-test-1:7",
        })
        self.state["revision"] = 8
        self.state["version"] = "world-test-1:8"
        self.broker.exchange({"schema_version": 1, "broker_epoch": self.broker.broker_epoch,
                              "session_id": "mcp-test-session", "state": self.state,
                              "has_result": False})
        result = self.call_sync("get_action_status", {"command_id": command_id})
        self.assertEqual(result["command_status"], "rejected")
        self.assertEqual(result["result"]["reason"], "stale_state")
        self.assertFalse(result["resolved"])

    def test_manual_alignment_is_returned_as_separate_resolution_evidence(self):
        command_id = "cmd-unknown-reconcile"
        self.broker.enqueue({
            "schema_version": 1, "id": command_id, "action": "close_menu",
            "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1", "expected_revision": 7,
            "expected_version": "world-test-1:7",
        })
        record = self.broker.records[command_id]
        self.broker._terminal(record, "outcome_unknown", "delivery_outcome_unknown")
        record["command"]["expires_at_unix_ms"] = 0
        self.state["revision"] = 8
        self.state["version"] = "world-test-1:8"
        self.state["menu_open"] = False
        self.state["can_close_menu"] = False
        self.broker.exchange({"schema_version": 1, "broker_epoch": self.broker.broker_epoch,
                              "session_id": "mcp-test-session", "state": self.state,
                              "has_result": False})
        self.broker.reconcile({
            "schema_version": 1, "id": command_id, "broker_epoch": self.broker.broker_epoch,
            "world_epoch": "world-test-1", "revision": 8, "expected_version": "world-test-1:8",
            "operator_note": "Verified the menu is closed in the current state.",
            "confirm_no_replay": True,
        })
        result = self.call_sync("get_action_status", {"command_id": command_id})
        self.assertEqual(result["command_status"], "outcome_unknown")
        self.assertTrue(result["resolved"])
        self.assertEqual(result["resolution"]["method"], "manual_state_alignment")
        self.assertEqual(result["resolution"]["snapshot"]["revision"], 8)

    def test_broker_down_does_not_prevent_stdio_tool_registration(self):
        port = self.server.server_port
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.server.server_port = port
        listed = self.call_sync("", list_tools=True)
        self.assertEqual(len(listed.tools), 4)
        health = self.call_sync("health_check")
        self.assertFalse(health["broker_connected"])
        self.assertEqual(health["error_source"], "connection")
        status = self.call_sync("get_action_status", {"command_id": "offline-id"})
        self.assertIsNone(status["found"])
        self.assertEqual(status["error_source"], "connection")

    def test_malformed_schema_version_fails_closed_as_protocol_error(self):
        self.state["schema_version"] = 2
        self.broker.state = self.state
        health = self.call_sync("health_check")
        self.assertFalse(health["protocol_compatible"])
        self.assertEqual(health["error_source"], "protocol")
        observed = self.call_sync("observe")
        self.assertEqual(observed["error_source"], "protocol")

    def test_malformed_game_version_fails_closed_as_protocol_error(self):
        self.state["version"] = 7
        self.broker.state = self.state
        health = self.call_sync("health_check")
        self.assertFalse(health["protocol_compatible"])
        self.assertEqual(health["error_source"], "protocol")

    def test_command_timeout_is_outcome_unknown_without_retry(self):
        original_enqueue = self.broker.enqueue

        def delayed_enqueue(body):
            queued = original_enqueue(body)
            time.sleep(4.5)  # bridge.client has a four-second, non-retrying timeout.
            return queued

        self.broker.enqueue = delayed_enqueue
        result = self.call_sync("act", {
            "command_id": "cmd-timeout", "action": "close_menu", "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1", "expected_revision": 7,
            "expected_version": "world-test-1:7",
        })
        self.assertEqual(result["command_status"], "outcome_unknown")
        self.assertEqual(result["error_source"], "connection")
        self.assertEqual(self.broker.records["cmd-timeout"]["command"]["id"], "cmd-timeout")
        time.sleep(0.7)
        self.assertEqual(len([r for r in self.broker.records if r == "cmd-timeout"]), 1)
        later = self.call_sync("get_action_status", {"command_id": "cmd-timeout"})
        self.assertTrue(later["found"])
        self.assertEqual(later["command_status"], "pending")


if __name__ == "__main__":
    unittest.main()
