"""Managed-run dispatch token through MCP stdio, HTTP header, and the Broker."""
from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path
import shutil
import sys
import threading
import unittest
import uuid

from mcp import Client
from mcp.client.stdio import StdioServerParameters

from bridge.run_store import RunStore
from bridge.server import Broker, Server
from tests.test_protocol_v4 import command_v4, snapshot_v4


ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT / "artifacts" / "m5c"
LIMIT_KEYS = {"customers", "writes", "trades", "buy_spend", "bargains", "day_fees", "days"}
BROKER_TOKEN = "a" * 64


class McpRunDispatchTests(unittest.TestCase):
    def test_stdio_env_token_is_forwarded_as_private_http_header_once(self):
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
        session_id = "stdio-managed-session"
        owner = "stdio-supervisor-owner"
        root = TEST_ROOT / ("test-run-mcp-" + uuid.uuid4().hex)
        root.mkdir(parents=True, exist_ok=False)
        broker = None
        try:
            journal_path = root / "broker-journal.json"
            run_path = root / "run-context.json"
            config_path = root / "session.json"
            RunStore(run_path).create(
                "stdio-managed-run", "stdio-fixture-save", str(journal_path), state["world_epoch"],
                session_id, 1, 2, {key: 10 for key in LIMIT_KEYS}, ["choose_dialogue_option"],
                "Complete the authorized day.")

            broker = Broker(BROKER_TOKEN, heartbeat_timeout=60, journal_path=journal_path,
                            run_path=run_path)
            broker.exchange({"schema_version": 4, "session_id": session_id,
                             "broker_epoch": broker.broker_epoch, "state": state,
                             "has_result": False, "result": None, "progress": None})
            broker.snapshot()  # Bind and sync the managed run from real Broker state.
            store = RunStore(run_path)
            context = store.snapshot()
            store.control("resume", context["revision"])
            broker.snapshot()
            dispatch_token = store.grant(owner, ttl_seconds=300)

            config_path.write_text(json.dumps({"port": 18765, "token": BROKER_TOKEN}), encoding="utf-8")
            http_dispatch_values = []
            original_enqueue = broker.enqueue

            def record_dispatch(body, dispatch_token=None):
                http_dispatch_values.append(dispatch_token)
                return original_enqueue(body, dispatch_token=dispatch_token)

            broker.enqueue = record_dispatch
            server = Server(broker, port=0)
            thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": .02}, daemon=True)
            thread.start()
            parameters = self._stdio_parameters(server.server_port, config_path, dispatch_token)
            try:
                result = asyncio.run(self._run_one_dispatch_and_replay(parameters, broker))
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

            self.assertEqual(result["listed_tools"], {
                "health_check", "observe", "act", "get_action_status", "wait_for_update"})
            self.assertEqual(result["first_status"], "pending")
            self.assertEqual(result["second_status"], "rejected")
            self.assertIn("dispatch", result["second_reason"])
            self.assertEqual(http_dispatch_values, [dispatch_token, dispatch_token])
            self.assertEqual(set(broker.records), {"stdio-managed-command-1"})
            self.assertEqual(store.snapshot()["budget"]["writes"], 1)
        finally:
            if broker is not None:
                broker.close()
            shutil.rmtree(root, ignore_errors=True)

    @staticmethod
    def _stdio_parameters(port: int, config_path: Path, dispatch_token: str):
        bootstrap = (
            "import bridge.client as c; "
            f"c.PORT={port}; "
            "import runpy; runpy.run_module('pawnpilot_mcp', run_name='__main__')"
        )
        env = os.environ.copy()
        env["PAWNPILOT_CONFIG"] = str(config_path)
        env["PAWNPILOT_DISPATCH_TOKEN"] = dispatch_token
        env["PYTHONPATH"] = str(ROOT) + os.pathsep + env.get("PYTHONPATH", "")
        return StdioServerParameters(command=sys.executable, args=["-c", bootstrap], cwd=ROOT, env=env)

    @staticmethod
    async def _run_one_dispatch_and_replay(parameters, broker):
        async with Client(parameters, read_timeout_seconds=15) as client:
            tools = await client.list_tools()
            observed = await client.call_tool("observe", {})
            if observed.is_error:
                raise AssertionError(observed.content)
            snap = observed.structured_content["snapshot"]
            state = snap["state"]
            request_base = command_v4(state, command_id="stdio-managed-command-1")
            first = await client.call_tool("act", {
                "schema_version": 4,
                "command_id": request_base["id"],
                "action": request_base["action"],
                "arguments": request_base["arguments"],
                "broker_epoch": snap["broker_epoch"],
                "expected_world_epoch": state["world_epoch"],
                "expected_revision": state["revision"],
                "expected_version": state["version"],
            })
            if first.is_error:
                raise AssertionError(first.content)

            request_second = command_v4(state, command_id="stdio-managed-command-2")
            second = await client.call_tool("act", {
                "schema_version": 4,
                "command_id": request_second["id"],
                "action": request_second["action"],
                "arguments": request_second["arguments"],
                "broker_epoch": snap["broker_epoch"],
                "expected_world_epoch": state["world_epoch"],
                "expected_revision": state["revision"],
                "expected_version": state["version"],
            })
            if second.is_error:
                raise AssertionError(second.content)
            return {
                "listed_tools": {tool.name for tool in tools.tools},
                "first_status": first.structured_content["command_status"],
                "second_status": second.structured_content["command_status"],
                "second_reason": second.structured_content.get("reason") or "",
            }


if __name__ == "__main__":
    unittest.main()
