"""Shared wire fixtures are synthetic; these checks never connect to the game."""
from copy import deepcopy
import json
from pathlib import Path
import unittest

from jsonschema import Draft202012Validator
from bridge.protocol import (canonical_request, request_fingerprint, strict_json_loads,
    valid_arguments, valid_command, valid_snapshot, valid_request, valid_progress,
    valid_progress_transition, progress_matches_command, valid_result_structure,
    valid_success_result)

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = json.loads((ROOT / "protocol/fixtures/v2-close-menu.json").read_text())
SCHEMA = json.loads((ROOT / "protocol/schemas/wire-v2.json").read_text())


class ProtocolV2Tests(unittest.TestCase):
    def test_shared_fixture_and_json_schema(self):
        self.assertEqual(FIXTURE["source"], "fixture")
        Draft202012Validator.check_schema(SCHEMA)
        for key, definition, check in (("before", "Snapshot", valid_snapshot),
                                      ("after", "Snapshot", valid_snapshot),
                                      ("request", "Request", valid_request),
                                      ("command", "Command", valid_command),
                                      ("progress", "Progress", valid_progress),
                                      ("result", "Result", valid_result_structure)):
            with self.subTest(key=key):
                Draft202012Validator({**SCHEMA, "$ref": "#/$defs/" + definition}).validate(FIXTURE[key])
                self.assertTrue(check(FIXTURE[key]))
        self.assertTrue(valid_success_result(FIXTURE["command"], FIXTURE["result"]))

    def test_full_request_canonical_fingerprint(self):
        for case in FIXTURE["fingerprint_cases"]:
            self.assertEqual(canonical_request(case["request"]), case["canonical_json"])
            self.assertEqual(request_fingerprint(case["request"]), case["sha256"])
            reverse_keys = dict(reversed(list(case["request"].items())))
            self.assertEqual(request_fingerprint(reverse_keys), case["sha256"])
        original = deepcopy(FIXTURE["fingerprint_cases"][1]["request"])
        base = request_fingerprint(original)
        for key in original["arguments"]:
            changed = deepcopy(original)
            if key == "item_refs":
                changed["arguments"][key].reverse()
            else:
                changed["arguments"][key] += "x"
            self.assertNotEqual(request_fingerprint(changed), base)
        removed = deepcopy(original)
        del removed["arguments"]["offer_ref"]
        self.assertNotEqual(request_fingerprint(removed), base)

    def test_future_parameter_contract_does_not_enable_commands(self):
        future = FIXTURE["fingerprint_cases"][1]["request"]
        self.assertTrue(valid_request(future))
        self.assertFalse(valid_command({**FIXTURE["command"], "arguments": future["arguments"]}))
        self.assertFalse(valid_command({**FIXTURE["command"], "action": "buy_items"}))
        for args in ({"method": "BuyItem"}, {"customer_ref": None}, {"item_refs": []},
                     {"item_refs": ["a", "a"]}, {"item_refs": ["a"] * 65},
                     {"offer_ref": "x" * 129}, {"option_ref": {"code": "run"}},
                     {"customer_ref": "hidden\nvalue"}, [], True):
            self.assertFalse(valid_arguments(args), repr(args))

    def test_exact_fields_depth_duplicate_keys_and_nonfinite(self):
        for value in (dict(FIXTURE["request"], hidden=True), dict(FIXTURE["before"], true_value=100)):
            self.assertFalse(valid_request(value))
            self.assertFalse(valid_snapshot(value))
        for raw in ('{"x":1,"x":2}', '{"x":NaN}', '[' * 18 + '0' + ']' * 18,
                    '[' * 2000 + '0' + ']' * 2000):
            with self.assertRaises(ValueError):
                strict_json_loads(raw)
        self.assertEqual(strict_json_loads('{"x":[1,null]}'), {"x": [1, None]})

    def test_consistency_and_unsupported_business_are_explicit(self):
        state = deepcopy(FIXTURE["before"])
        self.assertTrue(valid_snapshot(state))
        self.assertEqual(state["interaction"]["consistency"], "unverified")
        state["business"]["item_refs"] = []
        self.assertFalse(valid_snapshot(state))
        state = deepcopy(FIXTURE["before"])
        state["legal_actions"] = []
        self.assertFalse(valid_snapshot(state))
        state = deepcopy(FIXTURE["before"])
        state["interaction"]["consistency"] = "blocked"
        self.assertFalse(valid_snapshot(state))
        for field, value in (("revision", True), ("cash", True), ("schema_version", 1), ("revision", 2**31)):
            self.assertFalse(valid_snapshot(dict(FIXTURE["before"], **{field: value})))

    def test_progress_is_monotonic_and_deadline_immutable(self):
        command = FIXTURE["command"]
        first = dict(FIXTURE["progress"], stage="invoking", sequence=2)
        next_progress = dict(first, stage="verifying", sequence=3)
        self.assertTrue(progress_matches_command(first, command))
        self.assertTrue(valid_progress_transition(first, next_progress))
        self.assertTrue(valid_progress_transition(next_progress, dict(next_progress)))
        self.assertFalse(valid_progress_transition(next_progress, first))
        self.assertFalse(valid_progress_transition(first, dict(next_progress, execution_deadline_unix_ms=105101)))
        self.assertFalse(valid_progress_transition(first, dict(next_progress, sequence=2)))
        self.assertFalse(progress_matches_command(dict(first, started_at_unix_ms=105000,
            execution_deadline_unix_ms=110000), command))

    def test_success_requires_full_menu_evidence_and_no_cash_change(self):
        command, result = deepcopy(FIXTURE["command"]), deepcopy(FIXTURE["result"])
        self.assertTrue(valid_success_result(command, result))
        result["after"]["cash"] -= 1
        self.assertFalse(valid_success_result(command, result))
        rejection = dict(FIXTURE["result"], status="rejected")
        self.assertFalse(valid_result_structure(rejection))
        synthetic = dict(rejection, execution=None, before=None, after=None)
        self.assertFalse(valid_result_structure(synthetic))
        self.assertTrue(valid_result_structure(synthetic, require_execution=False))
        self.assertFalse(valid_result_structure(dict(FIXTURE["result"], execution=None), require_execution=False))


if __name__ == "__main__":
    unittest.main()
