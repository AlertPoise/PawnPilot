"""Supervisor/RunStore/Broker delivery handoff without a model or live game."""
from __future__ import annotations

from pathlib import Path
import shutil
import unittest
import uuid

from bridge.run_store import RunStore
from bridge.server import Broker
from pawnpilot_supervisor.runtime import Supervisor
from tests.test_protocol_v4 import command_v4, progress, result_for, snapshot_v4, transitioned_after


LIMITS = {"customers": 10, "writes": 10, "trades": 10, "buy_spend": 1000,
          "bargains": 10, "day_fees": 1000, "days": 1}
ROOT = Path(__file__).resolve().parents[1]
TEST_ROOT = ROOT / "artifacts" / "m5c"


def managed_snapshot():
    state = snapshot_v4()
    state["business"]["details"]["day"] = {
        "day_number": 1,
        "remaining_customers": 2,
        "stage": "open",
        "reason": None,
        "can_close": False,
        "can_sleep": False,
        "can_confirm": False,
        "report_open": False,
        "transition_pending": False,
        "settlement": None,
    }
    return state


class DeferredModReader:
    """The fixture Mod waits until status polling to collect/deliver Broker work."""

    def __init__(self, broker, state, session_id):
        self.broker, self.state, self.session_id = broker, state, session_id
        self.delivered_ids = []

    def observe(self):
        return self.broker.snapshot()

    def status(self, command_id):
        record = self.broker.records[command_id]
        if record["command_status"] == "pending":
            delivery = self.broker.exchange({
                "schema_version": 4,
                "session_id": self.session_id,
                "broker_epoch": self.broker.broker_epoch,
                "state": self.state,
                "has_result": False,
                "result": None,
                "progress": None,
            })
            if delivery["has_command"]:
                command = delivery["command"]
                self.delivered_ids.append(command["id"])
                after = transitioned_after(self.state)
                receipt = {"input_receipt": "returned", "verification": "confirmed",
                           "effect_contract": "presentation", "observed_effects": ["dialogue_transition"],
                           "unverified_effects": ["presentation"], "cash_delta": 0,
                           "added_item_refs": [], "removed_item_refs": []}
                result = result_for(command, self.state, after, receipt)
                self.state = after
                self.broker.exchange({
                    "schema_version": 4,
                    "session_id": self.session_id,
                    "broker_epoch": self.broker.broker_epoch,
                    "state": after,
                    "has_result": True,
                    "result": result,
                    "progress": progress(command),
                })
        return self.broker.result(command_id)


class DeferredModDriver:
    timeout_seconds = 10

    def __init__(self, broker, state):
        self.broker, self.state = broker, state
        self.command_id = "supervisor-deferred-command"
        self.status_at_return = None

    def run(self, _prompt, token, _cancelled):
        request = {key: value for key, value in command_v4(
            self.state, command_id=self.command_id).items()
                   if key not in {"start_deadline_unix_ms", "execution_timeout_ms"}}
        request["broker_epoch"] = self.broker.broker_epoch
        reply = self.broker.enqueue(request, dispatch_token=token)
        self.status_at_return = reply["command_status"]
        return {"status": "completed", "answer": {
            "plan": "Wait for the next fresh observation.",
            "summary": "One authorized action was submitted.",
            "disposition": "acted",
        }}


class RunSupervisorIntegrationTests(unittest.TestCase):
    def test_pending_command_remains_deliverable_until_supervisor_settles_it(self):
        session_id = "supervisor-mod-session"
        state = managed_snapshot()
        root = TEST_ROOT / ("test-run-supervisor-" + uuid.uuid4().hex)
        root.mkdir(parents=True, exist_ok=False)
        broker = None
        try:
            journal_path = root / "broker-journal.json"
            run_path = root / "run-context.json"
            store = RunStore(run_path)
            store.create("supervisor-integration-run", "fixture-save", str(journal_path),
                         state["world_epoch"], session_id, 1, 2, dict(LIMITS),
                         ["choose_dialogue_option"], "Finish the authorized day.")
            broker = Broker("supervisor-test", heartbeat_timeout=60,
                            journal_path=journal_path, run_path=run_path)
            try:
                broker.exchange({"schema_version": 4, "session_id": session_id,
                                 "broker_epoch": broker.broker_epoch, "state": state,
                                 "has_result": False, "result": None, "progress": None})
                broker.snapshot()
                context = store.snapshot()
                store.control("resume", context["revision"])
                broker.snapshot()

                reader = DeferredModReader(broker, state, session_id)
                driver = DeferredModDriver(broker, state)
                result = Supervisor(store, reader, driver, sleep=lambda _: None,
                                    poll_seconds=.01, result_timeout=1, max_turns=2).run()

                self.assertEqual(driver.status_at_return, "pending",
                                 "Driver returns before the fixture Mod performs its next poll")
                self.assertEqual(reader.delivered_ids, [driver.command_id])
                self.assertEqual(broker.result(driver.command_id)["command_status"], "succeeded")
                managed = next(item for item in store.snapshot()["commands"]
                               if item["command_id"] == driver.command_id)
                self.assertEqual(managed["journal_status"], "succeeded")
                self.assertEqual(store.snapshot()["budget"]["writes"], 1)
                self.assertNotEqual(result["status"], "completed")
            finally:
                if broker is not None:
                    broker.close()
                shutil.rmtree(root, ignore_errors=True)
        finally:
            # Also clean up if setup failed before the Broker-specific finally.
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
