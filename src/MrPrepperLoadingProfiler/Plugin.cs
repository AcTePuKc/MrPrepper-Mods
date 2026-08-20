using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace MrPrepperLoadingProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.loadingprofiler";
    public const string PluginName = "Mr. Prepper Loading Profiler";
    public const string PluginVersion = "0.3.0";

    private static ManualLogSource log;
    private static ConfigEntry<bool> logUiButtonPresses;

    private ConfigEntry<bool> profilerEnabled;
    private ConfigEntry<float> stallThresholdMs;
    private ConfigEntry<float> immediateStallLogThresholdMs;
    private ConfigEntry<float> summaryIntervalSeconds;
    private ConfigEntry<bool> ignoreUnfocused;
    private ConfigEntry<bool> logSceneEvents;
    private ConfigEntry<bool> logMemory;
    private ConfigEntry<int> postLoadFramesToWatch;
    private ConfigEntry<bool> logRawMouseClicks;
    private ConfigEntry<bool> logVideoPlayers;
    private ConfigEntry<float> videoScanIntervalSeconds;

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

    private Harmony harmony;
    private float nextVideoScanAt;
    private readonly Dictionary<int, VideoState> videoStates = new();

    private static string lastUserAction = "<none>";
    private static float lastUserActionAt = -1f;

    private sealed class VideoState
    {
        public bool IsPlaying;
        public bool IsPrepared;
        public string Description;
    }

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
            "Include managed and Unity allocator memory in diagnostics where available.");
        logRawMouseClicks = Config.Bind("Input", "LogRawMouseClicks", true,
            "Log mouse clicks and the top EventSystem object under the pointer.");
        logUiButtonPresses = Config.Bind("Input", "LogUiButtonPresses", true,
            "Log Unity UI Button.Press calls before the button callback runs.");
        logVideoPlayers = Config.Bind("Video", "LogVideoPlayers", true,
            "Discover VideoPlayer components and log prepare/play/stop state changes.");
        videoScanIntervalSeconds = Config.Bind("Video", "ScanIntervalSeconds", 0.10f,
            "Polling interval used for VideoPlayer diagnostics.");

        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;

        InstallUiButtonHook();
        ResetSummaryWindow();

        log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        log.LogInfo($"Unity={Application.unityVersion}; GameVersion={Application.version}; OS={SystemInfo.operatingSystem}");
        log.LogInfo($"CPU={SystemInfo.processorType}; RAM={SystemInfo.systemMemorySize} MB; GPU={SystemInfo.graphicsDeviceName}; VRAM={SystemInfo.graphicsMemorySize} MB");
        log.LogInfo($"Diagnostics: rawMouse={logRawMouseClicks.Value}, uiButtons={logUiButtonPresses.Value}, video={logVideoPlayers.Value}");
        LogPoint("START", GetActiveSceneName());
    }

    private void InstallUiButtonHook()
    {
        var press = AccessTools.Method(typeof(Button), "Press");
        if (press == null)
        {
            log.LogWarning("[UI DIAG] UnityEngine.UI.Button.Press was not found; button callback markers are unavailable.");
            return;
        }

        harmony = new Harmony(PluginGuid + ".ui");
        harmony.Patch(press, prefix: new HarmonyMethod(typeof(Plugin), nameof(ButtonPressPrefix)));
        log.LogInfo("[UI DIAG] Hooked UnityEngine.UI.Button.Press.");
    }

    private static void ButtonPressPrefix(Button __instance)
    {
        if (log == null || logUiButtonPresses == null || !logUiButtonPresses.Value || __instance == null)
        {
            return;
        }

        var path = GetObjectPath(__instance.gameObject);
        var label = GetButtonLabel(__instance);
        var description = $"button='{path}'" + (string.IsNullOrEmpty(label) ? string.Empty : $" text='{TrimForLog(label)}'");
        RecordUserAction("UI BUTTON", description);
    }

    private void Update()
    {
        if (!profilerEnabled.Value)
        {
            return;
        }

        CaptureRawInput();
        ScanVideoPlayersIfDue();

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
                log.LogWarning($"[STALL] {frameMs:0.0} ms | phase={phase} | scene='{GetActiveSceneName()}' | frame={Time.frameCount}{GetRecentUserActionSuffix()}{GetMemorySuffix()}");
            }
        }

        if (watchedPostLoadFramesRemaining > 0)
        {
            var watchedIndex = Math.Max(1, postLoadFramesToWatch.Value) - watchedPostLoadFramesRemaining + 1;
            if (frameMs >= Math.Max(1f, stallThresholdMs.Value))
            {
                log.LogInfo($"[POST-LOAD FRAME] scene='{lastLoadedScene}' | index={watchedIndex} | frame={Time.frameCount} | {frameMs:0.0} ms{GetRecentUserActionSuffix()}{GetMemorySuffix()}");
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

    private void CaptureRawInput()
    {
        if (!logRawMouseClicks.Value)
        {
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            RecordUserAction("MOUSE", $"button=left pos={FormatMousePosition()} target='{GetPointerTarget()}'");
        }
        if (Input.GetMouseButtonDown(1))
        {
            RecordUserAction("MOUSE", $"button=right pos={FormatMousePosition()} target='{GetPointerTarget()}'");
        }
    }

    private static string FormatMousePosition()
    {
        var position = Input.mousePosition;
        return $"({position.x:0},{position.y:0})";
    }

    private static string GetPointerTarget()
    {
        try
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return "<no EventSystem>";
            }

            var pointer = new PointerEventData(eventSystem) { position = Input.mousePosition };
            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointer, results);
            if (results.Count == 0 || results[0].gameObject == null)
            {
                return "<none>";
            }

            return GetObjectPath(results[0].gameObject);
        }
        catch (Exception ex)
        {
            return "<raycast failed: " + ex.GetType().Name + ">";
        }
    }

    private void ScanVideoPlayersIfDue()
    {
        if (!logVideoPlayers.Value || Time.realtimeSinceStartup < nextVideoScanAt)
        {
            return;
        }

        nextVideoScanAt = Time.realtimeSinceStartup + Math.Max(0.02f, videoScanIntervalSeconds.Value);

        VideoPlayer[] players;
        try
        {
            players = Resources.FindObjectsOfTypeAll<VideoPlayer>();
        }
        catch (Exception ex)
        {
            log.LogWarning($"[VIDEO DIAG] Could not enumerate VideoPlayer components: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        foreach (var player in players)
        {
            if (player == null)
            {
                continue;
            }

            var id = player.GetInstanceID();
            var description = DescribeVideoPlayer(player);
            var isPlaying = SafeVideoBool(() => player.isPlaying);
            var isPrepared = SafeVideoBool(() => player.isPrepared);

            if (!videoStates.TryGetValue(id, out var state))
            {
                state = new VideoState
                {
                    IsPlaying = isPlaying,
                    IsPrepared = isPrepared,
                    Description = description
                };
                videoStates[id] = state;
                log.LogInfo($"[VIDEO FOUND] id={id} | {description} | prepared={isPrepared} | playing={isPlaying}");
                continue;
            }

            if (!string.Equals(state.Description, description, StringComparison.Ordinal))
            {
                state.Description = description;
            }

            if (state.IsPrepared != isPrepared)
            {
                log.LogInfo($"[VIDEO STATE] id={id} | prepared {state.IsPrepared}->{isPrepared} | {description}");
                state.IsPrepared = isPrepared;
            }

            if (state.IsPlaying != isPlaying)
            {
                log.LogInfo($"[VIDEO STATE] id={id} | playing {state.IsPlaying}->{isPlaying} | {description}");
                state.IsPlaying = isPlaying;
            }
        }
    }

    private static bool SafeVideoBool(Func<bool> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeVideoPlayer(VideoPlayer player)
    {
        try
        {
            var path = GetObjectPath(player.gameObject);
            var scene = player.gameObject.scene.IsValid() && !string.IsNullOrEmpty(player.gameObject.scene.name)
                ? player.gameObject.scene.name
                : "<no-scene>";
            var clipName = player.clip != null ? player.clip.name : "<none>";
            var url = string.IsNullOrEmpty(player.url) ? "<none>" : TrimForLog(player.url, 120);
            var frame = player.frame;
            var time = player.time;
            var length = player.length;
            return $"scene='{scene}' object='{path}' source={player.source} clip='{TrimForLog(clipName)}' url='{url}' frame={frame} time={time:0.000}s length={length:0.000}s";
        }
        catch (Exception ex)
        {
            return $"object='<describe failed>' error={ex.GetType().Name}";
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
        nextVideoScanAt = 0f;
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
        log.LogInfo($"[{eventName}] scene='{sceneName}' | realtime={Time.realtimeSinceStartup:0.000}s | frame={Time.frameCount}{GetRecentUserActionSuffix()}{GetMemorySuffix()}");
    }

    private static void RecordUserAction(string category, string description)
    {
        lastUserActionAt = Time.realtimeSinceStartup;
        lastUserAction = category + " " + description;
        log?.LogInfo($"[{category}] realtime={lastUserActionAt:0.000}s | frame={Time.frameCount} | scene='{GetActiveSceneName()}' | {description}");
    }

    private static string GetRecentUserActionSuffix()
    {
        if (lastUserActionAt < 0f)
        {
            return string.Empty;
        }

        var ageMs = Math.Max(0f, (Time.realtimeSinceStartup - lastUserActionAt) * 1000f);
        if (ageMs > 15000f)
        {
            return string.Empty;
        }

        return $" | lastAction=\"{TrimForLog(lastUserAction, 180)}\" age={ageMs:0}ms";
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

    private static string GetButtonLabel(Button button)
    {
        try
        {
            var tmp = button.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
            {
                return tmp.text;
            }

            var legacy = button.GetComponentInChildren<Text>(true);
            return legacy != null ? legacy.text : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetObjectPath(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return "<null>";
        }

        try
        {
            var names = new List<string>();
            var current = gameObject.transform;
            var guard = 0;
            while (current != null && guard++ < 32)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }
        catch
        {
            return gameObject.name ?? "<unnamed>";
        }
    }

    private static string TrimForLog(string value, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'").Trim();
        return normalized.Length <= maxLength ? normalized : normalized.Substring(0, maxLength) + "...";
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
        harmony?.UnpatchSelf();
    }
}
