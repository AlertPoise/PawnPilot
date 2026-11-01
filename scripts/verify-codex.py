"""Run an actual Codex CLI MCP client; retain its JSONL evidence locally.

Default is read-only. --business-stage A/B/C/FLOW asks Codex to use only currently legal PawnPilot
MCP actions for a bounded live test. Stages C/FLOW require an explicit caller spending ceiling.
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
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--close-menu", action="store_true")
    modes.add_argument("--dismiss-customer", action="store_true", help="Dismiss exactly one currently legal ordinary customer through MCP")
    modes.add_argument("--business-stage", choices=("A", "B", "C", "FLOW"), help="Run a bounded real MCP business stage")
    parser.add_argument("--business", action="store_true", help="Read v3 business completeness and quote evidence; never purchases")
    parser.add_argument("--max-spend", type=int, help="Explicit maximum total for stage C; no purchase is submitted above it")
    parser.add_argument("--model", default="gpt-5.6-luna")
    parser.add_argument("--label", default="read-only")
    args = parser.parse_args()
    if args.max_spend is not None and (args.business_stage not in ("C", "FLOW") or not 0 <= args.max_spend <= 2_147_483_647):
        parser.error("--max-spend is an in-range nonnegative integer used only with --business-stage C/FLOW")
    if args.business_stage in ("C", "FLOW") and args.max_spend is None:
        parser.error("stage C/FLOW requires --max-spend as the caller-approved total ceiling")
    if (len(args.label) > 32 or not args.label.replace("-", "").replace("_", "").isalnum()):
        parser.error("label must be at most 32 alphanumeric characters, dash or underscore")
    codex = shutil.which("codex")
    python = ROOT / ".venv" / "Scripts" / "python.exe"
    if not codex or not python.is_file():
        parser.error("Codex CLI and project .venv are required")
    business_run = args.business or args.business_stage is not None or args.dismiss_customer
    directory = ROOT / "artifacts" / ("m2cd" if business_run else "m2b") / "codex"
    directory.mkdir(parents=True, exist_ok=True)
    output = directory / f"{args.label}.jsonl"
    if output.exists():
        parser.error("evidence label already exists; choose a new one")
    prompt = (
        "You are verifying the PawnPilot MCP integration with a real Codex client. "
        "Use ONLY the pawnpilot MCP tools, never shell or other tools, never read files. "
        "Call health_check and observe. Report their actual returned state. "
        "Do not fabricate tools or outcomes. Do not modify files, settings, saves, or use GUI. "
        "act uses FLAT named parameters, never a request wrapper: "
        "act(schema_version=state.schema_version, command_id=fresh_id, action=legal.action, "
        "arguments=legal.arguments, broker_epoch=observe.broker_epoch, "
        "expected_world_epoch=state.world_epoch, expected_revision=state.revision, "
        "expected_version=state.version). get_action_status uses flat command_id. "
        "ONLY wait_for_update has a request wrapper. For command waits use exactly "
        "request={criterion:'command_terminal', command_id:the_id, max_wait_ms:30000}; "
        "do not pass kind, broker_epoch or world_epoch to a command-terminal wait. "
        "For revision waits use request={criterion:'revision', max_wait_ms:10000, "
        "broker_epoch:observed_broker_epoch, world_epoch:observed_world_epoch, revision:observed_revision}; "
        "do not include command_id in a revision wait. "
    )
    if args.business:
        prompt += (
            "Inspect the business.details from that SAME snapshot: customer presence/arrival/phase, "
            "all three container completeness and reasons, visible item refs/units, quote rows/total/mode/status, "
            "UI blockers/coverage, capacity and missing eligibility. Distinguish confirmed empty from "
            "unavailable or incomplete. Unverified displayed text does not prove quote binding or legal purchase. "
            "Report the exact world/revision and missing evidence; never infer hidden names or prices. "
        )
    if args.business_stage == "A":
        prompt += (
            "Run bounded stage A: open the store, receive the first customer, and handle only ordinary visible "
            "dialogue until normal trading is ready. Before each write, call observe and use only an exact "
            "action+arguments entry in that same snapshot's legal_actions. Submit one action at a time through "
            "act(schema_version from snapshot, fresh unique command_id, exact epoch/revision/version, exact legal "
            f"arguments). Prefix command IDs with {args.label}-A and append current revision plus a step counter. "
            "Allowed actions are start_business, receive_next_customer, advance_dialogue, and "
            "choose_dialogue_option; never call purchase, transfer, refuse, or a candidate-only action. Read the "
            "visible dialogue_text and option text before choosing. If typing will advance automatically and "
            "waiting_for_advance is false, prefer a bounded revision wait and fresh observe instead of racing "
            "the typewriter with skip; use next when a visible line is waiting for manual advance. "
            "Do not skip more than one dialogue step per "
            "act; observe again after each result. After every act, call wait_for_update(command_terminal, same ID, "
            "max_wait_ms=30000), then get_action_status for that same ID. If timed out, rejected, failed or "
            "outcome_unknown, query that ID and stop; never replay it or create a replacement ID for the same operation. Stop on special "
            "trade mechanisms that require an unsupported action, unavailable/unsupported states, or missing legal actions. "
            "The ordinary SELL panel may also advertise wholesale discounts; do not choose that branch, but its "
            "presence or a spoken wholesale offer does not prevent normal-price trading or ordinary dialogue. Stop once the "
            "customer reaches ordinary trading and report the actual actions and before/after revisions."
        )
    elif args.business_stage == "B":
        prompt += (
            "Run bounded stage B only. First observe the current visible front/back sets and normal quote. If "
            "prepare_purchase_items appears in the exact legal_actions list, submit exactly one act using that "
            f"entry's full arguments and current snapshot identity; prefix its fresh ID with {args.label}-B. "
            "Never invent item refs or use a candidate. Wait "
            "for that ID, query its status, then observe again and report actual front/back changes, purchase-set "
            "refs/fingerprint and the normal total/mode/status. Do not purchase, transfer twice, or reuse stale "
            "refs. If rejected, failed, unknown or waiting times out, query the same ID and stop without replay."
        )
    elif args.business_stage == "FLOW":
        prompt += (
            f"Run the bounded ordinary-buy acceptance flow in this authorized isolated test slot. Total purchase "
            f"ceiling is {args.max_spend}; make at most ONE purchase and visit at most SIX customers in this run. "
            "Immediately before EVERY write call observe again; never submit an action from a prior "
            "wait/status result. Use exactly one current legal_actions entry with its exact "
            "arguments and observed schema/broker/world/revision/version. Use a fresh unique command ID "
            f"prefixed {args.label}. Allowed actions: start_business, receive_next_customer, advance_dialogue, "
            "choose_dialogue_option, prepare_purchase_items, purchase_visible_offer, dismiss_customer. "
            "Read visible dialogue/options; allow automatic typing to finish by bounded revision waits, "
            "use next for manual waits, and only choose ordinary necessary options. Never choose wholesale, "
            "bargaining or special deals. Mere wholesale advertising does not prevent normal trading. "
            "At each customer, prepare once if legal, then observe the actual normal quote and subset. "
            "Buy only if purchase_visible_offer is currently legal with complete normal quote, sufficient "
            "capacity, enabled eligibility and total <= ceiling and current cash. Copy its exact arguments. "
            "If the trade/item set is unsupported or not eligible, and dismiss_customer is legal, normally "
            "dismiss without purchase, wait for departure, and receive the next customer if legal. "
            "For EVERY act, query the same command with wait_for_update(command_terminal) and get_action_status. "
            "On rejection, failure, timeout or outcome_unknown stop, query that same ID, and NEVER replay or "
            "create a replacement ID. Do not call two writes concurrently. "
            "After the ONE succeeded purchase, compare exact cash delta and all purchased item transfers/ownership "
            "from returned evidence, then finish only that customer's ordinary dialogue or legal dismiss if "
            "needed. Wait for departure and observe continuation readiness; do not receive another customer "
            "or buy again after purchase. If no legal action exists, use at most three bounded revision waits "
            "of up to 10000 ms, then stop and report. Report actual command IDs, results, prices, cash and "
            "inventory changes, and whether the customer interaction ended. Do not invent success."
        )
    elif args.business_stage == "C":
        prompt += (
            f"Run bounded stage C with caller-approved total ceiling {args.max_spend}. Observe the current "
            "customer, normal quote, purchase subset, capacity, cash, and exact legal_actions. Decide whether the "
            "observed normal displayed_total is appropriate and is at most that ceiling. Prefix one fresh "
            f"command ID with {args.label}-C. Submit a purchase_visible_offer act only if its exact action+arguments pair is currently legal, the quote is "
            "complete normal, eligibility is enabled, capacity sufficient, total <= ceiling, and cash covers the "
            "amount. Use those fresh exact arguments; never hardcode item refs, prices, or amounts. If any check "
            "fails, do not buy and report why. After act, wait_for_update(command_terminal) and query the same "
            "command ID once; on rejected/failed/timeout/unknown stop and never replay. For a succeeded purchase, "
            "compare the actual cash and item ownership/container changes in before/after evidence, then observe. "
            "Do not make another purchase or any other economic action. If the same ordinary customer still has "
            "legal advance_dialogue or choose_dialogue_option, handle only those visible current dialogue steps "
            "(maximum four), each from exact fresh legal_actions, with a new ID prefixed "
            f"{args.label}-C-dialogue and the same wait/status/no-replay rule. If there is no legal dialogue action "
            "and the customer is arriving/leaving, wait_for_update on the observed revision for up to 10000 ms, "
            "then observe again (maximum three waits). Do not call receive_next_customer after the purchase. Stop "
            "when the customer is absent/leaving, a different customer appears, a special mechanism appears, or "
            "the bounded waits/actions end."
        )
    if args.dismiss_customer:
        prompt += (
            "The caller authorizes ending this isolated ordinary customer interaction without buying. "
            "Observe, then submit exactly one dismiss_customer act only if its exact action/arguments are "
            "currently in legal_actions. Use the observed schema, broker/world identity, revision/version and "
            f"a fresh ID prefixed {args.label}. Do not use another action as a substitute. "
            "Wait for that command and get its status, then observe the actual customer state. "
            "Never repeat the command, receive another customer, buy, or transfer items in this run. "
            "On rejection/failure/unknown stop and report."
        )
    elif args.close_menu:
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
    elif args.business_stage is None:
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
        result = subprocess.run(command, cwd=ROOT, stdout=stdout, stderr=stderr, timeout=900 if args.business_stage == "FLOW" else 240)
    print(json.dumps({"exit_code": result.returncode, "evidence": str(output)}))
    raise SystemExit(result.returncode)


if __name__ == "__main__":
    main()
