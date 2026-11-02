using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ParkasaurusResurrection
{
    // Extra columns in the staff and dino lists (OverviewUI: the Employee View button and Park Info's
    // Staff / Dinosaurs tabs).
    // - Staff: "Occupation", what the employee is doing right now ("Idle, wandering", "Repairing
    //   Wooden Fence in Exhibit 2", ...). Cut off with "..." when it doesn't fit; hovering shows it in full.
    // - Staff and Dinosaurs: "Hat", the icon of the hat they wear ("None" without); hovering shows its
    //   name and effect. The Name columns are narrower to make room.
    // All sort by clicking their header (idle staff first, without a hat last).
    // The game builds a list's header and its 13 rows in OverviewUI.Refresh and fills the rows every
    // frame in ListItemUI.Update. Our cells are extra children of the header and the rows, placed
    // after the column they belong to; the game's own columns keep their indices (sorting, filling).
    public static class OverviewColumnsPatch
    {
        private const int StaffTab = 2, DinoTab = 3;
        // Our columns' sort numbers, beyond the game's own (Employee/Dinosaur.CompareTo)
        private const int OccupationCol = 100, HatCol = 101;
        private const int OccupationWidth = 120, OccupationMinWidth = 70, HatWidth = 30;
        // The game's Name column: staff 100, dinos 80
        private const int StaffNameWidth = 80, DinoNameWidth = 48;

        private enum Kind { Occupation, Hat }

        private struct Column
        {
            public Kind Kind;
            public int After; // the game's column it follows (0 = the row icon)
        }

        // Per list, in the order they're shown; Name is the game's column 1 (staff) or 2 (dinos)
        private static readonly Column[] StaffColumns =
        {
            new Column { Kind = Kind.Hat, After = 1 },        // after Name
            new Column { Kind = Kind.Occupation, After = 2 }, // after Job
        };
        private static readonly Column[] DinoColumns =
        {
            new Column { Kind = Kind.Hat, After = 2 },        // after Name
        };
        private const int StaffNameCol = 1, DinoNameCol = 2;

        // On a row: our cells
        public class ModRow : MonoBehaviour
        {
            public TMP_Text Occupation;
            public HelpTipUI OccupationTip;
            public Image Hat;
            public TMP_Text NoHat;
            public HelpTipUI HatTip;
        }

        private static readonly Func<ListItemUI, Employee> GetEmp =
            AccessTools.MethodDelegate<Func<ListItemUI, Employee>>(AccessTools.Method(typeof(ListItemUI), "GetEmp"));
        private static readonly Func<ListItemUI, Dinosaur> GetDino =
            AccessTools.MethodDelegate<Func<ListItemUI, Dinosaur>>(AccessTools.Method(typeof(ListItemUI), "GetDino"));
        private static readonly AccessTools.FieldRef<OverviewUI, int> CurrentCol =
            AccessTools.FieldRefAccess<OverviewUI, int>("m_currentCol");

        private static bool On => ModConfig.ParkInfoExtraColumns.Value;

        [HarmonyPatch(typeof(OverviewUI), "Refresh")]
        public static class Build
        {
            public static void Postfix(OverviewUI __instance)
            {
                if (!On || (__instance.m_tab != StaffTab && __instance.m_tab != DinoTab))
                    return;
                bool staff = __instance.m_tab == StaffTab;
                int first = FirstHeader(__instance);
                SetWidth(__instance.m_header.GetChild(first + (staff ? StaffNameCol : DinoNameCol)), staff ? StaffNameWidth : DinoNameWidth);
                Transform occupation = null;
                Column[] columns = staff ? StaffColumns : DinoColumns;
                // Last first, so the places of the earlier ones don't shift
                for (int c = columns.Length - 1; c >= 0; c--)
                {
                    Column col = columns[c];
                    Transform header = AddHeader(__instance, first + col.After + 1,
                        col.Kind == Kind.Occupation ? "Occupation" : "Hat",
                        col.Kind == Kind.Occupation ? OccupationCol : HatCol,
                        col.Kind == Kind.Occupation ? OccupationWidth : HatWidth);
                    if (col.Kind == Kind.Occupation)
                        occupation = header;
                }
                if (occupation != null)
                    FitWidth(__instance, first, occupation);
            }
        }

        // Each row as it's made (AddEmp / AddDino, from Refresh)
        [HarmonyPatch(typeof(OverviewUI), nameof(OverviewUI.AddEmp))]
        public static class StaffRow
        {
            public static void Postfix(OverviewUI __instance)
            {
                if (On)
                    AddCells(LastRow(__instance), StaffColumns, StaffNameCol, StaffNameWidth);
            }
        }

        [HarmonyPatch(typeof(OverviewUI), nameof(OverviewUI.AddDino))]
        public static class DinoRow
        {
            public static void Postfix(OverviewUI __instance)
            {
                if (On)
                    AddCells(LastRow(__instance), DinoColumns, DinoNameCol, DinoNameWidth);
            }
        }

        private static ListItemUI LastRow(OverviewUI ui) =>
            ui.m_root.childCount > 0 ? ui.m_root.GetChild(ui.m_root.childCount - 1).GetComponent<ListItemUI>() : null;

        // The headers of this Refresh are the last children: the old ones are only destroyed at the end
        // of the frame (Util.DestroyAllChildren), so they're still there when switching tabs
        private static int FirstHeader(OverviewUI ui) => ui.m_header.childCount - CurrentCol(ui);

        private static Transform AddHeader(OverviewUI ui, int index, string title, int sortCol, int width)
        {
            ui.AddTitleTxt("", width, TextAlignmentOptions.MidlineLeft);
            CurrentCol(ui)--; // not one of the game's columns
            Transform header = ui.m_header.GetChild(ui.m_header.childCount - 1);
            header.GetComponent<ListHeaderUI>().m_index = sortCol;
            header.GetChild(0).GetComponent<TMP_Text>().text = title;
            header.SetSiblingIndex(index);
            return header;
        }

        private static void AddCells(ListItemUI row, Column[] columns, int nameCol, int nameWidth)
        {
            if (row == null)
                return;
            SetWidth(row.transform.GetChild(nameCol), nameWidth);
            bool shown = row.transform.GetChild(0).gameObject.activeSelf;
            var cells = row.gameObject.AddComponent<ModRow>();
            for (int c = columns.Length - 1; c >= 0; c--)
            {
                Column col = columns[c];
                bool occupation = col.Kind == Kind.Occupation;
                GameObject go = UnityEngine.Object.Instantiate(occupation ? Global.Get.m_listTxt : Global.Get.m_listIcon, row.transform);
                go.name = "Mod" + col.Kind;
                SetWidth(go.transform, occupation ? OccupationWidth : HatWidth);
                // Same place as in the header: after the row icon and the game's columns before it
                go.transform.SetSiblingIndex(col.After + 1);
                go.SetActive(shown);
                var tip = go.AddComponent<HelpTipUI>();
                tip.ToolTipString = "";
                if (occupation)
                {
                    cells.Occupation = go.GetComponent<TMP_Text>();
                    cells.OccupationTip = tip;
                    cells.Occupation.text = "";
                    cells.Occupation.alignment = TextAlignmentOptions.MidlineLeft;
                    cells.Occupation.enableWordWrapping = false;
                    cells.Occupation.overflowMode = TextOverflowModes.Ellipsis;
                    cells.Occupation.raycastTarget = true;
                }
                else
                {
                    cells.HatTip = tip;
                    cells.Hat = go.GetComponent<Image>();
                    cells.Hat.preserveAspect = true;
                    cells.Hat.raycastTarget = true;
                    cells.Hat.enabled = false;
                    cells.Hat.color = Color.white; // the list icon is tinted for the dino class icons
                    // "None" when there's no hat
                    GameObject none = UnityEngine.Object.Instantiate(Global.Get.m_listTxt, go.transform);
                    none.name = "None";
                    var noneRect = (RectTransform)none.transform;
                    noneRect.anchorMin = Vector2.zero;
                    noneRect.anchorMax = Vector2.one;
                    noneRect.offsetMin = Vector2.zero;
                    noneRect.offsetMax = Vector2.zero;
                    cells.NoHat = none.GetComponent<TMP_Text>();
                    cells.NoHat.text = "None";
                    cells.NoHat.alignment = TextAlignmentOptions.Midline;
                    cells.NoHat.raycastTarget = false;
                }
            }
        }

        // The staff list is wide already: the Occupation column takes what's left, down to a minimum
        private static void FitWidth(OverviewUI ui, int first, Transform header)
        {
            ListItemUI row = LastRow(ui);
            var layout = ui.m_header.GetComponent<HorizontalLayoutGroup>();
            if (layout == null)
                return;
            float used = layout.padding.horizontal;
            for (int i = first; i < ui.m_header.childCount; i++)
                used += ((RectTransform)ui.m_header.GetChild(i)).sizeDelta.x + layout.spacing;
            float available = ((RectTransform)ui.m_header).rect.width;
            // Rows also end with the Fire button, which has no header
            float rowUsed = 0f, rowAvailable = 0f;
            if (row != null && row.GetComponent<HorizontalLayoutGroup>() is HorizontalLayoutGroup rowLayout)
            {
                rowUsed = rowLayout.padding.horizontal;
                foreach (Transform t in row.transform)
                    rowUsed += ((RectTransform)t).sizeDelta.x + rowLayout.spacing;
                rowAvailable = ((RectTransform)row.transform).rect.width;
            }
            float over = Mathf.Max(used - available, rowAvailable > 0f ? rowUsed - rowAvailable : 0f);
            int width = Mathf.Clamp(Mathf.FloorToInt(OccupationWidth - over), OccupationMinWidth, OccupationWidth);
            Plugin.Log.LogInfo($"Occupation column: header {used:0}/{available:0}, row {rowUsed:0}/{rowAvailable:0} -> width {width}");
            if (width == OccupationWidth)
                return;
            SetWidth(header, width);
            foreach (Transform r in ui.m_root)
            {
                ModRow cells = r.GetComponent<ModRow>(); // also the old rows, which go at the end of the frame
                if (cells != null && cells.Occupation != null)
                    SetWidth(cells.Occupation.transform, width);
            }
        }

        private static void SetWidth(Transform t, float width)
        {
            var rect = (RectTransform)t;
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
        }

        // Fill our cells when the game fills the row (it returns early for hidden rows)
        [HarmonyPatch(typeof(ListItemUI), nameof(ListItemUI.Update))]
        public static class Fill
        {
            public static void Postfix(ListItemUI __instance)
            {
                ModRow cells = __instance.GetComponent<ModRow>();
                if (cells == null || !__instance.transform.GetChild(0).gameObject.activeSelf)
                    return;
                if (__instance.m_emp)
                {
                    Employee emp = GetEmp(__instance);
                    if (emp == null)
                        return;
                    if (cells.Occupation != null)
                    {
                        string text = Occupation(emp);
                        cells.Occupation.text = text;
                        cells.OccupationTip.ToolTipString = text;
                    }
                    ShowHat(cells, emp.m_objectData.m_headItem.GetObj(), true);
                }
                else if (__instance.m_dino)
                {
                    Dinosaur dino = GetDino(__instance);
                    ShowHat(cells, dino != null ? dino.m_objectData.m_headItem.GetObj() : null, false);
                }
            }
        }

        private static void ShowHat(ModRow cells, ZooItemObject hat, bool staff)
        {
            if (cells.Hat == null)
                return;
            cells.Hat.enabled = hat != null && hat.Icon != null;
            cells.NoHat.gameObject.SetActive(hat == null);
            if (hat != null)
                cells.Hat.sprite = hat.Icon;
            cells.HatTip.ToolTipString = hat != null ? HatText(hat, staff) : "";
            cells.HatTip.enabled = hat != null; // no empty tooltip box
        }

        private static string HatText(ZooItemObject hat, bool staff)
        {
            string effect = (staff ? hat.GetHumanHat() : hat.GetDinoString()).TrimEnd('\n');
            return effect.Length > 0 ? hat.GetNameT() + "\n" + effect : hat.GetNameT();
        }

        // Sorting by our columns
        [HarmonyPatch(typeof(Employee), nameof(Employee.CompareTo))]
        public static class SortStaff
        {
            public static void Postfix(Employee __instance, Employee obj, int col, bool reverse, ref int __result)
            {
                if (obj == null)
                    return;
                if (col == HatCol)
                    __result = CompareHats(__instance.m_objectData.m_headItem.GetObj(), obj.m_objectData.m_headItem.GetObj());
                else if (col == OccupationCol)
                {
                    string a = Occupation(__instance), b = Occupation(obj);
                    bool idleA = a.StartsWith(Idle), idleB = b.StartsWith(Idle);
                    __result = idleA != idleB ? (idleA ? -1 : 1) : string.Compare(a, b, StringComparison.CurrentCulture);
                }
                else
                    return;
                if (reverse)
                    __result = -__result;
            }
        }

        [HarmonyPatch(typeof(Dinosaur), nameof(Dinosaur.CompareTo))]
        public static class SortDinos
        {
            public static void Postfix(Dinosaur __instance, Dinosaur obj, int col, bool reverse, ref int __result)
            {
                if (col != HatCol || obj == null)
                    return;
                __result = CompareHats(__instance.m_objectData.m_headItem.GetObj(), obj.m_objectData.m_headItem.GetObj());
                if (reverse)
                    __result = -__result;
            }
        }

        // With a hat first, by the hat's name
        private static int CompareHats(ZooItemObject a, ZooItemObject b) =>
            (a == null) != (b == null) ? (a == null ? 1 : -1)
            : a == null ? 0 : string.Compare(a.GetNameT(), b.GetNameT(), StringComparison.CurrentCulture);

        private const string Idle = "Idle";

        // What the employee is doing, in a few words
        internal static string Occupation(Employee emp)
        {
            if (emp == null)
                return "";
            if (!emp.gameObject.activeInHierarchy)
                return "Off duty";
            if (GameCore.Get.m_pickedUp == emp.gameObject)
                return "Picked up";

            GameObject work = emp.m_objectData.m_workingOn != null && !emp.m_objectData.m_workingOn.IsNull()
                ? emp.m_objectData.m_workingOn.gameobject : null;
            if (work != null)
                return (work.GetComponent<EmployeeLoungeMeta>() != null ? "Resting in " : "Researching in ") + NameOf(work);

            Think think = emp.m_doThisNow ? emp.m_doNow : emp.m_doingThis;
            string target = Target(think);
            switch (think.m_do)
            {
                case AnimalAction.Chill:
                case AnimalAction.Nothing:
                case AnimalAction.Do:
                    switch (emp.m_objectData.m_patrolMode)
                    {
                        case PatrolType.Station: return Idle + ", at their station";
                        case PatrolType.Waypoints: return Idle + ", walking their route";
                        default: return Idle + ", wandering";
                    }
                case AnimalAction.Repair: return "Repairing " + target;
                case AnimalAction.Maintenance: return "Maintaining " + target;
                case AnimalAction.Clean: return "Cleaning " + target;
                case AnimalAction.PickUp: return "Picking up " + target;
                case AnimalAction.DisposeGarbage: return "Emptying " + target;
                case AnimalAction.FillFood: return "Filling " + target;
                case AnimalAction.Heal: return "Healing " + target;
                case AnimalAction.Medicine_A:
                case AnimalAction.Medicine_B:
                case AnimalAction.Medicine_X:
                case AnimalAction.Medicine_Y:
                case AnimalAction.Medicine_General: return "Giving medicine to " + target;
                case AnimalAction.HelpDino: return "Helping " + target;
                case AnimalAction.Shoot: return "Tranquilizing " + target;
                case AnimalAction.Research: return "Researching " + target;
                case AnimalAction.Eat: return "Eating";
                case AnimalAction.Rest: return "Resting";
                default: return target.Length > 0 && target != "something" ? think.m_do + ": " + target : think.m_do.ToString();
            }
        }

        // The thing or dino of a task, with the exhibit it's in
        private static string Target(Think think)
        {
            if (think.m_dinosaur != null)
                return think.m_dinosaur.m_objectData.m_name + Where(think.m_dinosaur.transform.position);
            GameObject thing = think.m_zooThing != null ? think.m_zooThing.gameObject : null;
            if (thing == null && think.m_thing != null && !think.m_thing.IsNull())
                thing = think.m_thing.gameobject;
            if (thing == null)
                return "something";
            Dinosaur dino = thing.GetComponent<Dinosaur>();
            if (dino != null)
                return dino.m_objectData.m_name + Where(thing.transform.position);
            return NameOf(thing) + Where(thing.transform.position);
        }

        private static string NameOf(GameObject thing)
        {
            SaveLoad save = Util.GetSaveRoot(thing);
            GameObject root = save != null ? save.gameObject : thing;
            Place place = root.GetComponent<Place>();
            return place != null ? place.GetParkName() : root.name.Replace("(Clone)", "").Trim();
        }

        private static string Where(Vector3 position)
        {
            Room room = GameCore.Get.GetMyRoom(position);
            if (room == null || room.m_outisde || room.m_roomName == null || string.IsNullOrEmpty(room.m_roomName.m_name))
                return "";
            return " in " + room.m_roomName.m_name;
        }

        // Switched in the options menu: rebuild the lists that are open
        internal static void ApplyEnabled()
        {
            foreach (OverviewUI ui in Resources.FindObjectsOfTypeAll<OverviewUI>())
                if (ui != null && ui.isActiveAndEnabled && (ui.m_tab == StaffTab || ui.m_tab == DinoTab))
                    ui.SetTab(ui.m_tab);
        }

        // Park Info: the Staff tab calls ComputerUI.ShowPane(5), but slot 5 of m_toggles is the
        // Dinosaurs tab (5 and 6 both are; panes 3-6 are the same OverviewUI), so Dinosaurs lit up as
        // well (also without the mod). Pane 4 is the staff list with the Staff tab, as intended.
        private const int StaffPaneWrong = 5, StaffPane = 4;

        [HarmonyPatch(typeof(ComputerUI), nameof(ComputerUI.ShowPane))]
        public static class StaffTabFix
        {
            public static void Prefix(ComputerUI __instance, ref int me)
            {
                if (me == StaffPaneWrong && StaffPane < __instance.m_toggles.Length && __instance.m_toggles[StaffPane] != null &&
                    __instance.m_toggles[StaffPane].name == "Emploee")
                    me = StaffPane;
            }

            // The other tab may still be on from before (the tabs share a ToggleGroup, but are set without notify)
            public static void Postfix(ComputerUI __instance, int me)
            {
                Toggle shown = __instance.m_toggles[me];
                foreach (Toggle toggle in __instance.m_toggles)
                    if (toggle != null && toggle != shown && toggle.isOn)
                        toggle.SetIsOnWithoutNotify(false);
            }
        }

        internal static readonly Type[] Patches =
        {
            typeof(StaffTabFix), typeof(Build), typeof(StaffRow), typeof(DinoRow), typeof(Fill), typeof(SortStaff), typeof(SortDinos)
        };
    }
}
