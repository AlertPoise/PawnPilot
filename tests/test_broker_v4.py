"""Wire-v4 broker dispatch, receipt verification and journal preservation."""
import copy
import unittest

from bridge.journal import Journal
from bridge.protocol import request_fingerprint, valid_result_structure_v4
from bridge.server import Broker
from tests.test_protocol_v4 import command_v4, progress, result_for, snapshot_v4, transitioned_after


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now

    def monotonic(self):
        return self.now


class BrokerV4Tests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.broker = Broker("v4-test", clock=self.clock, monotonic=self.clock.monotonic,
                             heartbeat_timeout=60)
        self.session = "v4-session"
        self.state = snapshot_v4()
        self.exchange()

    def tearDown(self):
        self.broker.close()

    def exchange(self, state=None, result=None, progress_value=None):
        return self.broker.exchange({
            "schema_version": 4,
            "session_id": self.session,
            "broker_epoch": self.broker.broker_epoch,
            "state": self.state if state is None else state,
            "has_result": result is not None,
            "result": result,
            "progress": progress_value,
        })

    def request(self, command_id="v4-interaction"):
        request = {key: value for key, value in command_v4(self.state, command_id=command_id).items()
                   if key not in {"start_deadline_unix_ms", "execution_timeout_ms"}}
        request["broker_epoch"] = self.broker.broker_epoch
        return request

    def test_generic_dispatch_requires_verified_receipt_and_persists_v4_record(self):
        request = self.request()
        queued = self.broker.enqueue(request)
        self.assertEqual((queued["schema_version"], queued["record_schema_version"]), (4, 4))
        delivered = self.exchange()
        command = delivered["command"]
        self.assertEqual(command["schema_version"], 4)

        after = transitioned_after(self.state)
        receipt = {"input_receipt": "returned", "verification": "confirmed",
                   "effect_contract": "presentation", "observed_effects": ["dialogue_transition"],
                   "unverified_effects": ["presentation"], "cash_delta": 0,
                   "added_item_refs": [], "removed_item_refs": []}
        result = result_for(command, self.state, after, receipt)
        ack = self.exchange(after, result, progress(command))
        self.assertEqual(ack["acknowledged_result_id"], command["id"])
        persisted = self.broker.records[command["id"]]
        self.assertTrue(Journal._valid_v4_record(copy.deepcopy(persisted)))
        self.assertEqual(persisted["request_fingerprint"], request_fingerprint(request))
        self.assertTrue(valid_result_structure_v4(persisted["result"], command=command))
        self.assertTrue(Journal.valid_v2({"journal_format_version": 2,
                                          "broker_epoch": self.broker.broker_epoch,
                                          "records": [copy.deepcopy(persisted)]}))
        history = self.broker.result(command["id"])
        self.assertEqual((history["schema_version"], history["record_schema_version"],
                          history["result"]["schema_version"]), (4, 4, 4))

        forged = copy.deepcopy(persisted)
        forged["result"]["interaction_result"]["verification"] = "unknown"
        self.assertFalse(Journal._valid_v4_record(forged))

    def test_outcome_unknown_keeps_an_explicit_unknown_receipt(self):
        request = self.request("v4-unknown")
        self.broker.enqueue(request)
        self.exchange()
        record = self.broker.records[request["id"]]
        self.broker._terminal(record, "outcome_unknown", "execution_deadline_elapsed")
        result = self.broker.result(request["id"])["result"]
        self.assertEqual(result["interaction_result"]["input_receipt"], "unknown")
        self.assertEqual(result["interaction_result"]["verification"], "unknown")
        self.assertTrue(valid_result_structure_v4(result, require_execution=False,
                                                  command=record["command"]))
        self.assertTrue(Journal._valid_v4_record(copy.deepcopy(record)))

    def test_v4_legacy_variant_keeps_the_v3_policy_route(self):
        legacy = copy.deepcopy(self.state)
        legacy["legal_actions"] = [{"action": "choose_dialogue_option", "arguments": {
            "customer_ref": "customer-1", "dialogue_ref": "dialogue-1", "option_ref": "option-1"}}]
        from bridge.protocol import valid_snapshot_v4
        self.assertTrue(valid_snapshot_v4(legacy))
        self.state = legacy
        self.exchange(legacy)
        request = self.request("v4-legacy")
        request["arguments"] = copy.deepcopy(legacy["legal_actions"][0]["arguments"])
        queued = self.broker.enqueue(request)
        self.assertEqual(queued["record_schema_version"], 4)
        self.assertEqual(self.exchange(legacy)["command"]["arguments"], request["arguments"])


if __name__ == "__main__":
    unittest.main()
