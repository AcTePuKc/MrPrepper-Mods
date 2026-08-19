#!/usr/bin/env python3
"""Generate conservative Prepper-name consistency fixes from English source context.

Only proper-name uses are changed automatically. Generic lowercase prepper/preppers
and protected product branding are deliberately left for manual review.
"""

from __future__ import annotations

import re

from apply_bg_qa import LABELS, parse_labels
from generate_bg_consistency_fixes import normalize as normalize_locked
from validate_bg_qa import ROOT, load_source, parse_staged

OUT = ROOT / "temp" / "bg-qa" / "fixes-global-prepper-name.txt"

PROTECTED_KEYS = {
    "UI/demoEnd3",
    "UI/prologueEnd3",
    "PrologueExitShowcase8",
}

HONORIFIC_RE = re.compile(r"(?:господин|г-н|г-то|г-р)\s+Препър", re.IGNORECASE)
STANDALONE_RE = re.compile(r"\bПрепър(?:ът|а)?\b", re.IGNORECASE)


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
    for status, key, value, *_ in entries:
        if status in {"FIX", "OK"} and value is not None:
            accepted[key] = value
        elif status == "REMOVE":
            removes.add(key)
    merged = {k: v for k, v in current.items() if k not in removes}
    merged.update(accepted)
    return source, merged


def proper_name_source(english: str) -> bool:
    if "Mr. Prepper" in english or "Mr Prepper" in english:
        return True
    return bool(re.search(r"\bPrepper\b", english))


def normalize(value: str, english: str) -> str:
    # Compose with all already-locked terminology first so this pass never restores
    # an obsolete Murricaville / Awakening / Xeno / Minuteman / Fort / Brazen form.
    value = normalize_locked(value)

    if not proper_name_source(english):
        return value

    value = HONORIFIC_RE.sub("г-н Подготвения", value)
    value = STANDALONE_RE.sub("Подготвения", value)
    return value


def main() -> int:
    source, merged = simulated_mapping()
    fixes = []
    for key in sorted(merged):
        if key not in source or key in PROTECTED_KEYS:
            continue
        old = merged[key]
        # Run when either this is a Prepper-name candidate or locked normalization
        # still changes the same line, ensuring composed outputs agree across files.
        new = normalize(old, source[key])
        if new != old and (re.search(r"Препър", old, re.IGNORECASE) or proper_name_source(source[key])):
            fixes.append((key, new))

    lines = [
        "# Source-aware Prepper character-name cleanup\n",
        "# Generic lowercase prepper/preppers and protected product branding are excluded.\n",
        "# Locked terminology is composed first so overlapping fixes cannot regress.\n\n",
    ]
    lines.extend(f"{key}={value}\n" for key, value in fixes)
    OUT.write_text("".join(lines), encoding="utf-8")
    print(f"Generated {len(fixes)} Prepper-name fixes in {OUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
