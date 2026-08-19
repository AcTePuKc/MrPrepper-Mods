# Bulgarian translation QA

Temporary workspace for the full Bulgarian localization review.

This directory is intentionally kept out of the release translation. It can be deleted after the reviewed fixes are merged into `translations/labels.txt`.

## Source priority

1. English dump is authoritative for meaning.
2. Bulgarian is reviewed against English key by key.
3. French and Russian are context references only.
4. Russian must never be copied blindly into Bulgarian.

## Review order

- [ ] UI and system text - IN PROGRESS; split into base, DLC/custom-mode, combat/minigame, and fishing/animals review sets
- [ ] Items and item descriptions
- [ ] Objectives, quests, tips and letters
- [ ] Generic dialogue
- [ ] Jenny dialogue (`Dialogues/herb*`)
- [ ] Lavinia / Huntress dialogue after speaker prefix is identified
- [ ] Other character dialogue by speaker prefix
- [ ] DLC / fishing / animals dialogue
- [ ] Changelog
- [ ] Final English and Russian residue scan
- [ ] Placeholder, tag and escaped newline validation
- [ ] Final terminology consistency pass

## Working files

- `glossary.md` - verified terminology and protected names
- `fixes-ui.txt` - reviewed base UI replacements
- `fixes-ui-dlc.txt` - reviewed custom-mode / DLC / late UI replacements
- `fixes-ui-combat.txt` - combat controls, tutorials, and minigame UI
- `fixes-ui-fishing-animals.txt` - fishing equipment, animal interactions, and Xeno UI

## Status convention

- `FIX` - objectively wrong translation, safe to replace
- `REVIEW` - meaning is understood but Bulgarian wording is still editorial
- `CONTEXT` - needs in-game or adjacent-dialogue context before changing
- `OK` - explicitly reviewed and accepted

## Current findings

### Naming

- The actual game title / product name `Mr. Prepper` stays unchanged.
- In-world character nickname `Prepper` -> `Подготвения`.
- In-world formal address `Mr. Prepper` -> `г-н Подготвения` when the English source explicitly includes `Mr.`.
- Do not use the transliteration `Препър` in reviewed dialogue.
- `Rejected Games` is a protected studio name and stays unchanged.

### Защо някои имена са преведени така

Някои ключови имена в играта са локализирани по смисъл, а не само чрез транслитерация. Целта е да се запази идеята, шегата или културната препратка, която английското име носи в контекста на историята.

1. `Prepper` -> **Подготвения**
   - `Prepper` не е собственото име на героя, а прякор, с който хората го наричат.
   - Думата описва човек, който винаги е подготвен за бедствия, кризи и оцеляване.
   - **Подготвения** запазва смисъла на прякора и връзката с основната идея на героя, вместо да използва безсмислена за българския читател транслитерация като `Препър`.
   - Когато английският текст използва `Mr. Prepper`, обръщението е **г-н Подготвения**.

2. `Minuteman` -> **Опълченеца**
   - Името е революционният псевдоним на Агента.
   - Историческите американски `Minutemen` са граждани-ополченци, готови да бъдат мобилизирани с много кратко предизвестие.
   - **Опълченеца** предава ролята и революционния оттенък на името по-добре от механична транслитерация като `Минитмен`.

3. `Brazen Serpent` -> **Медният змей**
   - Името на сателита/проекта съдържа библейска препратка към медния змей.
   - Затова преводът не използва общото `змия`, а **Медният змей**, което запазва културната и религиозната асоциация.
   - Членуването се променя според изречението: **Медният змей** / **Медния змей**.

4. `Fort Observer` -> **Форт „Наблюдател“**
   - `Observer` е собственото име на военната база, а не прилагателно със значение „наблюдателен“.
   - **Форт „Наблюдател“** запазва името като название на обекта и същевременно съхранява играта на думи в репликата `Fort Observer will be left unobserved`.
   - В българския вариант това позволява шегата да остане: **„Форт „Наблюдател“ ще остане без наблюдение.“**

### UI first pass

Confirmed objective errors include wrong parts of speech in context actions, missing UI keys that would fall back to English, translated protected names, Russian contamination, and semantic hallucinations such as `Gear Slot 3 -> Работна маса 3` and `Best fishing rod -> Най-добра риболовна кърпа`.

The later UI/DLC block contains substantially more machine-translation damage than the opening UI block. Confirmed examples include `Бессмертен`, `ЗАБРАНИ` for `CLOSE`, `ПИЩАНЕ` for `FEED`, `Тягни` for `PULL`, broken fishing-rod terminology, malformed combat instructions, and several malformed custom-mode descriptions.

### Known high-risk area

The fishing / animals DLC dialogue contains many grammatical and semantic hallucinations and needs a full English-to-Bulgarian pass rather than light proofreading.

### Speaker mapping

- `Dialogues/prep*` - Prepper / Подготвения
- `Dialogues/herb*` - Jenny
- other prefixes must be identified before gender-sensitive editing
