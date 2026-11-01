"""SDK stdio -> real broker -> managed v3 simulation and retained v3 evidence."""
import asyncio
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
import unittest

from mcp import Client
from mcp.client.stdio import StdioServerParameters
from bridge.server import Broker, Server

ROOT = Path(__file__).resolve().parents[1]


class ManagedV3EndToEndTests(unittest.TestCase):
    def test_v3_business_snapshot_and_close_menu_round_trip(self):
        dll = ROOT / "tests/PawnPilot.CoreTests/bin/Release/net10.0/PawnPilot.CoreTests.dll"
        self.assertTrue(dll.exists(), "Build managed Release tests before running E2E")
        broker = Broker("e" * 64, heartbeat_timeout=.8)
        observations = []
        original_exchange = broker.exchange

        def recorded_exchange(body):
            reply = original_exchange(body)
            observations.append(json.loads(json.dumps(body)))
            return reply

        broker.exchange = recorded_exchange
        server = Server(broker, port=0)
        temp_root = ROOT / ".test-tmp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="managed-v3-", dir=temp_root) as directory:
            config = Path(directory) / "session.json"
            # The production config parser accepts only the documented port. The test's
            # broker is bound to an OS-assigned ephemeral port and the child client is
            # redirected to it below, so this value does not bind or contact 18765.
            config.write_text(json.dumps({"port": 18765, "token": "e" * 64}), encoding="utf8")
            self._run_managed_v3(config, broker, server, observations, dll)

    def _run_managed_v3(self, config, broker, server, observations, dll):
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": .02}, daemon=True)
        thread.start()
        process = subprocess.Popen(["dotnet", str(dll), "--simulate-mod-v3", str(server.server_port)],
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
                process.kill(); process.wait(timeout=5)
                self.fail("Managed v3 fixture did not start: " + process.stderr.read())
            self.assertEqual(ready["kind"], "ready")
            until = time.monotonic() + 5
            while not broker.snapshot()["connected"] and time.monotonic() < until:
                time.sleep(.025)
            initial = broker.snapshot()
            self.assertTrue(initial["connected"])
            self.assertEqual(initial["schema_version"], 3)
            self.assertEqual(initial["state"]["schema_version"], 3)
            self.assertEqual(initial["state"]["business"]["details"]["schema_version"], 2)
            self.assertEqual([a["action"] for a in initial["state"]["legal_actions"]], ["close_menu"])

            bootstrap = ("import bridge.client as c; " + f"c.PORT={server.server_port}; "
                         "import runpy; runpy.run_module('pawnpilot_mcp',run_name='__main__')")
            env = os.environ.copy()
            env["PAWNPILOT_CONFIG"] = str(config)
            env["PYTHONPATH"] = str(ROOT)
            params = StdioServerParameters(command=sys.executable, args=["-c", bootstrap], cwd=ROOT, env=env)
            asyncio.run(self._exercise(params, initial))
            until = time.monotonic() + 3
            while not any('"acknowledged"' in line for line in events) and time.monotonic() < until:
                time.sleep(.025)
            self.assertEqual(len(broker.records), 1)
            status = broker.result("managed-v3-close")
            self.assertEqual(status["record_schema_version"], 3)
            self.assertEqual(status["result"]["schema_version"], 3)
            self.assertTrue(any(o["state"]["business"]["details"] for o in observations))
            self.assertTrue(all(o["schema_version"] == 3 and o["state"]["schema_version"] == 3
                                for o in observations))
            process.stdin.write("stop\n"); process.stdin.flush()
            process.wait(timeout=5); reader.join(timeout=2)
            self.assertEqual(process.returncode, 0, process.stderr.read())
            parsed = [json.loads(line) for line in events]
            self.assertEqual(sum(e["kind"] == "invoked" for e in parsed), 1)
            self.assertTrue(any(e["kind"] == "acknowledged" for e in parsed))
        finally:
            if process.poll() is None:
                process.kill(); process.wait(timeout=5)
            for stream in (process.stdin, process.stdout, process.stderr):
                stream.close()
            reader.join(timeout=2)
            server.shutdown(); server.server_close(); thread.join(timeout=2)
            broker.close()

    async def _exercise(self, params, initial):
        async with Client(params, read_timeout_seconds=15) as client:
            tools = await client.list_tools()
            observed = await client.call_tool("observe", {})
            self.assertFalse(observed.is_error, observed.content)
            snapshot = observed.structured_content["snapshot"]
            self.assertEqual(snapshot["schema_version"], 3)
            state = snapshot["state"]
            self.assertEqual(state["business"]["details"]["schema_version"], 2)
            acted = await client.call_tool("act", {"schema_version": 3, "command_id": "managed-v3-close",
                "action": "close_menu", "arguments": {}, "broker_epoch": snapshot["broker_epoch"],
                "expected_world_epoch": state["world_epoch"], "expected_revision": state["revision"],
                "expected_version": state["version"]})
            self.assertFalse(acted.is_error, acted.content)
            self.assertIn(acted.structured_content["command_status"], {"pending", "running"})
            wait_request = {"criterion": "command_terminal", "command_id": "managed-v3-close", "max_wait_ms": 5000}
            wait_tool = next(tool for tool in tools.tools if tool.name == "wait_for_update")
            wait_args = {"request": wait_request} if "request" in wait_tool.input_schema.get("properties", {}) else wait_request
            waited = await client.call_tool("wait_for_update", wait_args)
            self.assertFalse(waited.is_error, waited.content)
            self.assertEqual(waited.structured_content["outcome"], "terminal")
            status = await client.call_tool("get_action_status", {"command_id": "managed-v3-close"})
            self.assertFalse(status.is_error, status.content)
            self.assertEqual(status.structured_content["record_schema_version"], 3)
            self.assertEqual(status.structured_content["result"]["before"]["business"]["details"]["schema_version"], 2)


if __name__ == "__main__": unittest.main()
