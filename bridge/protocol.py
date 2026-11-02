"""Strict wire-v2, wire-v3, and wire-v4 shapes shared by broker and adapters.

V2 and v3 are frozen compatibility contracts. V4 adds a separately validated generic interaction
surface and finite interaction receipts without widening either historical validator.
"""
from __future__ import annotations

import hashlib
import json
import re

WIRE_VERSION = 2
WIRE_VERSION_V3 = 3
WIRE_VERSION_V4 = 4
MAX_V2_MESSAGE_BYTES = 64 * 1024
MAX_V3_SNAPSHOT_BYTES = 64 * 1024
MAX_V3_MESSAGE_BYTES = 256 * 1024
MAX_V4_SNAPSHOT_BYTES = 64 * 1024
MAX_V4_MESSAGE_BYTES = 256 * 1024
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
    "prepare_sale_items": frozenset({"customer_ref", "item_refs", "collection_fingerprint"}),
    "sell_visible_offer": frozenset({"customer_ref", "offer_ref", "item_refs",
        "collection_fingerprint", "mode", "amount"}),
    "sort_inventory_default": frozenset({"container"}),
    "resume_inspection_handoff": frozenset({"handoff_ref"}),
    "close_business": frozenset(), "open_end_of_day": frozenset(),
    "confirm_end_of_day": frozenset({"settlement_ref", "amount", "max_spend"}),
    "close_day_report": frozenset(),
    "cancel_confirmation": frozenset(), "refuse_trade": frozenset({"customer_ref"}),
    "dismiss_customer": frozenset({"customer_ref"}),
    "open_trade_panel": frozenset({"customer_ref"}),
    "open_bargain_panel": frozenset({"customer_ref", "mode"}),
    "close_bargain_panel": frozenset({"customer_ref", "mode"}),
    "choose_bargain_option": frozenset({"customer_ref", "mode", "option_ref"}),
    "purchase_visible_offer": frozenset({"customer_ref", "offer_ref", "item_refs",
        "collection_fingerprint", "mode", "amount", "max_spend"}),
}
V3_CANDIDATE_ARGUMENTS = V3_ACTION_ARGUMENTS
# Availability is asserted per snapshot by legal_actions, then bound to that snapshot below.
# refuse_trade remains descriptive only until it has its own native operation.
V3_PRODUCTION_ACTIONS = frozenset(V3_ACTION_ARGUMENTS) - {"refuse_trade"}
GENERIC_INTERACTION_ACTIONS = frozenset({"advance_dialogue", "choose_dialogue_option", "dismiss_customer"})
V4_GENERIC_ACTION_ARGUMENTS = {
    "advance_dialogue": frozenset({"customer_ref", "dialogue_ref", "mode", "surface_ref", "control_ref"}),
    "choose_dialogue_option": frozenset({"customer_ref", "dialogue_ref", "option_ref", "surface_ref", "control_ref"}),
    "dismiss_customer": frozenset({"customer_ref", "surface_ref", "control_ref"}),
}
V4_ACTION_ARGUMENTS = dict(V3_ACTION_ARGUMENTS)
V4_ACTION_ARGUMENTS.update(V4_GENERIC_ACTION_ARGUMENTS)
V4_PRODUCTION_ACTIONS = V3_PRODUCTION_ACTIONS
DAY_ACTIONS = frozenset({"close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"})
V3_OPTIONAL_ARGUMENTS = frozenset({"customer_ref", "item_refs", "offer_ref",
                                    "collection_fingerprint", "option_ref", "dialogue_ref",
                                    "mode", "amount", "max_spend", "authorization_ref"})
V3_OPTIONAL_ARGUMENTS |= {"container"}
V3_OPTIONAL_ARGUMENTS |= {"handoff_ref"}
V3_OPTIONAL_ARGUMENTS |= {"settlement_ref"}
BARGAIN_ACTIONS = frozenset({"open_bargain_panel", "close_bargain_panel", "choose_bargain_option"})
SURFACE_KINDS = frozenset({"none", "dialogue", "customer_completion", "native_panel", "unknown"})
SURFACE_PHASES = frozenset({"absent", "typing", "waiting_for_advance", "awaiting_choice",
                            "completed", "progressing", "unsupported", "unknown"})
SURFACE_STATUSES = frozenset({"idle", "progressing", "awaiting_agent", "awaiting_user",
                              "unsupported", "unknown"})
SURFACE_EFFECT_CONTRACTS = frozenset({"presentation", "document_gift", "customer_departure", "unknown"})
INTERACTION_EFFECTS = frozenset({"presentation", "dialogue_transition", "story_state",
                                 "inventory_transfer", "cash", "reputation", "customer_departure",
                                 "day_transition", "external_scope"})
SURFACE_CONTROL_ACTIONS = GENERIC_INTERACTION_ACTIONS
SURFACE_LEGACY_SNAPSHOT_KEYS = SNAPSHOT_KEYS | {"surface"}
INTERACTION_RESULT_KEYS = {"input_receipt", "verification", "effect_contract", "observed_effects",
                           "unverified_effects", "cash_delta", "added_item_refs", "removed_item_refs"}
RESULT_KEYS_V4 = RESULT_KEYS | {"interaction_result"}


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
    """v3/v4 envelopes may carry two bounded snapshots; legacy messages remain 64 KiB."""
    version = value.get("schema_version") if type(value) is dict else None
    maximum = MAX_V4_MESSAGE_BYTES if type(version) is int and version == WIRE_VERSION_V4 else (
        MAX_V3_MESSAGE_BYTES if type(version) is int and version == WIRE_VERSION_V3 else MAX_V2_MESSAGE_BYTES)
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
        elif key == "amount":
            if "settlement_ref" in value:
                if not integer(entry, -2_147_483_648, MAX_INT):
                    return False
            elif not integer(entry):
                return False
        elif key == "max_spend":
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
    validator = (valid_request_v4 if type(value) is dict and value.get("schema_version") == WIRE_VERSION_V4 else
                 valid_request_v3 if type(value) is dict and value.get("schema_version") == WIRE_VERSION_V3 else
                 valid_request)
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


def _is_ordered_subset(values, source):
    at = 0
    for value in values:
        while at < len(source) and source[at] != value:
            at += 1
        if at == len(source):
            return False
        at += 1
    return True


def _valid_inspection_handoff(value):
    keys = {"requires_manual_inspection", "status", "handoff_ref", "customer_ref",
            "items", "reason", "last_resumed_handoff_ref"}
    if not shape(value, keys):
        return False
    if (type(value["requires_manual_inspection"]) is not bool
            or value["status"] not in {"waiting_for_user", "resumed"}
            or type(value["handoff_ref"]) is not str or REF.fullmatch(value["handoff_ref"]) is None
            or type(value["customer_ref"]) is not str or REF.fullmatch(value["customer_ref"]) is None
            or value["reason"] not in {"manual_item_inspection", "test_inspection_handoff"}
            or (value["last_resumed_handoff_ref"] is not None and
                (type(value["last_resumed_handoff_ref"]) is not str
                 or REF.fullmatch(value["last_resumed_handoff_ref"]) is None))):
        return False
    if value["status"] == "waiting_for_user":
        if (not value["requires_manual_inspection"]
                or value["last_resumed_handoff_ref"] == value["handoff_ref"]):
            return False
    elif value["requires_manual_inspection"] or value["last_resumed_handoff_ref"] != value["handoff_ref"]:
        return False
    items = value["items"]
    if type(items) is not list or not 1 <= len(items) <= 64:
        return False
    refs = []
    for item in items:
        if (not shape(item, {"item_ref", "name"}) or type(item["item_ref"]) is not str
                or REF.fullmatch(item["item_ref"]) is None or not _v3_text(item["name"], nullable=True)):
            return False
        refs.append(item["item_ref"])
    return len(refs) == len(set(refs))


def _valid_day_v3(value):
    keys = {"day_number", "remaining_customers", "stage", "reason", "can_close", "can_sleep",
            "can_confirm", "report_open", "transition_pending", "settlement"}
    if not shape(value, keys):
        return False
    if (value["day_number"] is not None and not integer(value["day_number"])
            or value["remaining_customers"] is not None and not integer(value["remaining_customers"])
            or value["stage"] not in {"unavailable", "morning", "open", "closed", "settlement", "next_morning"}
            or not _v3_text(value["reason"], nullable=True)
            or any(type(value[k]) is not bool for k in ("can_close", "can_sleep", "can_confirm",
                                                         "report_open", "transition_pending"))):
        return False
    settlement = value["settlement"]
    if settlement is None:
        return True
    if (not shape(settlement, {"settlement_ref", "total", "services"})
            or type(settlement["settlement_ref"]) is not str
            or REF.fullmatch(settlement["settlement_ref"]) is None
            or not integer(settlement["total"], -2_147_483_648, MAX_INT)
            or type(settlement["services"]) is not list or len(settlement["services"]) > 32):
        return False
    for service in settlement["services"]:
        if (not shape(service, {"label", "cost_text", "cost", "selected"})
                or not _v3_text(service["label"]) or not _v3_text(service["cost_text"])
                or not integer(service["cost"], -2_147_483_648, MAX_INT)
                or type(service["selected"]) is not bool):
            return False
    return True


_NONBLOCKING_UNOBSERVABLE_UI_BLOCKERS = frozenset({
    "global_ui_stack_unobservable", "global_raycast_order_unobservable",
    "quote_rebuild_completion_unobservable", "tutorial_group_active_unverified",
    "control_hints_active_unverified",
})


def _has_action_blocking_evidence(blockers, action):
    """Ignore known coverage placeholders, while retaining concrete action blockers."""
    if type(blockers) is not list:
        return True
    for blocker in blockers:
        if blocker in _NONBLOCKING_UNOBSERVABLE_UI_BLOCKERS:
            continue
        if action in {"advance_dialogue", "choose_dialogue_option"} and blocker == "dialogue_active":
            continue
        if action in {"prepare_purchase_items", "dismiss_customer"} and blocker == "dialogue_active":
            continue
        if action in BARGAIN_ACTIONS and blocker == "bargain_panel":
            continue
        if action == "close_bargain_panel" and blocker == "dialogue_active":
            continue
        return True
    return False


def _valid_bargain_v3(value):
    keys = {"available", "phase", "direction", "terms_revision", "options", "reputation_text",
            "hint_text", "last_option_ref", "result", "result_text"}
    if not (shape(value, keys) and type(value["available"]) is bool
            and value["phase"] in {"unavailable", "closed", "choosing", "responding"}
            and value["direction"] in {"buy", "sell", "unknown"}
            and integer(value["terms_revision"])
            and type(value["options"]) is list and len(value["options"]) <= 16
            and all(_v3_text(value[k], nullable=True) for k in ("reputation_text", "hint_text", "result_text"))
            and (value["last_option_ref"] is None or
                 type(value["last_option_ref"]) is str and REF.fullmatch(value["last_option_ref"]) is not None)
            and value["result"] in {"none", "responding", "price_changed", "unchanged", "customer_left", "unknown"}):
        return False
    if value["phase"] in {"closed", "unavailable"} and value["options"]:
        return False
    refs = []
    for option in value["options"]:
        if not (shape(option, {"option_ref", "text", "kind", "price_change_percent", "enabled", "selected"})
                and type(option["option_ref"]) is str and REF.fullmatch(option["option_ref"]) is not None
                and _v3_text(option["text"])
                and option["kind"] in {"lower_purchase_price", "raise_purchase_price", "raise_sale_price", "lower_sale_price"}
                and integer(option["price_change_percent"], -100, 100) and option["price_change_percent"] != 0
                and type(option["enabled"]) is bool and type(option["selected"]) is bool):
            return False
        refs.append(option["option_ref"])
    return len(refs) == len(set(refs))


def _blocks_trade_observation_v3(ui):
    blockers = ui["blockers"]
    return ("ui_reader_failed" in blockers or ui["status"] == "blocked" and not (
        "bargain_panel" in blockers and all(blocker == "bargain_panel"
            or blocker in _NONBLOCKING_UNOBSERVABLE_UI_BLOCKERS for blocker in blockers)))


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
    if type(d) is not dict or type(d.get("schema_version")) is not int or d["schema_version"] not in {1, 2}:
        return False
    schema = d["schema_version"]
    detail_keys = {"schema_version", "status", "customer", "containers", "items", "quote",
                   "ui", "capacity", "eligibility", "candidates"}
    if schema == 2:
        detail_keys.add("front_collection_fingerprint")
        # Optional on schema 2 so historical journal observations remain readable.
        if type(d) is dict and "inspection_handoff" in d:
            detail_keys.add("inspection_handoff")
        if "bargain" in d:
            detail_keys.add("bargain")
        if "day" in d:
            detail_keys.add("day")
    if not shape(d, detail_keys):
        return False
    if schema == 2 and d.get("bargain") is not None and not _valid_bargain_v3(d["bargain"]):
        return False
    if schema == 2 and d.get("day") is not None and not _valid_day_v3(d["day"]):
        return False
    if (d["status"] not in {"unavailable", "partial", "complete"}
            or (schema == 2 and d["front_collection_fingerprint"] is not None
                and (type(d["front_collection_fingerprint"]) is not str
                     or REF.fullmatch(d["front_collection_fingerprint"]) is None))):
        return False
    c = d["customer"]
    customer_keys = {"presence", "arrived", "trade_type", "phase", "dialogue_ref", "dialogue_stage", "option_refs"}
    if schema == 2:
        customer_keys |= {"dialogue_text", "is_typing", "waiting_for_advance", "options"}
        # Optional so schema-2 journal history written before this field remains readable.
        if type(c) is dict and "request_text" in c:
            customer_keys.add("request_text")
        if type(c) is dict and "trade_panel" in c:
            customer_keys.add("trade_panel")
    if not (shape(c, customer_keys)
            and c["presence"] in {"absent", "present", "unverified"}
            and (c["arrived"] is None or type(c["arrived"]) is bool)
            and c["trade_type"] in ({"sell", "buy", "other", "unknown"} if schema == 2 else {"sell", "other", "unknown"})
            and c["phase"] in {"absent", "arriving", "dialogue", "trading", "leaving", "unknown"}
            and (c["dialogue_ref"] is None or (type(c["dialogue_ref"]) is str and REF.fullmatch(c["dialogue_ref"]) is not None))
            and c["dialogue_stage"] in {"absent", "typing", "waiting", "choices", "updating", "unknown"}
            and _v3_refs(c["option_refs"])):
        return False
    if schema == 2:
        options = c["options"]
        if (("request_text" in c and not _v3_text(c["request_text"], nullable=True))
                or ("trade_panel" in c and c["trade_panel"] not in {None, "closed", "buy", "sell", "unavailable"})
                or not _v3_text(c["dialogue_text"], nullable=True)
                or (c["is_typing"] is not None and type(c["is_typing"]) is not bool)
                or (c["waiting_for_advance"] is not None and type(c["waiting_for_advance"]) is not bool)
                or type(options) is not list or len(options) > 16):
            return False
        option_rows = []
        for option in options:
            if not (shape(option, {"option_ref", "text"})
                    and type(option["option_ref"]) is str and REF.fullmatch(option["option_ref"]) is not None
                    and _v3_text(option["text"])):
                return False
            option_rows.append(option["option_ref"])
        if option_rows != c["option_refs"] or len(option_rows) != len(set(option_rows)):
            return False
        if option_rows and c["dialogue_stage"] != "choices":
            return False
        if c["dialogue_ref"] is None and (c["dialogue_text"] is not None
                or c["is_typing"] is not None or c["waiting_for_advance"] is not None or options):
            return False
    if schema == 2 and "inspection_handoff" in d:
        handoff = d["inspection_handoff"]
        if handoff is not None and not _valid_inspection_handoff(handoff):
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
    if schema == 1:
        if business["item_refs"] != item_refs:
            return False
    else:
        back = next((row for row in d["containers"] if row["container"] == "back"), None)
        back_item_refs = ([ref for ref in back["item_refs"]
                           if any(item["item_ref"] == ref and
                                  (not item["owned"] if c["trade_type"] == "sell" else item["owned"])
                                  for item in d["items"])]
                          if c["presence"] == "present" and c["trade_type"] in {"sell", "buy"}
                          and back is not None and back["completeness"] == "complete" else [])
        if (not _is_ordered_subset(business["item_refs"], back_item_refs)
                or (c["presence"] != "present" and business["item_refs"])):
            return False
        if d["front_collection_fingerprint"] is not None:
            front = next((row for row in d["containers"] if row["container"] == "front"), None)
            if (c["presence"] != "present" or c["trade_type"] != "sell" or front is None
                    or front["completeness"] != "complete" or not front["item_refs"]):
                return False
    q = d["quote"]
    quote_keys = {"status", "mode", "displayed_total", "rows", "reason"}
    if schema == 2:
        quote_keys.add("wholesale_total")
    if not (shape(q, quote_keys)
            and q["status"] in {"complete", "unverified", "unavailable"}
            and q["mode"] in {"normal", "wholesale", "unknown"}
            and (q["displayed_total"] is None or integer(q["displayed_total"]))
            and type(q["rows"]) is list and len(q["rows"]) <= 64 and _v3_text(q["reason"], nullable=True)):
        return False
    if schema == 2 and (q["wholesale_total"] is not None and not integer(q["wholesale_total"])):
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
    quote_item_refs = set(business["item_refs"] if schema == 1 else item_refs)
    if len(quote_refs) != len(set(quote_refs)) or any(ref not in quote_item_refs for ref in quote_refs):
        return False
    ui = d["ui"]
    if not (shape(ui, {"status", "blockers", "coverage"})
            and ui["status"] in {"consistent", "updating", "unverified", "blocked"}
            and _v3_text_list(ui["blockers"]) and ui["coverage"] in {"known_roots_only", "verified"}):
        return False
    if q["status"] != "complete" and business["offer_ref"] is not None:
        return False
    if q["status"] == "complete":
        if schema == 1:
            if not (d["status"] == "complete" and ui["status"] == "consistent"
                    and ui["coverage"] == "verified" and not ui["blockers"]
                    and c["presence"] == "present" and q["mode"] != "unknown"
                    and q["displayed_total"] is not None and len(q["rows"]) > 0
                    and all(row["price_text"] is not None for row in q["rows"])
                    and all(row["amount"] is not None and row["item_ref"] is not None for row in q["rows"])
                    and business["collection_fingerprint"] is not None and business["offer_ref"] is not None):
                return False
        else:
            back = next((row for row in d["containers"] if row["container"] == "back"), None)
            if not (c["presence"] == "present"
                    and c["trade_type"] in {"sell", "buy"} and c["phase"] == "trading"
                    and q["mode"] == "normal" and q["displayed_total"] is not None
                    and q["reason"] is None
                    and business["offer_ref"] is not None
                    and business["collection_fingerprint"] is not None
                    and business["item_refs"]
                    and back is not None and back["completeness"] == "complete"):
                return False
    if q["status"] == "complete":
        back = next((row for row in d["containers"] if row["container"] == "back"), None)
        if back is None or back["completeness"] != "complete":
            return False
        if schema == 1:
            back_items = [item for item in d["items"] if item["container"] == "back"]
            if (any(item["owned"] for item in back_items)
                    or [row["item_ref"] for row in q["rows"]] != back["item_refs"]):
                return False
            if q["mode"] == "normal":
                total = sum(row["amount"] for row in q["rows"])
                if total > MAX_INT or total != q["displayed_total"]:
                    return False
        if (d["status"] == "unavailable" or ui["status"] == "updating"
                or (ui["status"] == "blocked" if schema == 1 else _blocks_trade_observation_v3(ui))):
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
    if eligibility["purchase_enabled"] and not (schema == 2 and q["status"] == "complete"
            and cap["status"] == "sufficient" and not eligibility["missing"]
            and c["presence"] == "present"
            and c["trade_type"] == "sell" and c["phase"] == "trading"):
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
    if type(actions) is not list or len(actions) > 64:
        return False
    seen = set()
    for entry in actions:
        if type(entry) is not dict or set(entry) != {"action", "arguments"}:
            return False
        action, arguments = entry["action"], entry["arguments"]
        if (type(action) is not str or action not in V3_PRODUCTION_ACTIONS
                or type(arguments) is not dict or not valid_arguments_v3(arguments)
                or set(arguments) != V3_ACTION_ARGUMENTS[action]):
            return False
        action_identity = (action, json.dumps(arguments, sort_keys=True, separators=(",", ":"), ensure_ascii=False))
        if action_identity in seen:
            return False
        if action == "advance_dialogue" and arguments["mode"] not in {"skip", "next"}:
            return False
        if not valid_action_binding_v3(value["business"], action, arguments):
            return False
        if (action == "confirm_end_of_day"
                and arguments["max_spend"] != max(0, arguments["amount"])):
            return False
        if not valid_action_state_v3(value, action, arguments):
            return False
        seen.add(action_identity)
    handoff = value["business"]["details"].get("inspection_handoff")
    pending_handoff = (type(handoff) is dict and handoff["requires_manual_inspection"]
                       and handoff["status"] == "waiting_for_user")
    if pending_handoff:
        expected_actions = ([{"action": "resume_inspection_handoff",
                              "arguments": {"handoff_ref": handoff["handoff_ref"]}}]
                            if value["ready"] else [])
        if actions != expected_actions:
            return False
    elif any(entry["action"] == "resume_inspection_handoff" for entry in actions):
        return False
    expected_close = {"action": "close_menu", "arguments": {}}
    if (value["can_close_menu"] and not pending_handoff) != (expected_close in actions):
        return False
    purchase_enabled = value["business"]["details"]["eligibility"]["purchase_enabled"]
    purchase_is_legal = any(action["action"] == "purchase_visible_offer" for action in actions)
    if purchase_enabled != purchase_is_legal:
        return False
    size = compact_json_size(value)
    return size is not None and size <= MAX_V3_SNAPSHOT_BYTES


def valid_action_binding_v3(business, action, arguments):
    """Tie every emitted opaque handle back to the same observed business snapshot."""
    details = business["details"]
    day_gate = details.get("day") if details["schema_version"] == 2 else None
    if (type(day_gate) is dict and (day_gate["transition_pending"] or day_gate["report_open"]
                                    or day_gate["stage"] == "settlement")
            and action not in DAY_ACTIONS and action != "resume_inspection_handoff"):
        return False
    if action in DAY_ACTIONS:
        day = details.get("day") if details["schema_version"] == 2 else None
        if type(day) is not dict:
            return False
        if action == "close_business":
            customer = details["customer"]
            return (day["stage"] == "open" and day["remaining_customers"] == 0
                    and day["can_close"] is True and not day["report_open"] and day["settlement"] is None
                    and customer["presence"] == "absent")
        if action == "open_end_of_day":
            return (day["stage"] == "closed" and day["can_sleep"] is True
                    and not day["report_open"] and day["settlement"] is None)
        if action == "confirm_end_of_day":
            settlement = day["settlement"]
            return (day["stage"] == "settlement" and day["can_confirm"] is True and not day["report_open"]
                    and type(settlement) is dict
                    and arguments.get("settlement_ref") == settlement["settlement_ref"]
                    and arguments.get("amount") == settlement["total"]
                    and arguments.get("max_spend", -1) >= max(0, arguments.get("amount", 0)))
        return (day["stage"] in {"morning", "next_morning"} and day["report_open"] is True
                and day["settlement"] is None)
    if action == "resume_inspection_handoff":
        handoff = details.get("inspection_handoff")
        return (details["schema_version"] == 2 and type(handoff) is dict
                and handoff["requires_manual_inspection"] is True
                and handoff["status"] == "waiting_for_user"
                and arguments.get("handoff_ref") == handoff["handoff_ref"])
    if ("bargain_panel" in details["ui"]["blockers"]
            and action not in BARGAIN_ACTIONS and action != "close_menu"):
        return False
    if action in {"advance_dialogue", "choose_dialogue_option", "prepare_purchase_items",
                  "prepare_sale_items", "sell_visible_offer",
                  "refuse_trade", "dismiss_customer", "open_trade_panel"}:
        if arguments.get("customer_ref") != business["customer_ref"] or business["customer_ref"] is None:
            return False
    if action in {"advance_dialogue", "choose_dialogue_option"}:
        if details["schema_version"] != 2:
            return False
        customer = details["customer"]
        if arguments.get("dialogue_ref") != customer["dialogue_ref"] or customer["dialogue_ref"] is None:
            return False
        if action == "advance_dialogue":
            if (arguments.get("mode") == "skip" and customer["is_typing"] is not True
                    or arguments.get("mode") == "next"
                    and (customer["is_typing"] is True or customer["waiting_for_advance"] is not True)):
                return False
    if action == "choose_dialogue_option":
        if arguments.get("option_ref") not in business["details"]["customer"]["option_refs"]:
            return False
    schema = details["schema_version"]
    if action in BARGAIN_ACTIONS:
        customer = details["customer"]
        bargain = details.get("bargain")
        leaving_response = (action == "close_bargain_panel" and bargain is not None
            and customer["trade_type"] == "other" and bargain["phase"] == "responding"
            and bargain["result"] == "responding"
            and bargain["terms_revision"] > 0 and bargain["last_option_ref"] is not None)
        if (schema != 2 or bargain is None or business["customer_ref"] is None
                or arguments.get("customer_ref") != business["customer_ref"]
                or customer["presence"] != "present" or customer["arrived"] is not True
                or arguments.get("mode") not in {"buy", "sell"}
                or arguments["mode"] != customer["trade_type"] and not leaving_response
                or arguments["mode"] != bargain["direction"]
                or _has_action_blocking_evidence(details["ui"]["blockers"], action)
                or details["ui"]["status"] == "updating"
                or details["ui"]["status"] == "blocked" and not details["ui"]["blockers"]):
            return False
        if action == "close_bargain_panel":
            return bargain["phase"] in {"choosing", "responding"}
        if (not bargain["available"] or customer["dialogue_ref"] is not None
                or customer["phase"] in {"dialogue", "leaving"}
                or customer["dialogue_stage"] in {"typing", "waiting", "choices", "updating"}
                or customer.get("is_typing") is True or customer.get("waiting_for_advance") is True):
            return False
        if action == "open_bargain_panel":
            return bargain["phase"] == "closed"
        quote = details["quote"]
        return (bargain["phase"] == "choosing" and quote["status"] == "complete"
            and quote["mode"] == "normal" and quote["displayed_total"] is not None
            and business["offer_ref"] is not None and bool(business["item_refs"]) and any(
            option["option_ref"] == arguments.get("option_ref") and option["enabled"] and not option["selected"]
            for option in bargain["options"]))
    if action == "open_trade_panel":
        customer = details["customer"]
        if (schema != 2 or customer["presence"] != "present" or customer["arrived"] is not True
                or customer["trade_type"] not in {"buy", "sell"} or customer.get("trade_panel") != "closed"
                or customer["dialogue_ref"] is not None or customer["phase"] in {"dialogue", "leaving"}
                or customer.get("is_typing") is True or customer.get("waiting_for_advance") is True
                or _has_action_blocking_evidence(details["ui"]["blockers"], action)):
            return False
    if action == "prepare_purchase_items":
        front = next((row for row in details["containers"] if row["container"] == "front"), None)
        if (schema != 2 or front is None or front["completeness"] != "complete"
                or arguments.get("item_refs") != front["item_refs"]
                or arguments.get("collection_fingerprint") != details["front_collection_fingerprint"]
                or details["front_collection_fingerprint"] is None):
            return False
    if action == "prepare_sale_items":
        customer = details["customer"]
        player = next((row for row in details["containers"] if row["container"] == "player"), None)
        back = next((row for row in details["containers"] if row["container"] == "back"), None)
        chosen = arguments.get("item_refs")
        item = next((item for item in details["items"] if chosen and item["item_ref"] == chosen[0]), None)
        if (schema != 2 or len(chosen or []) != 1 or player is None or player["completeness"] != "complete"
                or back is None or back["completeness"] != "complete" or back["item_refs"]
                or item is None or item["container"] != "player" or not item["owned"]
                or arguments.get("collection_fingerprint") is None
                or customer["presence"] != "present" or customer["arrived"] is not True
                or customer["trade_type"] != "buy" or customer["phase"] != "trading"
                or details["quote"]["status"] != "unverified"
                or details["quote"]["mode"] != "unknown"
                or details["quote"]["displayed_total"] is not None):
            return False
    if action == "sort_inventory_default":
        customer = details["customer"]
        player = next((row for row in details["containers"] if row["container"] == "player"), None)
        if (schema != 2 or arguments.get("container") != "player" or player is None
                or player["completeness"] != "complete" or customer["dialogue_ref"] is not None
                or customer["phase"] == "dialogue"
                or customer["dialogue_stage"] in {"typing", "waiting", "choices", "updating"}
                or customer.get("is_typing") is True
                or _has_action_blocking_evidence(details["ui"]["blockers"], action)
                or details["ui"]["status"] in {"blocked", "updating"}):
            return False
    if action == "cancel_confirmation" and not details["ui"]["blockers"]:
        return False
    if action == "purchase_visible_offer":
        quote = details["quote"]
        if (schema != 2 or not details["eligibility"]["purchase_enabled"]
                or quote["status"] != "complete" or quote["mode"] != "normal"
                or arguments.get("customer_ref") != business["customer_ref"]
                or arguments.get("offer_ref") != business["offer_ref"]
                or arguments.get("collection_fingerprint") != business["collection_fingerprint"]
                or arguments.get("item_refs") != business["item_refs"]
                or arguments.get("mode") != "normal"
                or arguments.get("amount") != quote["displayed_total"]
                or arguments.get("amount", -1) > arguments.get("max_spend", -1)):
            return False
    if action == "sell_visible_offer":
        quote = details["quote"]
        customer = details["customer"]
        back = next((row for row in details["containers"] if row["container"] == "back"), None)
        if (schema != 2 or customer["presence"] != "present" or customer["arrived"] is not True
                or customer["trade_type"] != "buy" or customer["phase"] != "trading"
                or quote["status"] != "complete" or quote["mode"] != "normal"
                or arguments.get("customer_ref") != business["customer_ref"]
                or arguments.get("offer_ref") != business["offer_ref"]
                or arguments.get("collection_fingerprint") != business["collection_fingerprint"]
                or len(arguments.get("item_refs", [])) != 1
                or arguments.get("item_refs") != business["item_refs"]
                or arguments.get("mode") != "normal"
                or arguments.get("amount") != quote["displayed_total"]
                or back is None or back["completeness"] != "complete"
                or _has_action_blocking_evidence(details["ui"]["blockers"], action)
                or details["ui"]["status"] in {"blocked", "updating"}):
            return False
    return True


def valid_action_state_v3(snapshot, action, arguments):
    """Minimal per-action game-phase checks; unrelated business completeness is irrelevant."""
    details = snapshot["business"]["details"]
    day = details.get("day") if details["schema_version"] == 2 else None
    handoff = details.get("inspection_handoff")
    pending_handoff = (type(handoff) is dict and handoff["requires_manual_inspection"]
                       and handoff["status"] == "waiting_for_user")
    if pending_handoff:
        return (action == "resume_inspection_handoff" and snapshot["ready"]
                and arguments.get("handoff_ref") == handoff["handoff_ref"])
    if action == "resume_inspection_handoff":
        return False
    if (type(day) is dict and (day["transition_pending"] or day["report_open"]
                               or day["stage"] == "settlement")
            and action not in DAY_ACTIONS and action != "resume_inspection_handoff"):
        return False
    if action in DAY_ACTIONS:
        if (not snapshot["ready"] or snapshot["menu_open"] or type(day) is not dict
                or day["day_number"] is None
                or day["transition_pending"]
                or not valid_action_binding_v3(snapshot["business"], action, arguments)):
            return False
        if action == "close_business":
            return (snapshot["phase"] == "OPEN" and day["stage"] == "open"
                    and day["remaining_customers"] == 0
                    and not day["report_open"] and day["settlement"] is None
                    and snapshot["business"]["details"]["customer"]["presence"] == "absent")
        if action == "open_end_of_day":
            return (snapshot["phase"] == "CLOSED" and day["stage"] == "closed" and day["can_sleep"]
                    and not day["report_open"] and day["settlement"] is None)
        if action == "confirm_end_of_day":
            amount = arguments["amount"]
            return (snapshot["phase"] == "CLOSED" and day["stage"] == "settlement"
                    and day["can_confirm"] and not day["report_open"]
                    and (amount <= 0 or arguments["max_spend"] <= snapshot["cash"]))
        return (snapshot["phase"] == "MORNING" and day["stage"] in {"morning", "next_morning"}
                and day["report_open"] and day["settlement"] is None
                and snapshot["business"]["details"]["customer"]["presence"] == "absent")
    if action == "start_business":
        return snapshot["ready"] and snapshot["phase"] == "MORNING"
    if action in BARGAIN_ACTIONS:
        return snapshot["ready"] and valid_action_binding_v3(snapshot["business"], action, arguments)
    if action == "receive_next_customer":
        customer = snapshot["business"]["details"]["customer"]
        return (snapshot["ready"] and snapshot["phase"] == "OPEN"
                and customer["presence"] == "absent" and customer["phase"] == "absent")
    if action == "purchase_visible_offer":
        return (snapshot["business"]["details"]["eligibility"]["purchase_enabled"]
                and arguments.get("max_spend", MAX_INT + 1) <= snapshot["cash"]
                and arguments.get("amount", MAX_INT + 1) <= snapshot["cash"])
    if action in {"prepare_sale_items", "sell_visible_offer"}:
        return (snapshot["ready"] and snapshot["business"]["details"]["customer"]["arrived"] is True
                and snapshot["business"]["details"]["customer"]["trade_type"] == "buy"
                and snapshot["business"]["details"]["customer"]["phase"] == "trading")
    if action == "sort_inventory_default":
        ui = snapshot["business"]["details"]["ui"]
        return (snapshot["ready"] and not _has_action_blocking_evidence(ui["blockers"], action)
                and ui["status"] not in {"blocked", "updating"})
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
            and set(value["arguments"]) == V3_ACTION_ARGUMENTS[value["action"]]
            and (value["action"] != "advance_dialogue" or value["arguments"]["mode"] in {"skip", "next"})
            and (value["action"] not in {"purchase_visible_offer", "sell_visible_offer"} or value["arguments"].get("mode") == "normal")
            and (value["action"] not in BARGAIN_ACTIONS or value["arguments"].get("mode") in {"buy", "sell"})
            and (value["action"] != "sell_visible_offer" or len(value["arguments"].get("item_refs", [])) == 1)
            and (value["action"] != "confirm_end_of_day"
                 or value["arguments"].get("max_spend", -1) >= max(0, value["arguments"].get("amount", 0)))
            and (value["action"] != "resume_inspection_handoff"
                 or set(value["arguments"]) == {"handoff_ref"})
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
    return valid_action_success_evidence_v3(command, before, after)


def valid_success_evidence_v3(before, after):
    """Generic evidence shape for MCP history, which does not carry the action name."""
    return (valid_snapshot_v3(before) and valid_snapshot_v3(after)
            and before["world_epoch"] == after["world_epoch"]
            and after["revision"] > before["revision"])


def valid_action_success_evidence_v3(command, before, after):
    """Validate only the observable postcondition for the command's own operation."""
    action = command["action"]
    if action in {"close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"}:
        old = before["business"]["details"].get("day")
        new = after["business"]["details"].get("day")
        if (type(old) is not dict or type(new) is not dict
                or before["world_epoch"] != after["world_epoch"]
                or before["scene"] != after["scene"] or not after["ready"]
                or after["revision"] <= before["revision"]
                or not valid_action_binding_v3(before["business"], action, command["arguments"])
                or not valid_action_state_v3(before, action, command["arguments"])
                or new["day_number"] is None or new["transition_pending"]):
            return False
        if action == "confirm_end_of_day":
            return (old["stage"] == "settlement" and old["can_confirm"]
                    and old["settlement"] is not None
                    and old["settlement"]["settlement_ref"] == command["arguments"]["settlement_ref"]
                    and old["settlement"]["total"] == command["arguments"]["amount"]
                    and old["day_number"] is not None
                    and new["day_number"] == old["day_number"] + 1
                    and after["phase"] == "MORNING" and new["stage"] == "next_morning"
                    and not new["transition_pending"] and new["report_open"]
                    and new["settlement"] is None
                    and before["cash"] - after["cash"] == command["arguments"]["amount"])
        if (old["day_number"] is None or new["day_number"] != old["day_number"]
                or before["cash"] != after["cash"]):
            return False
        if action == "close_business":
            return (before["phase"] == "OPEN" and after["phase"] == "CLOSED"
                    and old["stage"] == "open" and new["stage"] == "closed"
                    and not new["report_open"] and new["settlement"] is None)
        if action == "open_end_of_day":
            return (before["phase"] == "CLOSED" and after["phase"] == "CLOSED"
                    and old["stage"] == "closed" and new["stage"] == "settlement"
                    and new["can_confirm"] and new["settlement"] is not None and not new["report_open"])
        return (before["phase"] == "MORNING" and after["phase"] == "MORNING"
                and old["stage"] in {"morning", "next_morning"} and old["report_open"]
                and new["stage"] in {"morning", "next_morning"} and not new["report_open"]
                and new["settlement"] is None)
    if action == "resume_inspection_handoff":
        old = before["business"]["details"].get("inspection_handoff")
        new = after["business"]["details"].get("inspection_handoff")
        return (type(old) is dict and type(new) is dict
                and old["requires_manual_inspection"] is True and old["status"] == "waiting_for_user"
                and old["handoff_ref"] == command["arguments"]["handoff_ref"]
                and type(new) is dict and new["last_resumed_handoff_ref"] == command["arguments"]["handoff_ref"]
                and before["world_epoch"] == after["world_epoch"]
                and after["revision"] > before["revision"])
    if action in BARGAIN_ACTIONS:
        args = command["arguments"]
        old_business, new_business = before["business"], after["business"]
        old = old_business["details"].get("bargain")
        new = new_business["details"].get("bargain")
        if (not valid_action_binding_v3(old_business, action, args) or old is None or new is None
                or before["world_epoch"] != after["world_epoch"] or before["scene"] != after["scene"]
                or after["revision"] <= before["revision"] or before["cash"] != after["cash"]
                or new_business["customer_ref"] != args["customer_ref"]
                or new_business["details"]["customer"]["presence"] != "present"
                or new_business["details"]["customer"]["arrived"] is not True
                or new_business["details"]["customer"]["trade_type"] not in (
                    {args["mode"], "other"} if action in {"choose_bargain_option", "close_bargain_panel"} else {args["mode"]})
                or new["direction"] != args["mode"]):
            return False
        if action == "open_bargain_panel":
            return old["phase"] == "closed" and new["phase"] == "choosing" and bool(new["options"])
        if action == "close_bargain_panel":
            return old["phase"] in {"choosing", "responding"} and new["phase"] == "closed"
        return (new["last_option_ref"] == args["option_ref"]
                and new["terms_revision"] > old["terms_revision"] and new["result"] != "none")
    if action == "open_trade_panel":
        old = before["business"]["details"]["customer"]
        new = after["business"]["details"]["customer"]
        quote = after["business"]["details"]["quote"]
        return (before["cash"] == after["cash"] and before["world_epoch"] == after["world_epoch"]
                and before["scene"] == after["scene"] and after["revision"] > before["revision"]
                and before["business"]["customer_ref"] == command["arguments"]["customer_ref"]
                and before["business"]["customer_ref"] == after["business"]["customer_ref"]
                and old["presence"] == "present" and old.get("trade_panel") == "closed"
                and old["trade_type"] in {"buy", "sell"} and new["trade_type"] == old["trade_type"]
                and new.get("trade_panel") == old["trade_type"] and new["presence"] == "present"
                and new["arrived"] is True and new["phase"] == "trading" and new["dialogue_ref"] is None
                and quote["mode"] == "normal" and quote["displayed_total"] is not None)
    if action == "close_menu":
        return valid_close_menu_evidence_v3(before, after)
    if action == "start_business":
        return before["phase"] == "MORNING" and after["phase"] == "OPEN"
    if action == "receive_next_customer":
        old = before["business"]["details"]["customer"]
        new = after["business"]["details"]["customer"]
        return (before["phase"] == "OPEN" and old["presence"] == "absent"
                and new["presence"] == "present" and new["arrived"] is True)
    if action in {"advance_dialogue", "choose_dialogue_option"}:
        old = before["business"]["details"]["customer"]
        new = after["business"]["details"]["customer"]
        if before["business"]["customer_ref"] != after["business"]["customer_ref"]:
            return False
        if old["dialogue_ref"] == new["dialogue_ref"]:
            return (old["dialogue_text"] != new["dialogue_text"]
                    or old["dialogue_stage"] != new["dialogue_stage"]
                    or old["option_refs"] != new["option_refs"])
        return new["presence"] == "present"
    if action == "prepare_purchase_items":
        old_front = next((r for r in before["business"]["details"]["containers"] if r["container"] == "front"), None)
        new_front = next((r for r in after["business"]["details"]["containers"] if r["container"] == "front"), None)
        old_back = next((r for r in before["business"]["details"]["containers"] if r["container"] == "back"), None)
        new_back = next((r for r in after["business"]["details"]["containers"] if r["container"] == "back"), None)
        return (old_front is not None and new_front is not None and old_back is not None and new_back is not None
                and len(new_front["item_refs"]) < len(old_front["item_refs"])
                and new_back["item_refs"] != old_back["item_refs"])
    if action == "prepare_sale_items":
        args = command["arguments"]
        old_items = before["business"]["details"]["items"]
        new_items = after["business"]["details"]["items"]
        ref = args["item_refs"][0]
        old_by_ref = {item["item_ref"]: item for item in old_items}
        new_by_ref = {item["item_ref"]: item for item in new_items}
        old_item, new_item = old_by_ref.get(ref), new_by_ref.get(ref)
        if (before["cash"] != after["cash"] or old_item is None or new_item is None
                or old_item["container"] != "player" or new_item["container"] != "back"
                or old_item["owned"] is not True or new_item["owned"] is not True
                or old_item["units"] != new_item["units"] or set(old_by_ref) != set(new_by_ref)):
            return False
        return all((new_by_ref[key]["container"], new_by_ref[key]["owned"], new_by_ref[key]["units"])
                   == (old_by_ref[key]["container"], old_by_ref[key]["owned"], old_by_ref[key]["units"])
                   for key in old_by_ref if key != ref)
    if action == "sort_inventory_default":
        old_items = before["business"]["details"]["items"]
        new_items = after["business"]["details"]["items"]
        old_by_ref = {item["item_ref"]: item for item in old_items}
        new_by_ref = {item["item_ref"]: item for item in new_items}
        return (before["cash"] == after["cash"] and set(old_by_ref) == set(new_by_ref)
                and all((new_by_ref[key]["container"], new_by_ref[key]["owned"], new_by_ref[key]["units"])
                        == (old_by_ref[key]["container"], old_by_ref[key]["owned"], old_by_ref[key]["units"])
                        for key in old_by_ref))
    if action == "dismiss_customer":
        old = before["business"]["details"]["customer"]
        new = after["business"]["details"]["customer"]
        old_ref = before["business"]["customer_ref"]
        new_ref = after["business"]["customer_ref"]
        return (old["presence"] == "present" and old_ref is not None
                and ((new["presence"] == "absent" and new_ref is None)
                     or (new["presence"] == "present" and new_ref is not None
                         and new_ref != old_ref)))
    if action == "cancel_confirmation":
        old = before["business"]["details"]["ui"]["blockers"]
        new = after["business"]["details"]["ui"]["blockers"]
        return len(new) < len(old)
    if action == "purchase_visible_offer":
        args = command["arguments"]
        old = before["business"]
        new = after["business"]
        purchased = set(args["item_refs"])
        old_items = old["details"]["items"]
        new_items = new["details"]["items"]
        expected_units = sum(item["units"] for item in old_items
                             if item["item_ref"] in purchased and item["container"] == "back" and not item["owned"])
        old_player_units = sum(item["units"] for item in old_items if item["container"] == "player")
        new_player_units = sum(item["units"] for item in new_items if item["container"] == "player")
        old_purchase_units = sum(item["units"] for item in old_items
                                 if item["container"] == "back" and not item["owned"])
        new_purchase_units = sum(item["units"] for item in new_items
                                 if item["container"] == "back" and not item["owned"])
        return (before["cash"] - after["cash"] == args["amount"]
                and expected_units > 0
                and new_player_units - old_player_units == expected_units
                and old_purchase_units - new_purchase_units == expected_units)
    if action == "sell_visible_offer":
        args = command["arguments"]
        old_items = before["business"]["details"]["items"]
        new_items = after["business"]["details"]["items"]
        sold = set(args["item_refs"])
        old_by_ref = {item["item_ref"]: item for item in old_items}
        new_by_ref = {item["item_ref"]: item for item in new_items}
        expected_units = sum(item["units"] for item in old_items
                             if item["item_ref"] in sold and item["container"] == "back" and item["owned"])
        removed_units = sum(item["units"] for item in old_items
                            if item["item_ref"] in sold and item["container"] == "back" and item["item_ref"] not in new_by_ref)
        retained = all(ref in new_by_ref and
                       (new_by_ref[ref]["container"], new_by_ref[ref]["owned"], new_by_ref[ref]["units"])
                       == (item["container"], item["owned"], item["units"])
                       for ref, item in old_by_ref.items() if ref not in sold)
        return (after["cash"] - before["cash"] == args["amount"] and expected_units > 0
                and removed_units == expected_units and retained)
    return False


def valid_close_menu_evidence_v3(before, after):
    return (valid_snapshot_v3(before) and valid_snapshot_v3(after)
            and before["ready"] and before["can_close_menu"] and after["ready"]
            and after["world_epoch"] == before["world_epoch"] and after["scene"] == before["scene"]
            and not after["menu_open"] and after["revision"] > before["revision"]
            and after["cash"] == before["cash"])


def _v4_ref(value, nullable=False):
    return (value is None if nullable else False) or (type(value) is str and REF.fullmatch(value) is not None)


def valid_surface_v4(surface):
    """Validate the standalone, bounded v4 interaction observation."""
    keys = {"schema_version", "surface_ref", "kind", "phase", "status", "completeness",
            "reason", "text", "customer_ref", "dialogue_ref", "controls"}
    if (not shape(surface, keys) or type(surface["schema_version"]) is not int
            or surface["schema_version"] != 1
            or not _v4_ref(surface["surface_ref"], nullable=True)
            or type(surface["kind"]) is not str or surface["kind"] not in SURFACE_KINDS
            or type(surface["phase"]) is not str or surface["phase"] not in SURFACE_PHASES
            or type(surface["status"]) is not str or surface["status"] not in SURFACE_STATUSES
            or type(surface["completeness"]) is not str
            or surface["completeness"] not in {"complete", "partial", "unavailable"}
            or not _v3_text(surface["reason"])
            or not _v3_text(surface["text"], nullable=True)
            or not _v4_ref(surface["customer_ref"], nullable=True)
            or not _v4_ref(surface["dialogue_ref"], nullable=True)
            or type(surface["controls"]) is not list or len(surface["controls"]) > 16):
        return False
    seen = set()
    for control in surface["controls"]:
        if (not shape(control, {"control_ref", "action", "mode", "option_ref", "text", "enabled",
                                "effect_contract", "effects", "reason"})
                or not _v4_ref(control["control_ref"])
                or type(control["action"]) is not str or control["action"] not in SURFACE_CONTROL_ACTIONS
                or (control["mode"] is not None and (type(control["mode"]) is not str
                    or control["mode"] not in {"skip", "next"}))
                or not _v4_ref(control["option_ref"], nullable=True)
                or not _v3_text(control["text"], nullable=True)
                or type(control["enabled"]) is not bool
                or type(control["effect_contract"]) is not str
                or control["effect_contract"] not in SURFACE_EFFECT_CONTRACTS
                or type(control["effects"]) is not list
                or len(control["effects"]) > len(INTERACTION_EFFECTS)
                or any(type(effect) is not str or effect not in INTERACTION_EFFECTS for effect in control["effects"])
                or len(control["effects"]) != len(set(control["effects"]))
                or not _v3_text(control["reason"])):
            return False
        if control["control_ref"] in seen:
            return False
        seen.add(control["control_ref"])
        if control["action"] == "advance_dialogue":
            if control["mode"] not in {"skip", "next"} or control["option_ref"] is not None:
                return False
        elif control["action"] == "choose_dialogue_option":
            if control["mode"] is not None or control["option_ref"] is None:
                return False
        elif control["mode"] is not None or control["option_ref"] is not None:
            return False
    return True


def _v4_is_generic_variant(action, arguments):
    return type(action) is str and action in GENERIC_INTERACTION_ACTIONS and type(arguments) is dict and (
        "surface_ref" in arguments or "control_ref" in arguments)


def _v4_legacy_arguments(arguments):
    return {key: value for key, value in arguments.items() if key not in {"surface_ref", "control_ref"}}


def valid_arguments_v4(value):
    if type(value) is not dict or not set(value) <= (V3_OPTIONAL_ARGUMENTS | {"surface_ref", "control_ref"}):
        return False
    return (valid_arguments_v3(_v4_legacy_arguments(value))
            and all(_v4_ref(value[key]) for key in ("surface_ref", "control_ref") if key in value))


def valid_request_v4(value):
    return (shape(value, REQUEST_KEYS) and type(value["schema_version"]) is int
            and value["schema_version"] == WIRE_VERSION_V4
            and all(string(value[k]) for k in ("id", "broker_epoch", "action", "expected_world_epoch"))
            and integer(value["expected_revision"])
            and string(value["expected_version"], 256)
            and value["expected_version"] == f"{value['expected_world_epoch']}:{value['expected_revision']}"
            and valid_arguments_v4(value["arguments"]))


def _v4_command_as_v3(command):
    return dict(command, schema_version=WIRE_VERSION_V3,
                arguments=_v4_legacy_arguments(command["arguments"]))


def valid_command_v4(value):
    if not shape(value, COMMAND_KEYS) or not valid_request_v4({k: value[k] for k in REQUEST_KEYS}):
        return False
    if not integer(value["start_deadline_unix_ms"], 1, MAX_TIME) or not integer(value["execution_timeout_ms"], 1, 30_000):
        return False
    action, arguments = value["action"], value["arguments"]
    if _v4_is_generic_variant(action, arguments):
        return (set(arguments) == V4_GENERIC_ACTION_ARGUMENTS[action]
                and valid_command_v3(_v4_command_as_v3(value)))
    if "surface_ref" in arguments or "control_ref" in arguments:
        return False
    return valid_command_v3(_v4_command_as_v3(value))


def _v4_shadow_snapshot(snapshot):
    """Project v4 onto the frozen v3 view for legacy-action validation."""
    return {**{key: snapshot[key] for key in SNAPSHOT_KEYS},
            "schema_version": WIRE_VERSION_V3,
            "legal_actions": [
                {"action": entry["action"], "arguments": entry["arguments"]}
                for entry in snapshot["legal_actions"]
                if not _v4_is_generic_variant(entry.get("action"), entry.get("arguments"))
            ]}


def _v4_surface_control(snapshot, arguments):
    surface = snapshot.get("surface") if type(snapshot) is dict else None
    if (not valid_surface_v4(surface) or arguments.get("surface_ref") != surface["surface_ref"]
            or surface["surface_ref"] is None):
        return None
    return next((control for control in surface["controls"]
                 if control["control_ref"] == arguments.get("control_ref")), None)


def valid_generic_action_binding_v4(snapshot, action, arguments):
    if (type(action) is not str or action not in GENERIC_INTERACTION_ACTIONS or type(arguments) is not dict
            or set(arguments) != V4_GENERIC_ACTION_ARGUMENTS[action] or not valid_arguments_v4(arguments)):
        return False
    surface = snapshot.get("surface") if type(snapshot) is dict else None
    control = _v4_surface_control(snapshot, arguments)
    if (control is None or control["action"] != action or not control["enabled"]
            or control["effect_contract"] == "unknown"
            or arguments["customer_ref"] != surface["customer_ref"]
            or arguments["customer_ref"] != snapshot["business"].get("customer_ref")):
        return False
    if action == "advance_dialogue":
        return (surface["kind"] == "dialogue" and surface["dialogue_ref"] is not None
                and arguments["dialogue_ref"] == surface["dialogue_ref"]
                and control["mode"] == arguments["mode"] and control["option_ref"] is None
                and ((arguments["mode"] == "skip" and surface["phase"] == "typing")
                     or (arguments["mode"] == "next" and surface["phase"] == "waiting_for_advance"))
                and control["effect_contract"] in {"presentation", "document_gift"})
    if action == "choose_dialogue_option":
        return (surface["kind"] == "dialogue" and surface["phase"] == "awaiting_choice"
                and surface["dialogue_ref"] is not None
                and arguments["dialogue_ref"] == surface["dialogue_ref"]
                and control["mode"] is None and control["option_ref"] == arguments["option_ref"]
                and control["effect_contract"] in {"presentation", "document_gift"})
    return (surface["kind"] == "customer_completion" and surface["phase"] == "completed"
            and control["mode"] is None and control["option_ref"] is None
            and control["effect_contract"] == "customer_departure")


def valid_generic_action_state_v4(snapshot, action, arguments):
    if (type(action) is not str or action not in GENERIC_INTERACTION_ACTIONS
            or not snapshot["loaded"] or not snapshot["ready"] or snapshot["phase"] != "OPEN"
            or snapshot["menu_open"] or snapshot["surface"]["completeness"] != "complete"
            or snapshot["surface"]["status"] != "awaiting_agent"):
        return False
    details = snapshot["business"]["details"]
    customer = details["customer"]
    if (details["schema_version"] != 2 or customer["presence"] != "present"
            or customer["arrived"] is not True):
        return False
    day = details.get("day")
    if type(day) is dict and (day["transition_pending"] or day["report_open"] or day["stage"] == "settlement"):
        return False
    handoff = details.get("inspection_handoff")
    if (type(handoff) is dict and handoff["requires_manual_inspection"]
            and handoff["status"] == "waiting_for_user"):
        return False
    ui = details["ui"]
    return ui["status"] != "updating" and not _has_action_blocking_evidence(ui["blockers"], action)


def valid_action_binding_v4(snapshot, action, arguments):
    if _v4_is_generic_variant(action, arguments):
        return valid_generic_action_binding_v4(snapshot, action, arguments)
    if type(arguments) is not dict or "surface_ref" in arguments or "control_ref" in arguments:
        return False
    return valid_action_binding_v3(snapshot["business"], action, arguments)


def valid_action_state_v4(snapshot, action, arguments):
    if _v4_is_generic_variant(action, arguments):
        return valid_generic_action_state_v4(snapshot, action, arguments)
    if type(arguments) is not dict or "surface_ref" in arguments or "control_ref" in arguments:
        return False
    return valid_action_state_v3(_v4_shadow_snapshot(snapshot), action, arguments)


def _v4_pending_handoff(snapshot):
    handoff = snapshot["business"]["details"].get("inspection_handoff")
    return (type(handoff) is dict and handoff["requires_manual_inspection"] is True
            and handoff["status"] == "waiting_for_user")


def _valid_surface_status_v4(snapshot):
    surface, actions = snapshot["surface"], snapshot["legal_actions"]
    if _v4_pending_handoff(snapshot):
        return surface["status"] == "awaiting_user"
    if actions:
        return surface["status"] == "awaiting_agent"
    if surface["status"] in {"awaiting_agent", "awaiting_user"}:
        return False
    if surface["phase"] in {"unknown", "unsupported"}:
        return surface["status"] == surface["phase"]
    if surface["phase"] == "absent" or not surface["controls"]:
        return surface["status"] == "idle"
    if surface["phase"] in {"progressing", "typing"}:
        return surface["status"] == "progressing"
    if surface["phase"] == "completed":
        return surface["status"] == "unsupported"
    return surface["status"] in {"idle", "progressing", "unsupported", "unknown"}


def _valid_surface_business_coherence_v4(snapshot):
    surface = snapshot["surface"]
    business = snapshot["business"]
    customer = business["details"]["customer"]
    if surface["phase"] == "absent" and surface["kind"] != "none":
        return False
    if not snapshot["ready"]:
        return True
    if surface["kind"] == "none" and customer["presence"] == "present":
        return False
    if surface["completeness"] != "complete":
        return True
    if surface["customer_ref"] != business["customer_ref"]:
        return False
    if surface["kind"] == "dialogue":
        return (customer["presence"] == "present" and surface["dialogue_ref"] is not None
                and surface["dialogue_ref"] == customer["dialogue_ref"])
    if surface["kind"] == "customer_completion":
        return (customer["presence"] == "present" and surface["dialogue_ref"] is None
                and customer["dialogue_ref"] is None)
    return True


def valid_snapshot_v4(value):
    if (not shape(value, SURFACE_LEGACY_SNAPSHOT_KEYS)
            or type(value.get("schema_version")) is not int
            or value["schema_version"] != WIRE_VERSION_V4
            or not valid_surface_v4(value["surface"])):
        return False
    if (type(value["legal_actions"]) is not list or len(value["legal_actions"]) > 64
            or any(type(entry) is not dict or set(entry) != {"action", "arguments"}
                   or type(entry.get("action")) is not str or type(entry.get("arguments")) is not dict
                   for entry in value["legal_actions"])):
        return False
    shadow = _v4_shadow_snapshot(value)
    if not valid_snapshot_v3(shadow):
        return False
    seen = set()
    for entry in value["legal_actions"]:
        if type(entry) is not dict or set(entry) != {"action", "arguments"}:
            return False
        action, arguments = entry["action"], entry["arguments"]
        if _v4_is_generic_variant(action, arguments):
            if (not valid_generic_action_binding_v4(value, action, arguments)
                    or not valid_generic_action_state_v4(value, action, arguments)):
                return False
            semantic_args = _v4_legacy_arguments(arguments)
        else:
            semantic_args = arguments
        identity = (action, json.dumps(semantic_args, sort_keys=True, separators=(",", ":"),
                                       ensure_ascii=False))
        if identity in seen:
            return False
        seen.add(identity)
    size = compact_json_size(value)
    return (_valid_surface_status_v4(value) and _valid_surface_business_coherence_v4(value)
            and size is not None and size <= MAX_V4_SNAPSHOT_BYTES)


def valid_interaction_result_v4(value):
    if (not shape(value, INTERACTION_RESULT_KEYS)
            or type(value["input_receipt"]) is not str
            or value["input_receipt"] not in {"not_invoked", "returned", "unknown"}
            or type(value["verification"]) is not str
            or value["verification"] not in {"confirmed", "unknown"}
            or type(value["effect_contract"]) is not str
            or value["effect_contract"] not in SURFACE_EFFECT_CONTRACTS
            or type(value["observed_effects"]) is not list
            or type(value["unverified_effects"]) is not list
            or any(type(effect) is not str or effect not in INTERACTION_EFFECTS
                   for effect in value["observed_effects"] + value["unverified_effects"])
            or len(value["observed_effects"]) > len(INTERACTION_EFFECTS)
            or len(value["unverified_effects"]) > len(INTERACTION_EFFECTS)
            or len(value["observed_effects"]) != len(set(value["observed_effects"]))
            or len(value["unverified_effects"]) != len(set(value["unverified_effects"]))
            or set(value["observed_effects"]) & set(value["unverified_effects"])
            or value["cash_delta"] is not None
            and not integer(value["cash_delta"], -2_147_483_648, MAX_INT)
            or not _v3_refs(value["added_item_refs"], maximum=64)
            or not _v3_refs(value["removed_item_refs"], maximum=64)
            or set(value["added_item_refs"]) & set(value["removed_item_refs"])):
        return False
    if value["verification"] == "confirmed" and value["input_receipt"] != "returned":
        return False
    if value["input_receipt"] in {"not_invoked", "unknown"} and value["verification"] != "unknown":
        return False
    return True


def valid_result_structure_v4(value, require_execution=True, command=None):
    keys = set(value) if type(value) is dict else set()
    if keys not in (RESULT_KEYS, RESULT_KEYS_V4):
        return False
    if (type(value["schema_version"]) is not int or value["schema_version"] != WIRE_VERSION_V4
            or not string(value["id"]) or not string(value["broker_epoch"])
            or type(value["status"]) is not str or value["status"] not in TERMINAL
            or not string(value["reason"], 512)
            or any(v is not None and not valid_snapshot_v4(v) for v in (value["before"], value["after"]))):
        return False
    generic = command is not None and _v4_is_generic_variant(command.get("action"), command.get("arguments"))
    if "interaction_result" in value and value["interaction_result"] is None:
        return False
    interaction_result = value.get("interaction_result")
    if interaction_result is not None:
        if not valid_interaction_result_v4(interaction_result) or (command is not None and not generic):
            return False
        if generic and value["status"] == "succeeded" and (
                interaction_result["input_receipt"] != "returned"
                or interaction_result["verification"] != "confirmed"):
            return False
        if generic and value["status"] == "rejected" and interaction_result["input_receipt"] != "not_invoked":
            return False
        if generic and value["status"] == "rejected" and (
                interaction_result["verification"] != "unknown"
                or interaction_result["observed_effects"] or interaction_result["unverified_effects"]
                or interaction_result["cash_delta"] is not None
                or interaction_result["added_item_refs"] or interaction_result["removed_item_refs"]):
            return False
        if generic and value["status"] == "outcome_unknown" and (
                interaction_result["input_receipt"] == "not_invoked"
                or interaction_result["verification"] != "unknown"):
            return False
        if (generic and interaction_result["input_receipt"] != "not_invoked"
                and not _valid_generic_receipt_evidence(command, value, interaction_result)):
            return False
    elif generic and value["status"] in {"succeeded", "outcome_unknown"}:
        return False
    progress = value["execution"]
    if progress is None:
        return not require_execution and value["status"] != "succeeded"
    if not (valid_progress(progress) and progress["id"] == value["id"]
            and progress["broker_epoch"] == value["broker_epoch"]):
        return False
    if value["status"] == "rejected":
        return progress["stage"] == "completed" and not progress["may_have_side_effects"]
    if value["status"] == "outcome_unknown":
        return progress["stage"] == "uncertain" and progress["may_have_side_effects"]
    return (progress["stage"] == "completed" and progress["may_have_side_effects"]
            and valid_snapshot_v4(value["before"]) and valid_snapshot_v4(value["after"]))


def _v4_inventory_facts(snapshot):
    details = snapshot["business"]["details"]
    containers = details["containers"]
    by_name = {row["container"]: row for row in containers}
    if set(by_name) != {"front", "back", "player"} or any(
            by_name[name]["completeness"] != "complete" for name in ("front", "back", "player")):
        return None
    facts = {}
    for item in details["items"]:
        ref = item["item_ref"]
        if ref in facts:
            return None
        facts[ref] = (item["container"], item["units"], item["owned"])
    for name, row in by_name.items():
        if any(ref not in facts or facts[ref][0] != name for ref in row["item_refs"]):
            return None
    if {ref for row in containers for ref in row["item_refs"]} != set(facts):
        return None
    return facts


def _valid_generic_receipt_evidence(command, result, receipt):
    """Bind every invoked receipt to its selected control and reported DTO deltas."""
    before, after = result.get("before"), result.get("after")
    if before is None:
        return (receipt["effect_contract"] == "unknown" and not receipt["observed_effects"]
                and not receipt["unverified_effects"] and receipt["cash_delta"] is None
                and not receipt["added_item_refs"] and not receipt["removed_item_refs"])
    action, arguments = command["action"], command["arguments"]
    if (before["world_epoch"] != command["expected_world_epoch"]
            or before["revision"] != command["expected_revision"]
            or before["version"] != command["expected_version"]
            or not any(entry["action"] == action and entry["arguments"] == arguments
                       for entry in before["legal_actions"])
            or not valid_generic_action_binding_v4(before, action, arguments)
            or not valid_generic_action_state_v4(before, action, arguments)):
        return False
    control = _v4_surface_control(before, arguments)
    declared, observed = set(control["effects"]), set(receipt["observed_effects"])
    if (receipt["effect_contract"] != control["effect_contract"]
            or receipt["effect_contract"] == "unknown"
            or not observed <= declared
            or set(receipt["unverified_effects"]) != declared - observed):
        return False
    if after is None:
        return (not receipt["observed_effects"] and receipt["cash_delta"] is None
                and not receipt["added_item_refs"]
                and not receipt["removed_item_refs"])
    same_world = (after["world_epoch"] == before["world_epoch"]
                  and after["scene"] == before["scene"])
    cash_difference = after["cash"] - before["cash"]
    safe_cash_delta = (cash_difference if same_world and -2_147_483_648 <= cash_difference <= MAX_INT else None)
    if receipt["cash_delta"] != safe_cash_delta:
        return False
    before_items, after_items = _v4_inventory_facts(before), _v4_inventory_facts(after)
    if same_world and before_items is not None and after_items is not None:
        added, removed = set(after_items) - set(before_items), set(before_items) - set(after_items)
        if (set(receipt["added_item_refs"]) != added
                or set(receipt["removed_item_refs"]) != removed):
            return False
    else:
        if receipt["added_item_refs"] or receipt["removed_item_refs"]:
            return False

    # Unknown receipts are observations, not assertions. Accept only effects that the
    # snapshots themselves prove; story/reputation/day/external effects have no DTO proof.
    if receipt["verification"] == "unknown":
        provable = set()
        same_scene = (after["world_epoch"] == before["world_epoch"]
                      and after["scene"] == before["scene"]
                      and after["revision"] > before["revision"])
        if same_world and same_scene and _v4_dialogue_progress(command, before, after):
            provable.update({"presentation", "dialogue_transition"})
        old_customer = before["business"]["customer_ref"]
        new_customer_ref = after["business"]["customer_ref"]
        new_customer = after["business"]["details"]["customer"]
        customer_departed = ((new_customer_ref is None and new_customer["presence"] == "absent")
                             or (new_customer_ref is not None and new_customer_ref != old_customer
                                 and new_customer["presence"] == "present"))
        if same_world and customer_departed:
            provable.add("customer_departure")
        if (same_world and before_items is not None and after_items is not None
                and before_items != after_items):
            provable.add("inventory_transfer")
        if same_world and before["cash"] != after["cash"]:
            provable.add("cash")
        if not set(receipt["observed_effects"]) <= provable:
            return False
    return True


def _v4_dialogue_progress(command, before, after):
    old, new = before["surface"], after["surface"]
    old_dialogue, new_dialogue = old["dialogue_ref"], new["dialogue_ref"]
    old_options = {c["option_ref"] for c in old["controls"] if c["action"] == "choose_dialogue_option"}
    new_options = {c["option_ref"] for c in new["controls"] if c["action"] == "choose_dialogue_option"}
    return (old_dialogue != new_dialogue or new["phase"] == "completed" or old_options != new_options
            or command["action"] == "advance_dialogue" and command["arguments"]["mode"] == "skip"
            and old["phase"] == "typing" and new["phase"] != "typing")


def _valid_generic_success_v4(command, result):
    before, after = result["before"], result["after"]
    receipt = result.get("interaction_result")
    if (not valid_interaction_result_v4(receipt) or receipt["input_receipt"] != "returned"
            or receipt["verification"] != "confirmed" or before is None or after is None
            or not _valid_generic_receipt_evidence(command, result, receipt)
            or before["world_epoch"] != command["expected_world_epoch"]
            or before["revision"] != command["expected_revision"]
            or before["version"] != command["expected_version"]
            or not before["ready"] or not after["ready"]
            or before["world_epoch"] != after["world_epoch"] or before["scene"] != after["scene"]
            or after["revision"] <= before["revision"] or before["cash"] != after["cash"]
            or receipt["cash_delta"] != 0
            or not valid_generic_action_binding_v4(before, command["action"], command["arguments"])):
        return False
    control = _v4_surface_control(before, command["arguments"])
    contract = control["effect_contract"]
    observed = set(receipt["observed_effects"])
    unverified = set(receipt["unverified_effects"])
    declared = set(control["effects"])
    if (receipt["effect_contract"] != contract or contract == "unknown"
            or observed & unverified or observed | unverified != declared
            or "cash" in receipt["observed_effects"]):
        return False
    before_items, after_items = _v4_inventory_facts(before), _v4_inventory_facts(after)
    if before_items is None or after_items is None:
        return False
    before_refs, after_refs = set(before_items), set(after_items)
    added, removed = after_refs - before_refs, before_refs - after_refs
    if set(receipt["added_item_refs"]) != added or set(receipt["removed_item_refs"]) != removed:
        return False
    if any(before_items[ref] != after_items[ref] for ref in before_refs & after_refs) or removed:
        return False
    if contract == "document_gift":
        if (len(added) > 1 or any(after_items[ref][0] != "front" or not after_items[ref][2] for ref in added)
                or (added and "inventory_transfer" not in receipt["observed_effects"])
                or (not added and "inventory_transfer" in receipt["observed_effects"])
                or "story_state" not in receipt["unverified_effects"]
                or not _v4_dialogue_progress(command, before, after)):
            return False
    elif contract == "presentation":
        if (added or receipt["removed_item_refs"]
                or not _v4_dialogue_progress(command, before, after)
                or not ({"presentation", "dialogue_transition"} & set(receipt["observed_effects"]))):
            return False
    elif contract == "customer_departure":
        old_ref = before["business"]["customer_ref"]
        new_customer = after["business"]["details"]["customer"]
        if (command["action"] != "dismiss_customer"
                or (new_customer["presence"] != "absent" and after["business"]["customer_ref"] == old_ref)
                or added or receipt["removed_item_refs"]
                or "customer_departure" not in receipt["observed_effects"]):
            return False
    else:
        return False
    return True


def valid_success_result_v4(command, result):
    if not valid_command_v4(command) or not valid_result_structure_v4(result, command=command):
        return False
    if result["status"] != "succeeded":
        return True
    before, after = result["before"], result["after"]
    if (before["world_epoch"] != command["expected_world_epoch"]
            or before["revision"] != command["expected_revision"]
            or before["version"] != command["expected_version"]):
        return False
    if _v4_is_generic_variant(command["action"], command["arguments"]):
        return _valid_generic_success_v4(command, result)
    legacy_command = _v4_command_as_v3(command)
    legacy_result = {key: value for key, value in result.items() if key in RESULT_KEYS}
    legacy_result.update(schema_version=WIRE_VERSION_V3,
                         before=_v4_shadow_snapshot(before), after=_v4_shadow_snapshot(after))
    return valid_success_result_v3(legacy_command, legacy_result)


def valid_success_evidence_v4(before, after, interaction_result):
    """Verify finite generic receipts in MCP history, which lacks the command envelope."""
    if not valid_interaction_result_v4(interaction_result):
        return False
    if not valid_snapshot_v4(before) or not valid_snapshot_v4(after):
        return False
    for legal in before["legal_actions"]:
        if (_v4_is_generic_variant(legal["action"], legal["arguments"])
                and _valid_generic_success_v4(
                    {"action": legal["action"], "arguments": legal["arguments"],
                     "expected_world_epoch": before["world_epoch"], "expected_revision": before["revision"],
                     "expected_version": before["version"]},
                    {"before": before, "after": after, "interaction_result": interaction_result})):
            return True
    return False


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
