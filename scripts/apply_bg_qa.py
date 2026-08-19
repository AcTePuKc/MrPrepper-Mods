#!/usr/bin/env python3
"""Simulate or apply staged Bulgarian QA fixes to labels.txt safely.

Default mode is dry-run. Nothing is written unless --write is passed explicitly.
The script preserves existing line order/comments, replaces accepted FIX/OK values,
removes explicit REMOVE keys, appends missing accepted keys, and checks that the
merge introduces no new structural token/tag/newline mismatches.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from validate_bg_qa import ROOT, load_source, parse_staged, structural_signature

LABELS = ROOT / "src" / "MrPrepperTranslationMod" / "translations" / "labels.txt"


def parse_labels(text: str):
    lines = text.splitlines(keepends=True)
    mapping: dict[str, str] = {}
    positions: dict[str, int] = {}
    duplicates: list[str] = []

    for idx, raw in enumerate(lines):
        line = raw.rstrip("\r\n")
        if not line or line.lstrip().startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if not key:
            continue
        if key in mapping:
            duplicates.append(key)
        mapping[key] = value
        positions[key] = idx

    return lines, mapping, positions, duplicates


def structural_mismatches(mapping: dict[str, str], source: dict[str, str]):
    result: dict[str, tuple[dict[str, object], dict[str, object]]] = {}
    for key, value in mapping.items():
        if key not in source:
            continue
        src = structural_signature(source[key])
        dst = structural_signature(value)
        if src != dst:
            result[key] = (src, dst)
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true", help="write the merged result to labels.txt")
    parser.add_argument("--output", type=Path, help="write merged preview to another path")
    args = parser.parse_args()

    source = load_source()
    entries, malformed, _review_markers = parse_staged()
    original_text = LABELS.read_text(encoding="utf-8")
    lines, before, _positions, duplicates = parse_labels(original_text)

    errors: list[str] = []
    if duplicates:
        errors.append("Duplicate keys already present in labels.txt: " + ", ".join(sorted(set(duplicates))))
    if malformed:
        errors.append(f"Malformed staged QA entries: {len(malformed)}")

    accepted: dict[str, str] = {}
    removes: set[str] = set()
    for status, key, value, path, lineno in entries:
        if status in {"FIX", "OK"} and value is not None:
            if key in accepted and accepted[key] != value:
                errors.append(
                    f"Conflicting staged values for {key}: {accepted[key]!r} vs {value!r} "
                    f"({path.relative_to(ROOT)}:{lineno})"
                )
            accepted[key] = value
        elif status == "REMOVE":
            removes.add(key)

    overlap = sorted(set(accepted) & removes)
    if overlap:
        errors.append("Keys staged for both accepted translation and removal: " + ", ".join(overlap))

    before_mismatch = structural_mismatches(before, source)

    out_lines: list[str] = []
    seen: set[str] = set()
    changed = 0
    removed = 0

    for raw in lines:
        stripped = raw.rstrip("\r\n")
        newline = raw[len(stripped):]
        if not stripped or stripped.lstrip().startswith("#") or "=" not in stripped:
            out_lines.append(raw)
            continue

        key, old_value = stripped.split("=", 1)
        key = key.strip()
        if not key:
            out_lines.append(raw)
            continue

        seen.add(key)
        if key in removes:
            removed += 1
            continue
        if key in accepted:
            new_value = accepted[key]
            if old_value != new_value:
                changed += 1
            out_lines.append(f"{key}={new_value}{newline}")
        else:
            out_lines.append(raw)

    missing = sorted(set(accepted) - seen)
    if missing:
        if out_lines and not out_lines[-1].endswith(("\n", "\r")):
            out_lines[-1] += "\n"
        if out_lines and out_lines[-1].strip():
            out_lines.append("\n")
        out_lines.append("# Added by Bulgarian QA merge\n")
        for key in missing:
            out_lines.append(f"{key}={accepted[key]}\n")

    merged_text = "".join(out_lines)
    _, after, _, after_duplicates = parse_labels(merged_text)
    if after_duplicates:
        errors.append("Merge produced duplicate keys: " + ", ".join(sorted(set(after_duplicates))))

    # Every accepted value must survive exactly; every explicit removal must be absent.
    for key, expected in accepted.items():
        if after.get(key) != expected:
            errors.append(f"Accepted staged value not reproduced exactly after merge: {key}")
    for key in removes:
        if key in after:
            errors.append(f"REMOVE key still present after merge simulation: {key}")

    after_mismatch = structural_mismatches(after, source)
    new_mismatch_keys = sorted(set(after_mismatch) - set(before_mismatch))
    resolved_mismatch_keys = sorted(set(before_mismatch) - set(after_mismatch))

    # A touched key must never be structurally broken after merge, even if it was already broken before.
    touched_structural_errors = sorted((set(accepted) & set(after_mismatch)))
    if new_mismatch_keys:
        errors.append("Merge introduces new structural mismatches: " + ", ".join(new_mismatch_keys))
    if touched_structural_errors:
        errors.append("Accepted QA keys remain structurally mismatched after merge: " + ", ".join(touched_structural_errors))

    expected_count = len(before) - sum(1 for key in removes if key in before) + len(missing)
    if len(after) != expected_count:
        errors.append(f"Key count invariant failed: expected {expected_count}, got {len(after)}")

    print("BG QA MERGE DRY RUN" if not args.write else "BG QA MERGE WRITE")
    print(f"labels.txt keys before: {len(before)}")
    print(f"accepted staged keys: {len(accepted)}")
    print(f"explicit REMOVE keys: {len(removes)}")
    print(f"changed existing keys: {changed}")
    print(f"added missing keys: {len(missing)}")
    print(f"removed existing keys: {removed}")
    print(f"labels.txt keys after simulation: {len(after)}")
    print(f"pre-existing structural mismatches: {len(before_mismatch)}")
    print(f"structural mismatches after simulation: {len(after_mismatch)}")
    print(f"resolved structural mismatches: {len(resolved_mismatch_keys)}")
    print(f"new structural mismatches: {len(new_mismatch_keys)}")
    print(f"errors: {len(errors)}")

    if missing:
        print("\nMISSING KEYS TO APPEND")
        for key in missing:
            print(f"- {key}")

    if resolved_mismatch_keys:
        print("\nSTRUCTURAL MISMATCHES RESOLVED BY QA")
        for key in resolved_mismatch_keys:
            print(f"- {key}")

    if errors:
        print("\nERRORS")
        for error in errors:
            print(f"- {error}")
        return 1

    if args.output:
        output = args.output
        if not output.is_absolute():
            output = ROOT / output
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(merged_text, encoding="utf-8")
        print(f"\nPreview written to: {output.relative_to(ROOT)}")

    if args.write:
        LABELS.write_text(merged_text, encoding="utf-8")
        print(f"\nWrote: {LABELS.relative_to(ROOT)}")
    else:
        print("\nDry-run passed. labels.txt was not modified.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
