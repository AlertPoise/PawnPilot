"""用户侧运行管理 CLI；授权变更不会暴露为经营 Agent 的 MCP 工具。"""
from __future__ import annotations
import argparse
import json
import shutil
import sys
from pathlib import Path
from bridge.run_store import RunStore, RunStoreError
from .runtime import BrokerReader, Supervisor, SupervisorError

ROOT = Path(__file__).resolve().parents[1]


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', required=True, help='Run Context JSON path')
    parser.add_argument('--config', required=True, help='Pinned Broker session.json path')
    sub = parser.add_subparsers(dest='operation', required=True)
    init = sub.add_parser('create')
    init.add_argument('--run-id', required=True)
    init.add_argument('--save-label', required=True)
    init.add_argument('--authorization', required=True)
    init.add_argument('--days', type=int, default=1)
    start = sub.add_parser('start')
    start.add_argument('--model', default='gpt-6-luna')
    start.add_argument('--max-turns', type=int, default=40)
    start.add_argument('--timeout', type=int, default=180)
    for op in ('status', 'inbox', 'budget', 'pause', 'resume', 'stop'):
        sub.add_parser(op)
    amend = sub.add_parser('amend')
    amend.add_argument('--authorization', required=True)
    amend.add_argument('--expected-revision', type=int, required=True)
    rebind = sub.add_parser('rebind')
    rebind.add_argument('--confirm-world', required=True)
    rebind.add_argument('--expected-revision', type=int, required=True)
    confirm = sub.add_parser('confirm-inspection', help='Record the user decision for one current handoff')
    confirm.add_argument('--handoff-ref', required=True)
    confirm.add_argument('--instruction', required=True)
    confirm.add_argument('--expected-revision', type=int)
    args = parser.parse_args(argv)
    store = RunStore(args.run)
    config_path = Path(args.config).resolve()
    config = json.loads(config_path.read_text(encoding='utf-8'))
    if Path(config.get('run_context', '')).resolve() != Path(args.run).resolve():
        raise SupervisorError('broker_config_must_pin_run_context')
    reader = BrokerReader(config_path)
    if args.operation == 'create':
        auth = json.loads(Path(args.authorization).read_text(encoding='utf-8'))
        if set(auth) != {'limits', 'allowed_actions', 'goal'}:
            raise ValueError('invalid_authorization_shape')
        snap = reader.observe()
        state = snap['state']
        if not snap['connected'] or not state or not state['ready'] or snap['blocked_command_id']:
            raise SupervisorError('creation_requires_ready_unblocked_world')
        start_day = state['business']['details']['day']['day_number']
        store.create(args.run_id, args.save_label, str(config_path.with_name('broker-journal.json')),
                     state['world_epoch'], snap['session_id'], start_day, start_day + args.days,
                     auth['limits'], auth['allowed_actions'], auth['goal'])
        result = store.snapshot()
    elif args.operation in ('status', 'inbox', 'budget'):
        # 状态读取不消费或解决Inbox；Broker可达时先对账。
        try:
            reader.observe()
        except Exception:
            pass
        result = store.snapshot()
        if args.operation != 'status':
            result = result[args.operation]
    elif args.operation == 'start':
        from .codex_driver import CodexDriver
        ctx = store.snapshot()
        codex = shutil.which('codex')
        if not codex:
            raise SupervisorError('codex_not_found')
        driver = CodexDriver(codex_path=codex, python_path=sys.executable, repo_root=ROOT,
                             config_path=config_path,
                             workspace=ROOT / 'artifacts/m5c/agents' / ctx['run']['run_id'],
                             model=args.model, timeout_seconds=args.timeout)
        result = Supervisor(store, reader, driver, max_turns=args.max_turns).run(resume=True)
    elif args.operation == 'amend':
        auth = json.loads(Path(args.authorization).read_text(encoding='utf-8'))
        if set(auth) != {'limits', 'allowed_actions', 'goal'}:
            raise ValueError('invalid_authorization_shape')
        store.control('amend', args.expected_revision, limits=auth['limits'],
                      allowed_actions=auth['allowed_actions'])
        result = store.snapshot()
    elif args.operation == 'confirm-inspection':
        snap = reader.observe()
        state = snap.get('state')
        handoff = (state or {}).get('business', {}).get('details', {}).get('inspection_handoff')
        if (not snap['connected'] or not state or not state['ready'] or not handoff
                or handoff.get('handoff_ref') != args.handoff_ref):
            raise SupervisorError('fresh_handoff_confirmation_required')
        revision = (args.expected_revision if args.expected_revision is not None
                    else store.snapshot()['revision'])
        store.control('confirm_inspection', revision, state=state,
                      session_id=snap['session_id'], instruction=args.instruction)
        result = store.snapshot()
    elif args.operation == 'rebind':
        snap = reader.observe()
        state = snap.get('state')
        if not snap['connected'] or not state or not state['ready'] or state['world_epoch'] != args.confirm_world:
            raise SupervisorError('fresh_world_confirmation_required')
        store.control('rebind', args.expected_revision, world_epoch=state['world_epoch'],
                      session_id=snap['session_id'], day_number=state['business']['details']['day']['day_number'])
        result = store.snapshot()
    else:
        ctx = store.snapshot()
        store.control(args.operation, ctx['revision'])
        result = store.snapshot()
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (RunStoreError, SupervisorError, OSError, ValueError) as exc:
        print(json.dumps({'error': str(exc)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)
