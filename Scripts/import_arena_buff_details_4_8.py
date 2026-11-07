"""Import the EN 4.8 War Zone support-buff details table and update table provenance.

Run from the AscNet root: python3 Scripts/import_arena_buff_details_4_8.py [--source /path/to/en/bytes]
The server reads each support buff's affix icon to pair ArenaGroupFightEvent groups with a zone's element.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from import_client_tables import project, render
from import_team_recommend_4_8 import expand

SOURCE = Path('/Volumes/Lucia/PGR-native-research/PGR_DATA/en/bytes')
TARGET = Path('Resources/table')
LOGICAL = 'client/fuben/arena/ArenaAreaBuffDetails'
SPEC = 'Id Name BuffBg Desc Icon'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    source = parser.parse_args().source / f'{LOGICAL}.json'
    manifest_path = TARGET / 'manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    raw = source.read_bytes()
    rows = json.loads(raw)
    header = expand(SPEC)
    unknown = {field for row in rows for field in row} - {column.split('[')[0] for column in header}
    if unknown:
        raise ValueError(f'unprojected source fields {sorted(unknown)}')
    output = render(header, project(rows, header, [], key='Id'))
    relative = f'{LOGICAL}.tsv'
    entry = {
        'path': relative,
        'rows': len(rows),
        'columns': len(header),
        'naturalKey': 'Id',
        'filter': 'all',
        'contentSha256': hashlib.sha256(output.encode('utf-8')).hexdigest(),
        'source': [f'{source} (sha256:{hashlib.sha256(raw).hexdigest()})', 'Scripts/import_arena_buff_details_4_8.py'],
        'transformation': ['all authored rows and fields in source order; omitted values remain blank'],
    }
    table = TARGET / relative
    table.parent.mkdir(parents=True, exist_ok=True)
    table.write_text(output, encoding='utf-8')
    previous = next((i for i, value in enumerate(manifest['tables']) if value['path'] == relative), None)
    if previous is None:
        manifest['tables'].append(entry)
    else:
        manifest['tables'][previous] = entry
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(f'{relative}: {entry["rows"]} authored rows')


if __name__ == '__main__':
    main()
