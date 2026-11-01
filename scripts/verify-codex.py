"""Run an actual Codex CLI MCP client; retain its JSONL evidence locally.

Default is read-only. --business-stage A/B/C/FLOW asks Codex to use only currently legal PawnPilot
MCP actions for a bounded live test. Stages C/FLOW and --m3b require an explicit caller spending ceiling.
This script neither installs the Mod nor starts the broker.
"""
import argparse
import json
import re
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
M4_DIR = ROOT / "artifacts" / "m4" / "codex"
M4_SESSION_SCHEMA = 1
M4_ACTIONS = {
    "start_business", "receive_next_customer", "open_trade_panel", "advance_dialogue",
    "choose_dialogue_option", "prepare_purchase_items", "purchase_visible_offer",
    "prepare_sale_items", "sell_visible_offer", "dismiss_customer", "open_bargain_panel",
    "choose_bargain_option", "close_bargain_panel", "sort_inventory_default",
    "close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report",
}


def _decode_payload(result):
    """Decode the common MCP structured-content and Codex text result shapes."""
    if not isinstance(result, dict):
        return {}
    structured = result.get("structured_content") or result.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    for part in result.get("content", []):
        if isinstance(part, dict) and isinstance(part.get("text"), str):
            try:
                value = json.loads(part["text"])
            except (json.JSONDecodeError, TypeError):
                continue
            if isinstance(value, dict):
                return value
    return {}


def _extract_observed_state(payload):
    if not isinstance(payload, dict):
        return None
    snapshot = payload.get("snapshot")
    state = snapshot.get("state") if isinstance(snapshot, dict) else None
    if isinstance(state, dict):
        return state
    if isinstance(payload.get("state"), dict):
        return payload["state"]
    return None


def analyze_m4_evidence(paths):
    """Rebuild a conservative, auditable session ledger from Codex JSONL tool events."""
    events = []
    for run_index, path in enumerate(paths):
        with Path(path).open("r", encoding="utf-8") as stream:
            for line_number, line in enumerate(stream, 1):
                if not line.strip():
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise ValueError(f"malformed M4 evidence {path}:{line_number}") from exc
                if isinstance(event, dict):
                    events.append((len(events), run_index, event))

    calls = {}
    act_order = []
    status_queries = []
    observations = []
    duplicate_command_ids = set()
    for sequence, run_index, event in events:
        item = event.get("item")
        if not isinstance(item, dict) or item.get("type") != "mcp_tool_call" or item.get("server") != "pawnpilot":
            continue
        tool = item.get("tool")
        tool_id = item.get("id")
        arguments = item.get("arguments") if isinstance(item.get("arguments"), dict) else {}
        # Codex item IDs restart at item_0 for each exec invocation, so include the evidence
        # file/run index while still linking statuses by the PawnPilot command_id.
        key = (run_index, tool_id if tool_id is not None else f"line-{sequence}", tool)
        if tool == "act":
            existing = calls.get(key)
            if existing is None:
                command_id = arguments.get("command_id")
                record = {
                    "sequence": sequence, "tool_id": tool_id, "command_id": command_id,
                    "run_index": run_index,
                    "action": arguments.get("action"),
                    "arguments": arguments.get("arguments") if isinstance(arguments.get("arguments"), dict) else {},
                    "payload": {},
                }
                calls[key] = record
                act_order.append(record)
                if command_id:
                    if any(previous.get("command_id") == command_id for previous in act_order[:-1]):
                        duplicate_command_ids.add(command_id)
            payload = _decode_payload(item.get("result"))
            if payload:
                calls[key]["payload"] = payload
        elif tool == "get_action_status":
            payload = _decode_payload(item.get("result"))
            status_queries.append({
                "sequence": sequence, "command_id": arguments.get("command_id"),
                "payload": payload, "run_index": run_index,
            })
        elif tool == "observe":
            payload = _decode_payload(item.get("result"))
            state = _extract_observed_state(payload)
            if state is not None:
                observations.append({"sequence": sequence, "run_index": run_index, "state": state})

    # Status is linked by the command ID from the act, not by generated Codex item IDs.
    for attempt in act_order:
        command_id = attempt.get("command_id")
        matches = [q for q in status_queries if q.get("command_id") == command_id and
                   q["sequence"] > attempt["sequence"]]
        payload = matches[-1]["payload"] if matches else attempt.get("payload", {})
        attempt["status"] = payload.get("command_status") or payload.get("status")
        attempt["status_query_sequence"] = matches[-1]["sequence"] if matches else None
        outcome = payload.get("result")
        if isinstance(outcome, dict):
            before = outcome.get("before")
            after = outcome.get("after")
            before_cash = before.get("cash") if isinstance(before, dict) else None
            after_cash = after.get("cash") if isinstance(after, dict) else None
            attempt["cash_delta"] = before_cash - after_cash if isinstance(before_cash, int) and isinstance(after_cash, int) else None
        else:
            attempt["cash_delta"] = None

    day_observations = []
    for record in observations:
        day = record["state"].get("business", {}).get("details", {}).get("day", {})
        if isinstance(day, dict) and isinstance(day.get("day_number"), int):
            day_observations.append({"sequence": record["sequence"], "day_number": day["day_number"],
                                     "stage": day.get("stage"), "state": record["state"]})
    latest_day = day_observations[-1] if day_observations else None
    first_act_sequence = min((attempt["sequence"] for attempt in act_order), default=None)
    baseline_valid = bool(observations and day_observations and
                          day_observations[0]["sequence"] == observations[0]["sequence"] and
                          (first_act_sequence is None or day_observations[0]["sequence"] < first_act_sequence))

    def action_count(action):
        return sum(1 for attempt in act_order if attempt.get("action") == action)

    purchase_reserved = 0
    night_fee_reserved = 0
    unknown_purchase_reservation = False
    unknown_fee_reservation = False
    actual_paid = 0
    actual_night_fee = 0
    for attempt in act_order:
        action = attempt.get("action")
        args = attempt.get("arguments") or {}
        if action == "purchase_visible_offer":
            amount = args.get("amount")
            if isinstance(amount, int) and not isinstance(amount, bool) and amount >= 0:
                purchase_reserved += max(amount, attempt.get("cash_delta") or 0)
            else:
                unknown_purchase_reservation = True
            delta = attempt.get("cash_delta")
            if isinstance(delta, int) and delta > 0:
                actual_paid += delta
        elif action == "confirm_end_of_day":
            amount = args.get("amount")
            if isinstance(amount, int) and not isinstance(amount, bool):
                night_fee_reserved += max(0, amount)
            else:
                unknown_fee_reservation = True
            delta = attempt.get("cash_delta")
            if isinstance(delta, int) and delta > 0:
                actual_night_fee += delta
            elif isinstance(delta, int) and delta < 0:
                actual_night_fee += delta

    latest_handoff = None
    resumed_refs = set()
    for record in observations:
        details = record["state"].get("business", {}).get("details", {})
        handoff = details.get("inspection_handoff") if isinstance(details, dict) else None
        if isinstance(handoff, dict):
            latest_handoff = {"sequence": record["sequence"], "handoff_ref": handoff.get("handoff_ref"),
                              "status": handoff.get("status")}
            if handoff.get("last_resumed_handoff_ref"):
                resumed_refs.add(handoff["last_resumed_handoff_ref"])
        elif record is observations[-1]:
            latest_handoff = None

    # Commands with no terminal status, or explicit unknown outcomes, must be looked up by their
    # original ID before the session can issue another write. Rejected IDs are retained as audit.
    unresolved = []
    rejected = []
    for attempt in act_order:
        status = attempt.get("status")
        if (not attempt.get("command_id") or attempt.get("status_query_sequence") is None and
                status in ("rejected", "failed", "error")) or status in (
                    None, "pending", "running", "outcome_unknown", "unknown", "timeout", "not_found", "failed", "error"):
            unresolved.append(attempt.get("command_id") or "<missing-command-id>")
        elif status == "rejected":
            rejected.append(attempt.get("command_id"))

    return {
        "act_attempts": len(act_order),
        "customer_attempts": action_count("receive_next_customer"),
        "trade_attempts": action_count("purchase_visible_offer") + action_count("sell_visible_offer"),
        "bargain_attempts": action_count("choose_bargain_option"),
        "purchase_reserved": purchase_reserved,
        "night_fee_reserved": night_fee_reserved,
        "actual_paid": actual_paid,
        "actual_night_fee": actual_night_fee,
        "unknown_purchase_reservation": unknown_purchase_reservation,
        "unknown_fee_reservation": unknown_fee_reservation,
        "unresolved_command_ids": [value for value in unresolved if value],
        "rejected_command_ids": [value for value in rejected if value],
        "duplicate_command_ids": sorted(duplicate_command_ids),
        "initial_day": day_observations[0]["day_number"] if day_observations else None,
        "baseline_valid": baseline_valid,
        "latest_day": latest_day,
        "latest_handoff": latest_handoff,
        "resumed_handoff_refs": sorted(resumed_refs),
        "attempts": act_order,
    }


def _atomic_write_json(path, data):
    path = Path(path)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temp.replace(path)


def load_or_create_m4_session(session_id, budgets, label, confirmed_handoff=None):
    """Validate the immutable session contract, register this evidence file, and audit history."""
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", session_id or ""):
        raise ValueError("--session-id must be 1..64 letters, digits, dash or underscore")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,32}", label or ""):
        raise ValueError("M4 label must be 1..32 letters, digits, dash or underscore")
    M4_DIR.mkdir(parents=True, exist_ok=True)
    manifest_path = M4_DIR / f"session-{session_id}.json"
    output = M4_DIR / f"{label}.jsonl"
    if output.exists() or output.with_suffix(".stderr.log").exists():
        raise ValueError("M4 evidence label already exists; choose a unique label")
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest.get("schema_version") != M4_SESSION_SCHEMA or manifest.get("session_id") != session_id:
            raise ValueError("M4 session manifest is invalid")
        if manifest.get("budgets") != budgets:
            raise ValueError("M4 budgets are fixed at session creation; resume with identical totals")
    else:
        manifest = {"schema_version": M4_SESSION_SCHEMA, "session_id": session_id,
                    "budgets": budgets, "initial_day": None, "runs": [], "audit": {}}
    if any(run.get("label") == label for run in manifest.get("runs", [])):
        raise ValueError("M4 label is already registered in this session")
    paths = []
    for run in manifest.get("runs", []):
        relative = run.get("evidence")
        path = (ROOT / relative).resolve() if isinstance(relative, str) else None
        if path is None or M4_DIR.resolve() not in path.parents or not path.is_file():
            raise ValueError("M4 session history evidence is missing or outside artifacts/m4/codex")
        paths.append(path)
    audit = analyze_m4_evidence(paths) if paths else analyze_m4_evidence([])
    if "<missing-command-id>" in audit["unresolved_command_ids"]:
        raise ValueError("M4 history contains an act attempt without a queryable command_id")
    if manifest.get("runs") and not audit["baseline_valid"]:
        raise ValueError("M4 session has no valid initial-day observation before its first write")
    if audit["duplicate_command_ids"]:
        raise ValueError("M4 history reuses command IDs; stop for manual audit")
    if audit["unknown_purchase_reservation"] or audit["unknown_fee_reservation"]:
        raise ValueError("M4 history has an unbounded economic amount; stop for manual audit")
    initial_day = manifest.get("initial_day")
    if initial_day is not None and audit["initial_day"] is not None and initial_day != audit["initial_day"]:
        raise ValueError("M4 initial day conflicts with session history")
    if initial_day is None:
        initial_day = audit["initial_day"]
        manifest["initial_day"] = initial_day
    limits = budgets
    if audit["act_attempts"] >= limits["max_writes"] and not audit["unresolved_command_ids"]:
        raise ValueError("M4 cumulative write-attempt budget is exhausted")
    if audit["customer_attempts"] >= limits["max_customers"] and limits["max_customers"] >= 0:
        remaining_customers = 0
    else:
        remaining_customers = limits["max_customers"] - audit["customer_attempts"]
    if (audit["trade_attempts"] > limits["max_trades"] or audit["bargain_attempts"] > limits["max_bargains"]) and not audit["unresolved_command_ids"]:
        raise ValueError("M4 cumulative trade or bargain budget is exceeded")
    remaining = {
        "max_writes": max(0, limits["max_writes"] - audit["act_attempts"]),
        "max_customers": max(0, remaining_customers),
        "max_spend": max(0, limits["max_spend"] - audit["purchase_reserved"]),
        "max_trades": max(0, limits["max_trades"] - audit["trade_attempts"]),
        "max_bargains": max(0, limits["max_bargains"] - audit["bargain_attempts"]),
        "max_night_fee": max(0, limits["max_night_fee"] - audit["night_fee_reserved"]),
    }
    if (audit["purchase_reserved"] > limits["max_spend"] or audit["night_fee_reserved"] > limits["max_night_fee"]) and not audit["unresolved_command_ids"]:
        raise ValueError("M4 cumulative economic budget is exceeded")
    if (audit["latest_day"] and initial_day is not None and audit["latest_day"]["day_number"] > initial_day + 1 and
            not audit["unresolved_command_ids"]):
        raise ValueError("M4 observed a day beyond the single-day boundary")
    latest_waiting = (audit["latest_handoff"] or {}).get("handoff_ref") if (audit["latest_handoff"] or {}).get("status") == "waiting_for_user" else None
    handoff_ref = latest_waiting
    if confirmed_handoff and (not handoff_ref or confirmed_handoff != handoff_ref or
                              confirmed_handoff in audit["resumed_handoff_refs"]):
        raise ValueError("M4 --confirmed-handoff must exactly match the latest waiting, not-yet-resumed handoff in this session history")
    manifest["runs"].append({"label": label, "evidence": str(output.relative_to(ROOT).as_posix()), "status": "running"})
    manifest["audit"] = {key: value for key, value in audit.items() if key != "attempts"}
    manifest["initial_day"] = initial_day
    _atomic_write_json(manifest_path, manifest)
    return manifest_path, output, manifest, audit, remaining, handoff_ref


def finish_m4_session(manifest_path, manifest, return_code):
    paths = []
    for run in manifest.get("runs", []):
        relative = run.get("evidence")
        path = (ROOT / relative).resolve() if isinstance(relative, str) else None
        if path is not None and M4_DIR.resolve() in path.parents and path.is_file():
            paths.append(path)
    try:
        audit = analyze_m4_evidence(paths) if paths else analyze_m4_evidence([])
    except ValueError as exc:
        manifest["audit_error"] = str(exc)
        audit = None
    if audit is not None:
        if manifest.get("initial_day") is None and audit["initial_day"] is not None:
            manifest["initial_day"] = audit["initial_day"]
        manifest["audit"] = {key: value for key, value in audit.items() if key != "attempts"}
    if manifest.get("runs"):
        manifest["runs"][-1]["status"] = "completed" if return_code == 0 else "exit_" + str(return_code)
    _atomic_write_json(manifest_path, manifest)
    return audit


def build_m4_prompt(args, audit, remaining, handoff_ref):
    prefix = args.label
    unresolved = audit["unresolved_command_ids"]
    unresolved_text = ", ".join(unresolved) if unresolved else "none"
    previous_rejected = ", ".join(audit["rejected_command_ids"]) if audit["rejected_command_ids"] else "none"
    initial_day = audit["initial_day"]
    latest = audit["latest_day"]
    latest_day = latest["day_number"] if latest else "not yet observed"
    next_morning_only = initial_day is not None and latest is not None and latest_day >= initial_day + 1
    prompt = (
        "You are running one bounded PawnPilot M4 continuous single-day ordinary-business session as a real Codex CLI MCP client. "
        "Use ONLY PawnPilot MCP tools; never shell, filesystem, GUI, save editing, installation or other tools. "
        "Do not operate the game outside exact current legal_actions. This is autonomous ordinary business, "
        "not a strategy engine: do not invent or add business actions. "
        f"Evidence label {prefix}; session id {args.session_id}; initial day {initial_day if initial_day is not None else 'must be established by the first observation'}; "
        f"latest observed day {latest_day}. Remaining cumulative ceilings: {remaining['max_writes']} act attempts, "
        f"{remaining['max_customers']} receive attempts, gross purchase budget {remaining['max_spend']}, "
        f"{remaining['max_trades']} trade attempts, {remaining['max_bargains']} bargain-choice attempts, "
        f"night-fee budget {remaining['max_night_fee']}. Past purchase reservation {audit['purchase_reserved']}, "
        f"past confirmed paid {audit['actual_paid']}; past night-fee reservation {audit['night_fee_reserved']}, "
        f"past confirmed night fees {audit['actual_night_fee']}. "
        "Call health_check and observe first; establish initial_day from business.details.day.day_number before any write. "
        "If day details are missing, stop without writes. For every write observe fresh and use one exact current legal_actions "
        "entry and exact observed schema/broker/world/revision/version. Use a fresh unique command_id prefixed with the evidence label. "
        "After EVERY act, query get_action_status with the original ID; if nonterminal wait only for that same ID, query it again, "
        "then observe fresh. A rejected, stale, failed, timeout or unknown write consumes budgets; query the SAME original ID and "
        "stop all further writes in this run. Never auto-refresh into a new ID/retry of that operation. "
        f"Historical unresolved command IDs that must be queried first by their ORIGINAL ID: {unresolved_text}. "
        f"Historical rejected command IDs (never replay): {previous_rejected}. Resolve required old IDs before any new action; "
        "if any remains unknown/pending, stop. Use at most three 10-second revision waits when waiting for visible automatic progress; "
        "stop after three unchanged waits. Never wait instead of taking a currently legal bounded next step. "
        "Use only ordinary currently legal actions and capabilities already exposed: start/receive, ordinary visible dialogue, "
        "trade panel, prepare and normal purchase/sale, normal dismiss, ordinary bargain flow, and fixed default inventory sort. "
        "Make customer and economic decisions autonomously from visible cash, the current exact normal quote, current inventory and "
        "known item costs/profit information, verified capacity, visible risk terms, and remaining trade/spend/night-fee budgets. "
        "Do not infer hidden costs, profit, acceptance odds, capacity, customer intent, or risk; say unknown when not exposed. "
        "Do not hardcode a maximum bargain discount; compare each currently visible option and risk against the actual quote and cash. "
        "If declining a customer/trade is better, use only currently legal ordinary visible refusal/dismiss/dialogue actions. "
        "Leave day settlement services at their default selection; do not toggle or reconfigure services. "
        "Respect remaining customer/trade/bargain/spend/night-fee ceilings before every submission. Never reuse a command ID, "
        "offer ref, customer ref, or old legal arguments. No wholesale, special deals, appraisal, blackmail, threats, or other unsupported paths. "
        "Use the observed day object as the phase controller: PREPARE -> OPENING -> SERVING -> BETWEEN_CUSTOMERS -> CLOSING -> "
        "SETTLEMENT -> NEXT_MORNING -> DONE; report actual stage/reason and do not infer a transition. "
        "Read day_number, remaining_customers, stage, can_close, can_sleep, can_confirm, report_open, transition_pending, reason, "
        "and settlement.settlement_ref/total/services when present; treat absent/unverified fields as unknown. "
        "At the customer limit, finish/leave the current customer normally but do not receive another. "
        "When day.can_close/can_sleep/can_confirm and legal actions permit it, use only the current exact day action: "
        "close_business {}, open_end_of_day {}, confirm_end_of_day {settlement_ref,amount,max_spend}, close_day_report {}. "
        "For confirm_end_of_day copy all exact current legal arguments including settlement_ref, amount, max_spend; ensure the fee amount "
        "is within remaining max_night_fee and the day's displayed settlement; do not override any argument. After each settlement action observe and re-evaluate. "
        "After observing day_number == initial_day + 1, NEVER start_business/open the store or receive another customer. "
        "Report the observed next morning and stop, except close_day_report may be submitted once if currently legal to close the report. "
        "At completion report initial/current day, actual stage, customer/trade/bargain/write counts, command IDs/statuses, "
        "cash deltas, inventory/service settlement evidence, reserved budget totals, and blockers. Do not fabricate outcomes. "
    )
    if handoff_ref:
        prompt += (
            f"Current session evidence shows a waiting manual inspection handoff {handoff_ref}. The caller supplied --confirmed-handoff "
            f"with this exact reference, representing explicit current-conversation user confirmation. Observe now and verify the same ref is "
            "still the waiting current handoff. Resume only by the currently legal matching resume_inspection_handoff with the exact ref, once. "
            "Then observe again and rebuild current customer/day/action state from that new observation. This confirmation is one-use and "
            "cannot authorize any later handoff; if another appears, stop and request a new user confirmation. "
        )
    elif args.confirmed_handoff:
        prompt += "The supplied handoff confirmation did not match a currently evidenced waiting handoff; stop without any write. "
    else:
        prompt += (
            "If business.details.inspection_handoff.requires_manual_inspection becomes true, stop all business writes immediately. "
            "Report exact handoff_ref and visible item names, and tell the user in Chinese: 我已暂停自动经营操作。请在游戏中手动检查，"
            "并自行决定是否质疑、购买或拒绝。处理结束后，请在本会话回复“检查完成，继续”。 Never resume by elapsed time, "
            "reconnection, customer departure, or simulated confirmation. A later run can resume only when its caller supplies "
            "--confirmed-handoff with the exact currently waiting reference from this same M4 session. "
        )
    if next_morning_only:
        prompt += (
            "The session has already observed day+1. This invocation is report-only: do not start/open, receive, trade, bargain, sort, "
            "or perform any other business write. Observe and report; close_day_report is the sole possible write, only once if "
            "currently legal, then observe and stop. "
        )
    if unresolved:
        prompt += (
            "RECOVERY-ONLY INVOCATION: because this session has an earlier act with no verified terminal status, "
            "use only health_check, observe, and get_action_status for each listed ORIGINAL command ID. "
            "Do not submit any act, even after a status becomes terminal; report the recovered state and stop. "
            "A later separately started M4 invocation may continue from this audit, but never replay the old command. "
        )
    return prompt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--close-menu", action="store_true")
    modes.add_argument("--dismiss-customer", action="store_true", help="Dismiss exactly one currently legal ordinary customer through MCP")
    modes.add_argument("--business-stage", choices=("A", "B", "C", "FLOW"), help="Run a bounded real MCP business stage")
    modes.add_argument("--m3a", choices=("FLOW", "SALE", "SORT"), help="Authorized isolated-slot M3-A acceptance")
    modes.add_argument("--m3b", action="store_true", help="Authorized bounded ordinary bargain and settlement acceptance")
    modes.add_argument("--m3c", choices=("HANDOFF", "RESUME"), help="Separate authorized handoff and user-confirmed resume phases")
    modes.add_argument("--m4", action="store_true", help="Authorized bounded continuous single-day ordinary-business session")
    parser.add_argument("--confirmed-handoff", help="Exact handoff_ref, supplied ONLY after the user says 检查完成，继续")
    modes.add_argument("--trade-panel", action="store_true", help="Authorized ordinary panel-open acceptance; never buys or sells")
    parser.add_argument("--business", action="store_true", help="Read v3 business completeness and quote evidence; never purchases")
    parser.add_argument("--max-spend", type=int, help="Explicit purchase ceiling for stage C/FLOW or M3-B")
    parser.add_argument("--model", default="gpt-5.6-luna")
    parser.add_argument("--max-customers", type=int, help="Customer budget (M4 cumulative across --session-id)")
    parser.add_argument("--max-writes", type=int, help="Write-attempt budget (M4 cumulative across --session-id)")
    parser.add_argument("--max-trades", type=int, help="M4 cumulative purchase/sale attempt ceiling")
    parser.add_argument("--max-bargains", type=int, help="M4 cumulative bargain-choice attempt ceiling")
    parser.add_argument("--max-night-fee", type=int, help="M4 cumulative day-settlement fee ceiling")
    parser.add_argument("--session-id", help="Bounded persistent M4 session id used to audit cumulative budgets")
    parser.add_argument("--skip-sort", action="store_true", help="M3-A resumed flow: sorting already completed in this slot")
    parser.add_argument("--label", default="read-only")
    args = parser.parse_args()
    if args.m4:
        if any(value is None for value in (args.session_id, args.max_customers, args.max_writes,
                args.max_spend, args.max_trades, args.max_bargains, args.max_night_fee)):
            parser.error("M4 requires --session-id and explicit --max-customers/--max-writes/--max-spend/--max-trades/--max-bargains/--max-night-fee")
        if not 0 <= args.max_customers <= 32 or not 1 <= args.max_writes <= 256 or \
                not 0 <= args.max_spend <= 2_147_483_647 or not 0 <= args.max_trades <= 32 or \
                not 0 <= args.max_bargains <= 32 or not 0 <= args.max_night_fee <= 2_147_483_647:
            parser.error("M4 budgets must be customers 0..32, writes 0..256, trades/bargains 0..32, and monetary ceilings in-range nonnegative integers")
        if args.max_bargains > args.max_writes:
            parser.error("M4 max-bargains cannot exceed max-writes")
    else:
        if args.max_customers is None:
            args.max_customers = 6
        if args.max_writes is None:
            args.max_writes = 24
        if not 0 <= args.max_customers <= 6 or not 1 <= args.max_writes <= 24:
            parser.error("M3-A/M3-B budgets must be 0..6 customers and 1..24 writes")
    if args.max_spend is not None and ((args.business_stage not in ("C", "FLOW") and not args.m3b and args.m3c != "RESUME" and not args.m4) or
                                      not 0 <= args.max_spend <= 2_147_483_647):
        parser.error("--max-spend is an in-range nonnegative integer used only with business-stage C/FLOW, M3-B/M3-C RESUME, or M4")
    if args.business_stage in ("C", "FLOW") and args.max_spend is None:
        parser.error("stage C/FLOW requires --max-spend as the caller-approved total ceiling")
    if args.m3b and (args.max_spend is None or args.max_spend <= 0):
        parser.error("--m3b requires a positive --max-spend as the caller-approved purchase ceiling")
    if args.m3c == "RESUME" and (not args.confirmed_handoff or args.max_spend is None):
        parser.error("M3-C RESUME requires --confirmed-handoff from explicit user confirmation and --max-spend")
    if args.confirmed_handoff is not None and ((args.m3c != "RESUME" and not args.m4) or
            not 1 <= len(args.confirmed_handoff) <= 256 or
            any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_:" for c in args.confirmed_handoff)):
        parser.error("--confirmed-handoff must be a bounded exact reference used only in M3-C RESUME or M4")
    if (len(args.label) > 32 or not args.label.replace("-", "").replace("_", "").isalnum()):
        parser.error("label must be at most 32 alphanumeric characters, dash or underscore")
    codex = shutil.which("codex")
    python = ROOT / ".venv" / "Scripts" / "python.exe"
    if not codex or not python.is_file():
        parser.error("Codex CLI and project .venv are required")
    business_run = args.business or args.business_stage is not None or args.dismiss_customer or args.m4
    directory = ROOT / "artifacts" / ("m4" if args.m4 else "m3c" if args.m3c else "m3b" if args.m3b else "m3a-panel" if args.trade_panel else "m3a" if args.m3a else "m2cd" if business_run else "m2b") / "codex"
    directory.mkdir(parents=True, exist_ok=True)
    output = directory / f"{args.label}.jsonl"
    m4_context = None
    if args.m4:
        budgets = {name: getattr(args, name) for name in (
            "max_customers", "max_writes", "max_spend", "max_trades", "max_bargains", "max_night_fee")}
        try:
            m4_context = load_or_create_m4_session(args.session_id, budgets, args.label, args.confirmed_handoff)
        except (ValueError, OSError, json.JSONDecodeError) as exc:
            parser.error(str(exc))
        manifest_path, output, manifest, audit, remaining, current_handoff = m4_context
    elif output.exists():
        parser.error("evidence label already exists; choose a new one")
    prompt = (
        "You are verifying the PawnPilot MCP integration with a real Codex client. "
        "Use ONLY the pawnpilot MCP tools, never shell or other tools, never read files. "
        "Call health_check and observe. Report their actual returned state. "
        "Do not fabricate tools or outcomes. Do not modify files, settings, saves, or use GUI. "
        "act uses FLAT named parameters, never a request wrapper: "
        "act(schema_version=observe.action_schema_version, command_id=fresh_id, action=legal.action, "
        "arguments=legal.arguments, broker_epoch=observe.snapshot.broker_epoch, "
        "expected_world_epoch=state.world_epoch, expected_revision=state.revision, "
        "expected_version=state.version). get_action_status uses flat command_id. "
        "Here state means observe.snapshot.state. Copy the explicit action_schema_version, which equals "
        "state.schema_version; null means no connected state, so do not act. Never copy from the "
        "outer tool-reply schema_version or business.details.schema_version (both may be 2 while the "
        "actual state wire version is 3). "
        "ONLY wait_for_update has a request wrapper. For command waits use exactly "
        "request={criterion:'command_terminal', command_id:the_id, max_wait_ms:30000}; "
        "do not pass kind, broker_epoch or world_epoch to a command-terminal wait. "
        "For revision waits use request={criterion:'revision', max_wait_ms:10000, "
        "broker_epoch:observed_broker_epoch, world_epoch:observed_world_epoch, revision:observed_revision}; "
        "do not include command_id in a revision wait. "
        "If business.details.inspection_handoff.requires_manual_inspection is true, STOP business writes. "
        "Report handoff_ref and visible item names, and tell the user in Chinese: 我已暂停自动经营操作。"
        "请在游戏中手动检查，并自行决定是否质疑、购买或拒绝。处理结束后，请在本会话回复“检查完成，继续”。 "
        "Never resume based on time, reconnection, customer departure or a simulated confirmation. "
        "Only a separately caller-confirmed M3-C RESUME or M4 session phase permits resuming its exact reference. "
    )
    if args.business:
        prompt += (
            "Inspect the business.details from that SAME snapshot: customer presence/arrival/phase, "
            "all three container completeness and reasons, visible item refs/units, quote rows/total/mode/status, "
            "UI blockers/coverage, capacity and missing eligibility. Distinguish confirmed empty from "
            "unavailable or incomplete. Unverified displayed text does not prove quote binding or legal purchase. "
            "Report the exact world/revision and missing evidence; never infer hidden names or prices. "
        )
    if args.m4:
        prompt += build_m4_prompt(args, audit, remaining,
                                  current_handoff if args.confirmed_handoff else None)
    if args.m3c:
        prompt += (
            f"Authorized isolated-slot M3-C test. Prefix IDs {args.label}; maximum {args.max_writes} act attempts "
            f"and {args.max_customers} received ordinary customers. Use at most 12 revision waits of 10000ms, "
            "stop after three unchanged waits. Before every write observe fresh and copy exact legal arguments "
            "and versions. After each act query its original ID, wait command_terminal if necessary, query that "
            "same ID again and observe. On failure/unknown/timeout stop, never replay. Definite stale rejection "
            "permits at most one fresh non-economic decision. No GUI, appraisal, bargaining, sale, sort, special "
            "deal, day crossing or save changes. Wait for arrival/stock animation before preparation. "
        )
        if args.m3c == "HANDOFF":
            prompt += (
                "This is ONLY the handoff phase. Allowed writes before handoff: start_business, receive_next_customer, "
                "open_trade_panel, necessary ordinary advance_dialogue/choose_dialogue_option, "
                "prepare_purchase_items and normal dismiss_customer for unsupported customers. Never purchase "
                "or resume. Seek one normal NPC SELL offer, using current legal preparation if necessary. "
                "As soon as any waiting inspection_handoff is observed, report its complete visible state and "
                "that legal_actions contains no purchase/bargain/dismiss/receive. Output the user reminder and "
                "STOP this CLI run; the parent conversation will wait for the actual human confirmation. "
                "Do not submit deliberately illegal actions; gate rejection is tested separately. "
            )
        else:
            prompt += (
                f"The user has explicitly confirmed 检查完成，继续 for ONLY handoff_ref={args.confirmed_handoff}. "
                "This confirmation is not reusable for another handoff. Observe, verify that exact waiting ref, "
                "then submit its current legal resume_inspection_handoff once. Verify its terminal result and "
                "last_resumed_handoff_ref, then OBSERVE AGAIN before any business decision. If another waiting "
                "handoff appears, immediately remind the user and STOP without resuming it. If the confirmed ref "
                "is no longer the current waiting one, stop and report; do not guess or substitute. "
                "If the user already bought, dismissed, or the customer is gone, report the new state and stop "
                "without another purchase or receiving another customer. Otherwise only handle that customer's "
                "normal dialogue, open_trade_panel and prepare_purchase_items as currently legal. "
                f"At most ONE purchase_visible_offer submission, amount <= {args.max_spend} and current cash, "
                "complete normal quote, legal exact current items/offer/fingerprint, enabled eligibility and "
                "sufficient capacity. Never override a capacity restriction. Any purchase submission consumes "
                "the allowance including rejection or unknown. Retain before cash/items/units/ownership, verify "
                "the actual cash decrease equals the current quoted amount and purchased items became owned. "
                "Then only finish that same customer's necessary ordinary dialogue or legal dismissal, observe "
                "and stop. Report handoff resume separately from purchase outcome and list any blocker honestly. "
            )
    elif args.trade_panel:
        prompt += (
            "Verify autonomous opening of an ordinary customer's trade panel in the authorized test slot. "
            f"Use command IDs prefixed {args.label}; at most {args.max_writes} writes and {args.max_customers} received customers. "
            "Allowed writes ONLY start_business, receive_next_customer, open_trade_panel, advance_dialogue, "
            "choose_dialogue_option, dismiss_customer. NO purchase, sale, item transfer, sorting, closing day or GUI. "
            "Before EACH write observe fresh, copy EXACT current legal action arguments and world/revision/version. "
            "After EACH act query get_action_status using its original ID, wait command_terminal if needed and query "
            "the SAME ID again. Unknown/error/timeout: stop, never replay. Definite stale rejection permits fresh "
            "observation and at most one new decision. Wait for automatic dialogue; advance next only when manual "
            "waiting is actually visible; choose only necessary visible ordinary options. Never invent choices. "
            "Use at most 12 revision waits, at most 10000ms each, stop after three unchanged waits. "
            "When absent, start then receive within budget. If an ordinary panel is CLOSED and open_trade_panel is "
            "legal, execute it via MCP. Verify same customer, trade_panel equals trade_type (NPC direction), phase "
            "trading, normal displayed_total readable, and unchanged cash. Empty-counter normal total 0 is valid "
            "visible information, NOT a purchasable offer. Verify open_trade_panel is no longer legal, do NOT toggle "
            "or submit an illegal repeat. Report this successful opening and STOP without dismissing that customer. "
            "If a panel already opened automatically, report it as already open, NOT MCP-open success; normally "
            "dismiss only if currently legal and receive another within budget. Report command IDs and exact evidence. "
        )
    elif args.m3b:
        prompt += (
            "Perform the caller-authorized bounded M3-B ordinary bargaining acceptance through MCP. "
            f"Prefix fresh command IDs with {args.label}. Maximum {args.max_writes} write attempts, "
            f"{args.max_customers} received customers, and 12 revision waits of at most 10000 ms each; "
            "stop after three unchanged waits. Count every act against the write budget. "
            f"The explicit total purchase ceiling is {args.max_spend}; never spend above it or current cash. "
            "At most ONE actual bargain attempt and ONE economic trade (purchase OR sale) in this run. "
            "Allowed writes ONLY start_business, receive_next_customer, open_trade_panel, advance_dialogue, "
            "choose_dialogue_option, prepare_purchase_items, purchase_visible_offer, prepare_sale_items, "
            "sell_visible_offer, dismiss_customer, open_bargain_panel, choose_bargain_option, close_bargain_panel. "
            "No sorting, day crossing, wholesale, appraisal, blackmail, threats, special deals, save operations or GUI. "
            "Before EVERY write observe immediately; copy the exact current legal_actions entry and observed "
            "schema/broker/world/revision/version. Never invent refs, modes, percentages or amounts; never use "
            "a candidate or action from old wait/status evidence. Submit one write at a time. After EVERY act, "
            "query get_action_status with its original ID, wait command_terminal if needed, query the SAME ID "
            "again, then observe the actual post-result state. On failed, timeout, unknown, connection loss or "
            "ambiguous submission, stop writes and query that original ID; never replay. A definite stale "
            "rejection for a non-bargain action permits at most one fresh decision from a new observation. "
            "Any submission of choose_bargain_option consumes the run's ONE bargain-attempt allowance, "
            "including rejected, stale, failed or unknown results. Never submit a second bargain choice. "
            "A succeeded command means the native choice attempt was handled, NOT that the customer accepted. "
            "Similarly, any submission of purchase_visible_offer or sell_visible_offer consumes the ONE trade "
            "allowance, even if rejected or unknown; never replay or switch to the other trade direction. "
            "Read current dialogue text and actual visible option labels before deciding. Wait for automatic "
            "typing; advance next only at visible manual waits. Choose only necessary ordinary dialogue options. "
            "When absent, start/receive only if legal and within the customer budget; dismiss unsupported "
            "customers normally if legal. Do not wait repeatedly for a revision needing your next receive action. "
            "Identify direction from the NPC trade_type: sell means NPC sells to us, buy means NPC buys from us. "
            "Open the normal trade panel via legal open_trade_panel if closed. "
            "Before preparing items, wait for arrival/stock animation to settle and observe a trading customer "
            "with trade_panel equal to its buy/sell direction. The early newspaper-only front collection can "
            "change while the customer is still arriving. A definite stale non-bargain preparation rejection "
            "does not count as a completed transfer; you may make the one permitted fresh decision. "
            "Prepare an ordinary current offer once if needed: use prepare_purchase_items for an NPC SELL; "
            "for an NPC BUY select ONE appropriate visible player item from current prepare_sale_items entries "
            "using request text, visible names and units. Explain that choice. Read the current ordinary normal "
            "quote, item set, cash, eligibility and capacity. If the current container is incomplete and no "
            "preparation is legal after arrival settles, normally dismiss that unsupported selection when legal "
            "and continue within the remaining customer budget. Do not open bargaining for an empty offer: "
            "first obtain a complete normal quote with a nonempty item set. When open_bargain_panel is legal, open it and inspect "
            "its actual visible option labels, "
            "prices, percentages and available risk/reputation information. Explain visible prices and cash, "
            "and state risk unknown if it is not shown. Decide autonomously whether bargaining is worthwhile "
            "and which currently legal ordinary option to choose; do not hardcode the highest percentage or "
            "insist on attempting a bargain. Use only a choose_bargain_option entry bound to this SAME current "
            "customer_ref, NPC buy/sell mode and option_ref; copy its complete arguments exactly. Never use "
            "payment mode normal as the NPC direction of a bargain choice. Do not infer acceptance odds from "
            "hidden budget, random state, unexposed features or backend data. "
            "After the single choice attempt, read its original command result and visible customer dialogue. "
            "If the response needs manual advance while the bargain panel is open, first use the currently "
            "legal close_bargain_panel, then advance the visible ordinary response; do not wait forever for "
            "an advance action blocked by the open bargain panel. Read the resulting dialogue "
            "to distinguish accepted, rejected or leaving; if evidence is inconclusive report unknown. Handle "
            "ordinary visible dialogue/departure normally. Never loop for random acceptance, receive another "
            "customer to try again, or demand that both NPC directions occur in one run. Rejected bargaining "
            "may leave an ordinary trade available; decide from the fresh visible terms. If the customer leaves, "
            "observe and report that actual result without chasing another bargain. "
            "Close the bargain panel normally using current legal close_bargain_panel via MCP before any "
            "purchase or sale, also if you chose not to bargain. Never use GUI or fabricate a close. Read a "
            "fresh complete normal quote and fresh legal settlement arguments after dialogue/panel closure. "
            "Normal primary payment mode already includes accepted bargain adjustments; it does not mean "
            "unbargained pricing. Never reuse the old offer_ref even if the displayed amount is unchanged. "
            "Trade only if currently legal with matching items and complete normal quote. Purchase requires "
            "sufficient capacity/cash and amount within the explicit ceiling; sale requires the currently legal "
            "single-item ordinary route. Do not recompute or override the quoted amount. Before trade retain "
            "observed cash, item refs, units and ownership/container facts. After the ONE trade submission, "
            "collect status and observe, compare actual cash delta against minus quoted purchase amount or "
            "plus quoted sale amount, verify selected items' transfer/ownership/units and other goods retained, "
            "and report any mismatch as unverified. Finish only that same customer's ordinary dialogue or "
            "legal dismissal within remaining budgets; do not receive another customer after a trade submission. "
            "Stop when this interaction is handled or any limit/blocker is reached. Report command IDs, exact "
            "choice/option labels and direction, why bargaining was chosen or declined, visible risk or unknown, "
            "attempt status separately from NPC acceptance, refreshed quote, cash/inventory delta and departure. "
            "Report missing scenarios as untested; never fabricate acceptance, settlement or both-direction coverage. "
        )
    elif args.m3a:
        prompt += (
            "Perform this bounded M3-A test ONLY in the caller-authorized isolated test slot. "
            f"Mode is {args.m3a}. Prefix fresh command IDs with {args.label}. "
            "Before EVERY write, observe immediately and copy one exact legal_actions entry with its "
            "arguments and identity/version. Write one action at a time. After EVERY act, call "
            "get_action_status with the original ID, use wait_for_update(command_terminal) if needed, "
            "then query the same ID again. Unknown, failed, timeout or connection loss: stop and query "
            "the original ID; never replay. A definite rejected/stale permits a fresh observe and new "
            "decision, at most two such retries. Never bypass legal_actions or alter amounts. "
            "Read current dialogue text and actual visible option text before deciding. Wait for automatic "
            "typing; use advance_dialogue next only for manual waits. Select only ordinary necessary "
            "options. No bargaining, wholesale, appraisal, special deals, closing day or save operations. "
            f"No purchases in this M3-A run. FLOW may start_business and receive at most {args.max_customers} customers, "
            "normally dismiss unsupported customers without trade, and wait for departure. SALE works "
            "only with the current customer, without starting or receiving. SORT performs only sorting. "
            "In FLOW/SALE, for a BUY customer (NPC wants to buy), read request_text, current visible "
            "dialogue and item names/units. Choose ONE appropriate item from current prepare_sale_items "
            "legal entries using that visible information. Explain the selection briefly, prepare it, "
            "then observe the exact normal quote. Submit at most ONE sell_visible_offer, only if legal "
            "and its current quote/selected item match. Verify actual cash increase equals amount, "
            "chosen goods leave the player counter, other goods remain. Finish that customer's normal "
            "dialogue or legal dismiss, wait for departure; never receive another customer after sale. "
            "In FLOW/SORT execute sort_inventory_default for player exactly ONCE when legal, preferably "
            "before receiving. Check original goods/units/ownership remain and report the actual status. "
            f"Maximum {args.max_writes} write attempts and 12 bounded revision waits of up to 10000 ms each. Stop "
            "when targets achieved or limits reached. Report command IDs, actual outcomes, selected item, "
            "quote, cash and inventory changes, refusal/dialogue coverage, sorting and departure. "
            "Unencountered dialogue/choice samples are NOT tested; never fabricate success. "
        )
        prompt += (
            "When the customer is absent and receive_next_customer is legal, receive the next customer "
            "until your remaining customer budget is used or the sale is complete; do not wait repeatedly "
            "for a revision that needs your next legal receive action. After three unchanged revision "
            "waits stop and report. "
        )
        if args.skip_sort:
            prompt += "Sorting already succeeded in this slot. Do NOT sort again; the instruction to sort once is already satisfied. "
    elif args.business_stage == "A":
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
    elif args.business_stage is None and args.m3a is None and not args.trade_panel and not args.m3b and not args.m3c and not args.m4:
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
    try:
        with output.open("x", encoding="utf-8") as stdout, output.with_suffix(".stderr.log").open("x", encoding="utf-8") as stderr:
            result = subprocess.run(command, cwd=ROOT, stdout=stdout, stderr=stderr,
                                    timeout=1800 if args.m4 else 900 if args.business_stage == "FLOW" or args.m3a == "FLOW" or args.trade_panel or args.m3b or args.m3c else 240)
    except BaseException:
        if m4_context:
            try:
                finish_m4_session(manifest_path, manifest, 124)
            except (ValueError, OSError):
                pass
        raise
    if m4_context:
        audit = finish_m4_session(manifest_path, manifest, result.returncode)
        audit_summary = {key: value for key, value in audit.items()
                         if key not in ("attempts", "latest_day", "latest_handoff")} if audit else {"error": "invalid_or_truncated_jsonl"}
        print(json.dumps({"exit_code": result.returncode, "evidence": str(output),
                          "session_id": args.session_id, "audit": audit_summary}, ensure_ascii=False))
    else:
        print(json.dumps({"exit_code": result.returncode, "evidence": str(output)}))
    raise SystemExit(result.returncode)


if __name__ == "__main__":
    main()
