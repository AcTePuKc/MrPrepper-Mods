#!/usr/bin/env python3
"""Generate deterministic staged fixes for locked Bulgarian localization terms.

The output is a QA staging file, never labels.txt. Only unambiguous locked-term
normalizations are performed here. Prepper is handled separately because it can be
both a character name and a common noun/brand.
"""

from __future__ import annotations

import re

from apply_bg_qa import LABELS, parse_labels
from validate_bg_qa import ROOT, load_source, parse_staged

OUT = ROOT / "temp" / "bg-qa" / "fixes-global-locked-terminology.txt"


def simulated_mapping():
    source = load_source()
    entries, malformed, _ = parse_staged()
    if malformed:
        raise RuntimeError(f"Malformed staged entries: {len(malformed)}")
    _, current, _, duplicates = parse_labels(LABELS.read_text(encoding="utf-8"))
    if duplicates:
        raise RuntimeError("Duplicate keys in labels.txt")
    accepted = {}
    removes = set()
    for status, key, value, path, _lineno in entries:
        # Never consume our own previous generated output.
        if path.resolve() == OUT.resolve():
            continue
        if status in {"FIX", "OK"} and value is not None:
            accepted[key] = value
        elif status == "REMOVE":
            removes.add(key)
    merged = {k: v for k, v in current.items() if k not in removes}
    merged.update(accepted)
    return source, merged


def normalize(value: str) -> str:
    value = re.sub(r"Минуteman|Минитмен|\bMinuteman\b", "Опълченеца", value, flags=re.IGNORECASE)
    value = re.sub(r"Мюрикавил|Мъррикавил|Мърицивил|Мърицивал|Муритикавил", "Мърикавил", value, flags=re.IGNORECASE)
    value = re.sub(r"\bЗено\b|\bXeno\b", "Ксено", value, flags=re.IGNORECASE)
    value = re.sub(r"Форт Обзървър|Форт Наблюдател|\bFort Observer\b", "Форт „Наблюдател“", value, flags=re.IGNORECASE)
    value = re.sub(r"\bBrazen Serpent\b|Бразана\s+Змия|Змийска отрова", "Медният змей", value, flags=re.IGNORECASE)
    value = re.sub(r"Операция\s+[„\"]?Събуждане[“\"]?", "Операция Пробуждане", value, flags=re.IGNORECASE)
    return value


def prepper_name_candidate(english: str, bulgarian: str) -> bool:
    """Return True when the same line belongs to the character-name cleanup layer."""
    if not re.search(r"Препър", bulgarian, re.IGNORECASE):
        return False
    return bool(
        re.search(r"\bMr\.?\s+Prepper\b", english)
        or re.search(r"\bPrepper\b", english)
    )


def main() -> int:
    source, merged = simulated_mapping()
    fixes = []
    for key in sorted(merged):
        if key not in source:
            continue
        old = merged[key]

        # If this line also needs character-name normalization, let the Prepper
        # generator own the complete final value. It composes locked terms first.
        if prepper_name_candidate(source[key], old):
            continue

        new = normalize(old)
        if new != old:
            fixes.append((key, new))

    lines = [
        "# Global locked-terminology cleanup\n",
        "# Generated deterministically from the simulated post-QA localization.\n",
        "# Prepper character-name overlap is delegated to fixes-global-prepper-name.txt.\n\n",
    ]
    lines.extend(f"{key}={value}\n" for key, value in fixes)
    OUT.write_text("".join(lines), encoding="utf-8")
    print(f"Generated {len(fixes)} locked-term fixes in {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
