# Mr. Prepper Bulgarian Translation

Community-made Bulgarian localization for **Mr. Prepper**.

The mod loads the Bulgarian translations through the game's localization system and applies them automatically when the game starts.

## Status

The Bulgarian localization has completed its full source-order QA pass and structural validation. The translation data is kept in `translations/labels.txt`.

## Installation

1. Install **BepInEx 5.x Mono** for the Windows version of Mr. Prepper.
2. Copy the contents of the release package into the game's directory.
3. Start the game once so BepInEx creates its configuration files.

The package should contain:

```text
BepInEx/plugins/AcTePuKc Mr Prepper Bulgarian Translation/
├── MrPrepperTranslationMod.dll
├── translations/
    ├── labels.txt
    └── changelog.txt
└── images/
    ├── prepStart_bg.png
    ├── ready_bg.png
    ├── escape_bg.png
    └── bulgaria.png
```

The optional `Cyrillic Font Fix` is distributed as a separate mod. It is recommended for users who want the game's thicker Cyrillic fallback font, but it is not required for the translation.

The game starts in English by default. The mod places the Bulgarian strings into the English localization slot so they are available immediately at startup, without requiring a language change in the settings.

## Configuration

The configuration file is created at:

```text
BepInEx/config/actepukc.mrprepper.uitranslationbulgarian.cfg
```

For normal play, keep translation enabled and diagnostics disabled:

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
I2ReferenceLanguageNames = Russian,French
```

Diagnostic options are disabled by default and are intended for local development and translation research. Users who need them can enable them manually in the generated configuration. `DumpI2Terms` writes the English catalog to `dumps/i2-terms.tsv`; `DumpI2ReferenceLanguage` writes one `key=value` file per language listed in `I2ReferenceLanguageNames`. Generated dumps are working files and are not part of the repository release content.

## Translation files

The main file is `translations/labels.txt`. Each line uses the game's localization key followed by `=` and the translated value:

```text
UI/Continue=Продължи
Items/TeslaCoilGun=Те́сла-пушка
```

Keyless Changelog and Patchnotes entries are kept separately in `translations/changelog.txt`.

Keys must remain unchanged. Preserve placeholders, escaped line breaks, color tags, voice tags, alignment tags, sprite tags, and other formatting markers exactly.

See [`translations/README.md`](translations/README.md) for Bulgarian terminology decisions, protected names, and localization editing rules.

## Building

Set `GameDir` in a local `User.targets` file if the game is installed in a different location, then build with:

```powershell
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-plugin.ps1
```

The build produces a package in `dist/` and stages it in the game's BepInEx plugin directory.

## Notes

This is an unofficial fan translation. **Mr. Prepper** and its related assets belong to their respective developers and publishers.
