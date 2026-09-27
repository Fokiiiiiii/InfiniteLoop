#!/usr/bin/env python3
"""Compare verified installed Lua against EN 4.7.13 without conflating old gaps."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NEW = ROOT / ".runtime/upgrade-4.8/lua"
OLD = ROOT.parent / "PGR_Data/en/lua"


def files(root: Path) -> dict[str, Path]:
    return {p.relative_to(root).as_posix().lower(): p for group in ("matrix", "launch", "dlcfight")
            for p in (root / group).rglob("*.lua")}


def requests(mod, path: Path) -> dict[str, dict[str, object]]:
    import re
    from collections import defaultdict
    text = path.read_text(encoding="utf-8", errors="replace")
    constants = mod.lua_constants(text)
    found = defaultdict(lambda: {"fields": set(), "responses": set(), "calls": []})
    for match in mod.CALL_RE.finditer(text):
        call = text[match.start():mod.closing_paren(text, match.end()-1)]
        name = mod.first_argument(call, constants)
        if name is None:
            continue
        record = found[name]
        record["fields"].update(mod.lua_request_fields(text, match.start(), call))
        for callback in re.finditer(r"function\s*\(\s*([A-Za-z_]\w*)", call):
            record["responses"].update(mod.consumed_fields(call[callback.end():], callback.group(1)))
        record["calls"].append(text.count("\n", 0, match.start()) + 1)
    return {name: {"fields": sorted(value["fields"]), "responses": sorted(value["responses"]),
                   "calls": value["calls"]} for name, value in found.items()}


def main() -> None:
    spec = importlib.util.spec_from_file_location("protocol_gap", ROOT / "Scripts/protocol_gap.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    new, old = files(NEW), files(OLD)
    added = sorted(new.keys()-old.keys())
    removed = sorted(old.keys()-new.keys())
    changed = sorted(k for k in new.keys() & old.keys()
                     if new[k].read_bytes().replace(b"\r\n", b"\n") != old[k].read_bytes().replace(b"\r\n", b"\n"))
    altered = added + changed
    before = {key: requests(mod, old[key]) for key in changed + removed}
    after = {key: requests(mod, new[key]) for key in altered}
    changes = []
    for key in sorted(set(before)|set(after)):
        prev, now = before.get(key, {}), after.get(key, {})
        for name in sorted(set(prev)|set(now)):
            if prev.get(name) != now.get(name):
                changes.append({"file": key, "name": name, "before": prev.get(name), "after": now.get(name)})
    report = {"baseline": "PGR_Data EN 4.7.13 a8420088f0d3a8b5d5959ad9ca2a2ff043d40f52",
              "installed_provenance": "lua/provenance.json", "counts": {"old": len(old), "new": len(new),
              "added": len(added), "removed": len(removed), "changed_lf_normalized": len(changed)},
              "added": added, "removed": removed, "changed": changed, "request_delta": changes}
    (NEW / "delta.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report["counts"]))
    print("request changes", len(changes))


if __name__ == "__main__":
    main()
