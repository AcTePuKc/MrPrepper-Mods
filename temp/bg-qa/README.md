# Bulgarian translation QA

Temporary workspace for the full Bulgarian localization review.

This directory is intentionally kept out of the release translation. It can be deleted after the reviewed fixes are merged into `translations/labels.txt`.

## Source priority

1. English dump is authoritative for meaning.
2. Bulgarian is reviewed against English key by key.
3. French and Russian are context references only.
4. Russian must never be copied blindly into Bulgarian.

## Review order

- [ ] UI and system text - IN PROGRESS; base UI and late DLC/custom-mode UI have reviewed fix sets
- [ ] Items and item descriptions
- [ ] Objectives, quests, tips and letters
- [ ] Generic dialogue
- [ ] Jenny dialogue (`Dialogues/herb*`)
- [ ] Lavinia / Huntress dialogue after speaker prefix is identified
- [ ] Other character dialogue by speaker prefix
- [ ] DLC / fishing / animals
- [ ] Changelog
- [ ] Final English and Russian residue scan
- [ ] Placeholder, tag and escaped newline validation
- [ ] Final terminology consistency pass

## Working files

- `glossary.md` - verified terminology and protected names
- `fixes-ui.txt` - reviewed base UI replacements
- `fixes-ui-dlc.txt` - reviewed custom-mode / DLC / late UI replacements

## Status convention

- `FIX` - objectively wrong translation, safe to replace
- `REVIEW` - meaning is understood but Bulgarian wording is still editorial
- `CONTEXT` - needs in-game or adjacent-dialogue context before changing
- `OK` - explicitly reviewed and accepted

## Current findings

### Naming

- `Mr. Prepper` is protected as the game title / brand and stays unchanged.
- `Prepper` as a character/title/common noun remains `CONTEXT`; do not globally replace it with `Препър`.
- `Rejected Games` is a protected studio name and stays unchanged.

### UI first pass

Confirmed objective errors include wrong parts of speech in context actions, missing UI keys that would fall back to English, translated protected names, Russian contamination, and semantic hallucinations such as `Gear Slot 3 -> Работна маса 3` and `Best fishing rod -> Най-добра риболовна кърпа`.

The later UI/DLC block contains substantially more machine-translation damage than the opening UI block. Confirmed examples include `Бессмертен`, `ЗАБРАНИ` for `CLOSE`, `ПИЩАНЕ` for `FEED`, `Тягни` for `PULL`, broken fishing-rod terminology, and several malformed custom-mode descriptions.

### Known high-risk area

The fishing / animals DLC dialogue contains many grammatical and semantic hallucinations and needs a full English-to-Bulgarian pass rather than light proofreading.

### Speaker mapping

- `Dialogues/prep*` - Prepper
- `Dialogues/herb*` - Jenny
- other prefixes must be identified before gender-sensitive editing
