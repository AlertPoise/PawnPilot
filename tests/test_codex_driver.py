"""Unit tests for the bounded Codex CLI supervisor driver; no real Codex invocation."""

import io
import json
import shutil
import subprocess
import unittest
from pathlib import Path
from uuid import uuid4

from pawnpilot_supervisor.codex_driver import CodexDriver


TOKEN = "dispatch-secret-98c4"
ROOT = Path(__file__).resolve().parents[1]


class FakeStdin:
    def __init__(self, order=None):
        self.data = bytearray()
        self.closed = False
        self.order = order

    def write(self, value):
        if self.order is not None:
            self.order.append("prompt")
        self.data.extend(value)
        return len(value)

    def close(self):
        self.closed = True


class FakeProcess:
    def __init__(self, stdout=b"", returncode=0, order=None):
        self.stdin = FakeStdin(order)
        self.stdout = io.BytesIO(stdout)
        self.stderr = io.BytesIO(b"")
        self.pid = 4242
        self.returncode = returncode
        self.killed = False
        self.order = order

    def poll(self):
        return self.returncode

    def wait(self, timeout=None):
        if self.returncode is None:
            raise subprocess.TimeoutExpired("fake-codex", timeout)
        return self.returncode

    def kill(self):
        self.killed = True
        self.returncode = -9

    def terminate(self):
        self.killed = True
        self.returncode = -15


class FakeJob:
    def __init__(self, assign_error=None, order=None):
        self.process = None
        self.assign_error = assign_error
        self.assigned = False
        self.closed = False
        self.terminated = False
        self.order = order

    def assign(self, process):
        if self.order is not None:
            self.order.append("assign")
        if self.assign_error:
            raise self.assign_error
        self.process = process
        self.assigned = True

    def terminate(self, _exit_code=1):
        self.terminated = True
        if self.process is not None:
            self.process.kill()

    def close(self):
        self.closed = True


def event_line(event):
    return (json.dumps(event, separators=(",", ":")) + "\n").encode("utf-8")


def valid_events(answer=None):
    answer = answer or {"summary": "observed", "plan": "observe again", "disposition": "wait"}
    return b"".join([
        event_line({"type": "thread.started", "thread_id": "cli-thread-1"}),
        event_line({"type": "turn.started"}),
        event_line({"type": "item.started", "item": {
            "id": "item_1", "type": "mcp_tool_call", "server": "pawnpilot",
            "tool": "observe", "arguments": {},
        }}),
        event_line({"type": "item.completed", "item": {
            "id": "item_1", "type": "mcp_tool_call", "server": "pawnpilot",
            "tool": "observe", "arguments": {}, "result": {"ok": True},
        }}),
        event_line({"type": "item.completed", "item": {
            "id": "item_2", "type": "agent_message", "text": json.dumps(answer),
        }}),
        event_line({"type": "turn.completed", "usage": {"input_tokens": 1, "output_tokens": 2}}),
    ])


class CodexDriverTests(unittest.TestCase):
    def setUp(self):
        self.repo = ROOT / "artifacts" / "m5c" / f".codex-driver-test-{uuid4().hex}"
        self.repo.mkdir(parents=True)
        self.addCleanup(shutil.rmtree, self.repo)
        self.workspace = self.repo / "artifacts" / "m5c" / "workspace"
        self.config = self.repo / "run-config.json"
        self.config.write_text("{}", encoding="utf-8")
        self.process = None
        self.order = []
        self.job = FakeJob(order=self.order)
        self.argv = None
        self.popen_options = None

    def factory(self, stdout=None, returncode=0, error=None):
        def popen(argv, **options):
            self.order.append("spawn")
            self.argv = argv
            self.popen_options = options
            if error:
                raise error
            self.process = FakeProcess(valid_events() if stdout is None else stdout, returncode, self.order)
            return self.process
        return popen

    def driver(self, **kwargs):
        return CodexDriver(
            codex_path="codex.exe",
            python_path="python.exe",
            repo_root=self.repo,
            config_path=self.config,
            workspace=self.workspace,
            popen_factory=kwargs.pop("popen_factory", self.factory()),
            job_factory=kwargs.pop("job_factory", lambda: self.job),
            timeout_seconds=kwargs.pop("timeout_seconds", 1),
            poll_interval_seconds=kwargs.pop("poll_interval_seconds", 0.001),
            termination_grace_seconds=kwargs.pop("termination_grace_seconds", 0.001),
            windows_mode=kwargs.pop("windows_mode", False),
            resume_process=kwargs.pop("resume_process", lambda _pid: self.order.append("resume")),
            **kwargs,
        )

    def test_builds_isolated_argv_and_returns_valid_structured_turn(self):
        driver = self.driver(windows_mode=True)
        result = driver.run("Observe the current game and decide one bounded step.", TOKEN)

        self.assertEqual(result["status"], "completed")
        self.assertEqual(result["exit_code"], 0)
        self.assertEqual(result["thread_id"], "cli-thread-1")
        self.assertEqual(result["answer"]["disposition"], "wait")
        self.assertEqual(result["mcp_calls"][0]["server"], "pawnpilot")
        self.assertIn("--json", self.argv)
        self.assertEqual(self.argv[self.argv.index("--model") + 1], "gpt-6-luna")
        self.assertIn("--output-schema", self.argv)
        self.assertIn("--ignore-user-config", self.argv)
        self.assertIn("--sandbox", self.argv)
        self.assertIn("read-only", self.argv)
        self.assertIn("--skip-git-repo-check", self.argv)
        self.assertIn("-C", self.argv)
        self.assertIn("--disable", self.argv)
        self.assertNotIn("--ephemeral", self.argv)
        self.assertNotIn("--last", self.argv)
        self.assertNotIn("resume", self.argv)
        self.assertNotIn(TOKEN, " ".join(self.argv))
        self.assertEqual(self.process.stdin.data.decode("utf-8"), "Observe the current game and decide one bounded step.")
        self.assertEqual(self.popen_options["shell"], False)
        self.assertEqual(self.popen_options["cwd"], str(self.workspace.resolve()))
        self.assertEqual(self.popen_options["env"]["PAWNPILOT_DISPATCH_TOKEN"], TOKEN)
        self.assertEqual(self.popen_options["env"]["PAWNPILOT_CONFIG"], str(self.config.resolve()))
        self.assertTrue(self.job.assigned)
        self.assertTrue(self.job.closed)
        self.assertEqual(self.order, ["spawn", "assign", "resume", "prompt"])
        self.assertTrue(self.popen_options["creationflags"] & getattr(subprocess, "CREATE_SUSPENDED", 0x00000004))
        log = Path(result["log_path"]).read_text(encoding="utf-8")
        self.assertIn("thread.started", log)
        self.assertNotIn(TOKEN, log)
        self.assertNotIn(TOKEN, Path(result["metadata_path"]).read_text(encoding="utf-8"))
        self.assertEqual(json.loads(Path(result["metadata_path"]).read_text(encoding="utf-8"))["pid"], 4242)

        config_values = [self.argv[index + 1] for index, value in enumerate(self.argv[:-1]) if value == "-c"]
        mcp_config = {
            entry.split("=", 1)[0]: json.loads(entry.split("=", 1)[1])
            for entry in config_values
        }
        self.assertEqual(mcp_config["mcp_servers.pawnpilot.enabled_tools"], [
            "health_check", "observe", "act", "get_action_status", "wait_for_update",
        ])
        self.assertEqual(mcp_config["mcp_servers.pawnpilot.env_vars"], [
            "PAWNPILOT_DISPATCH_TOKEN", "PAWNPILOT_CONFIG",
        ])
        self.assertEqual(mcp_config["model_reasoning_effort"], "xhigh")

    def test_spawn_error_is_recorded_without_exposing_credentials(self):
        driver = self.driver(popen_factory=self.factory(error=FileNotFoundError("missing codex")))
        result = driver.run("observe", TOKEN)
        self.assertEqual(result["status"], "spawn_error")
        self.assertIsNone(result["pid"])
        self.assertEqual(json.loads(Path(result["metadata_path"]).read_text(encoding="utf-8"))["status"], "spawn_error")
        self.assertNotIn(TOKEN, Path(result["metadata_path"]).read_text(encoding="utf-8"))

    def test_job_assignment_failure_kills_before_prompt_is_sent(self):
        self.job = FakeJob(assign_error=OSError("cannot assign"))
        self.job.order = self.order
        driver = self.driver(windows_mode=True)
        result = driver.run("do not start business", TOKEN)
        self.assertEqual(result["status"], "job_assignment_failed")
        self.assertTrue(self.process.killed)
        self.assertEqual(bytes(self.process.stdin.data), b"")
        self.assertFalse(self.job.assigned)
        self.assertEqual(self.order, ["spawn", "assign"])
        self.assertTrue(self.popen_options["creationflags"] & getattr(subprocess, "CREATE_SUSPENDED", 0x00000004))

    def test_timeout_terminates_only_the_owned_job(self):
        process = FakeProcess(stdout=b"", returncode=None)
        driver = self.driver(popen_factory=lambda _argv, **_options: self._remember_process(process),
                             timeout_seconds=0.01)
        result = driver.run("bounded task", TOKEN)
        self.assertEqual(result["status"], "timed_out")
        self.assertTrue(self.job.terminated)
        self.assertTrue(process.killed)
        self.assertEqual(result["exit_code"], -9)

    def test_cancel_callback_terminates_owned_job(self):
        process = FakeProcess(stdout=b"", returncode=None)
        calls = iter([False, True])

        def popen(_argv, **_options):
            self.order.append("spawn")
            return self._remember_process(process)

        driver = self.driver(
            popen_factory=popen,
            windows_mode=True,
        )
        result = driver.run("cancel me", TOKEN, cancelled=lambda: next(calls, True))
        self.assertEqual(result["status"], "cancelled")
        self.assertTrue(self.job.terminated)
        self.assertTrue(process.killed)
        self.assertEqual(bytes(process.stdin.data), b"")
        self.assertEqual(self.order, ["spawn", "assign", "resume"])

    def test_truncated_jsonl_fails_closed(self):
        output = event_line({"type": "thread.started", "thread_id": "cli-thread-1"}) + b'{"type":"turn.completed"'
        driver = self.driver(popen_factory=self.factory(stdout=output))
        result = driver.run("observe", TOKEN)
        self.assertEqual(result["status"], "invalid_output")
        self.assertIn("malformed JSONL", result["reason"])
        self.assertIsNone(result["answer"])

    def test_log_limit_fails_closed_and_never_exceeds_limit(self):
        output = valid_events()
        driver = self.driver(popen_factory=self.factory(stdout=output), max_log_bytes=96)
        result = driver.run("observe", TOKEN)
        self.assertEqual(result["status"], "output_limit")
        self.assertLessEqual(Path(result["log_path"]).stat().st_size, 96)
        self.assertIsNone(result["answer"])

    def test_invalid_final_schema_fails_closed(self):
        output = valid_events({"summary": "missing plan", "disposition": "acted"})
        driver = self.driver(popen_factory=self.factory(stdout=output))
        result = driver.run("observe", TOKEN)
        self.assertEqual(result["status"], "invalid_output")
        self.assertIn("keys do not match", result["reason"])

    def test_codex_error_event_fails_even_with_zero_exit(self):
        output = b"".join([
            event_line({"type": "thread.started", "thread_id": "cli-thread-1"}),
            event_line({"type": "error", "message": "failed"}),
            event_line({"type": "item.completed", "item": {
                "id": "item_2", "type": "agent_message",
                "text": json.dumps({"summary": "x", "plan": "y", "disposition": "wait"}),
            }}),
            event_line({"type": "turn.completed"}),
        ])
        driver = self.driver(popen_factory=self.factory(stdout=output))
        result = driver.run("observe", TOKEN)
        self.assertEqual(result["status"], "failed")
        self.assertIn("error", result["reason"])

    def test_known_luna_metadata_warning_is_nonfatal_and_retained(self):
        for model in ("gpt-6-luna", "gpt-5.6-luna"):
            with self.subTest(model=model):
                warning = event_line({"type": "item.completed", "item": {
                    "id": "item_0",
                    "type": "error",
                    "message": (
                        f"Model metadata for `{model}` not found. Defaulting to fallback metadata; "
                        "this can degrade performance and cause issues."
                    ),
                }})
                first, rest = valid_events().split(b"\n", 1)
                output = first + b"\n" + warning + rest
                driver = self.driver(popen_factory=self.factory(stdout=output), model=model)

                result = driver.run("observe", TOKEN)

                self.assertEqual(result["status"], "completed")
                self.assertEqual(result["answer"]["disposition"], "wait")
                self.assertIn(
                    f"Model metadata for `{model}` not found".encode(),
                    Path(result["log_path"]).read_bytes(),
                )

    def test_unknown_error_item_fails_and_terminates_owned_tree(self):
        output = b"".join([
            event_line({"type": "thread.started", "thread_id": "cli-thread-1"}),
            event_line({"type": "item.completed", "item": {
                "id": "item_error", "type": "error", "message": "unknown Codex diagnostic",
            }}),
        ])
        driver = self.driver(popen_factory=self.factory(stdout=output, returncode=None))

        result = driver.run("observe", TOKEN)

        self.assertEqual(result["status"], "failed")
        self.assertIn("error item", result["reason"])
        self.assertTrue(self.job.terminated)
        self.assertIn("unknown Codex diagnostic", Path(result["log_path"]).read_text(encoding="utf-8"))

    def test_metadata_warning_for_unselected_luna_model_still_fails(self):
        output = b"".join([
            event_line({"type": "thread.started", "thread_id": "cli-thread-1"}),
            event_line({"type": "item.completed", "item": {
                "id": "item_warning",
                "type": "error",
                "message": (
                    "Model metadata for `gpt-5.6-luna` not found. "
                    "Defaulting to fallback metadata; this can degrade performance and cause issues."
                ),
            }}),
        ])
        driver = self.driver(popen_factory=self.factory(stdout=output, returncode=None))

        result = driver.run("observe", TOKEN)

        self.assertEqual(result["status"], "failed")
        self.assertIn("error item", result["reason"])
        self.assertTrue(self.job.terminated)

    def test_non_pawnpilot_mcp_and_shell_events_fail_closed(self):
        for item in (
            {"id": "item_x", "type": "mcp_tool_call", "server": "other", "tool": "read"},
            {"id": "item_x", "type": "command_execution", "command": "dir"},
            {"id": "item_x", "type": "file_change", "changes": []},
        ):
            for returncode in (None, 0):
                with self.subTest(item_type=item["type"], returncode=returncode):
                    self.job = FakeJob(order=self.order)
                    output = b"".join([
                        event_line({"type": "thread.started", "thread_id": "cli-thread-1"}),
                        event_line({"type": "item.started", "item": item}),
                    ])
                    driver = self.driver(popen_factory=self.factory(stdout=output, returncode=returncode))
                    result = driver.run("observe", TOKEN)
                    self.assertEqual(result["status"], "invalid_output")
                    self.assertTrue(self.job.terminated)
                    self.assertIn(item["type"], Path(result["log_path"]).read_text(encoding="utf-8"))
                    self.assertEqual(
                        json.loads(Path(result["metadata_path"]).read_text(encoding="utf-8"))["status"],
                        "invalid_output",
                    )

    def test_dispatch_token_echo_is_redacted_and_rejected(self):
        output = b"".join([
            event_line({"type": "thread.started", "thread_id": "cli-thread-1"}),
            event_line({"type": "error", "message": TOKEN}),
        ])
        driver = self.driver(popen_factory=self.factory(stdout=output))
        result = driver.run("observe", TOKEN)
        self.assertEqual(result["status"], "invalid_output")
        self.assertNotIn(TOKEN.encode(), Path(result["log_path"]).read_bytes())
        self.assertNotIn(TOKEN, Path(result["metadata_path"]).read_text(encoding="utf-8"))

    def test_workspace_must_be_inside_m5c_artifacts(self):
        with self.assertRaisesRegex(ValueError, "inside repo_root/artifacts/m5c"):
            CodexDriver("codex", "python", self.repo, self.config, self.repo / "src", popen_factory=self.factory())

    def _remember_process(self, process):
        self.process = process
        return process


if __name__ == "__main__":
    unittest.main()
