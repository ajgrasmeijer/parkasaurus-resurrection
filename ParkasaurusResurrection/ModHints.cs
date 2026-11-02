using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ParkasaurusResurrection
{
    // Hints for the mod's controls, in the bottom right corner where the game shows its own building
    // tips ("Press R to rotate", PATH_TIP, ...). The box is a copy of the game's own one
    // (UICore.m_buildNotification, CoreUI/MainUI/ErrorNotification); when the game's box is showing,
    // ours sits right above it.
    public static class ModHints
    {
        private const string KeyColor = "<color=#8DC63FFF>";
        private static string Key(string key) => KeyColor + key + "</color>";

        private static readonly AccessTools.FieldRef<UICore, Place> HoldingPlace =
            AccessTools.FieldRefAccess<UICore, Place>("m_holdingPlace");

        private static GameObject s_box;
        private static TMP_Text s_tmp;
        private static Text s_text;
        private static bool s_failed;
        private static string s_shown;
        private static readonly List<string> Lines = new List<string>();

        // Panels with a hat slot, looked up once per scene
        private static DinoPaneUI[] s_dinoPanes;
        private static EggUI[] s_eggPanes;
        private static EmployeePaneUI[] s_staffPanes;

        [HarmonyPatch(typeof(UICore), nameof(UICore.LateUpdate))]
        public static class Show
        {
            public static void Postfix(UICore __instance)
            {
                if (s_failed)
                    return;
                UICore ui = __instance;
                Lines.Clear();
                if (ModConfig.ShowHints.Value && !Global.Get.m_controllerMode)
                    Collect(ui);

                if (Lines.Count == 0)
                {
                    if (s_box != null && s_box.activeSelf)
                        s_box.SetActive(false);
                    return;
                }
                if (s_box == null && !Create(ui))
                {
                    s_failed = true;
                    return;
                }
                string text = string.Join("\n", Lines);
                if (text != s_shown)
                {
                    s_shown = text;
                    if (s_tmp != null) s_tmp.text = text;
                    if (s_text != null) s_text.text = text;
                }
                Position(ui);
                if (!s_box.activeSelf)
                    s_box.SetActive(true);
            }
        }

        private static void Collect(UICore ui)
        {
            Place place = HoldingPlace(ui);
            if (place != null && ModConfig.DiagonalBuilding.Value &&
                (place.m_placeType == Place.PlaceType.Connector || place.m_placeType == Place.PlaceType.Path))
                Lines.Add($"Drag at an angle for a staircase, hold {Key("Ctrl")} to flip it");

            if (place != null && ModConfig.ShiftPaintsOneArea.Value && place.m_placeType == Place.PlaceType.Pool
                && TerrainPaintPatch.IsTerrain(place.PathType))
                Lines.Add($"Hold {Key("Shift")} to paint inside one exhibit only");

            if (MoveToolPatch.Active)
                Lines.Add($"Click an object to move it, {Key("Right-click")} or {Key("Esc")} to stop");

            if (ui.m_deleting && ui.m_deletingPool == 0 && ModConfig.RemoveItemArea.Value)
                Lines.Add($"Drag over empty ground to mark an area ({Key("Shift")}: start on an object, {Key("Alt")}: unmark)");

            if (place != null && ModConfig.TypeNumbers.Value && ToolUI.Get != null && ToolUI.Get.m_slider != null
                && ToolUI.Get.m_slider.gameObject.activeInHierarchy)
                Lines.Add("Click the variant number to type one");

            if (ModConfig.ShiftEmptiesTrash.Value && ui.m_inventory != null && ui.m_inventory.gameObject.activeInHierarchy)
                Lines.Add($"{Key("Shift")} + click the trash to delete everything");

            if (ModConfig.RightClickUnequip.Value && HatPanelOpen())
                Lines.Add($"{Key("Right-click")} a hat or egg item to put it back");
        }

        private static bool HatPanelOpen()
        {
            if (s_dinoPanes == null || s_dinoPanes.Any(p => p == null))
            {
                s_dinoPanes = Resources.FindObjectsOfTypeAll<DinoPaneUI>().Where(p => p.gameObject.scene.IsValid()).ToArray();
                s_eggPanes = Resources.FindObjectsOfTypeAll<EggUI>().Where(p => p.gameObject.scene.IsValid()).ToArray();
                s_staffPanes = Resources.FindObjectsOfTypeAll<EmployeePaneUI>().Where(p => p.gameObject.scene.IsValid()).ToArray();
            }
            return s_dinoPanes.Any(p => p != null && p.isActiveAndEnabled && p.m_focused != null
                                        && p.m_dinoInfoPane != null && p.m_dinoInfoPane.isActiveAndEnabled)
                   || s_eggPanes.Any(p => p != null && p.isActiveAndEnabled && p.m_focused != null)
                   || s_staffPanes.Any(p => p != null && p.isActiveAndEnabled);
        }

        // Called when a park is closed: the panels belong to the old scene
        internal static void Reset()
        {
            s_dinoPanes = null;
            s_box = null;
            s_shown = null;
            s_failed = false;
        }

        private static bool Create(UICore ui)
        {
            GameObject template = ui.m_buildNotification;
            if (template == null)
            {
                Plugin.Log.LogWarning("Hints: the game's tip box (m_buildNotification) wasn't found, no hints shown");
                return false;
            }
            s_box = Object.Instantiate(template, template.transform.parent);
            s_box.name = "ModHint";
            s_box.SetActive(false);
            // A localization script would put the game's own text back
            foreach (MonoBehaviour mb in s_box.GetComponentsInChildren<MonoBehaviour>(true)
                         .Where(m => m != null && m.GetType().Name.Contains("Localize")))
                Object.Destroy(mb);
            Transform close = s_box.transform.Find("CloseButton");
            if (close != null)
                close.gameObject.SetActive(false);

            // The game's text field is "Number" (the tip text); make it fit up to two lines
            Transform number = s_box.transform.Find("Number");
            s_tmp = number != null ? number.GetComponent<TMP_Text>() : s_box.GetComponentInChildren<TMP_Text>(true);
            s_text = s_tmp == null ? (number != null ? number.GetComponent<Text>() : s_box.GetComponentInChildren<Text>(true)) : null;
            if (s_tmp != null)
            {
                s_tmp.enableAutoSizing = true;
                s_tmp.fontSizeMin = 6f;
                s_tmp.fontSizeMax = s_tmp.fontSize;
                s_tmp.enableWordWrapping = true;
                s_tmp.raycastTarget = false;
            }
            if (s_text != null)
            {
                s_text.resizeTextForBestFit = true;
                s_text.resizeTextMinSize = 6;
                s_text.resizeTextMaxSize = s_text.fontSize;
                s_text.raycastTarget = false;
            }
            foreach (Graphic g in s_box.GetComponentsInChildren<Graphic>(true))
                g.raycastTarget = false;

            var rt = (RectTransform)s_box.transform;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x * 1.5f, rt.sizeDelta.y);
            return true;
        }

        // Right-aligned with the game's box; above it while that one is showing
        private static void Position(UICore ui)
        {
            var game = (RectTransform)ui.m_buildNotification.transform;
            var mine = (RectTransform)s_box.transform;
            float dx = (mine.sizeDelta.x - game.sizeDelta.x) * 0.5f;
            // The visible part of the box is its "Main" background, not the whole rectangle
            var background = game.Find("Main") as RectTransform;
            float height = background != null ? background.rect.height : game.sizeDelta.y * 0.5f;
            float dy = ui.m_buildNotification.activeSelf ? height + 4f : 0f;
            mine.anchoredPosition = game.anchoredPosition + new Vector2(-dx, dy);
        }
    }
}
