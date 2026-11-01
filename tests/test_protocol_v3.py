"""Strict v3 validation while the frozen v2 helpers remain unchanged."""
import copy
import json
from pathlib import Path
import sys
import unittest
from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.protocol import (canonical_request, request_fingerprint, valid_command,
                             valid_command_v3, valid_request, valid_request_v3,
                             valid_snapshot, valid_snapshot_v3, valid_success_result_v3,
                             valid_action_binding_v3, compact_json_size, MAX_V2_MESSAGE_BYTES,
                             MAX_V3_SNAPSHOT_BYTES, MAX_V3_MESSAGE_BYTES, valid_result_structure_v3)


FIXTURE = json.loads((ROOT / "protocol/fixtures/v3-business-observation.json").read_text(encoding="utf-8"))
V2_FIXTURE = json.loads((ROOT / "protocol/fixtures/v2-close-menu.json").read_text(encoding="utf-8"))
V3_DIALOGUE_FIXTURE = json.loads((ROOT / "protocol/fixtures/v3-dialogue-snapshot.json").read_text(encoding="utf-8"))
SCHEMA = json.loads((ROOT / "protocol/schemas/wire-v3.json").read_text(encoding="utf-8"))


class ProtocolV3Tests(unittest.TestCase):
    def test_fixture_is_valid_and_fingerprint_covers_v3_arguments(self):
        self.assertTrue(valid_snapshot_v3(FIXTURE["before"]))
        self.assertTrue(valid_request_v3(FIXTURE["request"]))
        Draft202012Validator({**SCHEMA, "$ref": "#/$defs/Snapshot"}).validate(FIXTURE["before"])
        self.assertTrue(valid_command_v3(FIXTURE["command"]))
        self.assertTrue(valid_success_result_v3(FIXTURE["command"], FIXTURE["result"]))
        self.assertGreaterEqual(len(FIXTURE["fingerprint_cases"]), 3)
        for case in FIXTURE["fingerprint_cases"]:
            self.assertTrue(valid_request_v3(case["request"]))
            self.assertEqual(canonical_request(case["request"]), case["canonical_json"])
            self.assertEqual(request_fingerprint(case["request"]), case["sha256"])
            if case["executable"]:
                self.assertEqual(case["request"]["action"], "close_menu")
            else:
                self.assertNotEqual(case["request"]["action"], "close_menu")

    @staticmethod
    def schema2_dialogue_snapshot():
        state = copy.deepcopy(FIXTURE["before"])
        state.update(phase="OPEN", menu_open=False, can_close_menu=False)
        state["interaction"].update(phase="dialogue", consistency="unverified", operable=True)
        business = state["business"]
        business.update(support="partial", customer_ref="customer-1", item_refs=[],
                        offer_ref=None, collection_fingerprint=None)
        details = business["details"]
        details["schema_version"] = 2
        details["front_collection_fingerprint"] = None
        details["quote"]["wholesale_total"] = None
        details["status"] = "partial"
        details["customer"].update(presence="present", arrived=True, trade_type="sell", phase="dialogue",
            dialogue_ref="dialogue-1", dialogue_stage="choices", option_refs=["option-1"],
            dialogue_text="Would you like to sell?", is_typing=False, waiting_for_advance=None,
            options=[{"option_ref": "option-1", "text": "Yes, please"}])
        state["legal_actions"] = [{"action": "choose_dialogue_option", "arguments": {
            "customer_ref": "customer-1", "dialogue_ref": "dialogue-1", "option_ref": "option-1"}}]
        return state

    def test_business_schema2_exposes_visible_dialogue_and_legal_action(self):
        Draft202012Validator.check_schema(SCHEMA)
        Draft202012Validator({**SCHEMA, "$ref": "#/$defs/Snapshot"}).validate(V3_DIALOGUE_FIXTURE)
        self.assertTrue(valid_snapshot_v3(V3_DIALOGUE_FIXTURE))
        self.assertEqual(V3_DIALOGUE_FIXTURE["business"]["details"]["schema_version"], 2)
        state = self.schema2_dialogue_snapshot()
        self.assertTrue(valid_snapshot_v3(state))
        customer = state["business"]["details"]["customer"]
        self.assertEqual(customer["dialogue_text"], "Would you like to sell?")
        self.assertEqual(customer["options"][0]["text"], "Yes, please")
        self.assertEqual(customer["option_refs"], [row["option_ref"] for row in customer["options"]])
        malformed = copy.deepcopy(state)
        malformed["business"]["details"]["customer"]["option_refs"] = []
        self.assertFalse(valid_snapshot_v3(malformed))

    def test_v2_validation_is_unchanged_and_rejects_v3_fields(self):
        self.assertTrue(valid_snapshot(V2_FIXTURE["before"]))
        self.assertTrue(valid_request(V2_FIXTURE["request"]))
        self.assertFalse(valid_snapshot(FIXTURE["before"]))
        self.assertFalse(valid_request(FIXTURE["request"]))
        self.assertTrue(valid_command(V2_FIXTURE["command"]))
        self.assertFalse(valid_command(FIXTURE["command"]))

    def test_schema1_business_extension_stays_strict_and_candidates_do_not_enable_purchase(self):
        malformed = copy.deepcopy(FIXTURE["before"])
        malformed["business"]["details"]["customer"]["surprise"] = True
        self.assertFalse(valid_snapshot_v3(malformed))
        malformed = copy.deepcopy(FIXTURE["before"])
        malformed["business"]["details"]["eligibility"]["purchase_enabled"] = True
        self.assertFalse(valid_snapshot_v3(malformed))
        malformed = copy.deepcopy(FIXTURE["before"])
        malformed["business"]["details"]["candidates"] = [
            {"action": "purchase_visible_offer", "status": "candidate", "reason": "visible"}]
        self.assertTrue(valid_snapshot_v3(malformed))  # candidate is descriptive only
        malformed["legal_actions"] = [{"action": "purchase_visible_offer", "arguments": {}}]
        self.assertFalse(valid_snapshot_v3(malformed))

    def test_action_reference_binding_is_snapshot_local(self):
        state = copy.deepcopy(FIXTURE["before"])
        business = state["business"]
        business["support"] = "partial"
        business["customer_ref"] = "cust-1"
        business["item_refs"] = ["item-1"]
        business["collection_fingerprint"] = "collection-1"
        details = business["details"]
        details["schema_version"] = 2
        details["front_collection_fingerprint"] = "front-1"
        details["quote"]["wholesale_total"] = None
        details["status"] = "partial"
        details["customer"].update(presence="present", trade_type="sell", dialogue_ref="dialogue-1", dialogue_stage="choices",
                                    option_refs=["option-1"], dialogue_text="visible line", is_typing=False,
                                    waiting_for_advance=False, options=[{"option_ref": "option-1", "text": "Sell"}])
        details["containers"] = [
            {"container": "front", "completeness": "complete", "reason": None,
             "item_refs": ["front-item"]},
            {"container": "back", "completeness": "complete", "reason": None,
             "item_refs": ["item-1"]}]
        details["items"] = [
            {"item_ref": "front-item", "container": "front", "name": "Front Item", "units": 1,
             "owned": True, "shape_status": "unverified", "width": None, "height": None},
            {"item_ref": "item-1", "container": "back", "name": "Item", "units": 1,
             "owned": False, "shape_status": "unverified", "width": None, "height": None}]
        details["quote"].update(status="unverified", rows=[{"item_ref": "item-1", "name": "Item",
                                  "price_text": None, "amount": None}])
        args = {"customer_ref": "cust-1", "item_refs": ["front-item"], "collection_fingerprint": "front-1"}
        self.assertTrue(valid_snapshot_v3(state))
        self.assertTrue(valid_action_binding_v3(business, "prepare_purchase_items", args))
        changed = dict(args, item_refs=["different-item"])
        self.assertFalse(valid_action_binding_v3(business, "prepare_purchase_items", changed))
        state.update(menu_open=False, can_close_menu=False)
        state["legal_actions"] = [{"action": "prepare_purchase_items", "arguments": args}]
        self.assertTrue(valid_snapshot_v3(state))  # exact action legalizes only this front-set transfer

    def test_complete_quote_requires_verified_full_back_collection_and_total(self):
        state = copy.deepcopy(FIXTURE["before"])
        business = state["business"]
        business.update(support="available", customer_ref="cust-1", item_refs=["item-1"],
                        offer_ref="offer-1", collection_fingerprint="collection-1")
        details = business["details"]
        details.update(status="complete", customer={"presence": "present", "arrived": True,
            "trade_type": "sell", "phase": "trading", "dialogue_ref": None,
            "dialogue_stage": "unknown", "option_refs": []},
            containers=[{"container": "back", "completeness": "complete", "reason": None,
                         "item_refs": ["item-1"]}],
            items=[{"item_ref": "item-1", "container": "back", "name": "Item", "units": 1,
                    "owned": False, "shape_status": "unverified", "width": None, "height": None}],
            quote={"status": "complete", "mode": "normal", "displayed_total": 10,
                   "rows": [{"item_ref": "item-1", "name": "Item", "price_text": "$10", "amount": 10}],
                   "reason": None},
            ui={"status": "consistent", "blockers": [], "coverage": "verified"})
        self.assertTrue(valid_snapshot_v3(state))
        bad_total = copy.deepcopy(state)
        bad_total["business"]["details"]["quote"]["displayed_total"] = 11
        self.assertFalse(valid_snapshot_v3(bad_total))
        bad_offer = copy.deepcopy(state)
        bad_offer["business"]["offer_ref"] = None
        self.assertFalse(valid_snapshot_v3(bad_offer))

    def test_schema2_purchase_quote_binds_normal_total_to_non_owned_back_subset(self):
        state = copy.deepcopy(self.schema2_dialogue_snapshot())
        business = state["business"]
        business.update(support="available", item_refs=["npc-item"], offer_ref="offer-1",
                        collection_fingerprint="buy-set-1")
        details = business["details"]
        details.update(status="partial", front_collection_fingerprint=None)
        details["customer"].update(phase="trading", dialogue_ref=None, dialogue_stage="unknown",
            option_refs=[], dialogue_text=None, is_typing=None, waiting_for_advance=None, options=[])
        details["containers"] = [
            {"container":"front","completeness":"unavailable","reason":"front root not present","item_refs":[]},
            {"container":"back","completeness":"complete","reason":None,
             "item_refs":["npc-item","npc-other","owned-item"]},
            {"container":"player","completeness":"complete","reason":None,"item_refs":[]}]
        details["items"] = [
            {"item_ref":"npc-item","container":"back","name":"Visible Item","units":1,
             "owned":False,"shape_status":"unverified","width":None,"height":None},
            {"item_ref":"npc-other","container":"back","name":"Other Item","units":1,
             "owned":False,"shape_status":"unverified","width":None,"height":None},
            {"item_ref":"owned-item","container":"back","name":"Player Item","units":1,
             "owned":True,"shape_status":"unverified","width":None,"height":None}]
        details["quote"] = {"status":"complete","mode":"normal","displayed_total":10,
            "wholesale_total":25,"rows":[{"item_ref":None,"name":"Visible Item",
            "price_text":"$10","amount":None}],"reason":None}
        details["capacity"] = {"status":"sufficient","reason":"normal_grid_fit","scope":"current_purchase_subset"}
        details["eligibility"] = {"purchase_enabled":True,"missing":[]}
        state["legal_actions"] = [{"action":"purchase_visible_offer","arguments":{
            "customer_ref":"customer-1","item_refs":["npc-item"],"offer_ref":"offer-1",
            "collection_fingerprint":"buy-set-1","mode":"normal","amount":10,"max_spend":10}}]
        self.assertTrue(valid_snapshot_v3(state))
        missing_legal_action = copy.deepcopy(state)
        missing_legal_action["legal_actions"] = []
        self.assertFalse(valid_snapshot_v3(missing_legal_action))
        action = copy.deepcopy(state["legal_actions"][0])
        tampered = copy.deepcopy(state)
        tampered["business"]["item_refs"].append("owned-item")
        self.assertFalse(valid_snapshot_v3(tampered))
        tampered = copy.deepcopy(state)
        tampered["legal_actions"][0]["arguments"]["amount"] = 25
        self.assertFalse(valid_snapshot_v3(tampered))
        self.assertEqual(action["arguments"]["amount"], details["quote"]["displayed_total"])

        request = {"schema_version":3,"id":"buy-once","broker_epoch":"broker-1",
            "action":"purchase_visible_offer","arguments":action["arguments"],
            "expected_world_epoch":state["world_epoch"],"expected_revision":state["revision"],
            "expected_version":state["version"],"start_deadline_unix_ms":105000,"execution_timeout_ms":5000}
        after = copy.deepcopy(state)
        after.update(revision=2, version=f"{state['world_epoch']}:2", cash=540, legal_actions=[])
        post_business = after["business"]
        post_business.update(support="partial", customer_ref=None, item_refs=[], offer_ref=None,
                             collection_fingerprint=None)
        post_details = post_business["details"]
        post_details["status"] = "partial"
        post_details["front_collection_fingerprint"] = None
        post_details["customer"].update(presence="absent", arrived=None, trade_type="unknown", phase="leaving",
            dialogue_ref=None, dialogue_stage="absent", option_refs=[], dialogue_text=None,
            is_typing=None, waiting_for_advance=None, options=[])
        post_details["containers"] = [
            {"container":"back","completeness":"complete","reason":None,
             "item_refs":["npc-other","owned-item"]},
            {"container":"player","completeness":"complete","reason":None,"item_refs":["new-player-item"]}]
        post_details["items"] = [
            {"item_ref":"npc-other","container":"back","name":"Other Item","units":1,
             "owned":False,"shape_status":"unverified","width":None,"height":None},
            {"item_ref":"owned-item","container":"back","name":"Player Item","units":1,
             "owned":True,"shape_status":"unverified","width":None,"height":None},
            {"item_ref":"new-player-item","container":"player","name":"Visible Item","units":1,
             "owned":True,"shape_status":"unverified","width":None,"height":None}]
        post_details["quote"] = {"status":"unavailable","mode":"unknown","displayed_total":None,
            "wholesale_total":None,"rows":[],"reason":"customer_left"}
        post_details["capacity"] = {"status":"unverified","reason":"native_rules_unverified","scope":"none"}
        post_details["eligibility"] = {"purchase_enabled":False,"missing":["quote_binding"]}
        self.assertTrue(valid_snapshot_v3(after))
        progress = {"id":"buy-once","broker_epoch":"broker-1","stage":"completed","sequence":4,
            "may_have_side_effects":True,"started_at_unix_ms":100100,"execution_deadline_unix_ms":105100}
        result = {"schema_version":3,"id":"buy-once","broker_epoch":"broker-1","status":"succeeded",
            "reason":"normal_trade_verified","before":state,"after":after,"execution":progress}
        self.assertTrue(valid_success_result_v3(request,result))

    def test_dismiss_success_requires_customer_absent_or_a_distinct_replacement(self):
        before = self.schema2_dialogue_snapshot()
        self.assertTrue(valid_snapshot_v3(before))

        def after_state(replacement=False, leaving_same_customer=False):
            after = copy.deepcopy(before)
            after["revision"] = before["revision"] + 1
            after["version"] = f'{after["world_epoch"]}:{after["revision"]}'
            after["legal_actions"] = []
            after["interaction"].update(phase="idle", reason="customer_departed")
            business = after["business"]
            details = business["details"]
            customer = details["customer"]
            if replacement:
                business["customer_ref"] = "customer-2"
                customer.update(presence="present", arrived=False, trade_type="unknown", phase="arriving",
                    dialogue_ref=None, dialogue_stage="unknown", option_refs=[], dialogue_text=None,
                    is_typing=None, waiting_for_advance=None, options=[])
            elif leaving_same_customer:
                customer["phase"] = "leaving"
            else:
                business.update(customer_ref=None, item_refs=[], offer_ref=None, collection_fingerprint=None)
                customer.update(presence="absent", arrived=None, trade_type="unknown", phase="leaving",
                    dialogue_ref=None, dialogue_stage="absent", option_refs=[], dialogue_text=None,
                    is_typing=None, waiting_for_advance=None, options=[])
            return after

        command = copy.deepcopy(FIXTURE["command"])
        command.update(action="dismiss_customer", arguments={"customer_ref": "customer-1"})
        for after in (after_state(), after_state(replacement=True)):
            self.assertTrue(valid_snapshot_v3(after))
            result = copy.deepcopy(FIXTURE["result"])
            result["before"], result["after"] = before, after
            self.assertTrue(valid_success_result_v3(command, result))

        unchanged_customer = after_state(leaving_same_customer=True)
        self.assertTrue(valid_snapshot_v3(unchanged_customer))
        result = copy.deepcopy(FIXTURE["result"])
        result["before"], result["after"] = before, unchanged_customer
        self.assertFalse(valid_success_result_v3(command, result))

    @staticmethod
    def sized_snapshot(full=False, revision=1, menu_open=True):
        state = copy.deepcopy(FIXTURE["before"])
        state.update(revision=revision, version=f"fixture-world:{revision}", menu_open=menu_open,
                     can_close_menu=menu_open,
                     legal_actions=([{"action": "close_menu", "arguments": {}}] if menu_open else []))
        business = state["business"]
        details = business["details"]
        details["candidates"] = [{"action": "start_business", "status": "candidate", "reason": str(i).ljust(256, "x")}
                                 for i in range(64)]
        details["ui"] = {"status": "unverified", "blockers": [str(i).ljust(256, "b") for i in range(64)],
                         "coverage": "known_roots_only"}
        details["eligibility"]["missing"] = [str(i).ljust(256, "m") for i in range(64)]
        if full:
            refs = [f"item-{i:02d}" for i in range(64)]
            business["support"] = "partial"
            business["item_refs"] = refs
            details["status"] = "partial"
            details["containers"] = [{"container": "back", "completeness": "incomplete",
                "reason": "container was only partly observed", "item_refs": refs}]
            details["items"] = [{"item_ref": ref, "container": "back", "name": "i" * 256, "units": 1,
                "owned": False, "shape_status": "unverified", "width": None, "height": None} for ref in refs]
            details["quote"] = {"status": "unverified", "mode": "normal", "displayed_total": None,
                "rows": [{"item_ref": ref, "name": "n" * 256, "price_text": "p" * 256, "amount": None}
                         for ref in refs], "reason": "q" * 256}
        return state

    def test_snapshot_and_aggregate_message_budgets_are_independent(self):
        full = self.sized_snapshot(full=True)
        self.assertGreater(compact_json_size(full), MAX_V3_SNAPSHOT_BYTES)
        self.assertLess(MAX_V3_SNAPSHOT_BYTES, MAX_V3_MESSAGE_BYTES)
        self.assertFalse(valid_snapshot_v3(full))

        before = self.sized_snapshot(revision=1, menu_open=True)
        after = self.sized_snapshot(revision=2, menu_open=False)
        self.assertLessEqual(compact_json_size(before), MAX_V3_SNAPSHOT_BYTES)
        self.assertLessEqual(compact_json_size(after), MAX_V3_SNAPSHOT_BYTES)
        result = copy.deepcopy(FIXTURE["result"])
        result["before"], result["after"] = before, after
        self.assertGreater(compact_json_size(result), MAX_V2_MESSAGE_BYTES)
        self.assertLessEqual(compact_json_size(result), MAX_V3_MESSAGE_BYTES)
        self.assertTrue(valid_result_structure_v3(result))
        self.assertTrue(valid_success_result_v3(FIXTURE["command"], result))

    def test_unverified_customer_state_cannot_retain_decision_references(self):
        state = copy.deepcopy(FIXTURE["before"])
        state["business"]["customer_ref"] = "stale-customer"
        self.assertFalse(valid_snapshot_v3(state))
        state = copy.deepcopy(FIXTURE["before"])
        state["business"]["collection_fingerprint"] = "stale-collection"
        self.assertFalse(valid_snapshot_v3(state))

    def test_v3_argument_shape_rejects_null_duplicate_and_unregistered_action(self):
        request = copy.deepcopy(FIXTURE["request"])
        request["action"] = "advance_dialogue"
        request["arguments"] = {"customer_ref": "c", "dialogue_ref": "d", "mode": "skip"}
        self.assertTrue(valid_request_v3(request))
        self.assertFalse(valid_request_v3({**request, "arguments": {**request["arguments"], "mode": None}}))
        legacy_closed_args = {**request, "action": "close_menu", "arguments": {"customer_ref": "c"}}
        self.assertTrue(valid_request_v3(legacy_closed_args))
        self.assertFalse(valid_command_v3({**legacy_closed_args, "start_deadline_unix_ms": 100,
                                           "execution_timeout_ms": 1000}))
        self.assertTrue(valid_request_v3({**request, "action": "future_action", "arguments": {}}))
        purchase = {**request, "action": "purchase_visible_offer", "arguments": {
            "customer_ref": "c", "offer_ref": "o", "item_refs": ["x"],
            "collection_fingerprint": "f", "mode": "normal", "amount": 2,
            "max_spend": 3, "authorization_ref": "auth"}}
        self.assertTrue(valid_request_v3(purchase))  # generic shape is fingerprinted before allowlist rejection
        self.assertFalse(valid_command_v3({**purchase, "start_deadline_unix_ms": 100,
                                           "execution_timeout_ms": 1000}))
        request["action"] = "prepare_purchase_items"
        request["arguments"] = {"customer_ref": "c", "item_refs": ["x", "x"], "collection_fingerprint": "f"}
        self.assertFalse(valid_request_v3(request))


if __name__ == "__main__":
    unittest.main()
