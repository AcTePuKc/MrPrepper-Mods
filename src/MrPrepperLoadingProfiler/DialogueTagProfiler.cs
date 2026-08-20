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
    public const string PluginVersion = "0.2.0";

    private static DialogueTagProfiler instance;
    private static ConfigEntry<bool> profilerEnabled;
    private static ConfigEntry<int> postLoadFrames;

    private Harmony harmony;
    private static bool windowArmed;
    private static bool sceneLoaded;
    private static int postLoadFramesRemaining;
    private static long windowStartedTicks;

    [ThreadStatic] private static int getTagDepth;

    private static readonly Dictionary<MethodBase, MethodStats> Stats = new();
    private static readonly Dictionary<string, MethodStats> TagNames = new(StringComparer.Ordinal);

    private sealed class MethodStats
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
    }

    private void Awake()
    {
        instance = this;
        profilerEnabled = Config.Bind("DialogueTag", "Enabled", true,
            "Profile TextTag.GetTag during Main16 dialogue parsing.");
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
        if (textTagType == null)
        {
            Logger.LogWarning("[DIALOGUE TAG] TextTag was not found.");
            return;
        }

        harmony = new Harmony(PluginGuid);

        var byRefString = typeof(string).MakeByRefType();
        var getTag = textTagType.GetMethod(
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

        harmony.Patch(getTag,
            prefix: new HarmonyMethod(typeof(DialogueTagProfiler), nameof(GetTagPrefix)),
            postfix: new HarmonyMethod(typeof(DialogueTagProfiler), nameof(GetTagPostfix)));
        Logger.LogInfo($"[DIALOGUE TAG PATCH] {DescribeMethod(getTag)}");

        // Keep this profiler deliberately narrow. The first version also patched Regex
        // constructors/Match/Replace in mscorlib. A diagnostic run then terminated abruptly
        // during Main16 initialization without a managed crash record. Patching framework
        // library regex internals is therefore avoided here; GetTag inclusive timing is enough
        // to establish whether the tag parser is a meaningful hotspot.
        var getTagPattern = textTagType.GetMethod(
            "GetTagPattern",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(bool) },
            null);
        var nestedPatched = 0;
        if (getTagPattern != null)
        {
            try
            {
                harmony.Patch(getTagPattern,
                    prefix: new HarmonyMethod(typeof(DialogueTagProfiler), nameof(NestedPrefix)),
                    postfix: new HarmonyMethod(typeof(DialogueTagProfiler), nameof(NestedPostfix)));
                nestedPatched = 1;
                Logger.LogInfo($"[DIALOGUE TAG INNER PATCH] {DescribeMethod(getTagPattern)}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DIALOGUE TAG INNER] Could not patch {DescribeMethod(getTagPattern)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

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
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. target={DescribeMethod(getTag)} nestedPatched={nestedPatched} safeMode=True postLoadFrames={postLoadFrames.Value}");
    }

    private static void SceneRequestPrefix(object[] __args)
    {
        if (profilerEnabled == null || !profilerEnabled.Value || windowArmed || !ArgumentsContainMain16(__args)) return;

        Stats.Clear();
        TagNames.Clear();
        getTagDepth = 0;
        sceneLoaded = false;
        postLoadFramesRemaining = 0;
        windowStartedTicks = Stopwatch.GetTimestamp();
        windowArmed = true;
        instance?.Logger.LogInfo($"[DIALOGUE TAG START] realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
    }

    private static void GetTagPrefix(object[] __args, MethodBase __originalMethod, ref long __state)
    {
        __state = 0L;
        if (!windowArmed || __originalMethod == null) return;

        getTagDepth++;
        __state = Stopwatch.GetTimestamp();

        var tagName = __args != null && __args.Length > 1 ? __args[1] as string : null;
        if (!string.IsNullOrEmpty(tagName))
        {
            if (!TagNames.TryGetValue(tagName, out var stats))
            {
                stats = new MethodStats();
                TagNames[tagName] = stats;
            }
            stats.Calls++;
        }
    }

    private static void GetTagPostfix(MethodBase __originalMethod, object[] __args, long __state)
    {
        if (__state != 0L && __originalMethod != null)
        {
            var elapsedMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - __state);
            AddMethodStat(Stats, __originalMethod, elapsedMs);

            var tagName = __args != null && __args.Length > 1 ? __args[1] as string : null;
            if (!string.IsNullOrEmpty(tagName) && TagNames.TryGetValue(tagName, out var stats))
            {
                stats.TotalMs += elapsedMs;
                if (elapsedMs > stats.MaxMs) stats.MaxMs = elapsedMs;
            }
        }

        if (getTagDepth > 0) getTagDepth--;
    }

    private static void NestedPrefix(ref long __state)
    {
        __state = (!windowArmed || getTagDepth <= 0) ? 0L : Stopwatch.GetTimestamp();
    }

    private static void NestedPostfix(MethodBase __originalMethod, long __state)
    {
        if (__state == 0L || __originalMethod == null || !windowArmed || getTagDepth <= 0) return;
        AddMethodStat(Stats, __originalMethod, TicksToMilliseconds(Stopwatch.GetTimestamp() - __state));
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!windowArmed || !string.Equals(scene.name, "Main16", StringComparison.OrdinalIgnoreCase)) return;
        sceneLoaded = true;
        postLoadFramesRemaining = Math.Max(1, postLoadFrames.Value);
        Logger.LogInfo($"[DIALOGUE TAG SCENE LOADED] requestToCallback={TicksToMilliseconds(Stopwatch.GetTimestamp() - windowStartedTicks):0.0}ms");
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
        Logger.LogInfo($"[DIALOGUE TAG SUMMARY] methods={Stats.Count} requestToEnd={TicksToMilliseconds(Stopwatch.GetTimestamp() - windowStartedTicks):0.0}ms safeMode=True note='GetTag is inclusive; framework Regex methods are intentionally not patched'");

        foreach (var pair in Stats.OrderByDescending(p => p.Value.TotalMs))
        {
            var s = pair.Value;
            var avg = s.Calls > 0 ? s.TotalMs / s.Calls : 0.0;
            Logger.LogInfo($"[DIALOGUE TAG INNER] method='{DescribeMethod(pair.Key)}' calls={s.Calls} total={s.TotalMs:0.000}ms avg={avg:0.0000}ms max={s.MaxMs:0.000}ms");
        }

        foreach (var pair in TagNames.OrderByDescending(p => p.Value.TotalMs))
        {
            var s = pair.Value;
            var avg = s.Calls > 0 ? s.TotalMs / s.Calls : 0.0;
            Logger.LogInfo($"[DIALOGUE TAG NAME] tag='{pair.Key}' calls={s.Calls} total={s.TotalMs:0.000}ms avg={avg:0.0000}ms max={s.MaxMs:0.000}ms");
        }
    }

    private static void AddMethodStat(Dictionary<MethodBase, MethodStats> dict, MethodBase method, double elapsedMs)
    {
        if (!dict.TryGetValue(method, out var stats))
        {
            stats = new MethodStats();
            dict[method] = stats;
        }
        stats.Calls++;
        stats.TotalMs += elapsedMs;
        if (elapsedMs > stats.MaxMs) stats.MaxMs = elapsedMs;
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
        getTagDepth = 0;
    }
}
