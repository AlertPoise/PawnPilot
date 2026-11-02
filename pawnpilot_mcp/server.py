"""MCP stdio tools adapting the local PawnPilot broker."""

from __future__ import annotations

import asyncio
import json
import os
import re
import time
from pathlib import Path
from typing import Annotated, Any, Literal
from urllib.error import URLError

import httpx2
from mcp.server import MCPServer
from mcp.types import ToolAnnotations
from pydantic import BaseModel, BeforeValidator, ConfigDict, Field, ValidationError, model_validator

import bridge.client as broker_client
from bridge.client import ResponseError, request
from bridge.protocol import (WIRE_VERSION_V3, WIRE_VERSION_V4, MAX_V3_MESSAGE_BYTES, MAX_V4_MESSAGE_BYTES,
                              response_size_valid, strict_json_loads, valid_close_menu_evidence,
                              valid_success_evidence_v3, valid_success_evidence_v4, valid_progress,
                              valid_result_structure, valid_result_structure_v3,
                              valid_snapshot, valid_snapshot_v3, valid_snapshot_v4,
                              valid_result_structure_v4, valid_interaction_result_v4,
                              V4_GENERIC_ACTION_ARGUMENTS, GENERIC_INTERACTION_ACTIONS)

SCHEMA_VERSION = 2
DEFAULT_CONFIG = Path("artifacts/session.json")
MAX_RESPONSE_BYTES = max(MAX_V3_MESSAGE_BYTES, MAX_V4_MESSAGE_BYTES)
MAX_WAIT_MS = 30000
POLL_INTERVAL_SECONDS = 0.25
_COMMAND_ID = r"^[A-Za-z0-9_-]+$"


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)


class CloseMenuArguments(StrictModel):
    """An action with no parameters (close_menu, start_business, receive, etc.)."""


class ActionArgumentsV3(StrictModel):
    customer_ref: str | None = None
    item_refs: list[str] | None = None
    offer_ref: str | None = None
    collection_fingerprint: str | None = None
    option_ref: str | None = None
    dialogue_ref: str | None = None
    mode: str | None = None
    amount: int | None = Field(default=None, strict=True, ge=-2147483648, le=2147483647)
    max_spend: int | None = Field(default=None, strict=True, ge=0, le=2147483647)
    container: str | None = None
    handoff_ref: str | None = Field(default=None, min_length=1, max_length=128,
                                    pattern=r"^[A-Za-z0-9_.:-]+$")
    settlement_ref: str | None = Field(default=None, min_length=1, max_length=128,
                                        pattern=r"^[A-Za-z0-9_.:-]+$")

    @model_validator(mode="before")
    @classmethod
    def validate_present_values(cls, value):
        if type(value) is dict and any(v is None for v in value.values()):
            raise ValueError("explicit_null_arguments_rejected")
        return value


class ActionArgumentsV4(ActionArgumentsV3):
    surface_ref: str | None = Field(default=None, min_length=1, max_length=128,
                                    pattern=r"^[A-Za-z0-9_.:-]+$")
    control_ref: str | None = Field(default=None, min_length=1, max_length=128,
                                    pattern=r"^[A-Za-z0-9_.:-]+$")


class SurfaceControlV4(StrictModel):
    control_ref: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    action: Literal["advance_dialogue", "choose_dialogue_option", "dismiss_customer"]
    mode: Literal["skip", "next"] | None
    option_ref: str | None = Field(max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    text: str | None = Field(max_length=256)
    enabled: bool
    effect_contract: Literal["presentation", "document_gift", "customer_departure", "unknown"]
    effects: list[Literal["presentation", "dialogue_transition", "story_state", "inventory_transfer",
                          "cash", "reputation", "customer_departure", "day_transition", "external_scope"]] = Field(max_length=9)
    reason: str = Field(min_length=1, max_length=256)


class SurfaceObservationV4(StrictModel):
    schema_version: Literal[1]
    surface_ref: str | None = Field(max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    kind: Literal["none", "dialogue", "customer_completion", "native_panel", "unknown"]
    phase: Literal["absent", "typing", "waiting_for_advance", "awaiting_choice", "completed",
                   "progressing", "unsupported", "unknown"]
    status: Literal["idle", "progressing", "awaiting_agent", "awaiting_user", "unsupported", "unknown"]
    completeness: Literal["complete", "partial", "unavailable"]
    reason: str = Field(min_length=1, max_length=256)
    text: str | None = Field(max_length=256)
    customer_ref: str | None = Field(max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    dialogue_ref: str | None = Field(max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")
    controls: list[SurfaceControlV4] = Field(max_length=16)


class Interaction(StrictModel):
    phase: str = Field(max_length=128)
    consistency: Literal["consistent", "updating", "unverified", "blocked"]
    reason: str | None = Field(max_length=256)
    operable: bool


class BusinessState(StrictModel):
    support: Literal["unsupported"]
    customer_ref: None
    item_refs: None
    offer_ref: None
    collection_fingerprint: None


class LegalAction(StrictModel):
    action: Literal["close_menu"]
    arguments: CloseMenuArguments


class LegalActionV3(StrictModel):
    action: Literal["close_menu", "start_business", "receive_next_customer", "advance_dialogue",
                    "choose_dialogue_option", "prepare_purchase_items", "cancel_confirmation",
                    "dismiss_customer", "purchase_visible_offer", "prepare_sale_items",
                    "sell_visible_offer", "sort_inventory_default", "resume_inspection_handoff", "open_trade_panel",
                    "open_bargain_panel", "close_bargain_panel", "choose_bargain_option",
                    "close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"]
    arguments: dict[str, Any]


class GameState(StrictModel):
    schema_version: Literal[2]
    version: str = Field(min_length=1, max_length=256)
    loaded: bool
    ready: bool
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0, le=2147483647)
    phase: str = Field(max_length=128)
    scene: str = Field(max_length=128)
    menu_open: bool
    can_close_menu: bool
    cash: int = Field(ge=-2147483648, le=2147483647)
    not_ready_reason: str | None = Field(max_length=256)
    interaction: Interaction
    business: BusinessState
    legal_actions: list[LegalAction] = Field(max_length=1)

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_snapshot(value):
            raise ValueError("invalid_v2_game_state")
        return value

    @model_validator(mode="after")
    def validate_state(self):
        if self.version != f"{self.world_epoch}:{self.revision}":
            raise ValueError("version_must_match_world_epoch_and_revision")
        if self.ready and (not self.loaded or self.not_ready_reason is not None):
            raise ValueError("ready_state_inconsistent")
        if self.interaction.operable and not self.ready:
            raise ValueError("interaction_operable_requires_ready")
        if self.interaction.consistency == "blocked" and self.interaction.operable:
            raise ValueError("blocked_interaction_cannot_be_operable")
        if self.can_close_menu and (not self.ready or not self.menu_open):
            raise ValueError("menu_action_precondition_inconsistent")
        actions = [entry.action for entry in self.legal_actions]
        if self.can_close_menu != (actions == ["close_menu"]):
            raise ValueError("legal_actions_must_match_supported_game_actions")
        return self


class GameStateV3(StrictModel):
    schema_version: Literal[3]
    version: str = Field(min_length=1, max_length=256)
    loaded: bool
    ready: bool
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0, le=2147483647)
    phase: str = Field(max_length=128)
    scene: str = Field(max_length=128)
    menu_open: bool
    can_close_menu: bool
    cash: int = Field(ge=-2147483648, le=2147483647)
    not_ready_reason: str | None = Field(max_length=256)
    interaction: Interaction
    business: dict[str, Any]
    legal_actions: list[LegalActionV3] = Field(max_length=64)

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_snapshot_v3(value):
            raise ValueError("invalid_v3_game_state")
        return value

    @model_validator(mode="after")
    def validate_state(self):
        if self.version != f"{self.world_epoch}:{self.revision}":
            raise ValueError("version_must_match_world_epoch_and_revision")
        return self


class LegalActionV4(StrictModel):
    action: Literal["close_menu", "start_business", "receive_next_customer", "advance_dialogue",
                    "choose_dialogue_option", "prepare_purchase_items", "cancel_confirmation",
                    "dismiss_customer", "purchase_visible_offer", "prepare_sale_items", "sell_visible_offer",
                    "sort_inventory_default", "resume_inspection_handoff", "open_trade_panel",
                    "open_bargain_panel", "close_bargain_panel", "choose_bargain_option",
                    "close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"]
    arguments: dict[str, Any]


class GameStateV4(StrictModel):
    schema_version: Literal[4]
    version: str = Field(min_length=1, max_length=256)
    loaded: bool
    ready: bool
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0, le=2147483647)
    phase: str = Field(max_length=128)
    scene: str = Field(max_length=128)
    menu_open: bool
    can_close_menu: bool
    cash: int = Field(ge=-2147483648, le=2147483647)
    not_ready_reason: str | None = Field(max_length=256)
    interaction: Interaction
    business: dict[str, Any]
    legal_actions: list[LegalActionV4] = Field(max_length=64)
    surface: SurfaceObservationV4

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_snapshot_v4(value):
            raise ValueError("invalid_v4_game_state")
        return value

    @model_validator(mode="after")
    def validate_state(self):
        if self.version != f"{self.world_epoch}:{self.revision}":
            raise ValueError("version_must_match_world_epoch_and_revision")
        return self


class GameStateV1(StrictModel):
    """Read-only parser for evidence retained by a v1 journal."""

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
    cash: int
    not_ready_reason: str | None = None

    @model_validator(mode="before")
    @classmethod
    def validate_v1_shape(cls, value):
        keys = {"schema_version", "version", "world_epoch", "phase", "scene", "not_ready_reason",
                "revision", "loaded", "ready", "menu_open", "can_close_menu", "cash"}
        if (type(value) is not dict or set(value) != keys or type(value.get("schema_version")) is not int
                or value.get("schema_version") != 1):
            raise ValueError("invalid_v1_game_state")
        return value

    @model_validator(mode="after")
    def validate_version_tuple(self):
        if self.version != f"{self.world_epoch}:{self.revision}":
            raise ValueError("version_must_match_world_epoch_and_revision")
        if self.ready and (not self.loaded or self.not_ready_reason is not None):
            raise ValueError("ready_state_inconsistent")
        if self.can_close_menu and (not self.ready or not self.menu_open):
            raise ValueError("menu_action_precondition_inconsistent")
        return self


class BusinessStateV2(StrictModel):
    support: Literal["unsupported"]
    customer_ref: None
    item_refs: None
    offer_ref: None
    collection_fingerprint: None


class ExecutionProgress(StrictModel):
    id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    stage: Literal["validated", "invoking", "verifying", "completed", "uncertain"]
    sequence: int = Field(gt=0, le=2147483647)
    may_have_side_effects: bool
    started_at_unix_ms: int | None = Field(ge=0)
    execution_deadline_unix_ms: int | None = Field(ge=0)

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_progress(value):
            raise ValueError("invalid_execution_progress")
        return value

    @model_validator(mode="after")
    def validate_stage_evidence(self):
        timestamps = self.started_at_unix_ms is not None and self.execution_deadline_unix_ms is not None
        if self.stage == "validated" and (self.may_have_side_effects or self.started_at_unix_ms is not None
                                           or self.execution_deadline_unix_ms is not None):
            raise ValueError("validated_progress_must_precede_side_effects")
        if self.stage in {"invoking", "verifying"} and (not self.may_have_side_effects or not timestamps):
            raise ValueError("invoked_progress_requires_effect_flag_and_timestamps")
        if self.stage == "uncertain" and (not self.may_have_side_effects or not timestamps):
            raise ValueError("uncertain_progress_requires_effect_flag_and_timestamps")
        if timestamps and (self.started_at_unix_ms < 1 or
                           not 1 <= self.execution_deadline_unix_ms - self.started_at_unix_ms <= 30_000):
            raise ValueError("invalid_execution_timestamp_span")
        return self


class Snapshot(StrictModel):
    schema_version: Literal[2]
    broker_epoch: str = Field(min_length=1, max_length=128)
    session_id: str | None = Field(max_length=128)
    connected: bool
    state: GameState | None
    blocking_reason: str | None = Field(max_length=512)
    blocked_command_id: str | None = Field(max_length=128)
    can_act: bool
    execution: ExecutionProgress | None

    @model_validator(mode="before")
    @classmethod
    def validate_wire_version(cls, value):
        if type(value) is not dict or type(value.get("schema_version")) is not int or value["schema_version"] != 2:
            raise ValueError("invalid_snapshot_schema_version")
        return value

    @model_validator(mode="after")
    def validate_operability(self):
        if self.can_act and (not self.connected or self.state is None or not self.state.ready):
            raise ValueError("broker_can_act_requires_connected_ready_snapshot")
        if self.can_act and (self.blocked_command_id is not None or self.execution is not None):
            raise ValueError("broker_can_act_conflicts_with_blocked_command_or_execution")
        if self.execution is not None and self.blocked_command_id != self.execution.id:
            raise ValueError("execution_must_match_blocked_command")
        return self


class SnapshotV3(StrictModel):
    schema_version: Literal[3]
    broker_epoch: str = Field(min_length=1, max_length=128)
    session_id: str | None = Field(max_length=128)
    connected: bool
    state: GameStateV3 | None
    blocking_reason: str | None = Field(max_length=512)
    blocked_command_id: str | None = Field(max_length=128)
    can_act: bool
    execution: ExecutionProgress | None

    @model_validator(mode="after")
    def validate_operability(self):
        if self.can_act and (not self.connected or self.state is None or not self.state.ready):
            raise ValueError("broker_can_act_requires_connected_ready_snapshot")
        if self.can_act and (self.blocked_command_id is not None or self.execution is not None):
            raise ValueError("broker_can_act_conflicts_with_blocked_command_or_execution")
        if self.execution is not None and self.blocked_command_id != self.execution.id:
            raise ValueError("execution_must_match_blocked_command")
        return self


class SnapshotV4(StrictModel):
    schema_version: Literal[4]
    broker_epoch: str = Field(min_length=1, max_length=128)
    session_id: str | None = Field(max_length=128)
    connected: bool
    state: GameStateV4 | None
    blocking_reason: str | None = Field(max_length=512)
    blocked_command_id: str | None = Field(max_length=128)
    can_act: bool
    execution: ExecutionProgress | None

    @model_validator(mode="after")
    def validate_operability(self):
        if self.can_act and (not self.connected or self.state is None or not self.state.ready):
            raise ValueError("broker_can_act_requires_connected_ready_snapshot")
        if self.can_act and (self.blocked_command_id is not None or self.execution is not None):
            raise ValueError("broker_can_act_conflicts_with_blocked_command_or_execution")
        if self.execution is not None and self.blocked_command_id != self.execution.id:
            raise ValueError("execution_must_match_blocked_command")
        return self


SnapshotUnion = Snapshot | SnapshotV3 | SnapshotV4


class SnapshotV1(StrictModel):
    """Strict v1 state envelope for historical result evidence only."""

    schema_version: Literal[1]
    broker_epoch: str = Field(min_length=1, max_length=128)
    session_id: str | None = None
    connected: bool
    state: GameStateV1 | None
    blocking_reason: str | None = None
    blocked_command_id: str | None = None
    can_act: bool


class ToolReply(StrictModel):
    schema_version: Literal[2] = Field(default=2, description=
        "MCP reply-envelope format only; NOT the wire version for act. Use observe.action_schema_version.")
    ok: bool
    error_source: Literal["none", "broker", "protocol", "connection", "configuration", "internal"]
    error: str | None = None


class HealthReply(ToolReply):
    action_schema_version: Literal[2, 3, 4] | None = Field(default=None, description=
        "Wire version from the connected game state; null without a connected snapshot. "
        "Before act, obtain a fresh observe and copy its action_schema_version.")
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
    action_schema_version: Literal[2, 3, 4] | None = Field(default=None, description=
        "Copy this value into act.schema_version. Equals snapshot.state.schema_version when connected; "
        "null without a connected game state. This does not authorize an action: check current legal_actions.")
    broker_connected: bool
    bridge_connected: bool
    snapshot: SnapshotUnion | None = None
    legal_actions: list[Literal["close_menu", "start_business", "receive_next_customer", "advance_dialogue",
                                 "choose_dialogue_option", "prepare_purchase_items", "cancel_confirmation",
                                 "refuse_trade", "dismiss_customer", "purchase_visible_offer", "prepare_sale_items",
                                 "sell_visible_offer", "sort_inventory_default", "resume_inspection_handoff", "open_trade_panel",
                                 "open_bargain_panel", "close_bargain_panel", "choose_bargain_option",
                                 "close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"]] = Field(default_factory=list, max_length=64)
    wait_reason: str | None = None
    manual_action_required: bool = False


class ActionEvidenceV1(StrictModel):
    schema_version: Literal[1]
    id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    status: Literal["success", "succeeded", "rejected", "uncertain", "outcome_unknown", "error", "failed"]
    reason: str = Field(max_length=512)
    before: GameStateV1 | None = None
    after: GameStateV1 | None = None

    @model_validator(mode="before")
    @classmethod
    def validate_v1_shape(cls, value):
        keys = {"schema_version", "id", "broker_epoch", "status", "reason", "before", "after"}
        if (type(value) is not dict or set(value) != keys or type(value.get("schema_version")) is not int
                or value.get("schema_version") != 1):
            raise ValueError("invalid_v1_result")
        return value


class ActionEvidenceV2(StrictModel):
    schema_version: Literal[2]
    id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    status: Literal["succeeded", "rejected", "outcome_unknown"]
    reason: str = Field(max_length=512)
    before: GameState | None
    after: GameState | None
    execution: ExecutionProgress | None

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_result_structure(value, require_execution=False):
            raise ValueError("invalid_v2_result")
        if (type(value) is dict and value.get("status") == "succeeded"
                and not valid_close_menu_evidence(value.get("before"), value.get("after"))):
            raise ValueError("invalid_close_menu_success_evidence")
        return value

    @model_validator(mode="after")
    def validate_evidence(self):
        if self.execution is not None and self.execution.id != self.id:
            raise ValueError("execution_id_mismatch")
        if self.status == "succeeded":
            if (self.execution is None or self.execution.stage != "completed"
                    or not self.execution.may_have_side_effects or self.before is None or self.after is None):
                raise ValueError("success_requires_completed_effect_evidence")
        if self.status == "rejected" and self.execution is not None and self.execution.may_have_side_effects:
            raise ValueError("rejection_cannot_claim_possible_side_effects")
        if self.status == "outcome_unknown" and self.execution is not None and not self.execution.may_have_side_effects:
            raise ValueError("unknown_result_requires_possible_side_effects")
        return self


class ActionEvidenceV3(StrictModel):
    schema_version: Literal[3]
    id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    status: Literal["succeeded", "rejected", "outcome_unknown"]
    reason: str = Field(max_length=512)
    before: GameStateV3 | None
    after: GameStateV3 | None
    execution: ExecutionProgress | None

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_result_structure_v3(value, require_execution=False):
            raise ValueError("invalid_v3_result")
        if (type(value) is dict and value.get("status") == "succeeded"
                and not valid_success_evidence_v3(value.get("before"), value.get("after"))):
            raise ValueError("invalid_v3_success_evidence")
        return value

    @model_validator(mode="after")
    def validate_evidence(self):
        if self.execution is not None and self.execution.id != self.id:
            raise ValueError("execution_id_mismatch")
        if self.status == "succeeded" and (self.execution is None or self.execution.stage != "completed"
                or not self.execution.may_have_side_effects or self.before is None or self.after is None):
            raise ValueError("success_requires_completed_effect_evidence")
        if self.status == "rejected" and self.execution is not None and self.execution.may_have_side_effects:
            raise ValueError("rejection_cannot_claim_possible_side_effects")
        if self.status == "outcome_unknown" and self.execution is not None and not self.execution.may_have_side_effects:
            raise ValueError("unknown_result_requires_possible_side_effects")
        return self


class InteractionResultV4(StrictModel):
    input_receipt: Literal["not_invoked", "returned", "unknown"]
    verification: Literal["confirmed", "unknown"]
    effect_contract: Literal["presentation", "document_gift", "customer_departure", "unknown"]
    observed_effects: list[Literal["presentation", "dialogue_transition", "story_state", "inventory_transfer",
                                   "cash", "reputation", "customer_departure", "day_transition", "external_scope"]] = Field(max_length=9)
    unverified_effects: list[Literal["presentation", "dialogue_transition", "story_state", "inventory_transfer",
                                     "cash", "reputation", "customer_departure", "day_transition", "external_scope"]] = Field(max_length=9)
    cash_delta: int | None = Field(default=None, strict=True, ge=-2147483648, le=2147483647)
    added_item_refs: list[str] = Field(max_length=64)
    removed_item_refs: list[str] = Field(max_length=64)

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_interaction_result_v4(value):
            raise ValueError("invalid_v4_interaction_result")
        return value


class ActionEvidenceV4(StrictModel):
    schema_version: Literal[4]
    id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    status: Literal["succeeded", "rejected", "outcome_unknown"]
    reason: str = Field(max_length=512)
    before: GameStateV4 | None
    after: GameStateV4 | None
    execution: ExecutionProgress | None
    interaction_result: InteractionResultV4 | None = None

    @model_validator(mode="before")
    @classmethod
    def validate_wire_shape(cls, value):
        if not valid_result_structure_v4(value, require_execution=False):
            raise ValueError("invalid_v4_result")
        if (type(value) is dict and value.get("status") == "succeeded"):
            before, after, receipt = value.get("before"), value.get("after"), value.get("interaction_result")
            if receipt is not None:
                if not valid_success_evidence_v4(before, after, receipt):
                    raise ValueError("invalid_v4_interaction_success_evidence")
            elif (not valid_snapshot_v4(before) or not valid_snapshot_v4(after)
                    or before["world_epoch"] != after["world_epoch"]
                    or after["revision"] <= before["revision"]):
                raise ValueError("invalid_v4_success_evidence")
        return value

    @model_validator(mode="after")
    def validate_evidence(self):
        if self.execution is not None and self.execution.id != self.id:
            raise ValueError("execution_id_mismatch")
        if self.status == "succeeded" and (self.execution is None or self.execution.stage != "completed"
                or not self.execution.may_have_side_effects or self.before is None or self.after is None):
            raise ValueError("success_requires_completed_effect_evidence")
        if self.status == "rejected" and self.execution is not None and self.execution.may_have_side_effects:
            raise ValueError("rejection_cannot_claim_possible_side_effects")
        if self.status == "outcome_unknown" and self.execution is not None and not self.execution.may_have_side_effects:
            raise ValueError("unknown_result_requires_possible_side_effects")
        return self


ActionEvidence = ActionEvidenceV4 | ActionEvidenceV3 | ActionEvidenceV2 | ActionEvidenceV1


class ResolutionEvidenceV1(StrictModel):
    schema_version: Literal[1]
    method: Literal["manual_state_alignment"]
    command_id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    command_expires_at_unix_ms: int = Field(ge=0)
    session_id: str = Field(min_length=1, max_length=128)
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0)
    version: str = Field(min_length=1, max_length=256)
    snapshot: GameStateV1
    operator_note: str = Field(min_length=8, max_length=512)
    confirm_no_replay: Literal[True]
    recorded_at_unix_ms: int = Field(ge=0)

    @model_validator(mode="before")
    @classmethod
    def validate_wire_version(cls, value):
        if type(value) is not dict or type(value.get("schema_version")) is not int or value["schema_version"] != 1:
            raise ValueError("invalid_v1_resolution_schema_version")
        return value

    @model_validator(mode="after")
    def validate_snapshot_identity(self):
        if (self.version != f"{self.world_epoch}:{self.revision}"
                or self.snapshot.world_epoch != self.world_epoch
                or self.snapshot.revision != self.revision
                or self.snapshot.version != self.version):
            raise ValueError("resolution_snapshot_identity_mismatch")
        return self


class ResolutionEvidenceV2(StrictModel):
    schema_version: Literal[2]
    method: Literal["manual_state_alignment"]
    command_id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    command_expires_at_unix_ms: int = Field(ge=0)
    session_id: str = Field(min_length=1, max_length=128)
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0, le=2147483647)
    version: str = Field(min_length=1, max_length=256)
    snapshot: GameState
    operator_note: str = Field(min_length=8, max_length=512)
    confirm_no_replay: Literal[True]
    recorded_at_unix_ms: int = Field(ge=0)

    @model_validator(mode="before")
    @classmethod
    def validate_wire_version(cls, value):
        if type(value) is not dict or type(value.get("schema_version")) is not int or value["schema_version"] != 2:
            raise ValueError("invalid_v2_resolution_schema_version")
        return value

    @model_validator(mode="after")
    def validate_snapshot_identity(self):
        if (self.version != f"{self.world_epoch}:{self.revision}"
                or self.snapshot.world_epoch != self.world_epoch
                or self.snapshot.revision != self.revision
                or self.snapshot.version != self.version):
            raise ValueError("resolution_snapshot_identity_mismatch")
        return self


class ResolutionEvidenceV3(StrictModel):
    schema_version: Literal[3]
    method: Literal["manual_state_alignment"]
    command_id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    command_expires_at_unix_ms: int = Field(ge=0)
    session_id: str = Field(min_length=1, max_length=128)
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0, le=2147483647)
    version: str = Field(min_length=1, max_length=256)
    snapshot: GameStateV3
    operator_note: str = Field(min_length=8, max_length=512)
    confirm_no_replay: Literal[True]
    recorded_at_unix_ms: int = Field(ge=0)

    @model_validator(mode="after")
    def validate_snapshot_identity(self):
        if (self.version != f"{self.world_epoch}:{self.revision}"
                or self.snapshot.world_epoch != self.world_epoch
                or self.snapshot.revision != self.revision or self.snapshot.version != self.version):
            raise ValueError("resolution_snapshot_identity_mismatch")
        return self


class ResolutionEvidenceV4(StrictModel):
    schema_version: Literal[4]
    method: Literal["manual_state_alignment"]
    command_id: str = Field(min_length=1, max_length=128)
    broker_epoch: str = Field(min_length=1, max_length=128)
    command_expires_at_unix_ms: int = Field(ge=0)
    session_id: str = Field(min_length=1, max_length=128)
    world_epoch: str = Field(min_length=1, max_length=128)
    revision: int = Field(ge=0, le=2147483647)
    version: str = Field(min_length=1, max_length=256)
    snapshot: GameStateV4
    operator_note: str = Field(min_length=8, max_length=512)
    confirm_no_replay: Literal[True]
    recorded_at_unix_ms: int = Field(ge=0)

    @model_validator(mode="after")
    def validate_snapshot_identity(self):
        if (self.version != f"{self.world_epoch}:{self.revision}"
                or self.snapshot.world_epoch != self.world_epoch
                or self.snapshot.revision != self.revision or self.snapshot.version != self.version):
            raise ValueError("resolution_snapshot_identity_mismatch")
        return self


ResolutionEvidence = ResolutionEvidenceV4 | ResolutionEvidenceV3 | ResolutionEvidenceV2 | ResolutionEvidenceV1


class CommandReply(StrictModel):
    schema_version: Literal[2, 3, 4]
    id: str = Field(min_length=1, max_length=128)
    command_status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"]
    status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"]
    result: ActionEvidence | None
    resolved: bool
    resolution: ResolutionEvidence | None
    execution: ExecutionProgress | None
    record_schema_version: Literal[1, 2, 3, 4]

    @model_validator(mode="before")
    @classmethod
    def validate_raw_envelope(cls, value):
        if (type(value) is not dict or type(value.get("schema_version")) is not int
                or value.get("schema_version") not in (2, 3, 4)):
            raise ValueError("command_envelope_must_be_object")
        record_version = value.get("record_schema_version")
        result = value.get("result")
        if type(record_version) is not int or record_version not in (1, 2, 3, 4):
            raise ValueError("invalid_record_schema_version")
        expected_envelope = record_version if record_version in (3, 4) else 2
        if value.get("schema_version") != expected_envelope:
            raise ValueError("record_and_envelope_schema_mismatch")
        if result is not None:
            result_version = result.get("schema_version") if type(result) is dict else None
            if type(result_version) is not int:
                raise ValueError("invalid_result_schema_version")
            if result_version != record_version:
                raise ValueError("record_and_result_schema_mismatch")
            if result_version == 2 and not valid_result_structure(result, require_execution=False):
                raise ValueError("invalid_v2_result")
            if result_version == 3 and not valid_result_structure_v3(result, require_execution=False):
                raise ValueError("invalid_v3_result")
            if result_version == 4 and not valid_result_structure_v4(result, require_execution=False):
                raise ValueError("invalid_v4_result")
        elif record_version not in (1, 2, 3, 4):
            raise ValueError("invalid_record_schema_version")
        if value.get("resolved") is not (value.get("resolution") is not None):
            raise ValueError("resolution_flag_mismatch")
        status = value.get("status")
        command_status = value.get("command_status")
        aliases = {"success": "succeeded", "error": "failed", "uncertain": "outcome_unknown"}
        normalized = aliases.get(status, status)
        if normalized != command_status:
            raise ValueError("command_status_alias_mismatch")
        if normalized in {"pending", "running"}:
            if result is not None:
                raise ValueError("nonterminal_command_has_result")
        elif normalized in {"succeeded", "rejected", "failed", "outcome_unknown"} and result is None:
            raise ValueError("terminal_command_missing_result")
        if result is not None:
            result_status = result.get("status") if type(result) is dict else None
            if aliases.get(result_status, result_status) != normalized:
                raise ValueError("result_status_does_not_match_command")
        return value

    @model_validator(mode="after")
    def validate_envelope(self):
        if self.status != self.command_status:
            raise ValueError("command_status_alias_mismatch")
        if self.result is not None and self.result.id != self.id:
            raise ValueError("result_id_mismatch")
        if self.resolution is not None and self.resolution.command_id != self.id:
            raise ValueError("resolution_id_mismatch")
        expected_resolutions = {1, 2} if self.record_schema_version == 1 else {self.record_schema_version}
        if self.resolution is not None and self.resolution.schema_version not in expected_resolutions:
            raise ValueError("resolution_record_schema_mismatch")
        if self.execution is not None and self.execution.id != self.id:
            raise ValueError("progress_id_mismatch")
        if self.record_schema_version == 2 and not isinstance(self.result, (ActionEvidenceV2, type(None))):
            raise ValueError("v2_record_has_v1_result")
        if self.record_schema_version == 3 and self.result is not None and not isinstance(self.result, ActionEvidenceV3):
            raise ValueError("v3_record_has_non_v3_result")
        if self.record_schema_version == 4 and self.result is not None and not isinstance(self.result, ActionEvidenceV4):
            raise ValueError("v4_record_has_non_v4_result")
        if self.record_schema_version == 1 and (isinstance(self.result, (ActionEvidenceV2, ActionEvidenceV3, ActionEvidenceV4)) or self.execution is not None):
            raise ValueError("v1_record_has_v2_evidence")
        if self.resolved != (self.resolution is not None):
            raise ValueError("resolution_flag_mismatch")
        result_execution = getattr(self.result, "execution", None) if self.result is not None else None
        if self.result is not None and result_execution is not None and self.execution != result_execution:
            raise ValueError("execution_evidence_mismatch")
        return self


class ActReply(ToolReply):
    command_id: str
    command_status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"]
    status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown"]
    broker_epoch: str
    world_epoch: str
    revision: int
    version: str
    reason: str | None = None
    result: ActionEvidence | None = None
    execution: ExecutionProgress | None = None
    record_schema_version: Literal[1, 2, 3, 4] | None = None


class ActionStatusReply(ToolReply):
    command_id: str
    found: bool | None
    command_status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown", "not_found"] | None
    broker_epoch: str | None = None
    reason: str | None = None
    result: ActionEvidence | None = None
    resolved: bool = False
    resolution: ResolutionEvidence | None = None
    execution: ExecutionProgress | None = None
    record_schema_version: Literal[1, 2, 3, 4] | None = None


class WaitRequest(StrictModel):
    criterion: Literal["revision", "command_terminal", "operable"]
    max_wait_ms: int = Field(ge=0, le=MAX_WAIT_MS)
    broker_epoch: str | None = Field(default=None, min_length=1, max_length=128)
    world_epoch: str | None = Field(default=None, min_length=1, max_length=128)
    revision: int | None = Field(default=None, strict=True, ge=0, le=2147483647)
    command_id: str | None = Field(default=None, min_length=1, max_length=128, pattern=_COMMAND_ID)

    @model_validator(mode="after")
    def validate_criterion_fields(self):
        expected = {"criterion", "max_wait_ms"}
        if self.criterion == "revision":
            expected |= {"broker_epoch", "world_epoch", "revision"}
        elif self.criterion == "command_terminal":
            expected.add("command_id")
        supplied = self.model_fields_set
        if supplied != expected:
            raise ValueError("wait_parameters_must_match_criterion")
        if self.criterion == "revision" and (self.broker_epoch is None or self.world_epoch is None or self.revision is None):
            raise ValueError("revision_wait_requires_observed_identity")
        if self.criterion == "command_terminal" and self.command_id is None:
            raise ValueError("command_terminal_wait_requires_command_id")
        return self


class WaitReply(ToolReply):
    outcome: Literal["updated", "terminal", "operable", "timed_out"] | None = None
    snapshot: SnapshotUnion | None = None
    command_id: str | None = None
    command_status: Literal["pending", "running", "succeeded", "rejected", "failed", "outcome_unknown", "not_found"] | None = None
    command: CommandReply | None = None
    reason: str | None = None


class AdapterError(Exception):
    def __init__(self, source: str, message: str):
        super().__init__(message)
        self.source = source


def _exact_schema_2(value):
    if type(value) is not int or value not in (2, 3, 4):
        raise ValueError("schema_version_must_be_exact_integer_2_or_3_or_4")
    return value


def _config_path() -> Path:
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
        raise AdapterError("connection", "broker_request_timed_out_or_unreachable") from exc
    except Exception as exc:
        raise AdapterError("internal", "broker_request_internal_error") from exc
    if not isinstance(payload, dict):
        raise AdapterError("protocol", "broker_response_must_be_object")
    return status, payload


def _parse_snapshot(payload: dict) -> SnapshotUnion:
    try:
        version = payload.get("schema_version")
        if type(version) is int and version == WIRE_VERSION_V4:
            return SnapshotV4.model_validate(payload)
        if type(version) is int and version == WIRE_VERSION_V3:
            return SnapshotV3.model_validate(payload)
        return Snapshot.model_validate(payload)
    except ValidationError as exc:
        raise AdapterError("protocol", "broker_state_schema_invalid") from exc


def _snapshot() -> SnapshotUnion:
    status, payload = _call("/state")
    if status != 200:
        error = str(payload.get("error", f"http_{status}"))
        source = "protocol" if error == "schema_version_unsupported" or status == 400 else "broker"
        raise AdapterError(source, error)
    return _parse_snapshot(payload)


def _state_can_act(snap: SnapshotUnion) -> bool:
    return bool(snap.connected and snap.can_act and snap.state and snap.state.ready and snap.state.legal_actions)


def _command_reply(payload: dict, command_id: str) -> CommandReply:
    if payload.get("id") != command_id:
        raise AdapterError("protocol", "command_response_id_mismatch")
    try:
        return CommandReply.model_validate(payload)
    except ValidationError as exc:
        raise AdapterError("protocol", "command_response_schema_invalid") from exc


def _to_action_status(command: CommandReply) -> ActionStatusReply:
    return ActionStatusReply(ok=True, error_source="none", command_id=command.id, found=True,
                              command_status=command.command_status,
                              broker_epoch=command.result.broker_epoch if command.result else None,
                              reason=command.result.reason if command.result else None,
                              result=command.result, resolved=command.resolved,
                              resolution=command.resolution, execution=command.execution,
                              record_schema_version=command.record_schema_version)


async def _async_get(client: httpx2.AsyncClient, config: dict, path: str,
                     timeout: float) -> tuple[int, dict]:
    """Bounded, cancellation-friendly GET. Wait never makes a mutating request."""
    url = f"http://127.0.0.1:{broker_client.PORT}{path}"
    headers = {"Authorization": "Bearer " + config["token"], "Accept": "application/json"}
    try:
        async with client.stream("GET", url, headers=headers, timeout=timeout) as response:
            if 300 <= response.status_code < 400:
                raise AdapterError("protocol", "broker_redirect_refused")
            content_length = response.headers.get("content-length")
            if content_length is not None and content_length.isdigit() and int(content_length) > MAX_RESPONSE_BYTES:
                raise AdapterError("protocol", "response_too_large")
            raw = bytearray()
            async for chunk in response.aiter_bytes(chunk_size=8192):
                raw.extend(chunk)
                if len(raw) > MAX_RESPONSE_BYTES:
                    raise AdapterError("protocol", "response_too_large")
            if response.headers.get("content-type", "").split(";", 1)[0].strip().lower() != "application/json":
                raise AdapterError("protocol", "response_json_required")
    except AdapterError:
        raise
    except httpx2.TimeoutException as exc:
        raise AdapterError("connection", "broker_read_timed_out") from exc
    except httpx2.TransportError as exc:
        raise AdapterError("connection", "broker_request_unreachable") from exc
    try:
        payload = strict_json_loads(bytes(raw))
    except (ValueError, UnicodeError) as exc:
        raise AdapterError("protocol", "response_invalid_json") from exc
    if not isinstance(payload, dict):
        raise AdapterError("protocol", "broker_response_must_be_object")
    if not response_size_valid(payload, len(raw)):
        raise AdapterError("protocol", "response_too_large")
    return response.status_code, payload


async def _snapshot_async(client: httpx2.AsyncClient, config: dict, timeout: float) -> SnapshotUnion:
    status, payload = await _async_get(client, config, "/state", timeout)
    if status != 200:
        error = str(payload.get("error", f"http_{status}"))
        source = "protocol" if error == "schema_version_unsupported" or status == 400 else "broker"
        raise AdapterError(source, error)
    return _parse_snapshot(payload)


async def _command_async(client: httpx2.AsyncClient, config: dict, command_id: str,
                         timeout: float) -> CommandReply | None:
    status, payload = await _async_get(client, config, "/results/" + command_id, timeout)
    if status == 404:
        return None
    if status != 200:
        error = str(payload.get("error", f"http_{status}"))
        source = "protocol" if error == "schema_version_unsupported" or status == 400 else "broker"
        raise AdapterError(source, error)
    return _command_reply(payload, command_id)


mcp = MCPServer("PawnPilot")


@mcp.tool(annotations=ToolAnnotations(read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False))
def health_check() -> HealthReply:
    """Check broker and game bridge connectivity, readiness, and protocol compatibility."""
    try:
        snap = _snapshot()
    except AdapterError as exc:
        return HealthReply(ok=False, error_source=exc.source, error=str(exc), protocol_compatible=False
                           if exc.source == "protocol" else None,
                           broker_connected=exc.source in {"broker", "protocol"}, bridge_connected=False,
                           loaded=False, ready=False, can_act=False)
    state = snap.state
    can_act = _state_can_act(snap)
    blocking_reason = None if can_act else (
        snap.blocking_reason or (state.not_ready_reason if state else None)
        or ("no_legal_actions" if state and state.schema_version in (3, 4) and state.ready
            and not state.legal_actions else None)
        or ("menu_not_open" if state and state.schema_version == 2 and state.ready
            and not state.can_close_menu else None))
    return HealthReply(ok=True, error_source="none", protocol_compatible=True,
                       action_schema_version=state.schema_version if snap.connected and state else None,
                       broker_connected=True, bridge_connected=snap.connected,
                       loaded=bool(state and state.loaded), ready=bool(state and state.ready),
                       can_act=can_act, broker_epoch=snap.broker_epoch,
                       world_epoch=state.world_epoch if state else None,
                       revision=state.revision if state else None,
                       version=state.version if state else None,
                       phase=state.phase if state else None,
                       blocking_reason=blocking_reason,
                       blocked_command_id=snap.blocked_command_id)


@mcp.tool(annotations=ToolAnnotations(read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False))
def observe() -> ObserveReply:
    """Read current state and legal actions. Copy action_schema_version into act.schema_version,
    NOT the reply-envelope schema_version or business.details.schema_version.

    If business.details.inspection_handoff.requires_manual_inspection is true and status is
    waiting_for_user, immediately tell the user which visible items need manual inspection, stop
    business writes, and wait for the user's explicit "检查完成，继续" confirmation. Read-only tools
    remain available during handoff.
    """
    try:
        snap = _snapshot()
    except AdapterError as exc:
        return ObserveReply(ok=False, error_source=exc.source, error=str(exc),
                            broker_connected=exc.source in {"broker", "protocol"}, bridge_connected=False,
                            wait_reason=str(exc), manual_action_required=exc.source == "broker")
    state = snap.state
    can_act = _state_can_act(snap)
    legal = [entry.action for entry in state.legal_actions] if can_act and state else []
    reason = snap.blocking_reason or (state.interaction.reason if state else None) or (state.not_ready_reason if state else None)
    if not snap.connected and reason is None:
        reason = "game_bridge_disconnected"
    elif state and not state.ready and reason is None:
        reason = "game_not_ready"
    elif snap.connected and not legal and reason is None:
        reason = "no_legal_actions"
    return ObserveReply(ok=True, error_source="none", broker_connected=True,
                        action_schema_version=state.schema_version if snap.connected and state else None,
                        bridge_connected=snap.connected, snapshot=snap, legal_actions=legal,
                        wait_reason=reason, manual_action_required=bool(snap.blocked_command_id))


@mcp.tool(annotations=ToolAnnotations(read_only_hint=False, destructive_hint=False, idempotent_hint=False, open_world_hint=False))
def act(schema_version: Annotated[Literal[2, 3, 4], BeforeValidator(_exact_schema_2), Field(description=
            "Copy action_schema_version from the immediately preceding observe response "
            "(equals snapshot.state.schema_version). Do NOT use the outer reply schema_version=2 "
            "or business.details.schema_version. Never guess or silently upgrade the wire version.")],
        command_id: Annotated[str, Field(min_length=1, max_length=128, pattern=_COMMAND_ID)],
        action: Literal["close_menu", "start_business", "receive_next_customer", "advance_dialogue",
                        "choose_dialogue_option", "prepare_purchase_items", "cancel_confirmation",
                        "refuse_trade", "dismiss_customer", "purchase_visible_offer", "prepare_sale_items",
                        "sell_visible_offer", "sort_inventory_default", "resume_inspection_handoff", "open_trade_panel",
                        "open_bargain_panel", "close_bargain_panel", "choose_bargain_option",
                        "close_business", "open_end_of_day", "confirm_end_of_day", "close_day_report"],
        arguments: CloseMenuArguments | ActionArgumentsV4 | ActionArgumentsV3,
        broker_epoch: Annotated[str, Field(min_length=1, max_length=128)],
        expected_world_epoch: Annotated[str, Field(min_length=1, max_length=128)],
        expected_revision: Annotated[int, Field(strict=True, ge=0, le=2147483647)],
        expected_version: Annotated[str, Field(min_length=1, max_length=256)]) -> ActReply:
    """Submit one version-guarded action using observe.action_schema_version as schema_version.
    Query the same ID after ambiguous outcomes; never replay automatically. Submit
    resume_inspection_handoff only after explicit user confirmation; bind handoff_ref from the latest
    observe result, then observe again before any further action. Use the day-stage legal actions to
    close business, open end-of-day, confirm the currently displayed settlement, and close its report.
    For confirm_end_of_day, pass the observed settlement_ref and total as amount; max_spend must be
    at least max(0, amount), and a positive amount is limited by current cash.
    """
    if schema_version == 2 and (action != "close_menu" or arguments.model_dump(mode="json") != {}):
        reason = "action_not_allowed"
        return ActReply(ok=False, error_source="protocol", error=reason,
                        command_id=command_id, command_status="rejected", status="rejected",
                        broker_epoch=broker_epoch, world_epoch=expected_world_epoch,
                        revision=expected_revision, version=expected_version, reason=reason)
    argument_values = arguments.model_dump(mode="json", exclude_unset=True, exclude_none=True)
    if schema_version == 3 and ({"surface_ref", "control_ref"} & set(argument_values)):
        reason = "action_not_allowed"
        return ActReply(ok=False, error_source="protocol", error=reason,
                        command_id=command_id, command_status="rejected", status="rejected",
                        broker_epoch=broker_epoch, world_epoch=expected_world_epoch,
                        revision=expected_revision, version=expected_version, reason=reason)
    has_generic_refs = bool({"surface_ref", "control_ref"} & set(argument_values))
    if schema_version == 4 and has_generic_refs and (
            action not in GENERIC_INTERACTION_ACTIONS
            or set(argument_values) != V4_GENERIC_ACTION_ARGUMENTS[action]):
        reason = "invalid_arguments"
        return ActReply(ok=False, error_source="protocol", error=reason,
                        command_id=command_id, command_status="rejected", status="rejected",
                        broker_epoch=broker_epoch, world_epoch=expected_world_epoch,
                        revision=expected_revision, version=expected_version, reason=reason)
    body = {"schema_version": schema_version, "id": command_id, "action": action,
            "arguments": argument_values, "broker_epoch": broker_epoch,
            "expected_world_epoch": expected_world_epoch, "expected_revision": expected_revision,
            "expected_version": expected_version}
    try:
        status, payload = _call("/commands", body)
        if status != 200:
            reason = str(payload.get("error", f"http_{status}"))
            if reason == "schema_version_unsupported" or status == 400:
                source, command_status = "protocol", "rejected"
            else:
                source, command_status = "broker", "outcome_unknown" if status >= 500 else "rejected"
            return ActReply(ok=False, error_source=source, error=reason,
                            command_id=command_id, command_status=command_status, status=command_status,
                            broker_epoch=broker_epoch, world_epoch=expected_world_epoch,
                            revision=expected_revision, version=expected_version, reason=reason)
        command = _command_reply(payload, command_id)
        reason = command.result.reason if command.result else None
        failed = command.command_status in {"rejected", "failed", "outcome_unknown"}
        return ActReply(ok=not failed, error_source="broker" if failed else "none",
                        error=reason if failed else None, command_id=command.id,
                        command_status=command.command_status, status=command.status,
                        broker_epoch=broker_epoch, world_epoch=expected_world_epoch,
                        revision=expected_revision, version=expected_version, reason=reason,
                        result=command.result, execution=command.execution,
                        record_schema_version=command.record_schema_version)
    except AdapterError as exc:
        # A malformed/ambiguous POST response may follow a durable enqueue. Never retry here.
        result_status = "outcome_unknown" if exc.source in {"connection", "protocol", "internal"} else "rejected"
        return ActReply(ok=False, error_source=exc.source, error=str(exc), command_id=command_id,
                        command_status=result_status, status=result_status, broker_epoch=broker_epoch,
                        world_epoch=expected_world_epoch, revision=expected_revision,
                        version=expected_version, reason="query_this_command_id_before_any_new_action"
                        if result_status == "outcome_unknown" else str(exc))


@mcp.tool(annotations=ToolAnnotations(read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False))
def get_action_status(command_id: Annotated[str, Field(min_length=1, max_length=128, pattern=_COMMAND_ID)]) -> ActionStatusReply:
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
        reason = str(payload.get("error", f"http_{status}"))
        source = "protocol" if reason == "schema_version_unsupported" or status == 400 else "broker"
        return ActionStatusReply(ok=False, error_source=source, error=reason,
                                 command_id=command_id, found=None, command_status=None)
    try:
        return _to_action_status(_command_reply(payload, command_id))
    except AdapterError as exc:
        return ActionStatusReply(ok=False, error_source=exc.source, error=str(exc),
                                 command_id=command_id, found=None, command_status=None)


@mcp.tool(annotations=ToolAnnotations(read_only_hint=True, destructive_hint=False, idempotent_hint=True, open_world_hint=False))
async def wait_for_update(request: WaitRequest) -> WaitReply:
    """Wait briefly for a revision, terminal command result, or supported game-operable state."""
    try:
        config = _load_config()
    except AdapterError as exc:
        return WaitReply(ok=False, error_source=exc.source, error=str(exc), reason=str(exc))

    deadline = time.monotonic() + (0.05 if request.max_wait_ms == 0 else request.max_wait_ms / 1000.0)
    terminal = {"succeeded", "rejected", "failed", "outcome_unknown"}
    last_snapshot = None
    last_command = None
    last_command_status = None
    try:
        async with httpx2.AsyncClient(trust_env=False, follow_redirects=False, timeout=1.0) as client:
            while True:
                remaining = deadline - time.monotonic()
                cycle_budget = remaining
                if cycle_budget <= 0:
                    return WaitReply(ok=True, error_source="none", outcome="timed_out", snapshot=last_snapshot,
                                     command_id=request.command_id, command_status=last_command_status,
                                     command=last_command, reason="deadline_elapsed")
                try:
                    # Bound the entire poll cycle, including both GETs for command waits.
                    async with asyncio.timeout(cycle_budget):
                        snap = await _snapshot_async(client, config, min(1.0, cycle_budget + 0.02))
                        last_snapshot = snap
                        command = None
                        if request.criterion == "command_terminal":
                            remaining = deadline - time.monotonic()
                            if remaining <= 0:
                                raise TimeoutError
                            command = await _command_async(client, config, request.command_id,
                                                           min(1.0, remaining + 0.02))
                            last_command = command
                            last_command_status = command.command_status if command else "not_found"
                            if command is not None and command.command_status in terminal:
                                return WaitReply(ok=True, error_source="none", outcome="terminal", snapshot=snap,
                                                 command_id=request.command_id, command_status=command.command_status,
                                                 command=command, reason=command.result.reason if command.result else None)
                        elif request.criterion == "revision":
                            state = snap.state
                            if (snap.broker_epoch != request.broker_epoch or state is not None and
                                    (state.world_epoch != request.world_epoch or state.revision != request.revision)):
                                return WaitReply(ok=True, error_source="none", outcome="updated", snapshot=snap)
                        elif (snap.connected and snap.can_act and snap.state is not None and snap.state.ready
                              and snap.state.interaction.operable):
                            return WaitReply(ok=True, error_source="none", outcome="operable", snapshot=snap)
                except TimeoutError:
                    return WaitReply(ok=True, error_source="none", outcome="timed_out", snapshot=last_snapshot,
                                     command_id=request.command_id, command_status=last_command_status,
                                     command=last_command, reason="deadline_elapsed")
                if request.max_wait_ms == 0:
                    return WaitReply(ok=True, error_source="none", outcome="timed_out", snapshot=last_snapshot,
                                     command_id=request.command_id, command_status=last_command_status,
                                     command=last_command, reason="deadline_elapsed")
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return WaitReply(ok=True, error_source="none", outcome="timed_out", snapshot=last_snapshot,
                                     command_id=request.command_id, command_status=last_command_status,
                                     command=last_command, reason="deadline_elapsed")
                await asyncio.sleep(min(POLL_INTERVAL_SECONDS, remaining))
    except AdapterError as exc:
        return WaitReply(ok=False, error_source=exc.source, error=str(exc), reason=str(exc),
                         command_id=request.command_id)
    except httpx2.TimeoutException:
        return WaitReply(ok=False, error_source="connection", error="broker_read_timed_out",
                         reason="broker_read_timed_out", command_id=request.command_id)
    # asyncio.CancelledError intentionally propagates: MCP cancellation is not timeout.


def main() -> None:
    # The MCP SDK owns stdout for protocol messages; diagnostics stay on stderr.
    asyncio.run(mcp.run_stdio_async())
