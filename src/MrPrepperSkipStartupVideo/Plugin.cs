using BepInEx;
using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

namespace MrPrepperSkipStartupVideo;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.skipstartupvideo";
    public const string PluginName = "Mr. Prepper Skip Startup Video";
    public const string PluginVersion = "0.2.0";

    private const string TargetScene = "LoadingScreen";
    private const string TargetObject = "RGintro";
    private const string TargetClip = "RGintro";
    private const double TailSeconds = 0.05;

    private bool handled;

    private void Awake()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryFastForwardStartupVideo("plugin-awake");
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. targetScene='{TargetScene}' targetObject='{TargetObject}' targetClip='{TargetClip}' mode=fast-forward-tail");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!string.Equals(scene.name, TargetScene, StringComparison.Ordinal))
            return;

        TryFastForwardStartupVideo("scene-loaded");
    }

    private void TryFastForwardStartupVideo(string reason)
    {
        if (handled)
            return;

        var players = Resources.FindObjectsOfTypeAll<VideoPlayer>();
        foreach (var player in players)
        {
            if (player == null || player.gameObject == null)
                continue;

            var scene = player.gameObject.scene;
            if (!scene.IsValid() || !string.Equals(scene.name, TargetScene, StringComparison.Ordinal))
                continue;

            if (!string.Equals(player.gameObject.name, TargetObject, StringComparison.Ordinal))
                continue;

            var clipName = player.clip != null ? player.clip.name : null;
            if (!string.Equals(clipName, TargetClip, StringComparison.Ordinal))
                continue;

            // Keep the GameObject and VideoPlayer alive so the game's own completion
            // callback / transition logic can still run. We only jump to the tail.
            player.playOnAwake = false;
            player.enabled = true;
            player.gameObject.SetActive(true);

            var length = player.clip != null ? player.clip.length : player.length;
            if (length > TailSeconds)
            {
                player.time = Math.Max(0.0, length - TailSeconds);
            }

            player.Play();
            handled = true;

            Logger.LogInfo($"[SKIP STARTUP VIDEO] fast-forwarded scene='{scene.name}' object='{player.gameObject.name}' clip='{clipName}' length={length:0.000}s time={player.time:0.000}s reason='{reason}'");
            return;
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }
}
