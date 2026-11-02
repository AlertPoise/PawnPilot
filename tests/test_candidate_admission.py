"""Production C# admission -> identical JSON -> strict broker/MCP/schema checks."""
import copy
import json
from pathlib import Path
import subprocess
import unittest

from jsonschema import Draft202012Validator
from bridge.protocol import valid_snapshot_v3
from bridge.server import Broker, ProtocolError
from pawnpilot_mcp.server import GameStateV3

ROOT = Path(__file__).resolve().parents[1]


class CandidateAdmissionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        process = subprocess.run(
            ['dotnet', str(ROOT / 'tests/PawnPilot.CoreTests/bin/Release/net10.0/PawnPilot.CoreTests.dll'),
             '--export-candidate-fixtures'], cwd=ROOT, capture_output=True, text=True, check=True, timeout=30)
        cls.states = json.loads(process.stdout)
        schema = json.loads((ROOT / 'protocol/schemas/wire-v3.json').read_text(encoding='utf-8'))
        cls.schema = Draft202012Validator({**schema, '$ref': '#/$defs/Snapshot'})

    def test_managed_publication_is_accepted_unchanged_by_all_readers(self):
        for name, state in self.states.items():
            with self.subTest(name=name):
                self.schema.validate(state)
                self.assertTrue(valid_snapshot_v3(state))
                GameStateV3.model_validate(state)
                broker = Broker('a' * 64)
                try:
                    broker.exchange({'schema_version': 3, 'session_id': 'admission-test', 'broker_epoch': None,
                                     'state': state, 'has_result': False, 'result': None, 'progress': None})
                    self.assertEqual(broker.snapshot()['state'], state)
                finally:
                    broker.close()

    def test_transition_native_candidate_never_becomes_public_legal_action(self):
        for name, state in self.states.items():
            if '_transition' in name or name.startswith('sort_blocked_'):
                with self.subTest(name=name):
                    self.assertEqual(state['legal_actions'], [])
                    self.assertTrue(any(row['status'] == 'blocked' for row in state['business']['details']['candidates']))

    def test_broker_still_rejects_original_sort_transition_defect(self):
        state = copy.deepcopy(self.states['sort_blocked_open'])
        state['legal_actions'] = [{'action': 'sort_inventory_default', 'arguments': {'container': 'player'}}]
        self.assertFalse(valid_snapshot_v3(state))
        broker = Broker('b' * 64)
        try:
            with self.assertRaises(ProtocolError) as error:
                broker.exchange({'schema_version': 3, 'session_id': 'invalid-test', 'broker_epoch': None,
                                 'state': state, 'has_result': False, 'result': None, 'progress': None})
            self.assertEqual(error.exception.reason, 'invalid_snapshot')
            self.assertIsNone(broker.snapshot()['state'])
        finally:
            broker.close()

    def test_ordinary_actions_and_report_close_remain_available(self):
        expected = {'sort_open': 'sort_inventory_default', 'sort_morning': 'sort_inventory_default',
                    'm3a-prepare_sale': 'prepare_sale_items', 'm3a-sell': 'sell_visible_offer',
                    'v3-dialogue-snapshot': 'choose_dialogue_option',
                    'report_close_remains_legal': 'close_day_report', 'ordinary_close_menu': 'close_menu'}
        expected['purchase'] = 'purchase_visible_offer'
        for name, action in expected.items():
            with self.subTest(name=name):
                self.assertEqual([row['action'] for row in self.states[name]['legal_actions']], [action])
        self.assertFalse(self.states['report_menu_suppressed']['can_close_menu'])
        self.assertEqual(self.states['report_menu_suppressed']['legal_actions'], [])
        self.assertFalse(self.states['purchase_transition']['business']['details']['eligibility']['purchase_enabled'])

    def test_historical_wire_fixtures_remain_valid(self):
        from bridge.protocol import valid_snapshot
        old = json.loads((ROOT / 'protocol/fixtures/v2-close-menu.json').read_text(encoding='utf-8'))
        legacy = json.loads((ROOT / 'protocol/fixtures/v3-business-observation.json').read_text(encoding='utf-8'))
        self.assertTrue(valid_snapshot(old['before']))
        self.assertTrue(valid_snapshot_v3(legacy['before']))
