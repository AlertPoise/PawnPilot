"""M4 day-state bindings and end-of-day action evidence."""
import copy
import json
from pathlib import Path
import sys
import unittest
from jsonschema import Draft202012Validator, ValidationError

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.protocol import (valid_action_binding_v3, valid_action_state_v3,
    valid_action_success_evidence_v3, valid_command_v3, valid_snapshot_v3)
from bridge.server import Broker, ProtocolError
from pawnpilot_mcp.server import ActionArgumentsV3

BASE = json.loads((ROOT / 'protocol/fixtures/m3a-prepare_sale.json').read_text(encoding='utf-8'))
SCHEMA = json.loads((ROOT / 'protocol/schemas/wire-v3.json').read_text(encoding='utf-8'))
SNAPSHOT_SCHEMA = Draft202012Validator({**SCHEMA, '$ref': '#/$defs/Snapshot'})


def day_snapshot(action, *, total=40, amount=None, max_spend=None):
    state = copy.deepcopy(BASE)
    details = state['business']['details']
    customer = details['customer']
    customer.update(presence='absent', arrived=None, trade_type='unknown', phase='absent',
                    dialogue_ref=None, dialogue_stage='absent', option_refs=[], dialogue_text=None,
                    is_typing=None, waiting_for_advance=None, options=[])
    state['business'].update(customer_ref=None, item_refs=[], offer_ref=None,
                             collection_fingerprint=None)
    details['day'] = {
        'day_number': 3, 'remaining_customers': 0, 'stage': 'morning', 'reason': None,
        'can_close': False, 'can_sleep': False, 'can_confirm': False,
        'report_open': False, 'transition_pending': False, 'settlement': None,
    }
    state['cash'] = 100
    state['menu_open'] = False
    state['phase'] = 'MORNING'
    args = {}
    day = details['day']
    if action == 'close_business':
        state['phase'] = 'OPEN'
        day.update(stage='open', can_close=True)
    elif action == 'open_end_of_day':
        state['phase'] = 'CLOSED'
        day.update(stage='closed', can_sleep=True)
    elif action == 'confirm_end_of_day':
        state['phase'] = 'CLOSED'
        day.update(stage='settlement', can_confirm=True, settlement={
            'settlement_ref': 'settlement-3', 'total': total,
            'services': [{'label': 'Power', 'cost_text': '$40', 'cost': total, 'selected': True}],
        })
        args = {'settlement_ref': 'settlement-3', 'amount': total if amount is None else amount,
                'max_spend': max(0, total) if max_spend is None else max_spend}
    elif action == 'close_day_report':
        day.update(stage='next_morning', report_open=True)
    else:
        raise AssertionError(action)
    state['legal_actions'] = [{'action': action, 'arguments': args}]
    return state


def command_for(action, args, before):
    return {'schema_version': 3, 'id': 'm4-command', 'broker_epoch': 'broker-1',
        'action': action, 'arguments': copy.deepcopy(args),
        'expected_world_epoch': before['world_epoch'], 'expected_revision': before['revision'],
        'expected_version': before['version'], 'start_deadline_unix_ms': 9_000_000_000_000,
        'execution_timeout_ms': 5_000}


def transitioned(before, *, phase, stage, **updates):
    after = copy.deepcopy(before)
    after['revision'] += 1
    after['version'] = f"{after['world_epoch']}:{after['revision']}"
    after['phase'] = phase
    after['legal_actions'] = []
    day = after['business']['details']['day']
    day.update(stage=stage, can_close=False, can_sleep=False, can_confirm=False,
               transition_pending=False)
    day.update(updates)
    return after


class M4ProtocolTests(unittest.TestCase):
    def test_day_is_schema2_optional_and_old_history_remains_valid(self):
        history = copy.deepcopy(BASE)
        self.assertTrue(valid_snapshot_v3(history))
        SNAPSHOT_SCHEMA.validate(history)
        day = day_snapshot('close_business')
        self.assertTrue(valid_snapshot_v3(day))
        SNAPSHOT_SCHEMA.validate(day)
        day['business']['details']['day'] = None
        day['legal_actions'] = []
        self.assertTrue(valid_snapshot_v3(day))
        SNAPSHOT_SCHEMA.validate(day)
        legacy = json.loads((ROOT / 'protocol/fixtures/v3-business-observation.json').read_text(
            encoding='utf-8'))['before']
        legacy['business']['details']['day'] = day_snapshot('close_business')['business']['details']['day']
        self.assertFalse(valid_snapshot_v3(legacy))

    def test_day_and_settlement_shapes_are_strict_bounded_and_signed(self):
        Draft202012Validator.check_schema(SCHEMA)
        valid = day_snapshot('confirm_end_of_day', total=-25)
        self.assertTrue(valid_snapshot_v3(valid))
        SNAPSHOT_SCHEMA.validate(valid)
        for mutate in (
            lambda s: s['business']['details']['day'].update(day_number=-1),
            lambda s: s['business']['details']['day'].update(remaining_customers=-1),
            lambda s: s['business']['details']['day'].update(stage='unknown'),
            lambda s: s['business']['details']['day']['settlement'].update(settlement_ref='bad ref'),
            lambda s: s['business']['details']['day']['settlement']['services'][0].update(selected=1),
            lambda s: s['business']['details']['day']['settlement']['services'][0].update(cost_text=''),
        ):
            malformed = copy.deepcopy(valid)
            mutate(malformed)
            self.assertFalse(valid_snapshot_v3(malformed))
            with self.assertRaises(ValidationError):
                SNAPSHOT_SCHEMA.validate(malformed)
        malformed = copy.deepcopy(valid)
        malformed['business']['details']['day']['settlement']['services'] = [
            copy.deepcopy(malformed['business']['details']['day']['settlement']['services'][0])
            for _ in range(33)]
        self.assertFalse(valid_snapshot_v3(malformed))

    def test_day_action_shapes_and_prerequisites_are_bound_to_current_state(self):
        for action in ('close_business', 'open_end_of_day', 'confirm_end_of_day', 'close_day_report'):
            with self.subTest(action=action):
                state = day_snapshot(action)
                args = state['legal_actions'][0]['arguments']
                self.assertTrue(valid_snapshot_v3(state))
                self.assertTrue(valid_action_binding_v3(state['business'], action, args))
                self.assertTrue(valid_action_state_v3(state, action, args))
                SNAPSHOT_SCHEMA.validate(state)

        bad_states = []
        bad = day_snapshot('close_business'); bad['business']['details']['day']['remaining_customers'] = 1; bad_states.append(bad)
        bad = day_snapshot('close_business'); bad['business']['details']['customer'].update(presence='present',arrived=True,trade_type='buy',phase='trading'); bad['business'].update(customer_ref='customer-m3a'); bad_states.append(bad)
        bad = day_snapshot('close_business'); bad['business']['details']['day']['can_close'] = False; bad_states.append(bad)
        bad = day_snapshot('close_business'); bad['phase'] = 'MORNING'; bad_states.append(bad)
        bad = day_snapshot('open_end_of_day'); bad['business']['details']['day']['can_sleep'] = False; bad_states.append(bad)
        bad = day_snapshot('open_end_of_day'); bad['phase'] = 'OPEN'; bad_states.append(bad)
        bad = day_snapshot('confirm_end_of_day'); bad['business']['details']['day']['can_confirm'] = False; bad_states.append(bad)
        bad = day_snapshot('confirm_end_of_day'); bad['business']['details']['day']['transition_pending'] = True; bad_states.append(bad)
        bad = day_snapshot('close_day_report'); bad['business']['details']['day']['report_open'] = False; bad_states.append(bad)
        for malformed in bad_states:
            self.assertFalse(valid_snapshot_v3(malformed))

        report = day_snapshot('close_day_report')
        report['legal_actions'].append({'action':'start_business','arguments':{}})
        self.assertFalse(valid_snapshot_v3(report))
        self.assertFalse(valid_action_binding_v3(report['business'], 'start_business', {}))

    def test_settlement_authorization_allows_negative_fees_and_caps_positive_spend(self):
        negative = day_snapshot('confirm_end_of_day', total=-25, amount=-25, max_spend=0)
        self.assertTrue(valid_snapshot_v3(negative))
        positive = day_snapshot('confirm_end_of_day', total=40)
        self.assertTrue(valid_snapshot_v3(positive))
        self.assertTrue(valid_action_state_v3(positive, 'confirm_end_of_day', positive['legal_actions'][0]['arguments']))
        for amount, max_spend, cash, expected in ((40, 39, 100, False), (40, 80, 79, False),
                                                   (40, 80, 80, True), (-5, 0, 0, True), (-5, 0, -1, True)):
            state = day_snapshot('confirm_end_of_day', total=amount, amount=amount, max_spend=max_spend)
            state['cash'] = cash
            args = state['legal_actions'][0]['arguments']
            bound = valid_action_binding_v3(state['business'], 'confirm_end_of_day', args)
            self.assertEqual(bound, max_spend >= max(0, amount))
            self.assertEqual(valid_snapshot_v3(state), expected and max_spend == max(0, amount))
            self.assertEqual(valid_action_state_v3(state, 'confirm_end_of_day', args), expected)
        mismatched = day_snapshot('confirm_end_of_day', total=40, amount=39, max_spend=40)
        self.assertFalse(valid_snapshot_v3(mismatched))
        changed_ref = day_snapshot('confirm_end_of_day')
        changed_ref['legal_actions'][0]['arguments']['settlement_ref'] = 'old-settlement'
        self.assertFalse(valid_snapshot_v3(changed_ref))

    def test_broker_allows_user_authorized_spend_ceiling_above_default_only_with_cash(self):
        class Clock:
            now = 100.0
            def __call__(self): return self.now
            def monotonic(self): return self.now

        def submit(cash, command_id):
            clock = Clock()
            broker = Broker('m4-test-token', clock=clock, monotonic=clock.monotonic, heartbeat_timeout=60)
            try:
                state = day_snapshot('confirm_end_of_day', total=40)
                state['cash'] = cash
                broker.exchange({'schema_version':3,'session_id':'m4-day-session','broker_epoch':None,
                    'state':state,'has_result':False,'result':None,'progress':None})
                request = {'schema_version':3,'id':command_id,'broker_epoch':broker.broker_epoch,
                    'action':'confirm_end_of_day','arguments':{'settlement_ref':'settlement-3',
                        'amount':40,'max_spend':80},'expected_world_epoch':state['world_epoch'],
                    'expected_revision':state['revision'],'expected_version':state['version']}
                return broker.enqueue(request)
            finally:
                broker.close()

        self.assertEqual(submit(80, 'm4-authorized').get('command_status'), 'pending')
        with self.assertRaises(ProtocolError) as caught:
            submit(79, 'm4-over-cash')
        self.assertEqual(caught.exception.reason, 'precondition')

    def test_mcp_arguments_accept_signed_confirm_amount_and_reject_bad_references(self):
        args = ActionArgumentsV3(settlement_ref='settlement-4', amount=-20, max_spend=0)
        self.assertEqual(args.model_dump(mode='json', exclude_unset=True, exclude_none=True),
                         {'settlement_ref': 'settlement-4', 'amount': -20, 'max_spend': 0})
        with self.assertRaises(Exception):
            ActionArgumentsV3(settlement_ref='bad ref', amount=10, max_spend=10)

    def test_confirm_command_and_all_day_postconditions_are_exact(self):
        confirm = day_snapshot('confirm_end_of_day', total=-25)
        args = confirm['legal_actions'][0]['arguments']
        command = command_for('confirm_end_of_day', args, confirm)
        self.assertTrue(valid_command_v3(command))
        after = transitioned(confirm, phase='MORNING', stage='next_morning', day_number=4,
                             report_open=True, settlement=None)
        after['cash'] = confirm['cash'] - 25
        self.assertTrue(valid_snapshot_v3(after))
        self.assertTrue(valid_action_success_evidence_v3(command, confirm, after))
        wrong_cash = copy.deepcopy(after); wrong_cash['cash'] += 1
        self.assertFalse(valid_action_success_evidence_v3(command, confirm, wrong_cash))
        wrong_day = copy.deepcopy(after); wrong_day['business']['details']['day']['day_number'] = 3
        self.assertFalse(valid_action_success_evidence_v3(command, confirm, wrong_day))

        before = day_snapshot('close_business')
        after = transitioned(before, phase='CLOSED', stage='closed', can_sleep=True)
        self.assertTrue(valid_action_success_evidence_v3(command_for('close_business', {}, before), before, after))
        before = day_snapshot('open_end_of_day')
        after = transitioned(before, phase='CLOSED', stage='settlement', can_confirm=True,
            settlement={'settlement_ref':'settlement-next','total':0,'services':[]})
        self.assertTrue(valid_action_success_evidence_v3(command_for('open_end_of_day', {}, before), before, after))
        before = day_snapshot('close_day_report')
        after = transitioned(before, phase='MORNING', stage='morning', report_open=False)
        self.assertTrue(valid_action_success_evidence_v3(command_for('close_day_report', {}, before), before, after))

    def test_day_action_reference_and_extra_arguments_are_rejected(self):
        state = day_snapshot('confirm_end_of_day')
        args = state['legal_actions'][0]['arguments']
        command = command_for('confirm_end_of_day', {**args, 'item_refs': ['surprise']}, state)
        self.assertFalse(valid_command_v3(command))
        command = command_for('confirm_end_of_day', {'settlement_ref':'settlement-3','amount':-40,'max_spend':0}, state)
        self.assertTrue(valid_command_v3(command))  # Structural validation defers current settlement binding.
        self.assertFalse(valid_action_binding_v3(state['business'], 'confirm_end_of_day', command['arguments']))


if __name__ == '__main__':
    unittest.main()
