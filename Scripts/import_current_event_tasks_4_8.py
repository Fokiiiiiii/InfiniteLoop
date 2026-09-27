"""Project the 4.8 time-limit event tasks into the CurrentTask alias tables.

client/activity/Activity.json rows 400 and 416..428 open UiActivityBase task panels on
TaskTimeLimit groups 768..779; share/trust/CharacterStoryActivity 1421003 (Adelyde) uses
group 762. The client lists those tasks only from server task data. TaskModule serves such
rows through CurrentTask/CurrentCondition/CurrentReward(Goods) and gates them by the group
TimeId, so LoginVisible=1 is the local login-visibility policy.
Existing alias rows are never rewritten. Writes
.runtime/upgrade-4.8/current-event-tasks-manifest-fragment.json.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import parse, project, render
from import_theatre6_4_8 import SOURCE, TARGET

FRAGMENT = Path('.runtime/upgrade-4.8/current-event-tasks-manifest-fragment.json')
PANEL_GROUPS = range(768, 780)
STORY_GROUPS = (762,)
GROUPS = (*PANEL_GROUPS, *STORY_GROUPS)


def load(name):
    return json.loads((SOURCE / f'share/{name}.json').read_text())


def main():
    panel_groups = {row['Params'][1] for row in json.loads((SOURCE / 'client/activity/Activity.json').read_text())
                    if len(row.get('Params') or []) > 1}
    if not set(PANEL_GROUPS) <= panel_groups:
        raise SystemExit(f'Activity.json no longer opens groups {sorted(set(PANEL_GROUPS) - panel_groups)}')
    story_groups = {row['TaskTimeLimitId'] for row in load('trust/CharacterStoryActivity')}
    if not set(STORY_GROUPS) <= story_groups:
        raise SystemExit('CharacterStoryActivity no longer uses group 762')
    limits = {row['Id']: row for row in load('task/TaskTimeLimit')}
    task_ids = {i for g in GROUPS for key in ('TaskId', 'DayTaskId', 'WeekTaskId') for i in limits[g].get(key) or []}
    tasks = [row for row in load('task/Task') if row['Id'] in task_ids]
    if len(tasks) != len(task_ids) or any(row['Type'] != 11 or len(row['Condition']) != 1 for row in tasks):
        raise SystemExit('unexpected 4.8 panel task shape')
    condition_ids = {row['Condition'][0] for row in tasks}
    reward_ids = {row['RewardId'] for row in tasks}
    rewards = [row for row in load('reward/Reward') if row['Id'] in reward_ids]
    goods_ids = {i for row in rewards for i in row['SubIds']}
    plan = {
        'task/CurrentTask': ('task/Task', tasks),
        'task/CurrentCondition': ('task/Condition', [r for r in load('task/Condition') if r['Id'] in condition_ids]),
        'task/CurrentReward': ('reward/Reward', rewards),
        'task/CurrentRewardGoods': ('reward/RewardGoods', [r for r in load('reward/RewardGoods') if r['Id'] in goods_ids]),
    }
    index_sha1 = json.loads((SOURCE / 'index-delta.json').read_text())['index_sha1']['document']
    fragment = {}
    for target, (source_name, rows) in plan.items():
        table = TARGET / f'share/{target}.tsv'
        old_text = table.read_text(encoding='utf-8-sig')
        header, current = parse(old_text)
        existing = {row['Id'] for row in current}
        new = [row for row in rows if str(row['Id']) not in existing]
        added = project(new, header, [], key='Id')
        if target == 'task/CurrentTask':
            for row in added:
                row['LoginVisible'] = '1'
        text = render(header, current + added)
        if text != old_text:
            table.write_text(text)
        source = SOURCE / f'share/{source_name}.json'
        raw = source.with_suffix('.tab').read_bytes()
        fragment[str(table)] = {
            'source_table': f'share/{source_name}.tab',
            'scope': 'document',
            'index_sha1': index_sha1,
            'raw_sha1': hashlib.sha1(raw).hexdigest(),
            'raw_size': len(raw),
            'decoded_json_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
            'rows': len(current) + len(added),
            'previous_rows': len(current),
            'added_rows': len(added),
            'added_ids': [int(row['Id']) for row in added],
            'tsv_sha256': hashlib.sha256(text.encode()).hexdigest(),
            'transformation': 'preserved-alias-keys + TaskTimeLimit 768..779 (Activity.json 4.8 panels) + 762 (CharacterStoryActivity 1421003) rows; existing rows untouched; CurrentTask LoginVisible=1',
        }
        print(f'{target}: {len(current)} -> {len(current) + len(added)} rows')
    FRAGMENT.write_text(json.dumps(fragment, indent=2) + '\n')


if __name__ == '__main__':
    main()
