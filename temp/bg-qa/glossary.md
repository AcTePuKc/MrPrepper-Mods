# Mr. Prepper Bulgarian glossary

Temporary terminology guide for the QA pass. Expand only when a term has been checked against actual game context.

| English | Bulgarian | Notes |
|---|---|---|
| Agency | Агенцията | Proper organization name in dialogue and UI |
| Agent | агент | Capitalize only when the source/UI function requires it |
| bunker | бункер | |
| blueprint | чертеж | Plural: `чертежи` |
| gear | екипировка | Avoid `оборудване` unless context specifically means equipment rather than the Gear system |
| inventory | инвентар | |
| Preparedness | готовност | Core player attribute |
| Mr. Prepper | Mr. Prepper | Game title / brand. Never translate or transliterate. |
| Prepper | CONTEXT | Character/title/common noun usage is unresolved. Do not globally replace with `Препър`. Review per key. |
| Rejected Games | Rejected Games | Developer name. Never translate. |
| Steam | Steam | Keep untranslated |
| Discord | Discord | Keep untranslated |
| Jenny | Джени | Character name |
| Mary | Мери | Jenny's old friend; keep consistent in story dialogue |
| Lavinia | Лавиния | Huntress name |
| Xeno | Ксено | Character/dog name, pending final consistency scan |
| Chupacabra | Чупакабра | Joe's dog; proper name, not the creature term in these dialogue keys |
| Big Joe | Големия Джо | Joe's ironic nickname; he explicitly explains that he is not big |
| Sleepless | Безсънните | Proper name of Bob/Jenny's underground resistance group; plural people, not the abstract noun `безсъние` |
| Bob (mine) | Боб | Human character associated with the mine |
| Bob (Jenny's plant) | Боб | Jenny's named plant; do not confuse with human Bob. `prepBobGeneric*` / `herbBobGeneric*` establish the joke explicitly. |
| Murricaville | Мърикавил | Town name; use this transliteration consistently |
| Murricaville County | окръг Мърикавил | Avoid literal/garbled forms such as `Муритикайл Каунти` |
| herbalist | билкарка | Jenny is female; avoid masculine `билкар` for her dialogue |
| mine (noun) | мина | Use for the location unless a specific context clearly requires `шахта` |
| old mine | старата мина | Jenny quest/location wording |
| buttercup | слънчице | Jenny nickname; localize as a natural affectionate address rather than literal `лютиче` |
| daisy | цвете мое | Jenny nickname in `herbShrooms1`; chosen to keep the affectionate flower theme and make Prepper's objection sound natural |
| herbal tea | билков чай | Jenny dialogue terminology |
| guarana | гуарана | Plant name |
| guarana fruit(s) | плод(ове) от гуарана | Prefer natural Bulgarian word order |
| mixture | смес | Jenny's guarana/herbal preparation; keep consistent instead of alternating with `микстура` |
| equip | екипирай | UI action, not `облекло` / `облечи` |
| build | построй | Context action |
| power on | включи | Context action |
| power off | изключи | Context action |
| dry | изсуши | Context action |
| water (verb) | полей | Plant interaction, confirmed by French `ARROSER` |
| mine (verb) | добивай / копай | CONTEXT: choose after checking the exact interaction |
| harvest (verb) | обери / прибери реколтата | CONTEXT: prefer short UI form after in-game check |
| establish a food source | осигури източник на храна | Plan objective |

## Style rules

- Context-menu commands are verbs, not nouns.
- Preserve source capitalization only when it is meaningful in the game's UI. Do not randomly mix all-caps and title case.
- Prefer natural Bulgarian over English or Russian syntax.
- Do not translate product, platform, studio, publisher, or game names such as Mr. Prepper, Rejected Games, Steam and Discord.
- Preserve all TMP/I2 tags, placeholders, escaped newlines and formatting tokens exactly unless the source itself changes them.
- For gendered dialogue, identify the speaker from the key/prefix and adjacent dialogue before editing.
- Jenny deliberately uses changing affectionate plant/flower nicknames. Localize each one as a natural Bulgarian endearment that preserves the playful botanical character, rather than translating the plant name mechanically.
