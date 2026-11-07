"""Import the EN client share tables behind favorite-mail collection and liberation aureoles.

ExhibitionLimit.AureoleIds / FashionAureole: XExhibitionConfigs.GetAureoleListByCharacterId.
MailCollectionBoxActivity / FavoriteMail: XMailModel (activity time id, mail reward ids).
Only server-relevant columns are projected; presentation text stays client-side.
Appends manifest entries (idempotent by path).
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('../PGR_DATA/en/bytes/share')
TARGET = Path('Resources/table')
MANIFEST = TARGET / 'manifest.json'
TABLES = {
    'share/exhibition/ExhibitionLimit': ('Exhibition/ExhibitionLimit', 'CharacterId SetBallColor AureoleIds[3]'),
    'share/fashion/FashionAureole': ('Fashion/FashionAureole', 'Id EffectPath Icon'),
    'share/mailcollectionbox/MailCollectionBoxActivity': ('MailCollectionBox/MailCollectionBoxActivity', 'Id TimeId FavoriteMailIds[2]'),
    'share/mailcollectionbox/MailCollectionBoxFavoriteMail': ('MailCollectionBox/MailCollectionBoxFavoriteMail', 'Id TagId RewardIds[2]'),
}


def expand(spec):
    columns = []
    for field in spec.split():
        if '[' in field:
            name, count = field.removesuffix(']').split('[')
            columns.extend(f'{name}[{i}]' for i in range(1, int(count) + 1))
        else:
            columns.append(field)
    return columns


def main():
    manifest = MANIFEST.read_text()
    entries = []
    for name, (src, spec) in TABLES.items():
        source = SOURCE / f'{src}.json'
        rows = json.loads(source.read_text())
        header = expand(spec)
        output = render(header, project(rows, header, [], key=header[0]))
        table = TARGET / f'{name}.tsv'
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(output)
        print(f'{name}: {len(rows)} rows')
        if f'"path": "{name}.tsv"' in manifest:
            continue
        entries.append({
            'path': f'{name}.tsv',
            'rows': len(rows),
            'columns': len(header),
            'naturalKey': header[0],
            'contentSha256': hashlib.sha256(output.encode()).hexdigest(),
            'source': [f'EN bytes/share/{src}.json'],
            'sourceSha256': {source.name: hashlib.sha256(source.read_bytes()).hexdigest()},
            'filter': 'all rows; server-relevant columns only',
            'transformation': ['project listed columns; list columns use 1-based TSV indices'],
        })
    if entries:
        marker = manifest.index('\n  ],', manifest.index('"tables"'))
        text = ',\n'.join('    ' + json.dumps(e, indent=2).replace('\n', '\n    ') for e in entries)
        MANIFEST.write_text(manifest[:marker] + ',\n' + text + manifest[marker:])


if __name__ == '__main__':
    main()
