"""Research-only counterexamples for the proposed PurchaseVerifier contract.

These synthetic fixtures do not execute game code or establish Q/C/V gates.
Evidence of invocation, complete coverage and audited callbacks cannot be
manufactured by rereading the current inventory after a lost result.
"""
import copy
import unittest


def classify_purchase(evidence):
    if evidence["invocations"] == 0 and evidence["never_invoked_proven"]:
        return "rejected" if evidence["before"] == evidence["after"] else "outcome_unknown"
    if evidence["invocations"] != 1 or not all(evidence[key] for key in (
        "native_returned", "coverage_complete", "quote_bound", "capacity_proven",
        "callback_scope_proven", "retained_before", "identity_unambiguous",
    )):
        return "outcome_unknown"
    refs = evidence["ordered_items"]
    if not refs or len(set(refs)) != len(refs) or evidence["amount"] <= 0:
        return "outcome_unknown"
    before, after = evidence["before"], evidence["after"]
    if (before["world"], before["customer"]) != (after["world"], after["customer"]):
        return "outcome_unknown"
    if before["cash"] - after["cash"] != evidence["amount"]:
        return "outcome_unknown"
    expected_source = copy.deepcopy(before["source"])
    expected_target = copy.deepcopy(before["target"])
    for ref in evidence["ordered_items"]:
        item = expected_source.pop(ref, None)
        if item is None or item["owned"] or item["units"] != 1 or ref in expected_target:
            return "outcome_unknown"
        expected_target[ref] = {"units": 1, "owned": True}
    return "succeeded" if (after["source"] == expected_source and
                           after["target"] == expected_target) else "outcome_unknown"


class PurchaseEvidenceResearchTests(unittest.TestCase):
    def evidence(self):
        return dict(invocations=1, never_invoked_proven=False, native_returned=True,
                    coverage_complete=True, quote_bound=True, capacity_proven=True,
                    callback_scope_proven=True, retained_before=True, identity_unambiguous=True,
                    amount=20, ordered_items=["fixture-item"],
                    before=dict(world="fixture-world", customer="fixture-customer", cash=100,
                                source={"fixture-item": dict(units=1, owned=False)}, target={}),
                    after=dict(world="fixture-world", customer="fixture-customer", cash=80,
                               source={}, target={"fixture-item": dict(units=1, owned=True)}))

    def test_full_joint_evidence_and_input_immutability(self):
        evidence = self.evidence()
        original = copy.deepcopy(evidence)
        self.assertEqual("succeeded", classify_purchase(evidence))
        self.assertEqual(original, evidence)

    def test_cash_alone_and_owned_but_not_received_are_unknown(self):
        evidence = self.evidence()
        evidence["after"]["target"] = {}
        evidence["after"]["source"] = {"fixture-item": dict(units=1, owned=True)}
        self.assertEqual("outcome_unknown", classify_purchase(evidence))

    def test_extra_item_or_wrong_cash_is_not_complete_success(self):
        for change in ("extra_item", "wrong_cash", "source_still_present"):
            with self.subTest(change=change):
                evidence = self.evidence()
                if change == "extra_item":
                    evidence["after"]["target"]["unexplained"] = dict(units=1, owned=True)
                elif change == "wrong_cash":
                    evidence["after"]["cash"] = 79
                else:
                    evidence["after"]["source"] = copy.deepcopy(evidence["before"]["source"])
                self.assertEqual("outcome_unknown", classify_purchase(evidence))

    def test_split_or_merge_cannot_reuse_simple_identity_verifier(self):
        evidence = self.evidence()
        evidence["after"]["target"] = {"different-ref": dict(units=1, owned=True)}
        self.assertEqual("outcome_unknown", classify_purchase(evidence))

    def test_missing_provenance_cannot_be_recovered_by_matching_current_state(self):
        for key in ("retained_before", "callback_scope_proven", "coverage_complete",
                    "quote_bound", "capacity_proven", "identity_unambiguous", "native_returned"):
            with self.subTest(key=key):
                evidence = self.evidence()
                evidence[key] = False
                self.assertEqual("outcome_unknown", classify_purchase(evidence))

    def test_world_or_customer_change_is_unknown_at_execution_boundary(self):
        for key in ("world", "customer"):
            evidence = self.evidence()
            evidence["after"][key] = "other"
            self.assertEqual("outcome_unknown", classify_purchase(evidence))

    def test_rejected_requires_proof_before_native_entry(self):
        evidence = self.evidence()
        evidence["invocations"] = 0
        self.assertEqual("outcome_unknown", classify_purchase(evidence))
        evidence["never_invoked_proven"] = True
        self.assertEqual("outcome_unknown", classify_purchase(evidence))
        evidence["after"] = copy.deepcopy(evidence["before"])
        self.assertEqual("rejected", classify_purchase(evidence))
        evidence["invocations"] = 1
        evidence["native_returned"] = False
        self.assertEqual("outcome_unknown", classify_purchase(evidence))

    def test_duplicate_native_invocation_never_becomes_success(self):
        evidence = self.evidence()
        evidence["invocations"] = 2
        self.assertEqual("outcome_unknown", classify_purchase(evidence))


if __name__ == "__main__":
    unittest.main()
