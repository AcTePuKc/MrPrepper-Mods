using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MrPrepperLoadingProfiler;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class LoadBenchmarkPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.loadingbenchmark";
    public const string PluginName = "Mr. Prepper Load Benchmark";
    public const string PluginVersion = "0.9.0";

    private static LoadBenchmarkPlugin instance;
    private static ConfigEntry<bool> enabled;
    private static ConfigEntry<UnityEngine.ThreadPriority> benchmarkPriority;
    private static ConfigEntry<int> postLoadFrames;

    private Harmony harmony;
    private static bool runActive;
    private static float buttonAt = -1f;
    private static float requestAt = -1f;
    private static float progress90At = -1f;
    private static float sceneLoadedAt = -1f;
    private static UnityEngine.ThreadPriority naturalPriority;
    private static UnityEngine.ThreadPriority appliedPriority;
    private static AsyncOperation sceneOperation;
    private static string sceneRequest = "<none>";
    private static int postLoadSamplesRemaining;
    private static readonly List<double> PostLoadFrameMs = new();
    private static double largestPreLoadFrameMs;

    private void Awake()
    {
        instance = this;
        enabled = Config.Bind("Benchmark", "Enabled", true,
            "Run a compact save-load benchmark after the second Continue button.");
        benchmarkPriority = Config.Bind("Benchmark", "BackgroundLoadingPriority", UnityEngine.ThreadPriority.Normal,
            "Priority to force for the benchmark load. Valid values: Low, BelowNormal, Normal, High.");
        postLoadFrames = Config.Bind("Benchmark", "PostLoadFrames", 8,
            "Number of Main16 Update frames to include in the compact benchmark result.");

        if (!enabled.Value)
        {
            Logger.LogInfo($"{PluginName} {PluginVersion} disabled by config.");
            return;
        }

        harmony = new Harmony(PluginGuid);
        InstallHooks();
        SceneManager.sceneLoaded += OnSceneLoaded;

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. benchmarkPriority={benchmarkPriority.Value} postLoadFrames={postLoadFrames.Value}");
    }

    private void InstallHooks()
    {
        var press = AccessTools.Method(typeof(Button), "Press");
        if (press != null)
        {
            var prefix = new HarmonyMethod(typeof(LoadBenchmarkPlugin), nameof(ButtonPressPrefix))
            {
                priority = Priority.First
            };
            var postfix = new HarmonyMethod(typeof(LoadBenchmarkPlugin), nameof(ButtonPressPostfix))
            {
                priority = Priority.Last
            };
            harmony.Patch(press, prefix: prefix, postfix: postfix);
        }

        foreach (var method in typeof(SceneManager).GetMethods(BindingFlags.Static | BindingFlags.Public))
        {
            if (!string.Equals(method.Name, "LoadSceneAsync", StringComparison.Ordinal) || method.ReturnType != typeof(AsyncOperation))
            {
                continue;
            }

            try
            {
                if (method.GetMethodBody() == null)
                {
                    continue;
                }

                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(typeof(LoadBenchmarkPlugin), nameof(SceneRequestPrefix)),
                    postfix: new HarmonyMethod(typeof(LoadBenchmarkPlugin), nameof(SceneRequestPostfix)));
            }
            catch
            {
            }
        }
    }

    private static void ButtonPressPrefix(Button __instance)
    {
        if (!IsSaveSlotPlay(__instance))
        {
            return;
        }

        runActive = true;
        buttonAt = Time.realtimeSinceStartup;
        requestAt = -1f;
        progress90At = -1f;
        sceneLoadedAt = -1f;
        sceneOperation = null;
        sceneRequest = "<none>";
        largestPreLoadFrameMs = 0.0;
        PostLoadFrameMs.Clear();
        postLoadSamplesRemaining = 0;
        naturalPriority = Application.backgroundLoadingPriority;

        instance?.Logger.LogInfo(
            $"[BENCHMARK START] naturalPriority={naturalPriority} targetPriority={benchmarkPriority.Value} realtime={buttonAt:0.000}s frame={Time.frameCount}");
    }

    private static void ButtonPressPostfix(Button __instance)
    {
        if (!runActive || !IsSaveSlotPlay(__instance))
        {
            return;
        }

        Application.backgroundLoadingPriority = benchmarkPriority.Value;
        appliedPriority = Application.backgroundLoadingPriority;
        instance?.Logger.LogInfo(
            $"[BENCHMARK PRIORITY] applied={appliedPriority} natural={naturalPriority} realtime={Time.realtimeSinceStartup:0.000}s frame={Time.frameCount}");
    }

    private static bool IsSaveSlotPlay(Button button)
    {
        if (button == null || button.gameObject == null)
        {
            return false;
        }

        var path = GetObjectPath(button.gameObject);
        return path.IndexOf("/SavedPanel/Play", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SceneRequestPrefix(MethodBase __originalMethod, object[] __args)
    {
        if (!runActive)
        {
            return;
        }

        var args = DescribeArguments(__args);
        if (args.IndexOf("Main16", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        if (requestAt < 0f)
        {
            requestAt = Time.realtimeSinceStartup;
            sceneRequest = DescribeMethod(__originalMethod) + " args=" + args;
            instance?.Logger.LogInfo(
                $"[BENCHMARK REQUEST] priority={Application.backgroundLoadingPriority} realtime={requestAt:0.000}s frame={Time.frameCount} request='{Trim(sceneRequest, 220)}'");
        }
    }

    private static void SceneRequestPostfix(object[] __args, AsyncOperation __result)
    {
        if (!runActive || ReferenceEquals(__result, null) || !ReferenceEquals(sceneOperation, null))
        {
            return;
        }

        var args = DescribeArguments(__args);
        if (args.IndexOf("Main16", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        sceneOperation = __result;
    }

    private void Update()
    {
        if (!runActive)
        {
            return;
        }

        var frameMs = Time.unscaledDeltaTime * 1000.0;

        if (sceneLoadedAt < 0f)
        {
            if (frameMs > largestPreLoadFrameMs)
            {
                largestPreLoadFrameMs = frameMs;
            }

            if (!ReferenceEquals(sceneOperation, null) && progress90At < 0f)
            {
                float progress;
                try { progress = sceneOperation.progress; } catch { progress = -1f; }
                if (progress >= 0.899f)
                {
                    progress90At = Time.realtimeSinceStartup;
                    Logger.LogInfo(
                        $"[BENCHMARK 90] requestTo90={Seconds(requestAt, progress90At):0.000}s frameDelta={frameMs:0.0}ms progress={progress:0.000} priority={Application.backgroundLoadingPriority}");
                }
            }
            return;
        }

        if (postLoadSamplesRemaining <= 0)
        {
            return;
        }

        PostLoadFrameMs.Add(frameMs);
        postLoadSamplesRemaining--;

        if (postLoadSamplesRemaining == 0)
        {
            LogBenchmarkResult();
            runActive = false;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!runActive || !string.Equals(scene.name, "Main16", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sceneLoadedAt = Time.realtimeSinceStartup;
        postLoadSamplesRemaining = Math.Max(1, postLoadFrames.Value);
        Application.backgroundLoadingPriority = naturalPriority;

        Logger.LogInfo(
            $"[BENCHMARK SCENE LOADED] requestToSceneLoaded={Seconds(requestAt, sceneLoadedAt):0.000}s progress90ToSceneLoaded={Seconds(progress90At, sceneLoadedAt):0.000}s restoredPriority={Application.backgroundLoadingPriority}");
    }

    private void LogBenchmarkResult()
    {
        var largest = 0.0;
        var secondLargest = 0.0;
        var total = 0.0;
        foreach (var value in PostLoadFrameMs)
        {
            total += value;
            if (value >= largest)
            {
                secondLargest = largest;
                largest = value;
            }
            else if (value > secondLargest)
            {
                secondLargest = value;
            }
        }

        var endAt = Time.realtimeSinceStartup;
        var postWindowMs = total;
        Logger.LogInfo(
            $"[LOAD BENCHMARK] priority={appliedPriority} naturalPriority={naturalPriority} " +
            $"buttonToRequest={Seconds(buttonAt, requestAt):0.000}s " +
            $"requestTo90={Seconds(requestAt, progress90At):0.000}s " +
            $"progress90ToSceneLoaded={Seconds(progress90At, sceneLoadedAt):0.000}s " +
            $"requestToSceneLoaded={Seconds(requestAt, sceneLoadedAt):0.000}s " +
            $"largestPreLoadFrame={largestPreLoadFrameMs:0.0}ms " +
            $"postLoadLargest={largest:0.0}ms postLoadSecond={secondLargest:0.0}ms " +
            $"postLoadWindow={postWindowMs:0.0}ms totalButtonToPostWindowEnd={Seconds(buttonAt, endAt):0.000}s " +
            $"postLoadSamples={PostLoadFrameMs.Count}");
    }

    private static double Seconds(float from, float to)
    {
        return from >= 0f && to >= 0f ? Math.Max(0.0, to - from) : -1.0;
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
            try { parts[i] = args[i] == null ? "null" : args[i].ToString(); }
            catch { parts[i] = "<unprintable>"; }
        }
        return string.Join(", ", parts);
    }

    private static string DescribeMethod(MethodBase method)
    {
        if (method == null)
        {
            return "<null>";
        }

        var type = method.DeclaringType != null ? method.DeclaringType.FullName : "<no-type>";
        return type + "." + method.Name;
    }

    private static string GetObjectPath(GameObject gameObject)
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

    private static string Trim(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }
        return value.Substring(0, maxLength) + "...";
    }

    private void OnDestroy()
    {
        if (runActive)
        {
            Application.backgroundLoadingPriority = naturalPriority;
        }
        SceneManager.sceneLoaded -= OnSceneLoaded;
        harmony?.UnpatchSelf();
        instance = null;
    }
}
