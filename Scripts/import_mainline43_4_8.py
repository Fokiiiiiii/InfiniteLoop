"""Import installed 4.8 MainLine2 (chapter 43) and LifeTree tables onto existing server TSVs.

Full-table projection (manifest filter "all") via import_client_tables.project; local-only
cells and rows are kept, indexed columns widened when 4.8 authors longer lists.
Writes .runtime/upgrade-4.8/mainline43-manifest-fragment.json. `--check` only reports diffs.
"""
from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

from import_client_tables import parse, project, render
from import_theatre6_4_8 import widen

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')
FRAGMENT = Path('.runtime/upgrade-4.8/mainline43-manifest-fragment.json')
TABLES = {
    **{f'share/fuben/mainline2/MainLine2{n}': k for n, k in (
        ('Main', None), ('Chapter', None), ('StageGroup', None), ('Stage', None),
        ('Treasure', None), ('Achievement', None), ('ExhibitionChapter', None))},
    'share/lifetree/LifeTreeCharacter': None,
    'share/lifetree/LifeTreeChapter': None,
    'client/lifetree/LifeTreeClientConfig': None,
}


def main(check):
    index_sha1 = json.loads((SOURCE / 'index-delta.json').read_text())['index_sha1']['document']
    fragment = {}
    for name, key in TABLES.items():
        source = SOURCE / f'{name}.json'
        table = TARGET / f'{name}.tsv'
        old_text = table.read_text(encoding='utf-8-sig')
        old_header, current = parse(old_text)
        # Unindexed columns whose source list is always empty (e.g. ProgressConditions) render blank.
        rows = [{k: None if v == [] and k in old_header else v for k, v in r.items()}
                for r in json.loads(source.read_text())]
        header = widen(old_header, rows)
        key = key or header[0]
        projected = project(rows, header, current, key=key)
        seen = {row[key] for row in projected}
        projected += [{c: row.get(c, '') for c in header} for row in current if row[key] not in seen]
        text = render(header, projected)
        old = {r[key]: r for r in current}
        for row in projected:
            before = old.get(row[key])
            changed = {c: (before.get(c, '') if before else None, v) for c, v in row.items()
                       if not before or before.get(c, '') != v}
            if changed and before:
                print(f'  {name} {row[key]} changed {changed}')
            elif changed:
                print(f'  {name} {row[key]} added')
        if not check and text != old_text:
            table.write_text(text)
        raw = source.with_suffix('.tab').read_bytes()
        fragment[str(table)] = {
            'source_table': f'{name}.tab',
            'scope': 'document',
            'index_sha1': index_sha1,
            'raw_sha1': hashlib.sha1(raw).hexdigest(),
            'raw_size': len(raw),
            'decoded_json_sha256': hashlib.sha256(source.read_bytes()).hexdigest(),
            'rows': len(projected),
            'previous_rows': len(current),
            'added_rows': len(projected) - len(current),
            'widened_columns': [c for c in header if c not in old_header],
            'tsv_sha256': hashlib.sha256(text.encode()).hexdigest(),
            'transformation': 'import_client_tables.project onto existing server columns; local-only cells/rows kept',
        }
        print(f'{name}: {len(current)} -> {len(projected)} rows' + (' (changed)' if text != old_text else ''))
    if not check:
        FRAGMENT.write_text(json.dumps(fragment, indent=2) + '\n')


if __name__ == '__main__':
    main('--check' in sys.argv)
