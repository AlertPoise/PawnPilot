"""SDK stdio -> real broker -> test-only C# driver -> production ActionGate.

All state is fixture data. This does not load Unity or install a game Mod.
Build the managed tests once before running the full Python suite.
"""
import asyncio
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import unittest

from mcp import Client
from mcp.client.stdio import StdioServerParameters
from bridge.server import Broker, Server

ROOT = Path(__file__).resolve().parents[1]


class ManagedEndToEndTests(unittest.TestCase):
    def test_sdk_broker_managed_cross_frame_and_ack(self):
        directory = ROOT / "artifacts/m2b/simulation"
        directory.mkdir(parents=True, exist_ok=True)
        config = directory / "session.json"
        config.write_text(json.dumps({"port": 18765, "token": "e" * 64}), encoding="utf8")
        dll = ROOT / "tests/PawnPilot.CoreTests/bin/Release/net10.0/PawnPilot.CoreTests.dll"
        self.assertTrue(dll.exists(), "Build managed Release tests before running E2E")
        broker = Broker("e" * 64, heartbeat_timeout=.8)
        observations = []
        original_exchange = broker.exchange

        def recorded_exchange(body):
            response = original_exchange(body)
            observations.append(json.loads(json.dumps(body)))
            return response

        broker.exchange = recorded_exchange
        server = Server(broker, port=0)
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": .02}, daemon=True)
        thread.start()
        process = subprocess.Popen(["dotnet", str(dll), "--simulate-mod", str(server.server_port)],
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf8")
        lines = queue.Queue()
        events = []

        def read_lines():
            for line in process.stdout:
                lines.put(line)
                events.append(line)

        reader = threading.Thread(target=read_lines, daemon=True)
        reader.start()
        try:
            try:
                ready = json.loads(lines.get(timeout=10))
            except queue.Empty:
                process.kill()
                process.wait(timeout=5)
                self.fail("Managed fixture did not start: " + process.stderr.read())
            self.assertEqual(ready["source"], "fixture")
            self.assertEqual(ready["kind"], "ready")
            until = time.monotonic() + 5
            while not broker.snapshot()["connected"] and time.monotonic() < until:
                time.sleep(.025)
            self.assertTrue(broker.snapshot()["connected"])
            bootstrap = ("import bridge.client as c; " + f"c.PORT={server.server_port}; "
                         "import runpy; runpy.run_module('pawnpilot_mcp',run_name='__main__')")
            env = os.environ.copy()
            env["PAWNPILOT_CONFIG"] = str(config)
            env["PYTHONPATH"] = str(ROOT)
            params = StdioServerParameters(command=sys.executable, args=["-c", bootstrap], cwd=ROOT, env=env)
            asyncio.run(self._exercise(params))
            until = time.monotonic() + 3
            while not any('"acknowledged"' in line for line in events) and time.monotonic() < until:
                time.sleep(.025)
            self.assertEqual(len(broker.records), 1)
            self.assertEqual(broker.result("managed-sdk-command")["command_status"], "succeeded")
            running = [o for o in observations if o.get("progress", {}) and o["progress"]["stage"] == "verifying"]
            self.assertGreaterEqual(len(running), 3, "Heartbeat must continue while managed verifier waits")
            self.assertTrue(any(o["state"]["interaction"]["consistency"] == "updating" for o in observations))
            self.assertEqual(broker.state["interaction"]["phase"], "needs_agent")
            self.assertTrue(broker.snapshot()["can_act"], "Confirmed effect must not hold lock for next Agent decision")
            process.stdin.write("stop\n")
            process.stdin.flush()
            process.wait(timeout=5)
            reader.join(timeout=2)
            stderr = process.stderr.read()
            self.assertEqual(process.returncode, 0, stderr)
            parsed = [json.loads(line) for line in events]
            self.assertEqual(sum(e["kind"] == "invoked" for e in parsed), 1)
            self.assertTrue(any(e["kind"] == "acknowledged" for e in parsed))
            (directory / "managed-events.jsonl").write_text("".join(events), encoding="utf8")
            (directory / "exchanges.json").write_text(json.dumps(observations, indent=2), encoding="utf8")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()
            reader.join(timeout=2)
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    async def _exercise(self, params):
        async with Client(params, read_timeout_seconds=15) as client:
            async def call(name, args=None):
                result = await client.call_tool(name, args or {})
                self.assertFalse(result.is_error, result.content)
                return result.structured_content
            tools = await client.list_tools()
            self.assertEqual({t.name for t in tools.tools},
                             {"health_check", "observe", "act", "get_action_status", "wait_for_update"})
            observed = await call("observe")
            snapshot = observed["snapshot"]
            state = snapshot["state"]
            acted = await call("act", {"schema_version": 2, "command_id": "managed-sdk-command",
                "action": "close_menu", "arguments": {}, "broker_epoch": snapshot["broker_epoch"],
                "expected_world_epoch": state["world_epoch"], "expected_revision": state["revision"],
                "expected_version": state["version"]})
            self.assertIn(acted["command_status"], {"pending", "running"})
            wait_tool = next(t for t in tools.tools if t.name == "wait_for_update")
            wait = {"criterion": "command_terminal", "command_id": "managed-sdk-command", "max_wait_ms": 5000}
            # Support a discriminated request model without weakening its strict inner fields.
            wait_args = {"request": wait} if "request" in wait_tool.input_schema.get("properties", {}) else wait
            waited = await call("wait_for_update", wait_args)
            self.assertEqual(waited["outcome"], "terminal")
            status = await call("get_action_status", {"command_id": "managed-sdk-command"})
            self.assertEqual(status["command_status"], "succeeded")
            self.assertEqual(status["result"]["before"]["cash"], status["result"]["after"]["cash"])
            self.assertFalse(status["result"]["after"]["menu_open"])


if __name__ == "__main__":
    unittest.main()
