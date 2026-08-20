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
    public const string PluginVersion = "0.1.0";

    private const string TargetScene = "LoadingScreen";
    private const string TargetObject = "RGintro";
    private const string TargetClip = "RGintro";

    private void Awake()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryDisableStartupVideo("plugin-awake");
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded. targetScene='{TargetScene}' targetObject='{TargetObject}' targetClip='{TargetClip}'");
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!string.Equals(scene.name, TargetScene, StringComparison.Ordinal))
            return;

        TryDisableStartupVideo("scene-loaded");
    }

    private void TryDisableStartupVideo(string reason)
    {
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

            player.playOnAwake = false;
            if (player.isPlaying || player.isPrepared)
                player.Stop();
            player.enabled = false;
            player.gameObject.SetActive(false);

            Logger.LogInfo($"[SKIP STARTUP VIDEO] disabled scene='{scene.name}' object='{player.gameObject.name}' clip='{clipName}' reason='{reason}'");
            return;
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }
}
