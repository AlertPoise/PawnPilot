"""Small crash-safe JSON journal and single-writer lock for the broker."""
from __future__ import annotations

import json
import hashlib
import os
from pathlib import Path
import tempfile

MAX_JOURNAL_BYTES = 16 * 1024 * 1024
STATUSES = {"pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"}
RESULT_TO_COMMAND = {"success": "succeeded", "succeeded": "succeeded", "rejected": "rejected",
                     "error": "failed", "failed": "failed", "uncertain": "outcome_unknown",
                     "outcome_unknown": "outcome_unknown"}


class JournalError(RuntimeError):
    pass


class Journal:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.lock_path = self.path.with_suffix(self.path.suffix + ".lock")
        self._lock_file = None

    def acquire(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._lock_file = self.lock_path.open("a+b")
        try:
            if os.name == "nt":
                import msvcrt
                self._lock_file.seek(0)
                if self._lock_file.read(1) == b"":
                    self._lock_file.seek(0)
                    self._lock_file.write(b"0")
                    self._lock_file.flush()
                self._lock_file.seek(0)
                msvcrt.locking(self._lock_file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._lock_file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except (OSError, BlockingIOError) as exc:
            self._lock_file.close()
            self._lock_file = None
            raise JournalError("broker_journal_already_owned") from exc

    def release(self):
        if self._lock_file is None:
            return
        try:
            if os.name == "nt":
                import msvcrt
                self._lock_file.seek(0)
                msvcrt.locking(self._lock_file.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._lock_file.fileno(), fcntl.LOCK_UN)
        finally:
            self._lock_file.close()
            self._lock_file = None

    def load(self):
        if not self.path.exists():
            return None
        try:
            if self.path.stat().st_size > MAX_JOURNAL_BYTES:
                raise JournalError("broker_journal_corrupt")
            with self.path.open("r", encoding="utf-8") as file:
                value = json.load(file, parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
            if self.path.stat().st_size > MAX_JOURNAL_BYTES:
                raise JournalError("broker_journal_corrupt")
        except JournalError:
            raise
        except (OSError, UnicodeError, ValueError, json.JSONDecodeError) as exc:
            raise JournalError("broker_journal_corrupt") from exc
        if (not isinstance(value, dict) or type(value.get("schema_version")) is not int
                or value.get("schema_version") != 1
                or not isinstance(value.get("broker_epoch"), str) or not value["broker_epoch"]
                or len(value["broker_epoch"]) > 64
                or not isinstance(value.get("records"), list)):
            raise JournalError("broker_journal_corrupt")
        active_count = 0
        seen = set()
        for record in value["records"]:
            if (not isinstance(record, dict) or not isinstance(record.get("command_id"), str)
                    or record["command_id"] in seen
                    or not isinstance(record.get("request_fingerprint"), str)
                    or not isinstance(record.get("request"), dict)
                    or not isinstance(record.get("command"), dict)
                    or type(record.get("command_status")) is not str
                    or record.get("command_status") not in STATUSES
                    or (record.get("result") is not None and not isinstance(record.get("result"), dict))):
                raise JournalError("broker_journal_corrupt")
            command = record["command"]
            request = record["request"]
            status = record["command_status"]
            fingerprint = hashlib.sha256(json.dumps(
                request, sort_keys=True, separators=(",", ":"), ensure_ascii=False
            ).encode("utf-8")).hexdigest()
            if (command.get("id") != record["command_id"]
                    or set(command) != {"schema_version", "id", "action", "broker_epoch",
                                        "expected_world_epoch", "expected_revision", "expected_version",
                                        "expires_at_unix_ms"}
                    or type(command.get("schema_version")) is not int or command["schema_version"] != 1
                    or command.get("broker_epoch") != request.get("broker_epoch")
                    or command.get("action") not in {"observe", "close_menu"}
                    or any(not isinstance(command.get(k), str) for k in
                           ("broker_epoch", "expected_world_epoch", "expected_version"))
                    or type(command.get("expected_revision")) is not int or command["expected_revision"] < 0
                    or type(command.get("expires_at_unix_ms")) is not int
                    or command.get("expected_version") != f"{command.get('expected_world_epoch')}:{command.get('expected_revision')}"
                    or set(request) != {"schema_version", "id", "broker_epoch", "action",
                                        "expected_world_epoch", "expected_revision", "expected_version"}
                    or any(request.get(k) != command.get(k) for k in
                           ("action", "expected_world_epoch", "expected_revision", "expected_version"))
                    or request.get("id") != record["command_id"]
                    or type(request.get("schema_version")) is not int or request.get("schema_version") != 1
                    or type(request.get("expected_revision")) is not int or request["expected_revision"] < 0
                    or not isinstance(request.get("broker_epoch"), str)
                    or record.get("request_fingerprint") != fingerprint
                    or type(record.get("created_at_unix_ms")) is not int
                    or type(record.get("updated_at_unix_ms")) is not int
                    or record["created_at_unix_ms"] < 0 or record["updated_at_unix_ms"] < record["created_at_unix_ms"]
                    or (status in {"pending", "running"} and record.get("result") is not None)
                    or (status in {"succeeded", "rejected", "failed", "outcome_unknown"}
                        and not isinstance(record.get("result"), dict))
                    or (status == "pending" and record.get("delivery_session_id") is not None)
                    or (status in {"running", "outcome_unknown", "succeeded", "failed"}
                        and (not isinstance(record.get("delivery_session_id"), str)
                             or not record["delivery_session_id"]))
                    or (record.get("delivery_session_id") is not None
                        and (not isinstance(record.get("delivery_session_id"), str)
                             or not record["delivery_session_id"]))):
                raise JournalError("broker_journal_corrupt")
            result = record.get("result")
            if result is not None:
                if (set(result) != {"schema_version", "id", "broker_epoch", "status", "reason", "before", "after"}
                        or result.get("id") != record["command_id"]
                        or result.get("broker_epoch") != command.get("broker_epoch")
                        or type(result.get("schema_version")) is not int or result["schema_version"] != 1
                        or not isinstance(result.get("reason"), str) or len(result["reason"]) > 512
                        or not isinstance(result.get("status"), str)
                        or result.get("status") not in RESULT_TO_COMMAND
                        or ("before" not in result or "after" not in result)):
                    raise JournalError("broker_journal_corrupt")
                if RESULT_TO_COMMAND.get(result["status"]) != status:
                    raise JournalError("broker_journal_corrupt")
                for snapshot in (result.get("before"), result.get("after")):
                    if snapshot is not None and not self._valid_snapshot(snapshot):
                        raise JournalError("broker_journal_corrupt")
                if status == "succeeded":
                    before, after = result.get("before"), result.get("after")
                    if (not self._valid_snapshot(before) or not self._valid_snapshot(after)
                            or before.get("world_epoch") != command.get("expected_world_epoch")
                            or before.get("revision") != command.get("expected_revision")
                            or before.get("version") != command.get("expected_version")):
                        raise JournalError("broker_journal_corrupt")
                    if command["action"] == "close_menu":
                        if (not before["ready"] or not before["can_close_menu"] or not after["ready"]
                                or after["world_epoch"] != before["world_epoch"]
                                or after["scene"] != before["scene"] or after["menu_open"]
                                or after["revision"] <= before["revision"]):
                            raise JournalError("broker_journal_corrupt")
                    elif (after["world_epoch"] != before["world_epoch"]
                          or after["revision"] != before["revision"]
                          or after["version"] != before["version"]):
                        raise JournalError("broker_journal_corrupt")
            resolution = record.get("resolution")
            if resolution is not None:
                if (status not in {"outcome_unknown", "succeeded", "rejected", "failed"}
                        or not isinstance(resolution, dict)
                        or set(resolution) != {"schema_version", "method", "command_id", "broker_epoch",
                                               "command_expires_at_unix_ms", "session_id", "world_epoch", "revision", "version",
                                               "snapshot", "operator_note", "confirm_no_replay", "recorded_at_unix_ms"}
                        or type(resolution.get("schema_version")) is not int or resolution["schema_version"] != 1
                        or resolution.get("method") != "manual_state_alignment"
                        or command.get("action") != "close_menu"
                        or resolution.get("command_id") != record["command_id"]
                        or not isinstance(resolution.get("broker_epoch"), str)
                        or resolution.get("command_expires_at_unix_ms") != command.get("expires_at_unix_ms")
                        or not isinstance(resolution.get("session_id"), str) or not resolution["session_id"]
                        or not isinstance(resolution.get("world_epoch"), str)
                        or type(resolution.get("revision")) is not int or resolution["revision"] < 0
                        or resolution.get("version") != f"{resolution.get('world_epoch')}:{resolution.get('revision')}"
                        or not self._valid_snapshot(resolution.get("snapshot"))
                        or resolution["snapshot"].get("world_epoch") != resolution.get("world_epoch")
                        or resolution["snapshot"].get("revision") != resolution.get("revision")
                        or resolution["snapshot"].get("version") != resolution.get("version")
                        or not resolution["snapshot"].get("ready") or resolution["snapshot"].get("menu_open")
                        or not isinstance(resolution.get("operator_note"), str)
                        or not 8 <= len(resolution["operator_note"].strip()) <= 500
                        or resolution.get("confirm_no_replay") is not True
                        or type(resolution.get("recorded_at_unix_ms")) is not int
                        or resolution["recorded_at_unix_ms"] <= command["expires_at_unix_ms"]
                        or record.get("updated_at_unix_ms", 0) < resolution.get("recorded_at_unix_ms", 0)
                        or (resolution["world_epoch"] == command["expected_world_epoch"]
                            and resolution["revision"] <= command["expected_revision"])):
                    raise JournalError("broker_journal_corrupt")
            if status in {"pending", "running"} or (status == "outcome_unknown" and resolution is None):
                active_count += 1
            seen.add(record["command_id"])
        if active_count > 1:
            raise JournalError("broker_journal_corrupt")
        return value

    @staticmethod
    def _valid_snapshot(snapshot):
        if not isinstance(snapshot, dict):
            return False
        if set(snapshot) != {"schema_version", "version", "world_epoch", "phase", "scene",
                             "not_ready_reason", "revision", "loaded", "ready", "menu_open",
                             "can_close_menu", "cash"}:
            return False
        epoch, revision = snapshot.get("world_epoch"), snapshot.get("revision")
        return (type(snapshot.get("schema_version")) is int and snapshot["schema_version"] == 1
                and isinstance(epoch, str) and isinstance(revision, int) and type(revision) is int
                and revision >= 0 and snapshot.get("version") == f"{epoch}:{revision}"
                and isinstance(snapshot.get("scene"), str) and type(snapshot.get("loaded")) is bool
                and type(snapshot.get("ready")) is bool and type(snapshot.get("can_close_menu")) is bool
                and (snapshot.get("not_ready_reason") is None or
                     (isinstance(snapshot.get("not_ready_reason"), str)
                      and len(snapshot["not_ready_reason"]) <= 256))
                and type(snapshot.get("cash")) is int and isinstance(snapshot.get("phase"), str)
                and type(snapshot.get("menu_open")) is bool and len(snapshot["phase"]) <= 128
                and isinstance(snapshot.get("scene"), str) and len(snapshot["scene"]) <= 128
                and (snapshot["ready"] or isinstance(snapshot.get("not_ready_reason"), str))
                and (not snapshot["ready"] or (snapshot["loaded"] and snapshot["not_ready_reason"] is None))
                and (not snapshot["can_close_menu"] or (snapshot["ready"] and snapshot["menu_open"])))

    def save(self, value):
        """Durably replace the journal; caller holds the owner lock."""
        temp_name = None
        try:
            raw = json.dumps(value, separators=(",", ":"), ensure_ascii=False,
                             allow_nan=False).encode("utf-8")
            if len(raw) > MAX_JOURNAL_BYTES:
                raise JournalError("broker_journal_write_too_large")
            fd, temp_name = tempfile.mkstemp(prefix=self.path.name + ".", suffix=".tmp",
                                             dir=str(self.path.parent))
            with os.fdopen(fd, "wb") as file:
                file.write(raw)
                file.flush()
                os.fsync(file.fileno())
            os.replace(temp_name, self.path)
            if os.name != "nt":
                directory_fd = os.open(self.path.parent, os.O_RDONLY)
                try:
                    os.fsync(directory_fd)
                finally:
                    os.close(directory_fd)
        except (OSError, ValueError, TypeError) as exc:
            if temp_name is not None:
                try:
                    os.unlink(temp_name)
                except OSError:
                    pass
            raise JournalError("broker_journal_write_failed") from exc
