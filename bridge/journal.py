"""Crash-safe JSON journal with versioned, exact wire record validation."""
from __future__ import annotations

import json
import os
from pathlib import Path
import tempfile
import time

try:
    from .legacy_journal import Journal as LegacyJournal, JournalError
    from .protocol import (WIRE_VERSION_V3, WIRE_VERSION_V4, progress_matches_command, request_fingerprint, strict_json_loads,
                           valid_command, valid_request, valid_result_structure, valid_snapshot,
                           valid_success_result, valid_command_v3, valid_request_v3,
                           valid_result_structure_v3, valid_snapshot_v3, valid_success_result_v3,
                           valid_command_v4, valid_request_v4, valid_result_structure_v4,
                           valid_snapshot_v4, valid_success_result_v4)
except ImportError:  # Support the existing `python bridge/server.py` launch path.
    from legacy_journal import Journal as LegacyJournal, JournalError
    from protocol import (WIRE_VERSION_V3, WIRE_VERSION_V4, progress_matches_command, request_fingerprint, strict_json_loads,
                         valid_command, valid_request, valid_result_structure, valid_snapshot,
                         valid_success_result, valid_command_v3, valid_request_v3,
                          valid_result_structure_v3, valid_snapshot_v3, valid_success_result_v3,
                          valid_command_v4, valid_request_v4, valid_result_structure_v4,
                          valid_snapshot_v4, valid_success_result_v4)

MAX_JOURNAL_BYTES = 16 * 1024 * 1024
MAX_RECORDS = 4096
STATUSES = {"pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"}
RESULT_TO_COMMAND = {"success": "succeeded", "succeeded": "succeeded", "rejected": "rejected",
                     "error": "failed", "failed": "failed", "uncertain": "outcome_unknown",
                     "outcome_unknown": "outcome_unknown"}
_V1_RECORD_KEYS = {"command_id", "request_fingerprint", "request", "command", "command_status",
                   "result", "delivery_session_id", "created_at_unix_ms", "updated_at_unix_ms"}
_V2_RECORD_KEYS = _V1_RECORD_KEYS | {"execution"}
_RESOLUTION_KEYS = {"schema_version", "method", "command_id", "broker_epoch",
                    "command_expires_at_unix_ms", "session_id", "world_epoch", "revision",
                    "version", "snapshot", "operator_note", "confirm_no_replay", "recorded_at_unix_ms"}


class Journal:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        self.lock_path = self.path.with_suffix(self.path.suffix + ".lock")
        self.backup_path = self.path.with_suffix(self.path.suffix + ".v1.bak")
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
            raw = self.path.read_bytes()
            if len(raw) > MAX_JOURNAL_BYTES:
                raise JournalError("broker_journal_corrupt")
            # Preserve the exact v1 parser/validator, including its legacy JSON semantics.
            probe = json.loads(raw.decode("utf-8"),
                               parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))
            if isinstance(probe, dict) and type(probe.get("schema_version")) is int and probe["schema_version"] == 1:
                legacy = LegacyJournal(self.path).load()
                if self.path.stat().st_size != len(raw) or self.path.read_bytes() != raw:
                    raise JournalError("broker_journal_corrupt")
                return self._migrate_v1(raw, legacy)
            value = strict_json_loads(raw.decode("utf-8"))
            if not self.valid_v2(value):
                raise JournalError("broker_journal_corrupt")
            return value
        except JournalError:
            raise
        except (OSError, UnicodeError, ValueError, RecursionError, json.JSONDecodeError) as exc:
            raise JournalError("broker_journal_corrupt") from exc

    def _migrate_v1(self, raw: bytes, legacy):
        if legacy is None or not isinstance(legacy, dict):
            raise JournalError("broker_journal_corrupt")
        if len(legacy["records"]) > MAX_RECORDS:
            raise JournalError("broker_journal_corrupt")
        # An existing matching backup is an interrupted, resumable migration. A differing
        # backup is preserved and stops startup; migration never overwrites operator data.
        try:
            if self.backup_path.exists():
                if self.backup_path.read_bytes() != raw:
                    raise JournalError("broker_journal_migration_backup_failed")
            else:
                self._install_backup(raw)
            if self.backup_path.read_bytes() != raw:
                raise JournalError("broker_journal_migration_backup_failed")
        except JournalError:
            raise
        except OSError as exc:
            raise JournalError("broker_journal_migration_backup_failed") from exc

        candidate = {"journal_format_version": 2, "broker_epoch": legacy["broker_epoch"],
                     "records": legacy["records"]}
        self._validate_or_corrupt(candidate)
        self._atomic_replace(candidate, migration=True)
        return candidate

    def _install_backup(self, raw: bytes):
        temp = None
        try:
            temp = self._write_temp(raw, suffix=".v1-backup.tmp")
            # Atomic no-clobber installation. Hard links are same-volume and fail if the
            # target appeared; the primary journal remains untouched on any failure.
            os.link(temp, self.backup_path)
            os.unlink(temp)
            temp = None
            self._fsync_directory(self.path.parent)
        except OSError as exc:
            raise JournalError("broker_journal_migration_backup_failed") from exc
        finally:
            if temp is not None:
                try:
                    os.unlink(temp)
                except OSError:
                    pass

    @staticmethod
    def _valid_v1_snapshot(snapshot):
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
                and len(snapshot["scene"]) <= 128
                and (snapshot["ready"] or isinstance(snapshot.get("not_ready_reason"), str))
                and (not snapshot["ready"] or (snapshot["loaded"] and snapshot["not_ready_reason"] is None))
                and (not snapshot["can_close_menu"] or (snapshot["ready"] and snapshot["menu_open"])))

    @classmethod
    def _valid_v1_record(cls, record):
        if (not isinstance(record, dict) or not isinstance(record.get("command_id"), str)
                or not isinstance(record.get("request_fingerprint"), str)
                or not isinstance(record.get("request"), dict)
                or not isinstance(record.get("command"), dict)
                or type(record.get("command_status")) is not str
                or record["command_status"] not in STATUSES
                or (record.get("result") is not None and not isinstance(record["result"], dict))):
            return False
        command, request, status = record["command"], record["request"], record["command_status"]
        try:
            import hashlib
            fingerprint = hashlib.sha256(json.dumps(
                request, sort_keys=True, separators=(",", ":"), ensure_ascii=False
            ).encode("utf-8")).hexdigest()
        except (TypeError, ValueError):
            return False
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
            return False
        result = record.get("result")
        if result is not None:
            if (set(result) != {"schema_version", "id", "broker_epoch", "status", "reason", "before", "after"}
                    or result.get("id") != record["command_id"]
                    or result.get("broker_epoch") != command.get("broker_epoch")
                    or type(result.get("schema_version")) is not int or result["schema_version"] != 1
                    or not isinstance(result.get("reason"), str) or len(result["reason"]) > 512
                    or not isinstance(result.get("status"), str)
                    or result.get("status") not in RESULT_TO_COMMAND
                    or ("before" not in result or "after" not in result)
                    or RESULT_TO_COMMAND[result["status"]] != status):
                return False
            for snapshot in (result.get("before"), result.get("after")):
                if snapshot is not None and not cls._valid_v1_snapshot(snapshot):
                    return False
            if status == "succeeded":
                before, after = result.get("before"), result.get("after")
                if (not cls._valid_v1_snapshot(before) or not cls._valid_v1_snapshot(after)
                        or before.get("world_epoch") != command.get("expected_world_epoch")
                        or before.get("revision") != command.get("expected_revision")
                        or before.get("version") != command.get("expected_version")):
                    return False
                if command["action"] == "close_menu":
                    if (not before["ready"] or not before["can_close_menu"] or not after["ready"]
                            or after["world_epoch"] != before["world_epoch"]
                            or after["scene"] != before["scene"] or after["menu_open"]
                            or after["revision"] <= before["revision"]):
                        return False
                elif (after["world_epoch"] != before["world_epoch"]
                      or after["revision"] != before["revision"] or after["version"] != before["version"]):
                    return False
        resolution = record.get("resolution")
        if isinstance(resolution, dict) and resolution.get("schema_version") == 2:
            if not cls._valid_v2_resolution(record, resolution):
                return False
        elif resolution is not None:
            if (status not in {"outcome_unknown", "succeeded", "rejected", "failed"}
                    or not isinstance(resolution, dict)
                    or set(resolution) != _RESOLUTION_KEYS
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
                    or not cls._valid_v1_snapshot(resolution.get("snapshot"))
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
                return False
        return True

    @classmethod
    def valid_v2(cls, value):
        try:
            return cls._valid_v2(value)
        except Exception:
            # Validators are a boundary: malformed persisted values must fail closed,
            # never leak implementation exceptions into broker error handling.
            return False

    @classmethod
    def _valid_v2(cls, value):
        if (type(value) is not dict or set(value) != {"journal_format_version", "broker_epoch", "records"}
                or type(value.get("journal_format_version")) is not int or value["journal_format_version"] != 2
                or type(value.get("broker_epoch")) is not str or not 1 <= len(value["broker_epoch"]) <= 128
                or type(value.get("records")) is not list or len(value["records"]) > MAX_RECORDS):
            return False
        ids, active = set(), 0
        for record in value["records"]:
            if type(record) is not dict or type(record.get("command")) is not dict:
                return False
            version = record["command"].get("schema_version")
            if version == 1:
                if not cls._valid_v1_record(record) or "execution" in record:
                    return False
                status = record["command_status"]
                resolution = record.get("resolution")
            elif version == 2:
                if not cls._valid_v2_record(record):
                    return False
                status = record["command_status"]
                resolution = record.get("resolution")
            elif version == WIRE_VERSION_V3:
                if not cls._valid_v3_record(record):
                    return False
                status = record["command_status"]
                resolution = record.get("resolution")
            elif version == WIRE_VERSION_V4:
                if not cls._valid_v4_record(record):
                    return False
                status = record["command_status"]
                resolution = record.get("resolution")
            else:
                return False
            command_id = record["command_id"]
            if command_id in ids:
                return False
            ids.add(command_id)
            if status in {"pending", "running"} or (status == "outcome_unknown" and resolution is None):
                active += 1
        return active <= 1

    @classmethod
    def _valid_v2_record(cls, record):
        return cls._valid_wire_record(record, 2)

    @classmethod
    def _valid_v3_record(cls, record):
        return cls._valid_wire_record(record, WIRE_VERSION_V3)

    @classmethod
    def _valid_v4_record(cls, record):
        return cls._valid_wire_record(record, WIRE_VERSION_V4)

    @classmethod
    def _valid_wire_record(cls, record, version):
        keys = set(record)
        if (keys != _V2_RECORD_KEYS and keys != (_V2_RECORD_KEYS | {"resolution"})):
            return False
        if (
                not isinstance(record.get("command_id"), str)
                or not isinstance(record.get("request_fingerprint"), str)
                or not isinstance(record.get("request"), dict)
                or not isinstance(record.get("command"), dict)
                or type(record.get("command_status")) is not str
                or record["command_status"] not in STATUSES
                or (record.get("result") is not None and not isinstance(record.get("result"), dict))
                or (record.get("delivery_session_id") is not None
                    and (not isinstance(record.get("delivery_session_id"), str) or not record["delivery_session_id"]))
                or type(record.get("created_at_unix_ms")) is not int or record["created_at_unix_ms"] < 0
                or type(record.get("updated_at_unix_ms")) is not int
                or record["updated_at_unix_ms"] < record["created_at_unix_ms"]):
            return False
        command, request, status = record["command"], record["request"], record["command_status"]
        request_valid = (valid_request_v4 if version == WIRE_VERSION_V4 else
                         valid_request_v3 if version == WIRE_VERSION_V3 else valid_request)
        command_valid = (valid_command_v4 if version == WIRE_VERSION_V4 else
                         valid_command_v3 if version == WIRE_VERSION_V3 else valid_command)
        if version == WIRE_VERSION_V4:
            result_valid = lambda value, require_execution=False: valid_result_structure_v4(
                value, require_execution, command=command)
        elif version == WIRE_VERSION_V3:
            result_valid = valid_result_structure_v3
        else:
            result_valid = valid_result_structure
        snapshot_valid = (valid_snapshot_v4 if version == WIRE_VERSION_V4 else
                          valid_snapshot_v3 if version == WIRE_VERSION_V3 else valid_snapshot)
        success_valid = (valid_success_result_v4 if version == WIRE_VERSION_V4 else
                         valid_success_result_v3 if version == WIRE_VERSION_V3 else valid_success_result)
        if (not command_valid(command) or not request_valid(request)
                or command["id"] != record["command_id"]
                or command["broker_epoch"] != request.get("broker_epoch")
                or set(request) != {"schema_version", "id", "broker_epoch", "action", "expected_world_epoch",
                                    "expected_revision", "expected_version", "arguments"}
                or any(request[k] != command[k] for k in
                       ("schema_version", "id", "broker_epoch", "action", "expected_world_epoch",
                        "expected_revision", "expected_version", "arguments"))
                or record["request_fingerprint"] != request_fingerprint(request)
                or (status == "pending" and record.get("delivery_session_id") is not None)
                or (status in {"running", "outcome_unknown", "succeeded", "failed"}
                    and (not isinstance(record.get("delivery_session_id"), str)
                         or not record["delivery_session_id"]))):
            return False
        execution = record["execution"]
        if execution is not None and not progress_matches_command(execution, command):
            return False
        if status == "pending" and execution is not None:
            return False
        if status == "running" and execution is not None and execution["stage"] in {"completed", "uncertain"}:
            return False
        result = record["result"]
        if status in {"pending", "running"}:
            if result is not None:
                return False
        else:
            if result is None:
                return False
            if (not result_valid(result, require_execution=False)
                    or result["id"] != record["command_id"]
                    or result["broker_epoch"] != command["broker_epoch"]
                    or RESULT_TO_COMMAND.get(result["status"]) != status):
                return False
            if result["schema_version"] == version:
                if result["execution"] is not None:
                    if result["execution"] != execution:
                        return False
                elif status == "rejected" and execution is not None and execution["may_have_side_effects"]:
                    return False
                if result["status"] == "succeeded" and not success_valid(command, result):
                    return False
            elif result["schema_version"] == 1:
                # A v2 command cannot be retroactively backed by a v1 result.
                return False
            else:
                return False
        resolution = record.get("resolution")
        if resolution is not None and not cls._valid_resolution(record, resolution, version):
            return False
        return True

    @classmethod
    def _valid_v2_resolution(cls, record, resolution):
        return cls._valid_resolution(record, resolution, 2)

    @classmethod
    def _valid_resolution(cls, record, resolution, version):
        command = record["command"]
        deadline = (command["expires_at_unix_ms"] if command["schema_version"] == 1 else
                    command["start_deadline_unix_ms"] + command["execution_timeout_ms"])
        return (record["command_status"] in {"outcome_unknown", "succeeded", "rejected", "failed"}
                and type(resolution) is dict and set(resolution) == _RESOLUTION_KEYS
                and type(resolution.get("schema_version")) is int and resolution["schema_version"] == version
                and resolution.get("method") == "manual_state_alignment"
                and (command.get("action") == "close_menu" or version in {WIRE_VERSION_V3, WIRE_VERSION_V4}
                     and command.get("action") in {"open_bargain_panel", "close_bargain_panel"})
                and resolution.get("command_id") == record["command_id"]
                and isinstance(resolution.get("broker_epoch"), str) and 1 <= len(resolution["broker_epoch"]) <= 128
                and resolution.get("command_expires_at_unix_ms") == deadline
                and isinstance(resolution.get("session_id"), str) and resolution["session_id"]
                and resolution["session_id"] != record.get("delivery_session_id")
                and isinstance(resolution.get("world_epoch"), str) and 1 <= len(resolution["world_epoch"]) <= 128
                and type(resolution.get("revision")) is int and 0 <= resolution["revision"] <= 2_147_483_647
                and resolution.get("version") == f"{resolution.get('world_epoch')}:{resolution.get('revision')}"
                and (valid_snapshot_v4(resolution.get("snapshot")) if version == WIRE_VERSION_V4 else
                     valid_snapshot_v3(resolution.get("snapshot")) if version == WIRE_VERSION_V3 else
                     valid_snapshot(resolution.get("snapshot")))
                and resolution["snapshot"].get("world_epoch") == resolution.get("world_epoch")
                and resolution["snapshot"].get("revision") == resolution.get("revision")
                and resolution["snapshot"].get("version") == resolution.get("version")
                and resolution["snapshot"].get("ready") and not resolution["snapshot"].get("menu_open")
                and isinstance(resolution.get("operator_note"), str)
                and 8 <= len(resolution["operator_note"].strip()) <= 500
                and resolution.get("confirm_no_replay") is True
                and type(resolution.get("recorded_at_unix_ms")) is int
                and resolution["recorded_at_unix_ms"] >= deadline
                and record.get("updated_at_unix_ms", 0) >= resolution.get("recorded_at_unix_ms", 0)
                and (resolution["world_epoch"] != command["expected_world_epoch"]
                     or resolution["revision"] > command["expected_revision"]))

    @staticmethod
    def _fsync_directory(path: Path | None = None):
        directory = path or Path.cwd()
        if os.name != "nt":
            fd = os.open(directory, os.O_RDONLY)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)

    def _write_temp(self, raw: bytes, suffix=".tmp"):
        fd, name = tempfile.mkstemp(prefix=self.path.name + ".", suffix=suffix, dir=str(self.path.parent))
        try:
            with os.fdopen(fd, "wb") as file:
                file.write(raw)
                file.flush()
                os.fsync(file.fileno())
            return name
        except Exception:
            try:
                os.unlink(name)
            except OSError:
                pass
            raise

    def _atomic_replace(self, value, migration=False):
        temp = None
        reason = "broker_journal_migration_failed" if migration else "broker_journal_write_failed"
        try:
            raw = json.dumps(value, separators=(",", ":"), ensure_ascii=False,
                             allow_nan=False).encode("utf-8")
            if len(raw) > MAX_JOURNAL_BYTES:
                raise JournalError("broker_journal_write_too_large")
            temp = self._write_temp(raw)
            candidate = strict_json_loads(Path(temp).read_bytes().decode("utf-8"))
            self._validate_or_corrupt(candidate)
            self._replace_with_retry(temp, self.path)
            temp = None
            try:
                self._fsync_directory(self.path.parent)
            except OSError:
                # The v1 backup and replacement file are already fsynced. If the
                # directory barrier is unavailable, treating this as a failed
                # migration would report failure after committing the replacement.
                if not migration:
                    raise
        except JournalError:
            raise
        except (OSError, ValueError, TypeError, UnicodeError) as exc:
            raise JournalError(reason) from exc
        finally:
            if temp is not None:
                try:
                    os.unlink(temp)
                except OSError:
                    pass

    @classmethod
    def _validate_or_corrupt(cls, value):
        if not cls.valid_v2(value):
            raise JournalError("broker_journal_corrupt")

    @staticmethod
    def _replace_with_retry(source, destination):
        # Windows security scanners can briefly deny replacement while inspecting
        # the flushed temp file. Retry only that transient error, then fail closed.
        for attempt in range(5):
            try:
                os.replace(source, destination)
                return
            except PermissionError:
                if attempt == 4:
                    raise
                time.sleep(0.01 * (attempt + 1))

    def save(self, value):
        """Durably replace format2 journal; caller holds the owner lock."""
        self._validate_or_corrupt(value)
        self._atomic_replace(value)
