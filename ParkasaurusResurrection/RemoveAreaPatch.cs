using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Destruction tools, "Remove Item": drag a rectangle to mark everything in it, like the other
    // destruction tools. Unlike those, nothing is removed yet: the marked objects join the game's own
    // selection and are removed with its "Destroy N objects?" button, as before.
    // - Drag starting on empty ground, or with Shift held anywhere: add the rectangle to the selection.
    // - Hold Alt (the game's own unmark key in this tool) or Ctrl while dragging: take the rectangle
    //   out of the selection.
    // - Click or sweep over objects without Shift: the game's own marking, unchanged.
    // Same objects as the game allows: no visitors, dinos, staff, eggs or the rocket.
    //
    // The game's tool is a coroutine (UICore.ReadyDestroy, m_deletingPool == 0) that marks whatever is
    // under the mouse while the button is held. This runs after it, in UICore.LateUpdate, and each
    // frame of a drag sets the selection to: selection before the drag + (or -) the rectangle.
    public static class RemoveAreaPatch
    {
        private static bool s_dragging;
        private static bool s_removing;
        private static Vector3 s_start;
        private static readonly List<GameObject> s_before = new List<GameObject>();
        private static readonly List<GameObject> s_lastFrame = new List<GameObject>();
        private static readonly HashSet<GameObject> s_inRect = new HashSet<GameObject>();

        private static readonly AccessTools.FieldRef<UICore, UICore.UICallback> SelectionChanged =
            AccessTools.FieldRefAccess<UICore, UICore.UICallback>("OnSelectionChange");

        [HarmonyPatch(typeof(UICore), nameof(UICore.LateUpdate))]
        public static class AfterTool
        {
            public static void Postfix(UICore __instance)
            {
                UICore ui = __instance;
                if (!ModConfig.RemoveItemArea.Value || !ui.m_deleting || ui.m_deletingPool != 0 || Global.Get.m_controllerMode)
                {
                    s_dragging = false;
                    s_lastFrame.Clear();
                    return;
                }

                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                bool shift = keyboard != null && keyboard.shiftKey.isPressed;
                bool ctrl = keyboard != null && (keyboard.ctrlKey.isPressed || keyboard.altKey.isPressed);

                if (!s_dragging && Global.Get.m_parkinput.UI.ClickDownHeld.triggered && !ui.isOverUI())
                {
                    // The game has already marked what's under the mouse this frame; a rectangle
                    // starts from the selection of the frame before
                    bool onObject = ui.m_hover != null && Removable(ui.m_hover);
                    if (shift || ctrl || !onObject)
                    {
                        s_dragging = true;
                        s_removing = ctrl;
                        s_start = Grid(ui.GetMouseOnTerrainGrid());
                        s_before.Clear();
                        s_before.AddRange(s_lastFrame);
                    }
                }

                if (s_dragging)
                {
                    Vector3 end = Grid(ui.GetMouseOnTerrainGrid());
                    Collect(s_start, end);
                    Apply(ui);
                    DrawOutline(ui, s_start, end);
                    if (Global.Get.IsMouseUp() || !Global.Get.m_parkinput.UI.ClickDownHeld.IsPressed())
                    {
                        s_dragging = false;
                        GameCore.Get.PlaySound(Global2.Get.m_soundUIMarkDelete);
                    }
                }

                s_lastFrame.Clear();
                s_lastFrame.AddRange(ui.m_destroyNext);
            }
        }

        private static Vector3 Grid(Vector3 p) => new Vector3(Mathf.Round(p.x), 0f, Mathf.Round(p.z));

        private static void Collect(Vector3 a, Vector3 b)
        {
            s_inRect.Clear();
            int x0 = (int)Mathf.Min(a.x, b.x), x1 = (int)Mathf.Max(a.x, b.x);
            int z0 = (int)Mathf.Min(a.z, b.z), z1 = (int)Mathf.Max(a.z, b.z);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    GameObject block = GameCore.Get.GetTileGameObject(x, z);
                    if (block != null && Removable(block))
                        s_inRect.Add(block);
                }
            // Junk and debris don't take up tiles
            foreach (GarbageMeta junk in Object.FindObjectsOfType<GarbageMeta>())
                AddIfInside(junk.gameObject, x0, x1, z0, z1);
            foreach (DebrisMeta debris in Object.FindObjectsOfType<DebrisMeta>())
                AddIfInside(debris.gameObject, x0, x1, z0, z1);
        }

        private static void AddIfInside(GameObject go, int x0, int x1, int z0, int z1)
        {
            Vector3 p = go.transform.position;
            int x = Mathf.RoundToInt(p.x), z = Mathf.RoundToInt(p.z);
            if (x >= x0 && x <= x1 && z >= z0 && z <= z1 && Removable(go))
                s_inRect.Add(go);
        }

        // Same check as the game's Remove Item tool
        private static bool Removable(GameObject go) =>
            !go.GetComponent<RocketMeta>() && !go.GetComponent<Human>() && !go.GetComponent<Dinosaur>()
            && !go.GetComponent<Employee>() && !go.GetComponent<Egg>();

        // Selection = before the drag + rectangle (or - rectangle with Ctrl)
        private static void Apply(UICore ui)
        {
            var wanted = new List<GameObject>();
            foreach (GameObject go in s_before)
                if (go != null && !(s_removing && s_inRect.Contains(go)))
                    wanted.Add(go);
            if (!s_removing)
                foreach (GameObject go in s_inRect)
                    if (!wanted.Contains(go))
                        wanted.Add(go);

            bool changed = wanted.Count != ui.m_destroyNext.Count;
            foreach (GameObject go in ui.m_destroyNext)
                if (go != null && !wanted.Contains(go))
                {
                    go.GetComponent<Place>()?.SelectDelete(set: false);
                    changed = true;
                }
            foreach (GameObject go in wanted)
                if (!ui.m_destroyNext.Contains(go))
                {
                    go.GetComponent<Place>()?.SelectDelete(set: true);
                    changed = true;
                }
            if (!changed)
                return;
            ui.m_destroyNext.Clear();
            ui.m_destroyNext.AddRange(wanted);
            ui.m_destroyBoxText.text = GameCore.Translate("Destroy {1} objects?", wanted.Count.ToString());
            SelectionChanged(ui)?.Invoke();
        }

        private static void DrawOutline(UICore ui, Vector3 a, Vector3 b)
        {
            Color col = ui.m_heatMap != null ? ui.m_heatMap.Evaluate(0f) : Color.red;
            col.a = 0.3f;
            int x0 = (int)Mathf.Min(a.x, b.x), x1 = (int)Mathf.Max(a.x, b.x);
            int z0 = (int)Mathf.Min(a.z, b.z), z1 = (int)Mathf.Max(a.z, b.z);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    if (x == x0 || x == x1 || z == z0 || z == z1)
                        ui.DrawTerrainBlock(new Vector3(x, 0f, z), col);
        }
    }
}
