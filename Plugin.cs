using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Rewired.UI.ControlMapper;
using UnityEngine;
using UnityEngine.UI;

namespace KeybindsUltrawideFix
{
    /// <summary>
    /// The keybinds screen is Rewired's stock Control Mapper prefab, which has
    /// its own Canvas separate from the game's menus. Its CanvasScalerFitter
    /// picks a reference resolution from a preset list of aspect-ratio "break
    /// points" that tops out at 16:9, so on an ultrawide monitor the closest
    /// (16:9) preset is applied and the width-driven CanvasScaler blows the UI
    /// up until most of it is off-screen.
    ///
    /// Fix: after the fitter runs, if the real aspect ratio is wider than the
    /// widest break point, widen the reference resolution to match the real
    /// aspect (same height, so elements keep the size they'd have on a 16:9
    /// monitor of the same height). Optionally the main window is then capped
    /// at its designed 16:9 width and centred, so the menu looks exactly like
    /// it does on a normal monitor instead of stretching edge to edge.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.keybindsultrawidefix";
        public const string PluginName = "Keybinds Ultrawide Fix";
        public const string PluginVersion = "0.1.0";

        private const float AspectTolerance = 0.01f;

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> CenterContent;

        // CanvasScalerFitter privates.
        private static readonly FieldInfo FitterScalerField =
            AccessTools.Field(typeof(CanvasScalerFitter), "canvasScaler");
        private static readonly FieldInfo FitterBreakPointsField =
            AccessTools.Field(typeof(CanvasScalerFitter), "breakPoints");
        private static readonly Type BreakPointType =
            AccessTools.Inner(typeof(CanvasScalerFitter), "BreakPoint");
        private static readonly FieldInfo BreakPointAspectField =
            BreakPointType == null ? null : AccessTools.Field(BreakPointType, "screenAspectRatio");
        private static readonly FieldInfo BreakPointResolutionField =
            BreakPointType == null ? null : AccessTools.Field(BreakPointType, "referenceResolution");

        // ControlMapper privates.
        private static readonly FieldInfo MapperCanvasField =
            AccessTools.Field(typeof(ControlMapper), "canvas");
        private static readonly FieldInfo MapperReferencesField =
            AccessTools.Field(typeof(ControlMapper), "references");
        private static readonly PropertyInfo ReferencesMainContentProperty =
            MapperReferencesField == null
                ? null
                : AccessTools.Property(MapperReferencesField.FieldType, "mainContent");

        // Original horizontal offsets of the main content rect, keyed by
        // instance ID, so centring can be recomputed or undone at any time.
        private static readonly Dictionary<int, Vector2> OriginalOffsets = new Dictionary<int, Vector2>();
        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();

        private void Awake()
        {
            Log = Logger;

            CenterContent = Config.Bind("Layout", "CenterContent", true,
                "Keep the keybinds window at the width it was designed for (16:9) and centre it, " +
                "like on a regular monitor. Off = let the window stretch across the full screen width.");

            var harmony = new Harmony(PluginGuid);
            PatchMethod(harmony, typeof(CanvasScalerFitter), "UpdateSize", null, nameof(UpdateSizePostfix));
            PatchMethod(harmony, typeof(ControlMapper), "Open", new[] { typeof(bool) }, nameof(OpenPostfix));

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void PatchMethod(Harmony harmony, Type type, string methodName, Type[] parameters, string postfixName)
        {
            MethodInfo target = parameters == null
                ? AccessTools.Method(type, methodName)
                : AccessTools.Method(type, methodName, parameters);
            if (target == null)
            {
                Log.LogError($"{type.Name}.{methodName} not found — the keybinds menu fix may not apply.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), postfixName)));
            Log.LogInfo($"Patched {type.Name}.{methodName}");
        }

        // Runs whenever Rewired re-evaluates its aspect break points (menu
        // enable, screen size change).
        private static void UpdateSizePostfix(CanvasScalerFitter __instance)
        {
            try
            {
                ApplyFix(__instance, FindMapperAbove(__instance));
            }
            catch (Exception e)
            {
                Log.LogError($"UpdateSize postfix failed: {e}");
            }
        }

        // Safety net for the case where the game's prefab has no
        // CanvasScalerFitter at all, plus the trigger for centring.
        private static void OpenPostfix(ControlMapper __instance)
        {
            try
            {
                var canvasGo = MapperCanvasField?.GetValue(__instance) as GameObject;
                if (canvasGo == null) return;

                var fitter = canvasGo.GetComponentInChildren<CanvasScalerFitter>(true);
                if (fitter != null)
                {
                    ApplyFix(fitter, __instance);
                }
                else
                {
                    var scaler = canvasGo.GetComponentInChildren<CanvasScaler>(true);
                    ApplyFix(scaler, 16f / 9f, scaler != null ? scaler.referenceResolution.y : 0f, __instance);
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Open postfix failed: {e}");
            }
        }

        private static void ApplyFix(CanvasScalerFitter fitter, ControlMapper mapper)
        {
            if (fitter == null) return;

            var scaler = FitterScalerField?.GetValue(fitter) as CanvasScaler;
            if (scaler == null) scaler = fitter.GetComponent<CanvasScaler>();

            float baseAspect, baseHeight;
            if (!TryGetWidestBreakPoint(fitter, out baseAspect, out baseHeight))
            {
                baseAspect = 16f / 9f;
                baseHeight = scaler != null ? scaler.referenceResolution.y : 0f;
            }

            ApplyFix(scaler, baseAspect, baseHeight, mapper);
        }

        private static void ApplyFix(CanvasScaler scaler, float baseAspect, float baseHeight, ControlMapper mapper)
        {
            if (scaler == null)
            {
                WarnOnce("no-scaler", "No CanvasScaler found on the keybinds menu canvas — cannot fix scaling.");
                return;
            }
            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize || baseHeight <= 0f)
                return;

            float aspect = (float)Screen.width / Screen.height;
            if (aspect <= baseAspect + AspectTolerance)
            {
                // Normal monitor (or window resized back): stock behaviour,
                // undo any centring we did earlier.
                RestoreContent(mapper);
                return;
            }

            var wideRef = new Vector2(baseHeight * aspect, baseHeight);
            if ((scaler.referenceResolution - wideRef).sqrMagnitude > 0.25f)
            {
                scaler.referenceResolution = wideRef;
                Log.LogInfo($"Screen {Screen.width}x{Screen.height} (aspect {aspect:F2}) is wider than the " +
                            $"widest Rewired break point ({baseAspect:F2}) — reference resolution set to " +
                            $"{wideRef.x:F0}x{wideRef.y:F0}.");
            }

            if (CenterContent.Value)
                CenterMainContent(mapper, canvasWidth: baseHeight * aspect, targetWidth: baseHeight * baseAspect);
            else
                RestoreContent(mapper);
        }

        private static bool TryGetWidestBreakPoint(CanvasScalerFitter fitter, out float aspect, out float height)
        {
            aspect = 0f;
            height = 0f;
            if (FitterBreakPointsField == null || BreakPointAspectField == null || BreakPointResolutionField == null)
                return false;

            var breakPoints = FitterBreakPointsField.GetValue(fitter) as Array;
            if (breakPoints == null || breakPoints.Length == 0) return false;

            bool found = false;
            foreach (object bp in breakPoints)
            {
                if (bp == null) continue;
                float bpAspect = (float)BreakPointAspectField.GetValue(bp);
                if (bpAspect <= aspect && found) continue;
                aspect = bpAspect;
                height = ((Vector2)BreakPointResolutionField.GetValue(bp)).y;
                found = true;
            }
            return found && height > 0f;
        }

        private static void CenterMainContent(ControlMapper mapper, float canvasWidth, float targetWidth)
        {
            RectTransform content = GetMainContent(mapper);
            if (content == null)
            {
                WarnOnce("no-content", "Main content rect not found — menu will stretch full width instead of centring.");
                return;
            }

            // The stock prefab stretches the window across the whole canvas.
            // If the game changed that, insetting would be wrong, so skip.
            if (Mathf.Abs(content.anchorMax.x - content.anchorMin.x - 1f) > 0.001f)
            {
                WarnOnce("odd-anchors", "Main content rect is not full-width anchored — leaving its layout alone.");
                return;
            }

            int id = content.GetInstanceID();
            if (!OriginalOffsets.TryGetValue(id, out Vector2 original))
            {
                original = new Vector2(content.offsetMin.x, content.offsetMax.x);
                OriginalOffsets[id] = original;
            }

            float inset = Mathf.Max(0f, (canvasWidth - targetWidth) * 0.5f);
            content.offsetMin = new Vector2(original.x + inset, content.offsetMin.y);
            content.offsetMax = new Vector2(original.y - inset, content.offsetMax.y);
        }

        private static void RestoreContent(ControlMapper mapper)
        {
            RectTransform content = GetMainContent(mapper);
            if (content == null) return;
            if (!OriginalOffsets.TryGetValue(content.GetInstanceID(), out Vector2 original)) return;

            content.offsetMin = new Vector2(original.x, content.offsetMin.y);
            content.offsetMax = new Vector2(original.y, content.offsetMax.y);
        }

        private static RectTransform GetMainContent(ControlMapper mapper)
        {
            if (mapper == null || MapperReferencesField == null || ReferencesMainContentProperty == null)
                return null;

            object references = MapperReferencesField.GetValue(mapper);
            if (references == null) return null;
            return ReferencesMainContentProperty.GetValue(references, null) as RectTransform;
        }

        private static ControlMapper FindMapperAbove(Component component)
        {
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                var mapper = t.GetComponent<ControlMapper>();
                if (mapper != null) return mapper;
            }
            return null;
        }

        private static void WarnOnce(string key, string message)
        {
            if (!WarnedOnce.Add(key)) return;
            Log.LogWarning(message);
        }
    }
}
