"""Import installed 4.8 Theatre6 tables onto the existing server TSV schemas.

Every staged share/client theatre6 + theatre6pvp table that already has a server TSV is
projected with import_client_tables.project (existing columns only; local-only cells and
local-only rows are kept). Indexed columns are widened when 4.8 authors longer lists.
Server policy tables with no retail source (Theatre6SkillPool, Theatre6AttrPackPool,
Theatre6PvpRankFight) and client-only tables with no server TSV are left untouched.
Writes a manifest fragment to .runtime/upgrade-4.8/theatre6-manifest-fragment.json.
"""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

from import_client_tables import parse, project, render

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')
FRAGMENT = Path('.runtime/upgrade-4.8/theatre6-manifest-fragment.json')
INDEXED = re.compile(r'^(.*?)\[(\d+)\]$')


def widen(header, rows):
    """Append indexed columns after the last existing index when the source list is longer."""
    header = list(header)
    for field in {m.group(1) for m in map(INDEXED.fullmatch, header) if m}:
        indexes = [int(m.group(2)) for m in map(INDEXED.fullmatch, header) if m and m.group(1) == field]
        start, last = min(indexes), max(indexes)
        need = max((len(r.get(field) or []) for r in rows if isinstance(r.get(field), list)), default=0)
        position = header.index(f'{field}[{last}]')
        for index in range(last + 1, start + need):
            position += 1
            header.insert(position, f'{field}[{index}]')
    return header


def main():
    index_sha1 = json.loads((SOURCE / 'index-delta.json').read_text())['index_sha1']['document']
    fragment = {}
    for source in sorted([*SOURCE.glob('*/theatre6/*.json'), *SOURCE.glob('*/theatre6pvp/*.json')]):
        name = source.relative_to(SOURCE).with_suffix('')
        table = TARGET / f'{name}.tsv'
        if not table.exists():
            continue
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
            'widened_columns': [c for c in header if c not in old_header],
            'tsv_sha256': hashlib.sha256(text.encode()).hexdigest(),
            'transformation': 'import_client_tables.project onto existing server columns; local-only cells/rows kept',
        }
        print(f'{name}: {len(current)} -> {len(projected)} rows' + (' (changed)' if text != old_text else ''))
    FRAGMENT.write_text(json.dumps(fragment, indent=2) + '\n')


if __name__ == '__main__':
    main()
