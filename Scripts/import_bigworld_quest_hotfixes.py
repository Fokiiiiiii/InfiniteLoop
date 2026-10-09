#!/usr/bin/env python3
"""Import the client's DlcFight quest-objective hotfixes into QuestObjectiveHotfix.tsv.

Source: xdlcquesthotfixmanager.lua (active, uncommented HotfixObjectiveIds: scriptId -> objectiveId) and each
active questhotfix/Hotfix_<scriptId>.lua OnStateInProgressFunc. The only call understood is
`proxy:SetActorInteractableComponentEnableByPlaceId(ETargetActorType.<Type>, <placeId>, true|false)`, optionally
under `if [not] proxy:IsActorInteractableComponentByPlaceId(<same type>, <same place>) then` (the guard is
idempotence only, the server applies a row only when the flag differs). Anything else fails loudly.

Usage: Scripts/import_bigworld_quest_hotfixes.py [--lua-root DIR]
"""
import argparse
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / 'Resources/table/share/statussyncfight/quest/QuestObjectiveHotfix.tsv'
FALLBACK = Path('/Volumes/Lucia/PGR-native-research/PGR_DATA/en/lua/dlcfight')

SET = re.compile(r'proxy:SetActorInteractableComponentEnableByPlaceId\(ETargetActorType\.(\w+), (\d+), (true|false)\)')
GUARD = re.compile(r'if (not )?proxy:IsActorInteractableComponentByPlaceId\(ETargetActorType\.(\w+), (\d+)\) then')


def find(root: Path, rel: str) -> Path:
    """Case-insensitive path lookup (installed bundle is lowercase, EN corpus is mixed case)."""
    path = root
    for part in rel.split('/'):
        match = [p for p in path.iterdir() if p.name.lower() == part.lower()]
        if not match:
            raise FileNotFoundError(f'{path}/{part}')
        path = match[0]
    return path


def strip_comments(text: str) -> str:
    return '\n'.join(line.split('--')[0].rstrip() for line in text.splitlines())


def function_body(text: str, name: str, script: str) -> str:
    match = re.search(rf'^\s*{name}\s*=\s*function\(obj, proxy\)\n(.*?)^\s{{4}}end,', text, re.S | re.M)
    if not match:
        raise ValueError(f'{script}: {name} not found')
    return match.group(1)


def parse_script(path: Path, target_types: dict) -> list:
    text = strip_comments(path.read_text(encoding='utf-8-sig'))
    hooks = set(re.findall(r'^\s{4}(OnState\w+Func)\s*=', text, re.M))
    if not hooks <= {'OnStateEnterFunc', 'OnStateInProgressFunc'}:
        raise ValueError(f'{path.name}: unsupported hooks {sorted(hooks)}')
    if function_body(text, 'OnStateEnterFunc', path.name).strip():
        raise ValueError(f'{path.name}: OnStateEnterFunc is not empty')
    rows, guard = [], None
    for line in function_body(text, 'OnStateInProgressFunc', path.name).splitlines():
        line = line.strip()
        if not line:
            continue
        if (m := GUARD.fullmatch(line)) and guard is None:
            guard = (m.group(1) is not None, m.group(2), int(m.group(3)))
        elif (m := SET.fullmatch(line)):
            kind, place, enable = m.group(1), int(m.group(2)), m.group(3) == 'true'
            if kind not in target_types:
                raise ValueError(f'{path.name}: unknown ETargetActorType.{kind}')
            # Guard must be the idempotence form: `if not Is(x) then Set(x,true)` / `if Is(x) then Set(x,false)`.
            if guard is None or guard[1:] != (kind, place) or guard[0] != enable:
                raise ValueError(f'{path.name}: set call is not under its idempotence guard: {line}')
            rows.append((kind, place, enable))
        elif line == 'end' and guard is not None:
            guard = None
        else:
            raise ValueError(f'{path.name}: unsupported statement: {line}')
    if guard is not None:
        raise ValueError(f'{path.name}: unterminated if')
    return rows


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument('--lua-root', type=Path, default=REPO / '.runtime/installed-lua/dlcfight')
    root = ap.parse_args().lua_root
    if not (root / 'questhotfix').is_dir() and FALLBACK.joinpath('questhotfix').is_dir():
        root = FALLBACK
    manager = find(root, 'xdlcquesthotfixmanager.lua').read_text(encoding='utf-8-sig')
    ids = strip_comments(manager)
    block = re.search(r'local HotfixObjectiveIds = \{(.*?)^\}', ids, re.S | re.M)
    if not block:
        raise ValueError('HotfixObjectiveIds not found')
    entries = re.findall(r'^\s*\[(\d+)\]\s*=\s*(\d+),?\s*$', block.group(1), re.M)
    if len(entries) != len([l for l in block.group(1).splitlines() if l.strip()]):
        raise ValueError('HotfixObjectiveIds has lines this importer does not understand')
    enum = re.search(r'ETargetActorType\s*=\s*\{(.*?)\}', find(root, 'xdlcfightenum.lua').read_text(encoding='utf-8-sig'), re.S)
    target_types = {k: v for k, v in re.findall(r'(\w+)\s*=\s*(\d+)', enum.group(1)) if k in ('Npc', 'SceneObject')} if enum else {}
    lines, row_id = [], 0
    for script_id, objective_id in entries:
        for kind, place, enable in parse_script(find(root, f'questhotfix/hotfix_{script_id}.lua'), target_types):
            row_id += 1
            lines.append(f'{row_id}\t{script_id}\t{objective_id}\t{kind}\t{place}\t{int(enable)}')
    OUT.write_text('Id\tScriptId\tObjectiveId\tTargetType\tPlaceId\tEnable\n' + '\n'.join(lines) + '\n', encoding='utf-8')
    print(f'{OUT}: {row_id} rows from {root}')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, FileNotFoundError) as e:
        sys.exit(f'import_bigworld_quest_hotfixes: {e}')
