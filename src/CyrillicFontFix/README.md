# Mr. Prepper Cyrillic Font Fix

Optional BepInEx Mono mod for Mr. Prepper. It reduces the overly heavy outline used by the game's `HelperText` component and can optionally expose diagnostic or experimental Cyrillic fallback settings.

The fix is independent from the Bulgarian Translation mod. The translation works without it; install this mod only if the Cyrillic text rendering needs improvement.

## Installation

Copy the contents of the release ZIP into the Mr. Prepper game directory:

```text
BepInEx/config/actepukc.mrprepper.cyrillicfontfix.cfg
BepInEx/plugins/AcTePuKc Cyrillic Font Fix/CyrillicFontFix.dll
```

The normal fix is enabled by default. Diagnostics and experimental font replacements are disabled by default and can be enabled in the generated configuration when needed.

## Compatibility

Requires BepInEx 5.x Mono for the Windows version of Mr. Prepper. This mod does not replace the game's original assets.
