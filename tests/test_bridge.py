"""真实 loopback HTTP 测试；游戏状态为测试夹具，不证明游戏已连接。"""
import concurrent.futures
import http.client
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bridge.server import Broker, Server


class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.now = 100.0
        self.journal_path = Path(__file__).resolve().parents[1] / f".broker-test-{uuid.uuid4().hex}.json"
        self.broker = Broker("test-token", clock=lambda: self.now, journal_path=self.journal_path)
        self.broker_epoch = None
        self.server = Server(self.broker, port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": .01})
        self.thread.start()
        self.state = {"schema_version": 1, "world_epoch": "world-1", "revision": 1,
                      "version": "world-1:1", "scene": "InventoryScene", "loaded": True,
                      "ready": True, "can_close_menu": True, "not_ready_reason": None,
                      "cash": 550, "phase": "morning", "menu_open": True}

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.assertFalse(self.thread.is_alive())
        self.journal_path.unlink(missing_ok=True)
        self.journal_path.with_suffix(self.journal_path.suffix + ".lock").unlink(missing_ok=True)

    def req(self, path, body=None, headers=None):
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=2)
        options = {"Authorization": "Bearer test-token", "Content-Type": "application/json"}
        options.update(headers or {})
        connection.request("GET" if body is None else "POST", path,
                           body=None if body is None else json.dumps(body).encode(), headers=options)
        response = connection.getresponse()
        answer = response.status, json.load(response)
        connection.close()
        return answer

    def exchange(self, result=None, session="session-a", state=None):
        answer = self.req("/exchange", {"schema_version": 1, "broker_epoch": self.broker_epoch,
                                         "session_id": session, "state": state or self.state,
                                         "has_result": result is not None, "result": result})
        if answer[0] == 200:
            self.broker_epoch = answer[1]["broker_epoch"]
        return answer

    def queue(self, action="close_menu", version="world-1:1", command_id=None):
        return self.req("/commands", {"schema_version": 1, "id": command_id or uuid.uuid4().hex,
                                       "broker_epoch": self.broker_epoch, "action": action,
                                       "expected_world_epoch": self.state["world_epoch"],
                                       "expected_revision": self.state["revision"],
                                       "expected_version": version})

    def result(self, command, status="success", reason="verified"):
        return {"schema_version": 1, "id": command["id"], "broker_epoch": command["broker_epoch"],
                "status": status, "reason": reason,
                "before": dict(self.state),
                "after": dict(self.state, revision=command["expected_revision"] + 1,
                              version=f"{command['expected_world_epoch']}:{command['expected_revision'] + 1}",
                              menu_open=False, can_close_menu=False)}

    def test_request_response_result_roundtrip(self):
        self.assertEqual(self.exchange()[0], 200)
        code, queued = self.queue()
        self.assertEqual(code, 200)
        code, delivery = self.exchange()
        self.assertTrue(delivery["has_command"])
        self.assertEqual(delivery["command"]["id"], queued["id"])
        self.assertEqual(delivery["command"]["action"], "close_menu")
        self.assertEqual(self.exchange()[0], 200)
        result = self.result(delivery["command"])
        self.assertEqual(self.exchange(result)[0], 200)
        status = self.req("/results/" + queued["id"])
        self.assertEqual(status[1]["result"], result)

    def test_authentication_origin_and_host_rejected(self):
        for headers, expected in [({"Authorization": "Bearer wrong"}, 401),
                                  ({"Origin": "http://attacker.invalid"}, 403),
                                  ({"Host": "attacker.invalid"}, 403)]:
            with self.subTest(headers=headers):
                self.assertEqual(self.req("/state", headers=headers)[0], expected)

    def test_actions_allowlisted(self):
        self.exchange()
        for action in ["open_network", "close_network", "open_menu", "eval", "set_cash", "", [], {}]:
            with self.subTest(action=action):
                self.assertEqual(self.queue(action)[0], 400)

    def test_disconnected_and_unloaded_safe(self):
        self.assertEqual(self.queue()[0], 400)  # A command cannot be formed before epoch handshake.
        self.assertEqual(self.req("/state")[1]["connected"], False)
        self.state.update(loaded=False, ready=False, can_close_menu=False,
                          not_ready_reason="not_gameplay_scene", phase="not_loaded")
        self.exchange()
        self.assertEqual(self.req("/state")[1]["state"]["loaded"], False)

    def test_concurrent_commands_only_one_accepted(self):
        self.exchange()
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            statuses = list(pool.map(lambda _: self.queue()[0], range(2)))
        self.assertEqual(sorted(statuses), [200, 409])

    def test_stale_state_before_queue_and_delivery(self):
        self.exchange()
        self.assertEqual(self.queue(version="old")[0], 409)
        command = self.queue()[1]
        self.assertEqual(self.exchange(state=dict(self.state, revision=2, version="world-1:2"))[1]["command"], None)
        self.assertEqual(self.req("/results/" + command["id"])[1]["result"]["reason"], "stale_state")

    def test_expired_before_delivery_never_executed(self):
        self.exchange()
        command = self.queue()[1]
        self.now += 6
        self.assertIsNone(self.exchange()[1]["command"])
        self.assertEqual(self.req("/results/" + command["id"])[1]["status"], "rejected")

    def test_delivery_timeout_is_uncertain_and_gate_remains_locked(self):
        self.exchange()
        command = self.queue()[1]
        self.exchange()
        self.now += 6
        self.exchange()
        self.assertEqual(self.req("/results/" + command["id"])[1]["status"], "uncertain")
        self.assertEqual(self.queue()[0], 409)
        result = self.result(self.broker.records[command["id"]]["command"], reason="late_confirmation")
        self.assertEqual(self.exchange(result)[0], 200)
        self.assertEqual(self.queue()[0], 200)

    def test_duplicate_result_idempotent_conflicting_rejected(self):
        self.exchange()
        command = self.queue()[1]
        self.exchange()
        result = self.result(self.broker.records[command["id"]]["command"])
        self.assertEqual(self.exchange(result)[0], 200)
        self.assertEqual(self.exchange(result)[0], 200)
        self.assertEqual(self.exchange(dict(result, status="error"))[0], 409)

    def test_rejected_result_with_null_snapshots_is_acknowledged(self):
        self.exchange()
        queued = self.queue()[1]
        command = self.exchange()[1]["command"]
        result = {"schema_version": 1, "id": queued["id"],
                  "broker_epoch": command["broker_epoch"], "status": "rejected",
                  "reason": "precondition", "before": None, "after": None}
        self.assertEqual(self.exchange(result)[0], 200)
        self.assertEqual(self.req("/results/" + queued["id"])[1]["command_status"], "rejected")

    def test_unknown_result_rejected(self):
        self.exchange()
        unknown = self.result({"id": "unknown", "broker_epoch": self.broker_epoch,
                               "expected_world_epoch": "world-1", "expected_revision": 1,
                               "expected_version": "world-1:1"}, reason="bad")
        self.assertEqual(self.exchange(unknown)[0], 409)

    def test_explicit_result_flag_ignores_unity_default_object(self):
        body = {"session_id": "session-a", "state": self.state, "has_result": False,
                "result": {"id": "", "status": "", "reason": "", "before": {}, "after": {}}}
        handshake = self.req("/exchange", dict(body, schema_version=1, broker_epoch=self.broker_epoch))
        self.assertEqual(handshake[0], 200)
        self.broker_epoch = handshake[1]["broker_epoch"]
        command = self.queue()[1]
        delivery = self.req("/exchange", dict(body, schema_version=1, broker_epoch=self.broker_epoch))[1]
        self.assertTrue(delivery["has_command"])
        self.assertEqual(delivery["command"]["id"], command["id"])
        self.assertEqual(self.req("/exchange", dict(body, schema_version=1, broker_epoch=self.broker_epoch))[0], 200)
        self.assertIsNotNone(self.broker.pending)

    def test_result_flag_must_be_explicit_boolean(self):
        body = {"schema_version": 1, "broker_epoch": self.broker_epoch,
                "session_id": "session-a", "state": self.state, "result": None}
        self.assertEqual(self.req("/exchange", body), (400, {"error": "has_result_boolean_required"}))
        for value in [None, 0, 1, "false", [], {}]:
            with self.subTest(flag=value):
                self.assertEqual(self.req("/exchange", dict(body, has_result=value))[0], 400)
        self.assertEqual(self.req("/exchange", dict(body, has_result=True)), (400, {"error": "invalid_result"}))

    def test_broker_restart_reconciles_same_session_result_without_replay(self):
        self.exchange()
        command = self.queue()[1]
        self.assertEqual(self.exchange()[1]["command"]["id"], command["id"])
        old_result = self.result(self.broker.records[command["id"]]["command"], reason="executed_before_restart")
        # 模拟动作结果未送达 broker 时重启外部进程；结果需由原游戏会话核对。
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.assertFalse(self.thread.is_alive())
        self.assertEqual(self.broker.result(command["id"])["status"], "uncertain")
        self.broker = Broker("test-token", clock=lambda: self.now, journal_path=self.journal_path)
        self.server = Server(self.broker, port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": .01})
        self.thread.start()
        for _ in range(2):
            self.assertEqual(self.exchange(old_result)[0], 200)
            self.assertIsNone(self.broker.pending)
            self.assertFalse(self.broker.delivered)
        self.assertEqual(self.queue()[0], 200)
        self.assertTrue(self.req("/state")[1]["connected"])
        # 人工重启 Mod 创建新会话后，只恢复状态，不重排旧动作。
        self.assertEqual(self.exchange(session="session-after-mod-restart")[0], 200)
        self.assertEqual(self.req("/results/" + command["id"])[0], 200)

    def test_session_change_marks_delivered_uncertain(self):
        self.exchange()
        command = self.queue()[1]
        self.exchange()
        self.assertEqual(self.exchange(session="session-b")[0], 200)
        self.assertEqual(self.req("/results/" + command["id"])[1]["command_status"], "outcome_unknown")
        self.assertEqual(self.exchange(self.result(self.broker.records[command["id"]]["command"], reason="late"), session="session-a")[0], 200)

    def test_heartbeat_disconnect(self):
        self.exchange()
        self.now += 4
        self.assertFalse(self.req("/state")[1]["connected"])
        self.assertEqual(self.queue()[0], 503)

    def test_body_limit_and_invalid_snapshot(self):
        # 先发送超限长度头，验证服务端无需接收大 body 即拒绝。
        # Windows 若客户端仍灌入 body，提前关闭可能表现为 TCP reset 而非可读 413。
        connection = http.client.HTTPConnection("127.0.0.1", self.server.server_port, timeout=2)
        connection.putrequest("POST", "/exchange")
        connection.putheader("Authorization", "Bearer test-token")
        connection.putheader("Content-Type", "application/json")
        connection.putheader("Content-Length", "65537")
        connection.endheaders()
        response = connection.getresponse()
        self.assertEqual(response.status, 413)
        response.read()
        connection.close()
        self.assertIsNone(self.broker.state)
        self.assertEqual(self.exchange(state={"version": "v1"})[0], 400)

    def test_shutdown_marks_delivered_uncertain(self):
        self.exchange()
        command = self.queue()[1]
        self.exchange()
        self.broker.close()
        self.assertEqual(self.broker.result(command["id"])["result"]["reason"], "server_shutdown")
        self.assertEqual(self.exchange()[0], 503)

    def test_bound_only_to_loopback(self):
        self.assertEqual(self.server.server_address[0], "127.0.0.1")


class ProcessIntegrationTests(unittest.TestCase):
    def test_independent_process_exchange_and_normal_shutdown(self):
        # 外部 Python 进程运行真实 server；此处仍是游戏协议夹具，而非 Unity。
        script = (
            "from bridge.server import Broker,Server; import threading,sys; "
            "s=Server(Broker('process-test-token'),port=0); "
            "t=threading.Thread(target=s.serve_forever,kwargs={'poll_interval':.01}); "
            "t.start(); print(s.server_port,flush=True); sys.stdin.readline(); "
            "s.shutdown(); s.server_close(); t.join(); print('CLOSED',flush=True)"
        )
        process = subprocess.Popen([sys.executable, "-c", script],
                                   cwd=Path(__file__).resolve().parents[1],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, text=True)
        try:
            startup = queue.Queue()
            reader = threading.Thread(target=lambda: startup.put(process.stdout.readline()), daemon=True)
            reader.start()
            line = startup.get(timeout=5)
            port = int(line.strip())

            def request(path, body):
                connection = http.client.HTTPConnection("127.0.0.1", port, timeout=2)
                connection.request("POST", path, json.dumps(body),
                                   {"Authorization": "Bearer process-test-token", "Content-Type": "application/json"})
                response = connection.getresponse()
                self.assertEqual(response.status, 200)
                payload = json.load(response)
                connection.close()
                return payload

            fixture = {"schema_version": 1, "broker_epoch": None,
                       "session_id": "independent-process-fixture", "has_result": False, "state": {
                "schema_version": 1, "world_epoch": "fixture-world", "revision": 1,
                "version": "fixture-world:1", "scene": "InventoryScene", "loaded": True,
                "ready": True, "can_close_menu": True, "not_ready_reason": None,
                "cash": 12, "phase": "fixture", "menu_open": True}}
            handshake = request("/exchange", fixture)
            fixture["broker_epoch"] = handshake["broker_epoch"]
            self.assertIsNone(handshake["command"])
            command_id = uuid.uuid4().hex
            command = request("/commands", {"schema_version": 1, "id": command_id,
                                             "broker_epoch": fixture["broker_epoch"], "action": "observe",
                                             "expected_world_epoch": "fixture-world",
                                             "expected_revision": 1, "expected_version": "fixture-world:1"})
            self.assertEqual(request("/exchange", fixture)["command"]["id"], command["id"])
            fixture["result"] = {"schema_version": 1, "id": command["id"],
                                  "broker_epoch": fixture["broker_epoch"],
                                  "status": "success", "reason": "fixture_received",
                                  "before": fixture["state"], "after": fixture["state"]}
            fixture["has_result"] = True
            self.assertIsNone(request("/exchange", fixture)["command"])
            stdout, stderr = process.communicate("\n", timeout=5)
            self.assertEqual(process.returncode, 0, stderr)
            self.assertEqual(stdout.strip(), "CLOSED")
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate(timeout=5)


if __name__ == "__main__":
    unittest.main(verbosity=2)
