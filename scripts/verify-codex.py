"""Run an actual Codex CLI MCP client; retain its JSONL evidence locally.

Default is read-only. --close-menu is only for an approved, isolated live test.
This script neither installs the Mod nor starts the broker.
"""
import argparse
import json
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--close-menu", action="store_true")
    parser.add_argument("--business", action="store_true", help="Read v3 business completeness and quote evidence; never purchases")
    parser.add_argument("--model", default="gpt-5.6-luna")
    parser.add_argument("--label", default="read-only")
    args = parser.parse_args()
    if not args.label.replace("-", "").replace("_", "").isalnum():
        parser.error("label must be alphanumeric, dash or underscore")
    codex = shutil.which("codex")
    python = ROOT / ".venv" / "Scripts" / "python.exe"
    if not codex or not python.is_file():
        parser.error("Codex CLI and project .venv are required")
    directory = ROOT / "artifacts" / ("m2cd" if args.business else "m2b") / "codex"
    directory.mkdir(parents=True, exist_ok=True)
    output = directory / f"{args.label}.jsonl"
    if output.exists():
        parser.error("evidence label already exists; choose a new one")
    prompt = (
        "You are verifying the PawnPilot MCP integration with a real Codex client. "
        "Use ONLY the pawnpilot MCP tools, never shell or other tools, never read files. "
        "Call health_check and observe. Report their actual returned state. "
        "Do not fabricate tools or outcomes. Do not modify files, settings, saves, or use GUI. "
    )
    if args.business:
        prompt += (
            "Inspect the business.details from that SAME snapshot: customer presence/arrival/phase, "
            "all three container completeness and reasons, visible item refs/units, quote rows/total/mode/status, "
            "UI blockers/coverage, capacity and missing eligibility. Distinguish confirmed empty from "
            "unavailable or incomplete. Unverified displayed text does not prove quote binding or legal purchase. "
            "Report the exact world/revision and missing evidence; never infer hidden names or prices. "
        )
    if args.close_menu:
        prompt += (
            "The user approved this isolated live test. If observe reports connected, ready, "
            "can_act and legal close_menu, submit exactly ONE act(close_menu) with a new unique "
            "command_id, schema_version equal to the observed state.schema_version, arguments={} and the exact observed "
            "broker_epoch/world_epoch/revision/version. "
            "Then call wait_for_update with criterion=command_terminal for that ID, "
            "max_wait_ms=10000; call get_action_status for that same ID once. "
            "Never submit act again, even on timeout, unknown, stale or error. Then observe once. "
            "If prerequisites fail, stop and report. Include command ID and before/after evidence."
        )
    else:
        prompt += (
            "Do not call act. Also query get_action_status for ID integration_nonexistent_readonly. "
            "Call wait_for_update with criterion=operable and max_wait_ms=500 to exercise bounded waiting."
        )
    config = {
        "mcp_servers.pawnpilot.command": str(python),
        "mcp_servers.pawnpilot.args": ["-m", "pawnpilot_mcp"],
        "mcp_servers.pawnpilot.cwd": str(ROOT),
        "mcp_servers.pawnpilot.required": True,
        "mcp_servers.pawnpilot.startup_timeout_sec": 20,
        "mcp_servers.pawnpilot.tool_timeout_sec": 40,
        "mcp_servers.pawnpilot.default_tools_approval_mode": "approve",
    }
    command = [codex, "exec", "--ignore-user-config", "--ephemeral", "--json",
               "--sandbox", "read-only", "--model", args.model, "--cd", str(ROOT)]
    for key, value in config.items():
        command += ["-c", key + "=" + json.dumps(value)]
    command += [prompt]
    with output.open("x", encoding="utf-8") as stdout, output.with_suffix(".stderr.log").open("x", encoding="utf-8") as stderr:
        result = subprocess.run(command, cwd=ROOT, stdout=stdout, stderr=stderr, timeout=240)
    print(json.dumps({"exit_code": result.returncode, "evidence": str(output)}))
    raise SystemExit(result.returncode)


if __name__ == "__main__":
    main()
