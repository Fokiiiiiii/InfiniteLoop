"""Import installed 4.8 FestivalActivity and ExperimentLevel columns used for the login clear-state projections."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')


def festival_header(rows):
    width = max(len(row['StageId']) for row in rows)
    return ['Id', 'TimeId', *(f'StageId[{i}]' for i in range(1, width + 1))]


TABLES = {
    'share/fuben/festival/FestivalActivity': festival_header,
    'share/fuben/experiment/ExperimentLevel': lambda rows: ['Id', 'Type', 'SingStageId', 'MultStageId', 'TimeId'],
}


def main():
    for name, header_of in TABLES.items():
        source = SOURCE / f'{name}.json'
        rows = json.loads(source.read_text())
        output = render(header_of(rows), project(rows, header_of(rows), [], key='Id'))
        table = TARGET / f'{name}.tsv'
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(output)
        print(f'{name}\trows={len(rows)}\tsource_sha256={hashlib.sha256(source.read_bytes()).hexdigest()}'
              f'\ttsv_sha256={hashlib.sha256(output.encode()).hexdigest()}')


if __name__ == '__main__':
    main()
