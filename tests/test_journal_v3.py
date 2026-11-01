"""Journal format 2 keeps v1/v2 history strict while adding v3 records."""
import copy
import json
from pathlib import Path
import sys
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.journal import Journal, JournalError
from bridge.protocol import request_fingerprint
from bridge.server import Broker

V2_FIXTURE = json.loads((ROOT / "protocol/fixtures/v2-close-menu.json").read_text(encoding="utf-8"))
V3_FIXTURE = json.loads((ROOT / "protocol/fixtures/v3-business-observation.json").read_text(encoding="utf-8"))


def record_from_fixture(fixture, status="rejected"):
    request = copy.deepcopy(fixture["request"])
    command = copy.deepcopy(fixture["command"])
    version = command["schema_version"]
    result = {"schema_version": version, "id": command["id"], "broker_epoch": command["broker_epoch"],
              "status": "rejected", "reason": "fixture_rejected", "before": None, "after": None,
              "execution": None}
    return {"command_id": command["id"], "request_fingerprint": request_fingerprint(request),
            "request": request, "command": command, "command_status": status, "result": result,
            "delivery_session_id": None, "created_at_unix_ms": 100_000,
            "updated_at_unix_ms": 100_000, "execution": None}


class JournalV3Tests(unittest.TestCase):
    def test_journal_container_accepts_mixed_v2_and_v3_records(self):
        v2 = record_from_fixture(V2_FIXTURE)
        v3 = record_from_fixture(V3_FIXTURE)
        v3["command"]["id"] = v3["request"]["id"] = v3["command_id"] = "fixture-v3-rejected"
        v3["result"]["id"] = "fixture-v3-rejected"
        v3["request_fingerprint"] = request_fingerprint(v3["request"])
        journal = {"journal_format_version": 2, "broker_epoch": "fixture-broker", "records": [v2, v3]}
        self.assertTrue(Journal.valid_v2(journal))
        malformed = copy.deepcopy(journal)
        malformed["records"][1]["command"]["action"] = "purchase_visible_offer"
        malformed["records"][1]["request"]["action"] = "purchase_visible_offer"
        malformed["records"][1]["command"]["arguments"] = malformed["records"][1]["request"]["arguments"] = {
            "customer_ref": "c", "offer_ref": "o", "item_refs": ["x"],
            "collection_fingerprint": "f", "mode": "normal", "amount": 2,
            "max_spend": 3, "authorization_ref": "auth"}
        malformed["records"][1]["request_fingerprint"] = request_fingerprint(malformed["records"][1]["request"])
        malformed["records"][1]["result"]["id"] = "fixture-v3-rejected"
        self.assertFalse(Journal.valid_v2(malformed))

    def test_reader_keeps_v2_record_and_v3_record_versions_separate(self):
        folder = ROOT / ".test-tmp"
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / f"v3-mixed-{uuid.uuid4().hex}.json"
        journal = Journal(path)
        value = {"journal_format_version": 2, "broker_epoch": "fixture-broker",
                 "records": [record_from_fixture(V2_FIXTURE), record_from_fixture(V3_FIXTURE)]}
        value["records"][1]["command"]["id"] = value["records"][1]["request"]["id"] = value["records"][1]["command_id"] = "distinct-v3"
        value["records"][1]["result"]["id"] = "distinct-v3"
        value["records"][1]["request_fingerprint"] = request_fingerprint(value["records"][1]["request"])
        try:
            journal.acquire()
            journal.save(value)
            loaded = journal.load()
            self.assertEqual([r["command"]["schema_version"] for r in loaded["records"]], [2, 3])
            self.assertEqual([r["result"]["schema_version"] for r in loaded["records"]], [2, 3])
            journal.release()
            broker = Broker("mixed-history", journal_path=path)
            try:
                old_v2 = broker.result(value["records"][0]["command_id"])
                old_v3 = broker.result(value["records"][1]["command_id"])
                self.assertEqual((old_v2["schema_version"], old_v2["record_schema_version"],
                                  old_v2["result"]["schema_version"]), (2, 2, 2))
                self.assertEqual((old_v3["schema_version"], old_v3["record_schema_version"],
                                  old_v3["result"]["schema_version"]), (3, 3, 3))
            finally:
                broker.close()
        finally:
            journal.release()
            for candidate in (path, journal.backup_path, journal.lock_path):
                candidate.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
