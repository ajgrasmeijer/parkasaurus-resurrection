using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Painting terrain (forest, swamp, desert, ocean ground) with Shift held only changes tiles in
    // the same area as the tile where the drag started: inside that exhibit, or, when the drag
    // starts outside any exhibit, only tiles outside exhibits. So a brush stroke can go over the
    // fence without painting the path or the neighbouring exhibit.
    //
    // Terrain tiles are dragged as a rectangle (Place.PlaceType.Pool in UICore.ReadyPlaceEnd): the
    // preview draws the rectangle's outline with DrawTerrainBlock, the release places each tile with
    // FinalizeReadyPlace. With Shift, the outline is replaced by all tiles that will really change
    // (drawn in UICore.LateUpdate, after the drag code ran), and the other tiles aren't placed.
    public static class TerrainPaintPatch
    {
        private static readonly AccessTools.FieldRef<UICore, Place> HoldingPlace =
            AccessTools.FieldRefAccess<UICore, Place>("m_holdingPlace");
        private static readonly AccessTools.FieldRef<UICore, Vector3> PlaceStart =
            AccessTools.FieldRefAccess<UICore, Vector3>("m_placeStart");

        // Larger rectangles only get their outline, to keep the preview cheap
        private const int MaxFilledTiles = 4096;

        private static readonly List<Vector3> Outline = new List<Vector3>();
        private static int s_outlineFrame = -1;
        private static Color s_color;
        private static bool s_drawingOwn;

        // Collects the outline the game draws this frame instead of drawing it
        [HarmonyPatch(typeof(UICore), nameof(UICore.DrawTerrainBlock))]
        public static class Preview
        {
            public static bool Prefix(UICore __instance, Vector3 worldPos, Color col)
            {
                if (s_drawingOwn || !Active(__instance))
                    return true;
                if (s_outlineFrame != Time.frameCount)
                {
                    s_outlineFrame = Time.frameCount;
                    Outline.Clear();
                    s_color = col;
                }
                Outline.Add(worldPos);
                return false;
            }
        }

        [HarmonyPatch(typeof(UICore), nameof(UICore.LateUpdate))]
        public static class DrawArea
        {
            public static void Postfix(UICore __instance)
            {
                if (s_outlineFrame != Time.frameCount || Outline.Count == 0)
                    return;
                int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
                foreach (Vector3 p in Outline)
                {
                    int x = Mathf.RoundToInt(p.x), z = Mathf.RoundToInt(p.z);
                    minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                    minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                }
                Vector3 start = PlaceStart(__instance);
                bool fill = (maxX - minX + 1) * (maxZ - minZ + 1) <= MaxFilledTiles;
                s_drawingOwn = true;
                try
                {
                    if (fill)
                    {
                        for (int x = minX; x <= maxX; x++)
                            for (int z = minZ; z <= maxZ; z++)
                            {
                                var pos = new Vector3(x, 0f, z);
                                if (SameArea(start, pos))
                                    __instance.DrawTerrainBlock(pos, s_color);
                            }
                    }
                    else
                    {
                        foreach (Vector3 p in Outline)
                            if (SameArea(start, p))
                                __instance.DrawTerrainBlock(p, s_color);
                    }
                }
                finally
                {
                    s_drawingOwn = false;
                    Outline.Clear();
                }
            }
        }

        [HarmonyPatch(typeof(UICore), nameof(UICore.FinalizeReadyPlace))]
        public static class Placing
        {
            public static bool Prefix(UICore __instance, GameObject obj, Vector3 pos, ref GameObject __result)
            {
                if (obj != __instance.m_holding || Allowed(__instance, pos))
                    return true;
                __result = null; // the brush loop ignores the result
                return false;
            }
        }

        private static bool Allowed(UICore ui, Vector3 pos) => !Active(ui) || SameArea(PlaceStart(ui), pos);

        // Shift held while holding a terrain tile
        private static bool Active(UICore ui)
        {
            if (!ModConfig.ShiftPaintsOneArea.Value || UnityEngine.InputSystem.Keyboard.current?.shiftKey.isPressed != true)
                return false;
            Place place = HoldingPlace(ui);
            return place != null && place.m_placeType == Place.PlaceType.Pool && IsTerrain(place.PathType);
        }

        // The ground types the game itself treats as terrain (see the pool code in ReadyPlaceEnd)
        internal static bool IsTerrain(GroundType type) =>
            (type >= GroundType.Forest && type <= GroundType.Desert) || type == GroundType.Ocean;

        private static bool SameArea(Vector3 start, Vector3 pos)
        {
            Room startRoom = ExhibitAt(start);
            Room room = ExhibitAt(pos);
            return !IsFence(pos) && room == startRoom;
        }

        // The exhibit a tile belongs to, or null outside exhibits (or off the map)
        private static Room ExhibitAt(Vector3 pos)
        {
            GameCore core = GameCore.Get;
            int x = Mathf.RoundToInt(pos.x), z = Mathf.RoundToInt(pos.z);
            if (core == null || x < 0 || z < 0 || x >= core.m_gridSize || z >= core.m_gridSize)
                return null;
            TileBlockRoom tile = core.m_groundRoom[x, z];
            return tile.HasRoom() ? tile.m_region.m_room : null;
        }

        // Tiles under a fence belong to neither side
        private static bool IsFence(Vector3 pos)
        {
            GameCore core = GameCore.Get;
            int x = Mathf.RoundToInt(pos.x), z = Mathf.RoundToInt(pos.z);
            return core != null && x >= 0 && z >= 0 && x < core.m_gridSize && z < core.m_gridSize &&
                   core.m_wallGrid[x, z] != null;
        }
    }
}
