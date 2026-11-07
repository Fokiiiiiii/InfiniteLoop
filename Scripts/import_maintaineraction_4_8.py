"""Import the EN 4.8 Operation Guardians (MaintainerAction) shared tables and update table provenance.

Run from the AscNet root: python3 Scripts/import_maintaineraction_4_8.py [--source /path/to/en/bytes]
Every authored row and field is imported. The event table's first row has no Id key (the
decoder omits the default 0); it is written with Id 0, which is how the client keys it.
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
FOLDER = 'share/fuben/maintaineraction'
TABLES = {
    'MaintainerActionConfig': ('Id', 'Id TimeId Name StoryId MaxDailyActionCount MaxFightWinCount MaxBoxCount '
                                     'MaxWarehouseFinishCount NodeCount MaxCardNumber MaxHandCardCount NodePoolId '
                                     'TeacherMailId StudentMailId'),
    'MaintainerActionLevel': ('Level', 'Level StageIds[13] BoxRewardIds[1]'),
    'MaintainerActionEvent': ('Id', 'Id Name IsRecord RecordText HintText DescText EventIcon'),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    source_root = parser.parse_args().source
    manifest_path = TARGET / 'manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    staged = []
    for name, (key, spec) in TABLES.items():
        logical = f'{FOLDER}/{name}'
        source = source_root / f'{logical}.json'
        raw = source.read_bytes()
        rows = json.loads(raw)
        if key == 'Id':
            for row in rows:
                row.setdefault('Id', 0)
        header = expand(spec)
        fields = {column.split('[')[0] for column in header}
        unknown = {field for row in rows for field in row} - fields
        if unknown:
            raise ValueError(f'{name}: unprojected source fields {sorted(unknown)}')
        output = render(header, project(rows, header, [], key=key))
        relative = f'{logical}.tsv'
        entry = {
            'path': relative,
            'rows': len(rows),
            'columns': len(header),
            'naturalKey': key,
            'filter': 'all',
            'contentSha256': hashlib.sha256(output.encode('utf-8')).hexdigest(),
            'source': [f'{source} (sha256:{hashlib.sha256(raw).hexdigest()})',
                       'Scripts/import_maintaineraction_4_8.py'],
            'transformation': [
                'all authored rows and fields in source order; lists expanded to one-based indexed columns; omitted values remain blank'
                + ('; the Id-less first event row is written as Id 0' if name == 'MaintainerActionEvent' else '')
            ],
        }
        staged.append((relative, output, entry))
    for relative, output, entry in staged:
        table = TARGET / relative
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(output, encoding='utf-8')
        previous = next((i for i, value in enumerate(manifest['tables']) if value['path'] == relative), None)
        if previous is None:
            manifest['tables'].append(entry)
        else:
            manifest['tables'][previous] = entry
        print(f'{relative}: {entry["rows"]} authored rows')
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
