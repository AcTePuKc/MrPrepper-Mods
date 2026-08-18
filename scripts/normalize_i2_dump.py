#!/usr/bin/env python3
"""Normalize the exported I2 English TSV without touching the source dump.

The dump contains logical records whose English field may span multiple physical
lines. A new record is recognized by the source/term/english TSV prefix; lines
that do not match that shape are continuations of the previous English value.

Outputs:
- deduplicated TSV (exact duplicate logical records removed, first occurrence kept)
- chunks of the deduplicated TSV
- a report with counts and conflicting duplicate terms
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path

HEADER = "# source\tterm\tenglish"


def parse_records(text: str):
    text = text.lstrip("\ufeff")
    lines = text.splitlines(keepends=True)

    records: list[tuple[str, str, str]] = []
    current: list[str] | None = None

    for physical in lines:
        line = physical.rstrip("\r\n")

        if line == HEADER or line.startswith("# source\tterm\tenglish"):
            continue

        # A logical record begins with at least three TSV fields. Continuation
        # lines in exported I2 text do not have the source+term prefix.
        parts = line.split("\t", 2)
        is_record_start = len(parts) == 3 and bool(parts[0]) and bool(parts[1])

        if is_record_start:
            if current is not None:
                records.append((current[0], current[1], current[2]))
            current = [parts[0], parts[1], parts[2]]
        elif current is not None:
            current[2] += "\n" + line
        elif line.strip():
            raise ValueError(f"Unexpected content before first TSV record: {line!r}")

    if current is not None:
        records.append((current[0], current[1], current[2]))

    return records


def render(records: list[tuple[str, str, str]]) -> str:
    body = "\n".join(f"{source}\t{term}\t{english}" for source, term, english in records)
    return HEADER + "\n" + body + ("\n" if body else "")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output_dir", type=Path)
    parser.add_argument("--chunk-size", type=int, default=1500,
                        help="Logical records per chunk")
    args = parser.parse_args()

    source_text = args.input.read_text(encoding="utf-8-sig")
    records = parse_records(source_text)

    unique: list[tuple[str, str, str]] = []
    seen_exact: set[tuple[str, str, str]] = set()
    exact_duplicates = 0

    by_term: dict[tuple[str, str], list[str]] = defaultdict(list)

    for record in records:
        source, term, english = record
        if record in seen_exact:
            exact_duplicates += 1
            continue
        seen_exact.add(record)
        unique.append(record)
        if english not in by_term[(source, term)]:
            by_term[(source, term)].append(english)

    conflicts = {
        key: values for key, values in by_term.items()
        if len(values) > 1
    }

    out = args.output_dir
    chunks = out / "chunks"
    chunks.mkdir(parents=True, exist_ok=True)

    dedup_path = out / "i2-terms-en-only.dedup.tsv"
    dedup_path.write_text(render(unique), encoding="utf-8")

    # Clear stale chunks from previous runs.
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
        f"Input logical records: {len(records)}",
        f"Unique logical records: {len(unique)}",
        f"Exact duplicate records removed: {exact_duplicates}",
        f"Terms with conflicting English values: {len(conflicts)}",
        f"Chunk size: {args.chunk_size}",
        f"Chunks written: {chunk_count}",
        "",
    ]

    if conflicts:
        report.append("Conflicting terms (preserved in output):")
        report.append("")
        for (source, term), values in sorted(conflicts.items()):
            report.append(f"[{source}] {term}")
            for i, value in enumerate(values, 1):
                preview = value.replace("\n", "\\n")
                report.append(f"  {i}. {preview}")
            report.append("")
    else:
        report.append("No conflicting duplicate terms found.")

    (out / "report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")

    print("\n".join(report[:8]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
