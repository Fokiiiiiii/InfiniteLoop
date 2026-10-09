#!/usr/bin/env python3
"""Exports the XRpc surface of the 4.8 client (il2cpp dump.cs) used by the BigWorld coverage selector.

AscNet.Test/Fixtures/BigWorld/client_xrpcs.tsv: one row per protocol class (Rpc*/XRpc* : IProtocol)
  <envelope>\t<protocol>\t<handled|sent>
  handled = the client has an ARpc{Common,ActorAction,ComponentAction}Handler<protocol>: the server pushes it
  sent    = no client handler (class or Handle<Name> method): the client sends it (or it is server-only/engine-only)
envelope is the XRpc family of the handler (XRpcCommon, XRpcActorAction, XRpcComponentAction); '-' when unhandled.
Usage: python3 Scripts/export_bigworld_client_rpcs.py"""
import re
from pathlib import Path
DUMP = Path('/Volumes/Lucia/PGR-native-research/il2cpp/dumps/il2cppdumper-4.8-runtime/dump.cs')
OUT = Path(__file__).resolve().parent.parent / 'AscNet.Test/Fixtures/BigWorld/client_xrpcs.tsv'
FAMILY = {'ARpcCommonHandler': 'XRpcCommon', 'ARpcActorActionHandler': 'XRpcActorAction', 'ARpcComponentActionHandler': 'XRpcComponentAction'}
text = DUMP.read_text(encoding='utf8', errors='ignore')
# The handler class is Rpc<Name>Handler; its last generic argument is the protocol (RpcAddBuffHandler handles RepBuff), the RPC
# name on the wire is the protocol class name. Both names are exported as handled.
handled = {}
for m in re.finditer(r'^public (?:sealed )?class ((?:X)?Rpc\w+)Handler : (ARpc\w+Handler)<([\w, ]+)>', text, re.M):
    if m.group(2) in FAMILY:
        handled[m.group(1)] = handled[m.group(3).split(', ')[-1]] = FAMILY[m.group(2)]
# Quest/engine managers also register plain Handle<Name> methods (XQuestManager.HandleQuestDeliverItemsFailedNotify handles RpcQuestDeliverItemsFailedNotify).
methods = set(re.findall(r'\bHandle(\w+)\(', text))
protocols = sorted(set(re.findall(r'^public (?:sealed )?class ((?:X)?Rpc\w+|Rep\w+) : IProtocol', text, re.M)) | set(handled))
def is_handled(p):
    return p in handled or re.sub(r'^X?Rpc', '', p) in methods
OUT.write_text(''.join(f'{handled.get(p, "-")}\t{p}\t{"handled" if is_handled(p) else "sent"}\n' for p in protocols), encoding='utf-8')
print(f'{OUT}: {len(protocols)} protocols, {len(handled)} handled')
