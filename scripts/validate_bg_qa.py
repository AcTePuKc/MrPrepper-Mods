#!/usr/bin/env python3
"""Validate staged Bulgarian localization QA fixes against authoritative English.

This script is intentionally strict about structural tokens. It does not judge prose
quality; it protects placeholders, TMP/I2 tags, escaped newlines and staged QA
integrity before fixes are merged back into labels.txt.
"""

from __future__ import annotations

import csv
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QA_DIR = ROOT / "temp" / "bg-qa"
COMPARE_DIR = ROOT / "temp" / "translation-dumps" / "generated" / "compare"

STATUSES = {"FIX", "REVIEW", "CONTEXT", "OK", "REMOVE"}
TAG_RE = re.compile(r"<[^>]+>")
DOLLAR_RE = re.compile(r"\$[^$\r\n]+\$")
BRACE_RE = re.compile(r"\{[^{}\r\n]+\}")

# Locked/obsolete variants that should not survive in accepted FIX values.
FORBIDDEN_FIX_PATTERNS = {
    "Препър": re.compile(r"Препър", re.IGNORECASE),
    "Минитмен": re.compile(r"Минитмен", re.IGNORECASE),
    "mixed Minuteman": re.compile(r"Минуteman|Minuteman", re.IGNORECASE),
    "old Murricaville variant": re.compile(r"Мюрикавил|Мъррикавил", re.IGNORECASE),
    "Fort Observer untranslated/old": re.compile(r"Fort Observer|Форт Обзървър|Форт Наблюдател(?![“\"])", re.IGNORECASE),
    "Brazen Serpent untranslated/old": re.compile(r"Brazen Serpent|Бразана\s+Змия", re.IGNORECASE),
    "old Xeno variant": re.compile(r"\bЗено\b", re.IGNORECASE),
    "old Operation Awakening variant": re.compile(r"операция\s+[„\"]?Събуждане[“\"]?", re.IGNORECASE),
}


def load_source() -> dict[str, str]:
    source: dict[str, str] = {}
    for path in sorted(COMPARE_DIR.glob("part-*.tsv")):
        with path.open("r", encoding="utf-8", newline="") as fh:
            reader = csv.reader(fh, delimiter="\t")
            for row in reader:
                if not row or row[0].startswith("#"):
                    continue
                if len(row) < 2:
                    continue
                key, english = row[0], row[1]
                if key in source and source[key] != english:
                    raise RuntimeError(f"Conflicting English source for key {key!r}")
                source[key] = english
    return source


def parse_staged():
    entries = []
    malformed = []
    for path in sorted(QA_DIR.glob("*.txt")):
        with path.open("r", encoding="utf-8") as fh:
            for lineno, raw in enumerate(fh, 1):
                line = raw.rstrip("\n\r")
                if not line or line.lstrip().startswith("#"):
                    continue
                if "\t" not in line:
                    # Notes/rationale lines are allowed only when they do not look like statuses.
                    head = line.split(None, 1)[0] if line.split() else ""
                    if head in STATUSES:
                        malformed.append((path, lineno, line))
                    continue
                status, payload = line.split("\t", 1)
                status = status.strip()
                if status not in STATUSES:
                    continue
                if status == "REMOVE":
                    key = payload.strip()
                    value = None
                else:
                    if "=" not in payload:
                        # REVIEW/CONTEXT can be commentary-only; FIX/OK cannot.
                        if status in {"FIX", "OK"}:
                            malformed.append((path, lineno, line))
                        continue
                    key, value = payload.split("=", 1)
                    key = key.strip()
                entries.append((status, key, value, path, lineno))
    return entries, malformed


def structural_signature(text: str) -> dict[str, object]:
    tags = TAG_RE.findall(text)
    without_tags = TAG_RE.sub("", text)
    return {
        "tags": tags,
        "dollar": DOLLAR_RE.findall(text),
        "braces": BRACE_RE.findall(text),
        "escaped_n": text.count(r"\n"),
        "escaped_r": text.count(r"\r"),
        "hash_count": without_tags.count("#"),
        "random_open": text.count("<random>"),
        "random_close": text.count("</random>"),
    }


def main() -> int:
    source = load_source()
    entries, malformed = parse_staged()

    errors: list[str] = []
    warnings: list[str] = []

    for path, lineno, line in malformed:
        errors.append(f"{path.relative_to(ROOT)}:{lineno}: malformed staged QA line: {line}")

    by_key: dict[str, list[tuple[str, str | None, Path, int]]] = defaultdict(list)
    for status, key, value, path, lineno in entries:
        by_key[key].append((status, value, path, lineno))
        if key not in source:
            errors.append(f"{path.relative_to(ROOT)}:{lineno}: key not found in authoritative compare data: {key}")
            continue

        if status == "REMOVE":
            continue
        if value is None:
            continue

        # Structural comparison is strict for accepted fixes. REVIEW/CONTEXT are reported
        # only as warnings because they may intentionally remain unresolved.
        src_sig = structural_signature(source[key])
        dst_sig = structural_signature(value)
        for field in ("tags", "dollar", "braces", "escaped_n", "escaped_r", "hash_count", "random_open", "random_close"):
            if src_sig[field] != dst_sig[field]:
                msg = (
                    f"{path.relative_to(ROOT)}:{lineno}: {key}: structural mismatch in {field}: "
                    f"EN={src_sig[field]!r} BG={dst_sig[field]!r}"
                )
                (errors if status in {"FIX", "OK"} else warnings).append(msg)

        if status == "FIX":
            for label, pattern in FORBIDDEN_FIX_PATTERNS.items():
                if pattern.search(value):
                    errors.append(
                        f"{path.relative_to(ROOT)}:{lineno}: {key}: locked/obsolete form remains ({label}): {value}"
                    )

    # Conflicting accepted values and FIX/REMOVE overlap.
    for key, vals in sorted(by_key.items()):
        accepted = {(status, value) for status, value, _, _ in vals if status in {"FIX", "OK"}}
        accepted_values = {value for _, value in accepted}
        if len(accepted_values) > 1:
            locs = ", ".join(f"{p.relative_to(ROOT)}:{ln}" for _, _, p, ln in vals)
            errors.append(f"{key}: conflicting accepted translations across staged files ({locs})")
        has_remove = any(status == "REMOVE" for status, _, _, _ in vals)
        has_fix = any(status in {"FIX", "OK"} for status, _, _, _ in vals)
        if has_remove and has_fix:
            locs = ", ".join(f"{p.relative_to(ROOT)}:{ln}" for _, _, p, ln in vals)
            errors.append(f"{key}: staged both REMOVE and FIX/OK ({locs})")

    print(f"Authoritative keys: {len(source)}")
    print(f"Staged QA entries: {len(entries)}")
    print(f"Unique staged keys: {len(by_key)}")
    print(f"Warnings: {len(warnings)}")
    print(f"Errors: {len(errors)}")

    if warnings:
        print("\nWARNINGS")
        for msg in warnings:
            print(f"- {msg}")

    if errors:
        print("\nERRORS")
        for msg in errors:
            print(f"- {msg}")
        return 1

    print("\nBG QA structural validation passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
