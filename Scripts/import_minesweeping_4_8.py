"""Import the installed 4.8 Cosmic Wonders Mine Sweeping tables, preserving all authored fields."""
from __future__ import annotations

import json
from pathlib import Path

from import_client_tables import project, render
from import_team_recommend_4_8 import expand

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')
TABLES = {
    'share/miniactivity/minesweepinggame/MineSweepingActivity': 'Id TimeId CoinItemId ChapterIds[4]',
    'share/miniactivity/minesweepinggame/MineSweepingChapter': 'Id Name NameEn ActivityStageIds[2] ShowActivityStageIds[2] CompleteStoryId CompletePicture AllMinePicture MineEffect WinGridEffect MineIcon',
    'share/miniactivity/minesweepinggame/MineSweepingStage': 'Id Name WinEffect CostCoinNum RowCount ColumnCount RewardId CanFailedCounts[4]',
}


def main():
    for name, spec in TABLES.items():
        rows = json.loads((SOURCE / f'{name}.json').read_text())
        header = expand(spec)
        table = TARGET / f'{name}.tsv'
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(render(header, project(rows, header, [], key=header[0])))
        print(f'{name}: {len(rows)} authored rows')


if __name__ == '__main__':
    main()
