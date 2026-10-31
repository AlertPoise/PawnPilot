"""PawnPilot M0：仅绑定 loopback 的标准库通信探针，不是 MCP 服务。"""
from __future__ import annotations

import argparse
import hashlib
import hmac
import json
from pathlib import Path
import secrets
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

try:
    from .journal import Journal, JournalError
except ImportError:  # Support the existing `python bridge/server.py` launch path.
    from journal import Journal, JournalError

PORT = 18765
MAX_BODY = 65536
ACTIONS = frozenset({"observe", "close_menu"})
RESULT_STATUS = {"success": "succeeded", "succeeded": "succeeded",
                 "rejected": "rejected", "uncertain": "outcome_unknown",
                 "outcome_unknown": "outcome_unknown", "error": "failed", "failed": "failed"}
LEGACY_STATUS = {"pending": "queued", "running": "delivered", "succeeded": "success",
                 "rejected": "rejected", "failed": "error", "outcome_unknown": "uncertain"}


class ProtocolError(Exception):
    def __init__(self, status: int, reason: str):
        self.status, self.reason = status, reason


class Broker:
    """Durable single-writer broker; a delivered action is never replayed."""
    def __init__(self, token: str, clock=time.time, ttl=5.0, heartbeat_timeout=3.0,
                 journal_path=None, max_records=4096):
        self.token = token
        self.clock = clock
        self.ttl = ttl
        self.heartbeat_timeout = heartbeat_timeout
        self.lock = threading.Lock()
        self.journal = Journal(journal_path) if journal_path is not None else None
        self.max_records = max_records
        self.poisoned = False
        self.session_id = None
        self.state = None
        self.last_seen = 0.0
        self.closed = False
        self.records = {}
        self.broker_epoch = str(uuid.uuid4())
        try:
            if self.journal:
                self.journal.acquire()
                previous = self.journal.load()
                if previous:
                    self.records = {r["command_id"]: r for r in previous["records"]}
                    for record in self.records.values():
                        if record["command_status"] == "pending":
                            self._set_terminal(record, "rejected", "broker_restart_before_delivery")
                        elif record["command_status"] == "running":
                            self._set_terminal(record, "outcome_unknown", "broker_restart_during_execution")
                self._persist()
        except Exception:
            if self.journal:
                self.journal.release()
            raise

    @property
    def pending(self):
        record = self._active()
        return record["command"] if record else None

    @property
    def delivered(self):
        record = self._active()
        return record is not None and record["command_status"] in {"running", "outcome_unknown"}

    def _active(self):
        return next((r for r in self.records.values()
                     if r["command_status"] in {"pending", "running"}
                     or (r["command_status"] == "outcome_unknown" and not r.get("resolution"))), None)

    def _persist(self):
        if not self.journal:
            return
        value = {"schema_version": 1, "broker_epoch": self.broker_epoch,
                 "records": list(self.records.values())}
        try:
            self.journal.save(value)
        except JournalError:
            self.poisoned = True
            raise ProtocolError(503, "broker_persistence_failed")

    def _set_terminal(self, record, status, reason, result=None):
        record["command_status"] = status
        record["result"] = result or {"schema_version": 1, "id": record["command_id"],
                                      "broker_epoch": record["command"]["broker_epoch"],
                                      "status": LEGACY_STATUS[status],
                                      "reason": reason, "before": None, "after": None}
        record["updated_at_unix_ms"] = max(record["created_at_unix_ms"], int(self.clock() * 1000))

    def _terminal(self, record, status, reason, result=None):
        self._set_terminal(record, status, reason, result)
        self._persist()

    def _connected(self):
        return self.session_id is not None and self.clock() - self.last_seen <= self.heartbeat_timeout

    def _expire(self):
        record = self._active()
        if not record:
            return
        if record["command_status"] == "pending" and self.clock() * 1000 > record["command"]["expires_at_unix_ms"]:
            self._terminal(record, "rejected", "expired_before_delivery")
        elif (record["command_status"] == "running"
              and (self.clock() * 1000 > record["command"]["expires_at_unix_ms"] or not self._connected())):
            self._terminal(record, "outcome_unknown", "delivery_outcome_unknown")

    @staticmethod
    def _valid_state(state):
        if not isinstance(state, dict):
            return False
        if set(state) != {"schema_version", "version", "world_epoch", "phase", "scene",
                          "not_ready_reason", "revision", "loaded", "ready", "menu_open",
                          "can_close_menu", "cash"}:
            return False
        epoch, revision = state.get("world_epoch"), state.get("revision")
        return (type(state.get("schema_version")) is int and state["schema_version"] == 1
                and isinstance(epoch, str) and 0 < len(epoch) <= 128
                and type(revision) is int and revision >= 0
                and isinstance(state.get("version"), str) and state["version"] == f"{epoch}:{revision}"
                and isinstance(state.get("scene"), str) and len(state["scene"]) <= 128
                and type(state.get("ready")) is bool and type(state.get("can_close_menu")) is bool
                and (state.get("not_ready_reason") is None or
                     (isinstance(state.get("not_ready_reason"), str) and len(state["not_ready_reason"]) <= 256))
                and type(state.get("loaded")) is bool
                and isinstance(state.get("phase"), str) and len(state["phase"]) <= 128
                and type(state.get("menu_open")) is bool
                and (state["ready"] or isinstance(state.get("not_ready_reason"), str))
                and type(state.get("cash")) is int
                and (not state["ready"] or (state["loaded"] and state.get("not_ready_reason") is None))
                and (not state["can_close_menu"] or (state["ready"] and state["menu_open"])))

    def exchange(self, body):
        sid = body.get("session_id")
        if type(body.get("schema_version")) is not int or body.get("schema_version") != 1:
            raise ProtocolError(400, "schema_version_unsupported")
        seen_epoch = body.get("broker_epoch")
        if seen_epoch is not None and (not isinstance(seen_epoch, str) or len(seen_epoch) > 64):
            raise ProtocolError(400, "invalid_broker_epoch")
        # Unity JsonUtility 的内联对象可能把 null 序列化成默认对象，必须用显式标志。
        if type(body.get("has_result")) is not bool:
            raise ProtocolError(400, "has_result_boolean_required")
        state = body.get("state")
        result = body.get("result") if body["has_result"] else None
        if not isinstance(sid, str) or not 1 <= len(sid) <= 128 or not self._valid_state(state):
            raise ProtocolError(400, "invalid_snapshot")
        if body["has_result"] and (not isinstance(result, dict)
                                  or type(result.get("schema_version")) is not int
                                  or result.get("schema_version") != 1
                                  or not isinstance(result.get("id"), str)
                                  or not isinstance(result.get("status"), str)
                                  or result.get("status") not in RESULT_STATUS
                                  or not isinstance(result.get("reason"), str) or len(result["reason"]) > 512
                                  or not isinstance(result.get("broker_epoch"), str)
                                  or (result.get("before") is not None
                                      and not self._valid_state(result.get("before")))
                                  or (result.get("after") is not None
                                      and not self._valid_state(result.get("after")))
                                  or (RESULT_STATUS.get(result.get("status")) == "succeeded"
                                      and (not self._valid_state(result.get("before"))
                                           or not self._valid_state(result.get("after"))))):
            raise ProtocolError(400, "invalid_result")
        with self.lock:
            if self.closed or self.poisoned:
                raise ProtocolError(503, "server_closed")
            self._expire()
            if self.session_id is not None and self.session_id != sid:
                active = self._active()
                if active and active["command_status"] == "pending":
                    self._terminal(active, "rejected", "session_changed_before_delivery")
                elif active and active["command_status"] == "running":
                    self._terminal(active, "outcome_unknown", "session_changed_after_delivery")
            acknowledged_id = None
            if result is not None:
                record = self.records.get(result["id"])
                if (record is None or record.get("delivery_session_id") != sid
                        or record["command"]["broker_epoch"] != result["broker_epoch"]):
                    raise ProtocolError(409, "unknown_or_conflicting_result")
                acknowledged_id = result["id"]
                self._validate_success_result(record, result)
                if record["result"] == result:
                    pass
                elif record["command_status"] in {"running", "outcome_unknown"}:
                    status = RESULT_STATUS[result["status"]]
                    self._terminal(record, status, result["reason"], result)
                else:
                    raise ProtocolError(409, "unknown_or_conflicting_result")
            is_prior_session_result = (self.session_id is not None and self.session_id != sid
                                       and result is not None and acknowledged_id is not None)
            if not is_prior_session_result:
                self.session_id, self.state, self.last_seen = sid, state, self.clock()
            command = None
            active = self._active()
            if seen_epoch == self.broker_epoch and active and active["command_status"] == "pending":
                expected = active["command"]
                if (expected["expected_world_epoch"] != state["world_epoch"]
                        or expected["expected_revision"] != state["revision"]
                        or expected["expected_version"] != state["version"]):
                    self._terminal(active, "rejected", "stale_state")
                else:
                    active["command_status"] = "running"
                    active["delivery_session_id"] = sid
                    active["updated_at_unix_ms"] = int(self.clock() * 1000)
                    self._persist()
                    command = dict(expected)
            return {"schema_version": 1, "broker_epoch": self.broker_epoch,
                    "acknowledged_result_id": acknowledged_id,
                    "has_command": command is not None, "command": command}

    @classmethod
    def _validate_success_result(cls, record, result):
        if RESULT_STATUS[result["status"]] != "succeeded":
            return
        before, after, command = result["before"], result["after"], record["command"]
        if (not cls._valid_state(after)
                or before["world_epoch"] != command["expected_world_epoch"]
                or before["revision"] != command["expected_revision"]
                or before["version"] != command["expected_version"]):
            raise ProtocolError(409, "invalid_success_result")
        if command["action"] == "close_menu":
            if (not before["ready"] or not before["can_close_menu"] or not after["ready"]
                    or after["world_epoch"] != before["world_epoch"]
                    or after["scene"] != before["scene"] or after["menu_open"]
                    or after["revision"] <= before["revision"] or after["version"] == before["version"]):
                raise ProtocolError(409, "invalid_success_result")
        elif (after["world_epoch"] != before["world_epoch"]
              or after["revision"] != before["revision"] or after["version"] != before["version"]):
            raise ProtocolError(409, "invalid_success_result")

    def enqueue(self, body):
        required = (body.get("schema_version"), body.get("id"), body.get("broker_epoch"),
                    body.get("expected_world_epoch"), body.get("expected_revision"),
                    body.get("expected_version"), body.get("action"))
        if (type(required[0]) is not int or required[0] != 1
                or not isinstance(required[1], str) or not 1 <= len(required[1]) <= 128
                or not isinstance(required[2], str) or not isinstance(required[3], str)
                or type(required[4]) is not int or required[4] < 0
                or not isinstance(required[5], str) or not isinstance(required[6], str)
                or body["action"] not in ACTIONS):
            raise ProtocolError(400, "invalid_command")
        request = {"schema_version": 1, "id": body["id"], "broker_epoch": body["broker_epoch"],
                   "action": body["action"], "expected_world_epoch": body["expected_world_epoch"],
                   "expected_revision": body["expected_revision"], "expected_version": body["expected_version"]}
        fingerprint = hashlib.sha256(json.dumps(request, sort_keys=True, separators=(",", ":"),
                                               ensure_ascii=False).encode("utf-8")).hexdigest()
        with self.lock:
            if self.poisoned:
                raise ProtocolError(503, "broker_persistence_failed")
            self._expire()
            old = self.records.get(body["id"])
            if old:
                if old["request_fingerprint"] != fingerprint:
                    raise ProtocolError(409, "command_id_conflict")
                return self._command_reply(old)
            if self.closed or not self._connected():
                raise ProtocolError(503, "game_disconnected")
            if len(self.records) >= self.max_records:
                raise ProtocolError(503, "command_journal_full")
            if self._active():
                raise ProtocolError(409, "command_in_flight")
            if body["broker_epoch"] != self.broker_epoch:
                raise ProtocolError(409, "broker_epoch_mismatch")
            if (body["expected_world_epoch"] != self.state["world_epoch"]
                    or body["expected_revision"] != self.state["revision"]
                    or body["expected_version"] != self.state["version"]):
                raise ProtocolError(409, "stale_state")
            if body["action"] != "observe" and not self.state["ready"]:
                raise ProtocolError(409, self.state.get("not_ready_reason") or "game_not_ready")
            if body["action"] == "close_menu" and not self.state["can_close_menu"]:
                raise ProtocolError(409, "precondition")
            command = {"schema_version": 1, "id": body["id"], "action": body["action"],
                       "broker_epoch": self.broker_epoch,
                       "expected_world_epoch": body["expected_world_epoch"],
                       "expected_revision": body["expected_revision"],
                       "expected_version": body["expected_version"],
                       "expires_at_unix_ms": int((self.clock() + self.ttl) * 1000)}
            record = {"command_id": body["id"], "request_fingerprint": fingerprint,
                      "request": request, "command": command, "command_status": "pending",
                      "result": None, "delivery_session_id": None,
                      "created_at_unix_ms": int(self.clock() * 1000),
                      "updated_at_unix_ms": int(self.clock() * 1000)}
            self.records[body["id"]] = record
            self._persist()
            return self._command_reply(record)

    def reconcile(self, body):
        if (not isinstance(body, dict)
                or set(body) != {"schema_version", "id", "broker_epoch", "world_epoch", "revision",
                                "expected_version", "operator_note", "confirm_no_replay"}
                or type(body.get("schema_version")) is not int
                or body.get("schema_version") != 1 or not isinstance(body.get("id"), str)
                or not isinstance(body.get("broker_epoch"), str)
                or not isinstance(body.get("world_epoch"), str) or not 1 <= len(body["world_epoch"]) <= 128
                or type(body.get("revision")) is not int or body["revision"] < 0
                or not isinstance(body.get("expected_version"), str)
                or not isinstance(body.get("operator_note"), str)
                or not 8 <= len(body["operator_note"].strip()) <= 500
                or body.get("confirm_no_replay") is not True):
            raise ProtocolError(400, "invalid_reconciliation")
        with self.lock:
            if self.closed or self.poisoned:
                raise ProtocolError(503, "server_closed")
            if body["broker_epoch"] != self.broker_epoch:
                raise ProtocolError(409, "broker_epoch_mismatch")
            if not self._connected() or self.state is None:
                raise ProtocolError(409, "game_disconnected")
            record = self.records.get(body["id"])
            if (record is None or record["command_status"] != "outcome_unknown"
                    or record.get("resolution") is not None or record["command"].get("action") != "close_menu"):
                raise ProtocolError(409, "command_not_reconcilable")
            if self.clock() * 1000 <= record["command"]["expires_at_unix_ms"]:
                raise ProtocolError(409, "command_not_expired")
            if (not self.state["ready"] or self.state["menu_open"]
                    or self.state["world_epoch"] != body["world_epoch"]
                    or self.state["revision"] != body["revision"]
                    or self.state["version"] != body["expected_version"]):
                raise ProtocolError(409, "snapshot_mismatch_or_not_aligned")
            original = record["command"]
            if (body["world_epoch"] == original["expected_world_epoch"]
                    and body["revision"] <= original["expected_revision"]):
                raise ProtocolError(409, "snapshot_not_advanced")
            resolution = {"schema_version": 1, "method": "manual_state_alignment",
                          "command_id": record["command_id"], "broker_epoch": self.broker_epoch,
                          "command_expires_at_unix_ms": original["expires_at_unix_ms"],
                          "session_id": self.session_id, "world_epoch": self.state["world_epoch"],
                          "revision": self.state["revision"], "version": self.state["version"],
                          "snapshot": dict(self.state), "operator_note": body["operator_note"].strip(),
                          "confirm_no_replay": True, "recorded_at_unix_ms": int(self.clock() * 1000)}
            record["resolution"] = resolution
            record["updated_at_unix_ms"] = max(record["updated_at_unix_ms"],
                                                resolution["recorded_at_unix_ms"])
            self._persist()
            return self._command_reply(record)

    @staticmethod
    def _command_reply(record):
        return {"id": record["command_id"], "command_status": record["command_status"],
                "status": LEGACY_STATUS[record["command_status"]],
                "result": record["result"], "resolved": bool(record.get("resolution")),
                "resolution": record.get("resolution")}

    def snapshot(self):
        with self.lock:
            self._expire()
            active = self._active()
            if self.poisoned:
                reason = "broker_persistence_failed"
            elif active and active["command_status"] == "outcome_unknown":
                reason = "outcome_unknown"
            elif active:
                reason = "command_in_flight"
            elif not self._connected():
                reason = "game_disconnected"
            elif self.state is not None and not self.state["ready"]:
                reason = self.state.get("not_ready_reason") or "game_not_ready"
            else:
                reason = None
            return {"schema_version": 1, "broker_epoch": self.broker_epoch,
                    "session_id": self.session_id, "state": self.state,
                    "connected": self._connected() and not self.closed,
                    "can_act": (self._connected() and self.state is not None and self.state["ready"]
                                and not self.closed and not self.poisoned and not active),
                    "blocked_command_id": active["command_id"] if active else None,
                    "blocking_reason": reason}

    def result(self, command_id):
        with self.lock:
            self._expire()
            record = self.records.get(command_id)
            if record:
                return self._command_reply(record)
            raise ProtocolError(404, "unknown_command")

    def close(self):
        with self.lock:
            if self.closed:
                if self.journal:
                    self.journal.release()
                return
            self.closed = True
            active = self._active()
            try:
                if active:
                    status = "outcome_unknown" if active["command_status"] in {"running", "outcome_unknown"} else "rejected"
                    try:
                        self._terminal(active, status, "server_shutdown")
                    except ProtocolError:
                        # The broker is poisoned; it must not keep running after a failed final write.
                        pass
            finally:
                if self.journal:
                    self.journal.release()


class Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self, broker, port=PORT, diagnostics=False):
        self.broker = broker
        self.diagnostics = diagnostics
        self.reported_errors = set()
        super().__init__(("127.0.0.1", port), Handler)

    def server_close(self):
        self.broker.close()
        super().server_close()


class Handler(BaseHTTPRequestHandler):
    server_version = "PawnPilotM0"

    def setup(self):
        super().setup()
        self.connection.settimeout(3.0)

    def log_message(self, *_):
        pass  # 不记录请求头或连接密钥。

    def _reply(self, status, body):
        raw = json.dumps(body, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(raw)
        self.close_connection = True

    def _handle(self, method):
        try:
            expected_host = f"127.0.0.1:{self.server.server_port}"
            if self.headers.get("Origin") is not None or self.headers.get("Host") != expected_host:
                raise ProtocolError(403, "browser_or_invalid_host")
            auth = self.headers.get("Authorization", "")
            if not hmac.compare_digest(auth.encode(), ("Bearer " + self.server.broker.token).encode()):
                raise ProtocolError(401, "unauthorized")
            if method == "GET":
                if self.path == "/state":
                    answer = self.server.broker.snapshot()
                elif self.path.startswith("/results/"):
                    answer = self.server.broker.result(self.path[len("/results/"):])
                else:
                    raise ProtocolError(404, "unknown_endpoint")
            else:
                if self.headers.get("Transfer-Encoding") is not None:
                    raise ProtocolError(400, "unsupported_transfer_encoding")
                lengths = self.headers.get_all("Content-Length", [])
                if len(lengths) != 1 or not lengths[0].isdigit():
                    raise ProtocolError(400, "invalid_content_length")
                length = int(lengths[0])
                if not 0 < length <= MAX_BODY:
                    raise ProtocolError(413, "body_too_large_or_empty")
                if self.headers.get_content_type() != "application/json":
                    raise ProtocolError(415, "json_required")
                raw = self.rfile.read(length)
                if len(raw) != length:
                    raise ProtocolError(400, "incomplete_body")
                body = json.loads(raw, parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite")))
                if not isinstance(body, dict):
                    raise ProtocolError(400, "object_required")
                if self.path == "/exchange":
                    answer = self.server.broker.exchange(body)
                elif self.path == "/commands":
                    answer = self.server.broker.enqueue(body)
                elif self.path == "/reconcile":
                    answer = self.server.broker.reconcile(body)
                else:
                    raise ProtocolError(404, "unknown_endpoint")
            self._reply(200, answer)
        except ProtocolError as exc:
            if self.server.diagnostics and exc.reason not in self.server.reported_errors:
                self.server.reported_errors.add(exc.reason)
                print(f"Protocol rejection: HTTP {exc.status} {exc.reason}", flush=True)
                if exc.reason == "invalid_snapshot":
                    # 只输出协议字段形状，不输出令牌或任意请求内容。
                    snapshot = body.get("state")
                    shape = {k: type(v).__name__ for k, v in snapshot.items()} if isinstance(snapshot, dict) else type(snapshot).__name__
                    print(f"Snapshot shape: {shape}; session_type={type(body.get('session_id')).__name__}", flush=True)
            self._reply(exc.status, {"error": exc.reason})
        except (ValueError, UnicodeError):
            self._reply(400, {"error": "invalid_json"})
        except (TimeoutError, ConnectionError):
            self.close_connection = True

    def do_GET(self):
        self._handle("GET")

    def do_POST(self):
        self._handle("POST")


def create_config(path):
    # 配置仅可创建在本项目内，不覆盖现有密钥。
    root = Path(__file__).resolve().parents[1]
    target = Path(path).resolve()
    if not target.is_relative_to(root):
        raise ValueError("config must be inside this repository")
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        config = json.loads(target.read_text(encoding="utf-8"))
        if (not isinstance(config, dict) or config.get("port") != PORT
                or not isinstance(config.get("token"), str) or len(config["token"]) != 64
                or any(c not in "0123456789abcdef" for c in config["token"])):
            raise ValueError("invalid existing config")
        return config
    config = {"port": PORT, "token": secrets.token_hex(32)}
    with target.open("x", encoding="utf-8") as file:
        json.dump(config, file)
    return config


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", default="artifacts/session.json")
    args = parser.parse_args()
    config = create_config(args.config)
    config_path = Path(args.config).resolve()
    journal_path = config_path.with_name("broker-journal.json")
    server = Server(Broker(config["token"], journal_path=journal_path), diagnostics=True)
    print(f"PawnPilot broker listening on 127.0.0.1:{PORT}; Ctrl+C to stop", flush=True)
    try:
        server.serve_forever(poll_interval=0.2)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
