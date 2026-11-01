"""MCP v2 schemas, bounded waits, cancellation, and historical v1 evidence."""

from __future__ import annotations

import asyncio
import os
from pathlib import Path
import sys
import threading
import time
import unittest
from pydantic import ValidationError

from mcp import Client
from mcp.client.stdio import StdioServerParameters

from bridge.server import Broker, Server
from tests.test_mcp import ROOT, TOKEN, fresh_snapshot
from pawnpilot_mcp.server import (ActionEvidenceV1, ActionEvidenceV2, CommandReply, ExecutionProgress,
                                  GameState, GameStateV1, Snapshot)


class McpV2Case(unittest.TestCase):
    def setUp(self):
        self.config_path = ROOT / "tests" / "fixtures" / "mcp-session.json"
        self.state = fresh_snapshot()["state"]
        self.broker = Broker(TOKEN, ttl=60, heartbeat_timeout=60)
        self.broker.exchange({"schema_version": 2, "broker_epoch": self.broker.broker_epoch,
                              "session_id": "mcp-v2-session", "state": self.state,
                              "has_result": False, "result": None, "progress": None})
        self.server = Server(self.broker, port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)

    def parameters(self):
        bootstrap = ("import bridge.client as c; " + f"c.PORT={self.server.server_port}; "
                     "import runpy; runpy.run_module('pawnpilot_mcp', run_name='__main__')")
        env = os.environ.copy()
        env["PAWNPILOT_CONFIG"] = str(self.config_path)
        env["PYTHONPATH"] = str(ROOT) + os.pathsep + env.get("PYTHONPATH", "")
        return StdioServerParameters(command=sys.executable, args=["-c", bootstrap], cwd=ROOT, env=env)

    async def _call(self, client, name, args=None):
        return await client.call_tool(name, args or {})

    def _wait_args(self, tool, request):
        return {"request": request} if "request" in tool.input_schema.get("properties", {}) else request

    def test_wait_revision_terminal_operable_and_timeout(self):
        async def exercise():
            async with Client(self.parameters(), read_timeout_seconds=15) as client:
                tools = await client.list_tools()
                wait_tool = next(tool for tool in tools.tools if tool.name == "wait_for_update")

                revision = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "revision", "broker_epoch": "previous-broker",
                    "world_epoch": "world-test-1", "revision": 7, "max_wait_ms": 1000,
                }))
                self.assertFalse(revision.is_error, revision.content)
                self.assertEqual(revision.structured_content["outcome"], "updated")

                command_id = "terminal-wait"
                self.broker.enqueue({"schema_version": 2, "id": command_id, "action": "close_menu",
                    "arguments": {}, "broker_epoch": self.broker.broker_epoch,
                    "expected_world_epoch": "world-test-1", "expected_revision": 7,
                    "expected_version": "world-test-1:7"})
                self.broker._terminal(self.broker.records[command_id], "outcome_unknown", "fixture_unknown")
                terminal = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "command_terminal", "command_id": command_id, "max_wait_ms": 1000,
                }))
                self.assertEqual(terminal.structured_content["outcome"], "terminal")
                self.assertEqual(terminal.structured_content["command_status"], "outcome_unknown")

                immediate = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "command_terminal", "command_id": command_id, "max_wait_ms": 0,
                }))
                self.assertEqual(immediate.structured_content["outcome"], "terminal")
                self.assertEqual(immediate.structured_content["command_status"], "outcome_unknown")

        asyncio.run(exercise())

        # Use an unblocked broker for the independent operable and timeout outcomes.
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.broker = Broker(TOKEN, ttl=60, heartbeat_timeout=60)
        self.broker.exchange({"schema_version": 2, "broker_epoch": self.broker.broker_epoch,
                              "session_id": "mcp-v2-session-2", "state": self.state,
                              "has_result": False, "result": None, "progress": None})
        self.server = Server(self.broker, port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

        async def remaining():
            async with Client(self.parameters(), read_timeout_seconds=15) as client:
                tools = await client.list_tools()
                wait_tool = next(tool for tool in tools.tools if tool.name == "wait_for_update")
                started = time.monotonic()
                operable = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "operable", "max_wait_ms": 1000,
                }))
                self.assertEqual(operable.structured_content["outcome"], "operable")
                self.assertTrue(operable.structured_content["snapshot"]["state"]["interaction"]["operable"])
                self.assertLess(time.monotonic() - started, 1.0)

                started = time.monotonic()
                timeout = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "command_terminal", "command_id": "not-yet-known", "max_wait_ms": 150,
                }))
                self.assertEqual(timeout.structured_content["outcome"], "timed_out")
                self.assertEqual(timeout.structured_content["command_status"], "not_found")
                self.assertIsNotNone(timeout.structured_content["snapshot"])
                self.assertLess(time.monotonic() - started, 1.0)

        asyncio.run(remaining())

    def test_wait_deadline_covers_both_reads_and_cancels_future_polls(self):
        async def exercise():
            original_snapshot = self.broker.snapshot
            original_result = self.broker.result
            calls = {"snapshot": 0, "result": 0}

            def slow_snapshot():
                calls["snapshot"] += 1
                time.sleep(0.20)
                return original_snapshot()

            def slow_result(command_id):
                calls["result"] += 1
                time.sleep(1.00)
                return original_result(command_id)

            self.broker.snapshot = slow_snapshot
            self.broker.result = slow_result
            async with Client(self.parameters(), read_timeout_seconds=15) as client:
                tools = await client.list_tools()
                wait_tool = next(tool for tool in tools.tools if tool.name == "wait_for_update")
                started = time.monotonic()
                result = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "command_terminal", "command_id": "slow-missing", "max_wait_ms": 800,
                }))
                elapsed = time.monotonic() - started
                self.assertFalse(result.is_error, result.content)
                self.assertEqual(result.structured_content["outcome"], "timed_out")
                self.assertLess(elapsed, 1.3, "The entire snapshot+result cycle must fit the deadline")
                self.assertEqual(calls, {"snapshot": 1, "result": 1})

                # A canceled wait may finish its current read cleanup, but must not poll again.
                calls.update(snapshot=0, result=0)
                pending = asyncio.create_task(self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "command_terminal", "command_id": "slow-missing", "max_wait_ms": 5000,
                })))
                await asyncio.sleep(0.03)
                pending.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await pending
                await asyncio.sleep(0.25)
                self.assertLessEqual(calls["snapshot"], 1)

        asyncio.run(exercise())

    def test_wait_and_act_schemas_reject_irrelevant_or_v1_fields(self):
        async def exercise():
            async with Client(self.parameters(), read_timeout_seconds=15) as client:
                tools = await client.list_tools()
                wait_tool = next(tool for tool in tools.tools if tool.name == "wait_for_update")
                invalid_wait = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "revision", "broker_epoch": self.broker.broker_epoch,
                    "world_epoch": "world-test-1", "revision": 7,
                    "command_id": "irrelevant", "max_wait_ms": 0,
                }))
                self.assertTrue(invalid_wait.is_error)
                missing_dependency = await self._call(client, "wait_for_update", self._wait_args(wait_tool, {
                    "criterion": "revision", "broker_epoch": self.broker.broker_epoch,
                    "world_epoch": "world-test-1", "max_wait_ms": 0,
                }))
                self.assertTrue(missing_dependency.is_error)
                v1_act = await self._call(client, "act", {
                    "schema_version": 1, "command_id": "v1-client", "action": "close_menu",
                    "arguments": {}, "broker_epoch": self.broker.broker_epoch,
                    "expected_world_epoch": "world-test-1", "expected_revision": 7,
                    "expected_version": "world-test-1:7",
                })
                self.assertTrue(v1_act.is_error)
                float_schema = await self._call(client, "act", {
                    "schema_version": 2.0, "command_id": "float-schema", "action": "close_menu",
                    "arguments": {}, "broker_epoch": self.broker.broker_epoch,
                    "expected_world_epoch": "world-test-1", "expected_revision": 7,
                    "expected_version": "world-test-1:7",
                })
                self.assertTrue(float_schema.is_error)
                bool_revision = await self._call(client, "act", {
                    "schema_version": 2, "command_id": "bool-revision", "action": "close_menu",
                    "arguments": {}, "broker_epoch": self.broker.broker_epoch,
                    "expected_world_epoch": "world-test-1", "expected_revision": True,
                    "expected_version": "world-test-1:True",
                })
                self.assertTrue(bool_revision.is_error)
                self.assertEqual(len(self.broker.records), 0)

        asyncio.run(exercise())

    def test_historical_v1_result_is_returned_without_relabeling(self):
        command_id = "old-v1-command"
        old_state = {
            "schema_version": 1, "version": "world-old:3", "loaded": True, "ready": True,
            "world_epoch": "world-old", "revision": 3, "phase": "store", "scene": "InventoryScene",
            "menu_open": False, "can_close_menu": False, "cash": 125, "not_ready_reason": None,
        }
        request = {"schema_version": 1, "id": command_id, "action": "close_menu",
                   "broker_epoch": "broker-old", "expected_world_epoch": "world-old",
                   "expected_revision": 2, "expected_version": "world-old:2"}
        self.broker.records[command_id] = {
            "command_id": command_id, "request": request,
            "command": dict(request, expires_at_unix_ms=1), "command_status": "failed",
            "result": {"schema_version": 1, "id": command_id, "broker_epoch": "broker-old",
                       "status": "error", "reason": "legacy_failure", "before": old_state, "after": old_state},
            "resolution": None, "execution": None,
        }

        result = self._call_status_sync(command_id)
        self.assertTrue(result["ok"])
        self.assertEqual(result["schema_version"], 2)
        self.assertEqual(result["record_schema_version"], 1)
        self.assertEqual(result["command_status"], "failed")
        self.assertEqual(result["result"]["schema_version"], 1)
        self.assertEqual(result["result"]["status"], "error")

    def test_unknown_result_can_retain_outer_execution_progress(self):
        command_id = "unknown-with-progress"
        self.broker.enqueue({"schema_version": 2, "id": command_id, "action": "close_menu",
            "arguments": {}, "broker_epoch": self.broker.broker_epoch,
            "expected_world_epoch": "world-test-1", "expected_revision": 7,
            "expected_version": "world-test-1:7"})
        record = self.broker.records[command_id]
        progress = {"id": command_id, "broker_epoch": self.broker.broker_epoch,
                    "stage": "verifying", "sequence": 2, "may_have_side_effects": True,
                    "started_at_unix_ms": 1, "execution_deadline_unix_ms": 1000}
        self.broker._terminal(record, "outcome_unknown", "delivery_outcome_unknown")
        record["execution"] = progress
        parsed = self._call_status_sync(command_id)
        self.assertEqual(parsed["command_status"], "outcome_unknown")
        self.assertEqual(parsed["execution"]["stage"], "verifying")
        self.assertIsNone(parsed["result"]["execution"])

    def test_raw_wire_numbers_and_terminal_relationships_are_strict(self):
        state = fresh_snapshot()["state"]
        progress = {"id": "strict-id", "broker_epoch": "strict-broker", "stage": "invoking",
                    "sequence": 1, "may_have_side_effects": True,
                    "started_at_unix_ms": 1, "execution_deadline_unix_ms": 1000}
        result = {"schema_version": 2, "id": "strict-id", "broker_epoch": "strict-broker",
                  "status": "outcome_unknown", "reason": "unknown", "before": None,
                  "after": None, "execution": {**progress, "stage": "uncertain"}}
        reply = {"schema_version": 2, "id": "strict-id", "command_status": "outcome_unknown",
                 "status": "outcome_unknown", "result": result, "resolved": False,
                 "resolution": None, "execution": result["execution"], "record_schema_version": 2}

        for validator, payload in (
            (Snapshot, {**fresh_snapshot(), "schema_version": 2.0}),
            (GameState, {**state, "schema_version": 2.0}),
            (ExecutionProgress, {**progress, "started_at_unix_ms": 0}),
            (ExecutionProgress, {**progress, "execution_deadline_unix_ms": 30002}),
            (CommandReply, {**reply, "schema_version": 2.0}),
            (CommandReply, {**reply, "record_schema_version": 2.0}),
            (CommandReply, {**reply, "result": {**result, "schema_version": 2.0}}),
            (CommandReply, {**reply, "status": "failed"}),
            (CommandReply, {**reply, "result": None}),
            (GameStateV1, {k: v for k, v in {
                "schema_version": 1, "version": "world-old:1", "world_epoch": "world-old",
                "revision": 1, "phase": "store", "scene": "InventoryScene", "loaded": True,
                "ready": True, "menu_open": False, "can_close_menu": False,
                "not_ready_reason": None, "cash": 1}.items() if k != "cash"}),
            (ActionEvidenceV1, {"id": "old", "broker_epoch": "old", "status": "error",
                                "reason": "legacy", "before": None, "after": None}),
            (ActionEvidenceV1, {"schema_version": 1.0, "id": "old", "broker_epoch": "old",
                                "status": "error", "reason": "legacy", "before": None, "after": None}),
        ):
            with self.subTest(model=validator.__name__, payload=payload):
                with self.assertRaises(ValidationError):
                    validator.model_validate(payload)

        blocked = {**state, "interaction": {"phase": "store", "consistency": "blocked",
                    "reason": "blocked", "operable": True}}
        with self.assertRaises(ValidationError):
            GameState.model_validate(blocked)

        pending_rejected = {"schema_version": 2, "id": "strict-id", "broker_epoch": "strict-broker",
            "status": "rejected", "reason": "command_expired_before_delivery",
            "before": None, "after": None, "execution": None}
        self.assertEqual(CommandReply.model_validate({"schema_version": 2, "id": "strict-id",
            "command_status": "rejected", "status": "rejected", "result": pending_rejected,
            "resolved": False, "resolution": None, "execution": None,
            "record_schema_version": 2}).command_status, "rejected")

    def test_success_evidence_enforces_shared_close_menu_postcondition(self):
        before = fresh_snapshot()["state"]
        after = {**before, "version": "world-test-1:8", "revision": 8,
                 "menu_open": False, "can_close_menu": False, "legal_actions": []}
        execution = {"id": "success-id", "broker_epoch": "strict-broker", "stage": "completed",
                     "sequence": 3, "may_have_side_effects": True,
                     "started_at_unix_ms": 100, "execution_deadline_unix_ms": 10100}
        evidence = {"schema_version": 2, "id": "success-id", "broker_epoch": "strict-broker",
                    "status": "succeeded", "reason": "verified", "before": before,
                    "after": after, "execution": execution}
        self.assertEqual(ActionEvidenceV2.model_validate(evidence).status, "succeeded")
        for bad_after in (
            {**after, "cash": after["cash"] + 1},
            {**after, "world_epoch": "other", "version": "other:8"},
            {**after, "scene": "OtherScene"},
            {**after, "menu_open": True, "can_close_menu": True,
             "legal_actions": [{"action": "close_menu", "arguments": {}}]},
        ):
            with self.subTest(after=bad_after):
                with self.assertRaises(ValidationError):
                    ActionEvidenceV2.model_validate({**evidence, "after": bad_after})

    def _call_status_sync(self, command_id):
        async def call():
            async with Client(self.parameters(), read_timeout_seconds=15) as client:
                result = await client.call_tool("get_action_status", {"command_id": command_id})
                self.assertFalse(result.is_error, result.content)
                return result.structured_content
        return asyncio.run(call())


if __name__ == "__main__":
    unittest.main()
