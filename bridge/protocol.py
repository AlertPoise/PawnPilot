"""Strict wire-v2 shapes shared by broker, journal, fixtures and adapter checks.

No game rules beyond the sole supported close_menu evidence predicate live here.
Arguments describe future collections but never enable additional actions.
"""
from __future__ import annotations

import hashlib
import json
import re

WIRE_VERSION = 2
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


def integer(value, minimum=0, maximum=MAX_INT):
    return type(value) is int and minimum <= value <= maximum


def string(value, maximum=128, minimum=1):
    return type(value) is str and minimum <= len(value) <= maximum


def nullable_string(value, maximum=256):
    return value is None or string(value, maximum)


def shape(value, keys):
    return type(value) is dict and set(value) == keys


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
    if not valid_request(value):
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
