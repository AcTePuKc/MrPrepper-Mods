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
public sealed class DialogueTagProfiler : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.dialoguetagprofiler";
    public const string PluginName = "Mr. Prepper Dialogue Tag Profiler";
    public const string PluginVersion = "0.3.0";

    private static DialogueTagProfiler instance;
    private static ConfigEntry<bool> profilerEnabled;
    private static ConfigEntry<int> postLoadFrames;

    private Harmony harmony;
    private static bool windowArmed;
    private static bool sceneLoaded;
    private static int postLoadFramesRemaining;
    private static long windowStartedTicks;

    private static long totalCalls;
    private static double totalMs;
    private static double maxMs;
    private static readonly Dictionary<string, TagStats> TagNames = new(StringComparer.Ordinal);

    private sealed class TagStats
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
    }

    private struct ProbeState
    {
        public long StartedTicks;
        public string TagName;
    }

    private void Awake()
    {
        instance = this;
        profilerEnabled = Config.Bind("DialogueTag", "Enabled", true,
            "Profile only TextTag.GetTag during Main16 dialogue parsing.");
        postLoadFrames = Config.Bind("DialogueTag", "PostLoadFrames", 8,
            "Number of frames after Main16 sceneLoaded to keep collecting timings.");

        if (!profilerEnabled.Value)
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
            Logger.LogWarning("[DIALOGUE TAG] TextTag.GetTag(String&,String,Boolean) was not found.");
            return;
        }

        harmony = new Harmony(PluginGuid);
        harmony.Patch(getTag,
            prefix: new HarmonyMethod(typeof(DialogueTagProfiler), nameof(GetTagPrefix)),
            postfix: new HarmonyMethod(typeof(DialogueTagProfiler), nameof(GetTagPostfix)));
        Logger.LogInfo($"[DIALOGUE TAG PATCH] {DescribeMethod(getTag)}");

        foreach (var method in typeof(SceneManager).GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, "LoadSceneAsync", StringComparison.Ordinal) || method.ReturnType != typeof(AsyncOperation)) continue;
            try
            {
                if (method.GetMethodBody() != null)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(DialogueTagProfiler), nameof(SceneRequestPrefix)));
            }
            catch { }
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. target={DescribeMethod(getTag)} singlePatch=True explicitArgs=True postLoadFrames={postLoadFrames.Value}");
    }

    private static void SceneRequestPrefix(object[] __args)
    {
        if (profilerEnabled == null || !profilerEnabled.Value || windowArmed || !ArgumentsContainMain16(__args)) return;

        TagNames.Clear();
        totalCalls = 0;
        totalMs = 0;
        maxMs = 0;
        sceneLoaded = false;
        postLoadFramesRemaining = 0;
        windowStartedTicks = Stopwatch.GetTimestamp();
        windowArmed = true;
        instance?.Logger.LogInfo($"[DIALOGUE TAG START] realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
    }

    // Use the explicit second argument instead of Harmony's object[] __args because
    // TextTag.GetTag has a ref string first parameter. This avoids boxing/copying the
    // by-ref argument on a very hot path in old Mono/Harmony.
    private static void GetTagPrefix(string __1, ref ProbeState __state)
    {
        __state = default;
        if (!windowArmed) return;

        __state.StartedTicks = Stopwatch.GetTimestamp();
        __state.TagName = __1;
    }

    private static void GetTagPostfix(ProbeState __state)
    {
        if (__state.StartedTicks == 0L || !windowArmed) return;

        var elapsedMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - __state.StartedTicks);
        totalCalls++;
        totalMs += elapsedMs;
        if (elapsedMs > maxMs) maxMs = elapsedMs;

        var tagName = string.IsNullOrEmpty(__state.TagName) ? "<null-or-empty>" : __state.TagName;
        if (!TagNames.TryGetValue(tagName, out var stats))
        {
            stats = new TagStats();
            TagNames[tagName] = stats;
        }

        stats.Calls++;
        stats.TotalMs += elapsedMs;
        if (elapsedMs > stats.MaxMs) stats.MaxMs = elapsedMs;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!windowArmed || !string.Equals(scene.name, "Main16", StringComparison.OrdinalIgnoreCase)) return;
        sceneLoaded = true;
        postLoadFramesRemaining = Math.Max(1, postLoadFrames.Value);
        Logger.LogInfo($"[DIALOGUE TAG SCENE LOADED] calls={totalCalls} total={totalMs:0.000}ms requestToCallback={TicksToMilliseconds(Stopwatch.GetTimestamp() - windowStartedTicks):0.0}ms");
    }

    private void Update()
    {
        if (!windowArmed || !sceneLoaded) return;
        if (postLoadFramesRemaining > 0) postLoadFramesRemaining--;
        if (postLoadFramesRemaining != 0) return;

        LogSummary();
        windowArmed = false;
        sceneLoaded = false;
    }

    private void LogSummary()
    {
        var avg = totalCalls > 0 ? totalMs / totalCalls : 0.0;
        Logger.LogInfo($"[DIALOGUE TAG SUMMARY] calls={totalCalls} total={totalMs:0.000}ms avg={avg:0.0000}ms max={maxMs:0.000}ms distinctTags={TagNames.Count} requestToEnd={TicksToMilliseconds(Stopwatch.GetTimestamp() - windowStartedTicks):0.0}ms");

        var rank = 0;
        foreach (var pair in TagNames.OrderByDescending(p => p.Value.TotalMs))
        {
            if (++rank > 20) break;
            var s = pair.Value;
            var tagAvg = s.Calls > 0 ? s.TotalMs / s.Calls : 0.0;
            Logger.LogInfo($"[DIALOGUE TAG NAME #{rank}] tag='{pair.Key}' calls={s.Calls} total={s.TotalMs:0.000}ms avg={tagAvg:0.0000}ms max={s.MaxMs:0.000}ms");
        }
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

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        harmony?.UnpatchSelf();
        instance = null;
        windowArmed = false;
        sceneLoaded = false;
    }
}
