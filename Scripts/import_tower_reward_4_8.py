"""Import installed 4.8 ChatBoard rows onto the existing server schema (Overclock Simulation rank reward).

Reward 69589 -> RewardGoods 695890 -> ChatBoard 25000015. Every authored row is projected
onto the existing server columns with import_client_tables.project; local-only rows and
cells are kept. Source .tab sha1/size are validated before anything is written.
Writes provenance to .runtime/upgrade-4.8/tower-reward-manifest-fragment.json.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import parse, project, render

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')
FRAGMENT = Path('.runtime/upgrade-4.8/tower-reward-manifest-fragment.json')
NAME = 'share/chat/ChatBoard'
REQUIRED = 25000015


def main():
    entry = json.loads((SOURCE / 'manifest.json').read_text())[f'{NAME.lower()}.tab']
    source = SOURCE / f'{NAME}.json'
    raw = source.with_suffix('.tab').read_bytes()
    if (hashlib.sha1(raw).hexdigest(), len(raw)) != (entry['sha1'], entry['size']):
        raise ValueError(f'{NAME}.tab does not match installed manifest sha1/size {entry["sha1"]}/{entry["size"]}')
    rows = json.loads(source.read_text())
    if not any(row['Id'] == REQUIRED for row in rows):
        raise ValueError(f'source lacks required ChatBoard {REQUIRED}')
    table = TARGET / f'{NAME}.tsv'
    old_text = table.read_text(encoding='utf-8-sig')
    header, current = parse(old_text)
    projected = project(rows, header, current, key='Id')
    seen = {row['Id'] for row in projected}
    local = [row for row in current if row['Id'] not in seen]
    text = render(header, projected + local)
    table.write_text(text)
    FRAGMENT.write_text(json.dumps({str(table): {
        'source_table': f'{NAME}.tab',
        'bundle': entry['bundle'],
        'scope': entry['scope'],
        'index_sha1': entry['index_sha1'],
        'index_size': entry['index_size'],
        'raw_sha1': entry['sha1'],
        'raw_size': entry['size'],
        'baseline_sha1': entry.get('baseline_sha1'),
        'decoded_json_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
        'rows': len(projected) + len(local),
        'previous_rows': len(current),
        'local_only_rows': len(local),
        'columns': len(header),
        'naturalKey': 'Id',
        'required_by': 'TransfiniteTowerChapter.RankRewardIds -> Reward 69589 -> RewardGoods 695890',
        'tsv_sha256': hashlib.sha256(text.encode()).hexdigest(),
        'transformation': 'import_client_tables.project onto existing server columns; local-only cells/rows kept',
    }}, indent=2) + '\n')
    print(f'{table}: {len(current)} -> {len(projected) + len(local)} rows')


if __name__ == '__main__':
    main()
