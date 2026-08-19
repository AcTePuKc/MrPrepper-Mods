# Bulgarian localization files

This directory contains the player-facing Bulgarian localization used by the mod.

## Files

- `labels.txt` - keyed I2 Localization strings used by the game.
- `changelog.txt` - optional translations for keyless Changelog / Patchnotes text.

## Editing rules

`labels.txt` uses the format:

```text
Localization/Key=Bulgarian translation
```

The key on the left side of `=` must never be translated or renamed.

When editing values, preserve the structure of the English source exactly where applicable:

- escaped line breaks such as `\n` and `\r`;
- rich-text and TMP tags such as `<color>`, `<i>`, `<b>` and `<sprite ...>`;
- dialogue tags such as `<voice ...>` and `<random>...</random>`;
- placeholders such as `{0}`, `$item$`, `$x$` and similar runtime tokens.

A wording improvement is not worth introducing a broken tag or placeholder. Structural integrity takes priority.

## Translation source priority

1. The English localization is authoritative for meaning.
2. Bulgarian is translated and reviewed against the English key.
3. French and Russian may be used only as context references when the English wording is ambiguous.
4. Other localizations must not be copied blindly.

## Locked names and terminology

The following forms are intentional and should remain consistent:

- `Prepper` - **Подготвения** when it is the protagonist's in-world nickname.
- `Mr. Prepper` - **г-н Подготвения** when characters address the protagonist; the actual game/product title **Mr. Prepper** remains unchanged.
- `Minuteman` - **Опълченеца**.
- `Brazen Serpent` - **Медният змей**; use **Медния змей** where Bulgarian grammar requires the short definite form.
- `Fort Observer` - **Форт „Наблюдател“**.
- `Murricaville` - **Мърикавил**.
- `Murricaville County` - **окръг Мърикавил**.
- `Xeno` - **Ксено**.
- `Operation Awakening` - **Операция Пробуждане**.
- `Sleepless` - **Безсънните**.
- `the Hum` - **Бученето**.
- `Geiger counter` - **Гайгеров брояч**.

Real product and studio names such as `Steam`, `Discord`, `Rejected Games` and the game title `Mr. Prepper` remain protected names.

## Why several names are localized by meaning

### Prepper - Подготвения

`Prepper` functions as the protagonist's descriptive nickname rather than a conventional personal name. **Подготвения** preserves the joke and the central idea of a character who prepares for disasters and survival instead of using the opaque transliteration `Препър`.

### Minuteman - Опълченеца

The Agent's secret revolutionary identity refers to the historical American Minutemen - citizen militia members expected to be ready at very short notice. **Опълченеца** preserves the militia and revolutionary meaning better than a mechanical transliteration.

### Brazen Serpent - Медният змей

The satellite/project name contains the biblical reference to the brazen serpent. **Медният змей** keeps that cultural association instead of reducing the name to a generic snake.

### Fort Observer - Форт „Наблюдател“

`Observer` is the name of the military installation. **Форт „Наблюдател“** also preserves the wordplay in the line about Fort Observer being left unobserved: **„Форт „Наблюдател“ ще остане без наблюдение.“**

## QA history

The full review workspace, source comparisons, staged fixes, validation reports and terminology investigation are intentionally kept on the `translation/bg-qa-fixes` branch rather than in the release branch. They can be used later if the localization needs another full audit.
