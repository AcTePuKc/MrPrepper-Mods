#!/usr/bin/env python3
"""Normalize the exported I2 English TSV without touching the source dump.

The raw dump contains logical records whose English field may span multiple
physical lines. The source column is required only while parsing the raw export;
it is intentionally discarded from QA output because it is repeated metadata.

Outputs:
- two-column deduplicated TSV: key + English
- English-only chunks for routine translation work
- optional aligned compare chunks: key + English + Bulgarian + French + Russian
- a report with counts and conflicting duplicate keys
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path

RAW_HEADER = "# source\tterm\tenglish"
EN_HEADER = "# key\tenglish"
COMPARE_HEADER = "# key\tenglish\tbulgarian\tfrench\trussian"
ESCAPED_RAW_HEADER = r"# source\tterm\tenglish"


def parse_records(text: str) -> list[tuple[str, str, str]]:
    text = text.lstrip("\ufeff")
    lines = text.splitlines(keepends=True)

    records: list[tuple[str, str, str]] = []
    current: list[str] | None = None

    for physical in lines:
        line = physical.rstrip("\r\n")

        if line == RAW_HEADER or line == ESCAPED_RAW_HEADER:
            continue

        parts = line.split("\t", 2)
        is_record_start = len(parts) == 3 and bool(parts[0]) and bool(parts[1])

        if is_record_start:
            if current is not None:
                records.append((current[0], current[1], current[2]))
            current = [parts[0], parts[1], parts[2]]
        elif current is not None:
            current[2] += "\n" + line
        elif line.strip():
            # Historical dump noise: literal escaped header followed by an
            # escaped newline before the first real record.
            if line.startswith(ESCAPED_RAW_HEADER + r"\n"):
                remainder = line[len(ESCAPED_RAW_HEADER + r"\n"):]
                parts = remainder.split("\t", 2)
                if len(parts) == 3 and parts[0] and parts[1]:
                    current = [parts[0], parts[1], parts[2]]
                    continue
            raise ValueError(f"Unexpected content before first TSV record: {line!r}")

    if current is not None:
        records.append((current[0], current[1], current[2]))

    return records


def load_key_values(path: Path | None) -> dict[str, str]:
    """Load key=value localization files, preserving everything after first '='."""
    if path is None:
        return {}

    result: dict[str, str] = {}
    text = path.read_text(encoding="utf-8-sig")
    for raw in text.splitlines():
        line = raw.rstrip("\r\n")
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key and key not in result:
            result[key] = value
    return result


def tsv_cell(value: str) -> str:
    """Keep each logical record on one physical line for connector-friendly QA."""
    return value.replace("\r", r"\r").replace("\n", r"\n").replace("\t", r"\t")


def render_en(records: list[tuple[str, str]]) -> str:
    body = "\n".join(f"{key}\t{tsv_cell(english)}" for key, english in records)
    return EN_HEADER + "\n" + body + ("\n" if body else "")


def render_compare(
    records: list[tuple[str, str]],
    bg: dict[str, str],
    fr: dict[str, str],
    ru: dict[str, str],
) -> str:
    rows = []
    for key, english in records:
        rows.append(
            "\t".join(
                (
                    key,
                    tsv_cell(english),
                    tsv_cell(bg.get(key, "")),
                    tsv_cell(fr.get(key, "")),
                    tsv_cell(ru.get(key, "")),
                )
            )
        )
    body = "\n".join(rows)
    return COMPARE_HEADER + "\n" + body + ("\n" if body else "")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--bg", type=Path)
    parser.add_argument("--fr", type=Path)
    parser.add_argument("--ru", type=Path)
    parser.add_argument(
        "--chunk-size",
        type=int,
        default=1500,
        help="Logical records per chunk",
    )
    args = parser.parse_args()

    source_text = args.input.read_text(encoding="utf-8-sig")
    parsed = parse_records(source_text)

    unique: list[tuple[str, str]] = []
    seen_exact: set[tuple[str, str]] = set()
    exact_duplicates = 0
    by_key: dict[str, list[str]] = defaultdict(list)

    for _source, key, english in parsed:
        record = (key, english)
        if record in seen_exact:
            exact_duplicates += 1
            continue
        seen_exact.add(record)
        unique.append(record)
        if english not in by_key[key]:
            by_key[key].append(english)

    conflicts = {
        key: values for key, values in by_key.items()
        if len(values) > 1
    }

    bg = load_key_values(args.bg)
    fr = load_key_values(args.fr)
    ru = load_key_values(args.ru)
    make_compare = any((args.bg, args.fr, args.ru))

    out = args.output_dir
    en_chunks = out / "en"
    compare_chunks = out / "compare"
    en_chunks.mkdir(parents=True, exist_ok=True)
    if make_compare:
        compare_chunks.mkdir(parents=True, exist_ok=True)

    # Remove legacy chunk layout and stale chunks from previous runs.
    legacy_chunks = out / "chunks"
    if legacy_chunks.exists():
        for old in legacy_chunks.glob("i2-terms-en-only.part-*.tsv"):
            old.unlink()
        try:
            legacy_chunks.rmdir()
        except OSError:
            pass

    for old in en_chunks.glob("part-*.tsv"):
        old.unlink()
    if compare_chunks.exists():
        for old in compare_chunks.glob("part-*.tsv"):
            old.unlink()

    dedup_path = out / "i2-terms-en-only.dedup.tsv"
    dedup_path.write_text(render_en(unique), encoding="utf-8")

    chunk_count = 0
    for start in range(0, len(unique), args.chunk_size):
        chunk_count += 1
        part = unique[start:start + args.chunk_size]
        name = f"part-{chunk_count:03d}.tsv"
        (en_chunks / name).write_text(render_en(part), encoding="utf-8")
        if make_compare:
            (compare_chunks / name).write_text(
                render_compare(part, bg, fr, ru), encoding="utf-8"
            )

    def coverage(values: dict[str, str]) -> int:
        return sum(1 for key, _english in unique if key in values)

    report = [
        "I2 English dump normalization report",
        "====================================",
        f"Input logical records: {len(parsed)}",
        f"Unique key+English records: {len(unique)}",
        f"Exact key+English duplicates removed: {exact_duplicates}",
        f"Keys with conflicting English values: {len(conflicts)}",
        f"Chunk size: {args.chunk_size}",
        f"Chunks written: {chunk_count}",
        "Primary output columns: key, English",
    ]

    if make_compare:
        report.extend(
            [
                "Compare output columns: key, English, Bulgarian, French, Russian",
                f"Bulgarian coverage: {coverage(bg)}/{len(unique)}",
                f"French coverage: {coverage(fr)}/{len(unique)}",
                f"Russian coverage: {coverage(ru)}/{len(unique)}",
            ]
        )

    report.append("")

    if conflicts:
        report.append("Conflicting keys (all values preserved in output):")
        report.append("")
        for key, values in sorted(conflicts.items()):
            report.append(key)
            for i, value in enumerate(values, 1):
                preview = value.replace("\n", "\\n")
                report.append(f"  {i}. {preview}")
            report.append("")
    else:
        report.append("No conflicting duplicate keys found.")

    (out / "report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")

    print("\n".join(report[:13]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
