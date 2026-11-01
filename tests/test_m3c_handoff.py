"""M3-C handoff protocol, write-gate and MCP behavior regressions."""
import copy
import json
from pathlib import Path
import sys
import unittest
from jsonschema import Draft202012Validator, ValidationError

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from bridge.protocol import (valid_action_binding_v3, valid_action_success_evidence_v3,
    valid_command_v3, valid_request_v3, valid_snapshot_v3)
from bridge.server import Broker, ProtocolError
from pawnpilot_mcp import server as mcp_server

BASE = json.loads((ROOT / 'protocol/fixtures/m3a-prepare_sale.json').read_text(encoding='utf-8'))
OLD_V3 = json.loads((ROOT / 'protocol/fixtures/v3-business-observation.json').read_text(encoding='utf-8'))['before']
SCHEMA = json.loads((ROOT / 'protocol/schemas/wire-v3.json').read_text(encoding='utf-8'))


def handoff_snapshot(status='waiting_for_user'):
    state = copy.deepcopy(BASE)
    handoff_ref = 'handoff-1'
    details = state['business']['details']
    details['inspection_handoff'] = {
        'requires_manual_inspection': status == 'waiting_for_user',
        'status': status,
        'handoff_ref': handoff_ref,
        'customer_ref': 'customer-trigger-history',
        'items': [{'item_ref': 'inspection-item-1', 'name': 'Hemingway cigarette pack'}],
        'reason': 'manual_item_inspection',
        'last_resumed_handoff_ref': handoff_ref if status == 'resumed' else None,
    }
    if status == 'waiting_for_user':
        state['can_close_menu'] = False
        state['interaction'].update(consistency='blocked', operable=False,
                                    reason='inspection_handoff')
        details['ui'].update(status='blocked', blockers=['inspection_handoff'])
        state['legal_actions'] = [{'action':'resume_inspection_handoff',
                                   'arguments':{'handoff_ref':handoff_ref}}]
    else:
        state['legal_actions'] = []
    return state


def command(action, args, state):
    return {'schema_version':3,'id':'resume-1','broker_epoch':'broker-1','action':action,
        'arguments':args,'expected_world_epoch':state['world_epoch'],
        'expected_revision':state['revision'],'expected_version':state['version'],
        'start_deadline_unix_ms':9999999999999,'execution_timeout_ms':5000}


def rebase_snapshot(state, revision):
    result=copy.deepcopy(state)
    result['revision']=revision
    result['version']=f"{result['world_epoch']}:{revision}"
    return result


class M3CHandoffTests(unittest.TestCase):
    def test_optional_handoff_is_schema2_only_and_old_schema2_history_without_it_stays_valid(self):
        history = copy.deepcopy(BASE)
        self.assertTrue(valid_snapshot_v3(history))
        self.assertNotIn('inspection_handoff', history['business']['details'])
        pending = handoff_snapshot()
        self.assertTrue(valid_snapshot_v3(pending))
        Draft202012Validator.check_schema(SCHEMA)
        Draft202012Validator({**SCHEMA,'$ref':'#/$defs/Snapshot'}).validate(pending)
        legacy = copy.deepcopy(OLD_V3)
        legacy['business']['details']['inspection_handoff'] = pending['business']['details']['inspection_handoff']
        self.assertFalse(valid_snapshot_v3(legacy))

    def test_pending_handoff_only_allows_matching_resume_even_with_menu_and_ui_blocker(self):
        pending = handoff_snapshot()
        self.assertEqual(pending['legal_actions'], [{'action':'resume_inspection_handoff',
            'arguments':{'handoff_ref':'handoff-1'}}])
        args = pending['legal_actions'][0]['arguments']
        self.assertTrue(valid_action_binding_v3(pending['business'],'resume_inspection_handoff',args))
        self.assertTrue(valid_snapshot_v3(pending))
        for action, arguments in (
            ('close_menu',{}), ('start_business',{}),
            ('resume_inspection_handoff',{'handoff_ref':'stale-handoff'}),
        ):
            changed=copy.deepcopy(pending)
            changed['legal_actions']=[{'action':action,'arguments':arguments}]
            self.assertFalse(valid_snapshot_v3(changed), action)
        with_wrong_command = command('resume_inspection_handoff', {'handoff_ref':'stale-handoff'}, pending)
        self.assertTrue(valid_request_v3({k:with_wrong_command[k] for k in (
            'schema_version','id','broker_epoch','action','expected_world_epoch','expected_revision','expected_version','arguments')}))
        self.assertFalse(valid_action_binding_v3(pending['business'],with_wrong_command['action'],with_wrong_command['arguments']))
        self.assertTrue(valid_command_v3(command('resume_inspection_handoff',args,pending)))
        self.assertFalse(valid_command_v3(command('resume_inspection_handoff',{'handoff_ref':'handoff-1','customer_ref':'x'},pending)))

    def test_broker_denies_close_purchase_and_bargain_with_handoff_reason_before_other_gates(self):
        class Clock:
            now=100.0
            def __call__(self): return self.now
            def monotonic(self): return self.now
        clock=Clock(); broker=Broker('broker-test-token',clock=clock,monotonic=clock.monotonic,heartbeat_timeout=60)
        state=handoff_snapshot()
        broker.exchange({'schema_version':3,'session_id':'handoff-session','broker_epoch':None,'state':state,
            'has_result':False,'result':None,'progress':None})
        def request(action,args,command_id):
            return {'schema_version':3,'id':command_id,'broker_epoch':broker.broker_epoch,'action':action,
                'expected_world_epoch':state['world_epoch'],'expected_revision':state['revision'],
                'expected_version':state['version'],'arguments':args}
        probes=(('close_menu',{}),
            ('purchase_visible_offer',{'customer_ref':'customer-m3a','offer_ref':'offer-1','item_refs':['item-1'],
                'collection_fingerprint':'fingerprint-1','mode':'normal','amount':10,'max_spend':10}),
            ('open_bargain_panel',{'customer_ref':'customer-m3a','mode':'sell'}))
        try:
            for i,(action,args) in enumerate(probes):
                with self.subTest(action=action), self.assertRaises(ProtocolError) as caught:
                    broker.enqueue(request(action,args,f'blocked-{i}'))
                self.assertEqual(caught.exception.reason,'inspection_handoff_pending')
        finally:
            broker.close()

    def test_broker_acks_native_action_whose_result_first_observes_handoff(self):
        class Clock:
            now=100.0
            def __call__(self): return self.now
            def monotonic(self): return self.now
        clock=Clock(); broker=Broker('broker-test-token',clock=clock,monotonic=clock.monotonic,heartbeat_timeout=60)
        before=copy.deepcopy(OLD_V3)
        before_body={'schema_version':3,'session_id':'native-session','broker_epoch':None,'state':before,
            'has_result':False,'result':None,'progress':None}
        broker.exchange(before_body)
        request={'schema_version':3,'id':'native-close','broker_epoch':broker.broker_epoch,'action':'close_menu',
            'expected_world_epoch':before['world_epoch'],'expected_revision':before['revision'],
            'expected_version':before['version'],'arguments':{}}
        try:
            broker.enqueue(request)
            delivery=broker.exchange({**before_body,'broker_epoch':broker.broker_epoch})
            action=delivery['command']
            self.assertTrue(delivery['has_command'])
            after=handoff_snapshot()
            after.update(world_epoch=before['world_epoch'],revision=before['revision']+1,
                version=f"{before['world_epoch']}:{before['revision']+1}",cash=before['cash'],
                phase=before['phase'],scene=before['scene'],loaded=True,ready=True,not_ready_reason=None,
                menu_open=False,can_close_menu=False)
            after['business']['details']['containers']=[]
            after['business']['details']['items']=[]
            customer=after['business']['details']['customer']
            customer.update(presence='absent',arrived=None,trade_type='unknown',phase='leaving',
                dialogue_ref=None,dialogue_stage='absent',option_refs=[],dialogue_text=None,
                is_typing=None,waiting_for_advance=None,options=[])
            after['business'].update(customer_ref=None,item_refs=[],offer_ref=None,collection_fingerprint=None)
            after['business']['details']['inspection_handoff']['customer_ref']='historical-customer'
            self.assertTrue(valid_snapshot_v3(after))
            started=100_100
            progress={'id':action['id'],'broker_epoch':broker.broker_epoch,'stage':'completed',
                'sequence':4,'may_have_side_effects':True,'started_at_unix_ms':started,
                'execution_deadline_unix_ms':started+action['execution_timeout_ms']}
            result={'schema_version':3,'id':action['id'],'broker_epoch':broker.broker_epoch,
                'status':'succeeded','reason':'menu_closed','before':before,'after':after,'execution':progress}
            ack=broker.exchange({'schema_version':3,'session_id':'native-session','broker_epoch':broker.broker_epoch,
                'state':after,'has_result':True,'result':result,'progress':progress})
            self.assertEqual(ack['acknowledged_result_id'],'native-close')
            self.assertEqual(broker.result('native-close')['command_status'],'succeeded')
        finally:
            broker.close()

    def test_pending_handoff_not_ready_has_no_write_action_and_bad_handoff_shapes_fail(self):
        pending=handoff_snapshot()
        pending.update(ready=False,not_ready_reason='loading')
        pending['interaction'].update(operable=False)
        pending['legal_actions']=[]
        self.assertTrue(valid_snapshot_v3(pending))
        invalid=handoff_snapshot()
        invalid['business']['details']['inspection_handoff']['items'][0]['item_ref']=''
        self.assertFalse(valid_snapshot_v3(invalid))
        invalid=handoff_snapshot()
        invalid['business']['details']['inspection_handoff']['status']='resumed'
        self.assertFalse(valid_snapshot_v3(invalid))
        invalid=handoff_snapshot()
        invalid['business']['details']['inspection_handoff']['items'][0]['name']=None
        # Unknown visible title is allowed; identifier and inspection reason still carry the event.
        self.assertTrue(valid_snapshot_v3(invalid))

    def test_resume_acknowledges_old_handoff_despite_user_changes_and_new_pending_handoff(self):
        before=handoff_snapshot()
        after=handoff_snapshot()
        after['revision']=before['revision']+1
        after['version']=f"{after['world_epoch']}:{after['revision']}"
        after['cash'] += 7
        after['business']['details']['items'][0]['units'] += 1
        next_handoff=after['business']['details']['inspection_handoff']
        next_handoff.update(handoff_ref='handoff-2',customer_ref='customer-next',
            items=[{'item_ref':'new-inspection-item','name':None}],
            last_resumed_handoff_ref='handoff-1')
        after['legal_actions']=[{'action':'resume_inspection_handoff','arguments':{'handoff_ref':'handoff-2'}}]
        self.assertTrue(valid_snapshot_v3(after))
        cmd=command('resume_inspection_handoff',{'handoff_ref':'handoff-1'},before)
        self.assertTrue(valid_action_success_evidence_v3(cmd,before,after))
        bad=copy.deepcopy(after)
        bad['business']['details']['inspection_handoff']['last_resumed_handoff_ref']='other-handoff'
        self.assertTrue(valid_snapshot_v3(bad))
        self.assertFalse(valid_action_success_evidence_v3(cmd,before,bad))

    def test_later_handoff_does_not_invalidate_an_already_started_native_action_result(self):
        before=copy.deepcopy(OLD_V3)
        after=handoff_snapshot()
        after.update(world_epoch=before['world_epoch'],revision=before['revision']+1,
                     version=f"{before['world_epoch']}:{before['revision']+1}",cash=before['cash'],
                     phase=before['phase'],scene=before['scene'],loaded=before['loaded'],ready=before['ready'],
                     not_ready_reason=None,menu_open=False,can_close_menu=False)
        after['business']['details']['containers'] = []
        after['business']['details']['items'] = []
        after['business']['details']['customer'].update(presence='absent',arrived=None,trade_type='unknown',
            phase='leaving',dialogue_ref=None,dialogue_stage='absent',option_refs=[],dialogue_text=None,
            is_typing=None,waiting_for_advance=None,options=[])
        after['business'].update(customer_ref=None,item_refs=[],offer_ref=None,collection_fingerprint=None)
        after['business']['details']['inspection_handoff']['customer_ref']='historical-customer'
        self.assertTrue(valid_snapshot_v3(before))
        self.assertTrue(valid_snapshot_v3(after))
        native_close={'action':'close_menu','arguments':{}}
        self.assertTrue(valid_action_success_evidence_v3(native_close,before,after))

    def test_mcp_observe_surfaces_handoff_without_changing_unknown_command_flag(self):
        pending=handoff_snapshot()
        snapshot=mcp_server._parse_snapshot({'schema_version':3,'broker_epoch':'broker-1',
            'session_id':'session-1','connected':True,'state':pending,'blocking_reason':None,
            'blocked_command_id':None,'can_act':True,'execution':None})
        from unittest.mock import patch
        with patch.object(mcp_server,'_snapshot',return_value=snapshot):
            observed=mcp_server.observe()
        self.assertEqual(observed.legal_actions,['resume_inspection_handoff'])
        self.assertFalse(observed.manual_action_required)
        self.assertTrue(observed.snapshot.state.business['details']['inspection_handoff']['requires_manual_inspection'])

    def test_mcp_resume_arguments_are_exact_and_reference_bounded(self):
        args=mcp_server.ActionArgumentsV3(handoff_ref='handoff-1')
        self.assertEqual(args.model_dump(mode='json',exclude_unset=True,exclude_none=True),
                         {'handoff_ref':'handoff-1'})
        with self.assertRaises(Exception):
            mcp_server.ActionArgumentsV3(handoff_ref='bad handoff')

class M3CProductionFullChainTests(unittest.TestCase):
    """C# classifier/projection export exercised through the actual MCP stdio process."""

    @classmethod
    def setUpClass(cls):
        import subprocess
        command = ['dotnet', 'tests/PawnPilot.CoreTests/bin/Release/net10.0/PawnPilot.CoreTests.dll',
                   '--export-inspection-fixtures']
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   timeout=60, check=True)
        cls.export = json.loads(completed.stdout)
        validator = Draft202012Validator({**SCHEMA,'$ref':'#/$defs/Snapshot'})
        for snapshot in cls.export['snapshots']:
            validator.validate(snapshot)
        validator.validate(cls.export['before'])
        Draft202012Validator({**SCHEMA,'$ref':'#/$defs/Command'}).validate(cls.export['resume_command'])

    def test_real_identifiers_flow_through_managed_projection_mcp_gate_and_resume(self):
        import asyncio
        import os
        import threading
        import time
        import sys
        from mcp import Client
        from mcp.client.stdio import StdioServerParameters
        from bridge.server import Broker, Server

        snapshots = self.export['snapshots']
        self.assertEqual(len(snapshots), 7, 'managed fixture exporter covers all seven verified identifiers')
        self.assertEqual(len({snapshot['business']['details']['inspection_handoff']['handoff_ref']
                              for snapshot in snapshots}),7)
        for produced in snapshots:
            self.assertTrue(valid_snapshot_v3(produced))
            handoff = produced['business']['details']['inspection_handoff']
            self.assertTrue(handoff['requires_manual_inspection'])
            self.assertEqual(handoff['status'], 'waiting_for_user')
            self.assertEqual(handoff['reason'], 'manual_item_inspection')

        snapshots=[rebase_snapshot(snapshot,index+1) for index,snapshot in enumerate(snapshots)]
        before = rebase_snapshot(self.export['before'],len(snapshots)+1)
        after = rebase_snapshot(self.export['new_pending_snapshot'],len(snapshots)+2)
        config_path = ROOT / 'tests/fixtures/m3c-mcp-session.json'
        token = json.loads(config_path.read_text(encoding='utf-8'))['token']
        broker = Broker(token, heartbeat_timeout=60)
        session_id = 'm3c-managed-export-session'
        initial = {'schema_version':3,'session_id':session_id,'broker_epoch':None,'state':snapshots[0],
                   'has_result':False,'result':None,'progress':None}
        broker.exchange(initial)
        http_server = Server(broker, port=0)
        server_thread = threading.Thread(target=http_server.serve_forever, daemon=True)
        server_thread.start()
        try:
            env = os.environ.copy()
            env['PAWNPILOT_CONFIG'] = str(config_path)
            env['PYTHONPATH'] = str(ROOT)
            bootstrap = ('import bridge.client as c; ' + f'c.PORT={http_server.server_port}; '
                         "import runpy; runpy.run_module('pawnpilot_mcp',run_name='__main__')")
            params = StdioServerParameters(command=sys.executable, args=['-c',bootstrap],
                                           env=env, cwd=ROOT)
            try:
                asyncio.run(self._exercise_mcp(params, broker, session_id, snapshots,before,after))
            except PermissionError as exc:
                if getattr(exc, 'winerror', None) != 5:
                    raise
                self.skipTest(f'host policy blocks launching the MCP stdio subprocess: {exc}')
        finally:
            http_server.shutdown()
            http_server.server_close()
            server_thread.join(timeout=5)

    @staticmethod
    async def _exercise_mcp(params, broker, session_id, snapshots, before, after):
        from mcp import Client
        import time
        async with Client(params, read_timeout_seconds=15) as mcp:
            state=snapshots[0]
            latest_observation=None
            for index,produced in enumerate(snapshots):
                observed_result=await mcp.call_tool('observe',{})
                assert not observed_result.is_error, observed_result.content
                latest_observation=observed_result.structured_content
                observed_state=latest_observation['snapshot']['state']
                handoff=observed_state['business']['details']['inspection_handoff']
                assert handoff['status']=='waiting_for_user'
                assert latest_observation['legal_actions']==['resume_inspection_handoff']
                assert latest_observation['manual_action_required'] is False
                assert observed_state['version']==produced['version']
                broker_epoch=latest_observation['snapshot']['broker_epoch']
                action_schema_version=latest_observation['action_schema_version']

                async def call_act(command_id, action, arguments):
                    result=await mcp.call_tool('act',{
                        'schema_version':action_schema_version,'command_id':command_id,
                        'action':action,'arguments':arguments,'broker_epoch':broker_epoch,
                        'expected_world_epoch':state['world_epoch'],'expected_revision':state['revision'],
                        'expected_version':state['version']})
                    assert not result.is_error,result.content
                    return result.structured_content

                purchase_args={'customer_ref':state['business']['customer_ref'] or handoff['customer_ref'],
                    'offer_ref':state['business']['offer_ref'] or 'inspection-offer',
                    'item_refs':state['business']['item_refs'] or [handoff['items'][0]['item_ref']],
                    'collection_fingerprint':state['business']['collection_fingerprint'] or 'inspection-collection',
                    'mode':'normal','amount':10,'max_spend':10}
                denied=await call_act(f'm3c-deny-purchase-{index}','purchase_visible_offer',purchase_args)
                assert denied['ok'] is False and denied['reason']=='inspection_handoff_pending'
                if index+1<len(snapshots):
                    state=snapshots[index+1]
                    exchange=broker.exchange({'schema_version':3,'session_id':session_id,
                        'broker_epoch':broker.broker_epoch,'state':state,'has_result':False,
                        'result':None,'progress':None})
                    assert exchange['has_command'] is False

            state=before
            exchange=broker.exchange({'schema_version':3,'session_id':session_id,
                'broker_epoch':broker.broker_epoch,'state':state,'has_result':False,
                'result':None,'progress':None})
            assert exchange['has_command'] is False
            observed_result=await mcp.call_tool('observe',{})
            assert not observed_result.is_error,observed_result.content
            latest_observation=observed_result.structured_content
            state=latest_observation['snapshot']['state']
            handoff_ref=state['business']['details']['inspection_handoff']['handoff_ref']
            action_schema_version=latest_observation['action_schema_version']
            broker_epoch=latest_observation['snapshot']['broker_epoch']

            async def call_act(command_id, action, arguments):
                result=await mcp.call_tool('act',{
                    'schema_version':action_schema_version,'command_id':command_id,
                    'action':action,'arguments':arguments,'broker_epoch':broker_epoch,
                    'expected_world_epoch':state['world_epoch'],'expected_revision':state['revision'],
                    'expected_version':state['version']})
                assert not result.is_error,result.content
                return result.structured_content

            close_denied=await call_act('m3c-deny-close','close_menu',{})
            assert close_denied['ok'] is False and close_denied['reason']=='inspection_handoff_pending'
            queued=await call_act('m3c-resume','resume_inspection_handoff',{'handoff_ref':handoff_ref})
            assert queued['ok'] is True and queued['command_status']=='pending'
            delivered=broker.exchange({'schema_version':3,'session_id':session_id,
                'broker_epoch':broker.broker_epoch,'state':state,'has_result':False,'result':None,'progress':None})
            command=delivered['command']
            assert delivered['has_command'] is True and command['action']=='resume_inspection_handoff'
            started=int(time.time()*1000)
            progress={'id':command['id'],'broker_epoch':broker.broker_epoch,'stage':'completed',
                'sequence':4,'may_have_side_effects':True,'started_at_unix_ms':started,
                'execution_deadline_unix_ms':started+command['execution_timeout_ms']}
            result={'schema_version':3,'id':command['id'],'broker_epoch':broker.broker_epoch,
                'status':'succeeded','reason':'inspection_handoff_resumed','before':state,'after':after,
                'execution':progress}
            broker.exchange({'schema_version':3,'session_id':session_id,'broker_epoch':broker.broker_epoch,
                'state':after,'has_result':True,'result':result,'progress':progress})
            current_result=await mcp.call_tool('observe',{})
            assert not current_result.is_error,current_result.content
            current=current_result.structured_content
            current_handoff=current['snapshot']['state']['business']['details']['inspection_handoff']
            assert current_handoff['handoff_ref'] != handoff_ref
            assert current_handoff['last_resumed_handoff_ref']==handoff_ref
            assert current['legal_actions']==['resume_inspection_handoff']
            assert current['manual_action_required'] is False


if __name__ == '__main__':
    unittest.main()
