"""Strict wire-v2 shapes shared by broker, journal, fixtures and adapter checks.

No game rules beyond the sole supported close_menu evidence predicate live here.
Arguments describe future collections but never enable additional actions.
"""
from __future__ import annotations

import hashlib
import json
import re

WIRE_VERSION = 2
WIRE_VERSION_V3 = 3
MAX_V2_MESSAGE_BYTES = 64 * 1024
MAX_V3_SNAPSHOT_BYTES = 64 * 1024
MAX_V3_MESSAGE_BYTES = 256 * 1024
MAX_DEPTH = 16
MAX_INT = 2_147_483_647
MAX_TIME = 9_007_199_254_740_991
REF = re.compile(r"[A-Za-z0-9_.:-]{1,128}\Z", re.ASCII)
REQUEST_KEYS = {"schema_version", "id", "broker_epoch", "action", "expected_world_epoch",
                "expected_revision", "expected_version", "arguments"}
COMMAND_KEYS = REQUEST_KEYS | {"start_deadline_unix_ms", "execution_timeout_ms"}
SNAPSHOT_KEYS = {"schema_version", "version", "world_epoch", "revision", "phase", "scene",
                 "loaded", "ready", "not_ready_reason", "cash", "menu_open", "can_close_menu",
                 "interaction", "business", "legal_actions"}
PROGRESS_KEYS = {"id", "broker_epoch", "stage", "sequence", "may_have_side_effects",
                 "started_at_unix_ms", "execution_deadline_unix_ms"}
RESULT_KEYS = {"schema_version", "id", "broker_epoch", "status", "reason", "before", "after", "execution"}
TERMINAL = frozenset({"succeeded", "rejected", "outcome_unknown"})
STAGES = {"validated": 0, "invoking": 1, "verifying": 2, "completed": 3, "uncertain": 3}

V3_ACTION_ARGUMENTS = {
    "close_menu": frozenset(), "start_business": frozenset(),
    "receive_next_customer": frozenset(),
    "advance_dialogue": frozenset({"customer_ref", "dialogue_ref", "mode"}),
    "choose_dialogue_option": frozenset({"customer_ref", "dialogue_ref", "option_ref"}),
    "prepare_purchase_items": frozenset({"customer_ref", "item_refs", "collection_fingerprint"}),
    "cancel_confirmation": frozenset(), "refuse_trade": frozenset({"customer_ref"}),
    "dismiss_customer": frozenset({"customer_ref"}),
}
V3_CANDIDATE_ARGUMENTS = {**V3_ACTION_ARGUMENTS, "purchase_visible_offer": frozenset({
    "customer_ref", "offer_ref", "item_refs", "collection_fingerprint", "mode",
    "amount", "max_spend", "authorization_ref"})}
V3_PRODUCTION_ACTIONS = frozenset({"close_menu"})
V3_OPTIONAL_ARGUMENTS = frozenset({"customer_ref", "item_refs", "offer_ref",
                                    "collection_fingerprint", "option_ref", "dialogue_ref",
                                    "mode", "amount", "max_spend", "authorization_ref"})


def integer(value, minimum=0, maximum=MAX_INT):
    return type(value) is int and minimum <= value <= maximum


def string(value, maximum=128, minimum=1):
    return type(value) is str and minimum <= len(value) <= maximum


def nullable_string(value, maximum=256):
    return value is None or string(value, maximum)


def shape(value, keys):
    return type(value) is dict and set(value) == keys


def compact_json_size(value):
    """Size of deterministic compact UTF-8 JSON, independent of input key order/spacing."""
    try:
        raw = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                         allow_nan=False).encode("utf-8")
    except (TypeError, ValueError, UnicodeError, RecursionError):
        return None
    return len(raw)


def response_size_valid(value, actual_bytes):
    """v3 envelopes may carry two bounded snapshots; legacy messages remain 64 KiB."""
    maximum = (MAX_V3_MESSAGE_BYTES if type(value) is dict and type(value.get("schema_version")) is int
               and value["schema_version"] == WIRE_VERSION_V3 else MAX_V2_MESSAGE_BYTES)
    return type(actual_bytes) is int and 0 <= actual_bytes <= maximum


def valid_arguments(value):
    if type(value) is not dict or not set(value) <= {
        "customer_ref", "item_refs", "offer_ref", "collection_fingerprint", "option_ref"
    }:
        return False
    for key, entry in value.items():
        if key == "item_refs":
            if (type(entry) is not list or not 1 <= len(entry) <= 64
                    or any(type(ref) is not str or REF.fullmatch(ref) is None for ref in entry)
                    or len(entry) != len(set(entry))):
                return False
        elif type(entry) is not str or REF.fullmatch(entry) is None:
            return False
    return True


def valid_arguments_v3(value):
    if type(value) is not dict or not set(value) <= V3_OPTIONAL_ARGUMENTS:
        return False
    for key, entry in value.items():
        if key == "item_refs":
            if (type(entry) is not list or not 1 <= len(entry) <= 64
                    or any(type(ref) is not str or REF.fullmatch(ref) is None for ref in entry)
                    or len(entry) != len(set(entry))):
                return False
        elif key in {"amount", "max_spend"}:
            if not integer(entry):
                return False
        elif type(entry) is not str or REF.fullmatch(entry) is None:
            return False
    return True


def valid_request(value):
    """Shape only; allowlist/action-specific empty arguments checked AFTER ID dedupe."""
    return (shape(value, REQUEST_KEYS) and type(value["schema_version"]) is int
            and value["schema_version"] == WIRE_VERSION
            and all(string(value[k]) for k in ("id", "broker_epoch", "action", "expected_world_epoch"))
            and integer(value["expected_revision"])
            and string(value["expected_version"], 256)
            and value["expected_version"] == f"{value['expected_world_epoch']}:{value['expected_revision']}"
            and valid_arguments(value["arguments"]))


def valid_command(value):
    return (shape(value, COMMAND_KEYS)
            and valid_request({k: value[k] for k in REQUEST_KEYS})
            and value["action"] == "close_menu" and value["arguments"] == {}
            and integer(value["start_deadline_unix_ms"], 1, MAX_TIME)
            and integer(value["execution_timeout_ms"], 1, 30_000))


def canonical_request(value):
    validator = valid_request_v3 if type(value) is dict and value.get("schema_version") == WIRE_VERSION_V3 else valid_request
    if not validator(value):
        raise ValueError("invalid_command")
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True, allow_nan=False)


def request_fingerprint(value):
    return hashlib.sha256(canonical_request(value).encode("utf-8")).hexdigest()


def valid_snapshot(value):
    if not shape(value, SNAPSHOT_KEYS):
        return False
    if not (type(value["schema_version"]) is int and value["schema_version"] == WIRE_VERSION
            and string(value["world_epoch"]) and integer(value["revision"])
            and string(value["version"], 256)
            and value["version"] == f"{value['world_epoch']}:{value['revision']}"
            and string(value["scene"], minimum=0) and string(value["phase"], minimum=0)
            and nullable_string(value["not_ready_reason"])
            and integer(value["cash"], -2_147_483_648)
            and all(type(value[k]) is bool for k in ("loaded", "ready", "menu_open", "can_close_menu"))):
        return False
    if ((value["ready"] and (not value["loaded"] or value["not_ready_reason"] is not None))
            or (not value["ready"] and value["not_ready_reason"] is None)
            or (value["can_close_menu"] and not (value["ready"] and value["menu_open"]))):
        return False
    interaction = value["interaction"]
    if not (shape(interaction, {"phase", "consistency", "reason", "operable"})
            and string(interaction["phase"])
            and type(interaction["consistency"]) is str
            and interaction["consistency"] in {"consistent", "updating", "unverified", "blocked"}
            and nullable_string(interaction["reason"])
            and type(interaction["operable"]) is bool
            and (not interaction["operable"] or (value["ready"] and interaction["consistency"] != "blocked"))):
        return False
    if value["business"] != {"support": "unsupported", "customer_ref": None, "item_refs": None,
                             "offer_ref": None, "collection_fingerprint": None}:
        return False
    expected = [{"action": "close_menu", "arguments": {}}] if value["can_close_menu"] else []
    return type(value["legal_actions"]) is list and value["legal_actions"] == expected


def valid_progress(value):
    if not (shape(value, PROGRESS_KEYS) and string(value["id"]) and string(value["broker_epoch"])
            and type(value["stage"]) is str and value["stage"] in STAGES
            and integer(value["sequence"], 1) and type(value["may_have_side_effects"]) is bool):
        return False
    effects = value["may_have_side_effects"]
    started, deadline = value["started_at_unix_ms"], value["execution_deadline_unix_ms"]
    if effects:
        return (value["stage"] != "validated" and integer(started, 1, MAX_TIME)
                and integer(deadline, 1, MAX_TIME) and 0 < deadline - started <= 30_000)
    return value["stage"] in {"validated", "completed"} and started is None and deadline is None


def progress_matches_command(progress, command):
    return (valid_progress(progress) and progress["id"] == command["id"]
            and progress["broker_epoch"] == command["broker_epoch"]
            and (not progress["may_have_side_effects"] or (
                progress["started_at_unix_ms"] < command["start_deadline_unix_ms"]
                and progress["execution_deadline_unix_ms"] == progress["started_at_unix_ms"] + command["execution_timeout_ms"])))


def valid_progress_transition(previous, current):
    if not valid_progress(current):
        return False
    if previous is None:
        return True
    if not valid_progress(previous) or current["id"] != previous["id"] or current["broker_epoch"] != previous["broker_epoch"]:
        return False
    if current["sequence"] == previous["sequence"]:
        return current == previous
    if current["sequence"] < previous["sequence"] or previous["stage"] in {"completed", "uncertain"}:
        return False
    if STAGES[current["stage"]] < STAGES[previous["stage"]]:
        return False
    if previous["may_have_side_effects"]:
        return (current["may_have_side_effects"] and all(current[k] == previous[k] for k in
                ("started_at_unix_ms", "execution_deadline_unix_ms")))
    return True


def valid_result_structure(value, require_execution=True):
    if not (shape(value, RESULT_KEYS) and type(value["schema_version"]) is int
            and value["schema_version"] == WIRE_VERSION and string(value["id"])
            and string(value["broker_epoch"]) and type(value["status"]) is str
            and value["status"] in TERMINAL and string(value["reason"], 512)
            and all(value[k] is None or valid_snapshot(value[k]) for k in ("before", "after"))):
        return False
    progress = value["execution"]
    if progress is None:
        return not require_execution and value["status"] != "succeeded"
    if not (valid_progress(progress) and progress["id"] == value["id"] and progress["broker_epoch"] == value["broker_epoch"]):
        return False
    if value["status"] == "rejected":
        return progress["stage"] == "completed" and not progress["may_have_side_effects"]
    if value["status"] == "outcome_unknown":
        return progress["stage"] == "uncertain" and progress["may_have_side_effects"]
    return (progress["stage"] == "completed" and progress["may_have_side_effects"]
            and valid_snapshot(value["before"]) and valid_snapshot(value["after"]))


def valid_success_result(command, result):
    if not valid_result_structure(result):
        return False
    if result["status"] != "succeeded":
        return True
    before, after = result["before"], result["after"]
    return (command["action"] == "close_menu" and command["arguments"] == {}
            and before["world_epoch"] == command["expected_world_epoch"]
            and before["revision"] == command["expected_revision"]
            and before["version"] == command["expected_version"]
            and valid_close_menu_evidence(before, after))


def valid_close_menu_evidence(before, after):
    """Shared evidence check, not a second action-admission policy in the Adapter."""
    return (valid_snapshot(before) and valid_snapshot(after)
            and before["ready"] and before["can_close_menu"] and after["ready"]
            and after["world_epoch"] == before["world_epoch"] and after["scene"] == before["scene"]
            and not after["menu_open"] and after["revision"] > before["revision"]
            and after["cash"] == before["cash"])


def _v3_text(value, nullable=False):
    return nullable_string(value, 256) if nullable else string(value, 256)


def _v3_refs(value, maximum=64):
    return (type(value) is list and len(value) <= maximum
            and all(type(ref) is str and REF.fullmatch(ref) is not None for ref in value)
            and len(value) == len(set(value)))


def _v3_text_list(value, maximum=64):
    return type(value) is list and len(value) <= maximum and all(_v3_text(v) for v in value)


def _valid_business_v3(business):
    """Strict validation for the C# BusinessState schema; kept separate from v2."""
    keys = {"support", "customer_ref", "item_refs", "offer_ref", "collection_fingerprint", "details"}
    if not shape(business, keys):
        return False
    if (business["support"] not in {"unsupported", "partial", "available"}
            or any(v is not None and (type(v) is not str or REF.fullmatch(v) is None)
                   for v in (business["customer_ref"], business["offer_ref"], business["collection_fingerprint"]))
            or not _v3_refs(business["item_refs"])):
        return False
    d = business["details"]
    if not shape(d, {"schema_version", "status", "customer", "containers", "items", "quote",
                     "ui", "capacity", "eligibility", "candidates"}):
        return False
    if type(d["schema_version"]) is not int or d["schema_version"] != 1 or d["status"] not in {"unavailable", "partial", "complete"}:
        return False
    c = d["customer"]
    if not (shape(c, {"presence", "arrived", "trade_type", "phase", "dialogue_ref", "dialogue_stage", "option_refs"})
            and c["presence"] in {"absent", "present", "unverified"}
            and (c["arrived"] is None or type(c["arrived"]) is bool)
            and c["trade_type"] in {"sell", "other", "unknown"}
            and c["phase"] in {"absent", "arriving", "dialogue", "trading", "leaving", "unknown"}
            and (c["dialogue_ref"] is None or (type(c["dialogue_ref"]) is str and REF.fullmatch(c["dialogue_ref"]) is not None))
            and c["dialogue_stage"] in {"absent", "typing", "waiting", "choices", "updating", "unknown"}
            and _v3_refs(c["option_refs"])):
        return False
    if (c["presence"] == "absent" and (business["customer_ref"] is not None or c["arrived"] is not None
            or c["phase"] not in {"absent", "leaving"} or c["trade_type"] != "unknown"
            or c["dialogue_ref"] is not None or c["option_refs"]
            or c["dialogue_stage"] not in {"absent", "unknown"})
            or (c["presence"] == "present" and business["customer_ref"] is None)
            or (c["dialogue_ref"] is None and c["option_refs"])):
        return False
    if (c["presence"] != "present"
            and (business["customer_ref"] is not None or c["dialogue_ref"] is not None
                 or c["option_refs"] or business["collection_fingerprint"] is not None)):
        return False
    if type(d["containers"]) is not list or len(d["containers"]) > 3 or type(d["items"]) is not list or len(d["items"]) > 64:
        return False
    container_names, memberships = set(), []
    for row in d["containers"]:
        if not (shape(row, {"container", "completeness", "reason", "item_refs"})
                and row["container"] in {"front", "back", "player"}
                and row["completeness"] in {"complete", "incomplete", "unavailable", "unimplemented"}
                and _v3_text(row["reason"], nullable=True) and _v3_refs(row["item_refs"])):
            return False
        if ((row["completeness"] == "complete" and row["reason"] is not None)
                or (row["completeness"] != "complete" and not _v3_text(row["reason"]))):
            return False
        if row["container"] in container_names:
            return False
        container_names.add(row["container"])
        memberships.extend(row["item_refs"])
    if len(memberships) != len(set(memberships)):
        return False
    item_refs = []
    for item in d["items"]:
        if not (shape(item, {"item_ref", "container", "name", "units", "owned", "shape_status", "width", "height"})
                and type(item["item_ref"]) is str and REF.fullmatch(item["item_ref"]) is not None
                and item["container"] in {"front", "back", "player"}
                and _v3_text(item["name"], nullable=True) and integer(item["units"], 1)
                and type(item["owned"]) is bool and item["shape_status"] in {"known", "unverified"}
                and (item["width"] is None or integer(item["width"], 1))
                and (item["height"] is None or integer(item["height"], 1))):
            return False
        if item["shape_status"] == "known" and (item["width"] is None or item["height"] is None):
            return False
        container_row = next((row for row in d["containers"] if row["container"] == item["container"]), None)
        if container_row is None or container_row["completeness"] in {"unavailable", "unimplemented"}:
            return False
        item_refs.append(item["item_ref"])
    if len(item_refs) != len(set(item_refs)) or (memberships and set(memberships) != set(item_refs)):
        return False
    for row in d["containers"]:
        expected_refs = [item["item_ref"] for item in d["items"] if item["container"] == row["container"]]
        if row["item_refs"] != expected_refs:
            return False
    if business["item_refs"] != item_refs:
        return False
    q = d["quote"]
    if not (shape(q, {"status", "mode", "displayed_total", "rows", "reason"})
            and q["status"] in {"complete", "unverified", "unavailable"}
            and q["mode"] in {"normal", "wholesale", "unknown"}
            and (q["displayed_total"] is None or integer(q["displayed_total"]))
            and type(q["rows"]) is list and len(q["rows"]) <= 64 and _v3_text(q["reason"], nullable=True)):
        return False
    quote_refs = []
    observed_total = 0
    for row in q["rows"]:
        if not (shape(row, {"item_ref", "name", "price_text", "amount"})
                and (row["item_ref"] is None or (type(row["item_ref"]) is str and REF.fullmatch(row["item_ref"]) is not None))
                and _v3_text(row["name"], nullable=True) and _v3_text(row["price_text"], nullable=True)
                and (row["amount"] is None or integer(row["amount"]))):
            return False
        if row["item_ref"] is not None:
            quote_refs.append(row["item_ref"])
        if row["amount"] is not None:
            observed_total += row["amount"]
            if observed_total > MAX_INT:
                return False
    if len(quote_refs) != len(set(quote_refs)) or any(ref not in item_refs for ref in quote_refs):
        return False
    ui = d["ui"]
    if not (shape(ui, {"status", "blockers", "coverage"})
            and ui["status"] in {"consistent", "updating", "unverified", "blocked"}
            and _v3_text_list(ui["blockers"]) and ui["coverage"] in {"known_roots_only", "verified"}):
        return False
    if q["status"] != "complete" and business["offer_ref"] is not None:
        return False
    if q["status"] == "complete" and not (
            d["status"] == "complete" and ui["status"] == "consistent"
            and ui["coverage"] == "verified" and not ui["blockers"]
            and c["presence"] == "present" and q["mode"] != "unknown"
            and q["displayed_total"] is not None and len(q["rows"]) > 0
            and all(row["price_text"] is not None for row in q["rows"])
            and all(row["amount"] is not None and row["item_ref"] is not None for row in q["rows"])
            and business["collection_fingerprint"] is not None and business["offer_ref"] is not None):
        return False
    if q["status"] == "complete":
        back = next((row for row in d["containers"] if row["container"] == "back"), None)
        if back is None or back["completeness"] != "complete":
            return False
        back_items = [item for item in d["items"] if item["container"] == "back"]
        if (any(item["owned"] for item in back_items)
                or [row["item_ref"] for row in q["rows"]] != back["item_refs"]):
            return False
        if q["mode"] == "normal":
            total = sum(row["amount"] for row in q["rows"])
            if total > MAX_INT or total != q["displayed_total"]:
                return False
    cap = d["capacity"]
    if not (shape(cap, {"status", "reason", "scope"})
            and cap["status"] in {"sufficient", "insufficient", "unverified"}
            and _v3_text(cap["reason"]) and _v3_text(cap["scope"])):
        return False
    eligibility = d["eligibility"]
    if not (shape(eligibility, {"purchase_enabled", "missing"})
            and type(eligibility["purchase_enabled"]) is bool and _v3_text_list(eligibility["missing"])):
        return False
    if len(eligibility["missing"]) != len(set(eligibility["missing"])):
        return False
    # No transport state may enable the economic purchase dispatcher.
    if eligibility["purchase_enabled"]:
        return False
    if type(d["candidates"]) is not list or len(d["candidates"]) > 64:
        return False
    for candidate in d["candidates"]:
        if not (shape(candidate, {"action", "status", "reason"})
                and type(candidate["action"]) is str and candidate["action"] in V3_CANDIDATE_ARGUMENTS
                and candidate["status"] in {"candidate", "blocked", "unverified"}
                and _v3_text(candidate["reason"])):
            return False
    return True


def valid_business_v3(business):
    try:
        return _valid_business_v3(business)
    except (KeyError, TypeError, ValueError):
        return False


def valid_snapshot_v3(value):
    keys = SNAPSHOT_KEYS
    if not shape(value, keys) or type(value.get("schema_version")) is not int or value["schema_version"] != WIRE_VERSION_V3:
        return False
    # Reuse identity/lifecycle checks while substituting only v3 business/actions.
    legacy_view = dict(value, schema_version=WIRE_VERSION,
                       business={"support": "unsupported", "customer_ref": None, "item_refs": None,
                                 "offer_ref": None, "collection_fingerprint": None},
                       legal_actions=([{"action": "close_menu", "arguments": {}}]
                                      if value.get("can_close_menu") else []))
    if not valid_snapshot(legacy_view) or not valid_business_v3(value["business"]):
        return False
    actions = value["legal_actions"]
    if type(actions) is not list or len(actions) > len(V3_ACTION_ARGUMENTS):
        return False
    seen = set()
    for entry in actions:
        if type(entry) is not dict or set(entry) != {"action", "arguments"}:
            return False
        action, arguments = entry["action"], entry["arguments"]
        if (type(action) is not str or action not in V3_PRODUCTION_ACTIONS or action in seen
                or not valid_arguments_v3(arguments) or set(arguments) != V3_ACTION_ARGUMENTS[action]):
            return False
        if action == "advance_dialogue" and arguments["mode"] not in {"skip", "next"}:
            return False
        if not valid_action_binding_v3(value["business"], action, arguments):
            return False
        seen.add(action)
    expected_close = {"action": "close_menu", "arguments": {}}
    if value["can_close_menu"] != (expected_close in actions):
        return False
    size = compact_json_size(value)
    return size is not None and size <= MAX_V3_SNAPSHOT_BYTES


def valid_action_binding_v3(business, action, arguments):
    """Tie every emitted opaque handle back to the same observed business snapshot."""
    if action in {"advance_dialogue", "choose_dialogue_option", "prepare_purchase_items",
                  "refuse_trade", "dismiss_customer"}:
        if arguments.get("customer_ref") != business["customer_ref"] or business["customer_ref"] is None:
            return False
    if action in {"advance_dialogue", "choose_dialogue_option"}:
        customer = business["details"]["customer"]
        if arguments.get("dialogue_ref") != customer["dialogue_ref"] or customer["dialogue_ref"] is None:
            return False
    if action == "choose_dialogue_option":
        if arguments.get("option_ref") not in business["details"]["customer"]["option_refs"]:
            return False
    if action == "prepare_purchase_items":
        if (arguments.get("item_refs") != business["item_refs"] or business["item_refs"] is None
                or arguments.get("collection_fingerprint") != business["collection_fingerprint"]
                or business["collection_fingerprint"] is None):
            return False
    return True


def valid_request_v3(value):
    return (shape(value, REQUEST_KEYS) and type(value["schema_version"]) is int
            and value["schema_version"] == WIRE_VERSION_V3
            and all(string(value[k]) for k in ("id", "broker_epoch", "action", "expected_world_epoch"))
            and integer(value["expected_revision"])
            and string(value["expected_version"], 256)
            and value["expected_version"] == f"{value['expected_world_epoch']}:{value['expected_revision']}"
            and valid_arguments_v3(value["arguments"]))


def valid_command_v3(value):
    return (shape(value, COMMAND_KEYS)
            and valid_request_v3({k: value[k] for k in REQUEST_KEYS})
            and value["action"] in V3_PRODUCTION_ACTIONS
            and value["arguments"] == {}
            and integer(value["start_deadline_unix_ms"], 1, MAX_TIME)
            and integer(value["execution_timeout_ms"], 1, 30_000))


def valid_result_structure_v3(value, require_execution=True):
    size = compact_json_size(value)
    if size is None or size > MAX_V3_MESSAGE_BYTES:
        return False
    if not (shape(value, RESULT_KEYS) and type(value["schema_version"]) is int
            and value["schema_version"] == WIRE_VERSION_V3 and string(value["id"])
            and string(value["broker_epoch"]) and type(value["status"]) is str
            and value["status"] in TERMINAL and string(value["reason"], 512)
            and all(v is None or valid_snapshot_v3(v) for v in (value["before"], value["after"]))):
        return False
    progress = value["execution"]
    if progress is None:
        return not require_execution and value["status"] != "succeeded"
    if not (valid_progress(progress) and progress["id"] == value["id"] and progress["broker_epoch"] == value["broker_epoch"]):
        return False
    if value["status"] == "rejected":
        return progress["stage"] == "completed" and not progress["may_have_side_effects"]
    if value["status"] == "outcome_unknown":
        return progress["stage"] == "uncertain" and progress["may_have_side_effects"]
    return (progress["stage"] == "completed" and progress["may_have_side_effects"]
            and valid_snapshot_v3(value["before"]) and valid_snapshot_v3(value["after"]))


def valid_success_result_v3(command, result):
    if not valid_result_structure_v3(result):
        return False
    if result["status"] != "succeeded":
        return True
    before, after = result["before"], result["after"]
    if (before["world_epoch"] != command["expected_world_epoch"]
            or before["revision"] != command["expected_revision"]
            or before["version"] != command["expected_version"]
            or after["world_epoch"] != before["world_epoch"]
            or after["revision"] <= before["revision"]):
        return False
    # Until action-specific verifiers are registered, business commands cannot claim success.
    if command["action"] != "close_menu":
        return False
    return valid_close_menu_evidence_v3(before, after)


def valid_close_menu_evidence_v3(before, after):
    return (valid_snapshot_v3(before) and valid_snapshot_v3(after)
            and before["ready"] and before["can_close_menu"] and after["ready"]
            and after["world_epoch"] == before["world_epoch"] and after["scene"] == before["scene"]
            and not after["menu_open"] and after["revision"] > before["revision"]
            and after["cash"] == before["cash"])


def strict_json_loads(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("duplicate_json_key")
            result[key] = value
        return result

    try:
        value = json.loads(raw, object_pairs_hook=pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite_json")))
    except RecursionError as exc:
        raise ValueError("json_too_deep") from exc

    def depth(node, level):
        if level > MAX_DEPTH:
            raise ValueError("json_too_deep")
        if isinstance(node, dict):
            for child in node.values():
                depth(child, level + 1)
        elif isinstance(node, list):
            for child in node:
                depth(child, level + 1)
    depth(value, 0)
    return value
