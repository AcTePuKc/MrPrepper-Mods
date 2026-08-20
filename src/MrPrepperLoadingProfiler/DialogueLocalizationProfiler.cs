using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MrPrepperLoadingProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class DialogueLocalizationProfiler : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.dialoguelocalizationprofiler";
    public const string PluginName = "Mr. Prepper Dialogue Localization Profiler";
    public const string PluginVersion = "0.3.0";

    private static DialogueLocalizationProfiler instance;
    private static ConfigEntry<bool> profilerEnabled;
    private static ConfigEntry<int> postLoadFrames;
    private static ConfigEntry<int> topKeys;

    private Harmony harmony;
    private static bool windowArmed;
    private static bool sceneLoaded;
    private static int postLoadFramesRemaining;
    private static long windowStartedTicks;
    private static long sceneLoadedTicks;

    [ThreadStatic]
    private static int localizationDepth;

    private static long totalCalls;
    private static double totalMs;
    private static double maxMs;
    private static long nullOrEmptyKeys;
    private static readonly Dictionary<string, KeyStats> Keys = new(StringComparer.Ordinal);
    private static readonly Dictionary<MethodBase, MethodStats> InnerStats = new();

    private sealed class KeyStats
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
    }

    private sealed class MethodStats
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
    }

    private void Awake()
    {
        instance = this;
        profilerEnabled = Config.Bind("DialogueLocalization", "Enabled", true,
            "Profile Characters.Dialogue.SetParagraphsFromLocalization(string) during Main16 loading.");
        postLoadFrames = Config.Bind("DialogueLocalization", "PostLoadFrames", 8,
            "Number of frames after Main16 sceneLoaded to keep collecting timings.");
        topKeys = Config.Bind("DialogueLocalization", "TopKeys", 20,
            "Number of localization keys to print by aggregate time and call count.");

        if (!profilerEnabled.Value)
        {
            Logger.LogInfo($"{PluginName} {PluginVersion} disabled by config.");
            return;
        }

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal));
        var dialogueType = assembly?.GetType("Characters.Dialogue", false);
        var target = dialogueType?.GetMethod(
            "SetParagraphsFromLocalization",
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null,
            new[] { typeof(string) },
            null);

        if (target == null)
        {
            Logger.LogWarning("[DIALOGUE LOC] Characters.Dialogue.SetParagraphsFromLocalization(string) was not found.");
            return;
        }

        harmony = new Harmony(PluginGuid);
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(TargetPrefix)),
            postfix: new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(TargetPostfix)));

        var innerPatched = PatchInnerTargets(assembly, dialogueType);

        foreach (var method in typeof(SceneManager).GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, "LoadSceneAsync", StringComparison.Ordinal) || method.ReturnType != typeof(AsyncOperation)) continue;
            try
            {
                if (method.GetMethodBody() != null)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(SceneRequestPrefix)));
            }
            catch { }
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. target={DescribeMethod(target)} static={target.IsStatic} innerPatched={innerPatched} postLoadFrames={postLoadFrames.Value}");
    }

    private int PatchInnerTargets(Assembly assembly, Type dialogueType)
    {
        var targets = new List<MethodBase>();

        var i2Type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("I2.Loc.LocalizationManager", false))
            .FirstOrDefault(t => t != null);
        if (i2Type != null)
        {
            var getTranslation = i2Type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, "GetTranslation", StringComparison.Ordinal) &&
                                     m.ReturnType == typeof(string) &&
                                     m.GetParameters().Length == 7 &&
                                     m.GetParameters()[0].ParameterType == typeof(string));
            if (getTranslation != null) targets.Add(getTranslation);
        }

        var byRefString = typeof(string).MakeByRefType();
        var setFromText = dialogueType?.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => string.Equals(m.Name, "SetParagraphsFromText", StringComparison.Ordinal) &&
                                 m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType == byRefString);
        if (setFromText != null) targets.Add(setFromText);

        var paragraphType = assembly?.GetType("Characters.DialogueParagraph", false);
        var pasteSettings = paragraphType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => string.Equals(m.Name, "PasteSettings", StringComparison.Ordinal) &&
                                 m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType == paragraphType);
        if (pasteSettings != null) targets.Add(pasteSettings);

        var prefix = new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(InnerPrefix));
        var postfix = new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(InnerPostfix));
        var patched = 0;

        foreach (var method in targets.Distinct())
        {
            try
            {
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                patched++;
                Logger.LogInfo($"[DIALOGUE LOC INNER PATCH] {DescribeMethod(method)}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DIALOGUE LOC INNER] Could not patch {DescribeMethod(method)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return patched;
    }

    private static void SceneRequestPrefix(object[] __args)
    {
        if (profilerEnabled == null || !profilerEnabled.Value || windowArmed || !ArgumentsContainMain16(__args)) return;

        Keys.Clear();
        InnerStats.Clear();
        totalCalls = 0;
        totalMs = 0;
        maxMs = 0;
        nullOrEmptyKeys = 0;
        localizationDepth = 0;
        sceneLoaded = false;
        postLoadFramesRemaining = 0;
        windowStartedTicks = Stopwatch.GetTimestamp();
        sceneLoadedTicks = 0;
        windowArmed = true;
        instance?.Logger.LogInfo($"[DIALOGUE LOC START] realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
    }

    private static void TargetPrefix(ref long __state)
    {
        if (!windowArmed)
        {
            __state = 0L;
            return;
        }

        localizationDepth++;
        __state = Stopwatch.GetTimestamp();
    }

    private static void TargetPostfix(object[] __args, long __state)
    {
        if (__state == 0L) return;

        var elapsedMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - __state);
        if (windowArmed)
        {
            totalCalls++;
            totalMs += elapsedMs;
            if (elapsedMs > maxMs) maxMs = elapsedMs;

            var key = (__args != null && __args.Length > 0) ? __args[0] as string : null;
            if (string.IsNullOrEmpty(key)) nullOrEmptyKeys++;
            key ??= "<null>";

            if (!Keys.TryGetValue(key, out var stats))
            {
                stats = new KeyStats();
                Keys[key] = stats;
            }
            stats.Calls++;
            stats.TotalMs += elapsedMs;
            if (elapsedMs > stats.MaxMs) stats.MaxMs = elapsedMs;
        }

        if (localizationDepth > 0) localizationDepth--;
    }

    private static void InnerPrefix(ref long __state)
    {
        __state = windowArmed && localizationDepth > 0 ? Stopwatch.GetTimestamp() : 0L;
    }

    private static void InnerPostfix(MethodBase __originalMethod, long __state)
    {
        if (!windowArmed || localizationDepth <= 0 || __state == 0L || __originalMethod == null) return;

        var elapsedMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - __state);
        if (!InnerStats.TryGetValue(__originalMethod, out var stats))
        {
            stats = new MethodStats();
            InnerStats[__originalMethod] = stats;
        }
        stats.Calls++;
        stats.TotalMs += elapsedMs;
        if (elapsedMs > stats.MaxMs) stats.MaxMs = elapsedMs;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!windowArmed || !string.Equals(scene.name, "Main16", StringComparison.OrdinalIgnoreCase)) return;

        sceneLoaded = true;
        sceneLoadedTicks = Stopwatch.GetTimestamp();
        postLoadFramesRemaining = Math.Max(1, postLoadFrames.Value);
        Logger.LogInfo($"[DIALOGUE LOC SCENE LOADED] calls={totalCalls} distinctKeys={Keys.Count} total={totalMs:0.000}ms requestToCallback={TicksToMilliseconds(sceneLoadedTicks - windowStartedTicks):0.0}ms");
    }

    private void Update()
    {
        if (!windowArmed || !sceneLoaded) return;
        if (postLoadFramesRemaining > 0) postLoadFramesRemaining--;
        if (postLoadFramesRemaining == 0)
        {
            LogSummary();
            windowArmed = false;
            sceneLoaded = false;
        }
    }

    private void LogSummary()
    {
        var now = Stopwatch.GetTimestamp();
        var duplicateCalls = Math.Max(0L, totalCalls - Keys.Count);
        var duplicatePercent = totalCalls > 0 ? duplicateCalls * 100.0 / totalCalls : 0.0;
        var averageMs = totalCalls > 0 ? totalMs / totalCalls : 0.0;
        var innerTotal = InnerStats.Values.Sum(s => s.TotalMs);
        var remainder = Math.Max(0.0, totalMs - innerTotal);

        Logger.LogInfo($"[DIALOGUE LOC SUMMARY] calls={totalCalls} distinctKeys={Keys.Count} duplicateCalls={duplicateCalls} duplicatePercent={duplicatePercent:0.0}% nullOrEmpty={nullOrEmptyKeys} total={totalMs:0.000}ms avg={averageMs:0.0000}ms max={maxMs:0.000}ms innerTotal={innerTotal:0.000}ms remainder={remainder:0.000}ms requestToEnd={TicksToMilliseconds(now - windowStartedTicks):0.0}ms note='innerTotal is inclusive if inner targets ever nest'");

        foreach (var pair in InnerStats.OrderByDescending(pair => pair.Value.TotalMs))
        {
            var stats = pair.Value;
            var avg = stats.Calls > 0 ? stats.TotalMs / stats.Calls : 0.0;
            Logger.LogInfo($"[DIALOGUE LOC INNER] method='{DescribeMethod(pair.Key)}' calls={stats.Calls} total={stats.TotalMs:0.000}ms avg={avg:0.0000}ms max={stats.MaxMs:0.000}ms");
        }

        var limit = Math.Max(1, topKeys.Value);
        var byTime = Keys.OrderByDescending(pair => pair.Value.TotalMs).Take(limit).ToArray();
        for (var i = 0; i < byTime.Length; i++) LogKey("TIME", i + 1, byTime[i].Key, byTime[i].Value);

        var byCalls = Keys.OrderByDescending(pair => pair.Value.Calls).ThenByDescending(pair => pair.Value.TotalMs).Take(limit).ToArray();
        for (var i = 0; i < byCalls.Length; i++) LogKey("CALLS", i + 1, byCalls[i].Key, byCalls[i].Value);
    }

    private void LogKey(string rank, int index, string key, KeyStats stats)
    {
        var avg = stats.Calls > 0 ? stats.TotalMs / stats.Calls : 0.0;
        Logger.LogInfo($"[DIALOGUE LOC {rank} #{index}] key='{TrimForLog(key, 180)}' calls={stats.Calls} total={stats.TotalMs:0.000}ms avg={avg:0.0000}ms max={stats.MaxMs:0.000}ms");
    }

    private static bool ArgumentsContainMain16(object[] args)
    {
        if (args == null) return false;
        foreach (var arg in args)
            if (arg is string value && string.Equals(value, "Main16", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string DescribeMethod(MethodBase method)
    {
        var declaring = method.DeclaringType != null ? method.DeclaringType.FullName : "<no-type>";
        var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));
        return $"{declaring}.{method.Name}({parameters})";
    }

    private static string TrimForLog(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "<null>";
        value = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("'", "\\'");
        return value.Length <= max ? value : value.Substring(0, max) + "...";
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        harmony?.UnpatchSelf();
        instance = null;
        windowArmed = false;
        sceneLoaded = false;
        localizationDepth = 0;
    }
}
