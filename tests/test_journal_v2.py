"""Journal-format migration and mixed-history integrity tests."""
import hashlib
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.journal import Journal, JournalError, MAX_JOURNAL_BYTES
from bridge.protocol import request_fingerprint


def v1_record(command_id="old-command", status="pending"):
    request = {"schema_version": 1, "id": command_id, "broker_epoch": "old-broker",
               "action": "close_menu", "expected_world_epoch": "old-world",
               "expected_revision": 8, "expected_version": "old-world:8"}
    fingerprint = hashlib.sha256(json.dumps(request, sort_keys=True, separators=(",", ":"),
                                                ensure_ascii=False).encode("utf-8")).hexdigest()
    command = {"schema_version": 1, "id": command_id, "action": "close_menu",
               "broker_epoch": "old-broker", "expected_world_epoch": "old-world",
               "expected_revision": 8, "expected_version": "old-world:8",
               "expires_at_unix_ms": 12_000}
    return {"command_id": command_id, "request_fingerprint": fingerprint, "request": request,
            "command": command, "command_status": status, "result": None,
            "delivery_session_id": "mod-session" if status in
            {"running", "outcome_unknown", "succeeded", "failed"} else None,
            "created_at_unix_ms": 10_000, "updated_at_unix_ms": 10_000}


def v1_journal(records=None):
    return {"schema_version": 1, "broker_epoch": "old-broker", "records": records or []}


def v2_snapshot(revision=8, menu_open=True):
    return {"schema_version": 2, "version": f"new-world:{revision}", "world_epoch": "new-world",
            "revision": revision, "phase": "MORNING", "scene": "InventoryScene",
            "loaded": True, "ready": True, "not_ready_reason": None, "cash": 100,
            "menu_open": menu_open, "can_close_menu": menu_open,
            "interaction": {"phase": "store", "consistency": "unverified", "reason": None,
                            "operable": True},
            "business": {"support": "unsupported", "customer_ref": None, "item_refs": None,
                          "offer_ref": None, "collection_fingerprint": None},
            "legal_actions": ([{"action": "close_menu", "arguments": {}}] if menu_open else [])}


def v2_record(command_id="new-command", status="pending", resolution=None):
    request = {"schema_version": 2, "id": command_id, "broker_epoch": "new-broker",
               "action": "close_menu", "expected_world_epoch": "new-world",
               "expected_revision": 8, "expected_version": "new-world:8", "arguments": {}}
    command = {**request, "start_deadline_unix_ms": 20_000, "execution_timeout_ms": 5_000}
    result = None
    delivery_session = None
    updated = 10_000
    if status == "outcome_unknown":
        result = {"schema_version": 2, "id": command_id, "broker_epoch": "new-broker",
                  "status": "outcome_unknown", "reason": "broker_restart_during_execution",
                  "before": None, "after": None, "execution": None}
        delivery_session = "old-mod-session"
        updated = 26_000
    record = {"command_id": command_id, "request_fingerprint": request_fingerprint(request),
              "request": request, "command": command, "command_status": status,
              "result": result, "delivery_session_id": delivery_session,
              "created_at_unix_ms": 10_000, "updated_at_unix_ms": updated, "execution": None}
    if resolution is not None:
        record["resolution"] = resolution
    return record


class JournalV2Tests(unittest.TestCase):
    def setUp(self):
        self.temp_root = ROOT / ".test-tmp"
        self.temp_root.mkdir(parents=True, exist_ok=True)
        self.path = self.temp_root / f"m2b-journal-{uuid.uuid4().hex}.json"
        self.journal = Journal(self.path)

    def tearDown(self):
        self.journal.release()
        for path in (self.path, self.journal.backup_path, self.journal.lock_path):
            path.unlink(missing_ok=True)
        for path in self.temp_root.glob(self.path.name + ".*.tmp"):
            path.unlink(missing_ok=True)

    def write_json(self, value):
        raw = json.dumps(value, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        self.path.write_bytes(raw)
        return raw

    def test_v1_migrates_only_after_exact_backup_and_keeps_records_byte_semantics(self):
        original = self.write_json(v1_journal([v1_record()]))
        loaded = self.journal.load()
        self.assertEqual(set(loaded), {"journal_format_version", "broker_epoch", "records"})
        self.assertEqual(loaded["journal_format_version"], 2)
        self.assertEqual(loaded["records"], v1_journal([v1_record()])["records"])
        self.assertEqual(self.journal.backup_path.read_bytes(), original)
        self.assertEqual(json.loads(self.path.read_text(encoding="utf-8")), loaded)
        self.assertNotIn("execution", loaded["records"][0])

    def test_existing_matching_backup_makes_interrupted_migration_idempotent(self):
        original = self.write_json(v1_journal([v1_record(status="running")]))
        self.journal.backup_path.write_bytes(original)
        loaded = self.journal.load()
        self.assertEqual(loaded["records"][0]["command_status"], "running")
        self.assertEqual(self.journal.backup_path.read_bytes(), original)

    def test_mismatching_backup_fails_closed_without_touching_primary(self):
        original = self.write_json(v1_journal([v1_record()]))
        self.journal.backup_path.write_bytes(b"operator data")
        with self.assertRaises(JournalError) as caught:
            self.journal.load()
        self.assertEqual(str(caught.exception), "broker_journal_migration_backup_failed")
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(self.journal.backup_path.read_bytes(), b"operator data")

    def test_invalid_v1_never_gets_backed_up_or_replaced(self):
        broken = v1_journal([v1_record()])
        broken["records"][0]["request_fingerprint"] = "wrong"
        original = self.write_json(broken)
        with self.assertRaises(JournalError):
            self.journal.load()
        self.assertEqual(self.path.read_bytes(), original)
        self.assertFalse(self.journal.backup_path.exists())

    def test_candidate_validation_failure_leaves_original_and_matching_backup(self):
        original = self.write_json(v1_journal([v1_record()]))
        with patch.object(Journal, "valid_v2", return_value=False):
            with self.assertRaises(JournalError):
                self.journal.load()
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(self.journal.backup_path.read_bytes(), original)
        self.assertEqual(self.journal.load()["journal_format_version"], 2)

    def test_atomic_replace_failure_leaves_original_and_backup(self):
        original = self.write_json(v1_journal([v1_record()]))
        with patch("bridge.journal.os.replace", side_effect=OSError("simulated")):
            with self.assertRaises(JournalError) as caught:
                self.journal.load()
        self.assertEqual(str(caught.exception), "broker_journal_migration_failed")
        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(self.journal.backup_path.read_bytes(), original)

    def test_mixed_v1_v2_history_validates_and_saves_without_schema_relabeling(self):
        old = v1_record(status="rejected")
        old["result"] = {"schema_version": 1, "id": old["command_id"],
                          "broker_epoch": old["command"]["broker_epoch"], "status": "rejected",
                          "reason": "broker_restart_before_delivery", "before": None, "after": None}
        value = {"journal_format_version": 2, "broker_epoch": "new-broker",
                 "records": [old, v2_record()]}
        self.assertTrue(Journal.valid_v2(value))
        self.journal.save(value)
        loaded = self.journal.load()
        self.assertEqual(loaded, value)
        self.assertEqual(loaded["records"][0]["result"]["schema_version"], 1)
        self.assertNotIn("execution", loaded["records"][0])
        self.assertIsNone(loaded["records"][1]["execution"])

    def test_v1_unknown_history_can_keep_old_result_and_add_v2_alignment(self):
        old = v1_record(status="outcome_unknown")
        old["result"] = {"schema_version": 1, "id": old["command_id"],
                          "broker_epoch": old["command"]["broker_epoch"], "status": "uncertain",
                          "reason": "broker_restart_during_execution", "before": None, "after": None}
        old["updated_at_unix_ms"] = 12_000
        resolution = {"schema_version": 2, "method": "manual_state_alignment",
                      "command_id": old["command_id"], "broker_epoch": "new-broker",
                      "command_expires_at_unix_ms": old["command"]["expires_at_unix_ms"],
                      "session_id": "fresh-mod-session", "world_epoch": "new-world", "revision": 9,
                      "version": "new-world:9", "snapshot": v2_snapshot(9, False),
                      "operator_note": "Reviewed current state and will not replay.",
                      "confirm_no_replay": True, "recorded_at_unix_ms": 12_000}
        old["resolution"] = resolution
        value = {"journal_format_version": 2, "broker_epoch": "new-broker", "records": [old]}
        self.assertTrue(Journal.valid_v2(value))
        self.journal.save(value)
        reread = self.journal.load()["records"][0]
        self.assertEqual(reread["result"]["schema_version"], 1)
        self.assertEqual(reread["resolution"]["schema_version"], 2)
        self.assertNotIn("execution", reread)

    def test_unknown_v2_result_and_resolution_require_new_mod_session_and_elapsed_deadline(self):
        record = v2_record(status="outcome_unknown")
        command = record["command"]
        deadline = command["start_deadline_unix_ms"] + command["execution_timeout_ms"]
        resolution = {"schema_version": 2, "method": "manual_state_alignment",
                      "command_id": record["command_id"], "broker_epoch": "new-broker",
                      "command_expires_at_unix_ms": deadline, "session_id": "fresh-mod-session",
                      "world_epoch": "new-world", "revision": 9, "version": "new-world:9",
                      "snapshot": v2_snapshot(9, False), "operator_note": "Reviewed current state safely.",
                      "confirm_no_replay": True, "recorded_at_unix_ms": deadline + 1}
        record["resolution"] = resolution
        record["updated_at_unix_ms"] = deadline + 1
        envelope = {"journal_format_version": 2, "broker_epoch": "new-broker", "records": [record]}
        self.assertTrue(Journal.valid_v2(envelope))
        record["resolution"]["session_id"] = "old-mod-session"
        self.assertFalse(Journal.valid_v2(envelope))
        record["resolution"]["session_id"] = "fresh-mod-session"
        record["resolution"]["recorded_at_unix_ms"] = deadline - 1
        record["updated_at_unix_ms"] = deadline - 1
        self.assertFalse(Journal.valid_v2(envelope))
        record["resolution"]["recorded_at_unix_ms"] = deadline
        record["updated_at_unix_ms"] = deadline
        self.assertTrue(Journal.valid_v2(envelope))

    def test_duplicate_keys_bad_progress_and_oversize_fail_closed(self):
        self.path.write_text('{"journal_format_version":2,"journal_format_version":2}', encoding="utf-8")
        with self.assertRaises(JournalError):
            self.journal.load()
        value = {"journal_format_version": 2, "broker_epoch": "new-broker", "records": [v2_record()]}
        value["records"][0]["execution"] = {"bad": "progress"}
        with self.assertRaises(JournalError):
            self.journal.save(value)
        with self.assertRaises(JournalError):
            self.journal.save({"payload": "x" * MAX_JOURNAL_BYTES})

    def test_synthetic_rejection_cannot_erase_recorded_possible_side_effects(self):
        record = v2_record(status="outcome_unknown")
        record["command_status"] = "rejected"
        record["result"]["status"] = "rejected"
        record["result"]["reason"] = "precondition_failed"
        record["execution"] = {"id": record["command_id"], "broker_epoch": "new-broker",
                               "stage": "invoking", "sequence": 1,
                               "may_have_side_effects": True,
                               "started_at_unix_ms": 19_999,
                               "execution_deadline_unix_ms": 24_999}
        value = {"journal_format_version": 2, "broker_epoch": "new-broker", "records": [record]}
        self.assertFalse(Journal.valid_v2(value))

    def test_malformed_request_types_return_false_and_save_fails_closed(self):
        value = {"journal_format_version": 2, "broker_epoch": "new-broker", "records": [v2_record()]}
        value["records"][0]["request"]["schema_version"] = 2.0
        self.assertFalse(Journal.valid_v2(value))
        with self.assertRaises(JournalError):
            self.journal.save(value)


if __name__ == "__main__":
    unittest.main(verbosity=2)
