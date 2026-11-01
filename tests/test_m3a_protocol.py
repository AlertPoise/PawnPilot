"""M3-A BUY-side protocol binding and immutable evidence checks."""
import copy
import json
from pathlib import Path
import sys
import unittest
from jsonschema import Draft202012Validator, ValidationError

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.protocol import (valid_snapshot_v3, valid_action_success_evidence_v3,
                             valid_business_v3, valid_action_binding_v3)

SCHEMA = json.loads((ROOT / 'protocol/schemas/wire-v3.json').read_text(encoding='utf-8'))
FIXTURES = {name: json.loads((ROOT / f'protocol/fixtures/m3a-{name}.json').read_text(encoding='utf-8'))
            for name in ('prepare_sale', 'sell', 'sort')}


def move_revision(state, revision):
    state['revision'] = revision
    state['version'] = f"{state['world_epoch']}:{revision}"
    return state


class M3AProtocolTests(unittest.TestCase):
    def test_buy_fixtures_are_valid_and_schema_accepts_their_action_shapes(self):
        Draft202012Validator.check_schema(SCHEMA)
        validator = Draft202012Validator({**SCHEMA, '$ref': '#/$defs/Snapshot'})
        for state in FIXTURES.values():
            self.assertTrue(valid_snapshot_v3(state))
            validator.validate(state)
        self.assertEqual(FIXTURES['sell']['business']['details']['customer']['trade_type'], 'buy')
        self.assertFalse(FIXTURES['sell']['business']['details']['eligibility']['purchase_enabled'])

    def test_schema2_request_text_is_optional_and_bounded_but_schema1_remains_strict(self):
        state = copy.deepcopy(FIXTURES['prepare_sale'])
        customer = state['business']['details']['customer']
        customer.pop('request_text')
        self.assertTrue(valid_snapshot_v3(state))
        customer['request_text'] = 'x' * 256
        self.assertTrue(valid_snapshot_v3(state))
        customer['request_text'] = 'x' * 257
        self.assertFalse(valid_snapshot_v3(state))
        legacy = json.loads((ROOT / 'protocol/fixtures/v3-business-observation.json').read_text(encoding='utf-8'))['before']
        self.assertTrue(valid_snapshot_v3(legacy))
        legacy['business']['details']['customer']['request_text'] = 'unsupported in schema 1'
        self.assertFalse(valid_snapshot_v3(legacy))
        legacy['business']['details']['customer'].pop('request_text')
        legacy['business']['details']['customer']['trade_type'] = 'buy'
        self.assertFalse(valid_snapshot_v3(legacy))

    def test_distinct_prepare_entries_are_allowed_but_duplicates_and_old_action_extensions_are_not(self):
        state = copy.deepcopy(FIXTURES['prepare_sale'])
        details = state['business']['details']
        details['containers'][1]['item_refs'].append('second-owned-item')
        details['items'].append({'item_ref':'second-owned-item','container':'player','name':'Second owned item',
            'units':1,'owned':True,'shape_status':'unverified','width':None,'height':None})
        state['legal_actions'].append({'action':'prepare_sale_items','arguments':{
            'customer_ref':'customer-m3a','item_refs':['second-owned-item'],'collection_fingerprint':'second-fingerprint'}})
        self.assertTrue(valid_snapshot_v3(state))
        duplicate = copy.deepcopy(state)
        duplicate['legal_actions'].append(copy.deepcopy(duplicate['legal_actions'][0]))
        self.assertFalse(valid_snapshot_v3(duplicate))
        old_action_extra = copy.deepcopy(FIXTURES['sort'])
        old_action_extra['phase'] = 'MORNING'
        old_action_extra['legal_actions'] = [{'action':'start_business','arguments':{'container':'player'}}]
        self.assertFalse(valid_snapshot_v3(old_action_extra))

    def test_legal_action_list_is_bounded_to_64(self):
        state = copy.deepcopy(FIXTURES['prepare_sale'])
        details = state['business']['details']
        details['containers'][1]['item_refs'] = []
        details['items'] = []
        state['legal_actions'] = [{'action':'sort_inventory_default','arguments':{'container':'player'}}] * 65
        self.assertFalse(valid_snapshot_v3(state))

    def test_prepare_sale_requires_one_owned_player_item_and_empty_complete_back(self):
        state = copy.deepcopy(FIXTURES['prepare_sale'])
        self.assertTrue(valid_snapshot_v3(state))
        self.assertTrue(valid_action_binding_v3(state['business'], 'prepare_sale_items', state['legal_actions'][0]['arguments']))
        for mutate in (
            lambda s: s['legal_actions'][0]['arguments'].update(item_refs=['owned-sale-item', 'other']),
            lambda s: s['business']['details']['items'][0].update(owned=False),
            lambda s: s['business']['details']['containers'][0]['item_refs'].append('intruder'),
            lambda s: s['business']['details']['customer'].update(arrived=False),
            lambda s: s['business']['details']['customer'].update(trade_type='sell'),
        ):
            changed = copy.deepcopy(state)
            mutate(changed)
            self.assertFalse(valid_snapshot_v3(changed))

    def test_sell_quote_binds_to_owned_back_subset_without_purchase_eligibility(self):
        state = copy.deepcopy(FIXTURES['sell'])
        self.assertTrue(valid_snapshot_v3(state))
        unverified_global_ui = copy.deepcopy(state)
        unverified_global_ui['business']['details']['ui'].update(
            status='unverified', coverage='known_roots_only',
            blockers=['global_ui_stack_unobservable', 'global_raycast_order_unobservable'])
        self.assertTrue(valid_snapshot_v3(unverified_global_ui))
        concrete_blocker = copy.deepcopy(state)
        concrete_blocker['business']['details']['ui'].update(status='unverified', blockers=['modal'])
        self.assertFalse(valid_snapshot_v3(concrete_blocker))
        malformed = copy.deepcopy(state)
        malformed['business']['item_refs'] = []
        self.assertFalse(valid_snapshot_v3(malformed))
        malformed = copy.deepcopy(state)
        malformed['legal_actions'][0]['arguments']['amount'] = 121
        self.assertFalse(valid_snapshot_v3(malformed))
        malformed = copy.deepcopy(state)
        malformed['business']['details']['containers'][0]['item_refs'].append('second-owned-item')
        malformed['business']['details']['items'].append({**malformed['business']['details']['items'][0],
            'item_ref':'second-owned-item'})
        malformed['business']['item_refs'].append('second-owned-item')
        malformed['legal_actions'][0]['arguments']['item_refs'].append('second-owned-item')
        self.assertFalse(valid_snapshot_v3(malformed))
        Draft202012Validator({**SCHEMA, '$ref': '#/$defs/Snapshot'}).validate(state)
        with self.assertRaises(ValidationError):
            Draft202012Validator({**SCHEMA, '$ref': '#/$defs/Snapshot'}).validate(malformed)
        malformed = copy.deepcopy(state)
        malformed['business']['details']['items'][0]['owned'] = False
        self.assertFalse(valid_snapshot_v3(malformed))
        malformed = copy.deepcopy(state)
        malformed['business']['details']['eligibility']['purchase_enabled'] = True
        self.assertFalse(valid_business_v3(malformed['business']))

    def test_sort_action_needs_complete_player_and_no_dialogue_or_concrete_blockers(self):
        state = copy.deepcopy(FIXTURES['sort'])
        self.assertTrue(valid_snapshot_v3(state))
        unverified_global_ui = copy.deepcopy(state)
        unverified_global_ui['business']['details']['ui'].update(
            status='unverified', coverage='known_roots_only',
            blockers=['global_ui_stack_unobservable', 'global_raycast_order_unobservable'])
        self.assertTrue(valid_snapshot_v3(unverified_global_ui))
        for mutate in (
            lambda s: s['business']['details']['containers'][0].update(completeness='incomplete', reason='unknown'),
            lambda s: s['business']['details']['customer'].update(dialogue_ref='dialogue-1'),
            lambda s: s['business']['details']['customer'].update(phase='dialogue'),
            lambda s: s['business']['details']['ui'].update(blockers=['modal']),
            lambda s: s['legal_actions'][0]['arguments'].update(container='back'),
        ):
            changed = copy.deepcopy(state)
            mutate(changed)
            self.assertFalse(valid_snapshot_v3(changed))

    def test_prepare_sale_success_requires_only_chosen_item_to_move_and_cash_unchanged(self):
        before = copy.deepcopy(FIXTURES['prepare_sale'])
        after = copy.deepcopy(before)
        move_revision(after, 2)
        details = after['business']['details']
        details['containers'][0]['item_refs'] = ['owned-sale-item']
        details['containers'][1]['item_refs'] = []
        details['items'][0]['container'] = 'back'
        after['business'].update(support='available', item_refs=['owned-sale-item'], collection_fingerprint='back-fp')
        after['legal_actions'] = []
        self.assertTrue(valid_snapshot_v3(after))
        command = {'action':'prepare_sale_items','arguments':before['legal_actions'][0]['arguments']}
        self.assertTrue(valid_action_success_evidence_v3(command, before, after))
        bad = copy.deepcopy(after); bad['cash'] += 1
        self.assertFalse(valid_action_success_evidence_v3(command, before, bad))

    def test_sale_success_requires_exact_cash_gain_and_retained_non_sale_items(self):
        before = copy.deepcopy(FIXTURES['sell'])
        details = before['business']['details']
        second = copy.deepcopy(details['items'][0]); second.update(item_ref='npc-item', name='NPC item', owned=False)
        details['containers'][0]['item_refs'].append('other-owned-item')
        details['items'].append(second)
        details['containers'][0]['item_refs'][-1] = 'npc-item'
        args = before['legal_actions'][0]['arguments']
        self.assertTrue(valid_snapshot_v3(before))
        after = copy.deepcopy(before); move_revision(after, 2); after['cash'] += 120
        d = after['business']['details']
        d['containers'][0]['item_refs'] = ['npc-item']
        d['items'] = [d['items'][1]]
        d['customer'].update(presence='absent',arrived=None,trade_type='unknown',phase='leaving',request_text=None)
        after['business'].update(support='partial',customer_ref=None,item_refs=[],offer_ref=None,collection_fingerprint=None)
        after['legal_actions'] = []
        d['quote']={'status':'unavailable','mode':'unknown','displayed_total':None,'wholesale_total':None,'rows':[],'reason':'customer_left'}
        d['eligibility']={'purchase_enabled':False,'missing':['customer_absent']}
        self.assertTrue(valid_snapshot_v3(after))
        command={'action':'sell_visible_offer','arguments':args}
        self.assertTrue(valid_action_success_evidence_v3(command,before,after))
        bad=copy.deepcopy(after); bad['cash'] += 1
        self.assertFalse(valid_action_success_evidence_v3(command,before,bad))
        bad=copy.deepcopy(after); bad['business']['details']['items'] = []
        bad['business']['details']['containers'][0]['item_refs'] = []
        self.assertFalse(valid_action_success_evidence_v3(command,before,bad))

    def test_sort_success_preserves_inventory_identity_units_and_cash(self):
        before=copy.deepcopy(FIXTURES['sort'])
        before['business']['details']['containers'][0]['item_refs'].append('other')
        before['business']['details']['items'].append({'item_ref':'other','container':'player','name':'Other','units':3,'owned':True,'shape_status':'unverified','width':None,'height':None})
        after=copy.deepcopy(before); move_revision(after,2)
        after['business']['details']['containers'][0]['item_refs'].reverse()
        after['business']['details']['items'].reverse()
        self.assertTrue(valid_snapshot_v3(after))
        command={'action':'sort_inventory_default','arguments':{'container':'player'}}
        self.assertTrue(valid_action_success_evidence_v3(command,before,after))
        bad=copy.deepcopy(after); bad['business']['details']['items'][0]['units']=1
        self.assertFalse(valid_action_success_evidence_v3(command,before,bad))


if __name__ == '__main__':
    unittest.main()
