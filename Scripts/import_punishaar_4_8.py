"""Import installed 4.8 Circuit Calculus (Punishaar) share tables used by the server runtime.

All authored rows and columns are projected with import_client_tables.project/render; list
fields become 1-based indexed columns sized to the longest authored list. PunishaarEnemy and
PunishaarEnemySkill are client presentation only and are not imported. Shop stock, CardGroup
membership and ShopGroup mapping have no distributed rows and stay server policy (no rows here).
Writes provenance to .runtime/upgrade-4.8/punishaar-manifest-fragment.json.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import parse, project, render

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')
FRAGMENT = Path('.runtime/upgrade-4.8/punishaar-manifest-fragment.json')
KEYS = {'PunishaarConfig': 'Key', 'PunishaarStageGroup': 'StageId', 'PunishaarStageContentGroup': 'StageId'}
TABLES = ['Activity', 'Config', 'Card', 'CardLevel', 'CardSale', 'Shop', 'StageGroup',
          'StageContentGroup', 'StageContent', 'Fight', 'EventGroup', 'EventReward']


def header_of(rows):
    fields = list(dict.fromkeys(field for row in rows for field in row))
    header = []
    for field in fields:
        lists = [row[field] for row in rows if isinstance(row.get(field), list)]
        if lists:
            # TableGeneratorV2 emits List<T> only for 2+ indexed columns.
            header += [f'{field}[{i}]' for i in range(1, max(2, *map(len, lists)) + 1)]
        else:
            header.append(field)
    return header


def main():
    authored = json.loads((SOURCE / 'manifest.json').read_text())
    fragment, staged = {}, []
    for short in TABLES:
        name = f'Punishaar{short}'
        key = KEYS.get(name, 'Id')
        source = SOURCE / 'share/punishaar' / f'{name}.json'
        entry = authored[f'share/punishaar/{name.lower()}.tab']
        raw = source.with_suffix('.tab').read_bytes()
        if (hashlib.sha1(raw).hexdigest(), len(raw)) != (entry['sha1'], entry['size']):
            raise ValueError(f'{name}.tab does not match installed manifest sha1/size {entry["sha1"]}/{entry["size"]}')
        rows = json.loads(source.read_text())
        header = header_of(rows)
        table = TARGET / 'share/punishaar' / f'{name}.tsv'
        current = []
        if table.exists():
            old_header, current = parse(table.read_text(encoding='utf-8-sig'))
            dropped = {row[key] for row in current} - {str(row[key]) for row in rows}
            if dropped:
                raise ValueError(f'{name}: source no longer authors existing rows {sorted(dropped)}')
            header += [column for column in old_header if column not in header]  # keep server/local columns
        text = render(header, project(rows, header, current, key=key))
        staged.append((table, text))
        fragment[str(table)] = {
            'source_table': f'share/punishaar/{name}.tab',
            'bundle': entry['bundle'],
            'scope': entry['scope'],
            'index_sha1': entry['index_sha1'],
            'raw_sha1': entry['sha1'],
            'raw_size': entry['size'],
            'decoded_json_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
            'rows': len(rows),
            'columns': len(header),
            'naturalKey': key,
            'tsv_sha256': hashlib.sha256(text.encode()).hexdigest(),
            'transformation': 'all authored rows/columns via import_client_tables.project; lists as 1-based indexed columns',
        }
    for table, text in staged:
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(text)
        print(f'{table}\ttsv_sha256={fragment[str(table)]["tsv_sha256"]}')
    FRAGMENT.write_text(json.dumps(fragment, indent=2) + '\n')


if __name__ == '__main__':
    main()
