using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // When a park is closed (GameCore is destroyed on every scene change), release GPU memory and
    // forget per-dino state, so nothing piles up over a long play session with several park loads.
    [HarmonyPatch(typeof(GameCore), nameof(GameCore.OnDestroy))]
    public static class SceneCleanupPatch
    {
        private static readonly AccessTools.FieldRef<GameCore, ComputeBuffer> HeatmapBuffer =
            AccessTools.FieldRefAccess<GameCore, ComputeBuffer>("m_waterBufferHeatmap");

        public static void Postfix(GameCore __instance)
        {
            // The game disposes all its water buffers here except the heat map one
            ComputeBuffer heatmap = HeatmapBuffer(__instance);
            if (heatmap != null)
            {
                heatmap.Release();
                HeatmapBuffer(__instance) = null;
            }

            TerrainCenterHeightPatch.ReleaseOriginal();
            TerrainQuadPatch.Reset();
            SwimHeightPatch.Reset();
            SwimSteeringPatch.Reset();
            ModHints.Reset();
            MoveToolPatch.Reset();
            AnimSpeedPatch.Reset();
            ParkHistoryPatch.Reset();
            ParkInfoTextScale.Reset();
            DiagnosePatch.Reset();
            PhysicsPatch.Reset();
            LiveIncomePatch.Reset();
        }
    }
}
