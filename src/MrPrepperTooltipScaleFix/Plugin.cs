using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System;
using System.Collections.Generic;

namespace MrPrepperTooltipScaleFix;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "actepukc.mrprepper.tooltipsalefix";
    public const string PluginName = "Mr. Prepper Tooltip Scale Fix";
    public const string PluginVersion = "0.1.0";

    private static ManualLogSource log;
    private static ConfigEntry<bool> fixEnabled;
    private static ConfigEntry<float> scale;
    private static ConfigEntry<bool> scaleAuxiliaryText;
    private static ConfigEntry<bool> safePositioning;
    private static ConfigEntry<float> mouseGap;
    private static ConfigEntry<float> edgePadding;
    private static ConfigEntry<bool> debugLogging;
    private static Harmony harmony;
    private static readonly Dictionary<int, float> originalFontSizes = new();
    private static readonly Dictionary<int, float> originalFontSizeBases = new();
    private static readonly Dictionary<int, Vector2> originalPivots = new();
    private static float lastScale = -1f;

    private void Awake()
    {
        log = Logger;
        fixEnabled = Config.Bind("Tooltip", "Enabled", true, "Enable the tooltip-only size fix.");
        scale = Config.Bind("Tooltip", "Scale", 1.5f, "Multiplier for TMP font sizes inside TooltipManagerV2.");
        scaleAuxiliaryText = Config.Bind("Tooltip", "ScaleAuxiliaryText", true, "Also scale quantity/category text shown with item tooltips.");
        safePositioning = Config.Bind("Tooltip", "SafePositioning", true, "Keep the tooltip outside the mouse pointer when there is room.");
        mouseGap = Config.Bind("Tooltip", "MouseGap", 24f, "Distance in canvas units between the mouse pointer and the tooltip.");
        edgePadding = Config.Bind("Tooltip", "EdgePadding", 8f, "Minimum distance from the canvas edge.");
        debugLogging = Config.Bind("Diagnostics", "DebugLogging", false, "Log the tooltip objects and font sizes changed by the fix.");

        harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin));
        log.LogInfo($"{PluginName} {PluginVersion} loaded; enabled={fixEnabled.Value}, scale={scale.Value:0.##}");
    }

    private void OnDestroy()
    {
        RestoreAll();
        harmony?.UnpatchSelf();
    }

    [HarmonyPatch(typeof(TooltipManagerV2), nameof(TooltipManagerV2.Show))]
    [HarmonyPostfix]
    private static void TooltipShown(TooltipManagerV2 __instance)
    {
        if (!fixEnabled.Value || __instance == null || __instance.tooltipTrans == null)
        {
            return;
        }

        var multiplier = Math.Max(0.1f, scale.Value);
        if (Math.Abs(multiplier - lastScale) > 0.001f)
        {
            RestoreAll();
            lastScale = multiplier;
        }

        var texts = __instance.tooltipTrans.GetComponentsInChildren<TMP_Text>(true);
        foreach (var text in texts)
        {
            if (text == null || (!scaleAuxiliaryText.Value && text != __instance.text))
            {
                continue;
            }

            var id = text.GetInstanceID();
            if (!originalFontSizes.ContainsKey(id))
            {
                originalFontSizes[id] = text.fontSize;
            originalFontSizeBases[id] = text.fontSize;
            }

            text.fontSize = originalFontSizes[id] * multiplier;
            text.SetVerticesDirty();
            text.SetLayoutDirty();

            if (debugLogging.Value)
            {
                log.LogInfo($"Scaled tooltip text: object='{text.gameObject.name}', size={text.fontSize:0.##}, text='{Trim(text.text)}'");
            }
        }

        UnityEngine.Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.tooltipTrans);
        if (safePositioning.Value)
        {
            RepositionTooltip(__instance, Input.mousePosition, true);
        }
    }

    // TooltipManagerV2 can recalculate its position after Show() (and does so
    // from its own Update/coroutine). Reapply our position after the original
    // update so the game cannot move the popup back under the mouse.
    [HarmonyPatch(typeof(TooltipManagerV2), "Update")]
    [HarmonyPostfix]
    private static void TooltipUpdated(TooltipManagerV2 __instance)
    {
        if (!fixEnabled.Value || !safePositioning.Value || __instance == null ||
            __instance.tooltipTrans == null || !__instance.tooltipTrans.gameObject.activeInHierarchy)
        {
            return;
        }

        RepositionTooltip(__instance, Input.mousePosition, false);
    }

    // FollowItemPosition also calls this private method directly. Returning
    // our calculated position here prevents that coroutine from restoring the
    // original, mouse-centred placement after Update has finished.
    [HarmonyPatch(typeof(TooltipManagerV2), "CalculateTooltipPosition")]
    [HarmonyPostfix]
    private static void TooltipPositionCalculated(TooltipManagerV2 __instance, ref Vector2 __result)
    {
        if (!fixEnabled.Value || !safePositioning.Value || __instance == null ||
            __instance.tooltipTrans == null || !__instance.tooltipTrans.gameObject.activeInHierarchy)
        {
            return;
        }

        RepositionTooltip(__instance, Input.mousePosition, false);
        __result = __instance.tooltipTrans.anchoredPosition;
    }

    private static void RepositionTooltip(TooltipManagerV2 manager, Vector3 screenPosition, bool writeLog)
    {
        var canvas = manager.GetComponent<Canvas>();
        var canvasRt = canvas == null ? null : canvas.GetComponent<RectTransform>();
        var coordinateRt = manager.tooltipTrans.parent as RectTransform ?? canvasRt;
        if (canvasRt == null || coordinateRt == null || manager.tooltipTrans == null)
        {
            return;
        }

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(coordinateRt, screenPosition, canvas.worldCamera, out var localMouse))
        {
            return;
        }

        // TooltipManagerV2's original code converts the local canvas point to a
        // bottom-left coordinate before assigning anchoredPosition. Keep that
        // convention; using rect.xMin here makes the popup drift toward center.
        var canvasSize = coordinateRt.rect.size;
        if (canvasSize.x <= 0f || canvasSize.y <= 0f)
        {
            canvasSize = coordinateRt.sizeDelta;
        }

        var mouse = localMouse - coordinateRt.rect.min;
        var tooltipSize = manager.tooltipTrans.rect.size;
        var gap = Mathf.Max(1f, mouseGap.Value);
        var padding = Mathf.Max(0f, edgePadding.Value);
        var right = canvasSize.x - mouse.x - tooltipSize.x - gap >= padding;
        var left = mouse.x - tooltipSize.x - gap >= padding;
        var above = canvasSize.y - mouse.y - tooltipSize.y - gap >= padding;
        var below = mouse.y - tooltipSize.y - gap >= padding;

        Vector2 pivot;
        Vector2 position;
        if (right && above)
        {
            pivot = new Vector2(0f, 0f);
            position = mouse + new Vector2(gap, gap);
        }
        else if (left && above)
        {
            pivot = new Vector2(1f, 0f);
            position = mouse + new Vector2(-gap, gap);
        }
        else if (right && below)
        {
            pivot = new Vector2(0f, 1f);
            position = mouse + new Vector2(gap, -gap);
        }
        else if (left && below)
        {
            pivot = new Vector2(1f, 1f);
            position = mouse + new Vector2(-gap, -gap);
        }
        else
        {
            // If no complete quadrant is available, keep the mouse at the
            // nearest side/corner and clamp the popup to the canvas.
            var useRight = canvasSize.x - mouse.x >= mouse.x;
            var useAbove = canvasSize.y - mouse.y >= mouse.y;
            pivot = new Vector2(useRight ? 0f : 1f, useAbove ? 0f : 1f);
            position = mouse + new Vector2(useRight ? gap : -gap, useAbove ? gap : -gap);
        }

        var id = manager.tooltipTrans.GetInstanceID();
        if (!originalPivots.ContainsKey(id))
        {
            originalPivots[id] = manager.tooltipTrans.pivot;
        }

        manager.tooltipTrans.pivot = pivot;
        var minX = padding + tooltipSize.x * pivot.x;
        var maxX = canvasSize.x - padding - tooltipSize.x * (1f - pivot.x);
        var minY = padding + tooltipSize.y * pivot.y;
        var maxY = canvasSize.y - padding - tooltipSize.y * (1f - pivot.y);
        position.x = Mathf.Clamp(position.x, minX, maxX);
        position.y = Mathf.Clamp(position.y, minY, maxY);
        manager.tooltipTrans.anchoredPosition = position;

        if (debugLogging.Value && writeLog)
        {
            var quadrant = $"pivot=({pivot.x:0},{pivot.y:0})";
            log.LogInfo($"Tooltip position: coordinate='{coordinateRt.name}', canvas={canvasSize.x:0.##}x{canvasSize.y:0.##}, mouse=({mouse.x:0.##},{mouse.y:0.##}), tooltip={tooltipSize.x:0.##}x{tooltipSize.y:0.##}, fits=R{right}/L{left}/A{above}/B{below}, {quadrant}, anchored=({position.x:0.##},{position.y:0.##})");
        }
    }

    private static void RestoreAll()
    {
        if (originalFontSizes.Count == 0)
        {
            return;
        }

        var texts = UnityEngine.Object.FindObjectsOfType<TMP_Text>();
        foreach (var text in texts)
        {
            if (text == null)
            {
                continue;
            }

            var id = text.GetInstanceID();
            if (originalFontSizes.TryGetValue(id, out var originalSize))
            {
                text.fontSize = originalSize;
                text.SetVerticesDirty();
                text.SetLayoutDirty();
            }
        }

        originalFontSizes.Clear();
        originalFontSizeBases.Clear();
        foreach (var pair in originalPivots)
        {
            var transforms = UnityEngine.Object.FindObjectsOfType<RectTransform>();
            foreach (var transform in transforms)
            {
                if (transform != null && transform.GetInstanceID() == pair.Key)
                {
                    transform.pivot = pair.Value;
                    break;
                }
            }
        }
        originalPivots.Clear();
    }

    private static string Trim(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        value = value.Replace("\r", "\\r").Replace("\n", "\\n");
        return value.Length <= 100 ? value : value.Substring(0, 100) + "...";
    }
}
