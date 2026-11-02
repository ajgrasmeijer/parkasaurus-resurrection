using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ParkasaurusResurrection
{
    // Park Info (office computer): all texts smaller, so the values have room for "(+N)" and the
    // screen has room for extra controls. Every text's original size is remembered, so changing
    // the setting always scales from the game's own size. Lists that fill in later (reviews,
    // finance rows) are picked up by a check twice per second while the computer is open.
    public static class ParkInfoTextScale
    {
        private class Sizes
        {
            public float Size, Min, Max;
        }

        private static readonly Dictionary<Component, Sizes> Known = new Dictionary<Component, Sizes>();
        private static ComputerUI s_open;
        private static float s_nextScan;

        // Called when a park is closed (GameCore.OnDestroy)
        internal static void Reset()
        {
            Known.Clear();
            s_open = null;
        }

        [HarmonyPatch(typeof(ComputerUI), nameof(ComputerUI.OnEnable))]
        public static class OnOpen
        {
            public static void Postfix(ComputerUI __instance)
            {
                s_open = __instance;
                Scan(__instance);
            }
        }

        [HarmonyPatch(typeof(ComputerUI), "Update")]
        public static class WhileOpen
        {
            public static void Postfix(ComputerUI __instance)
            {
                if (Time.unscaledTime < s_nextScan)
                    return;
                s_nextScan = Time.unscaledTime + 0.5f;
                Scan(__instance);
            }
        }

        // Re-applies the setting to everything seen so far (menu change or config reload)
        internal static void ApplyToAll()
        {
            var gone = new List<Component>();
            foreach (KeyValuePair<Component, Sizes> pair in Known)
            {
                if (pair.Key == null)
                    gone.Add(pair.Key);
                else
                    Apply(pair.Key, pair.Value);
            }
            foreach (Component c in gone)
                Known.Remove(c);
        }

        // A copy of a game text (e.g. a tab label) scales from the same original size as its source
        internal static void RegisterCopy(TMP_Text source, TMP_Text copy)
        {
            if (!Known.TryGetValue(source, out Sizes sizes))
            {
                sizes = new Sizes { Size = source.fontSize, Min = source.fontSizeMin, Max = source.fontSizeMax };
                Remember(source, sizes);
            }
            // The copy has a fixed size: what the source shows at the game's own size (an
            // auto-sized label is never taller than its box, and the box doesn't scale)
            float size = sizes.Size;
            if (source.enableAutoSizing)
                size = Mathf.Min(sizes.Max, sizes.Size);
            float box = ((RectTransform)source.transform).rect.height;
            if (box > 1f)
                size = Mathf.Min(size, box);
            Remember(copy, new Sizes { Size = size, Min = size, Max = size });
        }

        private static void Scan(ComputerUI ui)
        {
            foreach (Text text in ui.GetComponentsInChildren<Text>(true))
            {
                if (!Known.ContainsKey(text) && !Skip(text))
                    Remember(text, new Sizes { Size = text.fontSize, Min = text.resizeTextMinSize, Max = text.resizeTextMaxSize });
            }
            foreach (TMP_Text text in ui.GetComponentsInChildren<TMP_Text>(true))
            {
                if (!Known.ContainsKey(text) && !Skip(text))
                    Remember(text, new Sizes { Size = text.fontSize, Min = text.fontSizeMin, Max = text.fontSizeMax });
            }
        }

        // The compare dropdown is sized as a whole by ParkHistoryPatch
        private static bool Skip(Component c)
        {
            for (Transform t = c.transform; t != null; t = t.parent)
            {
                if (t.name == ParkHistoryPatch.DropDownName)
                    return true;
            }
            return false;
        }

        private static void Remember(Component c, Sizes sizes)
        {
            Known[c] = sizes;
            Apply(c, sizes);
        }

        private static void Apply(Component c, Sizes s)
        {
            float scale = ModConfig.ParkInfoTextScale.Value;
            if (c is Text text)
            {
                text.fontSize = Mathf.Max(1, Mathf.RoundToInt(s.Size * scale));
                text.resizeTextMinSize = Mathf.Max(1, Mathf.RoundToInt(s.Min * scale));
                text.resizeTextMaxSize = Mathf.Max(1, Mathf.RoundToInt(s.Max * scale));
            }
            else if (c is TMP_Text tmp)
            {
                tmp.fontSize = s.Size * scale;
                tmp.fontSizeMin = s.Min * scale;
                tmp.fontSizeMax = s.Max * scale;
            }
        }
    }
}
