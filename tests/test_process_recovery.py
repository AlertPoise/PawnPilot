"""Kill a real v2 broker process at durable boundaries; never uses Unity or user saves."""
import http.client
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
CHILD = """
import sys
from bridge.server import Broker, Server
server = Server(Broker('process-test-token', journal_path=sys.argv[1]), port=0)
print(server.server_port, flush=True)
server.serve_forever(poll_interval=.01)
"""


def state(revision=1, opened=True):
    return {"schema_version": 2, "world_epoch": "process-world", "revision": revision,
            "version": f"process-world:{revision}", "scene": "InventoryScene", "loaded": True,
            "ready": True, "phase": "MORNING", "menu_open": opened,
            "can_close_menu": opened, "not_ready_reason": None, "cash": 550,
            "interaction": {"phase": "menu" if opened else "store", "consistency": "unverified",
                            "reason": None, "operable": True},
            "business": {"support": "unsupported", "customer_ref": None, "item_refs": None,
                          "offer_ref": None, "collection_fingerprint": None},
            "legal_actions": ([{"action": "close_menu", "arguments": {}}] if opened else [])}


class ProcessCrashTests(unittest.TestCase):
    def setUp(self):
        directory = ROOT / ".test-tmp"
        directory.mkdir(parents=True, exist_ok=True)
        self.path = directory / ("m2b-process-" + uuid.uuid4().hex + ".json")
        self.proc = None
        self.start()

    def tearDown(self):
        self.kill()
        for path in (self.path, self.path.with_suffix(self.path.suffix + ".lock"),
                     self.path.with_suffix(self.path.suffix + ".v1.bak")):
            path.unlink(missing_ok=True)
        for path in self.path.parent.glob(self.path.name + ".*.tmp"):
            path.unlink(missing_ok=True)

    def start(self):
        self.proc = subprocess.Popen([sys.executable, "-u", "-c", CHILD, str(self.path)],
                                     cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        lines = queue.Queue()
        threading.Thread(target=lambda: lines.put(self.proc.stdout.readline()), daemon=True).start()
        self.port = int(lines.get(timeout=10))
        self.epoch = self.request("/state")[1]["broker_epoch"]

    def kill(self):
        if self.proc:
            self.proc.kill()
            self.proc.wait(timeout=10)
            self.proc.stdout.close()
            self.proc.stderr.close()
            self.proc = None

    def request(self, path, body=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=4)
        try:
            conn.request("GET" if body is None else "POST", path,
                         body=None if body is None else json.dumps(body),
                         headers={"Authorization": "Bearer process-test-token", "Content-Type": "application/json"})
            response = conn.getresponse()
            return response.status, json.load(response)
        finally:
            conn.close()

    def exchange(self, result=None, epoch=None):
        return self.request("/exchange", {"schema_version": 2, "broker_epoch": epoch or self.epoch,
            "session_id": "process-game-session", "state": state(),
            "has_result": result is not None, "result": result,
            "progress": result["execution"] if result is not None else None})

    def enqueue(self):
        body = {"schema_version": 2, "id": "process-command", "action": "close_menu",
                "broker_epoch": self.epoch, "expected_world_epoch": "process-world",
                "expected_revision": 1, "expected_version": "process-world:1", "arguments": {}}
        self.assertEqual(self.request("/commands", body)[0], 200)
        return body

    @staticmethod
    def success_result(command):
        started = command["start_deadline_unix_ms"] - 1
        execution = {"id": command["id"], "broker_epoch": command["broker_epoch"],
                     "stage": "completed", "sequence": 1, "may_have_side_effects": True,
                     "started_at_unix_ms": started,
                     "execution_deadline_unix_ms": started + command["execution_timeout_ms"]}
        return {"schema_version": 2, "id": command["id"], "broker_epoch": command["broker_epoch"],
                "status": "succeeded", "reason": "observed", "before": state(),
                "after": state(2, False), "execution": execution}

    def test_kill_before_delivery_rejects_without_replay(self):
        self.exchange()
        original = self.enqueue()
        self.kill()
        self.start()
        status = self.request("/results/process-command")[1]
        self.assertEqual(status["command_status"], "rejected")
        self.assertNotEqual(original["broker_epoch"], self.epoch)
        self.assertIsNone(self.exchange()[1]["command"])
        self.assertEqual(self.request("/commands", original)[1]["command_status"], "rejected")

    def test_kill_after_delivery_twice_recovers_unknown_then_acknowledged_result(self):
        self.exchange()
        original = self.enqueue()
        command = self.exchange()[1]["command"]
        self.assertEqual(command["id"], original["id"])
        for _ in range(2):
            self.kill()
            self.start()
            self.assertEqual(self.request("/results/process-command")[1]["command_status"], "outcome_unknown")
            self.assertIsNone(self.exchange()[1]["command"])
            self.assertFalse(self.request("/state")[1]["can_act"])
        result = self.success_result(command)
        reply = self.exchange(result, epoch=original["broker_epoch"])[1]
        self.assertEqual(reply["acknowledged_result_id"], command["id"])
        self.kill()
        self.start()
        recovered = self.request("/results/process-command")[1]
        self.assertEqual(recovered["command_status"], "succeeded")
        self.assertEqual(recovered["record_schema_version"], 2)
        self.assertEqual(self.request("/commands", original)[1]["command_status"], "succeeded")


if __name__ == "__main__":
    unittest.main(verbosity=2)
