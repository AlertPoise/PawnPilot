"""Crash-safe persistent context, decision inbox, and authorization gates for one run."""
from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import tempfile
import threading
import time
from typing import Any

try:
    from .journal import Journal
    from .protocol import (DAY_ACTIONS, V4_ACTION_ARGUMENTS,
                           WIRE_VERSION_V4, request_fingerprint, strict_json_loads,
                           valid_request_v4, valid_snapshot_v4, valid_success_result_v4)
except ImportError:  # Support `python bridge/run_store.py` and the broker launch path.
    from journal import Journal
    from protocol import (DAY_ACTIONS, V4_ACTION_ARGUMENTS,
                          WIRE_VERSION_V4, request_fingerprint, strict_json_loads,
                          valid_request_v4, valid_snapshot_v4, valid_success_result_v4)


SCHEMA_VERSION = 1
MAX_STORE_BYTES = 4 * 1024 * 1024
MAX_COMMANDS = 4096
MAX_INBOX = 512
MAX_HISTORY = 4096
MAX_TEXT = 4096
MAX_TIME_MS = 9_007_199_254_740_991
_BUDGET_KEYS = ("customers", "writes", "trades", "buy_spend", "bargains", "day_fees", "days")
_RUN_STATUSES = {"paused", "running", "stopped", "awaiting_user", "blocked_unknown",
                 "world_mismatch", "disconnected", "unsupported", "completed"}
_INBOX_STATUSES = {"pending", "submitted", "resolved", "superseded", "awaiting_user",
                   "blocked_unknown", "unsupported"}
_JOURNAL_STATUSES = {"pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"}
_RESERVATION_STATUSES = {"reserved", "rejected_prequeue", "unknown"}
_SHA256 = re.compile(r"[0-9a-f]{64}\Z", re.ASCII)
_FORBIDDEN_ACTIONS = {"resume_inspection_handoff", "observe", "refuse_trade"}
_WRITE_ACTIONS = frozenset(V4_ACTION_ARGUMENTS) - {"observe"}
_TOP_LEVEL_KEYS = {"schema_version", "revision", "run", "authorization", "budget", "plan", "binding",
                    "controls", "inbox", "commands", "customer_keys", "confirmed_history", "completion_evidence"}


class RunStoreError(Exception):
    """A fail-closed RunStore boundary error consumed by the Broker wrapper."""

    def __init__(self, reason: str):
        self.reason = reason
        super().__init__(reason)


def _exact_dict(value: Any, keys: set[str]) -> bool:
    return type(value) is dict and set(value) == keys


def _bounded_tree(value: Any, depth: int = 0) -> bool:
    """Reject non-JSON values and over-deep/oversized trees at persistence boundaries."""
    if depth > 12:
        return False
    if value is None or type(value) in (bool, int):
        return type(value) is not int or -2_147_483_648 <= value <= 9_007_199_254_740_991
    if type(value) is float:
        return False
    if type(value) is str:
        return len(value.encode("utf-8")) <= 16_384
    if type(value) is list:
        return len(value) <= 4096 and all(_bounded_tree(item, depth + 1) for item in value)
    if type(value) is dict:
        return (len(value) <= 128
                and all(type(key) is str and len(key) <= 128 and _bounded_tree(item, depth + 1)
                        for key, item in value.items()))
    return False


def _is_int(value: Any, minimum: int = 0, maximum: int = 2_147_483_647) -> bool:
    return type(value) is int and minimum <= value <= maximum


def _is_text(value: Any, maximum: int, minimum: int = 1) -> bool:
    return type(value) is str and minimum <= len(value) <= maximum


def _valid_limits(value: Any) -> bool:
    return (_exact_dict(value, set(_BUDGET_KEYS))
            and all(_is_int(value[key]) for key in _BUDGET_KEYS))


def _is_wire_ref(value: Any) -> bool:
    return type(value) is str and re.fullmatch(r"[A-Za-z0-9_.:-]{1,128}", value, re.ASCII) is not None


def _valid_operator_confirmation(value: Any) -> bool:
    if value is None:
        return True
    keys = {"world_epoch", "session_id", "customer_ref", "handoff_ref", "item_refs", "instruction", "command_id"}
    if (not _exact_dict(value, keys)
            or not _is_text(value["world_epoch"], 128)
            or not _is_text(value["session_id"], 128)
            or not _is_wire_ref(value["customer_ref"])
            or not _is_wire_ref(value["handoff_ref"])
            or type(value["item_refs"]) is not list or not 1 <= len(value["item_refs"]) <= 64
            or any(not _is_wire_ref(item_ref) for item_ref in value["item_refs"])
            or len(value["item_refs"]) != len(set(value["item_refs"]))
            or not _is_text(value["instruction"], MAX_TEXT) or not value["instruction"].strip()
            or (value["command_id"] is not None and not _is_text(value["command_id"], 128))):
        return False
    return True


def _valid_command_entry(value: Any) -> bool:
    keys = {"command_id", "request_fingerprint", "action", "expected_world_epoch", "expected_revision",
            "session_id", "reservation_status", "journal_status", "budget_cost", "created_at",
            "updated_at", "related_event_id", "reason", "journal_broker_epoch", "delivered", "evidence"}
    if (not _exact_dict(value, keys)
            or not _is_text(value["command_id"], 128)
            or type(value["request_fingerprint"]) is not str or not _SHA256.fullmatch(value["request_fingerprint"])
            or type(value["action"]) is not str or value["action"] not in _WRITE_ACTIONS
            or not _is_text(value["expected_world_epoch"], 128)
            or not _is_int(value["expected_revision"])
            or not _is_text(value["session_id"], 128)
            or value["reservation_status"] not in _RESERVATION_STATUSES
            or (value["journal_status"] is not None and value["journal_status"] not in _JOURNAL_STATUSES)
            or not _valid_limits(value["budget_cost"])
            or not _is_int(value["created_at"], 0, MAX_TIME_MS)
            or not _is_int(value["updated_at"], 0, MAX_TIME_MS)
            or value["updated_at"] < value["created_at"]
            or (value["related_event_id"] is not None and not _is_text(value["related_event_id"], 128))
            or (value["reason"] is not None and not _is_text(value["reason"], 512, 0))
            or (value["journal_broker_epoch"] is not None and not _is_text(value["journal_broker_epoch"], 128))
            or type(value["delivered"]) is not bool
            or (value["evidence"] is not None and not _exact_dict(
                value["evidence"], {"result_status", "world_epoch", "revision", "evidence_hash"}))):
        return False
    evidence = value["evidence"]
    if (value["reason"] != "journal_command_without_run_reservation"
            and value["budget_cost"]["writes"] != 1):
        return False
    return (evidence is None or
            (evidence["result_status"] in {"succeeded", "rejected", "failed", "outcome_unknown"}
             and _is_text(evidence["world_epoch"], 128)
             and _is_int(evidence["revision"])
             and type(evidence["evidence_hash"]) is str and _SHA256.fullmatch(evidence["evidence_hash"])))


def _valid_inbox_entry(value: Any) -> bool:
    keys = {"event_id", "dedupe_key", "kind", "status", "world_epoch", "customer_key", "dialogue_key",
            "day_number", "occurrence", "phase", "semantic_fingerprint", "first_seen", "last_seen",
            "related_command_id", "reason",
            "resolution_evidence", "superseded_by"}
    if (not _exact_dict(value, keys)
            or not _is_text(value["event_id"], 128)
            or type(value["dedupe_key"]) is not str or not _SHA256.fullmatch(value["dedupe_key"])
            or value["kind"] not in {"decision", "manual", "unsupported"}
            or value["status"] not in _INBOX_STATUSES
            or not _is_text(value["world_epoch"], 128)
            or (value["customer_key"] is not None and
                (type(value["customer_key"]) is not str or not _SHA256.fullmatch(value["customer_key"])))
            or (value["dialogue_key"] is not None and
                (type(value["dialogue_key"]) is not str or not _SHA256.fullmatch(value["dialogue_key"])))
            or (value["day_number"] is not None and not _is_int(value["day_number"]))
            or not _is_int(value["occurrence"], 1, 2_147_483_647)
            or not _is_text(value["phase"], 128, 0)
            or type(value["semantic_fingerprint"]) is not str or not _SHA256.fullmatch(value["semantic_fingerprint"])
            or not _is_int(value["first_seen"], 0, MAX_TIME_MS)
            or not _is_int(value["last_seen"], 0, MAX_TIME_MS)
            or value["last_seen"] < value["first_seen"]
            or (value["related_command_id"] is not None and not _is_text(value["related_command_id"], 128))
            or (value["reason"] is not None and not _is_text(value["reason"], 512, 0))
            or (value["resolution_evidence"] is not None and not _exact_dict(
                value["resolution_evidence"], {"command_id", "status", "world_epoch", "revision", "evidence_hash"}))
            or (value["superseded_by"] is not None and not _is_text(value["superseded_by"], 128))):
        return False
    evidence = value["resolution_evidence"]
    return (evidence is None or
            (evidence["command_id"] == value["related_command_id"]
             and evidence["status"] == "succeeded"
             and _is_text(evidence["world_epoch"], 128)
             and _is_int(evidence["revision"])
             and type(evidence["evidence_hash"]) is str and _SHA256.fullmatch(evidence["evidence_hash"])))


def _valid_history_entry(value: Any) -> bool:
    return (_exact_dict(value, {"command_id", "action", "world_epoch", "status", "confirmed_at", "evidence_hash"})
            and _is_text(value["command_id"], 128)
            and type(value["action"]) is str and value["action"] in _WRITE_ACTIONS
            and _is_text(value["world_epoch"], 128)
            and value["status"] == "succeeded"
            and _is_int(value["confirmed_at"], 0, MAX_TIME_MS)
            and type(value["evidence_hash"]) is str and bool(_SHA256.fullmatch(value["evidence_hash"])))


def _valid_document(value: Any) -> bool:
    if (type(value) is not dict
            or (set(value) != _TOP_LEVEL_KEYS and set(value) != _TOP_LEVEL_KEYS | {"operator_confirmation"})
            or type(value["schema_version"]) is not int or value["schema_version"] != SCHEMA_VERSION
            or not _valid_operator_confirmation(value.get("operator_confirmation"))):
        return False
    run = value["run"]
    if (not _exact_dict(run, {"run_id", "save_label", "journal_path", "world_epoch", "session_id", "start_day",
                              "target_day", "goal", "status", "created_at", "updated_at"})
            or not _is_text(run["run_id"], 128) or not _is_text(run["save_label"], 128)
            or not _is_text(run["journal_path"], 4096) or not Path(run["journal_path"]).is_absolute()
            or not _is_text(run["world_epoch"], 128) or not _is_text(run["session_id"], 128)
            or not _is_int(run["start_day"]) or not _is_int(run["target_day"])
            or run["target_day"] <= run["start_day"]
            or not _is_text(run["goal"], MAX_TEXT)
            or run["status"] not in _RUN_STATUSES
            or not _is_int(run["created_at"], 0, MAX_TIME_MS)
            or not _is_int(run["updated_at"], 0, MAX_TIME_MS) or run["updated_at"] < run["created_at"]):
        return False
    authorization = value["authorization"]
    if (not _exact_dict(authorization, {"version", "limits", "allowed_actions"})
            or not _is_int(authorization["version"], 1)
            or not _valid_limits(authorization["limits"])
            or type(authorization["allowed_actions"]) is not list or len(authorization["allowed_actions"]) > len(_WRITE_ACTIONS)
            or len(authorization["allowed_actions"]) != len(set(authorization["allowed_actions"]))
            or any(type(action) is not str or action not in _WRITE_ACTIONS or action in _FORBIDDEN_ACTIONS
                   for action in authorization["allowed_actions"])):
        return False
    # writes may reflect a refused new-ID attempt; the hard limit still gates grants.
    if not _valid_limits(value["budget"]):
        return False
    plan = value["plan"]
    if (not _exact_dict(plan, {"text", "updated_at", "source"})
            or not _is_text(plan["text"], MAX_TEXT, 0)
            or not _is_int(plan["updated_at"], 0, MAX_TIME_MS)
            or plan["source"] not in {"agent", "user"}):
        return False
    binding = value["binding"]
    if (not _exact_dict(binding, {"world_epoch", "session_id", "broker_epoch", "day_number", "seen_at"})
            or not _is_text(binding["world_epoch"], 128) or not _is_text(binding["session_id"], 128)
            or (binding["broker_epoch"] is not None and not _is_text(binding["broker_epoch"], 128))
            or (binding["day_number"] is not None and not _is_int(binding["day_number"]))
            or not _is_int(binding["seen_at"], 0, MAX_TIME_MS)):
        return False
    controls = value["controls"]
    if not _exact_dict(controls, {"paused", "stopped", "awaiting_user", "world_mismatch", "blocked_unknown",
                                  "disconnected", "unsupported", "completed", "reason", "dispatch"}):
        return False
    if (any(type(controls[key]) is not bool for key in
            ("paused", "stopped", "awaiting_user", "world_mismatch", "blocked_unknown", "disconnected",
             "unsupported", "completed"))
            or (controls["reason"] is not None and not _is_text(controls["reason"], 512, 0))):
        return False
    dispatch = controls["dispatch"]
    if (not _exact_dict(dispatch, {"token_hash", "owner", "expires_at", "revoked", "command_id",
                                   "authorization_version", "world_epoch", "session_id", "broker_epoch"})
            or (dispatch["token_hash"] is not None and
                (type(dispatch["token_hash"]) is not str or not _SHA256.fullmatch(dispatch["token_hash"])))
            or (dispatch["owner"] is not None and not _is_text(dispatch["owner"], 128))
            or (dispatch["expires_at"] is not None and not _is_int(dispatch["expires_at"], 0, MAX_TIME_MS))
            or type(dispatch["revoked"]) is not bool
            or (dispatch["command_id"] is not None and not _is_text(dispatch["command_id"], 128))
            or (dispatch["authorization_version"] is not None and not _is_int(dispatch["authorization_version"], 1))
            or (dispatch["world_epoch"] is not None and not _is_text(dispatch["world_epoch"], 128))
            or (dispatch["session_id"] is not None and not _is_text(dispatch["session_id"], 128))
            or (dispatch["broker_epoch"] is not None and not _is_text(dispatch["broker_epoch"], 128))):
        return False
    if (dispatch["token_hash"] is None) != (dispatch["owner"] is None) or (
            dispatch["token_hash"] is None) != (dispatch["expires_at"] is None):
        return False
    bound_values = (dispatch["authorization_version"], dispatch["world_epoch"],
                    dispatch["session_id"], dispatch["broker_epoch"])
    if any((value is None) != (dispatch["token_hash"] is None) for value in bound_values):
        return False
    if (type(value["revision"]) is not int or value["revision"] < 0
            or type(value["inbox"]) is not list or len(value["inbox"]) > MAX_INBOX
            or any(not _valid_inbox_entry(event) for event in value["inbox"])
            or type(value["commands"]) is not list or len(value["commands"]) > MAX_COMMANDS
            or any(not _valid_command_entry(command) for command in value["commands"])
            or type(value["customer_keys"]) is not list or len(value["customer_keys"]) > MAX_INBOX
            or any(type(key) is not str or not _SHA256.fullmatch(key) for key in value["customer_keys"])
            or len(value["customer_keys"]) != len(set(value["customer_keys"]))
            or type(value["confirmed_history"]) is not list or len(value["confirmed_history"]) > MAX_HISTORY
            or any(not _valid_history_entry(entry) for entry in value["confirmed_history"])):
        return False
    if (len({entry["command_id"] for entry in value["commands"]}) != len(value["commands"])
            or len({event["event_id"] for event in value["inbox"]}) != len(value["inbox"])
            or len({entry["command_id"] for entry in value["confirmed_history"]}) != len(value["confirmed_history"])):
        return False
    command_by_id = {entry["command_id"]: entry for entry in value["commands"]}
    operator_confirmation = value.get("operator_confirmation")
    if operator_confirmation is not None and operator_confirmation["command_id"] is not None:
        confirmation_command = command_by_id.get(operator_confirmation["command_id"])
        if (confirmation_command is None
                or confirmation_command["action"] != "resume_inspection_handoff"
                or confirmation_command["expected_world_epoch"] != operator_confirmation["world_epoch"]
                or confirmation_command["session_id"] != operator_confirmation["session_id"]):
            return False
    for history in value["confirmed_history"]:
        command = command_by_id.get(history["command_id"])
        if (command is None or command["reason"] == "journal_command_without_run_reservation"
                or command["reservation_status"] != "reserved"
                or command["journal_status"] != "succeeded" or command["evidence"] is None
                or command["evidence"]["result_status"] != "succeeded"
                or command["evidence"]["evidence_hash"] != history["evidence_hash"]
                or command["action"] != history["action"]
                or command["evidence"]["world_epoch"] != history["world_epoch"]):
            return False
    for event in value["inbox"]:
        related = command_by_id.get(event["related_command_id"]) if event["related_command_id"] else None
        if event["status"] == "resolved":
            evidence = event["resolution_evidence"]
            if (related is None or related["reason"] == "journal_command_without_run_reservation"
                    or related["reservation_status"] != "reserved"
                    or related["related_event_id"] != event["event_id"]
                    or related["journal_status"] != "succeeded" or related["evidence"] is None
                    or related["evidence"]["result_status"] != "succeeded"
                    or evidence is None or evidence["evidence_hash"] != related["evidence"]["evidence_hash"]
                    or evidence["world_epoch"] != related["evidence"]["world_epoch"]
                    or event["world_epoch"] != related["evidence"]["world_epoch"]
                    or evidence["revision"] != related["evidence"]["revision"]):
                return False
        elif event["status"] == "submitted":
            if related is None or related["reservation_status"] != "reserved" \
                    or related["related_event_id"] != event["event_id"] \
                    or related["journal_status"] not in {None, "pending", "running"}:
                return False
        elif event["status"] == "blocked_unknown":
            if (related is None or related["related_event_id"] != event["event_id"]
                    or related["reservation_status"] != "unknown" or not controls["blocked_unknown"]):
                return False
    completion = value["completion_evidence"]
    if (completion is not None and not _exact_dict(completion, {"target_day", "world_epoch", "revision", "command_ids"})):
        return False
    if completion is not None and (
            completion["target_day"] != run["target_day"]
            or not _is_text(completion["world_epoch"], 128)
            or not _is_int(completion["revision"])
            or type(completion["command_ids"]) is not list
            or not 1 <= len(completion["command_ids"]) <= MAX_COMMANDS
            or any(not _is_text(command_id, 128) for command_id in completion["command_ids"])
            or len(completion["command_ids"]) != len(set(completion["command_ids"]))):
        return False
    if controls["completed"] != (completion is not None) or (completion is not None and run["status"] != "completed"):
        return False
    expected_status = ("completed" if controls["completed"] else
                       "blocked_unknown" if controls["blocked_unknown"] else
                       "world_mismatch" if controls["world_mismatch"] else
                       "awaiting_user" if controls["awaiting_user"] else
                       "stopped" if controls["stopped"] else
                       "unsupported" if controls["unsupported"] else
                       "paused" if controls["paused"] else
                       "disconnected" if controls["disconnected"] else "running")
    if run["status"] != expected_status:
        return False
    if (any(command["reservation_status"] == "unknown" or command["journal_status"] == "outcome_unknown"
            for command in value["commands"]) and not controls["blocked_unknown"]):
        return False
    if any(sum(command["budget_cost"][key] for command in value["commands"]) != value["budget"][key]
           for key in _BUDGET_KEYS):
        return False
    if (sum(command["reservation_status"] == "reserved"
            and command["journal_status"] in {None, "pending", "running"}
            for command in value["commands"]) > 1):
        return False
    if not _bounded_tree(value):
        return False
    return True


class _FileLock:
    """一操作一把短 OS 锁；不与 Supervisor 的进程长锁共用锁文件。"""
    _registry_guard = threading.Lock()
    _thread_locks = {}

    def __init__(self, path: Path):
        self.path = path
        self.file = None
        key = os.path.normcase(os.path.abspath(path))
        with self._registry_guard:
            self.thread_lock = self._thread_locks.setdefault(key, threading.RLock())

    def __enter__(self):
        # Windows 字节区间锁以进程为单位；本地锁还要串行化同进程的不同线程。
        self.thread_lock.acquire()
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.file = self.path.open("a+b")
            if os.name == "nt":
                import msvcrt
                self.file.seek(0)
                # 空锁文件也先锁定偏移 0；不能在锁之前读取，竞争进程可能拿不到读权限。
                # Windows 字节区间锁允许锁到 EOF 之外，写入初始化字节必须在持锁后进行。
                msvcrt.locking(self.file.fileno(), msvcrt.LK_LOCK, 1)
                self.file.seek(0, os.SEEK_END)
                if self.file.tell() == 0:
                    self.file.seek(0)
                    self.file.write(b"0")
                    self.file.flush()
                self.file.seek(0)
            else:
                import fcntl
                fcntl.flock(self.file.fileno(), fcntl.LOCK_EX)
        except (OSError, BlockingIOError) as exc:
            if self.file is not None:
                self.file.close()
                self.file = None
            self.thread_lock.release()
            raise RunStoreError("run_store_already_locked") from exc
        return self

    def __exit__(self, *_):
        if self.file is None:
            return
        try:
            if os.name == "nt":
                import msvcrt
                self.file.seek(0)
                msvcrt.locking(self.file.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file.fileno(), fcntl.LOCK_UN)
        finally:
            self.file.close()
            self.file = None
            self.thread_lock.release()


class RunStore:
    """Durable state for one managed run. All writes are short locked CAS transactions."""
    def __init__(self, path: str | Path, clock=None):
        self.path = Path(path)
        self.lock_path = self.path.with_suffix(self.path.suffix + ".lock")
        self._clock = clock or time.time

    def _now(self) -> int:
        try:
            value = self._clock()
            if hasattr(value, "timestamp"):
                value = value.timestamp()
            milliseconds = int(float(value) * 1000)
        except (TypeError, ValueError, OverflowError, OSError) as exc:
            raise RunStoreError("run_store_clock_invalid") from exc
        if not 0 <= milliseconds <= MAX_TIME_MS:
            raise RunStoreError("run_store_clock_invalid")
        return milliseconds

    def _load_unlocked(self):
        if not self.path.exists():
            raise RunStoreError("run_store_not_found")
        try:
            size = self.path.stat().st_size
            if size > MAX_STORE_BYTES:
                raise RunStoreError("run_store_corrupt")
            raw = self.path.read_bytes()
            if len(raw) != size or len(raw) > MAX_STORE_BYTES:
                raise RunStoreError("run_store_corrupt")
            document = strict_json_loads(raw.decode("utf-8"))
            # 兼容M5-C旧版已知字段集合，绝不为未知或部分损坏的字段静默补值。
            if type(document) is dict and set(document) == _TOP_LEVEL_KEYS:
                document["operator_confirmation"] = None
            if not _valid_document(document):
                raise RunStoreError("run_store_corrupt")
            return document
        except RunStoreError:
            raise
        except (OSError, UnicodeError, ValueError, RecursionError, TypeError) as exc:
            raise RunStoreError("run_store_corrupt") from exc

    def _atomic_replace(self, document):
        temporary = None
        try:
            if not _valid_document(document):
                raise RunStoreError("run_store_invalid_state")
            raw = json.dumps(document, separators=(",", ":"), ensure_ascii=False,
                             allow_nan=False).encode("utf-8")
            if len(raw) > MAX_STORE_BYTES:
                raise RunStoreError("run_store_too_large")
            fd, name = tempfile.mkstemp(prefix=self.path.name + ".", suffix=".tmp", dir=str(self.path.parent))
            temporary = name
            with os.fdopen(fd, "wb") as output:
                output.write(raw)
                output.flush()
                os.fsync(output.fileno())
            candidate = strict_json_loads(Path(temporary).read_bytes().decode("utf-8"))
            if not _valid_document(candidate):
                raise RunStoreError("run_store_invalid_state")
            os.replace(temporary, self.path)
            temporary = None
            if os.name != "nt":
                directory = os.open(self.path.parent, os.O_RDONLY)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
        except RunStoreError:
            raise
        except (OSError, TypeError, ValueError, UnicodeError, RecursionError) as exc:
            raise RunStoreError("run_store_write_failed") from exc
        finally:
            if temporary is not None:
                try:
                    os.unlink(temporary)
                except OSError:
                    pass

    @staticmethod
    def _normalize_journal_path(path: str | Path) -> str:
        if not isinstance(path, (str, Path)) or not str(path):
            raise RunStoreError("run_store_invalid_journal_path")
        try:
            normalized = str(Path(path).expanduser().resolve(strict=False))
        except (OSError, RuntimeError, ValueError) as exc:
            raise RunStoreError("run_store_invalid_journal_path") from exc
        if len(normalized) > 4096:
            raise RunStoreError("run_store_invalid_journal_path")
        return normalized

    @staticmethod
    def _normalize_actions(allowed_actions):
        if type(allowed_actions) not in (list, tuple) or len(allowed_actions) > len(_WRITE_ACTIONS):
            raise RunStoreError("run_store_invalid_allowed_actions")
        actions = list(allowed_actions)
        if (any(type(action) is not str or action not in _WRITE_ACTIONS or action in _FORBIDDEN_ACTIONS
                for action in actions) or len(actions) != len(set(actions))):
            raise RunStoreError("run_store_invalid_allowed_actions")
        return sorted(actions)

    def create(self, run_id, save_label, journal_path, world_epoch, session_id, start_day, target_day,
               limits, allowed_actions, goal):
        """Create exactly one initial context; creation never replaces a prior record."""
        if (not _is_text(run_id, 128) or not _is_text(save_label, 128)
                or not _is_text(world_epoch, 128) or not _is_text(session_id, 128)
                or not _is_int(start_day) or not _is_int(target_day) or target_day <= start_day
                or target_day - start_day > MAX_COMMANDS // 4
                or not _valid_limits(limits) or not _is_text(goal, MAX_TEXT, 0)):
            raise RunStoreError("run_store_invalid_create")
        if target_day > start_day + limits["days"]:
            raise RunStoreError("run_store_invalid_target_day")
        actions = self._normalize_actions(allowed_actions)
        normalized_journal = self._normalize_journal_path(journal_path)
        now = self._now()
        document = {
            "schema_version": SCHEMA_VERSION, "revision": 0,
            "run": {"run_id": run_id, "save_label": save_label, "journal_path": normalized_journal,
                    "world_epoch": world_epoch, "session_id": session_id, "start_day": start_day,
                    "target_day": target_day, "goal": goal, "status": "paused",
                    "created_at": now, "updated_at": now},
            "authorization": {"version": 1, "limits": dict(limits), "allowed_actions": actions},
            "budget": {key: 0 for key in _BUDGET_KEYS},
            "plan": {"text": "", "updated_at": now, "source": "agent"},
            "binding": {"world_epoch": world_epoch, "session_id": session_id,
                        "broker_epoch": None, "day_number": start_day, "seen_at": now},
            "controls": {"paused": True, "stopped": False, "awaiting_user": False,
                         "world_mismatch": False, "blocked_unknown": False, "disconnected": False,
                         "unsupported": False,
                         "completed": False, "reason": "created_paused",
                         "dispatch": {"token_hash": None, "owner": None, "expires_at": None,
                                      "revoked": False, "command_id": None,
                                      "authorization_version": None, "world_epoch": None,
                                      "session_id": None, "broker_epoch": None}},
            "inbox": [], "commands": [], "customer_keys": [], "confirmed_history": [],
            "completion_evidence": None, "operator_confirmation": None,
        }
        with _FileLock(self.lock_path):
            if self.path.exists():
                raise RunStoreError("run_store_already_exists")
            self._atomic_replace(document)
        return copy.deepcopy(document)

    def snapshot(self):
        with _FileLock(self.lock_path):
            return copy.deepcopy(self._load_unlocked())

    @staticmethod
    def _status(document):
        controls = document["controls"]
        if controls["completed"]:
            return "completed"
        if controls["blocked_unknown"]:
            return "blocked_unknown"
        if controls["world_mismatch"]:
            return "world_mismatch"
        if controls["awaiting_user"]:
            return "awaiting_user"
        if controls["stopped"]:
            return "stopped"
        if controls["unsupported"]:
            return "unsupported"
        if controls["paused"]:
            return "paused"
        if controls["disconnected"]:
            return "disconnected"
        return "running"

    @staticmethod
    def _touch(document, now):
        document["revision"] += 1
        document["run"]["updated_at"] = now
        document["run"]["status"] = RunStore._status(document)

    @staticmethod
    def _check_revision(document, expected_revision):
        if type(expected_revision) is not int or expected_revision != document["revision"]:
            raise RunStoreError("run_store_revision_conflict")

    def control(self, operation, expected_revision, **kwargs):
        """CAS controls; plans cannot change authority and rebind requires current evidence."""
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            self._check_revision(document, expected_revision)
            controls = document["controls"]
            now = self._now()
            if operation == "pause":
                if kwargs:
                    raise RunStoreError("run_store_invalid_control")
                controls["paused"] = True
                controls["reason"] = "operator_paused"
            elif operation == "resume":
                if kwargs:
                    raise RunStoreError("run_store_invalid_control")
                if controls["stopped"] or controls["completed"]:
                    raise RunStoreError("run_store_not_resumable")
                if controls["blocked_unknown"] or controls["world_mismatch"] or controls["awaiting_user"]:
                    raise RunStoreError("run_store_blocked")
                controls["paused"] = False
                controls["reason"] = None
            elif operation == "stop":
                if kwargs:
                    raise RunStoreError("run_store_invalid_control")
                controls["stopped"] = True
                controls["paused"] = False
                controls["reason"] = "operator_stopped"
                dispatch = controls["dispatch"]
                dispatch["revoked"] = True
            elif operation == "amend":
                if set(kwargs) - {"limits", "allowed_actions"} or not kwargs:
                    raise RunStoreError("run_store_invalid_amendment")
                authorization = document["authorization"]
                new_limits = kwargs.get("limits", authorization["limits"])
                new_actions = self._normalize_actions(kwargs.get("allowed_actions", authorization["allowed_actions"]))
                if not _valid_limits(new_limits):
                    raise RunStoreError("run_store_invalid_amendment")
                authorization["version"] += 1
                authorization["limits"] = dict(new_limits)
                authorization["allowed_actions"] = new_actions
            elif operation == "plan":
                if set(kwargs) - {"text", "source"} or "text" not in kwargs:
                    raise RunStoreError("run_store_invalid_plan")
                text = kwargs["text"]
                source = kwargs.get("source", "agent")
                if not _is_text(text, MAX_TEXT, 0) or source not in {"agent", "user"}:
                    raise RunStoreError("run_store_invalid_plan")
                document["plan"] = {"text": text, "updated_at": now, "source": source}
            elif operation == "confirm_inspection":
                if set(kwargs) != {"state", "session_id", "instruction"}:
                    raise RunStoreError("run_store_invalid_inspection_confirmation")
                state = kwargs["state"]
                session_id = kwargs["session_id"]
                instruction = kwargs["instruction"]
                if (not valid_snapshot_v4(state) or not _is_text(session_id, 128)
                        or not _is_text(instruction, MAX_TEXT) or not instruction.strip()):
                    raise RunStoreError("run_store_invalid_inspection_confirmation")
                if (state["world_epoch"] != document["binding"]["world_epoch"]
                        or session_id != document["binding"]["session_id"]):
                    raise RunStoreError("run_store_inspection_confirmation_binding_mismatch")
                handoff = self._waiting_inspection_handoff(state)
                if handoff is None:
                    raise RunStoreError("run_store_inspection_handoff_not_waiting")
                previous_confirmation = document.get("operator_confirmation")
                if previous_confirmation is not None and previous_confirmation["command_id"] is not None:
                    raise RunStoreError("run_store_inspection_confirmation_consumed")
                item_refs = [item["item_ref"] for item in handoff["items"]]
                document["operator_confirmation"] = {
                    "world_epoch": state["world_epoch"], "session_id": session_id,
                    "customer_ref": handoff["customer_ref"], "handoff_ref": handoff["handoff_ref"],
                    "item_refs": item_refs, "instruction": instruction, "command_id": None,
                }
                if controls["awaiting_user"] and controls["reason"] == "manual_inspection_required":
                    controls["awaiting_user"] = False
                    controls["reason"] = None
                decision = self._current_decision(state)
                event = self._find_event(document, decision["dedupe_key"]) if decision is not None else None
                if event is not None and event["kind"] == "manual" and event["status"] == "awaiting_user":
                    event["status"] = "pending"
                    event["reason"] = "operator_confirmation_recorded"
            elif operation == "rebind":
                if set(kwargs) != {"world_epoch", "session_id", "day_number"}:
                    raise RunStoreError("run_store_invalid_rebind")
                world_epoch = kwargs["world_epoch"]
                session_id = kwargs["session_id"]
                day_number = kwargs["day_number"]
                if (not _is_text(world_epoch, 128) or not _is_text(session_id, 128)
                        or not _is_int(day_number)):
                    raise RunStoreError("run_store_invalid_rebind")
                if controls["blocked_unknown"]:
                    raise RunStoreError("run_store_blocked_unknown_permanent")
                if controls["completed"]:
                    raise RunStoreError("run_store_not_resumable")
                document["run"]["world_epoch"] = world_epoch
                document["run"]["session_id"] = session_id
                document["binding"] = {"world_epoch": world_epoch, "session_id": session_id,
                                        "broker_epoch": document["binding"]["broker_epoch"],
                                        "day_number": day_number, "seen_at": now}
                document["operator_confirmation"] = None
                controls["world_mismatch"] = False
                controls["awaiting_user"] = False
                controls["disconnected"] = False
                controls["reason"] = "operator_rebound"
                for event in document["inbox"]:
                    if event["status"] == "pending" and event["world_epoch"] != world_epoch:
                        event["status"] = "superseded"
                        event["reason"] = "world_rebound"
                document["completion_evidence"] = None
                controls["completed"] = False
            else:
                raise RunStoreError("run_store_invalid_control")
            # 旧 dispatch 不跨越暂停、恢复、授权变更或显式重绑定。
            if operation != "plan" and controls["dispatch"]["token_hash"] is not None:
                controls["dispatch"]["revoked"] = True
            self._touch(document, now)
            self._atomic_replace(document)
            return copy.deepcopy(document)

    def grant(self, owner, ttl_seconds=60):
        if not _is_text(owner, 128) or type(ttl_seconds) not in (int, float) or isinstance(ttl_seconds, bool):
            raise RunStoreError("run_store_invalid_dispatch")
        if not 0 < float(ttl_seconds) <= 3600:
            raise RunStoreError("run_store_invalid_dispatch")
        now = self._now()
        token = secrets.token_urlsafe(32)
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            if self._status(document) != "running":
                raise RunStoreError("run_store_dispatch_blocked")
            if document["binding"]["broker_epoch"] is None:
                raise RunStoreError("run_store_broker_not_bound")
            if document["budget"]["writes"] >= document["authorization"]["limits"]["writes"]:
                raise RunStoreError("run_store_budget_exceeded")
            if any(command["reservation_status"] in {"reserved", "unknown"}
                   and command["journal_status"] not in {"succeeded", "rejected", "failed"}
                   for command in document["commands"]):
                raise RunStoreError("run_store_command_in_flight")
            dispatch = document["controls"]["dispatch"]
            if (dispatch["token_hash"] is not None and dispatch["command_id"] is None
                    and not dispatch["revoked"] and dispatch["expires_at"] > now):
                raise RunStoreError("run_store_dispatch_exists")
            document["controls"]["dispatch"] = {
                "token_hash": hashlib.sha256(token.encode("ascii")).hexdigest(),
                "owner": owner, "expires_at": now + int(float(ttl_seconds) * 1000),
                "revoked": False, "command_id": None,
                "authorization_version": document["authorization"]["version"],
                "world_epoch": document["binding"]["world_epoch"],
                "session_id": document["binding"]["session_id"],
                "broker_epoch": document["binding"]["broker_epoch"],
            }
            self._touch(document, now)
            self._atomic_replace(document)
        return token

    def revoke(self, token):
        if type(token) is not str or not token:
            raise RunStoreError("run_store_invalid_dispatch_token")
        digest = hashlib.sha256(token.encode("utf-8")).hexdigest()
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            dispatch = document["controls"]["dispatch"]
            if dispatch["token_hash"] is None or not secrets.compare_digest(dispatch["token_hash"], digest):
                raise RunStoreError("run_store_dispatch_token_mismatch")
            if not dispatch["revoked"]:
                dispatch["revoked"] = True
                now = self._now()
                self._touch(document, now)
                self._atomic_replace(document)
            return copy.deepcopy(document)

    @staticmethod
    def _journal_document(records, broker_epoch):
        if type(records) is dict:
            if set(records) == {"journal_format_version", "broker_epoch", "records"}:
                value = records
                if value.get("broker_epoch") != broker_epoch:
                    raise RunStoreError("run_store_invalid_journal")
            else:
                # Broker keeps its already validated journal rows in an ID-indexed map.
                if any(type(key) is not str or type(record) is not dict
                       or record.get("command_id") != key for key, record in records.items()):
                    raise RunStoreError("run_store_invalid_journal")
                value = {"journal_format_version": 2, "broker_epoch": broker_epoch,
                         "records": list(records.values())}
        elif type(records) is list:
            value = {"journal_format_version": 2, "broker_epoch": broker_epoch, "records": records}
        else:
            raise RunStoreError("run_store_invalid_journal")
        try:
            size = len(json.dumps(value, separators=(",", ":"), ensure_ascii=False,
                                allow_nan=False).encode("utf-8"))
        except (TypeError, ValueError, UnicodeError, RecursionError) as exc:
            raise RunStoreError("run_store_invalid_journal") from exc
        if size > 16 * 1024 * 1024 or not Journal.valid_v2(value):
            raise RunStoreError("run_store_invalid_journal")
        return value

    @staticmethod
    def _record_index(journal):
        return {record["command_id"]: record for record in journal["records"]}

    @staticmethod
    def _hash(value: Any) -> str:
        raw = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                         allow_nan=False).encode("utf-8")
        return hashlib.sha256(raw).hexdigest()

    @staticmethod
    def _semantic_arguments(state, action, arguments):
        """忽略临时 surface/control 引用，但保留商品、报价及其他操作语义。"""
        result = {}
        customer = state.get("business", {}).get("details", {}).get("customer", {})
        options = customer.get("options", []) if type(customer) is dict else []
        option = next((item for item in options if type(item) is dict
                       and item.get("option_ref") == arguments.get("option_ref")), None)
        if option is None:
            controls = state.get("surface", {}).get("controls", [])
            option = next((item for item in controls if type(item) is dict
                           and item.get("option_ref") == arguments.get("option_ref")), None)
        for key, value in arguments.items():
            if key in {"surface_ref", "control_ref", "customer_ref", "dialogue_ref"}:
                continue
            if key == "item_refs":
                result[key] = sorted(value) if type(value) is list else []
            elif key == "option_ref":
                if option is not None:
                    result["option"] = {name: option.get(name) for name in
                                         ("text", "kind", "price_change_percent", "mode", "effect_contract")
                                         if name in option}
                else:
                    result["option"] = {"ref": value}
            else:
                result[key] = value
        return {"action": action, "arguments": result}

    @classmethod
    def _current_decision(cls, state):
        business = state.get("business") if type(state) is dict else None
        details = business.get("details") if type(business) is dict else None
        customer = details.get("customer") if type(details) is dict else None
        customer_ref = business.get("customer_ref") if type(business) is dict else None
        dialogue_ref = customer.get("dialogue_ref") if type(customer) is dict else None
        day_state = details.get("day") if type(details) is dict else None
        day_number = day_state.get("day_number") if type(day_state) is dict else None
        surface = state.get("surface") or {}
        legal = state.get("legal_actions", [])
        surface_status = surface.get("status")
        handoff = details.get("inspection_handoff") if type(details) is dict else None
        if (type(handoff) is dict and handoff.get("requires_manual_inspection") is True
                and handoff.get("status") == "waiting_for_user") or surface_status == "awaiting_user":
            kind, status, phase = "manual", "awaiting_user", "inspection"
            semantics = {"kind": kind, "phase": phase}
            if (type(handoff) is dict and handoff.get("requires_manual_inspection") is True
                    and handoff.get("status") == "waiting_for_user"):
                semantics["handoff_ref"] = handoff["handoff_ref"]
                semantics["item_refs"] = sorted(item["item_ref"] for item in handoff["items"])
        elif surface_status == "unsupported" or surface.get("phase") == "unsupported":
            kind, status, phase = "unsupported", "unsupported", surface.get("phase", "unsupported")
            semantics = {"kind": kind, "phase": phase, "reason": surface.get("reason")}
        elif legal:
            kind, status, phase = "decision", "pending", surface.get("phase", "")
            actions = [cls._semantic_arguments(state, entry["action"], entry["arguments"])
                       for entry in legal if type(entry) is dict and type(entry.get("arguments")) is dict]
            actions.sort(key=lambda item: json.dumps(item, sort_keys=True, ensure_ascii=False, separators=(",", ":")))
            semantics = {"kind": kind, "phase": phase, "actions": actions}
            if type(customer) is dict:
                options = customer.get("options", [])
                semantics["options"] = sorted(
                    [{"text": option.get("text", ""), "kind": option.get("kind"),
                      "price_change_percent": option.get("price_change_percent")}
                     for option in options if type(option) is dict],
                    key=lambda item: json.dumps(item, sort_keys=True, ensure_ascii=False, separators=(",", ":")))
                semantics["trade_type"] = customer.get("trade_type")
        else:
            return None
        world = state["world_epoch"]
        customer_key = cls._hash({"world": world, "customer_ref": customer_ref}) if customer_ref else None
        dialogue_key = cls._hash({"world": world, "customer_ref": customer_ref, "dialogue_ref": dialogue_ref}) \
            if dialogue_ref else None
        semantic_fingerprint = cls._hash(semantics)
        dedupe_key = cls._hash({"world": world, "customer": customer_key, "dialogue": dialogue_key,
                                "day_number": day_number, "phase": phase,
                                "semantics": semantic_fingerprint})
        return {"dedupe_key": dedupe_key, "kind": kind, "status": status, "world_epoch": world,
                "customer_key": customer_key, "dialogue_key": dialogue_key, "phase": phase,
                "day_number": day_number, "occurrence": 1,
                "semantic_fingerprint": semantic_fingerprint}

    @staticmethod
    def _find_event(document, dedupe_key):
        candidates = [event for event in document["inbox"] if event["dedupe_key"] == dedupe_key]
        return max(candidates, key=lambda event: event["occurrence"]) if candidates else None

    @staticmethod
    def _find_command(document, command_id):
        return next((entry for entry in document["commands"] if entry["command_id"] == command_id), None)

    @classmethod
    def _event_has_inflight_command(cls, document, event):
        if event["related_command_id"] is None:
            return False
        command = cls._find_command(document, event["related_command_id"])
        return (command is not None and command["reservation_status"] == "reserved"
                and command["journal_status"] in {None, "pending", "running"})

    @staticmethod
    def _waiting_inspection_handoff(state):
        if type(state) is not dict:
            return None
        details = state.get("business", {}).get("details", {})
        handoff = details.get("inspection_handoff") if type(details) is dict else None
        if (type(handoff) is not dict or handoff.get("requires_manual_inspection") is not True
                or handoff.get("status") != "waiting_for_user"):
            return None
        return handoff

    @classmethod
    def _operator_confirmation_matches(cls, document, state, session_id):
        confirmation = document.get("operator_confirmation")
        handoff = cls._waiting_inspection_handoff(state)
        if confirmation is None or handoff is None:
            return False
        item_refs = [item["item_ref"] for item in handoff["items"]]
        return (confirmation["world_epoch"] == state["world_epoch"]
                == document["binding"]["world_epoch"]
                and confirmation["session_id"] == session_id == document["binding"]["session_id"]
                and confirmation["customer_ref"] == handoff["customer_ref"]
                and confirmation["handoff_ref"] == handoff["handoff_ref"]
                and len(confirmation["item_refs"]) == len(item_refs)
                and set(confirmation["item_refs"]) == set(item_refs))

    @classmethod
    def _confirmation_allows_current_handoff(cls, document, state, session_id):
        if not cls._operator_confirmation_matches(document, state, session_id):
            return False
        confirmation = document["operator_confirmation"]
        if confirmation["command_id"] is None:
            return True
        command = cls._find_command(document, confirmation["command_id"])
        return (command is not None and command["action"] == "resume_inspection_handoff"
                and command["reservation_status"] == "reserved"
                and command["journal_status"] in {None, "pending", "running"})

    @staticmethod
    def _evidence_summary(record):
        result = record.get("result")
        if type(result) is not dict:
            return None
        after = result.get("after")
        world = after.get("world_epoch") if type(after) is dict else record["command"].get("expected_world_epoch")
        revision = after.get("revision") if type(after) is dict else record["command"].get("expected_revision")
        digest = hashlib.sha256(json.dumps(result, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                                          allow_nan=False).encode("utf-8")).hexdigest()
        return {"result_status": record.get("command_status"), "world_epoch": world,
                "revision": revision, "evidence_hash": digest}

    @staticmethod
    def _history_for(command, evidence, now):
        return {"command_id": command["command_id"], "action": command["action"],
                "world_epoch": evidence["world_epoch"], "status": "succeeded", "confirmed_at": now,
                "evidence_hash": evidence["evidence_hash"]}

    def _reconcile_records(self, document, journal, now):
        by_id = self._record_index(journal)
        commands = {entry["command_id"]: entry for entry in document["commands"]}
        changed = False
        for command_id, entry in commands.items():
            if entry["reservation_status"] == "unknown" or entry["journal_status"] == "outcome_unknown":
                document["controls"]["blocked_unknown"] = True
                continue
            record = by_id.get(command_id)
            if entry["reservation_status"] == "rejected_prequeue":
                if record is not None:
                    entry["reservation_status"] = "unknown"
                    entry["journal_status"] = "outcome_unknown"
                    entry["reason"] = "prequeue_rejection_has_journal_record"
                    document["controls"]["blocked_unknown"] = True
                    changed = True
                continue
            if record is None:
                if entry["reservation_status"] != "unknown":
                    entry["reservation_status"] = "unknown"
                    entry["journal_status"] = "outcome_unknown"
                    entry["reason"] = "reservation_missing_from_journal"
                    entry["updated_at"] = now
                    document["controls"]["blocked_unknown"] = True
                    document["controls"]["reason"] = "reservation_missing_from_journal"
                    self._block_event(document, entry, "reservation_missing_from_journal")
                    changed = True
                continue
            command = record["command"]
            if (record["request_fingerprint"] != entry["request_fingerprint"]
                    or command.get("action") != entry["action"]
                    or command.get("expected_world_epoch") != entry["expected_world_epoch"]
                    or command.get("expected_revision") != entry["expected_revision"]
                    or command.get("broker_epoch") != entry["journal_broker_epoch"]
                    or (record.get("delivery_session_id") is not None
                        and record["delivery_session_id"] != entry["session_id"])):
                entry["reservation_status"] = "unknown"
                entry["journal_status"] = "outcome_unknown"
                entry["reason"] = "journal_reservation_mismatch"
                entry["updated_at"] = now
                document["controls"]["blocked_unknown"] = True
                document["controls"]["reason"] = "journal_reservation_mismatch"
                self._block_event(document, entry, "journal_reservation_mismatch")
                changed = True
                continue
            status = record["command_status"]
            evidence = self._evidence_summary(record)
            delivered = record.get("delivery_session_id") is not None
            if (entry["journal_status"] != status or entry["delivered"] != delivered
                    or entry["evidence"] != evidence or entry["reservation_status"] != "reserved"):
                entry["journal_status"] = status
                entry["delivered"] = delivered
                entry["evidence"] = evidence
                entry["journal_broker_epoch"] = command.get("broker_epoch")
                entry["reservation_status"] = "reserved"
                entry["updated_at"] = now
                changed = True
            if status == "outcome_unknown":
                entry["reservation_status"] = "unknown"
                entry["reason"] = "journal_outcome_unknown"
                entry["updated_at"] = now
                document["controls"]["blocked_unknown"] = True
                document["controls"]["reason"] = "journal_outcome_unknown"
                self._block_event(document, entry, "journal_outcome_unknown")
                changed = True
            elif status == "succeeded" and evidence is not None:
                if not any(item["command_id"] == command_id for item in document["confirmed_history"]):
                    if len(document["confirmed_history"]) >= MAX_HISTORY:
                        raise RunStoreError("run_store_history_limit")
                    document["confirmed_history"].append(self._history_for(entry, evidence, now))
                    changed = True
                self._resolve_event(document, entry, evidence)
            elif status in {"rejected", "failed"}:
                event = self._event_for_command(document, entry)
                if event is not None and event["status"] == "submitted":
                    event["status"] = "pending"
                    event["reason"] = status
                    changed = True
        for command_id, record in by_id.items():
            if command_id in commands:
                continue
            command = record["command"]
            if command.get("action") not in _WRITE_ACTIONS:
                continue
            if len(document["commands"]) >= MAX_COMMANDS:
                raise RunStoreError("run_store_command_limit")
            entry = {
                "command_id": command_id,
                "request_fingerprint": record["request_fingerprint"],
                "action": command["action"], "expected_world_epoch": command.get("expected_world_epoch", "unknown"),
                "expected_revision": command.get("expected_revision", 0),
                "session_id": record.get("delivery_session_id") or document["binding"]["session_id"],
                "reservation_status": "unknown", "journal_status": "outcome_unknown",
                "budget_cost": {key: 0 for key in _BUDGET_KEYS},
                "created_at": record["created_at_unix_ms"], "updated_at": now,
                "related_event_id": None, "reason": "journal_command_without_run_reservation",
                "journal_broker_epoch": command.get("broker_epoch"),
                "delivered": record.get("delivery_session_id") is not None,
                "evidence": self._evidence_summary(record),
            }
            # Unmatched terminal success remains untrusted as managed-run success.
            document["commands"].append(entry)
            document["controls"]["blocked_unknown"] = True
            document["controls"]["reason"] = "journal_command_without_run_reservation"
            changed = True
        return changed

    @staticmethod
    def _event_for_command(document, command):
        if command["related_event_id"] is None:
            return None
        return next((event for event in document["inbox"]
                     if event["event_id"] == command["related_event_id"]
                     and event["related_command_id"] == command["command_id"]), None)

    def _block_event(self, document, command, reason):
        event = self._event_for_command(document, command)
        if event is None:
            return
        event["status"] = "blocked_unknown"
        event["reason"] = reason
        event["related_command_id"] = command["command_id"]

    def _resolve_event(self, document, command, evidence):
        event = self._event_for_command(document, command)
        if (event is None or event["related_command_id"] != command["command_id"]
                or command["journal_status"] != "succeeded"):
            return
        event["status"] = "resolved"
        event["reason"] = None
        event["related_command_id"] = command["command_id"]
        event["resolution_evidence"] = {
            "command_id": command["command_id"], "status": "succeeded",
            "world_epoch": evidence["world_epoch"], "revision": evidence["revision"],
            "evidence_hash": evidence["evidence_hash"],
        }

    @staticmethod
    def _without_sync_churn(document):
        normalized = copy.deepcopy(document)
        normalized["run"]["updated_at"] = 0
        normalized["binding"]["seen_at"] = 0
        for event in normalized["inbox"]:
            event["last_seen"] = 0
        return normalized

    def sync(self, state, records, session_id, connected, broker_epoch):
        """Refresh decision/evidence state from the actual Broker snapshot and journal."""
        if type(connected) is not bool or not _is_text(broker_epoch, 128):
            raise RunStoreError("run_store_invalid_sync")
        if connected and (not valid_snapshot_v4(state) or not _is_text(session_id, 128)):
            raise RunStoreError("run_store_invalid_snapshot")
        journal = self._journal_document(records, broker_epoch)
        now = self._now()
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            bound_broker = document["binding"]["broker_epoch"]
            if bound_broker is not None and bound_broker != broker_epoch:
                raise RunStoreError("run_store_broker_recovery_required")
            before = copy.deepcopy(document)
            reconciled = self._reconcile_records(document, journal, now)
            controls = document["controls"]
            if not connected:
                controls["disconnected"] = True
                if not controls["blocked_unknown"] and not controls["world_mismatch"] and not controls["awaiting_user"]:
                    controls["reason"] = "broker_disconnected"
            else:
                controls["disconnected"] = False
                expected_world = document["binding"]["world_epoch"]
                if state["world_epoch"] != expected_world:
                    controls["world_mismatch"] = True
                    controls["reason"] = "world_epoch_changed"
                elif session_id != document["binding"]["session_id"]:
                    controls["awaiting_user"] = True
                    controls["reason"] = "mod_session_changed"
                elif (not controls["world_mismatch"]
                      and (not controls["awaiting_user"] or controls["reason"] == "manual_inspection_required")):
                    document["binding"]["broker_epoch"] = broker_epoch
                    document["binding"]["seen_at"] = now
                    document["run"]["session_id"] = session_id
                    current_day = state["business"].get("details", {}).get("day")
                    document["binding"]["day_number"] = current_day.get("day_number") if type(current_day) is dict else None
                    if document["run"]["world_epoch"] != state["world_epoch"]:
                        controls["world_mismatch"] = True
                        controls["reason"] = "world_epoch_changed"
                    else:
                        current = self._current_decision(state)
                        day_state = state["business"].get("details", {}).get("day")
                        observed_day = day_state.get("day_number") if type(day_state) is dict else None
                        past_target = type(observed_day) is int and observed_day > document["run"]["target_day"]
                        before_start = type(observed_day) is int and observed_day < document["run"]["start_day"]
                        if controls["reason"] in {"target_day_exceeded", "day_before_authorized_range"}:
                            controls["unsupported"] = False
                            controls["awaiting_user"] = False
                            controls["reason"] = None
                        if past_target or before_start:
                            current = None
                        if (type(day_state) is dict
                                and day_state.get("day_number") == document["run"]["target_day"]
                                and state["phase"] == "MORNING" and day_state.get("report_open") is False
                                and not past_target and not before_start
                                and state["legal_actions"]
                                and any(entry["action"] == "start_business" for entry in state["legal_actions"])
                                and all(entry["action"] in {"start_business", "sort_inventory_default"}
                                        for entry in state["legal_actions"])):
                            # Run 完成在目标次日晨报关闭处；不把下一日开店当成本任务待办。
                            current = None
                        confirmation = document.get("operator_confirmation")
                        if (confirmation is not None and confirmation["command_id"] is None
                                and not self._operator_confirmation_matches(document, state, session_id)):
                            # 尚未消费的确认只对当下这一条handoff有效；handoff消失或变化后作废。
                            document["operator_confirmation"] = None
                        if current is not None:
                            event = self._find_event(document, current["dedupe_key"])
                            if event is None or event["status"] in {"resolved", "superseded"}:
                                occurrence = max((item["occurrence"] for item in document["inbox"]
                                                  if item["dedupe_key"] == current["dedupe_key"]), default=0) + 1
                                current["occurrence"] = occurrence
                                event_id = "evt-" + self._hash({"dedupe_key": current["dedupe_key"],
                                                               "occurrence": occurrence})[:32]
                                if len(document["inbox"]) >= MAX_INBOX:
                                    removable = next((item for item in document["inbox"]
                                                      if item["status"] in {"resolved", "superseded"}), None)
                                    if removable is None:
                                        raise RunStoreError("run_store_inbox_limit")
                                    document["inbox"].remove(removable)
                                event = {"event_id": event_id, **current, "first_seen": now, "last_seen": now,
                                         "related_command_id": None, "reason": None,
                                         "resolution_evidence": None, "superseded_by": None}
                                document["inbox"].append(event)
                            else:
                                event["last_seen"] = now
                                if event["status"] == "unsupported" and current["status"] == "pending":
                                    event["status"] = "pending"
                            for old_event in document["inbox"]:
                                stale_without_flight = (
                                    old_event["status"] == "awaiting_user" and old_event["kind"] == "manual"
                                    or old_event["status"] == "unsupported"
                                    and old_event["kind"] == "unsupported")
                                if (old_event["event_id"] != event["event_id"]
                                        and old_event["world_epoch"] == current["world_epoch"]
                                        and (old_event["status"] == "pending"
                                             or (stale_without_flight
                                                 and not self._event_has_inflight_command(document, old_event)))):
                                    old_event["status"] = "superseded"
                                    old_event["reason"] = "new_decision_observed"
                                    old_event["superseded_by"] = event["event_id"]
                            if current["status"] == "awaiting_user":
                                if self._confirmation_allows_current_handoff(document, state, session_id):
                                    if (controls["awaiting_user"]
                                            and controls["reason"] == "manual_inspection_required"):
                                        controls["awaiting_user"] = False
                                        controls["reason"] = None
                                    if event["kind"] == "manual" and event["status"] == "awaiting_user":
                                        event["status"] = "pending"
                                        event["reason"] = "operator_confirmation_recorded"
                                else:
                                    controls["awaiting_user"] = True
                                    controls["reason"] = "manual_inspection_required"
                                    controls["unsupported"] = False
                                    if event["kind"] == "manual":
                                        event["status"] = "awaiting_user"
                                        event["reason"] = "manual_inspection_required"
                            elif current["status"] == "unsupported":
                                if (controls["awaiting_user"]
                                        and controls["reason"] == "manual_inspection_required"
                                        and self._waiting_inspection_handoff(state) is None):
                                    # 真实人工交接已消失时，切换到 unsupported 要清掉旧人工等待门禁。
                                    controls["awaiting_user"] = False
                                event["status"] = "unsupported"
                                event["reason"] = "unsupported_interaction"
                                controls["unsupported"] = True
                                controls["reason"] = "unsupported_interaction"
                            else:
                                controls["unsupported"] = False
                        else:
                            # 已提交事项在消失时仍保留；手动接管仅失效，不记为 Agent 成功。
                            for event in document["inbox"]:
                                if event["world_epoch"] == state["world_epoch"] and event["status"] == "pending":
                                    event["status"] = "superseded"
                                    event["reason"] = "decision_no_longer_visible"
                                    event["superseded_by"] = None
                                elif (event["world_epoch"] == state["world_epoch"]
                                      and event["kind"] == "manual" and event["status"] == "awaiting_user"
                                      and not self._event_has_inflight_command(document, event)):
                                    event["status"] = "superseded"
                                    event["reason"] = "manual_state_changed"
                                    event["superseded_by"] = None
                                elif (event["world_epoch"] == state["world_epoch"]
                                      and event["kind"] == "unsupported" and event["status"] == "unsupported"
                                      and not self._event_has_inflight_command(document, event)):
                                    event["status"] = "superseded"
                                    event["reason"] = "unsupported_surface_changed"
                                    event["superseded_by"] = None
                            controls["unsupported"] = False
                        handoff = state["business"].get("details", {}).get("inspection_handoff")
                        waiting = (type(handoff) is dict and handoff.get("requires_manual_inspection") is True
                                   and handoff.get("status") == "waiting_for_user")
                        if not waiting and controls["awaiting_user"] and controls["reason"] == "manual_inspection_required":
                            controls["awaiting_user"] = False
                            controls["reason"] = None
                        if past_target:
                            controls["unsupported"] = True
                            controls["reason"] = "target_day_exceeded"
                        elif before_start:
                            controls["awaiting_user"] = True
                            controls["reason"] = "day_before_authorized_range"
                        customer_ref = state["business"].get("customer_ref")
                        if type(customer_ref) is str:
                            customer_key = self._hash({"world": state["world_epoch"], "customer_ref": customer_ref})
                            if customer_key not in document["customer_keys"] and len(document["customer_keys"]) < MAX_INBOX:
                                document["customer_keys"].append(customer_key)
            # A reserved request with no Journal row is ambiguous after this sync and is never replayed.
            if (controls["blocked_unknown"] or controls["world_mismatch"] or controls["awaiting_user"]
                    or controls["unsupported"] or controls["disconnected"]):
                if controls["dispatch"]["token_hash"] is not None:
                    controls["dispatch"]["revoked"] = True
            doc_changed = (self._without_sync_churn(document) != self._without_sync_churn(before))
            if not doc_changed and not reconciled and now - before["binding"]["seen_at"] < 1000:
                return copy.deepcopy(before)
            if doc_changed or reconciled or now - before["binding"]["seen_at"] >= 1000:
                self._touch(document, now)
                self._atomic_replace(document)
            return copy.deepcopy(document)

    @staticmethod
    def _action_cost(request, state, document):
        action = request["action"]
        arguments = request["arguments"]
        costs = {key: 0 for key in _BUDGET_KEYS}
        costs["writes"] = 1
        if action == "receive_next_customer":
            costs["customers"] = 1
        if action in {"purchase_visible_offer", "sell_visible_offer"}:
            costs["trades"] = 1
        if action == "purchase_visible_offer":
            amount = arguments.get("max_spend", arguments.get("amount"))
            costs["buy_spend"] = amount if _is_int(amount) else 2_147_483_647
        if action == "choose_bargain_option":
            costs["bargains"] = 1
        if action == "confirm_end_of_day":
            fee = arguments.get("max_spend", arguments.get("amount"))
            costs["day_fees"] = fee if _is_int(fee) else 2_147_483_647
            costs["days"] = 1
        return costs

    def _make_attempt(self, document, request, state, session_id, now, fingerprint):
        command_id = request["id"]
        existing = self._find_command(document, command_id)
        if existing is not None:
            if existing["request_fingerprint"] == fingerprint:
                raise RunStoreError("run_store_command_already_reserved")
            raise RunStoreError("run_store_command_id_reused")
        if len(document["commands"]) >= MAX_COMMANDS:
            raise RunStoreError("run_store_command_limit")
        cost = self._action_cost(request, state, document)
        document["budget"]["writes"] += 1
        entry = {"command_id": command_id, "request_fingerprint": fingerprint, "action": request["action"],
                 "expected_world_epoch": request["expected_world_epoch"],
                 "expected_revision": request["expected_revision"], "session_id": session_id,
                 "reservation_status": "rejected_prequeue", "journal_status": None,
                 "budget_cost": {key: 0 for key in _BUDGET_KEYS}, "created_at": now, "updated_at": now,
                 "related_event_id": None, "reason": "prequeue_attempt", "journal_broker_epoch": request["broker_epoch"],
                 "delivered": False, "evidence": None}
        entry["budget_cost"]["writes"] = 1
        document["commands"].append(entry)
        return entry, cost

    @staticmethod
    def _event_for_request(document, state, request):
        current = RunStore._current_decision(state)
        if current is None:
            return None
        event = RunStore._find_event(document, current["dedupe_key"])
        if event is not None and event["status"] in {"pending", "awaiting_user"}:
            return event
        return None

    def reserve(self, request, state, records, session_id, dispatch_token):
        """Consume a dispatch and durably reserve budget before the caller can enqueue."""
        if not valid_request_v4(request):
            raise RunStoreError("run_store_invalid_request")
        if not valid_snapshot_v4(state) or not _is_text(session_id, 128):
            raise RunStoreError("run_store_invalid_snapshot")
        if type(dispatch_token) is not str or not dispatch_token:
            raise RunStoreError("run_store_invalid_dispatch_token")
        journal = self._journal_document(records, request["broker_epoch"])
        fingerprint = request_fingerprint(request)
        now = self._now()
        token_hash = hashlib.sha256(dispatch_token.encode("utf-8")).hexdigest()
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            dispatch = document["controls"]["dispatch"]
            if (dispatch["token_hash"] is None or not secrets.compare_digest(dispatch["token_hash"], token_hash)
                    or dispatch["revoked"] or dispatch["expires_at"] <= now
                    or dispatch["command_id"] is not None):
                raise RunStoreError("run_store_dispatch_token_invalid")
            entry, requested_cost = self._make_attempt(document, request, state, session_id, now, fingerprint)
            dispatch["command_id"] = request["id"]
            failure = None
            if request["action"] == "resume_inspection_handoff":
                confirmation = document.get("operator_confirmation")
                handoff = self._waiting_inspection_handoff(state)
                if (confirmation is not None and confirmation["command_id"] is None
                        and self._operator_confirmation_matches(document, state, session_id)
                        and handoff is not None
                        and request["arguments"] == {"handoff_ref": handoff["handoff_ref"]}):
                    # 从第一个匹配handoff的命令尝试起消费确认；预入队拒绝也不能换ID重试。
                    confirmation["command_id"] = request["id"]
            if self._status(document) != "running":
                failure = "run_store_dispatch_blocked"
            elif session_id != document["binding"]["session_id"] or state["world_epoch"] != document["binding"]["world_epoch"]:
                failure = "run_store_world_or_session_mismatch"
            elif (dispatch["authorization_version"] != document["authorization"]["version"]
                  or dispatch["world_epoch"] != document["binding"]["world_epoch"]
                  or dispatch["session_id"] != session_id
                  or dispatch["broker_epoch"] != document["binding"]["broker_epoch"]):
                failure = "run_store_dispatch_binding_mismatch"
            elif request["broker_epoch"] != document["binding"]["broker_epoch"]:
                failure = "run_store_broker_epoch_mismatch"
            elif (request["expected_world_epoch"] != state["world_epoch"]
                  or request["expected_revision"] != state["revision"]
                  or request["expected_version"] != state["version"]):
                failure = "run_store_stale_snapshot"
            elif (request["action"] == "resume_inspection_handoff"
                  and (confirmation is None or confirmation["command_id"] != request["id"]
                       or not self._operator_confirmation_matches(document, state, session_id))):
                failure = "run_store_inspection_confirmation_required"
            elif (request["action"] != "resume_inspection_handoff"
                  and (request["action"] in _FORBIDDEN_ACTIONS
                       or request["action"] not in document["authorization"]["allowed_actions"])):
                failure = "run_store_action_not_authorized"
            elif not any(entry_value["action"] == request["action"]
                         and entry_value["arguments"] == request["arguments"] for entry_value in state["legal_actions"]):
                failure = "run_store_action_not_legal"
            elif any(command["reservation_status"] == "unknown"
                     or command["journal_status"] == "outcome_unknown" for command in document["commands"]):
                failure = "run_store_blocked_unknown"
            elif any(command["reservation_status"] == "reserved"
                     and command["journal_status"] not in {"succeeded", "rejected", "failed"}
                     for command in document["commands"] if command["command_id"] != request["id"]):
                failure = "run_store_command_in_flight"
            elif (self._waiting_inspection_handoff(state) is not None
                  and request["action"] != "resume_inspection_handoff"):
                failure = "run_store_manual_inspection_required"
            elif request["id"] in self._record_index(journal):
                failure = "run_store_command_id_in_journal"
            else:
                day = state["business"].get("details", {}).get("day")
                if type(day) is not dict or type(day.get("day_number")) is not int:
                    failure = "run_store_day_unverified"
                elif day["day_number"] < document["run"]["start_day"]:
                    failure = "run_store_day_before_authorized_range"
                elif day["day_number"] > document["run"]["target_day"]:
                    failure = "run_store_day_after_target"
                elif (day["day_number"] == document["run"]["target_day"]
                      and request["action"] != "close_day_report"):
                    failure = "run_store_target_day_write_forbidden"
            if failure is None:
                limits = document["authorization"]["limits"]
                if (document["budget"]["writes"] > limits["writes"]
                        or any(document["budget"][key] + requested_cost[key] > limits[key]
                               for key in _BUDGET_KEYS if key != "writes")):
                    failure = "run_store_budget_exceeded"
            if failure is not None:
                entry["reason"] = failure
                entry["updated_at"] = now
                self._touch(document, now)
                self._atomic_replace(document)
                raise RunStoreError(failure)
            for key in _BUDGET_KEYS:
                if key != "writes":
                    document["budget"][key] += requested_cost[key]
                entry["budget_cost"][key] = requested_cost[key]
            entry["reservation_status"] = "reserved"
            entry["reason"] = None
            entry["updated_at"] = now
            event = self._event_for_request(document, state, request)
            if event is not None:
                event["status"] = "submitted"
                event["related_command_id"] = request["id"]
                event["reason"] = None
                entry["related_event_id"] = event["event_id"]
            customer_ref = request["arguments"].get("customer_ref")
            if customer_ref is not None:
                customer_key = self._hash({"world": state["world_epoch"], "customer_ref": customer_ref})
                if customer_key not in document["customer_keys"]:
                    if len(document["customer_keys"]) >= MAX_INBOX:
                        entry["reservation_status"] = "rejected_prequeue"
                        entry["reason"] = "run_store_customer_limit"
                        self._touch(document, now)
                        self._atomic_replace(document)
                        raise RunStoreError("run_store_customer_limit")
                    document["customer_keys"].append(customer_key)
            self._touch(document, now)
            self._atomic_replace(document)
            return copy.deepcopy(entry)

    def _read_journal_for_reject(self, document):
        journal_path = Path(document["run"]["journal_path"])
        try:
            if not journal_path.exists():
                raise RunStoreError("run_store_cannot_prove_not_enqueued")
            size = journal_path.stat().st_size
            if size > 16 * 1024 * 1024:
                raise RunStoreError("run_store_invalid_journal")
            raw = journal_path.read_bytes()
            if len(raw) > 16 * 1024 * 1024 or len(raw) != size:
                raise RunStoreError("run_store_invalid_journal")
            value = strict_json_loads(raw.decode("utf-8"))
            if not Journal.valid_v2(value):
                raise RunStoreError("run_store_invalid_journal")
            return value
        except RunStoreError:
            raise
        except (OSError, UnicodeError, ValueError, TypeError, RecursionError) as exc:
            raise RunStoreError("run_store_cannot_prove_not_enqueued") from exc

    def reject(self, command_id, reason):
        """Record a pre-enqueue refusal only when the configured Journal proves absence."""
        if not _is_text(command_id, 128) or not _is_text(reason, 512):
            raise RunStoreError("run_store_invalid_rejection")
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            entry = self._find_command(document, command_id)
            if entry is None:
                raise RunStoreError("run_store_reservation_not_found")
            journal = self._read_journal_for_reject(document)
            if any(record["command_id"] == command_id for record in journal["records"]):
                entry["reservation_status"] = "unknown"
                entry["journal_status"] = "outcome_unknown"
                entry["reason"] = "rejection_after_journal_enqueue"
                entry["updated_at"] = self._now()
                document["controls"]["blocked_unknown"] = True
                document["controls"]["reason"] = "rejection_after_journal_enqueue"
                self._block_event(document, entry, "rejection_after_journal_enqueue")
                self._touch(document, entry["updated_at"])
                self._atomic_replace(document)
                raise RunStoreError("run_store_cannot_prove_not_enqueued")
            if entry["reservation_status"] not in {"reserved", "rejected_prequeue"}:
                raise RunStoreError("run_store_reservation_not_rejectable")
            now = self._now()
            entry["reservation_status"] = "rejected_prequeue"
            entry["journal_status"] = None
            entry["reason"] = reason
            entry["updated_at"] = now
            event = self._event_for_command(document, entry)
            if event is not None and event["status"] == "submitted":
                event["status"] = "pending"
                event["reason"] = reason
                event["related_command_id"] = None
            self._touch(document, now)
            self._atomic_replace(document)
            return copy.deepcopy(entry)

    def delivery_allowed(self, command_id):
        if not _is_text(command_id, 128):
            return False
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            entry = self._find_command(document, command_id)
            return self._delivery_ready(document, command_id, entry, self._now())

    @staticmethod
    def _delivery_ready(document, command_id, entry, now):
        """检查命令交付条件；调用方必须仍持有 RunStore 文件锁。"""
        if entry is None or entry["reservation_status"] != "reserved":
            return False
        if entry["journal_status"] not in {None, "pending"}:
            return False
        controls = document["controls"]
        if (RunStore._status(document) != "running" or controls["paused"] or controls["stopped"]
                or controls["awaiting_user"] or controls["world_mismatch"] or controls["blocked_unknown"]
                or controls["disconnected"] or controls["unsupported"] or controls["completed"]):
            return False
        dispatch = controls["dispatch"]
        if (dispatch["token_hash"] is None or dispatch["revoked"] or dispatch["expires_at"] <= now
                or dispatch["command_id"] != command_id
                or dispatch["authorization_version"] != document["authorization"]["version"]
                or dispatch["world_epoch"] != document["binding"]["world_epoch"]
                or dispatch["session_id"] != entry["session_id"]
                or dispatch["broker_epoch"] != document["binding"]["broker_epoch"]):
            return False
        if entry["action"] == "resume_inspection_handoff":
            confirmation = document.get("operator_confirmation")
            if (confirmation is None or confirmation["command_id"] != command_id
                    or confirmation["world_epoch"] != entry["expected_world_epoch"]
                    or confirmation["session_id"] != entry["session_id"]):
                return False
        return True

    def deliver(self, command_id, commit_callback) -> bool:
        """在同一文件锁内完成授权复核与 Journal 持久交付，避免暂停竞态。"""
        if not _is_text(command_id, 128) or not callable(commit_callback):
            return False
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            entry = self._find_command(document, command_id)
            now = self._now()
            if not self._delivery_ready(document, command_id, entry, now):
                return False
            # 回调只提交 Broker/Journal 的 running 状态，不得再次调用 RunStore。
            # 如果回调失败，异常原样返回；调用方后续恢复会按 Journal 证据处理。
            commit_callback()
            entry["delivered"] = True
            entry["journal_status"] = "running"
            entry["updated_at"] = now
            self._touch(document, now)
            self._atomic_replace(document)
            return True

    def reconcile(self, records, broker_epoch=None):
        """Reconcile evidence and fail closed on every unpaired Journal write."""
        if broker_epoch is None:
            if type(records) is dict and set(records) == {"journal_format_version", "broker_epoch", "records"}:
                broker_epoch = records.get("broker_epoch")
            else:
                with _FileLock(self.lock_path):
                    broker_epoch = self._load_unlocked()["binding"]["broker_epoch"]
        if not _is_text(broker_epoch, 128):
            raise RunStoreError("run_store_invalid_journal")
        journal = self._journal_document(records, broker_epoch)
        now = self._now()
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            if document["binding"]["broker_epoch"] not in (None, broker_epoch):
                raise RunStoreError("run_store_broker_recovery_required")
            before = copy.deepcopy(document)
            self._reconcile_records(document, journal, now)
            if document != before:
                self._touch(document, now)
                self._atomic_replace(document)
            return copy.deepcopy(document)

    def recover(self, records, broker_epoch=None):
        """Bind the first Broker epoch or reconcile a Broker restart; never replays a command."""
        if broker_epoch is None and type(records) is dict \
                and set(records) == {"journal_format_version", "broker_epoch", "records"}:
            broker_epoch = records.get("broker_epoch")
        if broker_epoch is None:
            # Broker passes its in-memory record map; the epoch belongs to the journal
            # envelope, so read the atomically replaced file without acquiring Broker's
            # lifetime owner lock.
            with _FileLock(self.lock_path):
                document = self._load_unlocked()
                try:
                    path = Path(document["run"]["journal_path"])
                    size = path.stat().st_size
                    if size > 16 * 1024 * 1024:
                        raise RunStoreError("run_store_invalid_journal")
                    raw = path.read_bytes()
                    if len(raw) != size or len(raw) > 16 * 1024 * 1024:
                        raise RunStoreError("run_store_invalid_journal")
                    disk = strict_json_loads(raw.decode("utf-8"))
                    if not Journal.valid_v2(disk):
                        raise RunStoreError("run_store_invalid_journal")
                    broker_epoch = disk["broker_epoch"]
                    if type(records) is dict and set(records) != {"journal_format_version", "broker_epoch", "records"}:
                        if records != {record["command_id"]: record for record in disk["records"]}:
                            raise RunStoreError("run_store_invalid_journal")
                except RunStoreError:
                    raise
                except (OSError, UnicodeError, ValueError, TypeError, RecursionError) as exc:
                    raise RunStoreError("run_store_invalid_journal") from exc
        if not _is_text(broker_epoch, 128):
            raise RunStoreError("run_store_invalid_journal")
        journal = self._journal_document(records, broker_epoch)
        now = self._now()
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            previous = document["binding"]["broker_epoch"]
            before = copy.deepcopy(document)
            self._reconcile_records(document, journal, now)
            if previous != broker_epoch and document["controls"]["dispatch"]["token_hash"] is not None:
                document["controls"]["dispatch"]["revoked"] = True
            document["binding"]["broker_epoch"] = broker_epoch
            document["binding"]["seen_at"] = now
            if previous is None and not document["controls"]["blocked_unknown"]:
                document["controls"]["reason"] = "broker_epoch_bound"
            if document != before:
                self._touch(document, now)
                self._atomic_replace(document)
            return copy.deepcopy(document)

    @staticmethod
    def _successful_actions_by_day(journal, managed_commands=None):
        successful = []
        for record in journal["records"]:
            if record["command_status"] != "succeeded":
                continue
            command, result = record["command"], record["result"]
            if command.get("schema_version") != WIRE_VERSION_V4 or command.get("action") not in DAY_ACTIONS:
                continue
            if managed_commands is not None:
                managed = managed_commands.get(record["command_id"])
                if (managed is None or managed["reservation_status"] != "reserved"
                        or managed["journal_status"] != "succeeded"
                        or record.get("delivery_session_id") != managed["session_id"]
                        or record["request_fingerprint"] != managed["request_fingerprint"]):
                    continue
            if not valid_success_result_v4(command, result):
                continue
            before, after = result.get("before"), result.get("after")
            before_day = before.get("business", {}).get("details", {}).get("day") if type(before) is dict else None
            after_day = after.get("business", {}).get("details", {}).get("day") if type(after) is dict else None
            if type(before_day) is not dict or type(after_day) is not dict:
                continue
            successful.append((command["action"], before, after, record["command_id"]))
        successful.sort(key=lambda item: (item[1]["world_epoch"], item[1]["revision"], item[3]))
        return successful

    def mark_completed(self, records, state, session_id):
        """Mark done only after journal proves every authorized day transition and report closure."""
        if not valid_snapshot_v4(state) or not _is_text(session_id, 128):
            raise RunStoreError("run_store_invalid_snapshot")
        if type(records) is dict and set(records) == {"journal_format_version", "broker_epoch", "records"}:
            broker_epoch = records.get("broker_epoch")
        else:
            with _FileLock(self.lock_path):
                broker_epoch = self._load_unlocked()["binding"]["broker_epoch"]
        if not _is_text(broker_epoch, 128):
            raise RunStoreError("run_store_invalid_journal")
        journal = self._journal_document(records, broker_epoch)
        now = self._now()
        with _FileLock(self.lock_path):
            document = self._load_unlocked()
            if document["binding"]["broker_epoch"] not in (None, broker_epoch):
                return False
            before_reconcile = copy.deepcopy(document)
            self._reconcile_records(document, journal, now)
            if document != before_reconcile:
                self._touch(document, now)
                self._atomic_replace(document)
            if (self._status(document) != "running" or document["controls"]["paused"]
                    or session_id != document["binding"]["session_id"]
                    or state["world_epoch"] != document["binding"]["world_epoch"]):
                return False
            day = state["business"].get("details", {}).get("day")
            surface_status = state.get("surface", {}).get("status")
            customer_state = state["business"].get("details", {}).get("customer")
            if (state["phase"] != "MORNING" or not state["ready"] or type(day) is not dict
                    or day.get("day_number") != document["run"]["target_day"]
                    or day.get("stage") not in {"morning", "next_morning"}
                    or day.get("report_open") is not False or day.get("transition_pending") is not False
                    or surface_status not in {"idle", "awaiting_agent"}
                    or (not state.get("legal_actions") and surface_status == "awaiting_agent")
                    or type(customer_state) is not dict or customer_state.get("presence") != "absent"
                    or any(entry.get("action") not in {"start_business", "sort_inventory_default"}
                           for entry in state.get("legal_actions", []))):
                return False
            if any(event["status"] in {"submitted", "awaiting_user", "blocked_unknown", "pending", "unsupported"}
                   for event in document["inbox"]):
                return False
            managed_commands = {entry["command_id"]: entry for entry in document["commands"]}
            by_day = self._successful_actions_by_day(journal, managed_commands)
            chain_ids = []
            chain_last_revision = -1
            for current_day in range(document["run"]["start_day"], document["run"]["target_day"]):
                expected = ("close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report")
                cursor = 0
                for action, before, after, command_id in by_day:
                    if action != expected[cursor] or before["world_epoch"] != state["world_epoch"]:
                        continue
                    if before["revision"] < chain_last_revision:
                        continue
                    before_day = before.get("business", {}).get("details", {}).get("day")
                    after_day = after.get("business", {}).get("details", {}).get("day")
                    if type(before_day) is not dict or type(after_day) is not dict:
                        continue
                    if cursor == 0 and before_day.get("day_number") == current_day and before["phase"] == "OPEN":
                        chain_ids.append(command_id)
                        cursor += 1
                        chain_last_revision = after["revision"]
                    elif cursor == 1 and before_day.get("day_number") == current_day and before["phase"] == "CLOSED":
                        chain_ids.append(command_id)
                        cursor += 1
                        chain_last_revision = after["revision"]
                    elif cursor == 2 and before_day.get("day_number") == current_day and after_day.get("day_number") == current_day + 1:
                        chain_ids.append(command_id)
                        cursor += 1
                        chain_last_revision = after["revision"]
                    elif cursor == 3 and before_day.get("day_number") == current_day + 1 and before_day.get("report_open") is True \
                            and after_day.get("day_number") == current_day + 1 and after_day.get("report_open") is False:
                        chain_ids.append(command_id)
                        cursor += 1
                        chain_last_revision = after["revision"]
                        break
                if cursor != 4:
                    return False
            if (state["revision"] < chain_last_revision
                    or any(command_id not in managed_commands for command_id in chain_ids)):
                return False
            if day.get("settlement") is not None or day.get("transition_pending"):
                return False
            document["completion_evidence"] = {"target_day": document["run"]["target_day"],
                                               "world_epoch": state["world_epoch"],
                                               "revision": state["revision"], "command_ids": chain_ids}
            document["controls"]["completed"] = True
            document["controls"]["paused"] = False
            document["controls"]["reason"] = "journal_day_chain_confirmed"
            self._touch(document, now)
            self._atomic_replace(document)
            return copy.deepcopy(document)
