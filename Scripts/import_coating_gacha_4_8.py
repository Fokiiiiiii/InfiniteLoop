"""Import installed 4.8 share/gacha columns consumed by the server Gacha flow (GachaManager)."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from import_client_tables import project, render

SOURCE = Path('.runtime/upgrade-4.8/tables')
TARGET = Path('Resources/table')


def listed(rows, *fields):
    return [f'{field}[{i}]' for field in fields for i in range(1, max(len(row[field]) for row in rows) + 1)]


TABLES = {
    'share/gacha/Gacha': lambda rows: [
        'Id', 'TimeId', 'ConsumeId', 'ConsumeCount', *listed(rows, 'BtnGachaCount'), 'OrganizeId',
        'PropStartTimes', 'PropAdd', 'PropAddGroupId', *listed(rows, 'GroupId', 'Weight'),
        'ExchangeId', 'CourseRewardId', 'PropAddInitVal'],
    'share/gacha/GachaReward': lambda rows: [
        'Id', 'GroupId', 'UsableTimes', 'TemplateId', 'Count', 'Weight', 'Rare', 'RewardType'],
    'share/gacha/GachaCourseReward': lambda rows: ['Id', *listed(rows, 'LimitDrawTimes', 'RewardIds')],
    'share/gacha/GachaItemExchange': lambda rows: [
        'Id', 'BuyCountMax', *listed(rows, 'UseItemIds', 'UseItemCounts'), 'TotalBuyCountMax'],
    'share/gacha/GachaFashionSelfChoiceActivity': lambda rows: ['Id', 'TimeId', *listed(rows, 'GachaGroupIds')],
    'share/gacha/GachaFashionSelfChoiceGroup': lambda rows: ['Id', 'TimeId', *listed(rows, 'GachaIds')],
}


def main():
    for name, header_of in TABLES.items():
        source = SOURCE / f'{name}.json'
        rows = json.loads(source.read_text())
        header = header_of(rows)
        output = render(header, project(rows, header, [], key='Id'))
        table = TARGET / f'{name}.tsv'
        table.parent.mkdir(parents=True, exist_ok=True)
        table.write_text(output)
        print(f'{name}\trows={len(rows)}\tcolumns={len(header)}\tsource_sha256={hashlib.sha256(source.read_bytes()).hexdigest()}'
              f'\ttsv_sha256={hashlib.sha256(output.encode()).hexdigest()}')


if __name__ == '__main__':
    main()
