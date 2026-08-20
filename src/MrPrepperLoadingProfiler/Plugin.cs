using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
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
    public const string PluginVersion = "0.8.0";

    private static ManualLogSource log;
    private static ConfigEntry<bool> logUiButtonPresses;
    private static ConfigEntry<bool> inspectSaveSlotPlay;
    private static ConfigEntry<bool> targetedLoadDiagnostics;
    private static ConfigEntry<float> targetedWindowSeconds;
    private static ConfigEntry<float> targetedMethodThresholdMs;
    private static ConfigEntry<bool> traceCoroutines;
    private static ConfigEntry<bool> traceDelayedInvokes;
    private static ConfigEntry<bool> traceSceneRequests;
    private static ConfigEntry<bool> overrideBackgroundLoadingPriority;
    private static ConfigEntry<UnityEngine.ThreadPriority> experimentalBackgroundLoadingPriority;

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

    private static Harmony harmony;
    private float nextVideoScanAt;
    private readonly Dictionary<int, VideoState> videoStates = new();

    private static string lastUserAction = "<none>";
    private static float lastUserActionAt = -1f;
    private static float targetedWindowUntil = -1f;
    private static string targetedTrigger = "<none>";
    private static readonly HashSet<MethodBase> TargetedPatchedMethods = new();
    private static readonly HashSet<MethodBase> DynamicPatchedMethods = new();
    private static readonly Dictionary<Type, string> CoroutineOwners = new();
    private static readonly Dictionary<MethodBase, string> DynamicMethodOwners = new();
    private static readonly List<SceneAsyncState> SceneAsyncOperations = new();
    private static UnityEngine.ThreadPriority originalBackgroundLoadingPriority;
    private static bool backgroundPriorityOverrideActive;

    private sealed class VideoState
    {
        public bool IsPlaying;
        public bool IsPrepared;
        public bool ActiveInHierarchy;
        public bool Enabled;
        public string Description;
    }

    private sealed class SceneAsyncState
    {
        public AsyncOperation Operation;
        public string Request;
        public float StartedAt;
        public float LastProgress = -1f;
        public bool LastIsDone;
        public bool LastAllowSceneActivation;
        public int LastLoggedFrame = -1;
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
        inspectSaveSlotPlay = Config.Bind("Input", "InspectSaveSlotPlay", true,
            "Log callbacks attached to save-slot SavedPanel/Play buttons.");
        logVideoPlayers = Config.Bind("Video", "LogVideoPlayers", true,
            "Discover VideoPlayer components and log active/prepare/play/stop state changes.");
        videoScanIntervalSeconds = Config.Bind("Video", "ScanIntervalSeconds", 0.10f,
            "Polling interval used for VideoPlayer diagnostics.");
        targetedLoadDiagnostics = Config.Bind("Targeted", "EnableSaveSlotTiming", true,
            "Time loading-related work after the save-slot Play button is pressed.");
        targetedWindowSeconds = Config.Bind("Targeted", "WindowSeconds", 15f,
            "How long after pressing save-slot Play targeted tracing remains active.");
        targetedMethodThresholdMs = Config.Bind("Targeted", "MethodLogThresholdMs", 1f,
            "Only targeted methods taking at least this many milliseconds are logged.");
        traceCoroutines = Config.Bind("Targeted", "TraceCoroutines", true,
            "Trace coroutines started during the targeted load window and time their MoveNext steps.");
        traceDelayedInvokes = Config.Bind("Targeted", "TraceDelayedInvokes", true,
            "Trace MonoBehaviour.Invoke/InvokeRepeating calls during the targeted load window.");
        traceSceneRequests = Config.Bind("Targeted", "TraceSceneRequests", true,
            "Trace SceneManager load requests and returned AsyncOperation progress during the targeted load window.");
        overrideBackgroundLoadingPriority = Config.Bind("Experiment", "OverrideBackgroundLoadingPriority", true,
            "Temporarily override Unity's background loading priority only during the save-slot scene load.");
        experimentalBackgroundLoadingPriority = Config.Bind("Experiment", "BackgroundLoadingPriority", UnityEngine.ThreadPriority.High,
            "Unity background loading priority to test during the save-slot scene load. Valid values: Low, BelowNormal, Normal, High.");

        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;

        harmony = new Harmony(PluginGuid);
        InstallUiButtonHook();
        InstallTargetedLoadHooks();
        InstallSchedulingHooks();
        ResetSummaryWindow();

        log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        log.LogInfo($"Unity={Application.unityVersion}; GameVersion={Application.version}; OS={SystemInfo.operatingSystem}");
        log.LogInfo($"CPU={SystemInfo.processorType}; RAM={SystemInfo.systemMemorySize} MB; GPU={SystemInfo.graphicsDeviceName}; VRAM={SystemInfo.graphicsMemorySize} MB");
        log.LogInfo(
            $"Diagnostics: rawMouse={logRawMouseClicks.Value}, uiButtons={logUiButtonPresses.Value}, " +
            $"saveSlotInspection={inspectSaveSlotPlay.Value}, targetedTiming={targetedLoadDiagnostics.Value}, " +
            $"coroutines={traceCoroutines.Value}, invokes={traceDelayedInvokes.Value}, sceneRequests={traceSceneRequests.Value}, video={logVideoPlayers.Value}");
        log.LogInfo(
            $"[LOAD PRIORITY] startup current={Application.backgroundLoadingPriority} | " +
            $"override={overrideBackgroundLoadingPriority.Value} target={experimentalBackgroundLoadingPriority.Value}");
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

        harmony.Patch(press, prefix: new HarmonyMethod(typeof(Plugin), nameof(ButtonPressPrefix)));
        log.LogInfo("[UI DIAG] Hooked UnityEngine.UI.Button.Press.");
    }

    private void InstallTargetedLoadHooks()
    {
        if (!targetedLoadDiagnostics.Value)
        {
            return;
        }

        var saveSlotType = AccessTools.TypeByName("SaveSlotControler");
        if (saveSlotType == null)
        {
            log.LogWarning("[TARGET DIAG] Type 'SaveSlotControler' was not found.");
            return;
        }

        var prefix = new HarmonyMethod(typeof(Plugin), nameof(TargetedMethodPrefix));
        var postfix = new HarmonyMethod(typeof(Plugin), nameof(TargetedMethodPostfix));

        var patched = 0;
        patched += PatchTargetTypeMethods(saveSlotType, prefix, postfix);

        foreach (var nested in saveSlotType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            patched += PatchTargetTypeMethods(nested, prefix, postfix);
        }

        log.LogInfo($"[TARGET DIAG] SaveSlotControler instrumentation ready: patchedMethods={patched} type='{saveSlotType.FullName}'.");
    }

    private void InstallSchedulingHooks()
    {
        if (!targetedLoadDiagnostics.Value)
        {
            return;
        }

        var installed = 0;

        if (traceCoroutines.Value)
        {
            var startEnumerator = AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(IEnumerator) });
            var startString = AccessTools.Method(typeof(MonoBehaviour), "StartCoroutine", new[] { typeof(string) });
            if (startEnumerator != null)
            {
                harmony.Patch(startEnumerator, prefix: new HarmonyMethod(typeof(Plugin), nameof(StartCoroutineEnumeratorPrefix)));
                installed++;
            }
            if (startString != null)
            {
                harmony.Patch(startString, prefix: new HarmonyMethod(typeof(Plugin), nameof(StartCoroutineStringPrefix)));
                installed++;
            }
        }

        if (traceDelayedInvokes.Value)
        {
            var invoke = AccessTools.Method(typeof(MonoBehaviour), "Invoke", new[] { typeof(string), typeof(float) });
            var invokeRepeating = AccessTools.Method(typeof(MonoBehaviour), "InvokeRepeating", new[] { typeof(string), typeof(float), typeof(float) });
            if (invoke != null)
            {
                harmony.Patch(invoke, prefix: new HarmonyMethod(typeof(Plugin), nameof(InvokePrefix)));
                installed++;
            }
            if (invokeRepeating != null)
            {
                harmony.Patch(invokeRepeating, prefix: new HarmonyMethod(typeof(Plugin), nameof(InvokeRepeatingPrefix)));
                installed++;
            }
        }

        if (traceSceneRequests.Value)
        {
            installed += PatchSceneLoadRequests();
        }

        log.LogInfo($"[TARGET DIAG] Scheduling instrumentation ready: hooks={installed}.");
    }

    private int PatchSceneLoadRequests()
    {
        var count = 0;
        foreach (var method in typeof(SceneManager).GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (method == null ||
                (!string.Equals(method.Name, "LoadScene", StringComparison.Ordinal) &&
                 !string.Equals(method.Name, "LoadSceneAsync", StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                if (method.GetMethodBody() == null)
                {
                    continue;
                }

                var postfix = method.ReturnType == typeof(AsyncOperation)
                    ? new HarmonyMethod(typeof(Plugin), nameof(SceneLoadRequestPostfix))
                    : null;

                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(SceneLoadRequestPrefix)),
                    postfix: postfix);
                count++;
            }
            catch
            {
            }
        }
        return count;
    }

    private int PatchTargetTypeMethods(Type type, HarmonyMethod prefix, HarmonyMethod postfix)
    {
        var count = 0;
        MethodInfo[] methods;
        try
        {
            methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            log.LogWarning($"[TARGET DIAG] Could not enumerate methods on '{type.FullName}': {ex.GetType().Name}: {ex.Message}");
            return 0;
        }

        foreach (var method in methods)
        {
            if (!CanPatchManagedMethod(method))
            {
                continue;
            }

            try
            {
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                TargetedPatchedMethods.Add(method);
                count++;

                if (string.Equals(method.Name, "Play", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(method.Name, "MoveNext", StringComparison.Ordinal))
                {
                    log.LogInfo($"[TARGET PATCH] {DescribeMethod(method)} return={method.ReturnType.Name}");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"[TARGET DIAG] Could not patch {DescribeMethod(method)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return count;
    }

    private static bool CanPatchManagedMethod(MethodInfo method)
    {
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.IsSpecialName)
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

    private static void TargetedMethodPrefix(MethodBase __originalMethod, ref long __state)
    {
        __state = 0L;
        if (!IsTargetedWindowActive() || __originalMethod == null)
        {
            return;
        }

        __state = Stopwatch.GetTimestamp();
        if (string.Equals(__originalMethod.Name, "Play", StringComparison.OrdinalIgnoreCase))
        {
            log?.LogInfo(
                $"[TARGET ENTER] method='{DescribeMethod(__originalMethod)}' | realtime={Time.realtimeSinceStartup:0.000}s | " +
                $"frame={Time.frameCount} | trigger='{TrimForLog(targetedTrigger, 160)}'");
        }
    }

    private static void TargetedMethodPostfix(MethodBase __originalMethod, long __state)
    {
        LogTimedTargetMethod("TARGET METHOD", __originalMethod, __state, null);
    }

    private static void StartCoroutineEnumeratorPrefix(MonoBehaviour __instance, IEnumerator routine)
    {
        if (!IsTargetedWindowActive() || traceCoroutines == null || !traceCoroutines.Value || routine == null)
        {
            return;
        }

        var owner = DescribeBehaviour(__instance);
        var routineType = routine.GetType();
        CoroutineOwners[routineType] = owner;

        log?.LogInfo(
            $"[COROUTINE START] owner='{TrimForLog(owner, 180)}' routine='{routineType.FullName}' | " +
            $"realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");

        PatchCoroutineMoveNext(routineType, owner);
    }

    private static void StartCoroutineStringPrefix(MonoBehaviour __instance, string methodName)
    {
        if (!IsTargetedWindowActive() || traceCoroutines == null || !traceCoroutines.Value)
        {
            return;
        }

        var owner = DescribeBehaviour(__instance);
        log?.LogInfo(
            $"[COROUTINE START] owner='{TrimForLog(owner, 180)}' routineMethod='{TrimForLog(methodName)}' | " +
            $"realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");

        TryPatchNamedTarget(__instance, methodName, owner, "COROUTINE METHOD");
    }

    private static void PatchCoroutineMoveNext(Type routineType, string owner)
    {
        if (routineType == null || harmony == null)
        {
            return;
        }

        var moveNext = routineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (!CanPatchManagedMethod(moveNext) || DynamicPatchedMethods.Contains(moveNext))
        {
            return;
        }

        try
        {
            harmony.Patch(
                moveNext,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(DynamicMethodPrefix)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(DynamicMethodPostfix)));
            DynamicPatchedMethods.Add(moveNext);
            DynamicMethodOwners[moveNext] = owner;
            log?.LogInfo($"[COROUTINE PATCH] method='{DescribeMethod(moveNext)}' owner='{TrimForLog(owner, 160)}'");
        }
        catch (Exception ex)
        {
            log?.LogWarning($"[COROUTINE PATCH] failed method='{DescribeMethod(moveNext)}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void InvokePrefix(MonoBehaviour __instance, string methodName, float time)
    {
        if (!IsTargetedWindowActive() || traceDelayedInvokes == null || !traceDelayedInvokes.Value)
        {
            return;
        }

        var owner = DescribeBehaviour(__instance);
        log?.LogInfo(
            $"[DELAYED INVOKE] owner='{TrimForLog(owner, 180)}' method='{TrimForLog(methodName)}' delay={time:0.000}s | " +
            $"realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
        TryPatchNamedTarget(__instance, methodName, owner, "INVOKED METHOD");
    }

    private static void InvokeRepeatingPrefix(MonoBehaviour __instance, string methodName, float time, float repeatRate)
    {
        if (!IsTargetedWindowActive() || traceDelayedInvokes == null || !traceDelayedInvokes.Value)
        {
            return;
        }

        var owner = DescribeBehaviour(__instance);
        log?.LogInfo(
            $"[DELAYED INVOKE] owner='{TrimForLog(owner, 180)}' method='{TrimForLog(methodName)}' delay={time:0.000}s repeat={repeatRate:0.000}s | " +
            $"realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
        TryPatchNamedTarget(__instance, methodName, owner, "INVOKED METHOD");
    }

    private static void TryPatchNamedTarget(MonoBehaviour instance, string methodName, string owner, string category)
    {
        if (instance == null || string.IsNullOrEmpty(methodName) || harmony == null)
        {
            return;
        }

        MethodInfo method = null;
        try
        {
            method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
        }
        catch
        {
        }

        if (!CanPatchManagedMethod(method) || DynamicPatchedMethods.Contains(method) || TargetedPatchedMethods.Contains(method))
        {
            return;
        }

        try
        {
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(Plugin), nameof(DynamicMethodPrefix)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(DynamicMethodPostfix)));
            DynamicPatchedMethods.Add(method);
            DynamicMethodOwners[method] = category + " owner=" + owner;
            log?.LogInfo($"[DYNAMIC PATCH] method='{DescribeMethod(method)}' owner='{TrimForLog(owner, 160)}'");
        }
        catch (Exception ex)
        {
            log?.LogWarning($"[DYNAMIC PATCH] failed method='{DescribeMethod(method)}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DynamicMethodPrefix(MethodBase __originalMethod, ref long __state)
    {
        __state = IsTargetedWindowActive() ? Stopwatch.GetTimestamp() : 0L;
    }

    private static void DynamicMethodPostfix(MethodBase __originalMethod, long __state)
    {
        string owner = null;
        if (__originalMethod != null)
        {
            DynamicMethodOwners.TryGetValue(__originalMethod, out owner);
        }
        LogTimedTargetMethod("TARGET STEP", __originalMethod, __state, owner);
    }

    private static void LogTimedTargetMethod(string category, MethodBase method, long started, string owner)
    {
        if (started == 0L || method == null)
        {
            return;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        var threshold = targetedMethodThresholdMs != null
            ? Math.Max(0.01f, targetedMethodThresholdMs.Value)
            : 1.0;

        if (elapsedMs < threshold &&
            !string.Equals(method.Name, "Play", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var ownerSuffix = string.IsNullOrEmpty(owner) ? string.Empty : $" | owner='{TrimForLog(owner, 180)}'";
        log?.LogInfo(
            $"[{category}] {elapsedMs:0.000} ms | method='{DescribeMethod(method)}' | " +
            $"scene='{GetActiveSceneName()}' | frame={Time.frameCount}{ownerSuffix} | trigger='{TrimForLog(targetedTrigger, 160)}'");
    }

    private static void SceneLoadRequestPrefix(MethodBase __originalMethod, object[] __args, ref long __state)
    {
        __state = 0L;
        if (!IsTargetedWindowActive() || traceSceneRequests == null || !traceSceneRequests.Value)
        {
            return;
        }

        __state = Stopwatch.GetTimestamp();
        var args = DescribeArguments(__args);
        log?.LogInfo(
            $"[SCENE REQUEST] method='{DescribeMethod(__originalMethod)}' args='{TrimForLog(args, 220)}' | " +
            $"realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount} bgPriority={Application.backgroundLoadingPriority}");
    }

    private static void SceneLoadRequestPostfix(MethodBase __originalMethod, object[] __args, long __state, AsyncOperation __result)
    {
        if (__state == 0L)
        {
            return;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        var request = DescribeMethod(__originalMethod) + " args=" + DescribeArguments(__args);
        log?.LogInfo(
            $"[SCENE REQUEST RETURN] {elapsedMs:0.000} ms | request='{TrimForLog(request, 260)}' | " +
            $"operation={(ReferenceEquals(__result, null) ? "<null>" : "returned")} | realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount} " +
            $"bgPriority={Application.backgroundLoadingPriority}");

        if (ReferenceEquals(__result, null))
        {
            return;
        }

        foreach (var existing in SceneAsyncOperations)
        {
            if (ReferenceEquals(existing.Operation, __result))
            {
                return;
            }
        }

        var state = new SceneAsyncState
        {
            Operation = __result,
            Request = request,
            StartedAt = Time.realtimeSinceStartup,
            LastProgress = SafeAsyncProgress(__result),
            LastIsDone = SafeAsyncDone(__result),
            LastAllowSceneActivation = SafeAsyncAllowSceneActivation(__result),
            LastLoggedFrame = Time.frameCount
        };
        SceneAsyncOperations.Add(state);

        log?.LogInfo(
            $"[SCENE ASYNC START] progress={state.LastProgress:0.000} isDone={state.LastIsDone} " +
            $"allowSceneActivation={state.LastAllowSceneActivation} operationPriority={SafeAsyncPriority(__result)} " +
            $"bgPriority={Application.backgroundLoadingPriority} | request='{TrimForLog(request, 220)}' | " +
            $"realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
    }

    private static float SafeAsyncProgress(AsyncOperation operation)
    {
        try { return operation.progress; } catch { return -1f; }
    }

    private static bool SafeAsyncDone(AsyncOperation operation)
    {
        try { return operation.isDone; } catch { return false; }
    }

    private static bool SafeAsyncAllowSceneActivation(AsyncOperation operation)
    {
        try { return operation.allowSceneActivation; } catch { return false; }
    }

    private static int SafeAsyncPriority(AsyncOperation operation)
    {
        try { return operation.priority; } catch { return int.MinValue; }
    }

    private static void SampleSceneAsyncOperations(double frameMs)
    {
        if (SceneAsyncOperations.Count == 0)
        {
            return;
        }

        for (var i = SceneAsyncOperations.Count - 1; i >= 0; i--)
        {
            var state = SceneAsyncOperations[i];
            if (state == null || ReferenceEquals(state.Operation, null))
            {
                SceneAsyncOperations.RemoveAt(i);
                continue;
            }

            var progress = SafeAsyncProgress(state.Operation);
            var isDone = SafeAsyncDone(state.Operation);
            var allow = SafeAsyncAllowSceneActivation(state.Operation);
            var progressChanged = state.LastProgress < 0f || Math.Abs(progress - state.LastProgress) >= 0.005f;
            var stateChanged = isDone != state.LastIsDone || allow != state.LastAllowSceneActivation;
            var stallFrame = frameMs >= 250.0;

            if (progressChanged || stateChanged || stallFrame)
            {
                var age = Math.Max(0f, Time.realtimeSinceStartup - state.StartedAt);
                log?.LogInfo(
                    $"[SCENE ASYNC] progress={progress:0.000} delta={progress - state.LastProgress:+0.000;-0.000;0.000} " +
                    $"isDone={isDone} allowSceneActivation={allow} operationPriority={SafeAsyncPriority(state.Operation)} " +
                    $"bgPriority={Application.backgroundLoadingPriority} | age={age:0.000}s frame={Time.frameCount} " +
                    $"frameDelta={frameMs:0.0}ms | request='{TrimForLog(state.Request, 200)}'");
                state.LastProgress = progress;
                state.LastIsDone = isDone;
                state.LastAllowSceneActivation = allow;
                state.LastLoggedFrame = Time.frameCount;
            }

            if (isDone)
            {
                SceneAsyncOperations.RemoveAt(i);
            }
        }
    }

    private static string DescribeArguments(object[] args)
    {
        if (args == null || args.Length == 0)
        {
            return "<none>";
        }

        var parts = new string[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                parts[i] = args[i] == null ? "null" : args[i].ToString();
            }
            catch
            {
                parts[i] = "<unprintable>";
            }
        }
        return string.Join(", ", parts);
    }

    private static string DescribeBehaviour(MonoBehaviour behaviour)
    {
        if (behaviour == null)
        {
            return "<null>";
        }

        var type = behaviour.GetType().FullName ?? behaviour.GetType().Name;
        var path = behaviour.gameObject != null ? GetObjectPath(behaviour.gameObject) : "<no-gameobject>";
        return type + " @ " + path;
    }

    private static bool IsTargetedWindowActive()
    {
        return targetedLoadDiagnostics != null &&
               targetedLoadDiagnostics.Value &&
               targetedWindowUntil >= 0f &&
               Time.realtimeSinceStartup <= targetedWindowUntil;
    }

    private static void BeginTargetedWindow(string trigger)
    {
        if (targetedLoadDiagnostics == null || !targetedLoadDiagnostics.Value)
        {
            return;
        }

        targetedTrigger = trigger ?? "<unknown>";
        var duration = targetedWindowSeconds != null ? Math.Max(1f, targetedWindowSeconds.Value) : 15f;
        targetedWindowUntil = Time.realtimeSinceStartup + duration;
        SceneAsyncOperations.Clear();
        ApplyBackgroundLoadingPriorityExperiment();

        log?.LogInfo(
            $"[TARGET WINDOW] opened for {duration:0.0}s | realtime={Time.realtimeSinceStartup:0.000}s | " +
            $"frame={Time.frameCount} | trigger='{TrimForLog(targetedTrigger, 180)}'");
    }

    private static void ApplyBackgroundLoadingPriorityExperiment()
    {
        var current = Application.backgroundLoadingPriority;
        if (overrideBackgroundLoadingPriority == null || !overrideBackgroundLoadingPriority.Value)
        {
            log?.LogInfo($"[LOAD PRIORITY] observe-only current={current}");
            return;
        }

        originalBackgroundLoadingPriority = current;
        var target = experimentalBackgroundLoadingPriority != null
            ? experimentalBackgroundLoadingPriority.Value
            : UnityEngine.ThreadPriority.High;

        Application.backgroundLoadingPriority = target;
        backgroundPriorityOverrideActive = true;
        log?.LogInfo($"[LOAD PRIORITY] applied original={originalBackgroundLoadingPriority} target={target} current={Application.backgroundLoadingPriority}");
    }

    private static void RestoreBackgroundLoadingPriority(string reason)
    {
        if (!backgroundPriorityOverrideActive)
        {
            return;
        }

        var before = Application.backgroundLoadingPriority;
        Application.backgroundLoadingPriority = originalBackgroundLoadingPriority;
        backgroundPriorityOverrideActive = false;
        log?.LogInfo(
            $"[LOAD PRIORITY] restored before={before} restored={Application.backgroundLoadingPriority} reason='{TrimForLog(reason, 120)}'");
    }

    private static void ButtonPressPrefix(Button __instance)
    {
        if (log == null || __instance == null)
        {
            return;
        }

        var path = GetObjectPath(__instance.gameObject);
        var label = GetButtonLabel(__instance);
        var description = $"button='{path}'" +
                          (string.IsNullOrEmpty(label) ? string.Empty : $" text='{TrimForLog(label)}'");

        if (logUiButtonPresses != null && logUiButtonPresses.Value)
        {
            RecordUserAction("UI BUTTON", description);
        }

        if (path.IndexOf("/SavedPanel/Play", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            BeginTargetedWindow(description);

            if (inspectSaveSlotPlay != null && inspectSaveSlotPlay.Value)
            {
                LogButtonListeners(__instance, path);
            }
        }
    }

    private static void LogButtonListeners(Button button, string path)
    {
        try
        {
            var onClick = button.onClick;
            var persistentCount = onClick?.GetPersistentEventCount() ?? 0;
            var runtimeDelegates = onClick != null ? GetRuntimeDelegates(onClick) : new List<Delegate>();
            log.LogInfo($"[BUTTON LISTENERS] button='{path}' persistent={persistentCount} runtimeDelegates={runtimeDelegates.Count}");

            if (onClick != null)
            {
                for (var i = 0; i < persistentCount; i++)
                {
                    var target = onClick.GetPersistentTarget(i);
                    var targetType = target != null ? target.GetType().FullName : "<null>";
                    log.LogInfo(
                        $"[BUTTON LISTENER] kind=persistent index={i} target='{targetType}' " +
                        $"method='{onClick.GetPersistentMethodName(i)}'");
                }
            }

            for (var i = 0; i < runtimeDelegates.Count; i++)
            {
                var del = runtimeDelegates[i];
                var targetType = del.Target != null ? del.Target.GetType().FullName : "<static>";
                log.LogInfo(
                    $"[BUTTON LISTENER] kind=runtime index={i} target='{targetType}' method='{DescribeMethod(del.Method)}'");
            }
        }
        catch (Exception ex)
        {
            log.LogWarning($"[BUTTON LISTENERS] Inspection failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<Delegate> GetRuntimeDelegates(UnityEventBase unityEvent)
    {
        var result = new List<Delegate>();

        try
        {
            var prepareInvoke = typeof(UnityEventBase).GetMethod(
                "PrepareInvoke",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var invokables = prepareInvoke?.Invoke(unityEvent, null) as IList;
            if (invokables == null)
            {
                return result;
            }

            foreach (var invokable in invokables)
            {
                var del = FindDelegateField(invokable);
                if (del == null)
                {
                    continue;
                }

                foreach (var item in del.GetInvocationList())
                {
                    var duplicate = false;
                    foreach (var existing in result)
                    {
                        if (existing.Method == item.Method && ReferenceEquals(existing.Target, item.Target))
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                    {
                        result.Add(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(
                $"[BUTTON LISTENERS] Runtime listener reflection failed: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    private static Delegate FindDelegateField(object invokable)
    {
        if (invokable == null)
        {
            return null;
        }

        for (var type = invokable.GetType(); type != null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                try
                {
                    if (field.GetValue(invokable) is Delegate del)
                    {
                        return del;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static string DescribeMethod(MethodBase method)
    {
        if (method == null)
        {
            return "<null>";
        }

        var declaring = method.DeclaringType != null ? method.DeclaringType.FullName : "<no-type>";
        var parameters = method.GetParameters();
        var names = new string[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            names[i] = parameters[i].ParameterType.Name;
        }

        return declaring + "." + method.Name + "(" + string.Join(",", names) + ")";
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
        SampleSceneAsyncOperations(frameMs);

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
                log.LogWarning(
                    $"[STALL] {frameMs:0.0} ms | phase={phase} | scene='{GetActiveSceneName()}' | " +
                    $"frame={Time.frameCount}{GetRecentUserActionSuffix()}{GetTargetedWindowSuffix()}{GetMemorySuffix()}");
            }
        }

        if (watchedPostLoadFramesRemaining > 0)
        {
            var watchedIndex =
                Math.Max(1, postLoadFramesToWatch.Value) - watchedPostLoadFramesRemaining + 1;

            if (frameMs >= Math.Max(1f, stallThresholdMs.Value))
            {
                log.LogInfo(
                    $"[POST-LOAD FRAME] scene='{lastLoadedScene}' | index={watchedIndex} | " +
                    $"frame={Time.frameCount} | {frameMs:0.0} ms{GetRecentUserActionSuffix()}" +
                    $"{GetTargetedWindowSuffix()}{GetMemorySuffix()}");
            }

            watchedPostLoadFramesRemaining--;
        }

        var now = Time.realtimeSinceStartup;
        if (now - summaryStartedAt >= Math.Max(1f, summaryIntervalSeconds.Value))
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
            RecordUserAction(
                "MOUSE",
                $"button=left pos={FormatMousePosition()} target='{GetPointerTarget()}'");
        }

        if (Input.GetMouseButtonDown(1))
        {
            RecordUserAction(
                "MOUSE",
                $"button=right pos={FormatMousePosition()} target='{GetPointerTarget()}'");
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

            return results.Count == 0 || results[0].gameObject == null
                ? "<none>"
                : GetObjectPath(results[0].gameObject);
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

        nextVideoScanAt =
            Time.realtimeSinceStartup + Math.Max(0.02f, videoScanIntervalSeconds.Value);

        VideoPlayer[] players;

        try
        {
            players = Resources.FindObjectsOfTypeAll<VideoPlayer>();
        }
        catch (Exception ex)
        {
            log.LogWarning(
                $"[VIDEO DIAG] Could not enumerate VideoPlayer components: {ex.GetType().Name}: {ex.Message}");
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
            var active = player.gameObject.activeInHierarchy;
            var enabled = player.enabled;

            if (!videoStates.TryGetValue(id, out var state))
            {
                state = new VideoState
                {
                    IsPlaying = isPlaying,
                    IsPrepared = isPrepared,
                    ActiveInHierarchy = active,
                    Enabled = enabled,
                    Description = description
                };
                videoStates[id] = state;
                log.LogInfo(
                    $"[VIDEO FOUND] id={id} | {description} | active={active} enabled={enabled} " +
                    $"playOnAwake={player.playOnAwake} prepared={isPrepared} playing={isPlaying}");
                continue;
            }

            state.Description = description;

            if (state.ActiveInHierarchy != active)
            {
                log.LogInfo(
                    $"[VIDEO STATE] id={id} | active {state.ActiveInHierarchy}->{active} | {description}");
                state.ActiveInHierarchy = active;
            }

            if (state.Enabled != enabled)
            {
                log.LogInfo(
                    $"[VIDEO STATE] id={id} | enabled {state.Enabled}->{enabled} | {description}");
                state.Enabled = enabled;
            }

            if (state.IsPrepared != isPrepared)
            {
                log.LogInfo(
                    $"[VIDEO STATE] id={id} | prepared {state.IsPrepared}->{isPrepared} | {description}");
                state.IsPrepared = isPrepared;
            }

            if (state.IsPlaying != isPlaying)
            {
                log.LogInfo(
                    $"[VIDEO STATE] id={id} | playing {state.IsPlaying}->{isPlaying} | {description}");
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
            var scene =
                player.gameObject.scene.IsValid() && !string.IsNullOrEmpty(player.gameObject.scene.name)
                    ? player.gameObject.scene.name
                    : "<no-scene>";
            var clipName = player.clip != null ? player.clip.name : "<none>";
            var url = string.IsNullOrEmpty(player.url) ? "<none>" : TrimForLog(player.url, 120);

            return
                $"scene='{scene}' object='{path}' source={player.source} clip='{TrimForLog(clipName)}' " +
                $"url='{url}' frame={player.frame} time={player.time:0.000}s length={player.length:0.000}s";
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
        transitionFrom =
            string.IsNullOrEmpty(scene.name) ? $"buildIndex:{scene.buildIndex}" : scene.name;
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

        var sceneName =
            string.IsNullOrEmpty(scene.name) ? $"buildIndex:{scene.buildIndex}" : scene.name;

        if (logSceneEvents.Value)
        {
            if (transitionOpen)
            {
                var eventGapMs =
                    Math.Max(0f, (Time.realtimeSinceStartup - transitionUnloadedAt) * 1000f);

                log.LogInfo(
                    $"[SCENE LOADED] '{transitionFrom}' -> '{sceneName}' | mode={mode} | " +
                    $"unload-to-loaded event gap={eventGapMs:0.0} ms{GetMemorySuffix()}");
            }
            else
            {
                log.LogInfo(
                    $"[SCENE LOADED] '{sceneName}' | mode={mode} | " +
                    $"no preceding unload event observed{GetMemorySuffix()}");
            }
        }

        RestoreBackgroundLoadingPriority("sceneLoaded:" + sceneName);
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

        var oldName =
            string.IsNullOrEmpty(oldScene.name) ? $"buildIndex:{oldScene.buildIndex}" : oldScene.name;
        var newName =
            string.IsNullOrEmpty(newScene.name) ? $"buildIndex:{newScene.buildIndex}" : newScene.name;

        log.LogInfo(
            $"[ACTIVE SCENE] '{oldName}' -> '{newName}'{GetMemorySuffix()}");
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
            $"[SUMMARY] {elapsed:0.0}s | scene='{GetActiveSceneName()}' | frames={frames} | " +
            $"avgFPS={averageFps:0.0} | stalls>={Math.Max(1f, stallThresholdMs.Value):0.#}ms={stallsInWindow} | " +
            $"stallTime={stallTimeInWindowMs:0.0}ms | longest={longestStallInWindowMs:0.0}ms | " +
            $"GC +{gen0 - lastGen0}/+{gen1 - lastGen1}/+{gen2 - lastGen2}{GetMemorySuffix()}");

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
        log.LogInfo(
            $"[{eventName}] scene='{sceneName}' | realtime={Time.realtimeSinceStartup:0.000}s | " +
            $"frame={Time.frameCount}{GetRecentUserActionSuffix()}{GetTargetedWindowSuffix()}{GetMemorySuffix()}");
    }

    private static void RecordUserAction(string category, string description)
    {
        lastUserActionAt = Time.realtimeSinceStartup;
        lastUserAction = category + " " + description;

        log?.LogInfo(
            $"[{category}] realtime={lastUserActionAt:0.000}s | frame={Time.frameCount} | " +
            $"scene='{GetActiveSceneName()}' | {description}");
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

    private static string GetTargetedWindowSuffix()
    {
        if (!IsTargetedWindowActive())
        {
            return string.Empty;
        }

        var remaining = Math.Max(0f, targetedWindowUntil - Time.realtimeSinceStartup);
        return $" | targetedWindow={remaining:0.000}s trigger=\"{TrimForLog(targetedTrigger, 140)}\"";
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
        return string.IsNullOrEmpty(scene.name)
            ? $"buildIndex:{scene.buildIndex}"
            : scene.name;
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

        var normalized =
            value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'").Trim();

        return normalized.Length <= maxLength
            ? normalized
            : normalized.Substring(0, maxLength) + "...";
    }

    private void OnApplicationQuit()
    {
        RestoreBackgroundLoadingPriority("applicationQuit");
        if (profilerEnabled.Value)
        {
            LogPoint("QUIT", GetActiveSceneName());
        }
    }

    private void OnDestroy()
    {
        RestoreBackgroundLoadingPriority("pluginDestroy");
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        SceneAsyncOperations.Clear();
        harmony?.UnpatchSelf();
    }
}
