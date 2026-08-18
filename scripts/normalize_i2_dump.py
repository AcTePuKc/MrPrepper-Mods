#!/usr/bin/env python3
"""Normalize the exported I2 English TSV without touching the source dump.

The raw dump contains logical records whose English field may span multiple
physical lines. The source column is required only while parsing the raw export;
it is intentionally discarded from QA output because it is repeated metadata.

Outputs:
- two-column deduplicated TSV: key + English
- chunks of the deduplicated TSV
- a report with counts and conflicting duplicate keys
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path

RAW_HEADER = "# source\tterm\tenglish"
OUTPUT_HEADER = "# key\tenglish"
ESCAPED_RAW_HEADER = r"# source\tterm\tenglish"


def parse_records(text: str) -> list[tuple[str, str, str]]:
    text = text.lstrip("\ufeff")
    lines = text.splitlines(keepends=True)

    records: list[tuple[str, str, str]] = []
    current: list[str] | None = None

    for physical in lines:
        line = physical.rstrip("\r\n")

        # Ignore both a normal TSV header and an accidentally escaped copy.
        if line == RAW_HEADER or line == ESCAPED_RAW_HEADER:
            continue

        # A logical record begins with source + key + English. Continuation
        # lines in exported I2 text do not have that prefix.
        parts = line.split("\t", 2)
        is_record_start = len(parts) == 3 and bool(parts[0]) and bool(parts[1])

        if is_record_start:
            if current is not None:
                records.append((current[0], current[1], current[2]))
            current = [parts[0], parts[1], parts[2]]
        elif current is not None:
            current[2] += "\n" + line
        elif line.strip():
            # Some historical dumps contain a literal escaped header followed
            # by an escaped newline before the first real record. It is noise.
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


def render(records: list[tuple[str, str]]) -> str:
    body = "\n".join(f"{key}\t{english}" for key, english in records)
    return OUTPUT_HEADER + "\n" + body + ("\n" if body else "")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument(
        "--chunk-size",
        type=int,
        default=1500,
        help="Logical records per chunk",
    )
    args = parser.parse_args()

    source_text = args.input.read_text(encoding="utf-8-sig")
    parsed = parse_records(source_text)

    # Source/provenance is deliberately discarded here. QA only needs the key
    # used by labels.txt and the authoritative English value.
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

    out = args.output_dir
    chunks = out / "chunks"
    chunks.mkdir(parents=True, exist_ok=True)

    dedup_path = out / "i2-terms-en-only.dedup.tsv"
    dedup_path.write_text(render(unique), encoding="utf-8")

    for old in chunks.glob("i2-terms-en-only.part-*.tsv"):
        old.unlink()

    chunk_count = 0
    for start in range(0, len(unique), args.chunk_size):
        chunk_count += 1
        part = unique[start:start + args.chunk_size]
        path = chunks / f"i2-terms-en-only.part-{chunk_count:03d}.tsv"
        path.write_text(render(part), encoding="utf-8")

    report = [
        "I2 English dump normalization report",
        "====================================",
        f"Input logical records: {len(parsed)}",
        f"Unique key+English records: {len(unique)}",
        f"Exact key+English duplicates removed: {exact_duplicates}",
        f"Keys with conflicting English values: {len(conflicts)}",
        f"Chunk size: {args.chunk_size}",
        f"Chunks written: {chunk_count}",
        "Output columns: key, English",
        "",
    ]

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

    print("\n".join(report[:9]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
