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
    public const string PluginVersion = "0.2.0";

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

    private static long totalCalls;
    private static double totalMs;
    private static double maxMs;
    private static long nullOrEmptyKeys;
    private static readonly Dictionary<string, KeyStats> Keys = new(StringComparer.Ordinal);

    private sealed class KeyStats
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
            if (dialogueType != null)
            {
                foreach (var candidate in dialogueType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                             .Where(m => string.Equals(m.Name, "SetParagraphsFromLocalization", StringComparison.Ordinal)))
                {
                    Logger.LogWarning($"[DIALOGUE LOC CANDIDATE] {DescribeMethod(candidate)} static={candidate.IsStatic} return={candidate.ReturnType.FullName}");
                }
            }
            return;
        }

        harmony = new Harmony(PluginGuid);
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(TargetPrefix)),
            postfix: new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(TargetPostfix)));

        foreach (var method in typeof(SceneManager).GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, "LoadSceneAsync", StringComparison.Ordinal) ||
                method.ReturnType != typeof(AsyncOperation))
            {
                continue;
            }

            try
            {
                if (method.GetMethodBody() != null)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(DialogueLocalizationProfiler), nameof(SceneRequestPrefix)));
                }
            }
            catch
            {
            }
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. target={DescribeMethod(target)} static={target.IsStatic} postLoadFrames={postLoadFrames.Value}");
    }

    private static void SceneRequestPrefix(object[] __args)
    {
        if (profilerEnabled == null || !profilerEnabled.Value || windowArmed || !ArgumentsContainMain16(__args))
        {
            return;
        }

        Keys.Clear();
        totalCalls = 0;
        totalMs = 0;
        maxMs = 0;
        nullOrEmptyKeys = 0;
        sceneLoaded = false;
        postLoadFramesRemaining = 0;
        windowStartedTicks = Stopwatch.GetTimestamp();
        sceneLoadedTicks = 0;
        windowArmed = true;

        instance?.Logger.LogInfo($"[DIALOGUE LOC START] realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
    }

    private static void TargetPrefix(object[] __args, ref long __state)
    {
        __state = 0L;
        if (!windowArmed)
        {
            return;
        }

        __state = Stopwatch.GetTimestamp();
    }

    private static void TargetPostfix(object[] __args, long __state)
    {
        if (!windowArmed || __state == 0L)
        {
            return;
        }

        var elapsedMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - __state);
        totalCalls++;
        totalMs += elapsedMs;
        if (elapsedMs > maxMs)
        {
            maxMs = elapsedMs;
        }

        var key = (__args != null && __args.Length > 0) ? __args[0] as string : null;
        if (string.IsNullOrEmpty(key))
        {
            nullOrEmptyKeys++;
        }

        key ??= "<null>";
        if (!Keys.TryGetValue(key, out var stats))
        {
            stats = new KeyStats();
            Keys[key] = stats;
        }

        stats.Calls++;
        stats.TotalMs += elapsedMs;
        if (elapsedMs > stats.MaxMs)
        {
            stats.MaxMs = elapsedMs;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!windowArmed || !string.Equals(scene.name, "Main16", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sceneLoaded = true;
        sceneLoadedTicks = Stopwatch.GetTimestamp();
        postLoadFramesRemaining = Math.Max(1, postLoadFrames.Value);
        Logger.LogInfo(
            $"[DIALOGUE LOC SCENE LOADED] calls={totalCalls} distinctKeys={Keys.Count} total={totalMs:0.000}ms " +
            $"requestToCallback={TicksToMilliseconds(sceneLoadedTicks - windowStartedTicks):0.0}ms");
    }

    private void Update()
    {
        if (!windowArmed || !sceneLoaded)
        {
            return;
        }

        if (postLoadFramesRemaining > 0)
        {
            postLoadFramesRemaining--;
        }

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

        Logger.LogInfo(
            $"[DIALOGUE LOC SUMMARY] calls={totalCalls} distinctKeys={Keys.Count} duplicateCalls={duplicateCalls} " +
            $"duplicatePercent={duplicatePercent:0.0}% nullOrEmpty={nullOrEmptyKeys} total={totalMs:0.000}ms " +
            $"avg={averageMs:0.4}ms max={maxMs:0.000}ms requestToEnd={TicksToMilliseconds(now - windowStartedTicks):0.0}ms");

        var limit = Math.Max(1, topKeys.Value);
        var byTime = Keys.OrderByDescending(pair => pair.Value.TotalMs).Take(limit).ToArray();
        for (var i = 0; i < byTime.Length; i++)
        {
            LogKey("TIME", i + 1, byTime[i].Key, byTime[i].Value);
        }

        var byCalls = Keys.OrderByDescending(pair => pair.Value.Calls)
            .ThenByDescending(pair => pair.Value.TotalMs)
            .Take(limit)
            .ToArray();
        for (var i = 0; i < byCalls.Length; i++)
        {
            LogKey("CALLS", i + 1, byCalls[i].Key, byCalls[i].Value);
        }
    }

    private void LogKey(string rank, int index, string key, KeyStats stats)
    {
        Logger.LogInfo(
            $"[DIALOGUE LOC {rank} #{index}] key='{TrimForLog(key, 180)}' calls={stats.Calls} " +
            $"total={stats.TotalMs:0.000}ms avg={(stats.Calls > 0 ? stats.TotalMs / stats.Calls : 0.0):0.4}ms max={stats.MaxMs:0.000}ms");
    }

    private static bool ArgumentsContainMain16(object[] args)
    {
        if (args == null)
        {
            return false;
        }

        foreach (var arg in args)
        {
            if (arg is string value && string.Equals(value, "Main16", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static string DescribeMethod(MethodBase method)
    {
        var declaring = method.DeclaringType != null ? method.DeclaringType.FullName : "<no-type>";
        var parameters = string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name));
        return $"{declaring}.{method.Name}({parameters})";
    }

    private static string TrimForLog(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "<null>";
        }
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
    }
}
