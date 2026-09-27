"""Import installed 4.8 share/passport tables onto the existing server TSV schemas.

Same projection as import_theatre6_4_8 (existing columns, local-only cells/rows kept).
Writes .runtime/upgrade-4.8/passport-manifest-fragment.json.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import parse, project, render
from import_theatre6_4_8 import SOURCE, TARGET, widen

FRAGMENT = Path('.runtime/upgrade-4.8/passport-manifest-fragment.json')


def main():
    index_sha1 = json.loads((SOURCE / 'index-delta.json').read_text())['index_sha1']['document']
    fragment = {}
    for source in sorted(SOURCE.glob('share/passport/*.json')):
        name = source.relative_to(SOURCE).with_suffix('')
        table = TARGET / f'{name}.tsv'
        old_text = table.read_text(encoding='utf-8-sig')
        old_header, current = parse(old_text)
        rows = json.loads(source.read_text())
        header = widen(old_header, rows)
        key = header[0]
        projected = project(rows, header, current, key=key)
        seen = {row[key] for row in projected}
        projected += [{c: row.get(c, '') for c in header} for row in current if row[key] not in seen]
        text = render(header, projected)
        if text != old_text:
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
            'tsv_sha256': hashlib.sha256(text.encode()).hexdigest(),
            'transformation': 'import_client_tables.project onto existing server columns; local-only cells/rows kept',
        }
        print(f'{name}: {len(current)} -> {len(projected)} rows' + (' (changed)' if text != old_text else ''))
    FRAGMENT.write_text(json.dumps(fragment, indent=2) + '\n')


if __name__ == '__main__':
    main()
