"""Import 4.8 BossActivity (Wrathful Monsoon), character trials and SkipFunctional from installed source.

Rows whose values are unchanged (blank and 0 are equivalent) keep their current bytes;
changed and new rows are projected from source, in source order.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')
TABLES = [f'share/fuben/bossactivity/{name}' for name in (
    'BossActivity', 'BossActivityStory', 'BossChallenge', 'BossRobotGroup',
    'BossScoreLevel', 'BossScoreTime', 'BossSection', 'BossStarReward')] + [
    'share/fuben/teaching/TeachingActivity',
    'client/functional/SkipFunctional',
]


def same(a: str, b: str) -> bool:
    norm = lambda line: ['' if cell == '0' else cell for cell in line.split('\t')]
    return norm(a) == norm(b)


def main():
    for name in TABLES:
        source = SOURCE / f'{name}.json'
        table = TARGET / f'{name}.tsv'
        current = table.read_text().splitlines()
        header = current[0].split('\t')
        old = {line.split('\t')[0]: line for line in current[1:]}
        projected = render(header, project(json.loads(source.read_text()), header, [], key=header[0])).splitlines()[1:]
        keys = {line.split('\t')[0] for line in projected}
        missing = sorted(set(old) - keys)
        if missing:
            raise SystemExit(f'{name}: source dropped rows {missing}; refusing to guess')
        lines = [old[key] if key in old and same(old[key], line) else line
                 for line in projected for key in [line.split('\t')[0]]]
        output = '\n'.join([current[0], *lines]) + '\n'
        table.write_text(output)
        print(f'{name}\trows={len(lines)}\tsource_sha256={hashlib.sha256(source.read_bytes()).hexdigest()}'
              f'\ttsv_sha256={hashlib.sha256(output.encode()).hexdigest()}')


if __name__ == '__main__':
    main()
