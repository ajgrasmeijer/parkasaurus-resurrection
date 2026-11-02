using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ParkasaurusResurrection
{
    // Quiet objects and the Move Item tool.
    // - Quiet objects: everything from the Scenery and Dinosaurs build tabs (trees, bushes, rocks,
    //   theme items, sheds, toys, nests, ... and their upgrades; not feeders, their food is set in their
    //   panel), the Decoration, Lights and
    //   Bridges groups of the Buildings tab, the simple facilities (QuietFacilities) and doors can't be clicked
    //   or hovered during normal play. Their click collider (the SelectPark object on the Selector
    //   layer, only used by the mouse) is switched off, so clicks go through to what's behind them.
    //   With a Destruction tool or the Move Item tool active they're clickable as usual.
    // - Move Item: an extra tool in the Destruction tab (a copy of Remove Item, with the icon of the
    //   game's own Move button). Clicking an object picks it up with the game's own move
    //   (UICore.MoveBuilding, what the Move button in the object panel does). After it's placed the
    //   tool stays active; right-click or Esc stops it. It takes everything with a place in the park
    //   (buildings, trees, map plants that only offer Destroy) except fences, paths and pools. The game's focus corners on the moved object are
    //   cleared once it's placed or the tool stops.
    public static class MoveToolPatch
    {
        private const int DestructionTab = 6;

        internal static bool Active { get; private set; }
        private static bool s_resumeAfterMove;
        private static bool s_pickingUp;
        private static GameObject s_moved;
        private static GameObject s_toolPrefab;
        private static HashSet<GameObject> s_quietPrefabs;
        private static readonly List<SelectPark> Quiet = new List<SelectPark>();
        private static bool? s_interactive;

        // Marks the copy of the Remove Item tool that is the Move Item tool
        public class MoveToolMarker : MonoBehaviour { }

        // --- The tool in the Destruction tab ---

        [HarmonyPatch(typeof(ToolUI), "BuildItems")]
        public static class AddTool
        {
            public static void Prefix(int build)
            {
                if (build == DestructionTab)
                    EnsureTool();
            }
        }

        private static void EnsureTool()
        {
            if (s_toolPrefab != null)
                return;
            TabList tabs = Global.Get.m_buildTabs[DestructionTab];
            foreach (SubGroup group in tabs.Tabs)
            {
                if (group.SubObjects == null)
                    continue;
                if (group.SubObjects.Any(o => o != null && o.GetComponent<MoveToolMarker>()))
                    return;
                int at = System.Array.FindIndex(group.SubObjects, o => o != null && o.GetComponent<Place>() != null &&
                                                                       o.GetComponent<Place>().m_placeType == Place.PlaceType.Delete);
                if (at < 0)
                    continue;

                // Copy under an inactive holder, so none of the copy's scripts start
                var holder = new GameObject("ModMoveToolHolder");
                holder.SetActive(false);
                Object.DontDestroyOnLoad(holder);
                GameObject tool = Object.Instantiate(group.SubObjects[at], holder.transform);
                tool.name = "Move Item";
                tool.AddComponent<MoveToolMarker>();
                Place place = tool.GetComponent<Place>();
                place.m_defaultName = "Move Item";
                Sprite icon = MoveIcon();
                if (icon != null)
                    place.m_previewImg = new[] { icon };

                var list = group.SubObjects.ToList();
                list.Insert(at + 1, tool);
                group.SubObjects = list.ToArray();
                s_toolPrefab = tool;
                return;
            }
            Plugin.Log.LogWarning("Move Item tool: Remove Item not found in the Destruction tab");
        }

        // The icon of the Move button in the game's object panel
        private static Sprite MoveIcon()
        {
            foreach (ZooObjectPaneUI pane in Resources.FindObjectsOfTypeAll<ZooObjectPaneUI>())
            {
                if (pane.m_moveButton == null)
                    continue;
                Image image = pane.m_moveButton.GetComponentsInChildren<Image>(true).LastOrDefault(i => i.sprite != null);
                if (image != null)
                    return image.sprite;
            }
            return null;
        }

        // Choosing the tool, or anything else
        [HarmonyPatch(typeof(UICore), nameof(UICore.HoldThis))]
        public static class Choose
        {
            public static bool Prefix(UICore __instance, GameObject me)
            {
                if (me != null && me.GetComponent<MoveToolMarker>() != null)
                {
                    __instance.HoldThis(null, null, saveHold: false);
                    Active = true;
                    s_resumeAfterMove = false;
                    return false;
                }
                if (me != null)
                {
                    // While an object hangs on the mouse the tool is paused; it comes back after placing.
                    // Choosing anything else from a menu ends it.
                    Active = false;
                    if (!s_pickingUp)
                        s_resumeAfterMove = false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(UICore), nameof(UICore.DeleteMode))]
        public static class OtherTool
        {
            public static void Prefix() { Active = false; Unfocus(); }
        }

        // Opening, closing or switching a build tab (also the inventory) ends the tool
        [HarmonyPatch(typeof(UICore), nameof(UICore.SetBuildPane))]
        public static class OtherTab
        {
            public static void Prefix() => Stop();
        }

        // Backing out of carrying a picked-up object (Esc, right-click) ends the tool instead of resuming it
        [HarmonyPatch(typeof(UICore), nameof(UICore.CleanUpReadys))]
        public static class BackOut
        {
            public static void Postfix(bool backout)
            {
                if (backout && !s_pickingUp)
                    Stop();
            }
        }

        private static void Stop()
        {
            Active = false;
            s_resumeAfterMove = false;
            Unfocus();
        }

        // The game keeps the moved object focused (corners around it); only clear the focus we set
        private static void Unfocus()
        {
            UICore ui = UICore.Get;
            if (s_moved != null && ui != null && ui.m_focused == s_moved)
                ui.SetFocused(null);
            s_moved = null;
        }

        // The game sends clicks and hovers to two handlers: SelectPark (the Selector collider) and
        // ZooThing (on the object itself, with its own colliders). Quiet objects are skipped in both;
        // a click on a quiet object goes on to the first not-quiet thing behind it (the ZooThing
        // colliders stay on, so otherwise they'd swallow clicks on vets in exhibits or employees in
        // a lounge). With the Move tool the click is handled in EveryFrame (like Remove Item, the game's
        // tools read the hovered object and the mouse button instead of click events).
        [HarmonyPatch(typeof(SelectPark), nameof(SelectPark.OnPointerClick))]
        public static class SelectClick
        {
            public static bool Prefix(SelectPark __instance, PointerEventData eventData) => AllowClick(__instance.m_root, eventData);
        }

        [HarmonyPatch(typeof(SelectPark), nameof(SelectPark.OnPointerEnter))]
        public static class SelectHover
        {
            public static bool Prefix(SelectPark __instance) => Allow(__instance.m_root, click: false);
        }

        [HarmonyPatch(typeof(ZooThing), nameof(ZooThing.OnPointerClick))]
        public static class ThingClick
        {
            public static bool Prefix(ZooThing __instance, PointerEventData eventData) => AllowClick(__instance.gameObject, eventData);
        }

        [HarmonyPatch(typeof(ZooThing), nameof(ZooThing.OnPointerEnter))]
        public static class ThingHover
        {
            public static bool Prefix(ZooThing __instance) => Allow(__instance.gameObject, click: false);
        }

        private static bool Allow(GameObject root, bool click)
        {
            if (root == null)
                return true;
            if (Active && click)
                return false;
            if (!ModConfig.QuietObjects.Value || s_interactive != false)
                return true;
            return !IsQuietCached(root);
        }

        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        private static bool AllowClick(GameObject root, PointerEventData eventData)
        {
            if (Allow(root, click: true))
                return true;
            if (!Active && eventData != null && EventSystem.current != null)
                PassClickOn(eventData);
            return false;
        }

        // Sends the click to the nearest handler under the mouse that isn't a quiet object
        private static void PassClickOn(PointerEventData eventData)
        {
            Hits.Clear();
            EventSystem.current.RaycastAll(eventData, Hits);
            foreach (RaycastResult hit in Hits)
            {
                GameObject handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
                if (handler == null || IsQuietHandler(handler))
                    continue;
                eventData.pointerPress = handler;
                ExecuteEvents.Execute(handler, eventData, ExecuteEvents.pointerClickHandler);
                break;
            }
            Hits.Clear();
        }

        private static bool IsQuietHandler(GameObject handler)
        {
            SelectPark select = handler.GetComponent<SelectPark>();
            if (select != null)
                return select.m_root != null && IsQuietCached(select.m_root);
            return handler.GetComponent<ZooThing>() != null && IsQuietCached(handler);
        }

        private static readonly Dictionary<int, bool> QuietCache = new Dictionary<int, bool>();

        private static bool IsQuietCached(GameObject root)
        {
            int id = root.GetInstanceID();
            if (!QuietCache.TryGetValue(id, out bool quiet))
                QuietCache[id] = quiet = IsQuiet(root);
            return quiet;
        }

        // Everything with a place in the park except fences, paths and pools (the game's move can't handle those)
        private static bool IsMovable(GameObject root)
        {
            if (root == null || root.GetComponent<SaveLoad>() == null || root.GetComponent<SubFence>() != null)
                return false;
            Place place = root.GetComponent<Place>();
            return place != null && place.m_placeType != Place.PlaceType.Path && place.m_placeType != Place.PlaceType.Pool;
        }

        private static readonly RaycastHit[] RayHits = new RaycastHit[16];

        // The object under the mouse: the game's hover, or else the nearest movable thing the mouse ray hits
        // (the game only sets the hover for some kinds of objects, and not for every collider of a building)
        private static GameObject Target(UICore ui)
        {
            if (IsMovable(ui.m_hover))
                return ui.m_hover;
            Camera cam = GameCore.Get != null ? GameCore.Get.m_mainCamera : null;
            if (cam == null)
                return null;
            Ray ray = cam.ScreenPointToRay(Global.Get.GetMouse());
            int count = Physics.RaycastNonAlloc(ray, RayHits, 600f, Global.Get.m_selectorMask.value | Global.Get.m_zooObjectMask.value,
                                                QueryTriggerInteraction.Collide);
            GameObject best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                SelectPark select = RayHits[i].collider.GetComponent<SelectPark>();
                GameObject root = select != null && select.m_root != null ? select.m_root
                    : RayHits[i].collider.GetComponentInParent<Place>()?.gameObject;
                if (RayHits[i].distance < bestDist && IsMovable(root))
                {
                    best = root;
                    bestDist = RayHits[i].distance;
                }
            }
            return best;
        }

        private static void PickUp(UICore ui, GameObject root)
        {
            ui.SetFocused(root);
            s_moved = root;
            s_resumeAfterMove = true;
            s_pickingUp = true;
            // Plants that were part of the map only offer Destroy in the game; the tool moves them too
            DecorationPlantMeta plant = root.GetComponent<DecorationPlantMeta>();
            bool cantMove = plant != null && plant.m_cantMove;
            if (cantMove)
                plant.m_cantMove = false;
            try { ui.MoveBuilding(); }
            finally
            {
                s_pickingUp = false;
                if (cantMove)
                    plant.m_cantMove = true;
            }
            if (ui.m_holding == null)
            {
                s_resumeAfterMove = false; // the game refused (broken, can't be moved)
                Place place = root.GetComponent<Place>();
                Plugin.Log.LogInfo($"Move Item: the game won't move {root.name} (can delete: {place.m_canDelete}, " +
                                   $"moving: {place.m_movingObject}, booth: {root.GetComponent<AdmissionBoothMeta>() != null})");
                Unfocus();
            }
        }

        // The tiles under the hovered object, like Remove Item shows them
        private static void DrawHover(UICore ui, GameObject hover)
        {
            Bounds b = Util.GetChildRendererBounds(hover);
            Color col = new Color(0.55f, 0.78f, 0.25f, 0.6f);
            bool any = false;
            for (int x = Mathf.FloorToInt(b.min.x); x <= Mathf.CeilToInt(b.max.x); x++)
                for (int z = Mathf.FloorToInt(b.min.z); z <= Mathf.CeilToInt(b.max.z); z++)
                    if (GameCore.Get.GetTileGameObject(x, z) == hover)
                    {
                        ui.DrawTerrainBlock(new Vector3(x, 0f, z), col);
                        any = true;
                    }
            // Things that don't claim tiles (some trees and plants): the tile they stand on
            if (!any)
            {
                Vector3 p = hover.transform.position;
                ui.DrawTerrainBlock(new Vector3(Mathf.Round(p.x), 0f, Mathf.Round(p.z)), col);
            }
        }

        // --- Quiet objects ---

        [HarmonyPatch(typeof(SelectPark), "Start")]
        public static class Register
        {
            public static void Postfix(SelectPark __instance)
            {
                if (__instance == null || __instance.m_root == null || !IsQuiet(__instance.m_root))
                    return;
                Quiet.Add(__instance);
                if (s_interactive.HasValue)
                    SetColliders(__instance, s_interactive.Value);
            }
        }

        private static bool IsQuiet(GameObject root)
        {
            if (root.GetComponent<RootDoor>() != null || root.GetComponent<Door>() != null)
                return true;
            SaveLoad save = root.GetComponent<SaveLoad>();
            if (save == null || save.m_objectData == null || SuperLoader.Get == null)
                return false;
            if (s_quietPrefabs == null)
                s_quietPrefabs = QuietPrefabs();
            GameObject prefab;
            try { prefab = SuperLoader.Get.GetPrefabByHash(save.m_objectData.m_hash); }
            catch (System.Exception) { return false; }
            return prefab != null && s_quietPrefabs.Contains(prefab);
        }

        // Groups of the Buildings tab that are quiet as a whole (SubGroup.Name)
        private static readonly string[] QuietGroups = { "Decoration", "Lights", "Bridges" };

        // Facilities without anything to look at or set (prefab names, also matches their upgrades)
        private static readonly string[] QuietFacilities = { "Bench", "GarbageBin", "WaterHeater", "Gazebo", "PicnicTable" };

        // Everything in the Scenery and Dinosaurs build tabs except feeders, the quiet groups and facilities, with their upgrades
        private static HashSet<GameObject> QuietPrefabs()
        {
            var set = new HashSet<GameObject>();
            string[] captions = ToolUI.Get != null ? ToolUI.Get.m_buildToppersCaption : null;
            for (int i = 0; i < Global.Get.m_buildTabs.Length; i++)
            {
                string caption = captions != null && i < captions.Length ? captions[i].ToUpperInvariant() : "";
                bool wholeTab = caption.Length > 0 ? caption.StartsWith("SCENER") || caption.StartsWith("DINO") : i == 2 || i == 3;
                foreach (SubGroup group in Global.Get.m_buildTabs[i].Tabs)
                    if (group.SubObjects != null)
                        foreach (GameObject prefab in group.SubObjects)
                        {
                            if (prefab == null || prefab.GetComponentInChildren<FoodBucketMeta>(true) != null || !(wholeTab || QuietGroups.Contains(group.Name) ||
                                                    (group.Name == "Facilities" && QuietFacilities.Any(f => prefab.name.StartsWith(f)))))
                                continue;
                            for (GameObject p = prefab; p != null && set.Add(p);)
                            {
                                BuildingMeta meta = p.GetComponent<BuildingMeta>();
                                p = meta != null && meta.m_nextUpgrade != null ? meta.m_nextUpgrade.gameObject : null;
                            }
                        }
            }
            Plugin.Log.LogInfo($"Quiet objects: {set.Count} kinds " +
                               $"(tabs: {(captions != null ? string.Join(", ", captions) : "unknown")}): " +
                               string.Join(", ", set.Select(p => p.GetComponent<Place>() != null ? p.GetComponent<Place>().m_defaultName : p.name)));
            return set;
        }

        // Every frame: switch the quiet objects' click colliders when a tool starts or stops
        [HarmonyPatch(typeof(UICore), nameof(UICore.LateUpdate))]
        public static class EveryFrame
        {
            public static void Postfix(UICore __instance)
            {
                UICore ui = __instance;
                if (Active)
                {
                    var input = Global.Get.m_parkinput.UI;
                    // A left click in the world also counts as backing out for the game (m_touchGround); for the
                    // tool only Esc and right-click do
                    bool click = input.ClickFirst.triggered || input.ClickDownHeld.triggered;
                    if (ui.m_holding == null && ui.WantToBackOut() && !click)
                        Stop();
                    else if (ui.m_holding == null && !ui.isOverUI() && Target(ui) is GameObject target)
                    {
                        DrawHover(ui, target);
                        if (input.ClickDownHeld.triggered)
                            PickUp(ui, target);
                    }
                }
                if (s_resumeAfterMove && ui.m_holding == null)
                {
                    s_resumeAfterMove = false;
                    Active = true;
                    Unfocus();
                }

                bool interactive = !ModConfig.QuietObjects.Value || ui.m_deleting || Active || s_resumeAfterMove;
                if (s_interactive == interactive)
                    return;
                s_interactive = interactive;
                Quiet.RemoveAll(s => s == null);
                foreach (SelectPark select in Quiet)
                    SetColliders(select, interactive);
                if (!interactive && ui.m_hover != null && Quiet.Any(s => s.m_root == ui.m_hover))
                    ui.m_hover = null;
            }
        }

        private static void SetColliders(SelectPark select, bool on)
        {
            foreach (Collider c in select.GetComponents<Collider>())
                c.enabled = on;
        }

        // Called when a park is closed
        internal static void Reset()
        {
            Quiet.Clear();
            QuietCache.Clear();
            s_quietPrefabs = null;
            s_interactive = null;
            Stop();
        }
    }
}
