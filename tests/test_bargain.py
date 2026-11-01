"""Bargain wire compatibility, capability binding and observable result evidence."""
import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

from jsonschema import Draft202012Validator, ValidationError

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.protocol import (BARGAIN_ACTIONS, valid_action_binding_v3,
    valid_action_success_evidence_v3, valid_command, valid_command_v3,
    valid_snapshot_v3, valid_success_result_v3)
from pawnpilot_mcp import server as mcp_server

SCHEMA = json.loads((ROOT / 'protocol/schemas/wire-v3.json').read_text(encoding='utf-8'))
BASE = json.loads((ROOT / 'protocol/fixtures/m3a-prepare_sale.json').read_text(encoding='utf-8'))
TRADE = json.loads((ROOT / 'protocol/fixtures/m3a-sell.json').read_text(encoding='utf-8'))
HISTORY = json.loads((ROOT / 'protocol/fixtures/v3-business-observation.json').read_text(encoding='utf-8'))
VALIDATOR = Draft202012Validator({**SCHEMA, '$ref': '#/$defs/Snapshot'})


def state(phase='choosing', direction='sell', action=None):
    value = copy.deepcopy(TRADE)
    value['legal_actions'] = []
    details = value['business']['details']
    details['customer']['trade_type'] = direction
    for item in details['items']:
        item['owned'] = direction == 'buy'
    details['bargain'] = {'available': True, 'phase': phase, 'direction': direction,
        'terms_revision': 1, 'options': [{'option_ref': 'bargain-1', 'text': '-10%',
            'kind': 'lower_purchase_price' if direction == 'sell' else 'raise_sale_price',
            'price_change_percent': -10 if direction == 'sell' else 10,
            'enabled': True, 'selected': False}], 'reputation_text': 'Buying: x-1.00',
        'hint_text': 'Accepts 80%; refuses 8%; walks out 12%',
        'last_option_ref': None, 'result': 'none', 'result_text': None}
    if phase in {'closed', 'unavailable'}:
        details['bargain']['options'] = []
    if action:
        args = {'customer_ref': value['business']['customer_ref'], 'mode': direction}
        if action == 'choose_bargain_option':
            args['option_ref'] = 'bargain-1'
        value['legal_actions'] = [{'action': action, 'arguments': args}]
    return value


def revised(value):
    value = copy.deepcopy(value)
    value['revision'] += 1
    value['version'] = f"{value['world_epoch']}:{value['revision']}"
    value['legal_actions'] = []
    return value


def command(before):
    return {'schema_version': 3, 'id': 'bargain-command', 'broker_epoch': 'broker',
        **before['legal_actions'][0], 'expected_world_epoch': before['world_epoch'],
        'expected_revision': before['revision'], 'expected_version': before['version'],
        'start_deadline_unix_ms': 1000, 'execution_timeout_ms': 5000}


class BargainTests(unittest.TestCase):
    def test_optional_extension_preserves_old_schema2_and_schema1_history(self):
        for value in (copy.deepcopy(BASE), state()):
            self.assertTrue(valid_snapshot_v3(value))
            VALIDATOR.validate(value)
        old = copy.deepcopy(BASE)
        old['business']['details']['bargain'] = None
        self.assertTrue(valid_snapshot_v3(old))
        VALIDATOR.validate(old)
        old = copy.deepcopy(HISTORY['before'])
        self.assertTrue(valid_snapshot_v3(old))
        old['business']['details']['bargain'] = None
        self.assertFalse(valid_snapshot_v3(old))
        with self.assertRaises(ValidationError):
            VALIDATOR.validate(old)

    def test_present_object_requires_every_field_and_rejects_extras(self):
        value = state()
        for field in value['business']['details']['bargain']:
            bad = copy.deepcopy(value)
            del bad['business']['details']['bargain'][field]
            self.assertFalse(valid_snapshot_v3(bad), field)
            with self.assertRaises(ValidationError):
                VALIDATOR.validate(bad)
        bad = copy.deepcopy(value)
        bad['business']['details']['bargain']['hidden_roll'] = 45
        self.assertFalse(valid_snapshot_v3(bad))

    def test_percent_is_signed_bounded_nonzero_integer_and_ref_list_is_bounded(self):
        for percent in (-100, -10, 10, 100):
            value = state()
            value['business']['details']['bargain']['options'][0]['price_change_percent'] = percent
            self.assertTrue(valid_snapshot_v3(value))
            VALIDATOR.validate(value)
        for percent in (-101, 0, 101, True, 10.5, '-10'):
            value = state()
            value['business']['details']['bargain']['options'][0]['price_change_percent'] = percent
            self.assertFalse(valid_snapshot_v3(value), repr(percent))
            with self.assertRaises(ValidationError):
                VALIDATOR.validate(value)
        for count in (16, 17):
            value = state()
            bargain = value['business']['details']['bargain']
            bargain['options'] = [{**bargain['options'][0], 'option_ref': f'option-{n}'} for n in range(count)]
            self.assertEqual(valid_snapshot_v3(value), count == 16)
        value = state()
        value['business']['details']['bargain']['options'] *= 2
        self.assertFalse(valid_snapshot_v3(value))

    def test_bounds_and_enums_do_not_allow_hidden_or_malformed_data(self):
        for field, bad_value in [('available', 1), ('phase', 'ready'), ('direction', 'normal'),
                ('terms_revision', -1), ('terms_revision', True), ('result', 'accepted'),
                ('last_option_ref', 'spaces forbidden'), ('hint_text', 'x' * 257),
                ('reputation_text', ''), ('result_text', 4)]:
            value = state()
            value['business']['details']['bargain'][field] = bad_value
            self.assertFalse(valid_snapshot_v3(value), field)
        for field, bad_value in [('text', 'x' * 257), ('kind', 'blackmail'), ('enabled', 1),
                ('selected', None), ('option_ref', '')]:
            value = state()
            value['business']['details']['bargain']['options'][0][field] = bad_value
            self.assertFalse(valid_snapshot_v3(value), field)

    def test_available_unavailable_and_both_npc_directions(self):
        for direction in ('buy', 'sell'):
            for action, phase in [('open_bargain_panel', 'closed'),
                    ('close_bargain_panel', 'choosing'), ('choose_bargain_option', 'choosing')]:
                value = state(phase, direction, action)
                self.assertTrue(valid_snapshot_v3(value), (direction, action))
                self.assertTrue(valid_command_v3(command(value)))
                Draft202012Validator({**SCHEMA, '$ref': '#/$defs/Command'}).validate(command(value))
                self.assertFalse(valid_command(dict(command(value), schema_version=2)))
                VALIDATOR.validate(value)
                value['business']['details']['bargain']['available'] = False
                self.assertEqual(valid_snapshot_v3(value), action == 'close_bargain_panel')
        value = state('unavailable')
        value['business']['details']['bargain'].update(available=False, direction='unknown', options=[])
        self.assertTrue(valid_snapshot_v3(value))

    def test_complete_normal_quote_remains_observable_under_only_bargain_panel(self):
        value = json.loads((ROOT / 'protocol/fixtures/m3a-sell.json').read_text(encoding='utf-8'))
        original_action = value['legal_actions'][0]
        value['legal_actions'] = []
        details = value['business']['details']
        details['bargain'] = state(direction='buy')['business']['details']['bargain']
        details['ui'].update(status='blocked', blockers=['bargain_panel',
            'global_ui_stack_unobservable', 'global_raycast_order_unobservable',
            'quote_rebuild_completion_unobservable', 'tutorial_group_active_unverified',
            'control_hints_active_unverified'])
        self.assertTrue(valid_snapshot_v3(value))
        self.assertEqual(details['quote']['mode'], 'normal')
        self.assertEqual(details['quote']['displayed_total'], 120)
        VALIDATOR.validate(value)
        value['legal_actions'] = [original_action]
        self.assertFalse(valid_snapshot_v3(value))
        value['legal_actions'] = []
        details['ui']['blockers'].append('modal')
        self.assertFalse(valid_snapshot_v3(value))
        details['ui'].update(status='consistent', blockers=['ui_reader_failed'])
        self.assertFalse(valid_snapshot_v3(value))

    def test_capabilities_bind_customer_direction_option_and_arrival(self):
        value = state(action='choose_bargain_option')
        for mutate in (
            lambda v: v['legal_actions'][0]['arguments'].update(customer_ref='expired-customer'),
            lambda v: v['legal_actions'][0]['arguments'].update(option_ref='expired-option'),
            lambda v: v['legal_actions'][0]['arguments'].update(mode='buy'),
            lambda v: v['business']['details']['customer'].update(trade_type='buy'),
            lambda v: v['business']['details']['bargain'].update(direction='buy'),
            lambda v: v['business']['details']['customer'].update(arrived=False),
            lambda v: v['business']['details']['bargain']['options'][0].update(enabled=False),
            lambda v: v['business']['details']['bargain']['options'][0].update(selected=True),
        ):
            bad = copy.deepcopy(value)
            mutate(bad)
            self.assertFalse(valid_snapshot_v3(bad))

    def test_choose_needs_prepared_visible_normal_quote_but_panel_controls_do_not(self):
        for action in BARGAIN_ACTIONS:
            value = state(phase='closed' if action == 'open_bargain_panel' else 'choosing', action=action)
            details = value['business']['details']
            details['quote'].update(status='unverified', mode='unknown', displayed_total=None, reason='not_prepared')
            value['business'].update(offer_ref=None, item_refs=[], collection_fingerprint=None)
            self.assertEqual(valid_snapshot_v3(value), action != 'choose_bargain_option')

    def test_choose_leave_ack_retains_direction_after_native_intent_becomes_other(self):
        before = state(action='choose_bargain_option')
        after = revised(before)
        business = after['business']
        business.update(offer_ref=None, item_refs=[], collection_fingerprint=None)
        details = business['details']
        details['customer']['trade_type'] = 'other'
        details['quote'].update(status='unavailable', mode='unknown', displayed_total=None, rows=[], reason='leaving')
        details['bargain'].update(phase='responding', terms_revision=2,
            last_option_ref='bargain-1', result='responding')
        self.assertTrue(valid_snapshot_v3(after))
        self.assertTrue(valid_action_success_evidence_v3(command(before), before, after))
        bad = copy.deepcopy(after)
        bad['business']['details']['bargain']['direction'] = 'unknown'
        self.assertFalse(valid_action_success_evidence_v3(command(before), before, bad))

    def test_close_acknowledged_leaving_response_uses_original_direction(self):
        before = state(phase='responding', action='close_bargain_panel')
        business = before['business']
        business.update(offer_ref=None, item_refs=[], collection_fingerprint=None)
        details = business['details']
        details['customer']['trade_type'] = 'other'
        details['quote'].update(status='unavailable', mode='unknown', displayed_total=None, rows=[], reason='leaving')
        details['bargain'].update(terms_revision=2, last_option_ref='actual-chosen-old-ref', result='responding')
        self.assertTrue(valid_snapshot_v3(before))
        after = revised(before)
        after['business']['details']['bargain'].update(phase='closed', options=[])
        self.assertTrue(valid_snapshot_v3(after))
        self.assertTrue(valid_action_success_evidence_v3(command(before), before, after))
        for values in ({'terms_revision':0}, {'last_option_ref':None}, {'phase':'choosing'}, {'direction':'buy'}, {'result':'none'}):
            bad = copy.deepcopy(before)
            bad['business']['details']['bargain'].update(values)
            self.assertFalse(valid_snapshot_v3(bad))

    def test_only_bargain_actions_ignore_panel_and_close_can_run_during_response(self):
        for action, phase in [('open_bargain_panel', 'closed'), ('choose_bargain_option', 'choosing'),
                ('close_bargain_panel', 'responding')]:
            value = state(phase=phase, action=action)
            details = value['business']['details']
            details['ui']['blockers'] = ['bargain_panel']
            self.assertTrue(valid_snapshot_v3(value))
            details['ui']['status'] = 'blocked'
            self.assertTrue(valid_snapshot_v3(value))
            details['ui']['blockers'].append('modal')
            self.assertFalse(valid_snapshot_v3(value))
            details['ui']['blockers'] = ['bargain_panel', 'dialogue_active']
            details['customer'].update(dialogue_ref='response-dialogue', phase='dialogue',
                dialogue_stage='typing', is_typing=True)
            details['quote'].update(status='unverified', mode='unknown', displayed_total=None, reason='responding')
            value['business']['offer_ref'] = None
            self.assertEqual(valid_snapshot_v3(value), action == 'close_bargain_panel')
        for action in ('dismiss_customer', 'prepare_purchase_items', 'sort_inventory_default'):
            value = state()
            value['business']['details']['ui']['blockers'] = ['bargain_panel']
            from bridge.protocol import _has_action_blocking_evidence
            self.assertTrue(_has_action_blocking_evidence(['bargain_panel'], action))
        value = state(action='choose_bargain_option')
        value['business']['details']['ui'].update(status='blocked', blockers=[])
        self.assertFalse(valid_snapshot_v3(value))

    def test_close_menu_keeps_its_original_gate_when_bargain_panel_is_open(self):
        value = state()
        value.update(menu_open=True, can_close_menu=True,
            legal_actions=[{'action':'close_menu', 'arguments':{}}])
        value['business']['details']['ui'].update(status='blocked', blockers=['bargain_panel'])
        self.assertTrue(valid_snapshot_v3(value))
        VALIDATOR.validate(value)
        value['can_close_menu'] = False
        self.assertFalse(valid_snapshot_v3(value))

    def test_open_close_success_requires_transition_customer_world_revision_and_unchanged_cash(self):
        for action, old_phase, new_phase in [('open_bargain_panel', 'closed', 'choosing'),
                ('close_bargain_panel', 'choosing', 'closed'), ('close_bargain_panel', 'responding', 'closed')]:
            before = state(phase=old_phase, action=action)
            after = revised(before)
            after['business']['details']['bargain']['phase'] = new_phase
            after['business']['details']['bargain']['options'] = (
                state()['business']['details']['bargain']['options'] if new_phase == 'choosing' else [])
            cmd = command(before)
            self.assertTrue(valid_action_success_evidence_v3(cmd, before, after))
            for mutate in (
                lambda v: v.update(cash=v['cash'] - 1),
                lambda v: v.update(world_epoch='other-world'),
                lambda v: v.update(revision=before['revision']),
                lambda v: v['business'].update(customer_ref='next-customer'),
                lambda v: v['business']['details']['bargain'].update(direction='buy'),
            ):
                bad = copy.deepcopy(after)
                mutate(bad)
                self.assertFalse(valid_action_success_evidence_v3(cmd, before, bad))
            if action == 'open_bargain_panel':
                after['business']['details']['bargain']['options'] = []
                self.assertFalse(valid_action_success_evidence_v3(cmd, before, after))

    def test_choose_success_requires_new_terms_matching_option_and_result(self):
        before = state(action='choose_bargain_option')
        after = revised(before)
        bargain = after['business']['details']['bargain']
        bargain.update(phase='responding', terms_revision=2, last_option_ref='bargain-1', result='responding')
        cmd = command(before)
        for result in ('responding', 'price_changed', 'unchanged', 'customer_left', 'unknown'):
            bargain['result'] = result
            self.assertTrue(valid_action_success_evidence_v3(cmd, before, after))
        for values in ({'terms_revision': 1}, {'last_option_ref': 'expired-option'}, {'result': 'none'}):
            bad = copy.deepcopy(after)
            bad['business']['details']['bargain'].update(values)
            self.assertFalse(valid_action_success_evidence_v3(cmd, before, bad))
        bad = copy.deepcopy(before)
        bad['business']['details']['bargain']['options'][0]['enabled'] = False
        self.assertFalse(valid_action_success_evidence_v3(cmd, bad, after))

    def test_stale_command_or_prior_result_cannot_prove_success(self):
        before = state(action='choose_bargain_option')
        after = revised(before)
        after['business']['details']['bargain'].update(terms_revision=2,
            last_option_ref='bargain-1', result='price_changed', result_text='Agreed')
        cmd = command(before)
        result = copy.deepcopy(HISTORY['result'])
        result.update(id=cmd['id'], broker_epoch=cmd['broker_epoch'], before=before, after=after)
        result['execution'].update(id=cmd['id'], broker_epoch=cmd['broker_epoch'])
        self.assertTrue(valid_success_result_v3(cmd, result))
        for values in ({'expected_revision': 0}, {'expected_version': 'fixture-world:0'},
                {'expected_world_epoch': 'old-world'}, {'arguments': {**cmd['arguments'], 'option_ref': 'old-option'}}):
            self.assertFalse(valid_success_result_v3({**cmd, **values}, result))
        after['business']['details']['bargain']['terms_revision'] = 1
        self.assertFalse(valid_success_result_v3(cmd, result))

    def test_mcp_literals_and_submission_preserve_exact_bargain_arguments(self):
        reply = mcp_server.ObserveReply(ok=True, error_source='none', broker_connected=True,
            bridge_connected=True, legal_actions=sorted(BARGAIN_ACTIONS))
        self.assertEqual(set(reply.legal_actions), BARGAIN_ACTIONS)
        for action in BARGAIN_ACTIONS:
            before = state(phase='closed' if action == 'open_bargain_panel' else 'choosing', action=action)
            self.assertEqual(mcp_server.GameStateV3.model_validate(before).legal_actions[0].action, action)
            captured = {}
            def fake_call(path, body=None):
                captured.update(path=path, body=copy.deepcopy(body))
                return 200, {'schema_version':3, 'id':'bargain-command', 'command_status':'pending',
                    'status':'pending', 'result':None, 'resolved':False, 'resolution':None,
                    'execution':None, 'record_schema_version':3}
            args = before['legal_actions'][0]['arguments']
            with patch.object(mcp_server, '_call', fake_call):
                response = mcp_server.act(3, 'bargain-command', action,
                    mcp_server.ActionArgumentsV3(**args), 'broker', before['world_epoch'], 1, before['version'])
            self.assertTrue(response.ok)
            self.assertEqual(captured['body']['arguments'], args)


if __name__ == '__main__':
    unittest.main()
