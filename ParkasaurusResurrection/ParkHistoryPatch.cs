using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ParkasaurusResurrection
{
    // Park Info history: the overview pane of the office computer (ComputerUI) gets a bar to show
    // one of the last 6 days and to compare it with another day. The stat blocks (DataBlockUI), the
    // science list, park appeal, stars and review count, and the income list (LiveIncomePatch) then
    // show that day, with "(+N)" / "(-N)" behind each value when comparing. What the game doesn't
    // keep per day is recorded by the mod (ParkHistoryData).
    //
    // The game already stores each day's counters (ParkFile.m_metricAll, one Metric per day, filled
    // at the end-of-day summary) but stops adding after 100 days; that list is turned into a
    // rolling one so recent days are always there. The save format stays the same.
    public static class ParkHistoryPatch
    {
        private const int MaxHistory = 100;   // the game's own limit
        internal const int MaxDaysBack = 6;

        private static int s_view;          // 0 = today, 1 = yesterday, ...
        private static int s_compare = -1;  // -1 = no comparison

        // --- data ------------------------------------------------------------------------------

        // The game stops recording after 100 days; drop the oldest instead so recent days are kept
        [HarmonyPatch(typeof(GameCore), nameof(GameCore.DoStartOfDay))]
        public static class RollingHistory
        {
            public static void Prefix(GameCore __instance)
            {
                List<Metric> all = __instance.m_park?.m_metricAll;
                if (ModConfig.ParkInfoHistory.Value && all != null && all.Count >= MaxHistory)
                    all.RemoveRange(0, all.Count - MaxHistory + 1);
            }
        }

        // Shown day (0 = today) and compared day (-1 = none)
        internal static int View => s_view;
        internal static int Compare => s_compare != s_view ? s_compare : -1;
        // Showing anything else than the game's own "today"
        internal static bool Active => ModConfig.ParkInfoHistory.Value && (s_view != 0 || Compare >= 0);

        // " (+N)" in green, " (-N)" in red, at the given font size
        internal static string Delta(int shown, int other, int size)
        {
            int delta = shown - other;
            string color = delta > 0 ? "#5BD75B" : delta < 0 ? "#FF6B5B" : "#BBBBBB";
            return $" <size={size}><color={color}>({(delta > 0 ? "+" : delta < 0 ? "-" : "±")}{Math.Abs(delta)})</color></size>";
        }

        // Counters of the day 'offset' days ago; null if that day wasn't recorded
        internal static int[] DayData(int offset)
        {
            ParkFile park = GameCore.Get?.m_park;
            if (park?.m_metricAll == null)
                return null;
            int day = park.m_totalDays - offset;
            for (int i = park.m_metricAll.Count - 1; i >= 0; i--)
            {
                Metric m = park.m_metricAll[i];
                if (m.m_day == day && m.m_data != null)
                    return m.m_data;
            }
            return null;
        }

        private static bool TryGetValue(int offset, MetaCountType type, out int value)
        {
            if (offset == 0)
            {
                value = GameCore.Get.GetCounterMeta(type);
                return true;
            }
            int[] data = DayData(offset);
            value = data != null && (int)type < data.Length ? data[(int)type] : 0;
            return data != null;
        }

        private static bool HasDay(int offset) => offset == 0 || DayData(offset) != null;

        private static string DayName(int offset) =>
            offset == 0 ? "Today" : offset == 1 ? "Yesterday" : $"{offset} days ago";

        // --- stat blocks -----------------------------------------------------------------------

        private static readonly Dictionary<int, bool> InComputer = new Dictionary<int, bool>();

        // Called when a park is closed (GameCore.OnDestroy), so nothing is kept for dinos that are gone
        internal static void Reset()
        {
            InComputer.Clear();
        }

        [HarmonyPatch(typeof(DataBlockUI), "Update")]
        public static class StatBlock
        {
            public static void Postfix(DataBlockUI __instance)
            {
                if (!ModConfig.ParkInfoHistory.Value || (s_view == 0 && s_compare < 0) || __instance.m_data == null ||
                    !IsInComputer(__instance))
                    return;

                MetaCountType type = __instance.m_show;
                string text = TryGetValue(s_view, type, out int shown) ? __instance.m_prefix + shown : "–";
                if (Compare >= 0 && TryGetValue(s_view, type, out shown) && TryGetValue(Compare, type, out int other))
                    text += Delta(shown, other, HalfSize(__instance.m_data)); // half size, so it fits next to the value
                __instance.m_data.supportRichText = true;
                __instance.m_data.text = text;
            }
        }

        // Font size of a (best-fit) text
        private static int FontSize(Text text) => text.resizeTextForBestFit ? text.resizeTextMaxSize : text.fontSize;

        // For a difference behind a big number: about half its size
        internal static int HalfSize(Text text) => Mathf.Max(6, Mathf.RoundToInt(FontSize(text) * 0.5f));

        // For a difference behind a name or small number: a bit smaller than the text
        internal static int SmallSize(Text text) => Mathf.Max(6, Mathf.RoundToInt(FontSize(text) * 0.9f));

        // --- science list, appeal, stars, reviews ------------------------------------------------

        // The science income list (Perks, Scientist Research, Talking Scientist; counters 25-27) is
        // built when the pane opens; it's rebuilt for the shown day
        [HarmonyPatch(typeof(ScienceSummaryUI), nameof(ScienceSummaryUI.OnEnable))]
        public static class ScienceList
        {
            public static void Postfix(ScienceSummaryUI __instance)
            {
                if (Active)
                    ShowScience(__instance);
            }
        }

        private static void ShowScience(ScienceSummaryUI ui)
        {
            if (ui == null || ui.m_root == null || GameCore.Get == null)
                return;
            Transform root = ui.m_root.transform;
            for (int i = root.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(root.GetChild(i).gameObject);
            var rows = new List<ProfitUI>();
            for (int i = (int)MetaCountType.SciencePerk; i <= (int)MetaCountType.ScienceTalk; i++)
            {
                var type = (MetaCountType)i;
                TryGetValue(s_view, type, out int value);
                ProfitUI row = ui.AddRow(GameCore.Get.GetCounterName(type), value);
                // The number is drawn by a counter; the difference goes behind the name
                if (Compare >= 0 && TryGetValue(Compare, type, out int other))
                {
                    row.m_name.supportRichText = true;
                    row.m_name.text += Delta(value, other, SmallSize(row.m_name));
                }
                rows.Add(row);
            }
            // Biggest first, as the game sorts them
            rows.Sort((x, y) => x.CompareTo(y));
            foreach (ProfitUI row in rows)
                row.transform.SetAsLastSibling();
        }

        // Park appeal, stars and today's review count are set every frame by ReviewListUI.Update
        [HarmonyPatch(typeof(ReviewListUI), "Update")]
        public static class Reviews
        {
            public static void Postfix(ReviewListUI __instance)
            {
                if (!Active || !IsInComputer(__instance))
                    return;
                ReviewListUI ui = __instance;
                if (ui.m_parkAppeal != null)
                {
                    ui.m_parkAppeal.supportRichText = true;
                    ui.m_parkAppeal.text = Shown(ParkHistoryData.TryGetAppeal, ui.m_parkAppeal);
                }
                // StarUI reads this text as a number, so it gets no difference behind it
                if (ui.m_starAmount != null && ParkHistoryData.TryGetStars(s_view, out int stars))
                    ui.m_starAmount.text = stars.ToString();
                if (ui.m_reviewAmount != null)
                {
                    ui.m_reviewAmount.supportRichText = true;
                    string count = Shown(ParkHistoryData.TryGetReviews, ui.m_reviewAmount);
                    ui.m_reviewAmount.text = count + (s_view == 0 ? GameCore.Translate(" reviews for today") : " reviews that day");
                }
            }

            private delegate bool Getter(int offset, out int value);

            private static string Shown(Getter get, Text text)
            {
                if (!get(s_view, out int shown))
                    return "–";
                string result = shown.ToString();
                if (Compare >= 0 && get(Compare, out int other))
                    result += Delta(shown, other, SmallSize(text));
                return result;
            }
        }

        // Only the blocks in the office computer, not the ones on other screens
        private static bool IsInComputer(Component block)
        {
            int id = block.GetInstanceID();
            if (!InComputer.TryGetValue(id, out bool inside))
            {
                for (Transform t = block.transform; t != null && !inside; t = t.parent)
                    inside = t.GetComponent<ComputerUI>() != null;
                InComputer[id] = inside;
            }
            return inside;
        }


        // --- the bar -----------------------------------------------------------------------------
        // Sits in the tab bar, in the empty space between the last tab (STAFF) and the close
        // button, with the look of the tab labels.

        internal const string DropDownName = "ModCompareDay";
        private const float DropWidth = 80f;

        private static RectTransform s_bar;
        private static TMP_Text s_dayLabel;
        private static Dropdown s_compareDropDown;
        private static bool s_placed;

        [HarmonyPatch(typeof(ComputerUI), nameof(ComputerUI.OnEnable))]
        public static class Bar
        {
            public static void Postfix(ComputerUI __instance)
            {
                if (!ModConfig.ParkInfoHistory.Value)
                    return;
                try
                {
                    if (s_dayLabel == null)
                        Build(__instance);
                    s_placed = false; // the tabs are laid out by the next frame
                    Refresh();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Park Info history bar: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(ComputerUI), "Update")]
        public static class BarPlacement
        {
            public static void Postfix(ComputerUI __instance)
            {
                if (s_placed || s_bar == null || !ModConfig.ParkInfoHistory.Value)
                    return;
                s_placed = true;
                try
                {
                    Place(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Park Info history bar placement: {e}");
                }
            }
        }

        private static void Build(ComputerUI ui)
        {
            Toggle tab = ui.m_toggles?.FirstOrDefault(t => t != null);
            TMP_Text labelStyle = tab != null ? tab.GetComponentInChildren<TMP_Text>(true) : null;
            Dropdown template = Resources.FindObjectsOfTypeAll<OptionsUI>()
                .Select(o => o.m_shadowQuality != null ? o.m_shadowQuality.m_dropDown : null)
                .FirstOrDefault(d => d != null);
            if (labelStyle == null || template == null)
            {
                Plugin.Log.LogWarning("Park Info history: tab label style or dropdown template not found");
                return;
            }

            var bar = new GameObject("ModParkHistory", typeof(RectTransform));
            s_bar = (RectTransform)bar.transform;
            s_bar.SetParent(ui.transform, false);
            s_bar.anchorMin = s_bar.anchorMax = s_bar.pivot = new Vector2(0.5f, 0.5f);
            var layout = bar.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            AddButton(bar.transform, labelStyle, "<", () => Step(+1));
            s_dayLabel = AddText(bar.transform, labelStyle, "Today", 76f);
            AddButton(bar.transform, labelStyle, ">", () => Step(-1));
            AddText(bar.transform, labelStyle, "Compare:", 64f);

            // The dropdown comes from the options menu and is much bigger than the tab bar: it's
            // scaled as a whole inside a holder that takes part in the layout (see Place)
            var holder = new GameObject("ModCompareHolder", typeof(RectTransform));
            holder.transform.SetParent(bar.transform, false);
            holder.AddComponent<LayoutElement>().preferredWidth = DropWidth;
            GameObject ddObject = Object.Instantiate(template.gameObject, holder.transform);
            ddObject.name = DropDownName;
            var ddRect = (RectTransform)ddObject.transform;
            ddRect.anchorMin = ddRect.anchorMax = ddRect.pivot = new Vector2(0.5f, 0.5f);
            ddRect.anchoredPosition = Vector2.zero;
            ddRect.sizeDelta = new Vector2(DropWidth, ((RectTransform)template.transform).rect.height);
            s_compareDropDown = ddObject.GetComponent<Dropdown>();
            for (int i = 0; i < s_compareDropDown.onValueChanged.GetPersistentEventCount(); i++)
                s_compareDropDown.onValueChanged.SetPersistentListenerState(i, UnityEventCallState.Off);
            s_compareDropDown.onValueChanged.RemoveAllListeners();
            s_compareDropDown.onValueChanged.AddListener(i =>
            {
                s_compare = i == 0 ? -1 : i - 1;
                Refresh();
            });
            Plugin.Log.LogInfo("Park Info history bar added");
        }

        // Between the right edge of the last visible tab and the close button, centered on the tabs
        private static void Place(ComputerUI ui)
        {
            var parent = (RectTransform)s_bar.parent;
            var corners = new Vector3[4];

            float left = float.MinValue, bottom = 0f, top = 0f;
            foreach (Toggle tab in ui.m_toggles)
            {
                if (tab == null || !tab.gameObject.activeInHierarchy)
                    continue;
                ((RectTransform)tab.transform).GetWorldCorners(corners);
                Vector3 bl = parent.InverseTransformPoint(corners[0]), tr = parent.InverseTransformPoint(corners[2]);
                if (tr.x > left)
                {
                    left = tr.x;
                    bottom = bl.y;
                    top = tr.y;
                }
            }
            if (left == float.MinValue)
                return;

            // The computer's rect includes the black screen border; the close button sits inside it
            Rect area = parent.rect;
            float right = area.xMax - 100f;
            Transform close = ui.transform.Find("CloseButton");
            if (close is RectTransform closeRect && closeRect.gameObject.activeInHierarchy)
            {
                closeRect.GetWorldCorners(corners);
                float closeLeft = parent.InverseTransformPoint(corners[0]).x;
                float closeRight = parent.InverseTransformPoint(corners[2]).x;
                if (closeRight - closeLeft > 2f && closeLeft > left)
                    right = Mathf.Min(right, closeLeft);
            }

            const float margin = 8f;
            float height = (top - bottom) * 0.5f;
            s_bar.sizeDelta = new Vector2(Mathf.Max(0f, right - left - 2f * margin), height);
            s_bar.anchoredPosition = new Vector2((left + right) / 2f, (top + bottom) / 2f) - area.center;

            var ddRect = (RectTransform)s_compareDropDown.transform;
            float fullHeight = ddRect.sizeDelta.y; // the template's height, set in Build
            float scale = fullHeight > 0f ? Mathf.Min(1f, height / fullHeight) : 1f;
            ddRect.localScale = new Vector3(scale, scale, 1f);
            ddRect.sizeDelta = new Vector2(DropWidth / scale, fullHeight);
        }

        // Switched in the options menu: show or hide the bar, and go back to today
        internal static void ApplyEnabled()
        {
            s_view = 0;
            s_compare = -1;
            if (s_bar != null)
                s_bar.gameObject.SetActive(ModConfig.ParkInfoHistory.Value);
            Refresh();
        }

        private static bool s_scienceShowsHistory;

        private static void Step(int direction)
        {
            int next = s_view + direction;
            if (next < 0 || next > MaxDaysBack || !HasDay(next))
                return;
            s_view = next;
            Refresh();
        }

        private static void Refresh()
        {
            RefreshBar();
            // Lists that are only built once get the shown day right away (and today's values back
            // when going back to today)
            LiveIncomePatch.ShowNow();
            ScienceSummaryUI science = Object.FindObjectOfType<ScienceSummaryUI>();
            if (science != null && (Active || s_scienceShowsHistory))
                ShowScience(science);
            s_scienceShowsHistory = Active;
        }

        private static void RefreshBar()
        {
            if (s_dayLabel == null)
                return;
            if (!HasDay(s_view))
                s_view = 0;
            s_dayLabel.text = DayName(s_view);

            var options = new List<string> { "—" };
            for (int i = 0; i <= MaxDaysBack; i++)
                options.Add(DayName(i) + (HasDay(i) ? "" : " (no data)"));
            s_compareDropDown.ClearOptions();
            s_compareDropDown.AddOptions(options);
            s_compareDropDown.SetValueWithoutNotify(s_compare + 1);
            s_compareDropDown.RefreshShownValue(); // SetValue skips this when the value didn't change
        }

        // A copy of a tab label, so it has the same font, color and (scaled) size
        private static TMP_Text AddText(Transform parent, TMP_Text style, string text, float width)
        {
            GameObject go = Object.Instantiate(style.gameObject, parent);
            go.name = "ModText";
            TMP_Text t = go.GetComponent<TMP_Text>();
            t.text = text;
            // The tab labels size themselves to their small box; in the bar that would make them
            // much bigger, so use the size the label really has
            t.enableAutoSizing = false;
            t.fontSize = LabelSize(style);
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            go.AddComponent<LayoutElement>().preferredWidth = width;
            ParkInfoTextScale.RegisterCopy(style, t);
            return t;
        }

        // An auto-sized label may report its maximum size; it can't be taller than its own box
        internal static float LabelSize(TMP_Text label)
        {
            float size = label.enableAutoSizing ? Mathf.Min(label.fontSize, label.fontSizeMax) : label.fontSize;
            float box = ((RectTransform)label.transform).rect.height;
            return box > 1f ? Mathf.Min(size, box) : size;
        }

        private static void AddButton(Transform parent, TMP_Text style, string label, Action onClick)
        {
            TMP_Text t = AddText(parent, style, label, 18f);
            t.fontStyle = FontStyles.Bold;
            Button button = t.gameObject.AddComponent<Button>();
            button.targetGraphic = t;
            button.onClick.AddListener(() => onClick());
        }
    }
}
