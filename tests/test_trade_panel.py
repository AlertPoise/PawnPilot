"""Panel opening binds a current customer and proves rendered mode/quote, without a trade."""
import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator
from bridge.protocol import valid_snapshot_v3, valid_action_success_evidence_v3
from pawnpilot_mcp.server import LegalActionV3

ROOT = Path(__file__).resolve().parents[1]


def state(direction="buy", panel="closed", revision=1):
    value = json.loads((ROOT / "protocol/fixtures/m3a-prepare_sale.json").read_text(encoding="utf8"))
    value["revision"] = revision
    value["version"] = f"{value['world_epoch']}:{revision}"
    customer = value["business"]["details"]["customer"]
    customer.update(trade_type=direction, trade_panel=panel, phase="unknown" if panel == "closed" else "trading",
                    dialogue_ref=None, is_typing=None, waiting_for_advance=None, dialogue_stage="absent")
    value["legal_actions"] = ([{"action":"open_trade_panel", "arguments":{"customer_ref":value["business"]["customer_ref"]}}]
                              if panel == "closed" else [])
    return value


class TradePanelTests(unittest.TestCase):
    def test_sell_and_buy_panel_admission_schema_and_mcp(self):
        schema = json.loads((ROOT / "protocol/schemas/wire-v3.json").read_text(encoding="utf8"))
        validator = Draft202012Validator({**schema, "$ref":"#/$defs/Snapshot"})
        for direction in ("buy", "sell"):
            value = state(direction)
            self.assertTrue(valid_snapshot_v3(value))
            validator.validate(value)
            LegalActionV3.model_validate(value["legal_actions"][0])

    def test_old_schema2_without_panel_stays_readable(self):
        value = state()
        value["legal_actions"] = []
        del value["business"]["details"]["customer"]["trade_panel"]
        self.assertTrue(valid_snapshot_v3(value))
        value["business"]["details"]["customer"]["trade_panel"] = None
        self.assertTrue(valid_snapshot_v3(value))

    def test_stale_customer_dialogue_arrival_and_reopen_blocked(self):
        for changes in ({"trade_panel":"buy"}, {"arrived":False}, {"trade_type":"other"},
                        {"dialogue_ref":"dialogue-active"}, {"is_typing":True}):
            value = state()
            value["business"]["details"]["customer"].update(changes)
            self.assertFalse(valid_snapshot_v3(value))
        value = state()
        value["legal_actions"][0]["arguments"]["customer_ref"] = "another-customer"
        self.assertFalse(valid_snapshot_v3(value))

    def test_open_proves_correct_mode_and_visible_quote(self):
        for direction in ("buy", "sell"):
            before, after = state(direction), state(direction, direction, 2)
            quote = after["business"]["details"]["quote"]
            quote.update(mode="normal", displayed_total=0)
            command = before["legal_actions"][0]
            self.assertTrue(valid_action_success_evidence_v3(command, before, after))
            for change in ("wrong_mode", "wrong_customer", "no_quote", "cash_changed"):
                bad = copy.deepcopy(after)
                if change == "wrong_mode": bad["business"]["details"]["customer"]["trade_panel"] = "unavailable"
                if change == "wrong_customer": bad["business"]["customer_ref"] = "other-customer"
                if change == "no_quote": bad["business"]["details"]["quote"]["displayed_total"] = None
                if change == "cash_changed": bad["cash"] += 1
                self.assertFalse(valid_action_success_evidence_v3(command, before, bad))
