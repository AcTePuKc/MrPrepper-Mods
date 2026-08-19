# Mr. Prepper Bulgarian glossary

Temporary terminology guide for the QA pass. Expand only when a term has been checked against actual game context.

| English | Bulgarian | Notes |
|---|---|---|
| Agency | Агенцията | Proper organization name in dialogue and UI |
| Agent | агент | Capitalize only when the source/UI function requires it |
| Security & Wellbeing Act | Закон за сигурността и благосъстоянието | Official bureaucratic law name used by Agents during inspections |
| Inspiration & Wellbeing Department | REVIEW: Отдел за вдъхновение и благосъстояние | Fort Observer department that tests Brazen Serpent wave technology; keep under review until wording is finalized |
| Minuteman | REVIEW: Minuteman | Agent's secret alter ego/codename; keep source form for now, decide later whether Bulgarian transliteration is preferable |
| bunker | бункер | |
| blueprint | чертеж | Plural: `чертежи` |
| gear | екипировка | Avoid `оборудване` unless context specifically means equipment rather than the Gear system |
| inventory | инвентар | |
| Preparedness | готовност | Core player attribute |
| Mr. Prepper (game title / brand) | Mr. Prepper | Keep the official game title unchanged when it is actually the product/brand name |
| Mr. Prepper (character address) | г-н Подготвения | Use when the English source explicitly says `Mr. Prepper` as the character's nickname/title |
| Prepper (character address/name) | Подготвения | In-world nickname derived from being prepared; use when the source says standalone `Prepper` |
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
| Operation Awakening | Операция Пробуждане | Sleepless resistance operation timed to the rocket launch |
| Bob (mine) | Боб | Human character associated with the mine |
| Bob (Jenny's plant) | Боб | Jenny's named plant; do not confuse with human Bob. `prepBobGeneric*` / `herbBobGeneric*` establish the joke explicitly. |
| Murricaville | Мърикавил | Town name; use this transliteration consistently |
| Murricaville County | окръг Мърикавил | Avoid literal/garbled forms such as `Муритикайл Каунти` |
| Brazen Serpent | Медният змей | Satellite/project name; biblical allusion. Use consistently across broadcast, newspaper and story dialogue |
| Fort Observer | Форт „Наблюдател“ | Military base name; treat `Observer` as the proper name, not the adjective `наблюдателен`; preserves the Observer/unobserved wordplay |
| Ellipse | Елипс | Fort Observer security AI |
| the Hum | Бученето | Confirmed story term for the low-frequency phenomenon/conspiracy discussed by Joe |
| junkyard | автоморга | Joe's location; prefer this over generic `бунище`/`лагер` when the place itself is meant |
| Geiger counter | Гайгеров брояч | Standard Bulgarian technical term; use consistently |
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
- Keep actual product, platform, studio and publisher names untranslated, including the game title `Mr. Prepper`, Rejected Games, Steam and Discord. This does not apply to the in-world character nickname `Prepper` / `Mr. Prepper`, which is localized as `Подготвения` / `г-н Подготвения`.
- Preserve all TMP/I2 tags, placeholders, escaped newlines and formatting tokens exactly unless the source itself changes them.
- For gendered dialogue, identify the speaker from the key/prefix and adjacent dialogue before editing.
- Jenny deliberately uses changing affectionate plant/flower nicknames. Localize each one as a natural Bulgarian endearment that preserves the playful botanical character, rather than translating the plant name mechanically.
