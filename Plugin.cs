using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
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
    /// The game's break points are width-matched (matchWidthOrHeight = 0)
    /// with a nominal height of 600 that is never used: the canvas actually
    /// ends up refWidth/aspect units tall (~1055-1060 at the widest presets),
    /// and that is the height the menu content is laid out for. On wider
    /// aspects than the presets cover, the canvas gets shorter than that and
    /// the bottom of the menu falls off screen.
    ///
    /// Fix: after the fitter runs, if the real aspect ratio is wider than the
    /// widest break point, set the reference resolution to (designHeight *
    /// aspect, designHeight) so the canvas keeps its designed unit height and
    /// every element keeps its designed size. Optionally the main window is
    /// then capped at the widest break point's designed width and centred, so
    /// the menu looks like it does on a normal monitor instead of stretching
    /// edge to edge.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "local.keybindsultrawidefix";
        public const string PluginName = "Keybinds Ultrawide Fix";
        public const string PluginVersion = "0.1.2";

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

        private static Plugin Instance;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

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
                    ApplyFix(scaler, 16f / 9f, scaler != null ? scaler.referenceResolution.x : 0f, __instance);
                }

                if (Instance != null)
                    Instance.StartCoroutine(DumpRoutine(__instance));
            }
            catch (Exception e)
            {
                Log.LogError($"Open postfix failed: {e}");
            }
        }

        private static IEnumerator DumpRoutine(ControlMapper mapper)
        {
            DumpLayout(mapper, "on open");
            yield return new WaitForSecondsRealtime(1f);
            DumpLayout(mapper, "1s later");
        }

        // Temporary diagnostics: logs everything relevant about the menu's
        // canvas so scaling problems can be diagnosed from LogOutput.log.
        private static void DumpLayout(ControlMapper mapper, string tag)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"--- layout dump ({tag}) — screen {Screen.width}x{Screen.height} ---");

                var canvasGo = MapperCanvasField?.GetValue(mapper) as GameObject;
                if (canvasGo == null)
                {
                    Log.LogInfo(sb.Append("no canvas GameObject").ToString());
                    return;
                }

                var canvas = canvasGo.GetComponentInChildren<Canvas>(true);
                if (canvas != null)
                {
                    var crt = (RectTransform)canvas.transform;
                    sb.AppendLine($"canvas '{canvas.name}' renderMode={canvas.renderMode} " +
                                  $"isRootCanvas={canvas.isRootCanvas} scaleFactor={canvas.scaleFactor:F3} " +
                                  $"rect={crt.rect.width:F0}x{crt.rect.height:F0} lossyScale={crt.lossyScale.x:F3}");
                }
                else
                {
                    sb.AppendLine("no Canvas component found");
                }

                for (Transform t = canvasGo.transform.parent; t != null; t = t.parent)
                {
                    var parentCanvas = t.GetComponent<Canvas>();
                    if (parentCanvas != null)
                        sb.AppendLine($"PARENT canvas '{t.name}' renderMode={parentCanvas.renderMode} " +
                                      $"scaleFactor={parentCanvas.scaleFactor:F3}");
                    var parentScaler = t.GetComponent<CanvasScaler>();
                    if (parentScaler != null)
                        sb.AppendLine($"PARENT scaler '{t.name}' mode={parentScaler.uiScaleMode} " +
                                      $"ref={parentScaler.referenceResolution}");
                }

                foreach (var scaler in canvasGo.GetComponentsInChildren<CanvasScaler>(true))
                    sb.AppendLine($"scaler '{scaler.name}' enabled={scaler.enabled} mode={scaler.uiScaleMode} " +
                                  $"match={scaler.screenMatchMode}/{scaler.matchWidthOrHeight:F2} " +
                                  $"ref={scaler.referenceResolution}");

                var fitter = canvasGo.GetComponentInChildren<CanvasScalerFitter>(true);
                if (fitter != null && FitterBreakPointsField != null &&
                    FitterBreakPointsField.GetValue(fitter) is Array breakPoints)
                {
                    foreach (object bp in breakPoints)
                    {
                        if (bp == null) continue;
                        sb.AppendLine($"breakpoint aspect={(float)BreakPointAspectField.GetValue(bp):F3} " +
                                      $"ref={(Vector2)BreakPointResolutionField.GetValue(bp)}");
                    }
                }

                RectTransform content = GetMainContent(mapper);
                if (content != null)
                    sb.AppendLine($"mainContent '{content.name}' anchors=({content.anchorMin.x:F2},{content.anchorMin.y:F2})-" +
                                  $"({content.anchorMax.x:F2},{content.anchorMax.y:F2}) " +
                                  $"offsets=({content.offsetMin.x:F0},{content.offsetMin.y:F0})-" +
                                  $"({content.offsetMax.x:F0},{content.offsetMax.y:F0}) " +
                                  $"rect={content.rect.width:F0}x{content.rect.height:F0}");

                foreach (Transform child in canvasGo.transform)
                {
                    if (!(child is RectTransform rt)) continue;
                    sb.AppendLine($"child '{child.name}' active={child.gameObject.activeSelf} " +
                                  $"rect={rt.rect.width:F0}x{rt.rect.height:F0} " +
                                  $"anchors=({rt.anchorMin.x:F2},{rt.anchorMin.y:F2})-({rt.anchorMax.x:F2},{rt.anchorMax.y:F2})");
                }

                Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Log.LogError($"Layout dump failed: {e}");
            }
        }

        private static void ApplyFix(CanvasScalerFitter fitter, ControlMapper mapper)
        {
            if (fitter == null) return;

            var scaler = FitterScalerField?.GetValue(fitter) as CanvasScaler;
            if (scaler == null) scaler = fitter.GetComponent<CanvasScaler>();

            float baseAspect, designWidth;
            if (!TryGetWidestBreakPoint(fitter, out baseAspect, out designWidth))
            {
                baseAspect = 16f / 9f;
                designWidth = scaler != null ? scaler.referenceResolution.x : 0f;
            }

            ApplyFix(scaler, baseAspect, designWidth, mapper);
        }

        private static void ApplyFix(CanvasScaler scaler, float baseAspect, float designWidth, ControlMapper mapper)
        {
            if (scaler == null)
            {
                WarnOnce("no-scaler", "No CanvasScaler found on the keybinds menu canvas — cannot fix scaling.");
                return;
            }
            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize ||
                baseAspect <= 0f || designWidth <= 0f)
                return;

            float aspect = (float)Screen.width / Screen.height;
            if (aspect <= baseAspect + AspectTolerance)
            {
                // Normal monitor (or window resized back): stock behaviour,
                // undo any centring we did earlier.
                RestoreContent(mapper);
                return;
            }

            // The game's break points are width-matched: the reference HEIGHT
            // (600) is ignored and the canvas ends up refWidth/aspect units
            // tall — ~1055 at the widest break point. That height, not the
            // nominal 600, is what the menu was laid out for, so preserve it
            // and widen the reference to the real aspect ratio.
            float designHeight = designWidth / baseAspect;
            var wideRef = new Vector2(designHeight * aspect, designHeight);
            if ((scaler.referenceResolution - wideRef).sqrMagnitude > 0.25f)
            {
                scaler.referenceResolution = wideRef;
                Log.LogInfo($"Screen {Screen.width}x{Screen.height} (aspect {aspect:F2}) is wider than the " +
                            $"widest Rewired break point ({baseAspect:F2}, design {designWidth:F0}x{designHeight:F0}) " +
                            $"— reference resolution set to {wideRef.x:F0}x{wideRef.y:F0}.");
            }

            if (CenterContent.Value)
                CenterMainContent(mapper, canvasWidth: designHeight * aspect, targetWidth: designWidth);
            else
                RestoreContent(mapper);
        }

        private static bool TryGetWidestBreakPoint(CanvasScalerFitter fitter, out float aspect, out float width)
        {
            aspect = 0f;
            width = 0f;
            if (FitterBreakPointsField == null || BreakPointAspectField == null || BreakPointResolutionField == null)
                return false;

            var breakPoints = FitterBreakPointsField.GetValue(fitter) as Array;
            if (breakPoints == null || breakPoints.Length == 0) return false;

            bool found = false;
            foreach (object bp in breakPoints)
            {
                if (bp == null) continue;
                float bpAspect = (float)BreakPointAspectField.GetValue(bp);
                if (bpAspect <= 0f || (found && bpAspect <= aspect)) continue;
                aspect = bpAspect;
                width = ((Vector2)BreakPointResolutionField.GetValue(bp)).x;
                found = true;
            }
            return found && width > 0f;
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
