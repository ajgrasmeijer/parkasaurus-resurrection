using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Builds every terrain tile from 4 triangles around a center point (by default the average
    // of the 4 corners, see EdgeShape) instead of 2 triangles with a fixed diagonal. Slopes become symmetric instead
    // of always folding along the same diagonal.
    //
    // This is a copy of SuperTerrain.BuildChunk with a 5th (center) vertex per tile; colors,
    // path UVs, biome data and the normal map are computed exactly like the original.
    // The shader takes vertex heights from a GPU buffer, not from the mesh; the center heights
    // are provided by TerrainCenterHeightPatch. Only the visible terrain changes: the
    // TerrainCollider (mouse/placement) and the NavMesh still use the game's own terrain.
    [HarmonyPatch(typeof(SuperTerrain), "BuildChunk")]
    public static class TerrainQuadPatch
    {
        private const int ChunkSize = 64;
        private const int VertsPerTile = 5;
        private const int VertCount = ChunkSize * ChunkSize * VertsPerTile; // 20480, fits 16-bit indices

        private static readonly Vector3[] Verts = new Vector3[VertCount];
        private static readonly Vector3[] Normals = new Vector3[VertCount];
        private static readonly Color[] Colors = new Color[VertCount];
        private static readonly Vector2[] Uv = new Vector2[VertCount];
        private static readonly Vector2[] Uv2 = new Vector2[VertCount];
        private static readonly Vector2[] Uv3 = new Vector2[VertCount];
        private static readonly Vector4[] Tangents = new Vector4[VertCount];
        private static int[] s_triangles;

        // Meshes that already got our layout, with the tangents we built for them. The game never
        // sets tangents itself (they come from the original mesh asset) but the shader needs them
        // for normal mapping, so they're carried over from the original 4-vertex layout.
        private static readonly Dictionary<int, Vector4[]> PreparedMeshes = new Dictionary<int, Vector4[]>();

        // Called when a park is closed: the terrain meshes are destroyed with it
        internal static void Reset()
        {
            PreparedMeshes.Clear();
            s_activeThisPark = null;
        }

        // The setting is read once per park: the terrain meshes and the enlarged height buffer
        // (TerrainCenterHeightPatch) are made when the park loads, and must stay as they are
        private static bool? s_activeThisPark;
        internal static bool ActiveThisPark
        {
            get
            {
                if (s_activeThisPark == null)
                    s_activeThisPark = ModConfig.QuadTerrainTiles.Value;
                return s_activeThisPark.Value;
            }
        }

        private static readonly Action<SuperTerrain, int, int, Vector4> SetNormal =
            AccessTools.MethodDelegate<Action<SuperTerrain, int, int, Vector4>>(AccessTools.Method(typeof(SuperTerrain), "SetNormal"));
        private static readonly Func<SuperTerrain, Vector3, Vector4, Vector2> PrePath =
            AccessTools.MethodDelegate<Func<SuperTerrain, Vector3, Vector4, Vector2>>(AccessTools.Method(typeof(SuperTerrain), "PrePath"));
        private static readonly AccessTools.FieldRef<SuperTerrain, Color32[]> NormalColors =
            AccessTools.FieldRefAccess<SuperTerrain, Color32[]>("m_normalColors");

        public static bool Prefix(SuperTerrain __instance, MeshFilter meshFilter, int baseX, int baseY)
        {
            if (!ActiveThisPark)
                return true;
            SuperTerrain t = __instance;
            Mesh mesh = meshFilter.mesh;
            GameCore core = GameCore.Get;
            int num = 0;

            for (int i = baseX; i < baseX + ChunkSize; i++)
            {
                for (int j = baseY; j < baseY + ChunkSize; j++)
                {
                    // Corner heights and their neighbours (for smooth normals), as in the original
                    float n2 = t.GetHeightWithColor(i - 1, j) * 64f;
                    float n3 = t.GetHeightWithColor(i, j - 1) * 64f;
                    float n4 = t.GetHeightWithColor(i + 1, j - 1) * 64f;
                    float n5 = t.GetHeightWithColor(i + 2, j) * 64f;
                    float n6 = t.GetHeightWithColor(i - 1, j + 1) * 64f;
                    float n7 = t.GetHeightWithColor(i, j + 2) * 64f;
                    float n8 = t.GetHeightWithColor(i + 1, j + 2) * 64f;
                    float n9 = t.GetHeightWithColor(i + 2, j + 1) * 64f;
                    float h00 = t.GetHeightWithColor(i, j) * 64f;
                    float h10 = t.GetHeightWithColor(i + 1, j) * 64f;
                    float h01 = t.GetHeightWithColor(i, j + 1) * 64f;
                    float h11 = t.GetHeightWithColor(i + 1, j + 1) * 64f;
                    float hCenter = TerrainCenterHeightPatch.CenterHeight(h00, h10, h01, h11);

                    Color color = core.GetBlockColorAt(i, j - 1);
                    Color color2 = core.GetBlockColorAt(i - 1, j - 1);
                    if (color2.r == color.r || color2.r <= 0f)
                        color2 = core.GetBlockColorAt(i, j);
                    if (color2.r == color.r || color2.r <= 0f)
                        color2 = core.GetBlockColorAt(i + 1, j - 1);
                    if (color2.r == color.r || color2.r <= 0f)
                        color2 = core.GetBlockColorAt(i, j - 2);

                    Verts[num] = new Vector3(i - 0.5f, h00, j - 1.5f);
                    Verts[num + 1] = new Vector3(i + 0.5f, h10, j - 1.5f);
                    Verts[num + 2] = new Vector3(i - 0.5f, h01, j - 0.5f);
                    Verts[num + 3] = new Vector3(i + 0.5f, h11, j - 0.5f);
                    Verts[num + 4] = new Vector3(i, hCenter, j - 1f);

                    Normals[num] = new Vector3(-(h10 - n2), 4f, -(h01 - n3)).normalized;
                    Normals[num + 1] = new Vector3(-(n5 - h00), 4f, -(h11 - n4)).normalized;
                    Normals[num + 2] = new Vector3(-(h11 - n6), 4f, -(n7 - h00)).normalized;
                    Normals[num + 3] = new Vector3(-(n9 - h01), 4f, -(n8 - h10)).normalized;
                    Vector3 avg = (Normals[num] + Normals[num + 1] + Normals[num + 2] + Normals[num + 3]) / 4f;
                    avg.Normalize();
                    Normals[num + 4] = avg;
                    SetNormal(t, i, j, avg);

                    // Terrain type / path encoding, unchanged from the original
                    Vector4 packed = new Vector4(color.r, color.g, color.b, color.a);
                    Vector2 scaled = new Vector2(packed.x, packed.y) * 16f;
                    scaled.x *= 2f;
                    float pathIndex = (scaled / 16f).y * 16f;
                    if (packed.x <= 0f)
                        pathIndex = 15f;
                    float pathOffset = packed.x >= 0.0875f ? 16f : 0f;
                    if ((double)color.r == 0.625)
                        color.r = 0.75f;
                    color.g = pathIndex + pathOffset;

                    Room room = core.GetMyRoom(new Vector3(i, 0f, j - 1));
                    float biomeHue = room != null ? room.GetBiomeHue() : 0.5f;
                    Vector2 uv3 = new Vector2(color2.r, biomeHue);

                    for (int k = 0; k < VertsPerTile; k++)
                    {
                        Colors[num + k] = color;
                        Uv2[num + k] = PrePath(t, Verts[num + k], color);
                        Uv3[num + k] = uv3;
                    }

                    Uv[num] = new Vector2(i, j);
                    Uv[num + 1] = new Vector2(Mathf.Min(i + 1, 255), j);
                    Uv[num + 2] = new Vector2(i, Mathf.Min(j + 1, 255));
                    Uv[num + 3] = new Vector2(Mathf.Min(i + 1, 255), Mathf.Min(j + 1, 255));
                    // The shader reads the height from m_heightData[uv.y * 256 + uv.x]; the
                    // center heights live after the corner heights in that buffer (TerrainCenterHeightPatch)
                    Uv[num + 4] = new Vector2(Mathf.Clamp(i, 0, 255), Mathf.Clamp(j, 0, 255) + TerrainCenterHeightPatch.CenterRow);

                    num += VertsPerTile;
                }
            }

            // First time for this mesh: the old triangles point at a 4-vertex layout, so reset
            int meshId = mesh.GetInstanceID();
            if (!PreparedMeshes.TryGetValue(meshId, out Vector4[] tangents))
            {
                tangents = ConvertTangents(mesh.tangents);
                mesh.Clear();
                mesh.vertices = Verts;
                mesh.triangles = GetTriangles();
                PreparedMeshes[meshId] = tangents;
            }
            else
            {
                mesh.vertices = Verts;
            }
            mesh.tangents = tangents;

            t.m_normalMap.SetPixels32(NormalColors(t));
            t.m_normalMap.Apply();

            mesh.normals = Normals;
            mesh.uv = Uv;
            mesh.uv2 = Uv2;
            mesh.uv3 = Uv3;
            mesh.colors = Colors;

            Bounds bounds = mesh.bounds;
            bounds.Encapsulate(Verts[0] + new Vector3(0f, 256f, 0f));
            bounds.Encapsulate(Verts[VertCount - 1] + new Vector3(0f, 256f, 0f));
            mesh.bounds = bounds;

            return false; // skip the original 2-triangle version
        }

        // Maps the original per-tile tangents (4 per tile) onto our layout (5 per tile)
        private static Vector4[] ConvertTangents(Vector4[] original)
        {
            var result = new Vector4[VertCount];
            bool hasOriginal = original != null && original.Length >= ChunkSize * ChunkSize * 4;
            if (PreparedMeshes.Count == 0)
                Plugin.Log.LogInfo(hasOriginal
                    ? $"Terrain tangents carried over (example: {original[0]})"
                    : "Terrain mesh had no tangents; using a default");

            for (int tile = 0; tile < ChunkSize * ChunkSize; tile++)
            {
                Vector4 sum = Vector4.zero;
                for (int k = 0; k < 4; k++)
                {
                    Vector4 tangent = hasOriginal ? original[tile * 4 + k] : new Vector4(1f, 0f, 0f, -1f);
                    result[tile * VertsPerTile + k] = tangent;
                    sum += tangent;
                }
                Vector3 dir = new Vector3(sum.x, sum.y, sum.z).normalized;
                result[tile * VertsPerTile + 4] = new Vector4(dir.x, dir.y, dir.z, Mathf.Sign(sum.w == 0f ? -1f : sum.w));
            }
            return result;
        }

        // Per tile: corners 0 (x0,z0), 1 (x1,z0), 2 (x0,z1), 3 (x1,z1) and center 4.
        // Same winding as the game's triangles (3,1,0) and (2,3,0).
        private static int[] GetTriangles()
        {
            if (s_triangles != null)
                return s_triangles;

            s_triangles = new int[ChunkSize * ChunkSize * 4 * 3];
            int v = 0;
            int t = 0;
            for (int tile = 0; tile < ChunkSize * ChunkSize; tile++)
            {
                int c = v + 4;
                AddTri(ref t, v + 1, v + 0, c);
                AddTri(ref t, v + 0, v + 2, c);
                AddTri(ref t, v + 2, v + 3, c);
                AddTri(ref t, v + 3, v + 1, c);
                v += VertsPerTile;
            }
            return s_triangles;
        }

        private static void AddTri(ref int t, int a, int b, int c)
        {
            s_triangles[t] = a;
            s_triangles[t + 1] = b;
            s_triangles[t + 2] = c;
            t += 3;
        }
    }
}
