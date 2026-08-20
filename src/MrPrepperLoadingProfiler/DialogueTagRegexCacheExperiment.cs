using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

namespace MrPrepperLoadingProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class DialogueTagRegexCacheExperiment : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.dialoguetagregexcacheexperiment";
    public const string PluginName = "Mr. Prepper Dialogue Tag Regex Cache Experiment";
    public const string PluginVersion = "0.1.0";

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);
    private static long cacheHits;
    private static long cacheMisses;

    private ConfigEntry<bool> experimentEnabled;
    private Harmony harmony;

    private void Awake()
    {
        experimentEnabled = Config.Bind("DialogueTagRegexCache", "Enabled", true,
            "Experimental: cache Regex instances created inside TextTag.GetTag instead of constructing a new Regex for every tag probe.");

        if (!experimentEnabled.Value)
        {
            Logger.LogInfo($"{PluginName} {PluginVersion} disabled by config.");
            return;
        }

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal));
        var textTagType = assembly?.GetType("TextTag", false);
        var byRefString = typeof(string).MakeByRefType();
        var getTag = textTagType?.GetMethod(
            "GetTag",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { byRefString, typeof(string), typeof(bool) },
            null);

        if (getTag == null)
        {
            Logger.LogWarning("[DIALOGUE TAG CACHE] TextTag.GetTag(String&,String,Boolean) was not found.");
            return;
        }

        harmony = new Harmony(PluginGuid);
        harmony.Patch(getTag, transpiler: new HarmonyMethod(typeof(DialogueTagRegexCacheExperiment), nameof(GetTagTranspiler)));
        Logger.LogInfo($"{PluginName} {PluginVersion} enabled. target=TextTag.GetTag(String&,String,Boolean)");
    }

    private static IEnumerable<CodeInstruction> GetTagTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var regexCtor = AccessTools.Constructor(typeof(Regex), new[] { typeof(string) });
        var cachedFactory = AccessTools.Method(typeof(DialogueTagRegexCacheExperiment), nameof(GetCachedRegex));
        var replaced = 0;

        foreach (var instruction in instructions)
        {
            if (regexCtor != null && instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, regexCtor))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, cachedFactory).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
            }
            else
            {
                yield return instruction;
            }
        }

        if (replaced != 1)
            throw new InvalidOperationException($"Expected exactly one Regex(String) constructor in TextTag.GetTag, replaced={replaced}.");
    }

    public static Regex GetCachedRegex(string pattern)
    {
        if (pattern == null)
            return new Regex(pattern);

        lock (CacheLock)
        {
            if (RegexCache.TryGetValue(pattern, out var cached))
            {
                cacheHits++;
                return cached;
            }

            var created = new Regex(pattern);
            RegexCache[pattern] = created;
            cacheMisses++;
            return created;
        }
    }

    private void OnDestroy()
    {
        if (experimentEnabled != null && experimentEnabled.Value)
        {
            Logger.LogInfo($"[DIALOGUE TAG CACHE SUMMARY] patterns={RegexCache.Count} hits={cacheHits} misses={cacheMisses}");
        }

        harmony?.UnpatchSelf();
    }
}
