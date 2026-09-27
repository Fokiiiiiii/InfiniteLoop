"""Import authored 4.8 trust/dorm rows (adds Kurumi 1411003 and Adelyde 1421003).

Every runtime row must still exist in source; the whole table is re-projected so
pre-4.8 rows keep their cells (blank-vs-zero policy lives in project()).
"""
from __future__ import annotations

import json
from pathlib import Path

from import_client_tables import parse, project, render

SOURCE = Path('.runtime/upgrade-4.8/tables/share')
TARGET = Path('Resources/table/share')
TABLES = {
    'trust/CharacterTrustExp': 'Id',
    'trust/CharacterTrustItem': 'Id',
    'dormitory/character/DormCharacterEvent': 'EventId',
    'dormitory/character/DormCharacterFondle': 'CharacterId',
    'dormitory/character/DormCharacterRecovery': 'Id',
    'dormitory/character/DormCharacterStyle': 'Id',
}
# Recovery AttrCondition is keyed by furniture AttrType-1 (xdormmanager.lua
# GetCharRecoveryCurLevel: Beauty 0, Comfort 1, Utility 2); keep all three slots.
ATTR = ['AttrCondition[1]', 'AttrCondition[2]', 'AttrCondition[3]']


def main():
    for name, key in TABLES.items():
        rows = json.loads((SOURCE / f'{name}.json').read_text())
        table = TARGET / f'{name}.tsv'
        header, current = parse(table.read_text())
        if 'AttrCondition[1]' in header:
            at = header.index('AttrCondition[1]')
            header = [c for c in header if not c.startswith('AttrCondition[')]
            header[at:at] = ATTR
            rows = [{**r, 'AttrCondition': [r['AttrCondition'][str(i)] for i in range(3)]} for r in rows]
        missing = {row[key] for row in current} - {str(r[key]) for r in rows}
        if missing:
            raise ValueError(f'{name}: runtime rows absent from source {sorted(missing)}')
        projected = project(rows, header, current, key=key)
        for row in projected:
            # Parsed lists drop blank cells; zero slots must stay positional.
            for column in ATTR if 'AttrCondition[1]' in header else []:
                row[column] = row[column] or '0'
        table.write_text(render(header, projected))


if __name__ == '__main__':
    main()
