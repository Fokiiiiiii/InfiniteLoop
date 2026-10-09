#!/usr/bin/env python3
"""Import the BigWorld sectors' static BoxColliders (statussyncfight/scenecollider) onto SceneCollider.tsv.

Source: .runtime/upgrade-4.8/tables/share/statussyncfight/scenecollider/<sector>/*.json (installed 4.8 share table).
Only the skygarden* sectors (BigWorld) and BoxCollider rows are kept: the rocks, cliffs and invisible walls that bound a level.
A box's world half extents are Size * Scale / 2; Id is unique per source file.

Run from the AscNet root: python3 Scripts/import_scene_colliders_4_8.py
"""
import json
from pathlib import Path

SOURCE = Path('.runtime/upgrade-4.8/tables/share/statussyncfight/scenecollider')
TABLE = Path('Resources/table/share/statussyncfight/scenecollider/SceneCollider.tsv')
COLUMNS = ['Sector', 'Scene', 'ColliderId', 'PosX', 'PosY', 'PosZ', 'QuatX', 'QuatY', 'QuatZ', 'QuatW', 'ScaleX', 'ScaleY', 'ScaleZ', 'SizeX', 'SizeY', 'SizeZ']
SOURCE_KEYS = ['PositionX', 'PositionY', 'PositionZ', 'QuaternionX', 'QuaternionY', 'QuaternionZ', 'QuaternionW', 'ScaleX', 'ScaleY', 'ScaleZ', 'SizeX', 'SizeY', 'SizeZ']

lines = ['\t'.join(COLUMNS)]
for sector in sorted(p for p in SOURCE.glob('skygarden*') if p.is_dir()):
    for file in sorted(sector.glob('*.json')):
        for row in json.loads(file.read_text(encoding='utf-8')):
            if row['Type'] == 'BoxCollider':
                lines.append('\t'.join([sector.name, file.stem, str(row['Id']), *(repr(float(row[k])) for k in SOURCE_KEYS)]))
TABLE.parent.mkdir(parents=True, exist_ok=True)
TABLE.write_text('\n'.join(lines) + '\n', encoding='utf-8')
print(f'{TABLE}: {len(lines) - 1} rows')
