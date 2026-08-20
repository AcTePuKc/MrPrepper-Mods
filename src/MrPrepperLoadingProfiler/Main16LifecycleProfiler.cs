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
public sealed class Main16LifecycleProfiler : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.main16lifecycleprofiler";
    public const string PluginName = "Mr. Prepper Main16 Lifecycle Profiler";
    public const string PluginVersion = "0.3.0";

    private static readonly string[] TargetMethods =
    {
        "Characters.Dialogue.Start",
        "Characters.Dialogue.SetParagraphs",
        "Characters.Dialogue.SetComponents",
        "ItemsInfo.Awake",
        "ComputerImageLoader.Start",
        "TradingManager.Start",
        "SetItemDataFromExcel.Awake",
        "FlipPanelBase.Awake",
        "NPCTextFade.Awake",
        "TextTimerUnscaled.Start",
        "SettingsManager.Start",
        "ListWindow.OnEnable"
    };

    private static Main16LifecycleProfiler instance;
    private static ConfigEntry<bool> profilerEnabled;
    private static ConfigEntry<int> postLoadFrames;
    private static ConfigEntry<double> minimumTotalMs;
    private static ConfigEntry<int> topCount;

    private Harmony harmony;
    private static bool windowArmed;
    private static bool sceneLoaded;
    private static int postLoadFramesRemaining;
    private static int sceneLoadedFrame = -1;
    private static long windowStartedTicks;
    private static long sceneLoadedTicks;

    private static readonly Dictionary<MethodBase, MethodStats> Stats = new();
    private static readonly HashSet<MethodBase> PatchedMethods = new();

    private sealed class MethodStats
    {
        public long Calls;
        public double TotalMs;
        public double MaxMs;
        public int FirstFrame = int.MaxValue;
        public int LastFrame = int.MinValue;
    }

    private void Awake()
    {
        instance = this;
        profilerEnabled = Config.Bind("Main16Lifecycle", "Enabled", true,
            "Profile selected Main16 initialization hotspots discovered by the exploratory passes.");
        postLoadFrames = Config.Bind("Main16Lifecycle", "PostLoadFrames", 8,
            "Number of frames after the Main16 sceneLoaded callback to keep collecting timings.");
        minimumTotalMs = Config.Bind("Main16Lifecycle", "MinimumTotalMs", 1.0,
            "Only methods whose aggregate measured time reaches this threshold are printed in the summary.");
        topCount = Config.Bind("Main16Lifecycle", "TopCount", 20,
            "Maximum number of methods printed in each ranked summary.");

        if (!profilerEnabled.Value)
        {
            Logger.LogInfo($"{PluginName} {PluginVersion} disabled by config.");
            return;
        }

        harmony = new Harmony(PluginGuid);
        var patched = PatchTargetMethods();
        PatchSceneRequests();
        SceneManager.sceneLoaded += OnSceneLoaded;

        Logger.LogInfo(
            $"{PluginName} {PluginVersion} loaded. patchedMethods={patched} " +
            $"postLoadFrames={postLoadFrames.Value} minimumTotalMs={minimumTotalMs.Value:0.###} topCount={topCount.Value}");

        foreach (var method in PatchedMethods.OrderBy(DescribeMethod))
        {
            Logger.LogInfo($"[MAIN16 LIFE PATCH] {DescribeMethod(method)}");
        }
    }

    private int PatchTargetMethods()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal));
        if (assembly == null)
        {
            Logger.LogWarning("[MAIN16 LIFE] Assembly-CSharp was not found; targeted profiling is unavailable.");
            return 0;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
        }

        var typeMap = types
            .Where(t => t != null)
            .GroupBy(t => t.FullName ?? t.Name)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var prefix = new HarmonyMethod(typeof(Main16LifecycleProfiler), nameof(TargetPrefix));
        var postfix = new HarmonyMethod(typeof(Main16LifecycleProfiler), nameof(TargetPostfix));
        var patched = 0;

        foreach (var target in TargetMethods)
        {
            var split = target.LastIndexOf('.');
            if (split <= 0 || split >= target.Length - 1)
            {
                continue;
            }

            var typeName = target.Substring(0, split);
            var methodName = target.Substring(split + 1);

            if (!typeMap.TryGetValue(typeName, out var type))
            {
                Logger.LogWarning($"[MAIN16 LIFE] Target type not found: {typeName}");
                continue;
            }

            MethodInfo method;
            try
            {
                method = type.GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
            }
            catch
            {
                method = null;
            }

            if (!CanPatch(method))
            {
                Logger.LogWarning($"[MAIN16 LIFE] Target method unavailable or not patchable: {target}()");
                continue;
            }

            try
            {
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                PatchedMethods.Add(method);
                patched++;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[MAIN16 LIFE] Could not patch {target}(): {ex.GetType().Name}: {ex.Message}");
            }
        }

        return patched;
    }

    private void PatchSceneRequests()
    {
        foreach (var method in typeof(SceneManager).GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, "LoadSceneAsync", StringComparison.Ordinal) ||
                method.ReturnType != typeof(AsyncOperation))
            {
                continue;
            }

            try
            {
                if (method.GetMethodBody() == null)
                {
                    continue;
                }

                harmony.Patch(method, prefix: new HarmonyMethod(typeof(Main16LifecycleProfiler), nameof(SceneRequestPrefix)));
            }
            catch
            {
            }
        }
    }

    private static bool CanPatch(MethodInfo method)
    {
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.IsSpecialName ||
            method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
        {
            return false;
        }

        try
        {
            return method.GetMethodBody() != null;
        }
        catch
        {
            return false;
        }
    }

    private static void SceneRequestPrefix(object[] __args)
    {
        if (profilerEnabled == null || !profilerEnabled.Value || windowArmed || !ArgumentsContainMain16(__args))
        {
            return;
        }

        Stats.Clear();
        windowArmed = true;
        sceneLoaded = false;
        postLoadFramesRemaining = 0;
        sceneLoadedFrame = -1;
        windowStartedTicks = Stopwatch.GetTimestamp();
        sceneLoadedTicks = 0;

        instance?.Logger.LogInfo(
            $"[MAIN16 LIFE START] realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount} patchedMethods={PatchedMethods.Count}");
    }

    private static void TargetPrefix(MonoBehaviour __instance, MethodBase __originalMethod, ref long __state)
    {
        __state = 0L;
        if (!windowArmed || __instance == null || __originalMethod == null || !BelongsToMain16(__instance))
        {
            return;
        }

        __state = Stopwatch.GetTimestamp();
    }

    private static void TargetPostfix(MethodBase __originalMethod, long __state)
    {
        if (__state == 0L || !windowArmed || __originalMethod == null)
        {
            return;
        }

        var elapsedMs = TicksToMilliseconds(Stopwatch.GetTimestamp() - __state);
        if (!Stats.TryGetValue(__originalMethod, out var stats))
        {
            stats = new MethodStats();
            Stats[__originalMethod] = stats;
        }

        stats.Calls++;
        stats.TotalMs += elapsedMs;
        if (elapsedMs > stats.MaxMs)
        {
            stats.MaxMs = elapsedMs;
        }
        if (Time.frameCount < stats.FirstFrame)
        {
            stats.FirstFrame = Time.frameCount;
        }
        if (Time.frameCount > stats.LastFrame)
        {
            stats.LastFrame = Time.frameCount;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!windowArmed || !string.Equals(scene.name, "Main16", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sceneLoaded = true;
        sceneLoadedFrame = Time.frameCount;
        sceneLoadedTicks = Stopwatch.GetTimestamp();
        postLoadFramesRemaining = Math.Max(1, postLoadFrames.Value);

        Logger.LogInfo(
            $"[MAIN16 LIFE SCENE LOADED] realtime={Time.realtimeSinceStartup:0.000}s frame={sceneLoadedFrame} " +
            $"requestToCallback={TicksToMilliseconds(sceneLoadedTicks - windowStartedTicks):0.0}ms collectedMethods={Stats.Count}");
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
        var inclusiveMeasuredMs = Stats.Values.Sum(s => s.TotalMs);
        var qualifying = Stats
            .Where(pair => pair.Value.TotalMs >= Math.Max(0.0, minimumTotalMs.Value))
            .ToList();
        var limit = Math.Max(1, topCount.Value);

        Logger.LogInfo(
            $"[MAIN16 LIFE SUMMARY] methods={Stats.Count} qualifying={qualifying.Count} calls={Stats.Values.Sum(s => s.Calls)} " +
            $"inclusiveMeasuredTotal={inclusiveMeasuredMs:0.0}ms requestToEnd={TicksToMilliseconds(now - windowStartedTicks):0.0}ms " +
            $"sceneLoadedToEnd={TicksToMilliseconds(now - sceneLoadedTicks):0.0}ms framesAfterSceneLoaded={Math.Max(0, Time.frameCount - sceneLoadedFrame)} " +
            $"note='inclusive total can double-count nested target calls'");

        var byTotal = qualifying.OrderByDescending(pair => pair.Value.TotalMs).Take(limit).ToArray();
        for (var i = 0; i < byTotal.Length; i++)
        {
            LogRank("TOTAL", i + 1, byTotal[i].Key, byTotal[i].Value);
        }

        var byMax = qualifying.OrderByDescending(pair => pair.Value.MaxMs).Take(limit).ToArray();
        for (var i = 0; i < byMax.Length; i++)
        {
            LogRank("MAX", i + 1, byMax[i].Key, byMax[i].Value);
        }
    }

    private void LogRank(string rank, int index, MethodBase method, MethodStats stats)
    {
        Logger.LogInfo(
            $"[MAIN16 LIFE {rank} #{index}] method='{DescribeMethod(method)}' calls={stats.Calls} " +
            $"total={stats.TotalMs:0.000}ms max={stats.MaxMs:0.000}ms frames={stats.FirstFrame}-{stats.LastFrame}");
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

    private static bool BelongsToMain16(MonoBehaviour behaviour)
    {
        try
        {
            var gameObject = behaviour.gameObject;
            if (gameObject == null)
            {
                return false;
            }
            var scene = gameObject.scene;
            return scene.IsValid() && string.Equals(scene.name, "Main16", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static string DescribeMethod(MethodBase method)
    {
        if (method == null)
        {
            return "<null>";
        }
        var type = method.DeclaringType != null ? method.DeclaringType.FullName : "<no-type>";
        return type + "." + method.Name + "()";
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
