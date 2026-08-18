# Bulgarian translation QA

Temporary workspace for the full Bulgarian localization review.

This directory is intentionally kept out of the release translation. It can be deleted after the reviewed fixes are merged into `translations/labels.txt`.

## Source priority

1. English dump is authoritative for meaning.
2. Bulgarian is reviewed against English key by key.
3. French and Russian are context references only.
4. Russian must never be copied blindly into Bulgarian.

## Review order

- [ ] UI and system text
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

## Status convention

- `FIX` - objectively wrong translation, safe to replace
- `REVIEW` - meaning is understood but Bulgarian wording is still editorial
- `CONTEXT` - needs in-game or adjacent-dialogue context before changing
- `OK` - explicitly reviewed and accepted

## Current findings

### UI first pass

Confirmed objective errors include:

- `UI/contextSLEEP=Сън` for `SLEEP`
- `UI/contextCRAFT=ИЗРАБОТКА` for `CRAFT`
- `UI/contextTRADE=Търговия` for `TRADE`
- `UI/contextWATER=Вода` for action `WATER` / French `ARROSER`
- `UI/contextHARVEST=ЖЪНЕЖ` for `HARVEST`
- `UI/contextEQUIP=ОБЛЕКЛО` for `EQUIP`
- `UI/contextMINE=Рудник` for `MINE`
- `UI/contextBUILD=ИЗРАБОТИ` for `BUILD`
- `UI/contextPOWEROFF=ИЗКЛЪЧВАНЕ` for `POWER OFF`
- `UI/contextDRY=Сух` for `DRY`
- `UI/GearSlot3=Работна маса 3` for `Gear Slot 3`
- inconsistent `UI/planRocket1..4`
- `UI/planFood=Намери храна` loses the meaning of `Establish a food source`

### Known high-risk area

The fishing / animals DLC dialogue contains many grammatical and semantic hallucinations and needs a full English-to-Bulgarian pass rather than light proofreading.

### Speaker mapping

- `Dialogues/prep*` - Prepper
- `Dialogues/herb*` - Jenny
- other prefixes must be identified before gender-sensitive editing
