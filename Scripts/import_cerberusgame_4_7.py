"""Import every authored EN 4.7 Cerberus Mode shared row and update table provenance.

Run from the AscNet root: python3 Scripts/import_cerberusgame_4_7.py [--source /path/to/en/bytes]
Only the client role mapping required for team authorization is imported.
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
FOLDER = 'share/fuben/cerberusgame'
TABLES = {
    'CerberusGameActivity': 'Id Name TimeId ShopId[2] ChapterIds[4]',
    'CerberusGameChallenge': 'Id ChapterId PreStageId CharacterIds[3] RobotIds[3] BuffIcon[2] BuffTitle[2] BuffDescs[2]',
    'CerberusGameChapter': 'Id Name TimeId OpenCondition IsClearData DefaultTeam[2]',
    'CerberusGameCommunication': 'Id Name BtnContent NpcName NpcHandIcon ContentsGroupId Type UiType',
    'CerberusGameStoryLine': 'Id ChapterId RobotIds[3] StoryPointIds[29] Name Bg FubenPrefab GirdStageFight GirdStageFightSpecial GridBossPrefab GridStory1 GridStory2 Difficulty OpenCondition',
    'CerberusGameStoryPoint': 'Id StoryPointType StoryPointTypeParams[4] Title Name GridShowType PrePointIds[3] BuffIcon[2] BuffTitle[2] BuffDescs[2] OpenCondition ExtraRobotId',
    'client/fuben/cerberusgame/CerberusGameRole': 'Id Chapter Difficulty RoleId',
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=SOURCE)
    source_root = parser.parse_args().source
    manifest_path = TARGET / 'manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    staged = []
    for name, spec in TABLES.items():
        logical = name if '/' in name else f'{FOLDER}/{name}'
        source = source_root / f'{logical}.json'
        raw = source.read_bytes()
        rows = json.loads(raw)
        header = expand(spec)
        fields = {column.split('[')[0] for column in header}
        unknown = {field for row in rows for field in row} - fields
        if unknown:
            raise ValueError(f'{name}: unprojected source fields {sorted(unknown)}')
        output = render(header, project(rows, header, [], key='Id'))
        relative = f'{logical}.tsv'
        entry = {
            'path': relative,
            'rows': len(rows),
            'columns': len(header),
            'naturalKey': 'Id',
            'filter': 'all',
            'contentSha256': hashlib.sha256(output.encode('utf-8')).hexdigest(),
            'source': [f'{source} (sha256:{hashlib.sha256(raw).hexdigest()})',
                       'Scripts/import_cerberusgame_4_7.py'],
            'transformation': [
                'all authored rows and fields in source order; lists expanded to one-based indexed columns; omitted values remain blank'
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
