using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ParkasaurusResurrection
{
    // Adds a "Resurrection" tab to the game's options menu. The Graphics pane is a scrollable list of
    // LabelUI / SliderUI / DropDownUI / ToggleUI rows, so it's cloned, emptied and refilled with
    // our settings, using one row of each type as a template. Everything reads and writes the
    // same config entries as the config file. A text box below the list explains the setting
    // under the mouse.
    //
    // The list has two columns. With "Wide options menu" on, the menu card is made wider while the
    // Resurrection tab is open and both columns sit side by side; otherwise the second column's rows are
    // moved below the first, as one list.
    public static class OptionsMenuPatch
    {
        // Settings that don't change right away; their help says when they do
        private const string RestartMark = " *";
        private const string DefaultHelp = "Point at a setting to see what it does. Settings marked * don't change right away: " +
                                           "pointing at them tells you when they do.";

        private class Row
        {
            public GameObject Go;
            public Func<bool> Visible;
            public Action Refresh;
            public int Column;
            public bool AlwaysShown; // also shown while the mod is switched off
        }

        private class Templates
        {
            public GameObject Label, Slider, Toggle, DropDown;
        }

        private static readonly List<Row> Rows = new List<Row>();
        internal static Text s_help;
        private static Species s_species = Species.Mosasaurus;

        private static GameObject s_pane;
        private static readonly Transform[] Columns = new Transform[2];
        private static int s_column;          // column new rows go into
        private static RectTransform s_card;  // the menu card around all panes
        private static RectTransform s_reset; // the game's reset button, next to the card
        private static float s_cardWidth, s_resetX, s_extraWidth;
        private static bool s_wide;

        // Prime runs once, the first time the options menu is opened
        [HarmonyPatch(typeof(OptionsUI), "Prime")]
        public static class BuildTab
        {
            public static void Postfix(OptionsUI __instance)
            {
                try
                {
                    Build(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Could not build the Resurrection options tab: {e}");
                }
            }
        }

        // Refresh runs every time the menu opens: show the current config values
        [HarmonyPatch(typeof(OptionsUI), nameof(OptionsUI.Refresh))]
        public static class RefreshTab
        {
            public static void Postfix() => RefreshAll();
        }

        // Wide only while the Resurrection tab is showing, so the game's own tabs keep their layout
        [HarmonyPatch(typeof(OptionsUI), nameof(OptionsUI.SetPane))]
        public static class PaneChanged
        {
            public static void Postfix() => ApplyWidth();
        }

        // Write the config file once when the menu closes, instead of on every slider step
        [HarmonyPatch(typeof(OptionsUI), nameof(OptionsUI.OnDisable))]
        public static class SaveOnClose
        {
            public static void Postfix()
            {
                Plugin.Instance.Config.Save();
                // Temporary: investigating T-posing swimmers after changing settings
                if (Plugin.ModEnabledAtStart)
                    AnimSpeedPatch.LogSnapshot();
            }
        }

        private static void RefreshAll()
        {
            bool modOn = ModConfig.ModEnabled.Value;
            foreach (Row row in Rows)
            {
                row.Refresh?.Invoke();
                row.Go.SetActive((row.AlwaysShown || modOn) && (row.Visible == null || row.Visible()));
            }
            ApplyWidth();
        }

        private static void Build(OptionsUI ui)
        {
            GameObject graphicsPane = ui.m_panes.FirstOrDefault(p => p != null && p.name == "Graphics");
            if (graphicsPane == null)
            {
                Plugin.Log.LogError("Resurrection options tab: Graphics pane not found");
                return;
            }
            int index = ui.tabs.Length;
            // A new options menu (e.g. after a scene change): forget the rows of the old one
            Rows.Clear();
            s_wide = false;

            // Tab: a copy of the last tab, placed right after it
            Toggle templateTab = ui.tabs[ui.tabs.Length - 1];
            GameObject tabObject = Object.Instantiate(templateTab.gameObject, templateTab.transform.parent);
            tabObject.name = "ModsTab";
            tabObject.transform.SetSiblingIndex(templateTab.transform.GetSiblingIndex() + 1);
            Toggle tab = tabObject.GetComponent<Toggle>();
            DetachListeners(tab.onValueChanged);
            tab.SetIsOnWithoutNotify(false);
            tab.onValueChanged.AddListener(on =>
            {
                if (on)
                    ui.SetPane(index);
            });
            SetText(tabObject, "Resurrection");

            // Pane: a copy of the Graphics pane, emptied except for one template row per type
            GameObject pane = Object.Instantiate(graphicsPane, graphicsPane.transform.parent);
            pane.name = "Mods";
            pane.SetActive(false);
            Transform content = pane.transform.Find("Scroll View/Viewport/Content");
            var scroll = (RectTransform)pane.transform.Find("Scroll View");
            Templates templates = content != null ? TakeTemplates(content, pane.transform) : null;
            if (templates == null || templates.Label == null || templates.Slider == null || templates.Toggle == null || templates.DropDown == null)
            {
                Plugin.Log.LogError("Resurrection options tab: scroll list or template rows not found");
                Object.Destroy(pane);
                Object.Destroy(tabObject);
                return;
            }
            var fitter = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Pane and list follow the card's width, so they grow with it in wide mode
            StretchKeepingPlace((RectTransform)pane.transform);
            StretchKeepingPlace(scroll);
            s_help = CreateHelpBox(scroll, templates.Label);
            MakeColumns((RectTransform)content);

            s_pane = pane;
            s_card = pane.transform.parent?.parent as RectTransform;
            s_reset = ui.transform.Find("Reset") as RectTransform;
            if (s_card != null)
                s_cardWidth = s_card.rect.width;
            if (s_reset != null)
                s_resetX = s_reset.anchoredPosition.x;
            // One extra column's worth of width, as far as the screen allows
            var canvas = ui.GetComponentInParent<Canvas>()?.rootCanvas.transform as RectTransform;
            float room = canvas != null ? canvas.rect.width * 0.95f - s_cardWidth : float.MaxValue;
            s_extraWidth = Mathf.Clamp(scroll.rect.width, 0f, Mathf.Max(0f, room));

            ui.tabs = ui.tabs.Append(tab).ToArray();
            ui.m_panes = ui.m_panes.Append(pane).ToArray();

            AddRows(templates);
            RefreshAll();
            Plugin.Log.LogInfo("Resurrection options tab added");
        }

        private static void AddRows(Templates t)
        {
            Transform c = null; // rows go into the current column (see UseColumn)
            UseColumn(0);
            AddLabel(c, t, "General").AlwaysShown = true;
            AddToggleRow(c, t, "Enable mod" + RestartMark,
                "Switch the whole mod on or off. The other settings only show while it's on. This tab stays available. " +
                "* Takes effect after restarting the game.",
                () => ModConfig.ModEnabled.Value,
                on =>
                {
                    ModConfig.ModEnabled.Value = on;
                    RefreshAll();
                }).AlwaysShown = true;
            AddToggleRow(c, t, "Wide options menu",
                "Make the options menu wider while this tab is open, with the settings in two columns.",
                () => ModConfig.WideOptionsMenu.Value,
                on =>
                {
                    ModConfig.WideOptionsMenu.Value = on;
                    ApplyWidth();
                });
            AddLabel(c, t, "Terrain");
            AddToggle(c, t, ModConfig.QuadTerrainTiles, "Smooth slopes" + RestartMark,
                "Sloped terrain tiles get a centre point, so hills and basins look smoother. Choose the look with Slope shape. " +
                "Without the mod: every sloped tile folds along the same diagonal. " +
                "* Takes effect the next time you load a park.");
            AddDropDown(c, t, "Slope shape",
                "The look of sloped tiles (needs Smooth slopes). Game default: the game's own fold. " +
                "Deep valleys: basins stay deep. Even: symmetric slopes. High ridges: hilltops and ridges stay high.",
                new[] { "Game default", "Deep valleys", "Even", "High ridges" },
                () => (int)ModConfig.SmoothEdges.Value,
                i =>
                {
                    ModConfig.SmoothEdges.Value = (EdgeShape)i;
                    // Re-upload the terrain heights so the new shape shows immediately
                    if (GameCore.Get != null)
                        GameCore.Get.m_heightGPUSync = true;
                });

            const string SwimmersHelp = "Settings for all swimming dinos. A species can get its own values under Per species.";
            AddLabel(c, t, "Swimming: depth", SwimmersHelp);
            AddToggle(c, t, ModConfig.SmoothSwimDepth, "Smooth swim depth",
                "Swimming dinos glide up and down smoothly instead of jumping between depths, and don't pop out of the water.");
            AddGlobalSlider(c, t, SpeciesSetting.MaxDiveDepth);
            AddGlobalSlider(c, t, SpeciesSetting.IdleDepthChangeTime);
            AddGlobalSlider(c, t, SpeciesSetting.BobbingAmount);
            AddSlider(c, t, "Depth smoothing", ModConfig.DepthSmoothingTime.Description.Description,
                0f, 5f, () => ModConfig.DepthSmoothingTime.Value, v => ModConfig.DepthSmoothingTime.Value = v);

            AddLabel(c, t, "Swimming: trips", SwimmersHelp);
            AddToggle(c, t, ModConfig.LongerSwims, "Longer swims",
                "Swimming dinos pick destinations further away, so they swim real stretches instead of short hops.");
            AddGlobalSlider(c, t, SpeciesSetting.MinSwimDistance);
            AddGlobalSlider(c, t, SpeciesSetting.MaxSwimDistance);
            AddSlider(c, t, "Minimum water depth", ModConfig.MinRouteDepth.Description.Description,
                0.8f, 10f, () => ModConfig.MinRouteDepth.Value, v => ModConfig.MinRouteDepth.Value = v);

            AddLabel(c, t, "Swimming: turning", SwimmersHelp);
            AddToggle(c, t, ModConfig.TurningCircle, "Smooth turns",
                "Swimming dinos turn in a curve instead of spinning around on the spot.");
            AddToggle(c, t, ModConfig.SmartRoutes, "Swim around corners",
                "Swimming dinos find their way through the water around corners and headlands, keep to the open water, " +
                "and don't stop with their head in a corner. Works together with Smooth turns.");
            AddGlobalSlider(c, t, SpeciesSetting.TurnRadius);
            AddGlobalSlider(c, t, SpeciesSetting.TurnSpeedFactor);
            AddGlobalSlider(c, t, SpeciesSetting.IdleTurnRate);

            AddLabel(c, t, "Speed and animation", SwimmersHelp);
            AddToggle(c, t, ModConfig.SpeedAndAnimation, "Custom speeds",
                "Use the speed and animation settings below for swimming dinos, and the speeds set under Per species.");
            AddGlobalSlider(c, t, SpeciesSetting.MovementSpeed);
            AddGlobalSlider(c, t, SpeciesSetting.AnimSpeedMoving);
            AddGlobalSlider(c, t, SpeciesSetting.AnimSpeedIdle);
            AddSlider(c, t, "Animation blending", ModConfig.AnimBlendTime.Description.Description,
                0f, 10f, () => ModConfig.AnimBlendTime.Value, v => ModConfig.AnimBlendTime.Value = v);

            UseColumn(1);
            AddSpeciesRows(c, t);

            AddLabel(c, t, "Building");
            AddToggle(c, t, ModConfig.DiagonalBuilding, "Diagonal fences & paths",
                "Drag a fence or path at an angle to build a staircase of tiles instead of a straight line. " +
                "Hold Ctrl while dragging to flip the staircase.");
            AddSlider(c, t, "Straight line margin",
                "Drags within this many degrees of a straight line stay straight; further off they become a staircase.",
                1f, 44f, () => ModConfig.StraightSnapAngle.Value, v => ModConfig.StraightSnapAngle.Value = v);
            AddToggle(c, t, ModConfig.ShiftPaintsOneArea, "Shift: paint one exhibit",
                "Hold Shift while dragging terrain tiles to only paint inside the exhibit where you started, " +
                "so you can drag across the fence freely. Start outside an exhibit to only paint outside exhibits.");
            AddToggle(c, t, ModConfig.RemoveItemArea, "Remove Item: drag an area",
                "With the Remove Item tool, drag a rectangle to mark everything in it: start on empty ground, or hold Shift. " +
                "Hold Alt (or Ctrl) to unmark an area. Then press Destroy as usual.");

            AddLabel(c, t, "Park Info");
            AddToggle(c, t, ModConfig.ParkInfoHistory, "Day history",
                "Office computer, Park Info: use < and > to look back up to 6 days, and compare two days: " +
                "every number then shows the difference (+N / -N).");
            AddToggle(c, t, ModConfig.ParkInfoLiveIncome, "Live income list",
                "Office computer, Park Info: the income list in the middle shows today's income and costs during the day, " +
                "not only at the end of the day.");
            AddToggle(c, t, ModConfig.ParkInfoExtraColumns, "Occupation and hat columns",
                "Staff list: what each employee is doing right now, so you can find who's idle (click the header to sort). " +
                "Dino list: the hat each dino wears. Point at an entry for the full text or the hat's effect.");
            AddSlider(c, t, "Text size", "Size of all text on the Park Info screen. 1 = the game's own size.",
                0.5f, 1f, () => ModConfig.ParkInfoTextScale.Value, v => ModConfig.ParkInfoTextScale.Value = v);

            AddLabel(c, t, "Performance");
            AddToggle(c, t, ModConfig.PhysicsStepFollowsSpeed, "Physics steps follow game speed",
                "On fast game speeds, physics runs in bigger steps instead of more often. Saves a lot of work on 2x and 3x speed; " +
                "doors, debris and flying visitors still work.");
            AddToggle(c, t, ModConfig.KinematicPeople, "Lighter physics for people and cars",
                "Visitors, staff and cars only take part in physics where needed (people open doors). Busy parks run " +
                "smoother, because physics no longer gets heavier with every visitor and car.");
            AddToggle(c, t, ModConfig.NoAutoSyncTransforms, "Sync physics once per step",
                "Moved objects are passed to physics once per physics step instead of before every check. " +
                "Same result, less work.");

            AddLabel(c, t, "Interface");
            AddToggle(c, t, ModConfig.QuietObjects, "Quiet scenery",
                "Trees, bushes, rocks, theme items, sheds, toys, nests, decorations, lights, bridges, benches, bins, heaters, " +
                "gazebos, picnic tables and doors can't be clicked during normal play, " +
                "so you click what's behind them. Use the Move Item or Remove Item tool to pick them.");
            AddToggle(c, t, ModConfig.ShowHints, "Show hints",
                "Shows the keys for this mod's features in the bottom right corner, next to the game's own building tips " +
                "(for example Ctrl while building fences, Shift while painting terrain).");
            AddToggle(c, t, ModConfig.ScrollableInventory, "Scrollable inventory",
                "A full inventory gets extra rows you can scroll to with the mouse wheel. " +
                "Without the mod, items that don't fit stay hidden until a slot is free.");
            AddToggle(c, t, ModConfig.CalmScrollWheel, "Calm scroll wheel",
                "Scrolling through lists moves a small step per wheel click, instead of jumping to the end.");
            AddSlider(c, t, "Scroll step", "How far one wheel click scrolls, as a part of the visible list.",
                0.02f, 1f, () => ModConfig.ScrollStep.Value, v => ModConfig.ScrollStep.Value = v);
            AddToggle(c, t, ModConfig.HideQuestVideos, "Hide quest videos",
                "The quest panel doesn't play its small tutorial videos. Under Proton these videos crash the game, " +
                "for example in the Space mission.");
            AddToggle(c, t, ModConfig.RightClickUnequip, "Right-click to unequip",
                "Right-click a hat on a dino or staff member, or an item on an egg, to put it back in your inventory.");
            AddToggle(c, t, ModConfig.ShiftEmptiesTrash, "Shift-click trash: empty all",
                "Hold Shift and click the trash slot in the Inventory or Storage window to delete everything in that window. " +
                "You're asked to confirm first.");
            AddToggle(c, t, ModConfig.TypeNumbers, "Type numbers",
                "Click a number next to a slider (like the scenery variant) or a shop item's daily amount, " +
                "and type the value you want. Enter to apply, Esc to cancel.");
            AddToggle(c, t, ModConfig.NewNames, "Pet names for dinos",
                "New dinos get playful pet names like Biscuit, Sir Chomps-a-Lot or Mabel. New staff get normal first names " +
                "that match their face, instead of made-up star names. Names you already have stay the same.");

            AddLabel(c, t, "Looks");
            AddToggle(c, t, ModConfig.HideDinoHats, "Hide dino hats",
                "Hats on dinos are not shown. Only the look changes: the hat still does its job.");
            AddToggle(c, t, ModConfig.HideDinoNeckGear, "Hide dino neck items",
                "Neck items on dinos are not shown. Only the look changes: the item still does its job.");
        }

        private static void AddSpeciesRows(Transform c, Templates t)
        {
            AddLabel(c, t, "Per species");
            Species[] species = SpeciesConfig.AllSpecies.ToArray();
            AddDropDown(c, t, "Species",
                "Pick a species to give it its own settings. Swimming settings only show for swimming species.",
                species.Select(SpeciesConfig.DisplayName).ToArray(),
                () => Math.Max(0, Array.IndexOf(species, s_species)),
                i =>
                {
                    s_species = species[i];
                    RefreshAll();
                });
            AddToggleRow(c, t, "Own settings",
                "Give this species its own values (below) instead of the general swimming settings. " +
                "For land species: instead of the game's normal speeds.",
                () => SpeciesConfig.HasCustom(s_species),
                on =>
                {
                    SpeciesConfig.SetCustom(s_species, on);
                    RefreshAll();
                });

            foreach (var pair in ModConfig.Settings)
            {
                SpeciesSetting setting = pair.Key;
                SettingInfo info = pair.Value;
                Row row = AddSlider(c, t, info.Label, info.Description, info.Min, info.Max,
                    () => SpeciesConfig.Entry(s_species, setting)?.Value
                          ?? SpeciesConfig.Get(s_species, setting, SpeciesConfig.IsSwimmer(s_species)),
                    v =>
                    {
                        ConfigEntry<float> entry = SpeciesConfig.Entry(s_species, setting);
                        if (entry != null)
                            entry.Value = v;
                    },
                    () => SpeciesConfig.HasCustom(s_species));
                row.Visible = () => !info.SwimmersOnly || SpeciesConfig.IsSwimmer(s_species);
            }
        }

        // Keeps one row of each type (inactive, outside the list) and removes all other rows
        private static Templates TakeTemplates(Transform content, Transform holder)
        {
            var t = new Templates();
            foreach (Transform row in content.Cast<Transform>().ToList())
            {
                bool keep = false;
                if (t.Label == null && row.GetComponent<LabelUI>() != null) { t.Label = row.gameObject; keep = true; }
                else if (t.Slider == null && row.GetComponent<SliderUI>() != null) { t.Slider = row.gameObject; keep = true; }
                else if (t.Toggle == null && row.gameObject.activeSelf && row.name != "VSync" && row.GetComponent<ToggleUI>() != null) { t.Toggle = row.gameObject; keep = true; }
                else if (t.DropDown == null && row.GetComponent<DropDownUI>() != null && row.name != "Resolution") { t.DropDown = row.gameObject; keep = true; }

                if (keep)
                {
                    row.SetParent(holder, false);
                    row.gameObject.SetActive(false);
                }
                else
                {
                    Object.Destroy(row.gameObject);
                }
            }
            return t;
        }

        // A text box below the scroll list; the list is shortened to make room
        private static Text CreateHelpBox(RectTransform scroll, GameObject labelTemplate)
        {
            const float height = 44f;
            Text style = labelTemplate.GetComponentsInChildren<Text>(true).FirstOrDefault();

            var go = new GameObject("ModsHelp", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(scroll.parent, false);
            rt.anchorMin = new Vector2(scroll.anchorMin.x, 0f);
            rt.anchorMax = new Vector2(scroll.anchorMax.x, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(scroll.offsetMin.x + 4f, scroll.offsetMin.y);
            rt.offsetMax = new Vector2(scroll.offsetMax.x - 4f, scroll.offsetMin.y + height);
            scroll.offsetMin = new Vector2(scroll.offsetMin.x, scroll.offsetMin.y + height + 4f);

            Text text = go.AddComponent<Text>();
            if (style != null)
            {
                text.font = style.font;
                text.fontSize = Math.Max(8, style.fontSize - 2);
                text.color = style.color;
            }
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = DefaultHelp;
            return text;
        }

        // Anchors a rect to all four sides of its parent without moving it, so it resizes with the parent
        private static void StretchKeepingPlace(RectTransform rt)
        {
            var parent = rt.parent as RectTransform;
            if (parent == null)
                return;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector2 min = parent.InverseTransformPoint(corners[0]);
            Vector2 max = parent.InverseTransformPoint(corners[2]);
            Rect area = parent.rect;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = min - area.min;
            rt.offsetMax = max - area.max;
        }

        // The list content gets two side-by-side columns, each laid out like the original list
        private static void MakeColumns(RectTransform content)
        {
            var old = content.GetComponent<VerticalLayoutGroup>();
            RectOffset padding = old != null ? old.padding : new RectOffset();
            float spacing = old != null ? old.spacing : 0f;
            TextAnchor alignment = old != null ? old.childAlignment : TextAnchor.UpperLeft;
            bool controlWidth = old != null && old.childControlWidth, controlHeight = old == null || old.childControlHeight;
            bool expandWidth = old != null && old.childForceExpandWidth;
            if (old != null)
                Object.DestroyImmediate(old);

            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, content.sizeDelta.y);
            content.anchoredPosition = Vector2.zero;

            var row = content.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.childAlignment = TextAnchor.UpperLeft;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = true;
            row.childForceExpandHeight = false;
            row.spacing = 8f;

            for (int i = 0; i < Columns.Length; i++)
            {
                var column = new GameObject("ModColumn" + i, typeof(RectTransform));
                column.transform.SetParent(content, false);
                var layout = column.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(padding.left, padding.right, padding.top, padding.bottom);
                layout.spacing = spacing;
                layout.childAlignment = alignment;
                layout.childControlWidth = controlWidth;
                layout.childControlHeight = controlHeight;
                layout.childForceExpandWidth = expandWidth;
                layout.childForceExpandHeight = false;
                column.AddComponent<LayoutElement>().flexibleWidth = 1f;
                Columns[i] = column.transform;
            }
        }

        // Wide: bigger card, two columns. Narrow (or another tab open): the game's card, one column.
        private static void ApplyWidth()
        {
            if (s_pane == null || Columns[1] == null)
                return;
            bool wide = ModConfig.WideOptionsMenu.Value && s_pane.activeSelf && s_extraWidth > 1f;
            if (wide == s_wide && Columns[1].gameObject.activeSelf == wide)
                return;
            s_wide = wide;

            // Rows go back in their original order, into their own column or all into the first
            foreach (Row row in Rows)
            {
                row.Go.transform.SetParent(Columns[wide ? row.Column : 0], false);
                row.Go.transform.SetAsLastSibling();
            }
            Columns[1].gameObject.SetActive(wide);

            if (s_card != null)
            {
                float width = s_cardWidth + (wide ? s_extraWidth : 0f);
                var layout = s_card.parent != null ? s_card.parent.GetComponent<HorizontalOrVerticalLayoutGroup>() : null;
                if (layout != null && layout.childControlWidth)
                {
                    var element = s_card.GetComponent<LayoutElement>() ?? s_card.gameObject.AddComponent<LayoutElement>();
                    element.preferredWidth = element.minWidth = width;
                }
                else
                {
                    s_card.sizeDelta = new Vector2(s_card.sizeDelta.x + width - s_card.rect.width, s_card.sizeDelta.y);
                }
                LayoutRebuilder.MarkLayoutForRebuild(s_card);
            }
            // The reset button sits against the card's right side
            if (s_reset != null)
                s_reset.anchoredPosition = new Vector2(s_resetX + (wide ? s_extraWidth / 2f : 0f), s_reset.anchoredPosition.y);
        }

        private static void UseColumn(int column) => s_column = column;

        // Config descriptions end with "Game: X" (what the game does without the mod); in the menu
        // that reads better as a sentence
        private static string Friendly(string help)
        {
            int i = help.LastIndexOf("Game: ", StringComparison.Ordinal);
            if (i < 0)
                return help;
            string before = help.Substring(0, i).TrimEnd(), game = help.Substring(i + 6).Trim().TrimEnd('.');
            if (before.Length > 0 && !before.EndsWith(".") && !before.EndsWith(":"))
                before += ".";
            return before + " Without the mod: " + game + ".";
        }

        private static Row NewRow(GameObject template, Transform content, string name, string help)
        {
            GameObject go = Object.Instantiate(template, content != null ? content : Columns[s_column]);
            go.name = "Mod_" + name;
            go.AddComponent<ModsHelpHover>().Text = Friendly(help);
            var row = new Row { Go = go, Column = s_column };
            Rows.Add(row);
            return row;
        }

        private static Row AddLabel(Transform content, Templates t, string text, string help = DefaultHelp)
        {
            Row row = NewRow(t.Label, content, text, help);
            row.Go.GetComponent<LabelUI>().SetLabel(text);
            SetText(row.Go, text);
            row.Go.SetActive(true);
            return row;
        }

        private static void AddToggle(Transform content, Templates t, ConfigEntry<bool> entry, string label, string help)
        {
            AddToggleRow(content, t, label, help, () => entry.Value, on => entry.Value = on);
        }

        private static Row AddToggleRow(Transform content, Templates t, string label, string help, Func<bool> get, Action<bool> set)
        {
            Row row = NewRow(t.Toggle, content, label, help);
            ToggleUI toggleUI = row.Go.GetComponent<ToggleUI>();
            toggleUI.SetLabel(label);
            if (toggleUI.m_label == null && toggleUI.m_labelT == null)
                SetText(row.Go, label);
            Toggle toggle = toggleUI.m_toggle;
            DetachListeners(toggle.onValueChanged);
            toggle.onValueChanged.AddListener(on => set(on));
            row.Refresh = () => toggle.SetIsOnWithoutNotify(get());
            row.Go.SetActive(true);
            return row;
        }

        // A slider for the [Swimming] value of a per-species setting
        private static void AddGlobalSlider(Transform content, Templates t, SpeciesSetting setting)
        {
            SettingInfo info = ModConfig.Settings[setting];
            AddSlider(content, t, info.Label, info.Description, info.Min, info.Max,
                () => info.Global.Value, v => info.Global.Value = v);
        }

        private static Row AddSlider(Transform content, Templates t, string label, string help, float min, float max,
            Func<float> get, Action<float> set, Func<bool> interactable = null)
        {
            Row row = NewRow(t.Slider, content, label, help);
            SliderUI sliderUI = row.Go.GetComponent<SliderUI>();
            sliderUI.m_preFix = "";
            sliderUI.m_postFix = "";
            sliderUI.m_type = SliderType.Normal;
            SetText(row.Go, label);
            Slider slider = sliderUI.m_slider;
            DetachListeners(slider.onValueChanged);
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.SetValueWithoutNotify(get());

            // Activating runs SliderUI.Awake, which adds its own text update listener; ours is
            // added after it so the value text can show 2 decimals instead of 1.
            row.Go.SetActive(true);
            slider.onValueChanged.AddListener(value =>
            {
                set((float)Math.Round(value, 2));
                ShowSliderValue(sliderUI, get());
            });
            row.Refresh = () =>
            {
                slider.SetValueWithoutNotify(get());
                ShowSliderValue(sliderUI, get());
                if (interactable != null)
                    slider.interactable = interactable();
            };
            return row;
        }

        private static void ShowSliderValue(SliderUI sliderUI, float value)
        {
            if (sliderUI.m_sliderTag != null)
                sliderUI.m_sliderTag.text = value.ToString("0.##");
        }

        private static Row AddDropDown(Transform content, Templates t, string label, string help,
            string[] options, Func<int> get, Action<int> set)
        {
            Row row = NewRow(t.DropDown, content, label, help);
            DropDownUI dropDownUI = row.Go.GetComponent<DropDownUI>();
            dropDownUI.m_autoTranslate = false;
            dropDownUI.m_haveLocks = false;
            dropDownUI.m_haveImages = false;
            if (dropDownUI.m_label != null)
                dropDownUI.m_label.text = label;
            else
                SetText(row.Go, label);

            Dropdown dropDown = dropDownUI.m_dropDown;
            DetachListeners(dropDown.onValueChanged);
            dropDown.ClearOptions();
            dropDown.AddOptions(options.ToList());
            dropDown.onValueChanged.AddListener(i => set(i));
            row.Refresh = () => dropDown.SetValueWithoutNotify(get());
            row.Go.SetActive(true);
            return row;
        }

        // Cloned controls still carry the game's own listeners (e.g. VSync, UI scale); switch them off
        private static void DetachListeners(UnityEventBase unityEvent)
        {
            for (int i = 0; i < unityEvent.GetPersistentEventCount(); i++)
                unityEvent.SetPersistentListenerState(i, UnityEventCallState.Off);
            unityEvent.RemoveAllListeners();
        }

        // Sets the first legacy or TextMeshPro text of a row
        private static void SetText(GameObject row, string text)
        {
            Text legacy = row.GetComponentsInChildren<Text>(true).FirstOrDefault();
            if (legacy != null)
            {
                legacy.text = text;
                return;
            }
            TMP_Text tmp = row.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
            if (tmp != null)
                tmp.text = text;
        }
    }

    // Shows a row's explanation in the Resurrection help box while the mouse is over it. Only handles
    // pointer enter, so scrolling the list still works.
    public class ModsHelpHover : MonoBehaviour, IPointerEnterHandler
    {
        public string Text;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (OptionsMenuPatch.s_help != null)
                OptionsMenuPatch.s_help.text = Text;
        }
    }
}
