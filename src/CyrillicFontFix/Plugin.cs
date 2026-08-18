using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace CyrillicFontFix;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.cyrillicfontfix";
    public const string PluginName = "(Optional) Mr. Prepper Cyrillic Font Fix";
    public const string PluginVersion = "0.1.0";

    private static ManualLogSource log;
    private static ConfigEntry<bool> dumpFontDiagnostics;
    private static ConfigEntry<bool> logAllTextComponents;
    private static ConfigEntry<bool> logLegacyTextComponents;
    private static ConfigEntry<bool> logLoadedLegacyFonts;
    private static ConfigEntry<float> scanInterval;
    private static ConfigEntry<bool> enableLegacyReplacement;
    private static ConfigEntry<string> legacyTargetFontName;
    private static ConfigEntry<string> legacyOsFontName;
    private static ConfigEntry<int> legacyFontSize;
    private static readonly Dictionary<int, UnityEngine.Font> originalLegacyFonts = new();
    private static UnityEngine.Font legacyReplacementFont;
    private static ConfigEntry<bool> enableTestReplacement;
    private static ConfigEntry<bool> enableFallbackFont;
    private static ConfigEntry<bool> excludeVsync;
    private static ConfigEntry<string> targetFontName;
    private static ConfigEntry<string> replacementFontName;
    private static ConfigEntry<float> replacementFontSizeMultiplier;
    private static ConfigEntry<float> characterSpacingAdjustment;
    private static ConfigEntry<float> wordSpacingAdjustment;
    private static ConfigEntry<float> lineSpacingAdjustment;
    private static ConfigEntry<string> characterSpacingOverrides;
    private static readonly HashSet<string> loggedEntries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> loggedFontChains = new(StringComparer.Ordinal);
    private static readonly HashSet<int> replacedComponents = new();
    private static readonly Dictionary<int, TMP_FontAsset> originalFonts = new();
    private static readonly Dictionary<int, float> originalFontSizes = new();
    private static readonly Dictionary<int, float> originalCharacterSpacing = new();
    private static readonly Dictionary<int, float> originalWordSpacing = new();
    private static readonly Dictionary<int, float> originalLineSpacing = new();
    private static float nextScanTime;

    private void Awake()
    {
        log = Logger;
        dumpFontDiagnostics = Config.Bind(
            "Diagnostics",
            "DumpFontDiagnostics",
            true,
            "Log the TMP font used by visible components containing Cyrillic text.");
        logAllTextComponents = Config.Bind(
            "Diagnostics",
            "LogAllTextComponents",
            false,
            "Log all TMP components, including Latin-only text.");
        logLegacyTextComponents = Config.Bind(
            "Diagnostics",
            "LogLegacyTextComponents",
            true,
            "Log legacy Unity UI.Text components and their Font names.");
        logLoadedLegacyFonts = Config.Bind(
            "Diagnostics",
            "LogLoadedLegacyFonts",
            true,
            "Log all loaded legacy Unity Font assets.");
        scanInterval = Config.Bind(
            "Diagnostics",
            "ScanIntervalSeconds",
            0.25f,
            "How often the safe runtime scanner checks for newly created text components.");
        enableLegacyReplacement = Config.Bind(
            "LegacyReplacement",
            "Enabled",
            false,
            "Test replacing legacy UI.Text fonts with a dynamic bold Cyrillic-capable font.");
        legacyTargetFontName = Config.Bind(
            "LegacyReplacement",
            "TargetFont",
            "Intro-Cond-Black-Free",
            "Only this legacy Font is replaced by the test font.");
        legacyOsFontName = Config.Bind(
            "LegacyReplacement",
            "OSFontName",
            "Arial",
            "Installed Windows font family used for the diagnostic replacement.");
        legacyFontSize = Config.Bind(
            "LegacyReplacement",
            "FontSize",
            64,
            "Dynamic diagnostic font size.");
        enableTestReplacement = Config.Bind(
            "TestReplacement",
            "Enabled",
            false,
            "Test replacing the selected TMP font on Cyrillic text only.");
        enableFallbackFont = Config.Bind(
            "FallbackFont",
            "Enabled",
            false,
            "Load the bundled Cyrillic TMP font as a global fallback.");
        excludeVsync = Config.Bind(
            "TestReplacement",
            "ExcludeVSync",
            true,
            "Keep V-Sync text on its original font.");
        targetFontName = Config.Bind(
            "TestReplacement",
            "TargetFont",
            "theboldfont SDF",
            "Only this current TMP font is replaced by the test font.");
        replacementFontName = Config.Bind(
            "TestReplacement",
            "ReplacementFont",
            "intro-cond-black-free SDF",
            "Loaded TMP font asset used for the test replacement.");
        replacementFontSizeMultiplier = Config.Bind(
            "TestReplacement",
            "FontSizeMultiplier",
            1.0f,
            "Multiplier applied to the original TMP font size during the test.");
        characterSpacingAdjustment = Config.Bind(
            "TestReplacement",
            "CharacterSpacingAdjustment",
            0.0f,
            "Added to the original TMP character spacing during the test.");
        wordSpacingAdjustment = Config.Bind(
            "TestReplacement",
            "WordSpacingAdjustment",
            0.0f,
            "Added to the original TMP word spacing during the test.");
        lineSpacingAdjustment = Config.Bind(
            "TestReplacement",
            "LineSpacingAdjustment",
            0.0f,
            "Added to the original TMP line spacing during the test.");
        characterSpacingOverrides = Config.Bind(
            "TestReplacement",
            "CharacterSpacingOverrides",
            string.Empty,
            "Per-text character spacing overrides in the form Text=-10|Other text=-20.");

        log.LogInfo($"{PluginName} {PluginVersion} loaded in safe diagnostic mode");
        LoadFallbackFont();
        LogLoadedFontAssets();
        LogLoadedLegacyFonts();
        CreateLegacyReplacementFont();
    }

    private static void LoadFallbackFont()
    {
        if (!enableFallbackFont.Value)
        {
            return;
        }

        var bundlePath = Path.Combine(Paths.PluginPath, "AcTePuKc Cyrillic Font Fix", "mrprepper-roboto-bold-cyrillic");
        if (!File.Exists(bundlePath))
        {
            log.LogWarning($"Cyrillic font bundle not found: {bundlePath}");
            return;
        }

        var bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null)
        {
            log.LogError("Could not load the Cyrillic font AssetBundle.");
            return;
        }

        var allAssets = bundle.LoadAllAssets();
        if (allAssets == null || allAssets.Length == 0)
        {
            log.LogError("The Cyrillic font AssetBundle contains no loadable assets.");
            return;
        }

        foreach (var asset in allAssets)
        {
            log.LogInfo($"Cyrillic bundle asset: type='{asset.GetType().FullName}', name='{asset.name}'");
        }

        var fallback = new List<TMP_FontAsset>();
        foreach (var asset in allAssets)
        {
            if (asset is TMP_FontAsset fontAsset)
            {
                fallback.Add(fontAsset);
            }
        }

        if (fallback.Count == 0)
        {
            log.LogError("The Cyrillic font AssetBundle contains no TMP_FontAsset after type inspection.");
            return;
        }

        if (TMP_Settings.fallbackFontAssets == null)
        {
            log.LogError("TMP fallback font list is unavailable.");
            return;
        }

        foreach (var fontAsset in fallback)
        {
            if (fontAsset != null && !TMP_Settings.fallbackFontAssets.Contains(fontAsset))
            {
                TMP_Settings.fallbackFontAssets.Add(fontAsset);
                log.LogInfo($"Cyrillic fallback font loaded: '{fontAsset.name}'");
            }
        }
    }

    private void Update()
    {
        if (!dumpFontDiagnostics.Value || Time.unscaledTime < nextScanTime)
        {
            return;
        }

        nextScanTime = Time.unscaledTime + Math.Max(0.05f, scanInterval.Value);
        TMP_Text[] components;
        try
        {
            components = FindObjectsOfType<TMP_Text>();
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not scan TMP components: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var component in components)
        {
            if (TryApplyTestReplacement(component))
            {
                continue;
            }

            LogComponent(component);
        }

        if (logLegacyTextComponents.Value)
        {
            LogLegacyTextComponents();
        }
    }

    private static bool TryApplyTestReplacement(TMP_Text component)
    {
        if (!enableTestReplacement.Value || component == null || component.font == null)
        {
            return false;
        }

        var text = component.text ?? string.Empty;
        var id = component.GetInstanceID();
        var isVsync = excludeVsync.Value && text.IndexOf("V-Sync", StringComparison.OrdinalIgnoreCase) >= 0;
        if (isVsync || !ContainsCyrillic(text))
        {
            RestoreOriginalFont(component, id);
            return false;
        }

        var replaceAnyFont = string.Equals(targetFontName.Value, "*", StringComparison.Ordinal);
        if (!replaceAnyFont && !string.Equals(component.font.name, targetFontName.Value, StringComparison.Ordinal))
        {
            return false;
        }

        var replacement = FindFontAsset(replacementFontName.Value);
        if (replacement == null || replacement == component.font)
        {
            return false;
        }

        if (replacedComponents.Add(id))
        {
            var objectName = component.gameObject == null ? "<null>" : component.gameObject.name;
            var originalFontName = component.font.name;
            originalFonts[id] = component.font;
            originalFontSizes[id] = component.fontSize;
            originalCharacterSpacing[id] = component.characterSpacing;
            originalWordSpacing[id] = component.wordSpacing;
            originalLineSpacing[id] = component.lineSpacing;
            component.font = replacement;
            component.fontSize = component.fontSize * Math.Max(0.1f, replacementFontSizeMultiplier.Value);
            component.characterSpacing += GetCharacterSpacingAdjustment(component.text);
            component.wordSpacing += wordSpacingAdjustment.Value;
            component.lineSpacing += lineSpacingAdjustment.Value;
            component.SetVerticesDirty();
            component.SetLayoutDirty();
            log.LogInfo($"Test font replacement: object='{objectName}', '{originalFontName}' -> '{replacement.name}'");
        }

        return true;
    }

    private static void RestoreOriginalFont(TMP_Text component, int id)
    {
        if (!originalFonts.TryGetValue(id, out var original) || original == null)
        {
            return;
        }

        if (component.font != original)
        {
            component.font = original;
        }

        if (originalFontSizes.TryGetValue(id, out var originalSize))
        {
            component.fontSize = originalSize;
            originalFontSizes.Remove(id);
        }

        if (originalCharacterSpacing.TryGetValue(id, out var originalCharacter))
        {
            component.characterSpacing = originalCharacter;
            originalCharacterSpacing.Remove(id);
        }

        if (originalWordSpacing.TryGetValue(id, out var originalWord))
        {
            component.wordSpacing = originalWord;
            originalWordSpacing.Remove(id);
        }

        if (originalLineSpacing.TryGetValue(id, out var originalLine))
        {
            component.lineSpacing = originalLine;
            originalLineSpacing.Remove(id);
        }

        component.SetVerticesDirty();
        component.SetLayoutDirty();
        originalFonts.Remove(id);
        replacedComponents.Remove(id);
    }

    private static TMP_FontAsset FindFontAsset(string name)
    {
        var assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        foreach (var asset in assets)
        {
            if (asset != null && string.Equals(asset.name, name, StringComparison.Ordinal))
            {
                return asset;
            }
        }

        var direct = Resources.Load<TMP_FontAsset>(name);
        if (direct != null)
        {
            log.LogInfo($"Loaded TMP font asset from Resources: '{direct.name}'");
            return direct;
        }

        var resourcePath = $"fonts & materials/{name}";
        direct = Resources.Load<TMP_FontAsset>(resourcePath);
        if (direct != null)
        {
            log.LogInfo($"Loaded TMP font asset from Resources: '{direct.name}' at '{resourcePath}'");
            return direct;
        }

        return null;
    }

    private static float GetCharacterSpacingAdjustment(string text)
    {
        var overrides = characterSpacingOverrides.Value ?? string.Empty;
        foreach (var item in overrides.Split('|'))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = item.Substring(0, separator).Trim();
            if (!string.Equals(key, text, StringComparison.Ordinal))
            {
                continue;
            }

            if (float.TryParse(item.Substring(separator + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return characterSpacingAdjustment.Value;
    }

    private static void LogComponent(TMP_Text component)
    {
        if (component == null)
        {
            return;
        }

        var text = component.text ?? string.Empty;
        if (!logAllTextComponents.Value && !ContainsCyrillic(text))
        {
            return;
        }

        var fontName = component.font == null ? "<null>" : component.font.name;
        var objectName = component.gameObject == null ? "<null>" : component.gameObject.name;
        if (component.font != null)
        {
            LogFontFallbacks(component.font, text);
        }
        var entry = $"{objectName}|{fontName}|{text}";
        if (loggedEntries.Add(entry))
        {
            log.LogInfo($"TMP font: object='{objectName}', font='{fontName}', size={component.fontSize:0.##}, charSpacing={component.characterSpacing:0.##}, wordSpacing={component.wordSpacing:0.##}, lineSpacing={component.lineSpacing:0.##}, text='{TrimForLog(text)}'");
        }
    }

    private static void LogLoadedFontAssets()
    {
        var assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        foreach (var asset in assets)
        {
            if (asset != null)
            {
                log.LogInfo($"Loaded TMP font asset: '{asset.name}'");
            }
        }
    }

    private static void LogFontFallbacks(TMP_FontAsset font, string text)
    {
        if (font == null)
        {
            return;
        }

        var fallbackNames = GetFontAssetNames(font, "fallbackFontAssetTable", "m_fallbackFontAssetTable");

        var globalNames = new List<string>();
        if (TMP_Settings.fallbackFontAssets != null)
        {
            foreach (var fallback in TMP_Settings.fallbackFontAssets)
            {
                if (fallback != null)
                {
                    globalNames.Add(fallback.name);
                }
            }
        }

        var missing = new List<string>();
        var characterLookup = GetObjectMember(font, "characterLookupTable", "m_characterLookupTable") as IDictionary;
        if (characterLookup != null && text != null)
        {
            var seen = new HashSet<char>();
            foreach (var character in text)
            {
                if (!ContainsCyrillic(character) || !seen.Add(character))
                {
                    continue;
                }

                if (!characterLookup.Contains((uint)character) && !characterLookup.Contains(character))
                {
                    missing.Add($"U+{(int)character:X4}('{character}')");
                }
            }
        }

        var key = $"{font.GetInstanceID()}|{string.Join(",", fallbackNames)}|{string.Join(",", globalNames)}|{string.Join(",", missing)}";
        if (!loggedFontChains.Add(key))
        {
            return;
        }

        log.LogInfo($"TMP fallback diagnostics: font='{font.name}', directFallbacks=[{string.Join(", ", fallbackNames)}], globalFallbacks=[{string.Join(", ", globalNames)}], missingInPrimary=[{string.Join(", ", missing)}]");
    }

    private static List<string> GetFontAssetNames(object owner, params string[] memberNames)
    {
        var names = new List<string>();
        var value = GetObjectMember(owner, memberNames);
        if (value is IEnumerable items)
        {
            foreach (var item in items)
            {
                if (item is TMP_FontAsset fontAsset)
                {
                    names.Add(fontAsset.name);
                }
            }
        }

        return names;
    }

    private static object GetObjectMember(object owner, params string[] memberNames)
    {
        if (owner == null)
        {
            return null;
        }

        var type = owner.GetType();
        foreach (var memberName in memberNames)
        {
            var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                try
                {
                    return property.GetValue(owner, null);
                }
                catch
                {
                    // Continue with the next compatible member name.
                }
            }

            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    return field.GetValue(owner);
                }
                catch
                {
                    // Continue with the next compatible member name.
                }
            }
        }

        return null;
    }

    private static void LogLegacyTextComponents()
    {
        UnityEngine.UI.Text[] components;
        try
        {
            components = FindObjectsOfType<UnityEngine.UI.Text>();
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not scan legacy UI.Text components: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var component in components)
        {
            if (component == null)
            {
                continue;
            }

            var text = component.text ?? string.Empty;
            if (TryApplyLegacyReplacement(component))
            {
                continue;
            }
            if (!ContainsCyrillic(text))
            {
                continue;
            }

            var fontName = component.font == null ? "<null>" : component.font.name;
            var objectName = component.gameObject == null ? "<null>" : component.gameObject.name;
            var entry = $"legacy|{objectName}|{fontName}|{text}";
            if (loggedEntries.Add(entry))
            {
                log.LogInfo($"Legacy UI font: object='{objectName}', font='{fontName}', text='{TrimForLog(text)}'");
            }
        }
    }

    private static void CreateLegacyReplacementFont()
    {
        if (!enableLegacyReplacement.Value)
        {
            return;
        }

        try
        {
            legacyReplacementFont = UnityEngine.Font.CreateDynamicFontFromOSFont(
                legacyOsFontName.Value,
                Math.Max(16, legacyFontSize.Value));
            if (legacyReplacementFont == null)
            {
                log.LogError($"Could not create dynamic legacy font from '{legacyOsFontName.Value}'.");
                return;
            }

            legacyReplacementFont.name = $"{legacyOsFontName.Value} Bold (diagnostic)";
            log.LogInfo($"Legacy diagnostic font created: '{legacyReplacementFont.name}', size={legacyFontSize.Value}");
        }
        catch (Exception ex)
        {
            log.LogError($"Could not create dynamic legacy font: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryApplyLegacyReplacement(UnityEngine.UI.Text component)
    {
        if (!enableLegacyReplacement.Value || legacyReplacementFont == null || component == null)
        {
            return false;
        }

        var id = component.GetInstanceID();
        var text = component.text ?? string.Empty;
        if (!ContainsCyrillic(text) || component.font == null)
        {
            RestoreOriginalLegacyFont(component, id);
            return false;
        }

        if (!string.Equals(component.font.name, legacyTargetFontName.Value, StringComparison.Ordinal))
        {
            return false;
        }

        if (originalLegacyFonts.ContainsKey(id))
        {
            return true;
        }

        originalLegacyFonts[id] = component.font;
        component.font = legacyReplacementFont;
        component.SetVerticesDirty();
        component.SetLayoutDirty();
        var objectName = component.gameObject == null ? "<null>" : component.gameObject.name;
        log.LogInfo($"Legacy font replacement: object='{objectName}', '{legacyTargetFontName.Value}' -> '{legacyReplacementFont.name}'");
        return true;
    }

    private static void RestoreOriginalLegacyFont(UnityEngine.UI.Text component, int id)
    {
        if (!originalLegacyFonts.TryGetValue(id, out var original) || original == null)
        {
            return;
        }

        if (component.font != original)
        {
            component.font = original;
            component.SetVerticesDirty();
            component.SetLayoutDirty();
        }

        originalLegacyFonts.Remove(id);
    }

    private static void LogLoadedLegacyFonts()
    {
        if (!logLoadedLegacyFonts.Value)
        {
            return;
        }

        var fonts = Resources.FindObjectsOfTypeAll<UnityEngine.Font>();
        foreach (var font in fonts)
        {
            if (font != null)
            {
                log.LogInfo($"Loaded legacy Font asset: '{font.name}'");
            }
        }
    }

    private static bool ContainsCyrillic(string value)
    {
        foreach (var character in value)
        {
            if ((character >= '\u0400' && character <= '\u052F') ||
                (character >= '\u2DE0' && character <= '\u2DFF') ||
                (character >= '\uA640' && character <= '\uA69F'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsCyrillic(char value)
    {
        return (value >= '\u0400' && value <= '\u052F') ||
               (value >= '\u2DE0' && value <= '\u2DFF') ||
               (value >= '\uA640' && value <= '\uA69F');
    }

    private static string TrimForLog(string value)
    {
        value = value.Replace("\r", "\\r").Replace("\n", "\\n");
        return value.Length <= 160 ? value : value.Substring(0, 160) + "...";
    }

}
