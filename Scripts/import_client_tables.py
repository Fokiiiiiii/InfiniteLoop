"""Project decoded retail rows onto an existing server TSV schema.

Caller owns source selection, filtering, removals, and any domain-specific transforms.
"""
from __future__ import annotations

import re

_INDEXED = re.compile(r"^(.*?)\[(\d+)\]$")


def _cell(value):
    if value is None:
        return ""
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, (dict, list)):
        raise ValueError(f"cannot encode nested TSV value: {value!r}")
    text = str(value)
    if any(char in text for char in "\t\r\n"):
        raise ValueError(f"TSV cell contains tab or newline: {text!r}")
    return text

def parse(content):
    """Read a TSV literally; csv.DictReader would strip authored quotes."""
    lines = content.splitlines()
    header = lines[0].split("\t")
    rows = []
    for line in lines[1:]:
        values = line.split("\t")
        if len(values) != len(header):
            raise ValueError(f"expected {len(header)} TSV cells, found {len(values)}")
        rows.append(dict(zip(header, values)))
    return header, rows


def project(rows, header, current_rows, *, baseline_rows=None, key="Id", aliases=None):
    """Return TSV row dicts, retaining unchanged and local-only cells by key.

    ``key`` may be a column name or a tuple of column names. Indexed columns
    map source lists from their first header index (0 or 1). With baseline_rows,
    only retail fields changed since that baseline overwrite existing cells;
    otherwise all source fields are authoritative. The caller decides whether
    to retain removed retail rows or override domain-specific projections.
    """
    header = list(header)
    aliases = aliases or {}
    columns = {}
    starts = {}
    for column in header:
        match = _INDEXED.fullmatch(column)
        source = aliases.get(column, match.group(1) if match else column)
        columns[column] = (source, int(match.group(2)) if match else None)
        if match:
            starts[source] = min(starts.get(source, int(match.group(2))), int(match.group(2)))
    widths = {field: max(index - starts[field] + 1 for source, index in columns.values()
                         if source == field and index is not None) for field in starts}
    keys = (key,) if isinstance(key, str) else tuple(key)

    def identity(row):
        return tuple(str(row[field]) for field in keys)

    current = {identity(row): row for row in current_rows}
    baseline = None if baseline_rows is None else {identity(row): row for row in baseline_rows}
    result = []
    seen = set()
    for source_row in rows:
        ident = identity(source_row)
        if ident in seen:
            raise ValueError(f"duplicate source {keys} {ident}")
        seen.add(ident)
        old = current.get(ident, {})
        former = baseline.get(ident, {}) if baseline is not None else {}
        row = {column: old.get(column, "") for column in header}
        for field, width in widths.items():
            values = source_row.get(field)
            if isinstance(values, list) and any(_cell(v) != "" for v in values[width:]):
                raise ValueError(f"{ident}.{field} exceeds {width} indexed TSV columns")
        for field in keys:
            row[field] = _cell(source_row[field])
        for column, (field, index) in columns.items():
            if column in keys or (field not in source_row and (baseline is None or field not in former)):
                continue
            if baseline is not None and old and field in source_row and field in former and source_row[field] == former[field]:
                continue
            if baseline is not None and old and field not in source_row and field not in former:
                continue
            value = source_row.get(field)
            if index is not None:
                if value is None:
                    value = []
                if not isinstance(value, list):
                    raise ValueError(f"{ident}.{field} must be a list")
                offset = index - starts[field]
                value = value[offset] if offset < len(value) else None
            encoded = _cell(value)
            # Preserve existing nullable/sparse cells when the decoder exposes
            # an omitted source default as zero; blank and zero are not
            # interchangeable in the generated model.
            if old and row[column] == "" and (
                isinstance(value, (int, float, bool)) and value == 0
                or value == "0x0000000000000000"
            ):
                continue
            row[column] = encoded
        result.append(row)
    return result


def render(header, rows):
    header = list(header)
    return "\t".join(header) + "\n" + "".join(
        "\t".join(_cell(row.get(column, "")) for column in header) + "\n" for row in rows
    )


if __name__ == "__main__":
    columns = ["Id", "Local", "Cost[1]", "Cost[2]", "Raw"]
    original = [{"Id": "7", "Local": "policy", "Cost[1]": "4", "Cost[2]": "", "Raw": "old"}]
    before = [{"Id": 7, "Cost": [4, None], "Raw": "old"}]
    after = [{"Id": 7, "Cost": [4, None], "Raw": "0xFFFFFFFE00000000"}]
    actual = project(after, columns, original, baseline_rows=before)
    assert render(columns, actual) == "Id\tLocal\tCost[1]\tCost[2]\tRaw\n7\tpolicy\t4\t\t0xFFFFFFFE00000000\n"
    assert parse('Id\tName\n1\t\"Quoted Name\"\n')[1][0]["Name"] == '"Quoted Name"'
    try:
        project([{"Id": 7, "Cost": [4, None, 9]}], columns, original)
    except ValueError:
        pass
    else:
        raise AssertionError("nonempty indexed overflow must fail")
