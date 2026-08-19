# Mr. Prepper Bulgarian Translation

A community-made Bulgarian localization mod for **Mr. Prepper**.

The mod loads the finished Bulgarian translations through the game's
localization system and applies them automatically when the game starts.

## Installation

1. Install **BepInEx 5.x Mono** for the Windows version of Mr. Prepper.
2. Copy the contents of the release package into the game's directory.
3. Start the game once so BepInEx creates its configuration files.

The package should contain:

```text
BepInEx/plugins/AcTePuKc Mr Prepper Bulgarian Translation/
├── MrPrepperTranslationMod.dll
└── translations/
    ├── labels.txt
    └── changelog.txt
```

The game starts in English by default. The mod places the Bulgarian strings
into the English localization slot so they are available immediately at
startup, without requiring a language change in the settings.

## Configuration

The configuration file is created at:

```text
BepInEx/config/actepukc.mrprepper.uitranslationbulgarian.cfg
```

For normal play, keep translation enabled and diagnostics disabled. Diagnostic
options can be enabled when collecting new localization terms or checking a
different language:

```ini
[General]
EnableTranslation = true
EnableChangelogTranslations = true
EnableI2LocalizationInjection = true
I2LanguageName = English

[Diagnostics]
DumpVisibleText = false
DumpOnly = false
DumpI2Terms = false
DumpI2ReferenceLanguage = false
I2ReferenceLanguageName = Russian
```

With `DumpI2ReferenceLanguage = true`, the mod writes a key/value reference
file such as `dumps/i2-reference-russian.txt`. Change
`I2ReferenceLanguageName` to another language supported by the game, for
example `French`, then start the game once. The reference file is useful for
checking gender, context, and ambiguous or damaged English entries.

## Translation file format

The main file is `translations/labels.txt`. Each line uses the game's
localization key followed by `=` and the translated value:

```text
UI/Continue=Продължи
Items/TeslaCoilGun=Те́сла-пушка
```

Keyless Changelog and Patchnotes entries are kept separately in
`translations/changelog.txt`. Their left side is the exact original text,
including escaped line breaks, rich-text tags, and formatting markers. This
file can be disabled independently with `EnableChangelogTranslations = false`.

Keys must remain unchanged. Preserve placeholders, line breaks, color tags,
voice tags, alignment tags, and other formatting markers exactly. Do not add
speaker names or explanatory text to a translation.

See [`translations/README.md`](src/MrPrepperTranslationMod/translations/README.md)
for the Bulgarian terminology decisions, protected names, and localization
editing rules.

## Building a new language mod

This project can be used as a template for another language:

1. Copy the repository and rename the plugin, package folder, and BepInEx
   plugin identifier in `Plugin.cs`.
2. Replace `translations/labels.txt` with the target language translations.
3. Keep the original localization key on the left side of every `=` sign.
4. Set the language slot used by the target game in the generated config.
5. Set `GameDir` in a local `User.targets` file if the game is installed in a
   different location.
6. Build with:

```powershell
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-plugin.ps1
```

The build produces a package in `dist/` and stages it in the game's BepInEx
plugin directory.

## Collecting another language for reference

The mod can collect the game's own localization data without changing the
installed assets:

1. Set `DumpI2Terms = true` to collect term keys and English values.
2. Set `DumpI2ReferenceLanguage = true`.
3. Enter the desired language in `I2ReferenceLanguageName`.
4. Start the game and allow its localization data to load.
5. Read the generated `dumps/i2-reference-<language>.txt` file.

Use these files only as local working references. They are not part of the
release package.

## Project scope

The repository contains the plugin source, the final Bulgarian translation
file, and the build helper. Original game assets, game assemblies, generated
dumps, and local translation work files are intentionally excluded.

This is an unofficial fan translation. Mr. Prepper and its related assets
belong to their respective developers and publishers.
