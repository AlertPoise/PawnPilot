"""Wire-v4 interaction validation while v2/v3 stay frozen."""
import copy
import json
from pathlib import Path
import sys
import unittest

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.protocol import (
    MAX_V4_MESSAGE_BYTES,
    canonical_request,
    request_fingerprint,
    valid_action_binding_v4,
    valid_action_state_v4,
    valid_command_v3,
    valid_command_v4,
    valid_interaction_result_v4,
    valid_request_v4,
    valid_result_structure_v4,
    valid_snapshot_v3,
    valid_snapshot_v4,
    valid_success_result_v4,
)


V3_DIALOGUE = json.loads((ROOT / "protocol/fixtures/v3-dialogue-snapshot.json").read_text(encoding="utf-8"))
V2_CLOSE = json.loads((ROOT / "protocol/fixtures/v2-close-menu.json").read_text(encoding="utf-8"))
V4_INTERACTION_FIXTURE = json.loads(
    (ROOT / "protocol/fixtures/v4-interaction-fixtures.json").read_text(encoding="utf-8"))
V2_SCHEMA = json.loads((ROOT / "protocol/schemas/wire-v2.json").read_text(encoding="utf-8"))
V3_SCHEMA = json.loads((ROOT / "protocol/schemas/wire-v3.json").read_text(encoding="utf-8"))
V4_SCHEMA = json.loads((ROOT / "protocol/schemas/wire-v4.json").read_text(encoding="utf-8"))


def surface_for(action="choose_dialogue_option", contract="presentation", effects=None):
    if action == "advance_dialogue":
        phase, mode, option_ref, kind = "typing", "skip", None, "dialogue"
    elif action == "choose_dialogue_option":
        phase, mode, option_ref, kind = "awaiting_choice", None, "option-1", "dialogue"
    else:
        phase, mode, option_ref, kind = "completed", None, None, "customer_completion"
        if contract == "presentation":
            contract = "customer_departure"
            effects = ["customer_departure"] if effects is None else effects
    return {
        "schema_version": 1,
        "surface_ref": "surface-1",
        "kind": kind,
        "phase": phase,
        "status": "awaiting_agent",
        "completeness": "complete",
        "reason": "surface_observed",
        "text": "Would you like to continue?",
        "customer_ref": "customer-1",
        "dialogue_ref": None if action == "dismiss_customer" else "dialogue-1",
        "controls": [{
            "control_ref": "control-1",
            "action": action,
            "mode": mode,
            "option_ref": option_ref,
            "text": "Continue",
            "enabled": True,
            "effect_contract": contract,
            "effects": effects if effects is not None else ["dialogue_transition", "presentation"],
            "reason": "native_control_enabled",
        }],
    }


def generic_arguments(action="choose_dialogue_option"):
    args = {"customer_ref": "customer-1", "surface_ref": "surface-1", "control_ref": "control-1"}
    if action == "advance_dialogue":
        args.update(dialogue_ref="dialogue-1", mode="skip")
    elif action == "choose_dialogue_option":
        args.update(dialogue_ref="dialogue-1", option_ref="option-1")
    return args


def snapshot_v4(action="choose_dialogue_option", contract="presentation", effects=None):
    state = copy.deepcopy(V3_DIALOGUE)
    state["schema_version"] = 4
    state["business"]["details"]["containers"] = [
        {"container": name, "completeness": "complete", "reason": None, "item_refs": []}
        for name in ("front", "back", "player")
    ]
    if action == "dismiss_customer":
        customer = state["business"]["details"]["customer"]
        customer.update(dialogue_ref=None, dialogue_stage="absent", dialogue_text=None,
                        is_typing=None, waiting_for_advance=None, option_refs=[], options=[])
    state["surface"] = surface_for(action, contract, effects)
    state["legal_actions"] = [{"action": action, "arguments": generic_arguments(action)}]
    return state


def command_v4(state, action="choose_dialogue_option", arguments=None, command_id="interaction-1"):
    return {
        "schema_version": 4,
        "id": command_id,
        "broker_epoch": "broker-1",
        "action": action,
        "expected_world_epoch": state["world_epoch"],
        "expected_revision": state["revision"],
        "expected_version": state["version"],
        "arguments": copy.deepcopy(arguments if arguments is not None else generic_arguments(action)),
        "start_deadline_unix_ms": 105000,
        "execution_timeout_ms": 5000,
    }


def transitioned_after(before):
    after = copy.deepcopy(before)
    after["revision"] += 1
    after["version"] = f'{after["world_epoch"]}:{after["revision"]}'
    after["legal_actions"] = []
    after["surface"].update(surface_ref="surface-2", phase="typing", status="idle",
                             dialogue_ref="dialogue-2", controls=[])
    customer = after["business"]["details"]["customer"]
    customer.update(dialogue_ref="dialogue-2", dialogue_stage="typing", option_refs=[],
                    dialogue_text="A new line.", is_typing=True, waiting_for_advance=False, options=[])
    return after


def progress(command):
    return {"id": command["id"], "broker_epoch": command["broker_epoch"], "stage": "completed",
            "sequence": 4, "may_have_side_effects": True, "started_at_unix_ms": 100100,
            "execution_deadline_unix_ms": 105100}


def result_for(command, before, after, receipt):
    return {"schema_version": 4, "id": command["id"], "broker_epoch": command["broker_epoch"],
            "status": "succeeded", "reason": "interaction_verified", "before": before, "after": after,
            "execution": progress(command), "interaction_result": receipt}


class ProtocolV4Tests(unittest.TestCase):
    def test_managed_runtime_exported_interaction_fixtures_match_python_contract(self):
        self.assertEqual(V4_INTERACTION_FIXTURE["source"], "fixture")
        self.assertEqual(V4_INTERACTION_FIXTURE["schema_version"], 4)
        cases = V4_INTERACTION_FIXTURE["cases"]
        self.assertEqual(set(cases), {
            "presentation_confirmed", "document_gift_zero_confirmed", "document_gift_one_confirmed",
            "customer_departure_confirmed", "cash_change_uncertain", "cash_and_inventory_delta_unknown",
            "invoked_exception_unknown", "preentry_rejected",
        })
        snapshot_schema = Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Snapshot"})
        command_schema = Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Command"})
        result_schema = Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Result"})
        confirmed = {"presentation_confirmed", "document_gift_zero_confirmed",
                     "document_gift_one_confirmed", "customer_departure_confirmed"}
        uncertain = {"cash_change_uncertain", "cash_and_inventory_delta_unknown", "invoked_exception_unknown"}
        for name, case in cases.items():
            with self.subTest(case=name):
                command, before, after = case["command"], case["before"], case["after"]
                result = case["result"]
                self.assertTrue(valid_command_v4(command))
                self.assertTrue(valid_snapshot_v4(before))
                self.assertTrue(valid_snapshot_v4(result["before"]))
                if after is not None:
                    self.assertTrue(valid_snapshot_v4(after))
                    snapshot_schema.validate(after)
                command_schema.validate(command)
                snapshot_schema.validate(before)
                result_schema.validate(result)
                self.assertEqual(case["progress"], result["execution"])
                self.assertTrue(valid_result_structure_v4(result, command=command))
                self.assertTrue(valid_success_result_v4(command, result))
                if name in confirmed:
                    self.assertEqual(result["status"], "succeeded")
                    self.assertEqual(result["interaction_result"]["input_receipt"], "returned")
                    self.assertEqual(result["interaction_result"]["verification"], "confirmed")
                elif name in uncertain:
                    self.assertEqual(result["status"], "outcome_unknown")
                    self.assertEqual(result["interaction_result"]["verification"], "unknown")
                    if name == "cash_and_inventory_delta_unknown":
                        self.assertEqual(result["interaction_result"]["cash_delta"], 1)
                        self.assertEqual(result["interaction_result"]["added_item_refs"], ["unexpected-item"])
                        self.assertEqual(result["interaction_result"]["removed_item_refs"], ["owned-sale-item"])
                else:
                    self.assertEqual(result["status"], "rejected")
                    self.assertNotIn("interaction_result", result)

        unresolved = copy.deepcopy(cases["cash_change_uncertain"])
        unresolved["result"]["interaction_result"]["unverified_effects"] = []
        self.assertFalse(valid_result_structure_v4(unresolved["result"], command=unresolved["command"]))
        unresolved = copy.deepcopy(cases["cash_change_uncertain"])
        unresolved["result"]["interaction_result"]["cash_delta"] = 0
        self.assertFalse(valid_result_structure_v4(unresolved["result"], command=unresolved["command"]))

    def test_v2_and_v3_contracts_reject_v4_surface_without_changing_v3_arguments(self):
        v3 = copy.deepcopy(V3_DIALOGUE)
        v3["surface"] = surface_for()
        self.assertTrue(valid_snapshot_v3(V3_DIALOGUE))
        self.assertFalse(valid_snapshot_v3(v3))
        v3_args = {"schema_version": 3, "id": "old", "broker_epoch": "b", "action": "choose_dialogue_option",
                   "expected_world_epoch": V3_DIALOGUE["world_epoch"], "expected_revision": 1,
                   "expected_version": V3_DIALOGUE["version"], "arguments": {
                       "customer_ref": "customer-1", "dialogue_ref": "dialogue-1", "option_ref": "option-1"},
                   "start_deadline_unix_ms": 105000, "execution_timeout_ms": 5000}
        self.assertTrue(valid_command_v3(v3_args))
        self.assertFalse(valid_command_v3(dict(v3_args, arguments=dict(v3_args["arguments"], surface_ref="surface-1"))))
        self.assertIn("surface", V4_SCHEMA["$defs"]["Snapshot"]["required"])
        self.assertNotIn("surface", V3_SCHEMA["$defs"]["Snapshot"]["properties"])
        self.assertNotIn("surface", V2_SCHEMA["$defs"]["Snapshot"]["properties"])
        v2_schema = Draft202012Validator({**V2_SCHEMA, "$ref": "#/$defs/Snapshot"})
        v3_schema = Draft202012Validator({**V3_SCHEMA, "$ref": "#/$defs/Snapshot"})
        v2_schema.validate(V2_CLOSE["before"])
        v3_schema.validate(V3_DIALOGUE)
        with self.assertRaises(Exception):
            v2_schema.validate({**V2_CLOSE["before"], "surface": surface_for()})
        with self.assertRaises(Exception):
            v3_schema.validate({**V3_DIALOGUE, "surface": surface_for()})

    def test_generic_arguments_bind_surface_control_mode_and_option(self):
        for action in ("advance_dialogue", "choose_dialogue_option", "dismiss_customer"):
            with self.subTest(action=action):
                state = snapshot_v4(action)
                args = generic_arguments(action)
                self.assertTrue(valid_snapshot_v4(state))
                self.assertTrue(valid_action_binding_v4(state, action, args))
                self.assertTrue(valid_action_state_v4(state, action, args))
                self.assertTrue(valid_command_v4(command_v4(state, action)))

        state = snapshot_v4("advance_dialogue")
        bad = copy.deepcopy(state)
        bad["legal_actions"][0]["arguments"]["surface_ref"] = "stale-surface"
        self.assertFalse(valid_snapshot_v4(bad))
        bad = copy.deepcopy(state)
        bad["legal_actions"][0]["arguments"]["control_ref"] = "stale-control"
        self.assertFalse(valid_snapshot_v4(bad))
        bad = copy.deepcopy(state)
        bad["legal_actions"][0]["arguments"]["mode"] = "next"
        self.assertFalse(valid_snapshot_v4(bad))
        bad = copy.deepcopy(state)
        bad["surface"]["controls"][0]["effect_contract"] = "unknown"
        self.assertFalse(valid_snapshot_v4(bad))
        bad = copy.deepcopy(state)
        bad["surface"]["status"] = "progressing"
        self.assertFalse(valid_snapshot_v4(bad))

    def test_v4_surface_business_coherence_fails_closed_but_keeps_incomplete_observations(self):
        bad = snapshot_v4()
        bad["surface"]["customer_ref"] = "customer-2"
        self.assertFalse(valid_snapshot_v4(bad))

        bad = snapshot_v4()
        bad["legal_actions"] = []
        bad["surface"].update(kind="none", phase="absent", status="idle", surface_ref=None,
                              customer_ref=None, dialogue_ref=None, controls=[])
        self.assertFalse(valid_snapshot_v4(bad))

        bad = snapshot_v4()
        bad["business"]["details"]["customer"]["dialogue_ref"] = "dialogue-2"
        self.assertFalse(valid_snapshot_v4(bad))

        bad = snapshot_v4()
        bad["legal_actions"] = []
        bad["surface"].update(kind="dialogue", phase="absent", status="unknown", completeness="unavailable",
                              surface_ref=None, customer_ref=None, dialogue_ref=None, controls=[])
        self.assertFalse(valid_snapshot_v4(bad))

        self.assertTrue(valid_snapshot_v4(snapshot_v4("dismiss_customer")))
        bad = snapshot_v4("dismiss_customer")
        bad["business"]["details"]["customer"]["dialogue_ref"] = "dialogue-1"
        self.assertFalse(valid_snapshot_v4(bad))

        unavailable = snapshot_v4()
        unavailable["legal_actions"] = []
        unavailable["surface"].update(kind="unknown", phase="unknown", status="unknown",
                                       completeness="unavailable", surface_ref=None, customer_ref=None,
                                       dialogue_ref=None, controls=[])
        self.assertTrue(valid_snapshot_v4(unavailable))
        partial = copy.deepcopy(unavailable)
        partial["surface"].update(kind="dialogue", completeness="partial", customer_ref="stale-customer",
                                  dialogue_ref=None)
        self.assertTrue(valid_snapshot_v4(partial))
        not_ready = copy.deepcopy(unavailable)
        not_ready.update(ready=False, not_ready_reason="game_not_ready")
        not_ready["interaction"]["operable"] = False
        self.assertTrue(valid_snapshot_v4(not_ready))

    def test_v4_legacy_route_is_checked_by_v3_policy(self):
        state = copy.deepcopy(V3_DIALOGUE)
        state["schema_version"] = 4
        state["surface"] = surface_for()
        state["surface"].update(kind="unknown", phase="unknown", status="awaiting_agent", completeness="unavailable",
                                 surface_ref=None, customer_ref=None, dialogue_ref=None, controls=[])
        args = {"customer_ref": "customer-1", "dialogue_ref": "dialogue-1", "option_ref": "option-1"}
        state["legal_actions"] = [{"action": "choose_dialogue_option", "arguments": args}]
        self.assertTrue(valid_snapshot_v4(state))
        self.assertTrue(valid_action_binding_v4(state, "choose_dialogue_option", args))
        self.assertTrue(valid_action_state_v4(state, "choose_dialogue_option", args))
        command = command_v4(state, "choose_dialogue_option", args)
        self.assertTrue(valid_command_v4(command))
        self.assertTrue(valid_request_v4({key: command[key] for key in
                                         ("schema_version", "id", "broker_epoch", "action", "expected_world_epoch",
                                          "expected_revision", "expected_version", "arguments")}))
        Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Snapshot"}).validate(state)
        Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Command"}).validate(command)

    def test_generic_success_requires_receipt_dialogue_progress_and_economic_invariants(self):
        before = snapshot_v4()
        command = command_v4(before)
        after = transitioned_after(before)
        receipt = {"input_receipt": "returned", "verification": "confirmed", "effect_contract": "presentation",
                   "observed_effects": ["dialogue_transition"], "unverified_effects": ["presentation"],
                   "cash_delta": 0, "added_item_refs": [], "removed_item_refs": []}
        result = result_for(command, before, after, receipt)
        self.assertTrue(valid_interaction_result_v4(receipt))
        self.assertTrue(valid_result_structure_v4(result, command=command))
        self.assertTrue(valid_success_result_v4(command, result))
        Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Result"}).validate(result)

        no_progress = copy.deepcopy(before)
        no_progress["revision"] += 1
        no_progress["version"] = f'{no_progress["world_epoch"]}:{no_progress["revision"]}'
        self.assertFalse(valid_success_result_v4(command, result_for(command, before, no_progress, receipt)))
        cash_changed = copy.deepcopy(after)
        cash_changed["cash"] += 1
        bad_cash = dict(receipt, cash_delta=1)
        self.assertFalse(valid_success_result_v4(command, result_for(command, before, cash_changed, bad_cash)))
        wrong_partition = dict(receipt, unverified_effects=[])
        self.assertFalse(valid_success_result_v4(command, result_for(command, before, after, wrong_partition)))
        revision_only = {**result, "interaction_result": None}
        self.assertFalse(valid_result_structure_v4(revision_only, command=command))

    def test_preentry_rejection_may_omit_receipt_or_mark_not_invoked(self):
        before = snapshot_v4()
        command = command_v4(before)
        rejected = {"schema_version": 4, "id": command["id"], "broker_epoch": command["broker_epoch"],
                    "status": "rejected", "reason": "precondition", "before": None, "after": None,
                    "execution": progress(command)}
        rejected["execution"].update(stage="completed", may_have_side_effects=False,
                                      started_at_unix_ms=None, execution_deadline_unix_ms=None)
        self.assertTrue(valid_result_structure_v4(rejected, command=command))
        rejected["interaction_result"] = {
            "input_receipt": "not_invoked", "verification": "unknown", "effect_contract": "presentation",
            "observed_effects": [], "unverified_effects": [], "cash_delta": None,
            "added_item_refs": [], "removed_item_refs": [],
        }
        self.assertTrue(valid_interaction_result_v4(rejected["interaction_result"]))
        self.assertTrue(valid_result_structure_v4(rejected, command=command))
        rejected["interaction_result"]["input_receipt"] = "returned"
        self.assertFalse(valid_result_structure_v4(rejected, command=command))

    def test_unknown_receipt_effects_require_dto_proof_and_safe_cash_delta(self):
        progressed = copy.deepcopy(V4_INTERACTION_FIXTURE["cases"]["presentation_confirmed"])
        result = progressed["result"]
        result["status"] = "outcome_unknown"
        result["reason"] = "postcondition_unconfirmed"
        result["execution"]["stage"] = "uncertain"
        result["interaction_result"]["verification"] = "unknown"
        self.assertTrue(valid_result_structure_v4(result, command=progressed["command"]))

        forged_story = copy.deepcopy(result)
        forged_story["interaction_result"]["observed_effects"].append("story_state")
        forged_story["interaction_result"]["unverified_effects"].remove("story_state")
        self.assertFalse(valid_result_structure_v4(forged_story, command=progressed["command"]))

        for effect in ("story_state", "reputation", "day_transition", "external_scope",
                       "inventory_transfer", "customer_departure", "cash"):
            with self.subTest(unproven_effect=effect):
                before = snapshot_v4(effects=["presentation", "dialogue_transition", effect])
                command = command_v4(before)
                after = transitioned_after(before)
                receipt = {
                    "input_receipt": "returned", "verification": "unknown",
                    "effect_contract": "presentation", "observed_effects": [effect],
                    "unverified_effects": [x for x in before["surface"]["controls"][0]["effects"]
                                           if x != effect],
                    "cash_delta": 0, "added_item_refs": [], "removed_item_refs": [],
                }
                outcome = result_for(command, before, after, receipt)
                outcome["status"] = "outcome_unknown"
                outcome["execution"]["stage"] = "uncertain"
                self.assertFalse(valid_result_structure_v4(outcome, command=command))

        departure = copy.deepcopy(V4_INTERACTION_FIXTURE["cases"]["customer_departure_confirmed"])
        departure_result = departure["result"]
        departure_result["status"] = "outcome_unknown"
        departure_result["execution"]["stage"] = "uncertain"
        departure_result["interaction_result"]["verification"] = "unknown"
        self.assertTrue(valid_result_structure_v4(departure_result, command=departure["command"]))

        before = snapshot_v4(effects=["presentation", "dialogue_transition", "inventory_transfer", "story_state"])
        command = command_v4(before)
        after = transitioned_after(before)
        front = next(row for row in after["business"]["details"]["containers"] if row["container"] == "front")
        front["item_refs"] = ["new-document"]
        after["business"]["details"]["items"].append({
            "item_ref": "new-document", "container": "front", "name": "Document", "units": 1,
            "owned": True, "shape_status": "unverified", "width": None, "height": None})
        receipt = {
            "input_receipt": "returned", "verification": "unknown", "effect_contract": "presentation",
            "observed_effects": ["presentation", "dialogue_transition", "inventory_transfer"],
            "unverified_effects": ["story_state"], "cash_delta": 0,
            "added_item_refs": ["new-document"], "removed_item_refs": [],
        }
        outcome = result_for(command, before, after, receipt)
        outcome["status"] = "outcome_unknown"
        outcome["execution"]["stage"] = "uncertain"
        self.assertTrue(valid_result_structure_v4(outcome, command=command))

        before = snapshot_v4()
        command = command_v4(before)
        after = copy.deepcopy(before)
        after["revision"] += 1
        after["version"] = f'{after["world_epoch"]}:{after["revision"]}'
        after["cash"] += 1
        no_progress_receipt = {
            "input_receipt": "returned", "verification": "unknown", "effect_contract": "presentation",
            "observed_effects": ["presentation"], "unverified_effects": ["dialogue_transition"],
            "cash_delta": 1, "added_item_refs": [], "removed_item_refs": [],
        }
        no_progress_result = result_for(command, before, after, no_progress_receipt)
        no_progress_result["status"] = "outcome_unknown"
        no_progress_result["execution"]["stage"] = "uncertain"
        self.assertFalse(valid_result_structure_v4(no_progress_result, command=command))

        for start_cash, end_cash, expected_delta in (
                (100, 101, 1), (-2_147_483_648, 2_147_483_647, None)):
            with self.subTest(start_cash=start_cash, end_cash=end_cash):
                before = snapshot_v4(effects=["presentation", "dialogue_transition", "cash"])
                before["cash"] = start_cash
                command = command_v4(before)
                after = copy.deepcopy(before)
                after["revision"] += 1
                after["version"] = f'{after["world_epoch"]}:{after["revision"]}'
                after["cash"] = end_cash
                receipt = {
                    "input_receipt": "returned", "verification": "unknown",
                    "effect_contract": "presentation", "observed_effects": ["cash"],
                    "unverified_effects": ["presentation", "dialogue_transition"],
                    "cash_delta": expected_delta, "added_item_refs": [], "removed_item_refs": [],
                }
                outcome = result_for(command, before, after, receipt)
                outcome["status"] = "outcome_unknown"
                outcome["execution"]["stage"] = "uncertain"
                self.assertTrue(valid_result_structure_v4(outcome, command=command))

    def test_document_gift_accepts_zero_or_one_new_owned_front_item_only(self):
        effects = ["dialogue_transition", "inventory_transfer", "story_state"]
        before = snapshot_v4("choose_dialogue_option", "document_gift", effects)
        command = command_v4(before)
        after = transitioned_after(before)
        receipt = {"input_receipt": "returned", "verification": "confirmed", "effect_contract": "document_gift",
                   "observed_effects": ["dialogue_transition"],
                   "unverified_effects": ["inventory_transfer", "story_state"], "cash_delta": 0,
                   "added_item_refs": [], "removed_item_refs": []}
        self.assertTrue(valid_success_result_v4(command, result_for(command, before, after, receipt)))

        gifted = transitioned_after(before)
        gifted_front = next(row for row in gifted["business"]["details"]["containers"] if row["container"] == "front")
        gifted_front["item_refs"] = ["gift-1"]
        gifted["business"]["details"]["items"] = [{
            "item_ref": "gift-1", "container": "front", "name": "Document", "units": 1,
            "owned": True, "shape_status": "unverified", "width": None, "height": None}]
        gift_receipt = {**receipt, "observed_effects": ["dialogue_transition", "inventory_transfer"],
                        "unverified_effects": ["story_state"], "added_item_refs": ["gift-1"]}
        self.assertTrue(valid_success_result_v4(command, result_for(command, before, gifted, gift_receipt)))

        wrong_container = copy.deepcopy(gifted)
        item = wrong_container["business"]["details"]["items"][0]
        item["container"] = "player"
        next(row for row in wrong_container["business"]["details"]["containers"] if row["container"] == "front")["item_refs"] = []
        next(row for row in wrong_container["business"]["details"]["containers"] if row["container"] == "player")["item_refs"] = ["gift-1"]
        self.assertFalse(valid_success_result_v4(command, result_for(command, before, wrong_container, gift_receipt)))

    def test_generic_duplicate_semantics_and_schema_validate(self):
        state = snapshot_v4()
        legacy = {"action": "choose_dialogue_option", "arguments": {
            "customer_ref": "customer-1", "dialogue_ref": "dialogue-1", "option_ref": "option-1"}}
        state["legal_actions"].append(legacy)
        self.assertFalse(valid_snapshot_v4(state))

        Draft202012Validator.check_schema(V4_SCHEMA)
        validator = Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Snapshot"})
        valid_state = snapshot_v4()
        validator.validate(valid_state)
        command = command_v4(valid_state)
        command_schema = Draft202012Validator({**V4_SCHEMA, "$ref": "#/$defs/Command"})
        command_schema.validate(command)
        request_keys = (
            "schema_version", "id", "broker_epoch", "action", "expected_world_epoch",
            "expected_revision", "expected_version", "arguments",
        )
        request = {key: command[key] for key in request_keys}
        self.assertLessEqual(len(canonical_request(request)), MAX_V4_MESSAGE_BYTES)
        changed = copy.deepcopy(command)
        changed["arguments"]["surface_ref"] = "surface-2"
        changed_request = {key: changed[key] for key in request_keys}
        self.assertNotEqual(request_fingerprint(request), request_fingerprint(changed_request))


if __name__ == "__main__":
    unittest.main()
