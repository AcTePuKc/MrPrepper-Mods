using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace MrPrepperLoadingProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.loadingprofiler";
    public const string PluginName = "Mr. Prepper Loading Profiler";
    public const string PluginVersion = "0.2.0";

    private static ManualLogSource log;

    private ConfigEntry<bool> profilerEnabled;
    private ConfigEntry<float> stallThresholdMs;
    private ConfigEntry<float> immediateStallLogThresholdMs;
    private ConfigEntry<float> summaryIntervalSeconds;
    private ConfigEntry<bool> ignoreUnfocused;
    private ConfigEntry<bool> logSceneEvents;
    private ConfigEntry<bool> logMemory;
    private ConfigEntry<int> postLoadFramesToWatch;

    private float summaryStartedAt;
    private int summaryStartedFrame;
    private int stallsInWindow;
    private double stallTimeInWindowMs;
    private double longestStallInWindowMs;

    private int lastGen0;
    private int lastGen1;
    private int lastGen2;

    private bool transitionOpen;
    private string transitionFrom = "<unknown>";
    private float transitionUnloadedAt;
    private int watchedPostLoadFramesRemaining;
    private string lastLoadedScene = "<none>";

    private void Awake()
    {
        log = Logger;

        profilerEnabled = Config.Bind("General", "Enabled", true,
            "Enable runtime profiling and diagnostic logging.");
        stallThresholdMs = Config.Bind("Frames", "StallThresholdMs", 50f,
            "Frames at or above this duration count as stalls in periodic summaries.");
        immediateStallLogThresholdMs = Config.Bind("Frames", "ImmediateStallLogThresholdMs", 250f,
            "Frames at or above this duration are logged immediately.");
        summaryIntervalSeconds = Config.Bind("Frames", "SummaryIntervalSeconds", 10f,
            "How often to print a frame, memory and GC summary.");
        ignoreUnfocused = Config.Bind("Frames", "IgnoreUnfocused", true,
            "Ignore frame stalls while the game window is not focused.");
        postLoadFramesToWatch = Config.Bind("Frames", "PostLoadFramesToWatch", 8,
            "Number of frames after sceneLoaded to classify separately as post-load work.");
        logSceneEvents = Config.Bind("Scenes", "LogSceneEvents", true,
            "Log scene load, unload and active-scene changes.");
        logMemory = Config.Bind("Memory", "LogMemory", true,
            "Include managed, Unity allocator and process working-set memory in diagnostics where available.");

        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;

        ResetSummaryWindow();

        log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        log.LogInfo($"Unity={Application.unityVersion}; GameVersion={Application.version}; OS={SystemInfo.operatingSystem}");
        log.LogInfo($"CPU={SystemInfo.processorType}; RAM={SystemInfo.systemMemorySize} MB; GPU={SystemInfo.graphicsDeviceName}; VRAM={SystemInfo.graphicsMemorySize} MB");
        LogPoint("START", GetActiveSceneName());
    }

    private void Update()
    {
        if (!profilerEnabled.Value)
        {
            return;
        }

        var frameMs = Time.unscaledDeltaTime * 1000.0;
        var focused = Application.isFocused;
        var phase = GetCurrentPhase();

        if ((!ignoreUnfocused.Value || focused) && frameMs >= Math.Max(1f, stallThresholdMs.Value))
        {
            stallsInWindow++;
            stallTimeInWindowMs += frameMs;
            if (frameMs > longestStallInWindowMs)
            {
                longestStallInWindowMs = frameMs;
            }

            if (frameMs >= Math.Max(stallThresholdMs.Value, immediateStallLogThresholdMs.Value))
            {
                log.LogWarning($"[STALL] {frameMs:0.0} ms | phase={phase} | scene='{GetActiveSceneName()}' | frame={Time.frameCount}{GetMemorySuffix()}");
            }
        }

        if (watchedPostLoadFramesRemaining > 0)
        {
            var watchedIndex = Math.Max(1, postLoadFramesToWatch.Value) - watchedPostLoadFramesRemaining + 1;
            if (frameMs >= Math.Max(1f, stallThresholdMs.Value))
            {
                log.LogInfo($"[POST-LOAD FRAME] scene='{lastLoadedScene}' | index={watchedIndex} | frame={Time.frameCount} | {frameMs:0.0} ms{GetMemorySuffix()}");
            }

            watchedPostLoadFramesRemaining--;
        }

        var now = Time.realtimeSinceStartup;
        var interval = Math.Max(1f, summaryIntervalSeconds.Value);
        if (now - summaryStartedAt >= interval)
        {
            LogSummary(now);
            ResetSummaryWindow();
        }
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (!profilerEnabled.Value)
        {
            return;
        }

        transitionOpen = true;
        transitionFrom = string.IsNullOrEmpty(scene.name) ? $"buildIndex:{scene.buildIndex}" : scene.name;
        transitionUnloadedAt = Time.realtimeSinceStartup;

        if (logSceneEvents.Value)
        {
            LogPoint("SCENE UNLOADED", transitionFrom);
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!profilerEnabled.Value)
        {
            return;
        }

        var sceneName = string.IsNullOrEmpty(scene.name) ? $"buildIndex:{scene.buildIndex}" : scene.name;

        if (logSceneEvents.Value)
        {
            if (transitionOpen)
            {
                var eventGapMs = Math.Max(0f, (Time.realtimeSinceStartup - transitionUnloadedAt) * 1000f);
                log.LogInfo($"[SCENE LOADED] '{transitionFrom}' -> '{sceneName}' | mode={mode} | unload-to-loaded event gap={eventGapMs:0.0} ms{GetMemorySuffix()}");
            }
            else
            {
                log.LogInfo($"[SCENE LOADED] '{sceneName}' | mode={mode} | no preceding unload event observed{GetMemorySuffix()}");
            }
        }

        transitionOpen = false;
        transitionFrom = sceneName;
        lastLoadedScene = sceneName;
        watchedPostLoadFramesRemaining = Math.Max(0, postLoadFramesToWatch.Value);
    }

    private void OnActiveSceneChanged(Scene oldScene, Scene newScene)
    {
        if (!profilerEnabled.Value || !logSceneEvents.Value)
        {
            return;
        }

        var oldName = string.IsNullOrEmpty(oldScene.name) ? $"buildIndex:{oldScene.buildIndex}" : oldScene.name;
        var newName = string.IsNullOrEmpty(newScene.name) ? $"buildIndex:{newScene.buildIndex}" : newScene.name;
        log.LogInfo($"[ACTIVE SCENE] '{oldName}' -> '{newName}'{GetMemorySuffix()}");
    }

    private void LogSummary(float now)
    {
        var elapsed = Math.Max(0.001f, now - summaryStartedAt);
        var frames = Math.Max(0, Time.frameCount - summaryStartedFrame);
        var averageFps = frames / elapsed;

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        log.LogInfo(
            $"[SUMMARY] {elapsed:0.0}s | scene='{GetActiveSceneName()}' | frames={frames} | avgFPS={averageFps:0.0} | " +
            $"stalls>={Math.Max(1f, stallThresholdMs.Value):0.#}ms={stallsInWindow} | stallTime={stallTimeInWindowMs:0.0}ms | " +
            $"longest={longestStallInWindowMs:0.0}ms | GC +{gen0 - lastGen0}/+{gen1 - lastGen1}/+{gen2 - lastGen2}{GetMemorySuffix()}");

        lastGen0 = gen0;
        lastGen1 = gen1;
        lastGen2 = gen2;
    }

    private void ResetSummaryWindow()
    {
        summaryStartedAt = Time.realtimeSinceStartup;
        summaryStartedFrame = Time.frameCount;
        stallsInWindow = 0;
        stallTimeInWindowMs = 0;
        longestStallInWindowMs = 0;

        lastGen0 = GC.CollectionCount(0);
        lastGen1 = GC.CollectionCount(1);
        lastGen2 = GC.CollectionCount(2);
    }

    private void LogPoint(string eventName, string sceneName)
    {
        log.LogInfo($"[{eventName}] scene='{sceneName}' | realtime={Time.realtimeSinceStartup:0.000}s | frame={Time.frameCount}{GetMemorySuffix()}");
    }

    private string GetMemorySuffix()
    {
        if (!logMemory.Value)
        {
            return string.Empty;
        }

        var managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        var monoUsedMb = TryGetUnityMemoryMb(() => Profiler.GetMonoUsedSizeLong());
        var monoHeapMb = TryGetUnityMemoryMb(() => Profiler.GetMonoHeapSizeLong());
        var unityAllocatedMb = TryGetUnityMemoryMb(() => Profiler.GetTotalAllocatedMemoryLong());
        var workingSetMb = GetProcessWorkingSetMb();

        var suffix = $" | managed={managedMb:0.0} MB";
        if (monoUsedMb >= 0)
        {
            suffix += $" | monoUsed={monoUsedMb:0.0} MB";
        }
        if (monoHeapMb >= 0)
        {
            suffix += $" | monoHeap={monoHeapMb:0.0} MB";
        }
        if (unityAllocatedMb >= 0)
        {
            suffix += $" | unityAllocated={unityAllocatedMb:0.0} MB";
        }
        if (workingSetMb >= 0)
        {
            suffix += $" | workingSet={workingSetMb:0.0} MB";
        }

        return suffix;
    }

    private static double TryGetUnityMemoryMb(Func<long> getter)
    {
        try
        {
            var bytes = getter();
            return bytes >= 0 ? bytes / (1024.0 * 1024.0) : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static double GetProcessWorkingSetMb()
    {
        try
        {
            using (var process = Process.GetCurrentProcess())
            {
                process.Refresh();
                var bytes = process.WorkingSet64;
                if (bytes <= 0)
                {
                    bytes = Environment.WorkingSet;
                }

                return bytes > 0 ? bytes / (1024.0 * 1024.0) : -1;
            }
        }
        catch
        {
            try
            {
                var bytes = Environment.WorkingSet;
                return bytes > 0 ? bytes / (1024.0 * 1024.0) : -1;
            }
            catch
            {
                return -1;
            }
        }
    }

    private string GetCurrentPhase()
    {
        if (transitionOpen)
        {
            return "scene-transition";
        }

        if (watchedPostLoadFramesRemaining > 0)
        {
            return "post-load";
        }

        return "runtime";
    }

    private static string GetActiveSceneName()
    {
        var scene = SceneManager.GetActiveScene();
        return string.IsNullOrEmpty(scene.name) ? $"buildIndex:{scene.buildIndex}" : scene.name;
    }

    private void OnApplicationQuit()
    {
        if (profilerEnabled.Value)
        {
            LogPoint("QUIT", GetActiveSceneName());
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }
}
