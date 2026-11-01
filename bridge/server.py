"""PawnPilot M0：仅绑定 loopback 的标准库通信探针，不是 MCP 服务。"""
from __future__ import annotations

import argparse
import copy
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
    from .protocol import (WIRE_VERSION, WIRE_VERSION_V3, MAX_V2_MESSAGE_BYTES,
                           MAX_V3_MESSAGE_BYTES, compact_json_size, response_size_valid,
                           progress_matches_command, request_fingerprint,
                           valid_command, valid_progress, valid_progress_transition,
                           valid_request, valid_result_structure, valid_snapshot,
                           valid_success_result, valid_command_v3, valid_request_v3,
                           valid_result_structure_v3, valid_snapshot_v3, valid_success_result_v3,
                           valid_action_binding_v3,
                           strict_json_loads)
except ImportError:  # Support the existing `python bridge/server.py` launch path.
    from journal import Journal, JournalError
    from protocol import (WIRE_VERSION, WIRE_VERSION_V3, MAX_V2_MESSAGE_BYTES,
                          MAX_V3_MESSAGE_BYTES, compact_json_size, response_size_valid,
                          progress_matches_command, request_fingerprint,
                          valid_command, valid_progress, valid_progress_transition,
                          valid_request, valid_result_structure, valid_snapshot,
                          valid_success_result, valid_command_v3, valid_request_v3,
                          valid_result_structure_v3, valid_snapshot_v3, valid_success_result_v3,
                          valid_action_binding_v3,
                          strict_json_loads)

PORT = 18765
MAX_BODY = MAX_V3_MESSAGE_BYTES
ACTION = "close_menu"
LEGACY_STATUS = {"pending": "queued", "running": "delivered", "succeeded": "success",
                 "rejected": "rejected", "failed": "error", "outcome_unknown": "uncertain"}


class ProtocolError(Exception):
    def __init__(self, status: int, reason: str):
        self.status, self.reason = status, reason


class Broker:
    """Durable single-writer broker; a delivered action is never replayed."""
    def __init__(self, token: str, clock=time.time, ttl=5.0, heartbeat_timeout=3.0,
                 journal_path=None, max_records=4096, execution_timeout_ms=5000,
                 monotonic=None):
        if type(execution_timeout_ms) is not int or not 1 <= execution_timeout_ms <= 30_000:
            raise ValueError("execution_timeout_ms must be between 1 and 30000")
        self.token = token
        self.clock = clock
        # Test clocks are commonly injected as a single source. Production keeps elapsed
        # durations on a monotonic clock even if the wall clock is adjusted.
        self.monotonic = monotonic if monotonic is not None else (
            time.monotonic if clock is time.time else clock)
        self.ttl = ttl
        self.heartbeat_timeout = heartbeat_timeout
        self.execution_timeout_ms = execution_timeout_ms
        self.lock = threading.Lock()
        self.journal = Journal(journal_path) if journal_path is not None else None
        self.max_records = max_records
        self.poisoned = False
        self.session_id = None
        self.state = None
        self.session_wire_version = None
        self.last_seen = 0.0
        self.start_monotonic_deadlines = {}
        self.execution_monotonic_deadlines = {}
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

    @staticmethod
    def _record_schema_version(record):
        return record["command"].get("schema_version", 1)

    @staticmethod
    def _snapshot_valid(value, version):
        return valid_snapshot_v3(value) if version == WIRE_VERSION_V3 else valid_snapshot(value)

    @staticmethod
    def _request_valid(value, version):
        return valid_request_v3(value) if version == WIRE_VERSION_V3 else valid_request(value)

    @staticmethod
    def _command_valid(value, version):
        return valid_command_v3(value) if version == WIRE_VERSION_V3 else valid_command(value)

    @staticmethod
    def _result_valid(value, version, require_execution=True):
        return (valid_result_structure_v3(value, require_execution) if version == WIRE_VERSION_V3
                else valid_result_structure(value, require_execution))

    @staticmethod
    def _command_deadline(command):
        if command.get("schema_version") == 1:
            return command["expires_at_unix_ms"]
        return command["start_deadline_unix_ms"] + command["execution_timeout_ms"]

    def _persist(self):
        if not self.journal:
            return
        value = {"journal_format_version": 2, "broker_epoch": self.broker_epoch,
                 "records": list(self.records.values())}
        try:
            self.journal.save(value)
        except JournalError:
            self.poisoned = True
            raise ProtocolError(503, "broker_persistence_failed")

    def _broker_result(self, record, status, reason):
        command = record["command"]
        if self._record_schema_version(record) == 1:
            return {"schema_version": 1, "id": record["command_id"],
                    "broker_epoch": command["broker_epoch"], "status": LEGACY_STATUS[status],
                    "reason": reason, "before": None, "after": None}
        version = self._record_schema_version(record)
        return {"schema_version": version if version in {WIRE_VERSION, WIRE_VERSION_V3} else WIRE_VERSION,
                "id": record["command_id"],
                "broker_epoch": command["broker_epoch"], "status": status,
                "reason": reason, "before": None, "after": None, "execution": None}

    def _set_terminal(self, record, status, reason, result=None):
        record["command_status"] = status
        record["result"] = copy.deepcopy(result) if result is not None else self._broker_result(record, status, reason)
        record["updated_at_unix_ms"] = max(record["created_at_unix_ms"], int(self.clock() * 1000))

    def _terminal(self, record, status, reason, result=None):
        self._set_terminal(record, status, reason, result)
        self._persist()

    def _connected(self):
        return self.session_id is not None and self.monotonic() - self.last_seen <= self.heartbeat_timeout

    def _expire(self):
        record = self._active()
        if not record or record["command_status"] not in {"pending", "running"}:
            return
        now_ms = int(self.clock() * 1000)
        now_monotonic = self.monotonic()
        command = record["command"]
        start_elapsed = (now_ms >= command["start_deadline_unix_ms"]
                         or now_monotonic >= self.start_monotonic_deadlines.get(
                             record["command_id"], float("inf")))
        if record["command_status"] == "pending" and start_elapsed:
            self._terminal(record, "rejected", "expired_before_delivery")
            return
        if record["command_status"] != "running":
            return
        execution = record.get("execution")
        if not self._connected():
            self._terminal(record, "outcome_unknown", "delivery_outcome_unknown")
        elif execution is None and start_elapsed:
            self._terminal(record, "outcome_unknown", "start_deadline_elapsed_without_invocation")
        elif (execution is not None and execution["may_have_side_effects"]
              and (now_ms >= execution["execution_deadline_unix_ms"]
                   or now_monotonic >= self.execution_monotonic_deadlines.get(
                       record["command_id"], float("inf")))):
            self._terminal(record, "outcome_unknown", "execution_deadline_elapsed")
        elif execution is not None and not execution["may_have_side_effects"] and start_elapsed:
            self._terminal(record, "outcome_unknown", "start_deadline_elapsed_without_invocation")

    @staticmethod
    def _valid_state(state):
        version = state.get("schema_version") if type(state) is dict else None
        return valid_snapshot_v3(state) if version == WIRE_VERSION_V3 else valid_snapshot(state)

    @staticmethod
    def _validate_exchange_shape(body):
        expected = {"schema_version", "session_id", "broker_epoch", "state",
                    "has_result", "result", "progress"}
        if type(body) is not dict:
            raise ProtocolError(400, "invalid_exchange")
        if type(body.get("schema_version")) is not int or body.get("schema_version") not in {WIRE_VERSION, WIRE_VERSION_V3}:
            raise ProtocolError(400, "schema_version_unsupported")
        if type(body.get("has_result")) is not bool:
            raise ProtocolError(400, "has_result_boolean_required")
        if set(body) != expected:
            raise ProtocolError(400, "invalid_exchange")
        limit = MAX_V3_MESSAGE_BYTES if body["schema_version"] == WIRE_VERSION_V3 else MAX_V2_MESSAGE_BYTES
        size = compact_json_size(body)
        if size is None or size > limit:
            raise ProtocolError(413, "exchange_too_large")

    def _progress_record(self, progress, sid, version):
        record = self.records.get(progress["id"])
        if (record is None or self._record_schema_version(record) != version
                or record.get("delivery_session_id") != sid
                or record["command"]["broker_epoch"] != progress["broker_epoch"]
                or not progress_matches_command(progress, record["command"])
                or not valid_progress_transition(record.get("execution"), progress)):
            raise ProtocolError(409, "invalid_progress")
        return record

    def _validate_result(self, record, result):
        version = self._record_schema_version(record)
        if (version not in {WIRE_VERSION, WIRE_VERSION_V3}
                or result["id"] != record["command_id"]
                or result["broker_epoch"] != record["command"]["broker_epoch"]
                or not progress_matches_command(result["execution"], record["command"])
                or not valid_progress_transition(record.get("execution"), result["execution"])
                or not (valid_success_result_v3(record["command"], result) if version == WIRE_VERSION_V3
                        else valid_success_result(record["command"], result))):
            raise ProtocolError(409, "invalid_result_evidence")

    def exchange(self, body):
        self._validate_exchange_shape(body)
        sid = body["session_id"]
        seen_epoch = body["broker_epoch"]
        if (type(sid) is not str or not 1 <= len(sid) <= 128
                or (seen_epoch is not None
                    and (type(seen_epoch) is not str or not 1 <= len(seen_epoch) <= 128))):
            raise ProtocolError(400, "invalid_exchange_identity")
        wire_version = body["schema_version"]
        state = body["state"]
        if not self._snapshot_valid(state, wire_version):
            raise ProtocolError(400, "invalid_snapshot")
        active_before_lock = self._active()
        if ((self.session_id == body["session_id"] and self.session_wire_version is not None
             and self.session_wire_version != wire_version)
                or (active_before_lock is not None
                    and self._record_schema_version(active_before_lock) in {WIRE_VERSION, WIRE_VERSION_V3}
                    and self._record_schema_version(active_before_lock) != wire_version)):
            raise ProtocolError(409, "session_wire_version_mismatch")
        progress = body["progress"]
        if progress is not None and not valid_progress(progress):
            raise ProtocolError(400, "invalid_progress")
        result = body["result"] if body["has_result"] else None
        if body["has_result"] and not self._result_valid(result, wire_version):
            raise ProtocolError(400, "invalid_result")
        if result is not None and progress is not None and progress != result["execution"]:
            raise ProtocolError(400, "conflicting_progress_and_result")
        if result is None and progress is not None and progress["stage"] in {"completed", "uncertain"}:
            raise ProtocolError(409, "terminal_progress_requires_result")

        with self.lock:
            if self.closed or self.poisoned:
                raise ProtocolError(503, "server_closed")
            self._expire()
            if (self.session_id == sid and self.session_wire_version is not None
                    and self.session_wire_version != wire_version):
                raise ProtocolError(409, "session_wire_version_mismatch")
            session_changed = self.session_id is not None and self.session_id != sid
            if session_changed:
                active = self._active()
                if active and active["command_status"] == "pending":
                    self._terminal(active, "rejected", "session_changed_before_delivery")
                elif active and active["command_status"] == "running":
                    self._terminal(active, "outcome_unknown", "session_changed_after_delivery")

            acknowledged_id = None
            progress_record = None
            if progress is not None:
                progress_record = self._progress_record(progress, sid, wire_version)
                if (result is None and progress_record["result"] is not None
                        and progress_record["result"].get("execution") is not None):
                    raise ProtocolError(409, "progress_after_terminal_result")
                if (progress["may_have_side_effects"]
                        and progress["id"] not in self.execution_monotonic_deadlines):
                    now_ms = int(self.clock() * 1000)
                    now_monotonic = self.monotonic()
                    remaining_ms = min(
                        progress["execution_deadline_unix_ms"] - progress["started_at_unix_ms"],
                        max(0, progress["execution_deadline_unix_ms"] - now_ms))
                    self.execution_monotonic_deadlines[progress["id"]] = (
                        now_monotonic + remaining_ms / 1000)
            if result is not None:
                record = self.records.get(result["id"])
                if (record is None or self._record_schema_version(record) != wire_version
                        or record.get("delivery_session_id") != sid
                        or record["command"]["broker_epoch"] != result["broker_epoch"]):
                    raise ProtocolError(409, "unknown_or_conflicting_result")
                acknowledged_id = result["id"]
                self._validate_result(record, result)
                if record["result"] == result:
                    pass
                elif record["command_status"] in {"running", "outcome_unknown"}:
                    record["execution"] = copy.deepcopy(result["execution"])
                    self._terminal(record, result["status"], result["reason"], result)
                else:
                    raise ProtocolError(409, "unknown_or_conflicting_result")
            elif progress is not None:
                progress_record["execution"] = copy.deepcopy(progress)
                progress_record["updated_at_unix_ms"] = max(
                    progress_record["updated_at_unix_ms"], int(self.clock() * 1000))
                self._persist()

            prior_session_evidence = (self.session_id is not None and self.session_id != sid
                                      and (result is not None or progress is not None))
            if not prior_session_evidence:
                self.session_id, self.state, self.last_seen = sid, copy.deepcopy(state), self.monotonic()
                self.session_wire_version = wire_version

            command = None
            active = self._active()
            if seen_epoch == self.broker_epoch and active and active["command_status"] == "pending":
                expected = active["command"]
                if (expected["expected_world_epoch"] != state["world_epoch"]
                        or expected["expected_revision"] != state["revision"]
                        or expected["expected_version"] != state["version"]):
                    self._terminal(active, "rejected", "stale_state")
                elif not any(a["action"] == expected["action"] and a["arguments"] == expected["arguments"]
                             for a in state["legal_actions"]):
                    self._terminal(active, "rejected", "precondition")
                else:
                    active["command_status"] = "running"
                    active["delivery_session_id"] = sid
                    active["updated_at_unix_ms"] = int(self.clock() * 1000)
                    self._persist()
                    command = dict(expected)
            return {"schema_version": wire_version, "broker_epoch": self.broker_epoch,
                    "acknowledged_result_id": acknowledged_id,
                    "has_command": command is not None, "command": command}

    def enqueue(self, body):
        if type(body) is not dict:
            raise ProtocolError(400, "invalid_command")
        if type(body.get("schema_version")) is not int or body.get("schema_version") not in {WIRE_VERSION, WIRE_VERSION_V3}:
            raise ProtocolError(400, "schema_version_unsupported")
        version = body["schema_version"]
        if not self._request_valid(body, version):
            raise ProtocolError(400, "invalid_command")
        try:
            fingerprint = request_fingerprint(body)
        except ValueError:
            raise ProtocolError(400, "invalid_command") from None
        with self.lock:
            if self.poisoned:
                raise ProtocolError(503, "broker_persistence_failed")
            self._expire()
            old = self.records.get(body["id"])
            if old:
                if old["request_fingerprint"] != fingerprint:
                    raise ProtocolError(409, "command_id_conflict")
                return self._command_reply(old)
            if version == WIRE_VERSION and (body["action"] != ACTION or body["arguments"] != {}):
                raise ProtocolError(400, "action_not_allowed")
            if version == WIRE_VERSION_V3 and (body["action"] != ACTION or body["arguments"] != {}):
                raise ProtocolError(400, "action_not_allowed")
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
            if not self.state["ready"]:
                raise ProtocolError(409, self.state.get("not_ready_reason") or "game_not_ready")
            if body["action"] == "close_menu" and not self.state["can_close_menu"]:
                raise ProtocolError(409, "precondition")
            if self.session_wire_version != version:
                raise ProtocolError(409, "session_wire_version_mismatch")
            if not any(a["action"] == body["action"] and a["arguments"] == body["arguments"]
                       for a in self.state["legal_actions"]):
                raise ProtocolError(409, "precondition")
            if version == WIRE_VERSION_V3 and not valid_action_binding_v3(
                    self.state["business"], body["action"], body["arguments"]):
                raise ProtocolError(409, "stale_business_reference")
            now_wall = self.clock()
            now_monotonic = self.monotonic()
            request = copy.deepcopy(body)
            command = dict(request, start_deadline_unix_ms=int((now_wall + self.ttl) * 1000),
                           execution_timeout_ms=self.execution_timeout_ms)
            if not self._command_valid(command, version):
                raise ProtocolError(500, "invalid_broker_command")
            now_ms = int(now_wall * 1000)
            record = {"command_id": body["id"], "request_fingerprint": fingerprint,
                      "request": request, "command": command, "command_status": "pending",
                      "result": None, "delivery_session_id": None,
                      "created_at_unix_ms": now_ms, "updated_at_unix_ms": now_ms,
                      "execution": None}
            self.records[body["id"]] = record
            self.start_monotonic_deadlines[body["id"]] = now_monotonic + self.ttl
            self._persist()
            return self._command_reply(record)

    def reconcile(self, body):
        if (not isinstance(body, dict)
                or set(body) != {"schema_version", "id", "broker_epoch", "world_epoch", "revision",
                                "expected_version", "operator_note", "confirm_no_replay"}
                or type(body.get("schema_version")) is not int
                or body.get("schema_version") not in {WIRE_VERSION, WIRE_VERSION_V3} or not isinstance(body.get("id"), str)
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
            record_version = self._record_schema_version(record)
            evidence_version = WIRE_VERSION if record_version == 1 else record_version
            if (evidence_version != body["schema_version"]
                    or self.session_wire_version != evidence_version):
                raise ProtocolError(409, "session_wire_version_mismatch")
            if self.session_id == record.get("delivery_session_id"):
                raise ProtocolError(409, "fresh_mod_session_required")
            deadline = self._command_deadline(record["command"])
            now_ms = int(self.clock() * 1000)
            if now_ms < deadline:
                raise ProtocolError(409, "command_not_expired")
            mono_deadline = self.start_monotonic_deadlines.get(record["command_id"])
            if mono_deadline is not None:
                # This process can prove its latest local deadline without wall-clock drift.
                # After restart the map is intentionally empty; the durable Unix deadline
                # above plus the required new Mod session are the available evidence.
                command = record["command"]
                if self._record_schema_version(record) in {WIRE_VERSION, WIRE_VERSION_V3}:
                    mono_deadline += command["execution_timeout_ms"] / 1000
                reported_deadline = self.execution_monotonic_deadlines.get(record["command_id"])
                if reported_deadline is not None:
                    mono_deadline = max(mono_deadline, reported_deadline)
                if self.monotonic() < mono_deadline:
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
            resolution = {"schema_version": evidence_version, "method": "manual_state_alignment",
                          "command_id": record["command_id"], "broker_epoch": self.broker_epoch,
                          "command_expires_at_unix_ms": deadline,
                          "session_id": self.session_id, "world_epoch": self.state["world_epoch"],
                          "revision": self.state["revision"], "version": self.state["version"],
                          "snapshot": copy.deepcopy(self.state), "operator_note": body["operator_note"].strip(),
                          "confirm_no_replay": True, "recorded_at_unix_ms": now_ms}
            record["resolution"] = resolution
            record["updated_at_unix_ms"] = max(record["updated_at_unix_ms"],
                                                resolution["recorded_at_unix_ms"])
            self._persist()
            return self._command_reply(record)

    @staticmethod
    def _command_reply(record):
        record_version = Broker._record_schema_version(record)
        envelope_version = record_version if record_version in {WIRE_VERSION, WIRE_VERSION_V3} else WIRE_VERSION
        return {"id": record["command_id"], "command_status": record["command_status"],
                "status": record["command_status"],
                "result": copy.deepcopy(record["result"]), "resolved": bool(record.get("resolution")),
                "resolution": copy.deepcopy(record.get("resolution")), "schema_version": envelope_version,
                "execution": copy.deepcopy(record.get("execution")),
                "record_schema_version": Broker._record_schema_version(record)}

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
            state_version = self.session_wire_version or WIRE_VERSION
            return {"schema_version": state_version, "broker_epoch": self.broker_epoch,
                    "session_id": self.session_id, "state": copy.deepcopy(self.state),
                    "connected": self._connected() and not self.closed,
                    "can_act": (self._connected() and self.state is not None and self.state["ready"]
                                and not self.closed and not self.poisoned and not active),
                    "blocked_command_id": active["command_id"] if active else None,
                    "blocking_reason": reason,
                    "execution": copy.deepcopy(active.get("execution")) if active else None}

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
        if not response_size_valid(body, len(raw)):
            status = 500
            raw = json.dumps({"error": "response_too_large"}, separators=(",", ":")).encode("utf-8")
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(raw)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(raw)
        except ConnectionError:
            # A bounded/cancelled read can close before even an error response is sent.
            # Command/result persistence already happened; never retry a mutation here.
            pass
        finally:
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
                wire_header = self.headers.get("X-PawnPilot-Wire-Version")
                if wire_header is not None and wire_header not in {"2", "3"}:
                    raise ProtocolError(400, "wire_version_header_invalid")
                if length > MAX_V2_MESSAGE_BYTES and wire_header != str(WIRE_VERSION_V3):
                    raise ProtocolError(413, "body_too_large_or_empty")
                if not 0 < length <= MAX_BODY:
                    raise ProtocolError(413, "body_too_large_or_empty")
                if self.headers.get_content_type() != "application/json":
                    raise ProtocolError(415, "json_required")
                raw = self.rfile.read(length)
                if len(raw) != length:
                    raise ProtocolError(400, "incomplete_body")
                try:
                    body = strict_json_loads(raw.decode("utf-8"))
                except (ValueError, UnicodeError) as exc:
                    reason = str(exc) if str(exc) in {
                        "duplicate_json_key", "json_too_deep", "nonfinite_json"
                    } else "invalid_json"
                    raise ProtocolError(400, reason) from None
                if not isinstance(body, dict):
                    raise ProtocolError(400, "object_required")
                body_version = body.get("schema_version")
                if (wire_header is not None and type(body_version) is int
                        and wire_header != str(body_version)):
                    raise ProtocolError(400, "wire_version_header_mismatch")
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
