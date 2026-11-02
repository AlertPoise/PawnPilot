"""Supervisor lifecycle tests: model text is never completion or execution evidence."""
import copy
import json
from pathlib import Path
import shutil
import unittest
import uuid

from pawnpilot_supervisor.runtime import Supervisor, OwnerLock, SupervisorError, agent_prompt

ROOT = Path(__file__).resolve().parents[1]


class MemoryStore:
    def __init__(self, path):
        self.path = path
        self.doc = {'revision': 0, 'run': {'run_id': 'test-run', 'status': 'running'},
                    'controls': {'paused': False, 'stopped': False},
                    'authorization': {'limits': {'writes': 10}}, 'budget': {'writes': 0},
                    'plan': {'text': ''}, 'commands': [], 'inbox': [], 'confirmed_history': []}
        self.grants = []
        self.revoked = []

    def snapshot(self):
        return copy.deepcopy(self.doc)

    def control(self, op, revision, **kwargs):
        if revision != self.doc['revision']:
            raise ValueError('revision_conflict')
        self.doc['revision'] += 1
        if op == 'pause':
            self.doc['controls']['paused'] = True
            self.doc['run']['status'] = 'paused'
        elif op == 'plan':
            self.doc['plan']['text'] = kwargs['text']

    def grant(self, owner, ttl_seconds):
        token = 'dispatch-' + str(len(self.grants))
        self.grants.append(token)
        return token

    def revoke(self, token):
        self.revoked.append(token)


class FakeReader:
    def __init__(self, store):
        self.store = store
        self.observations = 0
        self.results = {}
        self.broken = False
        self.surface = {'status': 'awaiting_agent', 'phase': 'absent'}
        self.legal = [{'action': 'start_business', 'arguments': {}}]
        self.after_observe = None
        self.blocked = None

    def observe(self):
        self.observations += 1
        if self.broken:
            raise OSError('connection_lost')
        if self.after_observe:
            self.after_observe(self)
        return {'connected': True, 'can_act': True, 'blocked_command_id': self.blocked,
                'state': {'ready': True, 'surface': copy.deepcopy(self.surface),
                          'legal_actions': copy.deepcopy(self.legal)}}

    def status(self, command_id):
        return {'command_status': self.results[command_id]}


class FakeDriver:
    timeout_seconds = 10

    def __init__(self, store, reader, outcomes=None):
        self.store, self.reader = store, reader
        self.outcomes = list(outcomes or ['succeeded'])
        self.calls = []
        self.status = 'completed'
        self.disposition = 'acted'

    def run(self, prompt, token, cancelled):
        self.calls.append(json.loads(prompt.split('持久上下文（不含调度令牌）：\n')[1].split('\n本轮开始')[0]))
        if self.outcomes:
            terminal = self.outcomes.pop(0)
            command_id = 'cmd-' + str(len(self.calls))
            self.store.doc['commands'].append({'command_id': command_id})
            self.store.doc['budget']['writes'] += 1
            self.reader.results[command_id] = terminal
        return {'status': self.status, 'answer': {'plan': '保留日结资金',
                'summary': '我已经全部完成了', 'disposition': self.disposition}}


class SupervisorTests(unittest.TestCase):
    def setUp(self):
        self.folder = ROOT / 'artifacts/m5c/test-tmp' / uuid.uuid4().hex
        self.folder.mkdir(parents=True)
        self.store = MemoryStore(self.folder / 'run.json')
        self.reader = FakeReader(self.store)
        self.driver = FakeDriver(self.store, self.reader, ['succeeded'] * 3)

    def tearDown(self):
        shutil.rmtree(self.folder)

    def supervise(self, **kwargs):
        return Supervisor(self.store, self.reader, self.driver, sleep=lambda _: None,
                          poll_seconds=.01, **kwargs).run()

    def test_multiple_decisions_refresh_context_without_model_completion(self):
        result = self.supervise(max_turns=3)
        self.assertEqual(result['reason'], 'turn_limit_checkpoint')
        self.assertEqual(len(self.driver.calls), 3)
        self.assertEqual([c['budget']['writes'] for c in self.driver.calls], [0, 1, 2])
        self.assertGreaterEqual(self.reader.observations, 9)
        self.assertEqual(self.store.revoked, self.store.grants)
        self.assertNotEqual(result['status'], 'completed')

    def test_operator_pause_and_resume_preserves_budget_and_plan(self):
        self.supervise(max_turns=1)
        result = self.supervise(max_turns=1)
        self.assertEqual(result['reason'], 'operator_control')
        self.assertEqual(len(self.driver.calls), 1)
        self.store.doc['controls']['paused'] = False
        self.store.doc['run']['status'] = 'running'
        self.supervise(max_turns=1)
        self.assertEqual(self.driver.calls[-1]['budget']['writes'], 1)
        self.assertEqual(self.driver.calls[-1]['plan']['text'], '保留日结资金')

    def test_unknown_input_blocks_new_decision(self):
        self.driver.outcomes = ['outcome_unknown', 'succeeded']
        result = self.supervise(max_turns=3)
        self.assertEqual(result['reason'], 'command_outcome_unknown')
        self.assertEqual(len(self.driver.calls), 1)
        self.assertEqual(self.store.doc['budget']['writes'], 1)

    def test_rejected_input_is_not_retried(self):
        self.driver.outcomes = ['rejected', 'succeeded']
        self.assertEqual(self.supervise()['reason'], 'command_rejected')
        self.assertEqual(len(self.driver.calls), 1)

    def test_reply_lost_with_successful_journal_does_not_replay(self):
        self.driver.status = 'failed'
        result = self.supervise()
        self.assertEqual(result['reason'], 'agent_failed')
        self.assertEqual(len(self.driver.calls), 1)
        self.assertEqual(self.reader.results, {'cmd-1': 'succeeded'})

    def test_driver_timeout_and_cancel_preserve_uncertain_input(self):
        for status in ('timed_out', 'cancelled'):
            with self.subTest(status=status):
                self.store.doc['controls']['paused'] = False
                self.store.doc['run']['status'] = 'running'
                self.driver.status = status
                self.driver.outcomes = []
                result = self.supervise()
                self.assertEqual(result['reason'], 'agent_' + status)

    def test_unknown_manual_and_world_blockers_prevent_spawn(self):
        for status in ('blocked_unknown', 'awaiting_user', 'world_mismatch'):
            self.store.doc['run']['status'] = status
            result = self.supervise()
            self.assertEqual(result['reason'], status)
        self.assertEqual(self.driver.calls, [])

    def test_budget_exhaustion_prevents_spawn(self):
        self.store.doc['budget']['writes'] = 10
        self.assertEqual(self.supervise()['reason'], 'budget_exhausted')
        self.assertEqual(self.driver.calls, [])

    def test_disconnected_broker_fails_closed(self):
        self.reader.broken = True
        self.assertEqual(self.supervise()['reason'], 'broker_unavailable_or_invalid')
        self.assertEqual(self.driver.calls, [])

    def test_unsupported_is_not_day_complete(self):
        self.reader.legal = []
        self.reader.surface['status'] = 'unsupported'
        result = self.supervise()
        self.assertEqual(result['reason'], 'unsupported')
        self.assertNotEqual(result['status'], 'completed')

    def test_progressing_is_bounded(self):
        self.reader.legal = []
        self.reader.surface['status'] = 'progressing'
        result = self.supervise(max_waits=3)
        self.assertEqual(result['reason'], 'bounded_wait_exhausted')
        self.assertEqual(self.reader.observations, 4)
        self.assertEqual(self.driver.calls, [])

    def test_model_no_action_is_bounded(self):
        self.driver.outcomes = []
        self.driver.disposition = 'wait'
        self.assertEqual(self.supervise(max_waits=2)['reason'], 'agent_no_progress')
        self.assertEqual(len(self.driver.calls), 2)

    def test_actual_completion_from_store_stops_next_day(self):
        def complete_after_one(reader):
            if reader.store.doc['budget']['writes'] == 1:
                reader.store.doc['run']['status'] = 'completed'
        self.reader.after_observe = complete_after_one
        result = self.supervise()
        self.assertEqual(result['reason'], 'verified_target_completed')
        self.assertEqual(len(self.driver.calls), 1)

    def test_same_task_has_one_supervisor_owner(self):
        with OwnerLock(self.store.path):
            with self.assertRaises(SupervisorError):
                self.supervise()
        self.assertEqual(self.driver.calls, [])

    def test_second_start_cannot_resume_existing_owner(self):
        self.store.doc['controls']['paused'] = True
        with OwnerLock(self.store.path):
            with self.assertRaises(SupervisorError):
                Supervisor(self.store, self.reader, self.driver).run(resume=True)
        self.assertTrue(self.store.doc['controls']['paused'])
        self.assertEqual(self.driver.calls, [])

    def test_prompt_separates_plan_authority_and_evidence(self):
        prompt = agent_prompt(self.store.snapshot(), self.reader.observe())
        self.assertIn('历史计划不是游戏事实', prompt)
        self.assertIn('本轮最多提交一个新的 act', prompt)
        self.assertIn('禁止 resume_inspection_handoff', prompt)
        self.assertNotIn('dispatch-', prompt)

    def test_operator_confirmation_is_not_authenticity_evidence(self):
        self.store.doc['operator_confirmation'] = {
            'handoff_ref': 'current-handoff', 'command_id': None,
            'instruction': '直接当正品一样买入这款香烟',
        }
        prompt = agent_prompt(self.store.snapshot(), self.reader.observe())
        self.assertIn('current-handoff', prompt)
        self.assertIn('这不是鉴定为真品的证据', prompt)
        self.assertIn('允许一次 resume_inspection_handoff', prompt)
        self.assertNotIn('禁止 resume_inspection_handoff、', prompt)


if __name__ == '__main__':
    unittest.main()
