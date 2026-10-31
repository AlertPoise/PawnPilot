"""MCP stdio tools that adapt the local PawnPilot broker."""

from __future__ import annotations

import json
import os
import asyncio
from pathlib import Path
from typing import Annotated, Literal
from urllib.error import URLError

from mcp.server import MCPServer
from mcp.types import ToolAnnotations
from pydantic import BaseModel, ConfigDict, Field, ValidationError, model_validator

from bridge.client import ResponseError, request

SCHEMA_VERSION = 1
DEFAULT_CONFIG = Path("artifacts/session.json")


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class GameState(StrictModel):
    schema_version: Literal[1]
    version: str = Field(min_length=1, max_length=256)
    loaded: bool
    ready: bool
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0)
    phase: str = Field(max_length=128)
    scene: str = Field(max_length=128)
    menu_open: bool
    can_close_menu: bool
    cash: int | None = None
    not_ready_reason: str | None = None

    @model_validator(mode="after")
    def validate_version_tuple(self):
        if self.version != f"{self.world_epoch}:{self.revision}":
            raise ValueError("version_must_match_world_epoch_and_revision")
        if self.ready and (not self.loaded or self.not_ready_reason is not None):
            raise ValueError("ready_state_inconsistent")
        if self.can_close_menu and (not self.ready or not self.menu_open):
            raise ValueError("menu_action_precondition_inconsistent")
        return self


class Snapshot(StrictModel):
    schema_version: Literal[1]
    broker_epoch: str = Field(min_length=1, max_length=128)
    session_id: str | None = None
    connected: bool
    state: GameState | None
    blocking_reason: str | None = None
    blocked_command_id: str | None = None
    can_act: bool


class ToolReply(StrictModel):
    ok: bool
    error_source: Literal["none", "broker", "protocol", "connection", "configuration", "internal"]
    error: str | None = None


class HealthReply(ToolReply):
    schema_version: int | None = None
    protocol_compatible: bool | None
    broker_connected: bool
    bridge_connected: bool
    loaded: bool
    ready: bool
    can_act: bool
    broker_epoch: str | None = None
    world_epoch: str | None = None
    revision: int | None = None
    version: str | None = None
    phase: str | None = None
    blocking_reason: str | None = None
    blocked_command_id: str | None = None


class ObserveReply(ToolReply):
    broker_connected: bool
    bridge_connected: bool
    snapshot: Snapshot | None = None
    legal_actions: list[Literal["close_menu"]] = Field(default_factory=list)
    wait_reason: str | None = None
    manual_action_required: bool = False


class ActReply(ToolReply):
    command_id: str
    command_status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"]
    broker_epoch: str
    world_epoch: str
    revision: int
    version: str
    reason: str | None = None
    result: "ActionEvidence | None" = None


class ActionEvidence(StrictModel):
    schema_version: Literal[1] | None = None
    id: str
    broker_epoch: str
    status: Literal["success", "succeeded", "rejected", "uncertain", "outcome_unknown", "error", "failed"]
    command_status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"] | None = None
    reason: str = ""
    before: GameState | None = None
    after: GameState | None = None


class ResolutionEvidence(StrictModel):
    schema_version: Literal[1]
    method: Literal["manual_state_alignment"]
    command_id: str
    broker_epoch: str
    command_expires_at_unix_ms: int
    session_id: str
    world_epoch: str
    revision: int
    version: str
    snapshot: GameState
    operator_note: str
    confirm_no_replay: Literal[True]
    recorded_at_unix_ms: int


class ActionStatusReply(ToolReply):
    command_id: str
    found: bool | None
    command_status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown", "not_found"] | None
    broker_epoch: str | None = None
    reason: str | None = None
    result: ActionEvidence | None = None
    resolved: bool = False
    resolution: ResolutionEvidence | None = None


class AdapterError(Exception):
    def __init__(self, source: str, message: str):
        super().__init__(message)
        self.source = source


def _config_path() -> Path:
    # Test harnesses may point at an isolated token file. The broker endpoint and
    # production port remain owned by bridge.client (127.0.0.1:18765).
    return Path(os.environ.get("PAWNPILOT_CONFIG", str(DEFAULT_CONFIG)))


def _load_config() -> dict:
    path = _config_path()
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise AdapterError("configuration", "broker_config_not_found") from exc
    except (OSError, json.JSONDecodeError) as exc:
        raise AdapterError("configuration", "broker_config_unreadable") from exc
    if (not isinstance(value, dict) or value.get("port") != 18765
            or not isinstance(value.get("token"), str) or len(value["token"]) != 64
            or any(c not in "0123456789abcdef" for c in value["token"])):
        raise AdapterError("configuration", "broker_config_invalid")
    return value


def _call(path: str, body: dict | None = None) -> tuple[int, dict]:
    config = _load_config()
    try:
        status, payload = request(config, path, body)
    except ResponseError as exc:
        raise AdapterError("protocol", str(exc)) from exc
    except (TimeoutError, OSError, URLError) as exc:
        # A timed-out command may already have run. Never submit it again here.
        raise AdapterError("connection", "broker_request_timed_out_or_unreachable") from exc
    except Exception as exc:
        raise AdapterError("internal", "broker_request_internal_error") from exc
    if not isinstance(payload, dict):
        raise AdapterError("protocol", "broker_response_must_be_object")
    return status, payload


def _snapshot() -> Snapshot:
    status, payload = _call("/state")
    if status != 200:
        raise AdapterError("broker", str(payload.get("error", f"http_{status}")))
    try:
        return Snapshot.model_validate(payload)
    except ValidationError as exc:
        raise AdapterError("protocol", "broker_state_schema_invalid") from exc


def _reply_error(source: str, message: str, **values):
    return {"ok": False, "error_source": source, "error": message, **values}


mcp = MCPServer("PawnPilot")


@mcp.tool(annotations=ToolAnnotations(read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False))
def health_check() -> HealthReply:
    """Check broker and game bridge connectivity, readiness, and protocol compatibility."""
    try:
        snap = _snapshot()
    except AdapterError as exc:
        return HealthReply(ok=False, error_source=exc.source, error=str(exc),
                           protocol_compatible=False if exc.source == "protocol" else None,
                           broker_connected=exc.source in {"broker", "protocol"},
                           bridge_connected=False, loaded=False, ready=False, can_act=False)
    state = snap.state
    return HealthReply(ok=True, error_source="none", protocol_compatible=True,
                       broker_connected=True, bridge_connected=snap.connected,
                       loaded=bool(state and state.loaded), ready=bool(state and state.ready),
                       can_act=bool(snap.connected and state and state.ready and snap.can_act and state.can_close_menu), schema_version=snap.schema_version,
                       broker_epoch=snap.broker_epoch,
                       world_epoch=state.world_epoch if state else None,
                       revision=state.revision if state else None,
                       version=state.version if state else None,
                       phase=state.phase if state else None,
                       blocking_reason=snap.blocking_reason or (state.not_ready_reason if state else None)
                       or ("menu_not_open" if state and state.ready and not state.can_close_menu else None),
                       blocked_command_id=snap.blocked_command_id)


@mcp.tool(annotations=ToolAnnotations(read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False))
def observe() -> ObserveReply:
    """Read the current broker snapshot and the actions currently supported by M1."""
    try:
        snap = _snapshot()
    except AdapterError as exc:
        return ObserveReply(ok=False, error_source=exc.source, error=str(exc),
                            broker_connected=exc.source in {"broker", "protocol"}, bridge_connected=False,
                            wait_reason=str(exc), manual_action_required=exc.source == "broker")
    state = snap.state
    legal = ["close_menu"] if snap.connected and snap.can_act and state and state.ready and state.can_close_menu else []
    reason = snap.blocking_reason or (state.not_ready_reason if state else None)
    if not snap.connected and reason is None:
        reason = "game_bridge_disconnected"
    elif state and not state.ready and reason is None:
        reason = "game_not_ready"
    return ObserveReply(ok=True, error_source="none", broker_connected=True,
                        bridge_connected=snap.connected, snapshot=snap,
                        legal_actions=legal, wait_reason=reason,
                        manual_action_required=bool(snap.blocked_command_id))


@mcp.tool(annotations=ToolAnnotations(read_only_hint=False, destructive_hint=False, idempotent_hint=False, open_world_hint=False))
def act(command_id: Annotated[str, Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_-]+$")],
        action: Literal["close_menu"], broker_epoch: str,
        expected_world_epoch: str, expected_revision: Annotated[int, Field(ge=0)],
        expected_version: str) -> ActReply:
    """Submit one version-guarded close_menu action. Keep command_id to query ambiguous outcomes."""
    body = {"schema_version": SCHEMA_VERSION, "id": command_id, "action": action,
            "broker_epoch": broker_epoch, "expected_world_epoch": expected_world_epoch,
            "expected_revision": expected_revision, "expected_version": expected_version}
    try:
        status, payload = _call("/commands", body)
        if status != 200:
            return ActReply(ok=False, error_source="broker", error=str(payload.get("error", f"http_{status}")),
                            command_id=command_id, command_status="outcome_unknown" if status >= 500 else "rejected", broker_epoch=broker_epoch,
                            world_epoch=expected_world_epoch, revision=expected_revision,
                            version=expected_version, reason=str(payload.get("error", f"http_{status}")))
        if payload.get("id") != command_id:
            raise AdapterError("protocol", "command_response_id_mismatch")
        canonical = payload.get("command_status", payload.get("status"))
        # If the POST response is malformed, submission may have reached the broker.
        # Mark uncertainty and direct the caller to query this same command id.
        if canonical not in {"pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"}:
            raise AdapterError("protocol", "command_response_status_invalid")
        try:
            evidence = ActionEvidence.model_validate(payload["result"]) if payload.get("result") is not None else None
        except ValidationError as exc:
            raise AdapterError("protocol", "command_result_schema_invalid") from exc
        if evidence is not None and evidence.id != command_id:
            raise AdapterError("protocol", "command_result_id_mismatch")
        reason = payload.get("reason") or (evidence.reason if evidence else None)
        return ActReply(ok=canonical not in {"rejected", "failed", "outcome_unknown"},
                        error_source="none" if canonical not in {"rejected", "failed", "outcome_unknown"} else "broker",
                        error=None if canonical not in {"rejected", "failed", "outcome_unknown"} else str(reason or canonical),
                        command_id=command_id, command_status=canonical, broker_epoch=broker_epoch,
                        world_epoch=expected_world_epoch, revision=expected_revision,
                        version=expected_version, reason=reason, result=evidence)
    except AdapterError as exc:
        # If a command call timed out or returned a malformed response, its result
        # is ambiguous. Surface that ambiguity instead of suggesting a retry.
        status_value = "outcome_unknown" if exc.source in {"connection", "protocol", "internal"} else "rejected"
        return ActReply(ok=False, error_source=exc.source, error=str(exc), command_id=command_id,
                        command_status=status_value, broker_epoch=broker_epoch,
                        world_epoch=expected_world_epoch, revision=expected_revision,
                        version=expected_version, reason="query_this_command_id_before_any_new_action"
                        if status_value == "outcome_unknown" else str(exc))


@mcp.tool(annotations=ToolAnnotations(read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False))
def get_action_status(command_id: Annotated[str, Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_-]+$")]) -> ActionStatusReply:
    """Query a prior command by its original ID; not found is not proof of failure."""
    try:
        status, payload = _call("/results/" + command_id)
    except AdapterError as exc:
        return ActionStatusReply(ok=False, error_source=exc.source, error=str(exc),
                                 command_id=command_id, found=None, command_status=None)
    if status == 404:
        return ActionStatusReply(ok=False, error_source="broker", error="command_not_found",
                                 command_id=command_id, found=False, command_status="not_found")
    if status != 200:
        return ActionStatusReply(ok=False, error_source="broker", error=str(payload.get("error", f"http_{status}")),
                                 command_id=command_id, found=None, command_status=None)
    if payload.get("id") != command_id:
        return ActionStatusReply(ok=False, error_source="protocol", error="result_response_id_mismatch",
                                 command_id=command_id, found=None, command_status=None)
    canonical = payload.get("command_status", payload.get("status"))
    if canonical not in {"pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"}:
        return ActionStatusReply(ok=False, error_source="protocol", error="result_response_status_invalid",
                                 command_id=command_id, found=None, command_status=None)
    try:
        evidence = ActionEvidence.model_validate(payload["result"]) if payload.get("result") is not None else None
    except ValidationError:
        return ActionStatusReply(ok=False, error_source="protocol", error="result_evidence_schema_invalid",
                                 command_id=command_id, found=None, command_status=None)
    if evidence is not None and evidence.id != command_id:
        return ActionStatusReply(ok=False, error_source="protocol", error="result_evidence_id_mismatch",
                                 command_id=command_id, found=None, command_status=None)
    try:
        resolution = (ResolutionEvidence.model_validate(payload["resolution"])
                      if payload.get("resolution") is not None else None)
    except ValidationError:
        return ActionStatusReply(ok=False, error_source="protocol", error="resolution_evidence_schema_invalid",
                                 command_id=command_id, found=None, command_status=None)
    if resolution is not None and resolution.command_id != command_id:
        return ActionStatusReply(ok=False, error_source="protocol", error="resolution_evidence_id_mismatch",
                                 command_id=command_id, found=None, command_status=None)
    return ActionStatusReply(ok=True, error_source="none", command_id=command_id, found=True,
                             command_status=canonical,
                             broker_epoch=evidence.broker_epoch if evidence else None,
                             reason=evidence.reason if evidence else payload.get("reason"), result=evidence,
                             resolved=payload.get("resolved") is True, resolution=resolution)


def main() -> None:
    # v2's stdio runner diverts ordinary stdout writes away from the protocol stream.
    asyncio.run(mcp.run_stdio_async())
