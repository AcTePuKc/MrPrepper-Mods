# Mr. Prepper Mods

A collection of mods, fixes, localization work, and experiments for **Mr. Prepper**.

The repository is no longer limited to the Bulgarian translation. The game has several localization, font, scaling, and UI quirks, so this workspace may grow to include additional fixes and quality-of-life mods over time.

## Projects

### Bulgarian Translation

**Status: completed QA / release preparation**

Community-made Bulgarian localization for Mr. Prepper.

Source:
[`src/MrPrepperTranslationMod`](src/MrPrepperTranslationMod)

Documentation:
[`src/MrPrepperTranslationMod/README.md`](src/MrPrepperTranslationMod/README.md)

Bulgarian terminology and localization rules:
[`src/MrPrepperTranslationMod/translations/README.md`](src/MrPrepperTranslationMod/translations/README.md)

### Cyrillic Font Fix

**Status: in development**

Runtime font replacement/fallback experiments intended to improve Cyrillic rendering in parts of the game that do not handle Cyrillic fonts correctly.

Source:
[`src/CyrillicFontFix`](src/CyrillicFontFix)

### 150% DPI / UI Scaling Fix

**Status: working test build**

Tooltip-only fix for the game's very small item popups at high Windows display scaling. It scales the tooltip text and keeps the popup outside the mouse pointer when screen space allows it.

Source:
[`src/MrPrepperTooltipScaleFix`](src/MrPrepperTooltipScaleFix)

The tested configuration template is included at:
[`src/MrPrepperTooltipScaleFix/config`](src/MrPrepperTooltipScaleFix/config)

### In-game Mod Configurator

**Status: exploratory**

Possible in-game configuration UI for compatible Mr. Prepper mods, if the game's UI and modding environment make this practical.

## Requirements

The current runtime mods target the Windows version of Mr. Prepper and use **BepInEx 5.x Mono**.

Individual projects may have additional requirements or configuration options. See the README inside each project directory when available.

## Repository layout

```text
src/
├── MrPrepperTranslationMod/   Bulgarian localization
├── CyrillicFontFix/            Cyrillic font rendering fix (in development)
└── MrPrepperTooltipScaleFix/   Tooltip size and positioning fix

scripts/                       Build/development helpers
useful-mods/                   Additional supporting mod work
```

Generated localization dumps, game assemblies, temporary QA files, local test assets, and other development-only data are not intended to be part of the main repository content.

## Building

Some projects reference files from the local Mr. Prepper installation. Set `GameDir` in a local `User.targets` file when necessary.

Example build command:

```powershell
pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-plugin.ps1
```

## Project scope

This repository contains unofficial community mods and fixes. Projects may be experimental, incomplete, or game-version dependent unless explicitly marked otherwise.

**Mr. Prepper** and its related assets belong to their respective developers and publishers.
