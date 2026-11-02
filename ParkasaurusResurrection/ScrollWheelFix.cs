using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ParkasaurusResurrection
{
    // Scroll lists in the game jump from top to bottom with a single wheel notch (their scroll
    // sensitivity is set for other mice). Every list now moves a fixed part of its visible height
    // per notch, whatever the list or mouse.
    [HarmonyPatch(typeof(ScrollRect), nameof(ScrollRect.OnScroll))]
    public static class ScrollWheelFix
    {
        // Input System 1.5 hands the UI 120 / 20 = 6 per wheel notch
        internal const float UnitsPerNotch = 6f;

        private static int s_logged;

        public static void Prefix(ScrollRect __instance, PointerEventData data)
        {
            Vector2 raw = data.scrollDelta;
            if (!ModConfig.CalmScrollWheel.Value || raw == Vector2.zero)
                return;
            if (s_logged < 5)
            {
                s_logged++;
                Plugin.Log.LogInfo($"Scroll wheel: raw delta {raw}, list sensitivity {__instance.scrollSensitivity}");
            }

            RectTransform view = __instance.viewport != null ? __instance.viewport : (RectTransform)__instance.transform;
            float sensitivity = Mathf.Abs(__instance.scrollSensitivity) > 0.001f ? __instance.scrollSensitivity : 1f;
            float step = ModConfig.ScrollStep.Value / UnitsPerNotch / sensitivity;
            data.scrollDelta = new Vector2(raw.x * step * view.rect.width, raw.y * step * view.rect.height);
        }
    }
}
