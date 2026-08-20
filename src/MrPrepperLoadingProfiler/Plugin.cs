using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MrPrepperLoadingProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.loadingprofiler";
    public const string PluginName = "Mr. Prepper Loading Profiler";
    public const string PluginVersion = "0.1.0";

    private static ManualLogSource log;

    private ConfigEntry<bool> enabled;
    private ConfigEntry<float> stallThresholdMs;
    private ConfigEntry<float> immediateStallLogThresholdMs;
    private ConfigEntry<float> summaryIntervalSeconds;
    private ConfigEntry<bool> ignoreUnfocused;
    private ConfigEntry<bool> logSceneEvents;
    private ConfigEntry<bool> logMemory;

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

    private void Awake()
    {
        log = Logger;

        enabled = Config.Bind("General", "Enabled", true,
            "Enable runtime profiling and diagnostic logging.");
        stallThresholdMs = Config.Bind("Frames", "StallThresholdMs", 50f,
            "Frames at or above this duration count as stalls in periodic summaries.");
        immediateStallLogThresholdMs = Config.Bind("Frames", "ImmediateStallLogThresholdMs", 250f,
            "Frames at or above this duration are logged immediately.");
        summaryIntervalSeconds = Config.Bind("Frames", "SummaryIntervalSeconds", 10f,
            "How often to print a frame, memory and GC summary.");
        ignoreUnfocused = Config.Bind("Frames", "IgnoreUnfocused", true,
            "Ignore frame stalls while the game window is not focused.");
        logSceneEvents = Config.Bind("Scenes", "LogSceneEvents", true,
            "Log scene load, unload and active-scene changes.");
        logMemory = Config.Bind("Memory", "LogMemory", true,
            "Include managed and process memory in diagnostics where available.");

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
        if (!enabled.Value)
        {
            return;
        }

        var frameMs = Time.unscaledDeltaTime * 1000.0;
        var focused = Application.isFocused;

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
                log.LogWarning($"[STALL] {frameMs:0.0} ms | scene='{GetActiveSceneName()}' | frame={Time.frameCount}{GetMemorySuffix()}");
            }
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
        if (!enabled.Value)
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
        if (!enabled.Value)
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
    }

    private void OnActiveSceneChanged(Scene oldScene, Scene newScene)
    {
        if (!enabled.Value || !logSceneEvents.Value)
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
        var processMb = GetProcessMemoryMb();
        return processMb >= 0
            ? $" | managed={managedMb:0.0} MB | process={processMb:0.0} MB"
            : $" | managed={managedMb:0.0} MB";
    }

    private static double GetProcessMemoryMb()
    {
        try
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.PrivateMemorySize64 / (1024.0 * 1024.0);
            }
        }
        catch
        {
            return -1;
        }
    }

    private static string GetActiveSceneName()
    {
        var scene = SceneManager.GetActiveScene();
        return string.IsNullOrEmpty(scene.name) ? $"buildIndex:{scene.buildIndex}" : scene.name;
    }

    private void OnApplicationQuit()
    {
        if (enabled.Value)
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
