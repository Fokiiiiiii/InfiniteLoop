#!/usr/bin/env python3
"""Independent check of Resources/table/share/statussyncfight/quest/LevelActionExecMode.tsv against the 4.8 client.

For every XLevelAction* class of the 4.8 il2cpp dump it disassembles that class's own get_ActionType / get_ExecMode
(exact addresses from script.json, no "+0x50" heuristic) in the installed GameAssembly.dll, prints the full
ELevelActionType -> ExecMode table and fails when a row of the TSV disagrees. Types the 4.8 client implements but no
quest/interaction config uses are listed as `unused`.
Usage: /tmp/re48venv/bin/python Scripts/verify_levelaction_execmode.py"""
import json, sys, re
from pathlib import Path
RES = Path('/Volumes/Lucia/PGR-native-research')
sys.argv = sys.argv[:1]
exec(open(RES / 'tools/re48/pe.py').read())
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM, X86_REG_EAX
md = Cs(CS_ARCH_X86, CS_MODE_64); md.detail = True
script = json.load(open(RES / 'il2cpp/dumps/il2cppdumper-4.8-runtime/script.json'))
methods = {x['Name']: x['Address'] for x in script['ScriptMethod']}

def constant(rva):
    o = rva2off(rva)
    last = None
    for ins in md.disasm(data[o:o + 0x80], base + rva):
        if ins.mnemonic == 'mov' and ins.op_str.startswith('eax, 0x') or ins.mnemonic == 'mov' and re.fullmatch(r'eax, \d+', ins.op_str):
            last = int(ins.op_str.split(', ')[1], 0)
        elif ins.mnemonic == 'xor' and ins.op_str == 'eax, eax':
            last = 0
        elif ins.mnemonic == 'ret':
            return last
    return None

table = {}
for name, rva in methods.items():
    m = re.fullmatch(r'StatusSyncFight\.(XLevelAction\w+)\$\$get_ActionType', name)
    if not m: continue
    cls = m.group(1)
    mode = methods.get(f'StatusSyncFight.{cls}$$get_ExecMode')
    table[constant(rva)] = (cls, constant(mode) if mode else None)
tsv = {int(a): int(b) for a, b, *_ in (l.split('\t') for l in (RES / 'AscNet/Resources/table/share/statussyncfight/quest/LevelActionExecMode.tsv').read_text().splitlines()[1:])}
bad = [(t, tsv[t], table.get(t)) for t in tsv if t not in table or table[t][1] != tsv[t]]
print(f'{len(table)} 4.8 action classes, {len(tsv)} TSV rows, {len(bad)} mismatching')
for t in sorted(table): print(t, table[t][0], 'ExecMode', table[t][1], '' if t in tsv else 'unused')
for b in bad: print('MISMATCH', b)
sys.exit(1 if bad else 0)
