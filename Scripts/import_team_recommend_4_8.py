"""Import the installed 4.8 recommendation tables, preserving all authored fields."""
from __future__ import annotations

import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')
TABLES = {
    'share/teamrecommend/TeamRecommendBaseCharacter': 'Id CharacterId WeaponId WeaponOverrunChoseSuit WeaponResonanceTypes[3] WeaponResonanceSkillIds[3] EquipIds[6] EquipResonanceTypes[12] EquipSkillIds[12] SuitIds[3] SuitCnts[3] CharacterQualityStar PartnerId',
    'share/teamrecommend/TeamRecommendBaseFormation': 'Id CharacterId FormationId Desc BaseCharacterIds[3] Order',
    'share/teamrecommend/TeamRecommendCharacterTarget': 'CharacterId BaseCharacterIds[6] TargetName[6]',
    'share/teamrecommend/TeamRecommendFormation': 'Id Name Desc Order Tags[2] FormationType StageType NeedLevel NeedScore MinCharacterQualityStar MaxCharacterQualityStar',
    'share/teamrecommend/TeamRecommendConfig': 'Key Desc Values[26]',
    'client/teamrecommend/TeamRecommendProgressWeight': 'Key Desc Weight',
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
    for name, spec in TABLES.items():
        source = SOURCE / f'{name}.json'
        rows = json.loads(source.read_text())
        header = expand(spec)
        table = TARGET / f'{name}.tsv'
        output = render(header, project(rows, header, [], key=header[0]))
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(output)
        print(f'{name}: {len(rows)} authored rows')


if __name__ == '__main__':
    main()
