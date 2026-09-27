"""Project exact installed 4.8 Draw/Partner tables onto existing server TSV schemas."""
from __future__ import annotations

import json
import re
from pathlib import Path

from import_client_tables import parse, project, render

ROOT = Path(__file__).resolve().parent.parent
STAGE = ROOT / ".runtime/upgrade-4.8/tables"
TABLE = ROOT / "Resources/table"
MANIFEST = json.loads((STAGE / "manifest.json").read_text())


def import_folder(folder: str, *, skip: set[str] = frozenset(), only: set[str] | None = None) -> None:
    for output in sorted((TABLE / folder).glob("*.tsv")):
        name = output.stem
        if name in skip or only is not None and name not in only:
            continue
        source = STAGE / folder / f"{name}.json"
        logical = f"{folder}/{name}.tab".lower()
        entry = MANIFEST.get(logical)
        if not source.exists() or not entry or "schema" not in entry or entry.get("decode_error"):
            raise ValueError(f"Missing exact decoded 4.8 source for {output}: {entry}")
        rows = json.loads(source.read_text())
        if not rows or not isinstance(rows, list):
            continue
        if name == "PartnerUiEffect":
            if any(len(row["EffectPath"]) > 1 for row in rows):
                raise ValueError("PartnerUiEffect scalar EffectPath cannot hold multiple authored effects")
            for row in rows:
                row["EffectPath"] = next(iter(row["EffectPath"]), "")
        header, current = parse(output.read_text())
        key = ("DrawId" if name == "DrawProbShow" else "PartnerId" if name == "PartnerSkill"
               else "ItemId" if name == "ItemCombine" else "Id")
        if key not in header:
            continue  # Domain-specific composite-key projections retain their existing schema.
        # Preserve indexed-list source positions; widen only if authored content actually needs them.
        for field in rows[0]:
            indexed = [(i, int(m.group(1))) for i, column in enumerate(header)
                       if (m := re.fullmatch(re.escape(field) + r"\[(\d+)\]", column))]
            if indexed:
                maximum = max(len(row.get(field) or []) for row in rows)
                last = max(indexed, key=lambda pair: pair[1])
                if maximum > last[1]:
                    header[last[0] + 1:last[0] + 1] = [f"{field}[{i}]" for i in range(last[1] + 1, maximum + 1)]
        projected = project(rows, header, current, key=key)
        content = render(header, projected)
        if content != output.read_text():
            output.write_text(content)
            print(f"{folder}/{name}: {len(current)} -> {len(projected)} rows")


if __name__ == "__main__":
    import_folder("client/draw", skip={"DrawPredict", "DrawCalibrationGuide", "DrawShow"})
    import_folder("share/partner")
    import_folder("share/archive", only={"PartnerSetting"})
    import_folder("share/draw", only={"DrawCombinations", "DrawCanLiverActivity", "DateALiveActivity"})
    import_folder("client/partner")
    import_folder("share/lotto")
    import_folder("share/item", only={"ItemCombine"})
    import_folder("share/item", only={"BuyAsset", "BuyAssetConfig"})
