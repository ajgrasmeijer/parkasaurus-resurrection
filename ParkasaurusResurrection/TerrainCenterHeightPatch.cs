using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // The terrain shader ignores the mesh height: its vertex shader reads the height from the
    // GPU buffer m_heightData at index (uv.y * 256 + uv.x), one entry per grid corner.
    // (Verified in the disassembly: in all 41 vertex shader variants uv/TEXCOORD0 is only used
    // for this lookup; the textures use uv2/uv3.)
    //
    // To give tile centers their own height without touching the shader, the buffer is made
    // larger: after the corner heights come PadRows rows of zeros, then the center heights, and
    // TerrainQuadPatch gives each center vertex uv (i, j + CenterRow), which lands exactly there.
    // The water shader reads up to row 258 (it clamps to 256, then reads rows +1 and +2); those
    // reads used to fall outside the buffer and return 0, so the zero rows keep them that way.
    // Without them, water showed up along the edge of the map. The water simulation only
    // indexes the first 256 rows.
    public static class TerrainCenterHeightPatch
    {
        private const int Grid = 256;
        private const int GridCells = Grid * Grid;

        private const int PadRows = 3;
        // Row (uv.y) of the first tile center in the buffer
        internal const int CenterRow = Grid + PadRows;
        private const int CenterOffset = CenterRow * Grid;
        private const int BufferSize = CenterOffset + GridCells;

        private static readonly float[] Centers = new float[GridCells];

        // The original buffer. SuperWater.Setup (called in GameCore.Start, before our postfix)
        // already bound it to the water materials; releasing it made the water shader read
        // zero terrain heights and draw the surface far too low. It's kept alive and updated
        // so any binding we don't know about stays correct.
        private static ComputeBuffer s_originalBuffer;

        // Called when a park is closed. The game disposes m_heightBuffer (our enlarged one) itself;
        // the original buffer is ours to release, otherwise ~256 KB of GPU memory leaks per park load.
        internal static void ReleaseOriginal()
        {
            s_originalBuffer?.Release();
            s_originalBuffer = null;
        }

        // Set by the reload hotkey: after the next upload, log one tile to check the GPU data
        internal static bool VerifyNextUpload;

        private static readonly AccessTools.FieldRef<GameCore, int> CSStep =
            AccessTools.FieldRefAccess<GameCore, int>("m_CSStep");

        // Replace the height buffer with one that also has room for the tile centers
        [HarmonyPatch(typeof(GameCore), "Start")]
        public static class EnlargeBuffer
        {
            public static void Postfix(GameCore __instance)
            {
                GameCore core = __instance;
                if (!TerrainQuadPatch.ActiveThisPark || core.m_heightBuffer == null)
                    return;

                s_originalBuffer = core.m_heightBuffer;
                core.m_heightBuffer = new ComputeBuffer(BufferSize, 4);
                core.m_heightBuffer.SetData(new float[BufferSize]); // new buffers aren't guaranteed to be zero
                core.m_waterComp.SetBuffer(CSStep(core), "m_heightData", core.m_heightBuffer);
                core.m_mainTerrain.SetBuffer("m_heightData", core.m_heightBuffer);
                RebindChildren(core.m_superWater != null ? core.m_superWater.transform : null, core.m_heightBuffer);
                RebindChildren(core.m_superTerrain != null ? core.m_superTerrain.transform : null, core.m_heightBuffer);
                core.m_heightGPUSync = true;
                Plugin.Log.LogInfo("Height buffer enlarged for tile centers");
            }
        }

        // CheckWater uploads the corner heights when m_heightGPUSync is set; upload the centers too
        [HarmonyPatch(typeof(GameCore), nameof(GameCore.CheckWater))]
        public static class UploadCenters
        {
            public static void Prefix(GameCore __instance, out bool __state)
            {
                __state = __instance.m_heightGPUSync || Global.Get.m_forceGPUUpdateEveryFrame;
            }

            public static void Postfix(GameCore __instance, bool __state)
            {
                GameCore core = __instance;
                if (!__state || core.m_heightBuffer == null || core.m_heightBuffer.count < BufferSize)
                    return;

                float[] corners = core.m_superTerrain.GetData();
                if (corners == null || corners.Length < GridCells)
                    return;

                for (int z = 0; z < Grid; z++)
                {
                    int z1 = Mathf.Min(z + 1, Grid - 1);
                    for (int x = 0; x < Grid; x++)
                    {
                        int x1 = Mathf.Min(x + 1, Grid - 1);
                        Centers[x + z * Grid] = CenterHeight(corners[x + z * Grid], corners[x1 + z * Grid],
                                                             corners[x + z1 * Grid], corners[x1 + z1 * Grid]);
                    }
                }
                core.m_heightBuffer.SetData(Centers, 0, CenterOffset, GridCells);
                if (s_originalBuffer != null)
                    s_originalBuffer.SetData(corners, 0, 0, GridCells);

                if (VerifyNextUpload)
                {
                    VerifyNextUpload = false;
                    LogVerification(core, corners);
                }
            }
        }

        // Finds the tile where the center modes differ most and logs what the GPU really holds
        private static void LogVerification(GameCore core, float[] corners)
        {
            int best = -1;
            float bestDiff = 0f;
            for (int z = 0; z < Grid - 1; z++)
            {
                for (int x = 0; x < Grid - 1; x++)
                {
                    float diff = Mathf.Abs((corners[x + z * Grid] + corners[x + 1 + (z + 1) * Grid])
                                         - (corners[x + 1 + z * Grid] + corners[x + (z + 1) * Grid]));
                    if (diff > bestDiff)
                    {
                        bestDiff = diff;
                        best = x + z * Grid;
                    }
                }
            }
            if (best < 0)
            {
                Plugin.Log.LogInfo("Check: no sloped tiles found");
                return;
            }

            var gpu = new float[1];
            core.m_heightBuffer.GetData(gpu, 0, CenterOffset + best, 1);
            int bx = best % Grid, bz = best / Grid;
            Plugin.Log.LogInfo($"Check tile center ({ModConfig.SmoothEdges.Value}) at tile ({bx},{bz}): " +
                               $"corners {corners[best] * 64f:F1}/{corners[best + 1] * 64f:F1}/{corners[best + Grid] * 64f:F1}/{corners[best + Grid + 1] * 64f:F1}, " +
                               $"expected {Centers[best] * 64f:F1}, on GPU {gpu[0] * 64f:F1} (height units)");
        }

        private static void RebindChildren(Transform parent, ComputeBuffer buffer)
        {
            if (parent == null)
                return;
            for (int i = 0; i < parent.childCount; i++)
            {
                var renderer = parent.GetChild(i).GetComponent<MeshRenderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                    renderer.sharedMaterial.SetBuffer("m_heightData", buffer);
            }
        }

        // h00 = (x, z), h10 = (x+1, z), h01 = (x, z+1), h11 = (x+1, z+1).
        // The game's own 2 triangles fold along the h00-h11 diagonal.
        public static float CenterHeight(float h00, float h10, float h01, float h11)
        {
            float diagGame = (h00 + h11) * 0.5f;
            float diagOther = (h10 + h01) * 0.5f;
            switch (ModConfig.SmoothEdges.Value)
            {
                case EdgeShape.Off: return diagGame;
                case EdgeShape.KeepValleys: return Mathf.Min(diagGame, diagOther);
                case EdgeShape.KeepRidges: return Mathf.Max(diagGame, diagOther);
                default: return (h00 + h10 + h01 + h11) * 0.25f;
            }
        }
    }
}
