using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ParkasaurusResurrection
{
    // Scrollable inventory. The game keeps every item in ParkFile.m_inventory (no limit), but the
    // window has 48 slots (the last one is the trash); items that don't fit stay hidden until a
    // slot frees up. When there are more items, extra slots are added in front of the trash and
    // the slot grid scrolls inside its original frame with the mouse wheel, one row per notch.
    //
    // The game links items to slots by child index (ZooItem.m_invSlot, InvSlotUI.m_slotPos), so
    // extra slots are real children of the same grid and all game logic keeps working.
    //
    // A scrollbar (a copy of one of the game's own) sits right of the grid while it can scroll;
    // dragging it moves the grid in whole rows.
    //
    // Switched off, the next Setup (every time the window opens) removes the extra slots and scrolls
    // back to the top: items beyond the 47 slots are hidden again, as in the game.
    public static class InventoryScrollPatch
    {
        private const int GameSlots = 48; // including the trash
        private const string ViewportName = "ModInvViewport";
        private const string ExtraSlotName = "ModSlot";
        private const float WheelUnitsPerNotch = 120f;

        private static float s_scroll;  // pixels scrolled down
        private static float s_notches; // wheel movement not yet turned into whole rows
        private static int s_logged;
        private static Scrollbar s_bar;
        private const float BarWidth = 8f;
        // While the scrollbar shows, the window's background grows this much to the right to make
        // room for it; the slots stay where they are
        private const float ExtraWidth = 12f;
        private static readonly string[] Backgrounds = { "BackPack", "Storage", "shadow", "shadowcrate" };
        private static bool s_widened;
        private static Transform s_widenedWindow; // a new park has a new window, not yet widened

        [HarmonyPatch(typeof(InvMainUI), nameof(InvMainUI.Setup))]
        public static class SlotsAndViewport
        {
            public static void Prefix(InvMainUI __instance)
            {
                try
                {
                    EnsureViewport(__instance);
                    EnsureSlots(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Inventory scroll: {e}");
                }
            }

            // The window gets its size for the current mode (backpack or crates) inside Setup
            public static void Postfix(InvMainUI __instance)
            {
                try
                {
                    FitContent(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Inventory scroll: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(InvMainUI), nameof(InvMainUI.Open))]
        public static class ResetOnOpen
        {
            public static void Prefix()
            {
                s_scroll = 0f;
                s_notches = 0f;
            }
        }

        [HarmonyPatch(typeof(InvMainUI), "LateUpdate")]
        public static class Wheel
        {
            public static void Postfix(InvMainUI __instance)
            {
                if (ModConfig.ScrollableInventory.Value)
                    HandleWheel(__instance);
            }
        }

        // A remembered slot that no longer exists, or that is now the trash, means "first free slot"
        [HarmonyPatch(typeof(InvMainUI), "AddAtBestSpot")]
        public static class ValidSlot
        {
            public static void Prefix(InvMainUI __instance, ZooItem item)
            {
                Transform root = __instance.m_root.transform;
                if (item.m_invSlot >= root.childCount ||
                    (item.m_invSlot >= 0 && root.GetChild(item.m_invSlot).GetComponent<InvSlotUI>().m_isTrash))
                    item.m_invSlot = -1;
            }
        }

        // Saves stay valid for the game without the mod: slot 47 and up don't exist there (47 is
        // the trash), so those items are saved without a slot and land in the first free one
        [HarmonyPatch(typeof(GameCore), nameof(GameCore.SaveGameAsString))]
        public static class SaveCompatible
        {
            public static void Prefix(GameCore __instance, out List<KeyValuePair<ZooItem, int>> __state)
            {
                __state = new List<KeyValuePair<ZooItem, int>>();
                ParkFile park = __instance.m_park;
                if (park == null)
                    return;
                foreach (List<ZooItem> list in new[] { park.m_inventory, park.m_dinoCrated })
                {
                    if (list == null)
                        continue;
                    foreach (ZooItem item in list)
                    {
                        if (item.m_invSlot >= GameSlots - 1)
                        {
                            __state.Add(new KeyValuePair<ZooItem, int>(item, item.m_invSlot));
                            item.m_invSlot = -1;
                        }
                    }
                }
            }

            public static void Postfix(List<KeyValuePair<ZooItem, int>> __state)
            {
                foreach (KeyValuePair<ZooItem, int> pair in __state)
                    pair.Key.m_invSlot = pair.Value;
            }
        }

        // The grid moves inside a masked frame that takes over the grid's original place and size
        private static void EnsureViewport(InvMainUI ui)
        {
            var root = (RectTransform)ui.m_root.transform;
            if ((root.parent != null && root.parent.name == ViewportName) || !ModConfig.ScrollableInventory.Value)
                return;

            var viewport = new GameObject(ViewportName, typeof(RectTransform), typeof(RectMask2D));
            var frame = (RectTransform)viewport.transform;
            frame.SetParent(root.parent, false);
            frame.SetSiblingIndex(root.GetSiblingIndex());
            frame.anchorMin = root.anchorMin;
            frame.anchorMax = root.anchorMax;
            frame.pivot = root.pivot;
            frame.offsetMin = root.offsetMin;
            frame.offsetMax = root.offsetMax;

            root.SetParent(frame, false);
            root.anchorMin = new Vector2(0f, 1f);
            root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(0f, frame.rect.height);
        }

        // Enough slots for every item plus one free slot; never fewer than the game's own
        private static void EnsureSlots(InvMainUI ui)
        {
            Transform root = ui.m_root.transform;
            ParkFile park = GameCore.Get?.m_park;
            if (park == null)
                return;
            int items = ui.m_crateMode ? park.m_dinoCrated.Count : park.m_inventory.Count;
            int wanted = ModConfig.ScrollableInventory.Value ? Math.Max(0, items + 1 - (GameSlots - 1)) : 0;

            List<GameObject> extras = Extras(root);
            for (int i = extras.Count - 1; i >= wanted; i--)
                Object.DestroyImmediate(extras[i]);
            for (int i = extras.Count; i < wanted; i++)
                AddExtraSlot(root);
        }

        private static List<GameObject> Extras(Transform root)
        {
            var extras = new List<GameObject>();
            for (int i = 0; i < root.childCount; i++)
            {
                if (root.GetChild(i).name == ExtraSlotName)
                    extras.Add(root.GetChild(i).gameObject);
            }
            return extras;
        }

        // A copy of the first slot, placed in front of the trash so the trash stays last
        private static void AddExtraSlot(Transform root)
        {
            Transform template = null;
            int trash = root.childCount;
            for (int i = 0; i < root.childCount; i++)
            {
                InvSlotUI slot = root.GetChild(i).GetComponent<InvSlotUI>();
                if (slot == null)
                    continue;
                if (slot.m_isTrash)
                    trash = Math.Min(trash, i);
                else if (template == null)
                    template = slot.transform;
            }
            if (template == null)
                return;

            GameObject extra = Object.Instantiate(template.gameObject, root);
            extra.name = ExtraSlotName;
            for (int i = extra.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(extra.transform.GetChild(i).gameObject);
            extra.transform.SetSiblingIndex(trash);
        }

        // Fills the last row, sizes the grid to all rows and keeps the scroll position in range
        private static void FitContent(InvMainUI ui)
        {
            var root = (RectTransform)ui.m_root.transform;
            var frame = root.parent as RectTransform;
            GridLayoutGroup grid = root.GetComponent<GridLayoutGroup>();
            if (frame == null || frame.name != ViewportName || grid == null)
                return;

            int columns = Columns(grid, frame.rect.width);
            if (Extras(root).Count > 0)
            {
                while (root.childCount % columns != 0)
                    AddExtraSlot(root);
                for (int i = 0; i < root.childCount; i++)
                    root.GetChild(i).GetComponent<InvSlotUI>().m_slotPos = i;
            }

            int rows = (root.childCount + columns - 1) / columns;
            float height = grid.padding.vertical + rows * grid.cellSize.y + Math.Max(0, rows - 1) * grid.spacing.y;
            root.sizeDelta = new Vector2(0f, Mathf.Max(frame.rect.height, height));
            Scroll(root, frame, 0f);
        }

        private static int Columns(GridLayoutGroup grid, float width)
        {
            if (grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount)
                return Math.Max(1, grid.constraintCount);
            float cell = grid.cellSize.x + grid.spacing.x;
            return Math.Max(1, Mathf.FloorToInt((width - grid.padding.horizontal + grid.spacing.x) / cell));
        }

        private static void HandleWheel(InvMainUI ui)
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var root = (RectTransform)ui.m_root.transform;
            var frame = root.parent as RectTransform;
            if (mouse == null || frame == null || frame.name != ViewportName)
                return;
            float wheel = mouse.scroll.ReadValue().y;
            if (wheel == 0f || root.rect.height <= frame.rect.height + 0.5f)
                return;

            Canvas canvas = frame.GetComponentInParent<Canvas>();
            Camera cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!RectTransformUtility.RectangleContainsScreenPoint(frame, mouse.position.ReadValue(), cam))
                return;
            if (s_logged < 5)
            {
                s_logged++;
                Plugin.Log.LogInfo($"Inventory wheel: raw {wheel}");
            }

            s_notches += wheel / WheelUnitsPerNotch;
            int rows = (int)s_notches; // whole notches only; smooth wheels add up
            if (rows == 0)
                return;
            s_notches -= rows;
            GridLayoutGroup grid = root.GetComponent<GridLayoutGroup>();
            float rowHeight = grid != null ? grid.cellSize.y + grid.spacing.y : 37f;
            Scroll(root, frame, -rows * rowHeight); // wheel up = towards the top
        }

        private static void Scroll(RectTransform root, RectTransform frame, float delta)
        {
            float max = Mathf.Max(0f, root.rect.height - frame.rect.height);
            s_scroll = Mathf.Clamp(s_scroll + delta, 0f, max);
            root.anchoredPosition = new Vector2(0f, s_scroll);
            UpdateBar(root, frame, max);
        }

        private static float RowHeight(RectTransform root)
        {
            GridLayoutGroup grid = root.GetComponent<GridLayoutGroup>();
            return grid != null ? grid.cellSize.y + grid.spacing.y : 37f;
        }

        // Shown only while the grid is taller than its frame; one step per row
        private static void UpdateBar(RectTransform root, RectTransform frame, float max)
        {
            if (s_bar == null)
                s_bar = CreateBar(root, frame);
            if (s_bar == null)
                return;
            bool show = max > 0.5f;
            s_bar.gameObject.SetActive(show);
            SetWidened(frame.parent, show);
            if (!show)
                return;
            ApplyColors(frame);
            s_bar.size = frame.rect.height / root.rect.height;
            s_bar.numberOfSteps = Mathf.RoundToInt(max / RowHeight(root)) + 1;
            s_bar.SetValueWithoutNotify(1f - s_scroll / max);
        }

        // Background images and the close button move their right edge; everything else stays put
        private static void SetWidened(Transform window, bool wide)
        {
            if (window != s_widenedWindow)
            {
                s_widenedWindow = window;
                s_widened = false;
            }
            if (wide == s_widened)
                return;
            s_widened = wide;
            float delta = wide ? ExtraWidth : -ExtraWidth;
            foreach (string name in Backgrounds)
            {
                if (window.Find(name) is RectTransform rt)
                    rt.offsetMax = new Vector2(rt.offsetMax.x + delta, rt.offsetMax.y);
            }
            if (window.Find("CloseButton") is RectTransform close)
                close.anchoredPosition = new Vector2(close.anchoredPosition.x + delta, close.anchoredPosition.y);
        }

        // In the window's own colours: the track a darker shade of the background, the handle in
        // the colour of the slots (backpack or crate mode)
        private static void ApplyColors(RectTransform frame)
        {
            InvMainUI ui = frame.GetComponentInParent<InvMainUI>();
            if (ui == null)
                return;
            Transform backgroundObject = frame.parent.Find(ui.m_crateMode ? "Storage" : "BackPack");
            Image background = backgroundObject != null ? backgroundObject.GetComponent<Image>() : null;
            Image track = s_bar.GetComponent<Image>();
            Image handle = s_bar.handleRect != null ? s_bar.handleRect.GetComponent<Image>() : null;
            if (track != null && background != null)
                track.color = Color.Lerp(background.color, Color.black, 0.25f);
            if (handle != null)
                handle.color = ui.m_crateMode ? ui.m_crateColor : ui.m_invColor;
        }

        private static Scrollbar CreateBar(RectTransform root, RectTransform frame)
        {
            Scrollbar template = Resources.FindObjectsOfTypeAll<Scrollbar>()
                .FirstOrDefault(b => b != null && b.direction == Scrollbar.Direction.BottomToTop && b.handleRect != null);
            if (template == null)
            {
                Plugin.Log.LogWarning("Inventory scroll: no scrollbar found to copy");
                return null;
            }

            GameObject go = Object.Instantiate(template.gameObject, frame.parent);
            go.name = "ModInvScrollbar";
            var rt = (RectTransform)go.transform;
            // Just right of the frame, as tall as the frame
            rt.anchorMin = new Vector2(frame.anchorMax.x, frame.anchorMin.y);
            rt.anchorMax = new Vector2(frame.anchorMax.x, frame.anchorMax.y);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(frame.offsetMax.x + 2f, frame.offsetMin.y);
            rt.offsetMax = new Vector2(frame.offsetMax.x + 2f + BarWidth, frame.offsetMax.y);

            Scrollbar bar = go.GetComponent<Scrollbar>();
            bar.transition = Selectable.Transition.None; // colours are set in ApplyColors
            for (int i = 0; i < bar.onValueChanged.GetPersistentEventCount(); i++)
                bar.onValueChanged.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            bar.onValueChanged.RemoveAllListeners();
            bar.onValueChanged.AddListener(value =>
            {
                float max = Mathf.Max(0f, root.rect.height - frame.rect.height);
                float row = RowHeight(root);
                s_scroll = Mathf.Clamp(Mathf.Round((1f - value) * max / row) * row, 0f, max);
                root.anchoredPosition = new Vector2(0f, s_scroll);
            });
            return bar;
        }
    }
}
