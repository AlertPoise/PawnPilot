"""Bounded, isolated Codex CLI driver for PawnPilot supervisor turns."""

from __future__ import annotations

import ctypes
import json
import os
import signal
import subprocess
import threading
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable
from uuid import uuid4


PAWNPILOT_TOOLS = (
    "health_check",
    "observe",
    "act",
    "get_action_status",
    "wait_for_update",
)
DISPOSITION_VALUES = ("acted", "wait", "blocked")
NON_TOOL_ITEM_TYPES = frozenset(("agent_message", "reasoning", "plan_update"))
OUTPUT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "summary": {"type": "string"},
        "plan": {"type": "string"},
        "disposition": {"type": "string", "enum": list(DISPOSITION_VALUES)},
    },
    "required": ["summary", "plan", "disposition"],
}
_NONFATAL_MODEL_METADATA_WARNING_MODELS = frozenset(("gpt-6-luna", "gpt-5.6-luna"))
_MODEL_METADATA_WARNING_SUFFIX = "` not found. Defaulting to fallback metadata;"


class _JobBasicLimitInformation(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_longlong),
        ("PerJobUserTimeLimit", ctypes.c_longlong),
        ("LimitFlags", ctypes.c_uint32),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", ctypes.c_uint32),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", ctypes.c_uint32),
        ("SchedulingClass", ctypes.c_uint32),
    ]


class _JobIoCounters(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_uint64),
        ("WriteOperationCount", ctypes.c_uint64),
        ("OtherOperationCount", ctypes.c_uint64),
        ("ReadTransferCount", ctypes.c_uint64),
        ("WriteTransferCount", ctypes.c_uint64),
        ("OtherTransferCount", ctypes.c_uint64),
    ]


class _JobExtendedLimitInformation(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", _JobBasicLimitInformation),
        ("IoInfo", _JobIoCounters),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


class _ThreadEntry32(ctypes.Structure):
    _fields_ = [
        ("dwSize", ctypes.c_uint32),
        ("cntUsage", ctypes.c_uint32),
        ("th32ThreadID", ctypes.c_uint32),
        ("th32OwnerProcessID", ctypes.c_uint32),
        ("tpBasePri", ctypes.c_int32),
        ("tpDeltaPri", ctypes.c_int32),
        ("dwFlags", ctypes.c_uint32),
    ]


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _clean_reason(value: object, secret: str, limit: int = 500) -> str:
    text = str(value).replace(secret, "[REDACTED]") if secret else str(value)
    text = " ".join(text.split())
    return text[:limit]


class _JsonlCapture:
    """Drain stdout, persist bounded/redacted JSONL, and collect protocol facts."""

    def __init__(
        self,
        stream: Any,
        log_path: Path,
        max_bytes: int,
        secret: str,
        model: str,
        max_line_bytes: int = 1024 * 1024,
    ) -> None:
        self.stream = stream
        self.log_path = log_path
        self.max_bytes = max_bytes
        self.secret = secret.encode("utf-8")
        self.model = model
        self.max_line_bytes = min(max_line_bytes, max_bytes)
        self.thread_id: str | None = None
        self.answer_text: str | None = None
        self.turn_completed = False
        self.failed_event: str | None = None
        self.invalid_reason: str | None = None
        self.output_limit = threading.Event()
        self.secret_echoed = threading.Event()
        self.mcp_calls: list[dict[str, Any]] = []
        self._buffer = bytearray()
        self._written = 0

    def run(self) -> None:
        try:
            with self.log_path.open("ab") as log:
                while True:
                    chunk = self.stream.read(64 * 1024)
                    if not chunk:
                        break
                    self._buffer.extend(chunk)
                    while True:
                        newline = self._buffer.find(b"\n")
                        if newline < 0:
                            if len(self._buffer) > self.max_line_bytes:
                                self.output_limit.set()
                                self._buffer.clear()
                            break
                        raw_line = bytes(self._buffer[: newline + 1])
                        del self._buffer[: newline + 1]
                        if not self._store_line(log, raw_line):
                            self._buffer.clear()
                            break
                if self._buffer and not self.output_limit.is_set():
                    self._store_line(log, bytes(self._buffer))
                    self._buffer.clear()
                log.flush()
                os.fsync(log.fileno())
        except OSError as exc:
            self.invalid_reason = f"could not capture Codex JSONL: {exc.__class__.__name__}"
        finally:
            try:
                self.stream.close()
            except OSError:
                pass

    def _store_line(self, log: Any, raw_line: bytes) -> bool:
        if not raw_line.strip():
            return True
        if len(raw_line) > self.max_line_bytes:
            self.output_limit.set()
            return False
        redacted_line = raw_line
        if self.secret and self.secret in raw_line:
            redacted_line = raw_line.replace(self.secret, b"[REDACTED]")
            self.secret_echoed.set()
        if self._written + len(redacted_line) > self.max_bytes:
            self.output_limit.set()
            return False
        log.write(redacted_line)
        self._written += len(redacted_line)

        try:
            event = json.loads(redacted_line)
        except (json.JSONDecodeError, UnicodeDecodeError):
            self.invalid_reason = "Codex emitted malformed JSONL"
            return True
        if not isinstance(event, dict) or not isinstance(event.get("type"), str):
            self.invalid_reason = "Codex emitted a JSONL event without a string type"
            return True
        self._consume_event(event)
        return True

    def _consume_event(self, event: dict[str, Any]) -> None:
        event_type = event["type"]
        if event_type == "thread.started":
            thread_id = event.get("thread_id")
            if not isinstance(thread_id, str) or not thread_id:
                self.invalid_reason = "thread.started did not contain a thread_id"
            elif self.thread_id is not None and self.thread_id != thread_id:
                self.invalid_reason = "Codex emitted conflicting thread IDs"
            else:
                self.thread_id = thread_id
        elif event_type in ("turn.failed", "thread.failed"):
            self.failed_event = event_type
        elif event_type == "error":
            self.failed_event = "error"
        elif event_type == "turn.completed":
            self.turn_completed = True

        if not event_type.startswith("item."):
            return
        item = event.get("item")
        if not isinstance(item, dict):
            self.invalid_reason = "item event did not contain an item object"
            return
        item_type = item.get("type")
        if not isinstance(item_type, str):
            self.invalid_reason = "item event did not contain a string item type"
            return
        if item_type == "mcp_tool_call":
            server = item.get("server")
            tool = item.get("tool")
            if len(self.mcp_calls) >= 1024:
                self.invalid_reason = "Codex emitted too many MCP tool events"
            else:
                self.mcp_calls.append({
                    "event": event_type,
                    "item_id": item.get("id") if isinstance(item.get("id"), str) else None,
                    "server": server if isinstance(server, str) else None,
                    "tool": tool if isinstance(tool, str) else None,
                })
            if server != "pawnpilot":
                self.invalid_reason = "Codex called an MCP server other than pawnpilot"
            elif tool not in PAWNPILOT_TOOLS:
                self.invalid_reason = "Codex called a tool outside the PawnPilot allowlist"
        elif item_type == "error":
            message = item.get("message")
            expected_warning = (
                f"Model metadata for `{self.model}{_MODEL_METADATA_WARNING_SUFFIX}"
                if self.model in _NONFATAL_MODEL_METADATA_WARNING_MODELS
                else None
            )
            if not isinstance(message, str) or expected_warning is None or not message.startswith(expected_warning):
                self.failed_event = "error item"
        elif item_type not in NON_TOOL_ITEM_TYPES:
            self.invalid_reason = f"unexpected Codex tool event: {item_type}"
        if event_type == "item.completed" and item_type == "agent_message":
            text = item.get("text")
            if isinstance(text, str):
                self.answer_text = text


class _WindowsJob:
    """Own Codex and its children through a kill-on-close Windows Job Object."""

    _JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9
    _JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000

    def __init__(self) -> None:
        if os.name != "nt":
            raise OSError("Windows Job Objects are only available on Windows")
        self._kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self._kernel32.CreateJobObjectW.restype = ctypes.c_void_p
        self._kernel32.CreateJobObjectW.argtypes = (ctypes.c_void_p, ctypes.c_wchar_p)
        self._kernel32.SetInformationJobObject.restype = ctypes.c_int
        self._kernel32.SetInformationJobObject.argtypes = (
            ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32,
        )
        self._kernel32.AssignProcessToJobObject.restype = ctypes.c_int
        self._kernel32.AssignProcessToJobObject.argtypes = (ctypes.c_void_p, ctypes.c_void_p)
        self._kernel32.TerminateJobObject.restype = ctypes.c_int
        self._kernel32.TerminateJobObject.argtypes = (ctypes.c_void_p, ctypes.c_uint32)
        self._kernel32.CloseHandle.restype = ctypes.c_int
        self._kernel32.CloseHandle.argtypes = (ctypes.c_void_p,)
        self._handle = self._kernel32.CreateJobObjectW(None, None)
        if not self._handle:
            raise ctypes.WinError(ctypes.get_last_error())
        information = _JobExtendedLimitInformation()
        information.BasicLimitInformation.LimitFlags = self._JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        ok = self._kernel32.SetInformationJobObject(
            self._handle,
            self._JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
            ctypes.byref(information),
            ctypes.sizeof(information),
        )
        if not ok:
            error = ctypes.WinError(ctypes.get_last_error())
            self.close()
            raise error

    def assign(self, process: subprocess.Popen[bytes]) -> None:
        process_handle = getattr(process, "_handle", None)
        if process_handle is None:
            raise OSError("Codex process handle is unavailable")
        if not self._kernel32.AssignProcessToJobObject(self._handle, int(process_handle)):
            raise ctypes.WinError(ctypes.get_last_error())

    def terminate(self, exit_code: int = 1) -> None:
        if self._handle and not self._kernel32.TerminateJobObject(self._handle, exit_code):
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self) -> None:
        if getattr(self, "_handle", None):
            handle, self._handle = self._handle, None
            self._kernel32.CloseHandle(handle)


def _resume_suspended_process(process_id: int) -> None:
    """Resume the one CREATE_SUSPENDED primary thread after job assignment."""
    if os.name != "nt":
        return
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
    kernel32.CreateToolhelp32Snapshot.argtypes = (ctypes.c_uint32, ctypes.c_uint32)
    kernel32.Thread32First.restype = ctypes.c_int
    kernel32.Thread32First.argtypes = (ctypes.c_void_p, ctypes.POINTER(_ThreadEntry32))
    kernel32.Thread32Next.restype = ctypes.c_int
    kernel32.Thread32Next.argtypes = (ctypes.c_void_p, ctypes.POINTER(_ThreadEntry32))
    kernel32.OpenThread.restype = ctypes.c_void_p
    kernel32.OpenThread.argtypes = (ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32)
    kernel32.ResumeThread.restype = ctypes.c_uint32
    kernel32.ResumeThread.argtypes = (ctypes.c_void_p,)
    kernel32.CloseHandle.restype = ctypes.c_int
    kernel32.CloseHandle.argtypes = (ctypes.c_void_p,)

    snapshot = kernel32.CreateToolhelp32Snapshot(0x00000004, 0)  # TH32CS_SNAPTHREAD
    invalid_handle = ctypes.c_void_p(-1).value
    if snapshot in (None, invalid_handle):
        raise ctypes.WinError(ctypes.get_last_error())
    thread_ids: list[int] = []
    try:
        entry = _ThreadEntry32()
        entry.dwSize = ctypes.sizeof(entry)
        ok = kernel32.Thread32First(snapshot, ctypes.byref(entry))
        while ok:
            if entry.th32OwnerProcessID == process_id:
                thread_ids.append(entry.th32ThreadID)
            entry.dwSize = ctypes.sizeof(entry)
            ok = kernel32.Thread32Next(snapshot, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snapshot)

    if len(thread_ids) != 1:
        raise OSError("expected exactly one suspended primary thread")
    thread = kernel32.OpenThread(0x0002, 0, thread_ids[0])  # THREAD_SUSPEND_RESUME
    if not thread:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        previous_suspend_count = kernel32.ResumeThread(thread)
        if previous_suspend_count == 0xFFFFFFFF:
            raise ctypes.WinError(ctypes.get_last_error())
        if previous_suspend_count != 1:
            raise OSError("unexpected primary thread suspend count")
    finally:
        kernel32.CloseHandle(thread)


class CodexDriver:
    """Run one constrained Codex turn and persist bounded machine-readable evidence."""

    def __init__(
        self,
        codex_path: str | Path,
        python_path: str | Path,
        repo_root: str | Path,
        config_path: str | Path,
        workspace: str | Path,
        model: str = "gpt-6-luna",
        timeout_seconds: float = 180,
        max_log_bytes: int = 8 * 1024 * 1024,
        *,
        termination_grace_seconds: float = 2,
        poll_interval_seconds: float = 0.05,
        popen_factory: Callable[..., subprocess.Popen[bytes]] = subprocess.Popen,
        job_factory: Callable[[], Any] | None = None,
        resume_process: Callable[[int], None] | None = None,
        windows_mode: bool | None = None,
    ) -> None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be positive")
        if max_log_bytes <= 0:
            raise ValueError("max_log_bytes must be positive")
        if termination_grace_seconds < 0 or poll_interval_seconds <= 0:
            raise ValueError("termination and polling intervals are invalid")
        self.codex_path = str(codex_path)
        self.python_path = str(python_path)
        self.repo_root = Path(repo_root).resolve()
        self.config_path = Path(config_path).resolve()
        self.m5c_root = (self.repo_root / "artifacts" / "m5c").resolve()
        self.workspace = Path(workspace).resolve()
        try:
            self.workspace.relative_to(self.m5c_root)
        except ValueError as exc:
            raise ValueError("workspace must be inside repo_root/artifacts/m5c") from exc
        if not isinstance(model, str) or not model.strip():
            raise ValueError("model must be a non-empty string")
        self.model = model
        self.timeout_seconds = timeout_seconds
        self.max_log_bytes = max_log_bytes
        self.termination_grace_seconds = termination_grace_seconds
        self.poll_interval_seconds = poll_interval_seconds
        self.popen_factory = popen_factory
        self.job_factory = job_factory
        self.resume_process = resume_process
        self.windows_mode = os.name == "nt" if windows_mode is None else windows_mode
        self._run_lock = threading.Lock()

    def run(
        self,
        prompt: str,
        dispatch_token: str,
        cancelled: Callable[[], bool] = lambda: False,
    ) -> dict[str, Any]:
        if not isinstance(prompt, str) or not prompt.strip():
            raise ValueError("prompt must be a non-empty string")
        if not isinstance(dispatch_token, str) or not dispatch_token or "\n" in dispatch_token or "\r" in dispatch_token:
            raise ValueError("dispatch_token must be non-empty and single-line")
        if dispatch_token in prompt:
            raise ValueError("prompt must not contain dispatch_token")
        if not callable(cancelled):
            raise ValueError("cancelled must be callable")
        if not self._run_lock.acquire(blocking=False):
            raise RuntimeError("CodexDriver already has an active run")

        try:
            return self._run(prompt, dispatch_token, cancelled)
        finally:
            self._run_lock.release()

    def _run(self, prompt: str, dispatch_token: str, cancelled: Callable[[], bool]) -> dict[str, Any]:
        self.workspace.mkdir(parents=True, exist_ok=True)
        try:
            self.workspace.resolve().relative_to(self.m5c_root)
        except ValueError as exc:
            raise ValueError("workspace resolved outside repo_root/artifacts/m5c") from exc

        run_id = uuid4().hex
        log_path = self.workspace / f"codex-{run_id}.jsonl"
        metadata_path = self.workspace / f"codex-{run_id}.meta.json"
        schema_path = self.workspace / f"codex-{run_id}.schema.json"
        schema_path.write_text(json.dumps(OUTPUT_SCHEMA, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

        result: dict[str, Any] = {
            "status": "failed",
            "exit_code": None,
            "thread_id": None,
            "answer": None,
            "log_path": str(log_path),
            "reason": None,
            "pid": None,
            "metadata_path": str(metadata_path),
            "mcp_calls": [],
        }
        started_at = _utc_now()
        process = None
        job = None
        stdout_thread = None
        stderr_thread = None
        capture = None
        forced_status: str | None = None
        reason: str | None = None
        try:
            try:
                was_cancelled = cancelled()
            except Exception as exc:
                was_cancelled = True
                forced_status, reason = "cancelled", f"cancellation callback failed ({exc.__class__.__name__}); stopped Codex"
            if was_cancelled:
                forced_status = forced_status or "cancelled"
                reason = reason or "cancelled before Codex start"
                return self._finish(result, metadata_path, started_at, forced_status, reason)
            try:
                job = self._create_job()
            except Exception as exc:
                forced_status, reason = "job_assignment_failed", f"could not create Codex process job: {exc.__class__.__name__}"
                return self._finish(result, metadata_path, started_at, forced_status, reason)
            argv = self._build_argv(schema_path)
            environment = os.environ.copy()
            environment["PAWNPILOT_DISPATCH_TOKEN"] = dispatch_token
            environment["PAWNPILOT_CONFIG"] = str(self.config_path)
            popen_options: dict[str, Any] = {
                "cwd": str(self.workspace),
                "env": environment,
                "stdin": subprocess.PIPE,
                "stdout": subprocess.PIPE,
                "stderr": subprocess.PIPE,
                "shell": False,
                "bufsize": 0,
            }
            if self.windows_mode:
                popen_options["creationflags"] = (
                    getattr(subprocess, "CREATE_NO_WINDOW", 0x08000000)
                    | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0x00000200)
                    | getattr(subprocess, "CREATE_SUSPENDED", 0x00000004)
                )
            else:
                popen_options["start_new_session"] = True
            process = self.popen_factory(argv, **popen_options)
            result["pid"] = process.pid

            if job is not None:
                try:
                    job.assign(process)
                except Exception as exc:
                    self._terminate_unassigned(process)
                    result["exit_code"] = process.poll()
                    forced_status = "job_assignment_failed"
                    reason = f"could not assign Codex to process job: {exc.__class__.__name__}"
                    return self._finish(result, metadata_path, started_at, forced_status, reason)

            capture = _JsonlCapture(process.stdout, log_path, self.max_log_bytes, dispatch_token, self.model)
            stdout_thread = threading.Thread(target=capture.run, name="pawnpilot-codex-stdout", daemon=True)
            stderr_thread = threading.Thread(target=self._drain, args=(process.stderr,),
                                             name="pawnpilot-codex-stderr", daemon=True)
            stdout_thread.start()
            stderr_thread.start()
            if self.windows_mode:
                try:
                    (self.resume_process or _resume_suspended_process)(process.pid)
                except Exception as exc:
                    forced_status = "job_assignment_failed"
                    reason = f"could not resume assigned Codex process: {exc.__class__.__name__}"
                    self._terminate_tree(process, job)
                    try:
                        result["exit_code"] = process.wait(timeout=max(self.termination_grace_seconds, 0.1))
                    except (OSError, subprocess.TimeoutExpired):
                        pass
                    self._join_thread(stdout_thread)
                    self._join_thread(stderr_thread)
                    result["thread_id"] = capture.thread_id
                    result["mcp_calls"] = capture.mcp_calls
                    return self._finish(result, metadata_path, started_at, forced_status, reason)
            try:
                if cancelled():
                    forced_status, reason = "cancelled", "cancelled before Codex prompt was sent"
                    self._terminate_tree(process, job)
                    try:
                        result["exit_code"] = process.wait(timeout=max(self.termination_grace_seconds, 0.1))
                    except (OSError, subprocess.TimeoutExpired):
                        pass
                    self._join_thread(stdout_thread)
                    self._join_thread(stderr_thread)
                    result["thread_id"] = capture.thread_id
                    result["mcp_calls"] = capture.mcp_calls
                    return self._finish(result, metadata_path, started_at, forced_status, reason)
            except Exception as exc:
                forced_status, reason = "cancelled", f"cancellation callback failed ({exc.__class__.__name__}) before Codex prompt"
                self._terminate_tree(process, job)
                try:
                    result["exit_code"] = process.wait(timeout=max(self.termination_grace_seconds, 0.1))
                except (OSError, subprocess.TimeoutExpired):
                    pass
                self._join_thread(stdout_thread)
                self._join_thread(stderr_thread)
                result["thread_id"] = capture.thread_id
                result["mcp_calls"] = capture.mcp_calls
                return self._finish(result, metadata_path, started_at, forced_status, reason)
            writer_thread = threading.Thread(target=self._write_prompt, args=(process.stdin, prompt),
                                             name="pawnpilot-codex-stdin", daemon=True)
            writer_thread.start()

            deadline = time.monotonic() + self.timeout_seconds
            while process.poll() is None:
                if capture.output_limit.is_set():
                    forced_status, reason = "output_limit", "Codex JSONL exceeded the configured byte limit"
                    self._terminate_tree(process, job)
                    break
                if capture.secret_echoed.is_set():
                    forced_status, reason = "invalid_output", "Codex output contained the dispatch token; log redacted"
                    self._terminate_tree(process, job)
                    break
                if capture.invalid_reason:
                    forced_status, reason = "invalid_output", capture.invalid_reason
                    self._terminate_tree(process, job)
                    break
                if capture.failed_event:
                    forced_status, reason = "failed", f"Codex emitted {capture.failed_event}"
                    self._terminate_tree(process, job)
                    break
                try:
                    was_cancelled = cancelled()
                except Exception as exc:
                    was_cancelled = True
                    forced_status, reason = "cancelled", f"cancellation callback failed ({exc.__class__.__name__}); stopped Codex"
                if was_cancelled:
                    forced_status = forced_status or "cancelled"
                    reason = reason or "cancelled by supervisor"
                    self._terminate_tree(process, job)
                    break
                if time.monotonic() >= deadline:
                    forced_status, reason = "timed_out", "Codex turn exceeded timeout_seconds"
                    self._terminate_tree(process, job)
                    break
                time.sleep(self.poll_interval_seconds)

            try:
                result["exit_code"] = process.wait(timeout=max(self.termination_grace_seconds, 0.1))
            except subprocess.TimeoutExpired:
                forced_status = forced_status or "failed"
                reason = reason or "Codex process did not exit after termination"
                self._terminate_tree(process, job)
                try:
                    result["exit_code"] = process.wait(timeout=max(self.termination_grace_seconds, 0.1))
                except subprocess.TimeoutExpired:
                    result["exit_code"] = None

            self._join_thread(stdout_thread)
            self._join_thread(stderr_thread)
            self._join_thread(writer_thread)
            if capture is not None:
                result["thread_id"] = capture.thread_id
                result["mcp_calls"] = capture.mcp_calls

            if capture is not None and capture.output_limit.is_set():
                forced_status, reason = "output_limit", "Codex JSONL exceeded the configured byte limit"
            elif capture is not None and capture.secret_echoed.is_set():
                forced_status, reason = "invalid_output", "Codex output contained the dispatch token; log redacted"
            elif capture is not None and capture.invalid_reason:
                forced_status, reason = "invalid_output", capture.invalid_reason
            elif forced_status is None and capture is not None and capture.failed_event:
                forced_status, reason = "failed", f"Codex emitted {capture.failed_event}"
            elif forced_status is None and result["exit_code"] != 0:
                forced_status, reason = "failed", "Codex exited with a non-zero status"
            elif forced_status is None and capture is not None:
                answer, answer_error = self._parse_answer(capture.answer_text)
                if answer_error:
                    forced_status, reason = "invalid_output", answer_error
                elif not capture.thread_id:
                    forced_status, reason = "invalid_output", "Codex JSONL did not contain thread.started"
                elif not capture.turn_completed:
                    forced_status, reason = "invalid_output", "Codex JSONL did not contain turn.completed"
                else:
                    result["answer"] = answer
                    forced_status, reason = "completed", None

            if forced_status not in (None, "completed"):
                self._terminate_tree(process, job)
            return self._finish(result, metadata_path, started_at, forced_status or "failed", reason)
        except OSError as exc:
            forced_status, reason = "spawn_error", f"could not start/capture Codex: {exc.__class__.__name__}"
            if process is not None:
                self._terminate_tree(process, job)
                try:
                    result["exit_code"] = process.wait(timeout=max(self.termination_grace_seconds, 0.1))
                except (OSError, subprocess.TimeoutExpired):
                    pass
            self._join_thread(stdout_thread)
            self._join_thread(stderr_thread)
            result["thread_id"] = capture.thread_id if capture else None
            result["mcp_calls"] = capture.mcp_calls if capture else []
            return self._finish(result, metadata_path, started_at, forced_status, reason)
        finally:
            if job is not None:
                try:
                    job.close()
                except Exception:
                    pass

    def _build_argv(self, schema_path: Path) -> list[str]:
        argv = [
            self.codex_path,
            "exec",
            "--json",
            "--strict-config",
            "--output-schema",
            str(schema_path),
            "--model",
            self.model,
            "--ignore-user-config",
            "--disable",
            "shell_tool",
            "--disable",
            "multi_agent",
            "--sandbox",
            "read-only",
            "-C",
            str(self.workspace),
            "--skip-git-repo-check",
        ]
        config = {
            "model_reasoning_effort": "xhigh",
            "mcp_servers.pawnpilot.enabled": True,
            "mcp_servers.pawnpilot.command": self.python_path,
            "mcp_servers.pawnpilot.args": ["-m", "pawnpilot_mcp"],
            "mcp_servers.pawnpilot.cwd": str(self.repo_root),
            "mcp_servers.pawnpilot.env_vars": ["PAWNPILOT_DISPATCH_TOKEN", "PAWNPILOT_CONFIG"],
            "mcp_servers.pawnpilot.required": True,
            "mcp_servers.pawnpilot.enabled_tools": list(PAWNPILOT_TOOLS),
            "mcp_servers.pawnpilot.startup_timeout_sec": 20,
            "mcp_servers.pawnpilot.tool_timeout_sec": 40,
            "mcp_servers.pawnpilot.default_tools_approval_mode": "approve",
        }
        for key, value in config.items():
            argv.extend(["-c", f"{key}={json.dumps(value, ensure_ascii=False)}"])
        argv.append("-")
        return argv

    def _create_job(self) -> Any | None:
        if self.job_factory is not None:
            return self.job_factory()
        if self.windows_mode:
            return _WindowsJob()
        return None

    @staticmethod
    def _write_prompt(stream: Any, prompt: str) -> None:
        try:
            stream.write(prompt.encode("utf-8"))
            stream.close()
        except (BrokenPipeError, OSError):
            try:
                stream.close()
            except OSError:
                pass

    @staticmethod
    def _drain(stream: Any) -> None:
        try:
            while stream.read(64 * 1024):
                pass
        except OSError:
            pass
        finally:
            try:
                stream.close()
            except OSError:
                pass

    @staticmethod
    def _join_thread(thread: threading.Thread | None) -> None:
        if thread is not None:
            thread.join(timeout=2)

    def _terminate_unassigned(self, process: Any) -> None:
        for stream in (process.stdin, process.stdout, process.stderr):
            try:
                if stream is not None:
                    stream.close()
            except OSError:
                pass
        try:
            process.kill()
        except OSError:
            pass
        try:
            process.wait(timeout=max(self.termination_grace_seconds, 0.1))
        except (OSError, subprocess.TimeoutExpired):
            pass

    def _terminate_tree(self, process: Any, job: Any | None) -> None:
        if process.poll() is not None:
            if job is not None:
                try:
                    job.terminate(1)
                except Exception:
                    pass
            elif not self.windows_mode:
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    return
                except OSError:
                    return
                if self.termination_grace_seconds:
                    time.sleep(self.termination_grace_seconds)
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                except OSError:
                    pass
            return
        if job is not None:
            if self.windows_mode:
                try:
                    process.send_signal(signal.CTRL_BREAK_EVENT)
                except Exception:
                    pass
                try:
                    process.wait(timeout=self.termination_grace_seconds)
                    return
                except subprocess.TimeoutExpired:
                    pass
            try:
                job.terminate(1)
            except Exception:
                try:
                    process.kill()
                except OSError:
                    pass
        elif self.windows_mode:
            try:
                process.terminate()
            except OSError:
                pass
        else:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                return
            except OSError:
                try:
                    process.terminate()
                except OSError:
                    pass
        try:
            process.wait(timeout=self.termination_grace_seconds)
            return
        except subprocess.TimeoutExpired:
            pass
        if job is not None:
            try:
                job.terminate(1)
            except Exception:
                pass
        elif self.windows_mode:
            try:
                process.kill()
            except OSError:
                pass
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            except OSError:
                try:
                    process.kill()
                except OSError:
                    pass

    @staticmethod
    def _parse_answer(text: str | None) -> tuple[dict[str, str] | None, str | None]:
        if not isinstance(text, str):
            return None, "Codex did not emit a final agent_message"
        try:
            answer = json.loads(text)
        except json.JSONDecodeError:
            return None, "final agent_message is not valid JSON"
        if not isinstance(answer, dict):
            return None, "final answer must be a JSON object"
        if set(answer) != {"summary", "plan", "disposition"}:
            return None, "final answer keys do not match the required schema"
        if not all(isinstance(answer.get(key), str) for key in ("summary", "plan", "disposition")):
            return None, "final answer fields must all be strings"
        if answer["disposition"] not in DISPOSITION_VALUES:
            return None, "final answer disposition is unsupported"
        return answer, None

    def _finish(
        self,
        result: dict[str, Any],
        metadata_path: Path,
        started_at: str,
        status: str,
        reason: str | None,
    ) -> dict[str, Any]:
        secret = ""  # Token is deliberately not part of the result or metadata.
        result["status"] = status
        result["reason"] = _clean_reason(reason, secret) if reason else None
        metadata = {
            "started_at": started_at,
            "finished_at": _utc_now(),
            "pid": result.get("pid"),
            "exit_code": result.get("exit_code"),
            "thread_id": result.get("thread_id"),
            "status": status,
            "reason": result["reason"],
            "mcp_calls": result.get("mcp_calls", []),
        }
        temporary = metadata_path.with_suffix(metadata_path.suffix + ".tmp")
        temporary.write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, metadata_path)
        return result
