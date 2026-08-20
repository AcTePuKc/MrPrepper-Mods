using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MrPrepperTranslationMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.uitranslationbulgarian";
    public const string PluginName = "(UI) Mr. Prepper Bulgarian Translation";
    public const string PluginVersion = "0.1.1";

    private static readonly Dictionary<string, string> Translations =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ChangelogTranslations =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly HashSet<string> DumpedText =
        new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> DumpedI2Terms =
        new HashSet<string>(StringComparer.Ordinal);

    private static ManualLogSource log;
    private string pluginDirectory;
    private string translationPath;
    private string changelogPath;
    private string dumpPath;
    private ConfigEntry<bool> enableTranslation;
    private static ConfigEntry<bool> enableChangelogTranslations;
    private ConfigEntry<bool> dumpVisibleText;
    private ConfigEntry<bool> dumpOnly;
    private static ConfigEntry<bool> enableI2Injection;
    private static ConfigEntry<string> i2LanguageName;
    private static ConfigEntry<bool> dumpI2Terms;
    private static string i2DumpPath;
    private static ConfigEntry<bool> dumpI2ReferenceLanguage;
    private static ConfigEntry<string> i2ReferenceLanguageName;
    private static string i2ReferenceDumpPath;
    private static readonly HashSet<string> DumpedI2ReferenceTerms =
        new HashSet<string>(StringComparer.Ordinal);
    private static bool injectingI2;
    private static bool startupLanguageApplied;
    private ConfigEntry<float> scanInterval;
    private float nextScan;

    private void Awake()
    {
        log = Logger;
        pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
        var labelsPath = Path.Combine(pluginDirectory, "translations", "labels.txt");
        var textPath = Path.Combine(pluginDirectory, "translations", "text.txt");
        translationPath = File.Exists(labelsPath) ? labelsPath : textPath;
        changelogPath = Path.Combine(pluginDirectory, "translations", "changelog.txt");
        dumpPath = Path.Combine(pluginDirectory, "dumps", "visible-text.tsv");
        i2DumpPath = Path.Combine(pluginDirectory, "dumps", "i2-terms.tsv");

        enableTranslation = Config.Bind("General", "EnableTranslation", true,
            "Apply exact local translations from translations/labels.txt.");
        enableChangelogTranslations = Config.Bind("General", "EnableChangelogTranslations", true,
            "Apply optional exact translations from translations/changelog.txt.");
        dumpVisibleText = Config.Bind("Diagnostics", "DumpVisibleText", true,
            "Record observed UI text in dumps/visible-text.tsv for later translation.");
        dumpOnly = Config.Bind("Diagnostics", "DumpOnly", false,
            "Record visible text without applying translations during diagnostics.");
        enableI2Injection = Config.Bind("General", "EnableI2LocalizationInjection", true,
            "Inject labels.txt into the game's I2.Loc language source.");
        i2LanguageName = Config.Bind("General", "I2LanguageName", "English",
            "I2 language slot to populate. English is recommended because the game starts in English.");
        dumpI2Terms = Config.Bind("Diagnostics", "DumpI2Terms", false,
            "Dump the complete I2 term catalog and English values once per startup.");
        dumpI2ReferenceLanguage = Config.Bind("Diagnostics", "DumpI2ReferenceLanguage", false,
            "Dump a second I2 language as key=translation for reference.");
        i2ReferenceLanguageName = Config.Bind("Diagnostics", "I2ReferenceLanguageName", "Russian",
            "Second I2 language to dump in key=translation format.");
        i2ReferenceDumpPath = Path.Combine(
            pluginDirectory,
            "dumps",
            "i2-reference-" + SafeFilePart(i2ReferenceLanguageName.Value) + ".txt");
        scanInterval = Config.Bind("Diagnostics", "ScanIntervalSeconds", 0.5f,
            "Seconds between scans of visible UGUI and TextMeshPro text components.");

        Directory.CreateDirectory(Path.GetDirectoryName(translationPath));
        Directory.CreateDirectory(Path.GetDirectoryName(dumpPath));
        if (dumpI2Terms.Value)
        {
            File.WriteAllText(i2DumpPath, "# source\\tterm\\tenglish\\n", Encoding.UTF8);
        }
        if (dumpI2ReferenceLanguage.Value)
        {
            File.WriteAllText(i2ReferenceDumpPath, "# I2 reference language: " + i2ReferenceLanguageName.Value + Environment.NewLine, Encoding.UTF8);
        }
        LoadTranslations();
        InstallTextHooks();
        InstallI2Hooks();
        StartCoroutine(WaitForI2Localization());

        log.LogInfo($"{PluginName} {PluginVersion} loaded");
        log.LogInfo($"Translation entries: {Translations.Count}");
        log.LogInfo($"Translation file: {translationPath}");
        log.LogInfo($"Dump file: {dumpPath}");
    }

    private void InstallI2Hooks()
    {
        var setLanguage = FindMethod(
            typeof(LocalizationManager),
            "SetLanguage",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(string));
        if (setLanguage != null)
        {
            var harmony = new Harmony(PluginGuid + ".i2");
            harmony.Patch(setLanguage, postfix: new HarmonyMethod(typeof(Plugin), nameof(I2_SetLanguage_Postfix)));
            log.LogInfo("I2 hook installed: LocalizationManager.SetLanguage");
        }
        else
        {
            log.LogInfo("I2 LocalizationManager.SetLanguage(string) is not present in this game build; using dictionary injection only.");
        }

        var updateDictionary = FindMethod(
            typeof(LanguageSourceData),
            "UpdateDictionary",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(bool));
        if (updateDictionary != null)
        {
            var harmony = new Harmony(PluginGuid + ".i2.dictionary");
            harmony.Patch(updateDictionary, postfix: new HarmonyMethod(typeof(Plugin), nameof(I2_UpdateDictionary_Postfix)));
            log.LogInfo("I2 hook installed: LanguageSourceData.UpdateDictionary");
        }
    }

    private static MethodInfo FindMethod(Type type, string name, BindingFlags flags, params Type[] parameterTypes)
    {
        return type.GetMethod(name, flags, null, parameterTypes, null);
    }

    private static IEnumerator WaitForI2Localization()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            yield return new WaitForSecondsRealtime(0.25f);
            if (enableI2Injection.Value && InjectI2Translations())
            {
                if (!string.Equals(i2LanguageName.Value, "English", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyStartupLanguage();
                }
                yield break;
            }
        }
    }

    private static void ApplyStartupLanguage()
    {
        if (startupLanguageApplied || string.IsNullOrEmpty(i2LanguageName.Value))
        {
            return;
        }

        startupLanguageApplied = true;
        try
        {
            var setLanguage = FindMethod(
                typeof(LocalizationManager),
                "SetLanguage",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                typeof(string));
            if (setLanguage == null)
            {
                throw new MissingMethodException("I2 LocalizationManager.SetLanguage(string) was not found");
            }

            setLanguage.Invoke(null, new object[] { i2LanguageName.Value });
            log.LogInfo($"I2 startup language selected: {i2LanguageName.Value}");
        }
        catch (Exception ex)
        {
            startupLanguageApplied = false;
            log.LogWarning($"Could not select I2 startup language '{i2LanguageName.Value}': {ex.Message}");
        }
    }

    private static void I2_SetLanguage_Postfix()
    {
        if (enableI2Injection.Value)
        {
            InjectI2Translations();
        }
    }

    private static void I2_UpdateDictionary_Postfix()
    {
        if (enableI2Injection.Value && !injectingI2)
        {
            InjectI2Translations();
        }
    }

    private static bool InjectI2Translations()
    {
        if (injectingI2 || Translations.Count == 0)
        {
            return false;
        }

        injectingI2 = true;
        try
        {
            var sourcesField = typeof(LocalizationManager).GetField(
                "Sources", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var sources = sourcesField?.GetValue(null) as IEnumerable;
            if (sources == null)
            {
                return false;
            }

            var injected = 0;
            var sourceCount = 0;
            var matchedByKey = 0;
            var matchedByEnglishValue = 0;
            var termCount = 0;
            foreach (var sourceObject in sources)
            {
                var source = sourceObject as LanguageSourceData;
                if (source == null)
                {
                    continue;
                }

                sourceCount++;

                var languageIndex = source.GetLanguageIndex(i2LanguageName.Value, true, true);
                if (languageIndex < 0)
                {
                    source.AddLanguage(i2LanguageName.Value);
                    languageIndex = source.GetLanguageIndex(i2LanguageName.Value, true, true);
                }

                if (languageIndex < 0)
                {
                    continue;
                }

                var englishIndex = source.GetLanguageIndex("English", true, true);
                var referenceIndex = source.GetLanguageIndex(i2ReferenceLanguageName.Value, true, true);

                foreach (var term in source.GetTermsList(string.Empty))
                {
                    termCount++;
                    var data = source.GetTermData(term, false);
                    var englishValue = englishIndex >= 0
                        ? data?.GetTranslation(englishIndex, string.Empty, false)
                        : string.Empty;
                    DumpI2Term(source, term, englishValue);
                    var referenceValue = referenceIndex >= 0
                        ? data?.GetTranslation(referenceIndex, string.Empty, false)
                        : string.Empty;
                    DumpI2ReferenceTerm(term, referenceValue);
                    var matched = Translations.TryGetValue(term, out var translation);
                    if (matched)
                    {
                        matchedByKey++;
                    }
                    else if (englishIndex >= 0)
                    {
                        matched = !string.IsNullOrEmpty(englishValue)
                            && Translations.TryGetValue(englishValue, out translation);
                        if (matched)
                        {
                            matchedByEnglishValue++;
                        }
                    }

                    if (!matched)
                    {
                        continue;
                    }

                    if (data == null)
                    {
                        continue;
                    }

                    data.SetTranslation(languageIndex, translation, string.Empty);
                    injected++;
                }

                source.UpdateDictionary(true);
            }

            if (injected > 0)
            {
                log.LogInfo($"I2 localization injection: {injected} translations into '{i2LanguageName.Value}' (by key={matchedByKey}, by English value={matchedByEnglishValue}, terms={termCount}, sources={sourceCount})");
                return true;
            }

            return sourceCount > 0;
        }
        catch (Exception ex)
        {
            log.LogWarning($"I2 localization injection failed: {ex.Message}");
            return false;
        }
        finally
        {
            injectingI2 = false;
        }
    }

    private static void DumpI2Term(LanguageSourceData source, string term, string englishValue)
    {
        if (!dumpI2Terms.Value)
        {
            return;
        }

        var sourceName = source.ownerObject != null ? source.ownerObject.ToString() : "I2Source";
        var normalized = sourceName + "\t" + term + "\t" + (englishValue ?? string.Empty);
        lock (DumpedI2Terms)
        {
            if (!DumpedI2Terms.Add(normalized))
            {
                return;
            }
        }

        var line = EncodeI2Field(sourceName) + "\t" + EncodeI2Field(term) + "\t" + EncodeI2Field(englishValue ?? string.Empty) + Environment.NewLine;
        File.AppendAllText(i2DumpPath, line, Encoding.UTF8);
    }

    private static string EncodeI2Field(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static void DumpI2ReferenceTerm(string term, string value)
    {
        if (!dumpI2ReferenceLanguage.Value || string.IsNullOrEmpty(term) || string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (DumpedI2ReferenceTerms)
        {
            if (!DumpedI2ReferenceTerms.Add(term))
            {
                return;
            }
        }

        var line = EncodeLabelField(term) + "=" + EncodeLabelField(value) + Environment.NewLine;
        File.AppendAllText(i2ReferenceDumpPath, line, Encoding.UTF8);
    }

    private static string EncodeLabelField(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string SafeFilePart(string value)
    {
        var result = (value ?? "reference").Trim().ToLowerInvariant();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(result) ? "reference" : result;
    }

    private void InstallTextHooks()
    {
        var harmony = new Harmony(PluginGuid);
        var tmpSetter = AccessTools.PropertySetter(typeof(TMP_Text), "text");
        var uiSetter = AccessTools.PropertySetter(typeof(Text), "text");
        var tmpPostfix = new HarmonyMethod(typeof(Plugin), nameof(TMP_Text_SetText_Postfix));
        var uiPostfix = new HarmonyMethod(typeof(Plugin), nameof(UnityText_SetText_Postfix));

        if (tmpSetter != null)
        {
            harmony.Patch(tmpSetter, postfix: tmpPostfix);
            log.LogInfo("Static hook installed: TMP_Text.text");
        }
        else
        {
            log.LogError("Could not find TMP_Text.text setter");
        }

        if (uiSetter != null)
        {
            harmony.Patch(uiSetter, postfix: uiPostfix);
            log.LogInfo("Static hook installed: UnityEngine.UI.Text.text");
        }
        else
        {
            log.LogError("Could not find UnityEngine.UI.Text.text setter");
        }

        var setTextMethods = typeof(TMP_Text).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var method in setTextMethods)
        {
            if (!string.Equals(method.Name, "SetText", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 0 ||
                (parameters[0].ParameterType != typeof(string) &&
                 parameters[0].ParameterType != typeof(StringBuilder)))
            {
                continue;
            }

            harmony.Patch(method, postfix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Text_SetText_Postfix)));
            log.LogInfo($"Static hook installed: TMP_Text.{method.Name}({parameters.Length} args)");
        }

        foreach (var method in setTextMethods)
        {
            if (!string.Equals(method.Name, "SetCharArray", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 0 ||
                (parameters[0].ParameterType != typeof(char[]) && parameters[0].ParameterType != typeof(int[])))
            {
                continue;
            }

            harmony.Patch(method, postfix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Text_SetText_Postfix)));
            log.LogInfo($"Static hook installed: TMP_Text.{method.Name}({parameters.Length} args)");
        }

        var tmpOnEnable = AccessTools.Method(typeof(TMP_Text), "OnEnable");
        if (tmpOnEnable != null)
        {
            harmony.Patch(tmpOnEnable, postfix: new HarmonyMethod(typeof(Plugin), nameof(TMP_Text_OnEnable_Postfix)));
            log.LogInfo("Static hook installed: TMP_Text.OnEnable");
        }

        var uiOnEnable = typeof(MaskableGraphic).GetMethod(
            "OnEnable",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (uiOnEnable != null)
        {
            harmony.Patch(uiOnEnable, postfix: new HarmonyMethod(typeof(Plugin), nameof(MaskableGraphic_OnEnable_Postfix)));
            log.LogInfo("Static hook installed: UnityEngine.UI.MaskableGraphic.OnEnable");
        }
    }

    private void Update()
    {
        if (!dumpVisibleText.Value)
        {
            return;
        }

        if (Time.unscaledTime < nextScan)
        {
            return;
        }

        nextScan = Time.unscaledTime + Mathf.Max(0.1f, scanInterval.Value);
        ScanVisibleText();
    }

    private void LoadTranslations()
    {
        Translations.Clear();
        ChangelogTranslations.Clear();
        if (File.Exists(translationPath))
        {
            LoadTranslationFile(translationPath, Translations);
        }
        else
        {
            log.LogWarning($"Translation file not found: {translationPath}");
        }

        if (enableChangelogTranslations.Value && File.Exists(changelogPath))
        {
            LoadTranslationFile(changelogPath, ChangelogTranslations);
        }
    }

    private static void LoadTranslationFile(string path, Dictionary<string, string> target)
    {
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var line = rawLine.TrimEnd('\r', '\n');
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('\t');
            var separatorLength = 1;
            if (separator < 0)
            {
                separator = line.IndexOf('=');
                separatorLength = 1;
            }

            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            var source = Decode(line.Substring(0, separator));
            var translated = Decode(line.Substring(separator + separatorLength));
            if (source.Length == 0 || translated.Length == 0)
            {
                continue;
            }

            target[source] = translated;
        }
    }

    private void ScanVisibleText()
    {
        foreach (var component in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>())
        {
            if (component == null || !component.isActiveAndEnabled)
            {
                continue;
            }

            if (dumpOnly.Value)
            {
                Dump(component.text);
            }
            else
            {
                Process(component.text, value => component.text = value);
            }
        }

        foreach (var component in UnityEngine.Object.FindObjectsOfType<Text>())
        {
            if (component == null || !component.isActiveAndEnabled)
            {
                continue;
            }

            if (dumpOnly.Value)
            {
                Dump(component.text);
            }
            else
            {
                Process(component.text, value => component.text = value);
            }
        }
    }

    private void Process(string value, Action<string> replace)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (dumpVisibleText.Value)
        {
            Dump(value);
        }

        if (enableTranslation.Value && TryGetTranslation(value, out var translated) && translated != value)
        {
            replace(translated);
        }
    }

    private static void TMP_Text_SetText_Postfix(TMP_Text __instance)
    {
        TranslateComponent(__instance);
    }

    private static void UnityText_SetText_Postfix(Text __instance)
    {
        if (__instance == null)
        {
            return;
        }

        var value = __instance.text;
        TranslateValue(ref value);
        if (!string.Equals(value, __instance.text, StringComparison.Ordinal))
        {
            __instance.text = value;
        }
    }

    private static void TranslateComponent(TMP_Text instance)
    {
        if (instance == null)
        {
            return;
        }

        var value = instance.text;
        TranslateValue(ref value);
        if (!string.Equals(value, instance.text, StringComparison.Ordinal))
        {
            instance.text = value;
        }
    }

    private static void TMP_Text_OnEnable_Postfix(TMP_Text __instance)
    {
        if (__instance == null)
        {
            return;
        }

        var value = __instance.text;
        TranslateValue(ref value);
        if (!string.Equals(value, __instance.text, StringComparison.Ordinal))
        {
            __instance.text = value;
        }
    }

    private static void MaskableGraphic_OnEnable_Postfix(MaskableGraphic __instance)
    {
        var text = __instance as Text;
        if (text == null)
        {
            return;
        }

        var value = text.text;
        TranslateValue(ref value);
        if (!string.Equals(value, text.text, StringComparison.Ordinal))
        {
            text.text = value;
        }
    }

    private static void TranslateValue(ref string value)
    {
        if (value == null || !TryGetTranslation(value, out var translated))
        {
            if (value == null)
            {
                return;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0 || !TryGetTranslation(trimmed, out translated))
            {
                return;
            }

            var leading = value.Length - value.TrimStart().Length;
            var trailing = value.Length - value.TrimEnd().Length;
            translated = new string(' ', leading) + translated + new string(' ', trailing);
        }

        if (translated == value)
        {
            return;
        }

        value = translated;
    }

    private static bool TryGetTranslation(string value, out string translated)
    {
        if (Translations.TryGetValue(value, out translated))
        {
            return true;
        }

        return enableChangelogTranslations != null &&
               enableChangelogTranslations.Value &&
               ChangelogTranslations.TryGetValue(value, out translated);
    }

    private void Dump(string value)
    {
        lock (DumpedText)
        {
            if (!DumpedText.Add(value))
            {
                return;
            }
        }

        try
        {
            var line = Escape(value) + "\t" + Escape(value) + Environment.NewLine;
            File.AppendAllText(dumpPath, line, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            log.LogWarning($"Could not write text dump: {ex.Message}");
        }
    }

    private static string Decode(string value)
    {
        return value.Replace("\\r\\n", "\n")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t");
    }

    private static string Escape(string value)
    {
        return value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
