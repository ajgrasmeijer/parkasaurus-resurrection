using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Replaces the game's swim height logic (end of Dinosaur.LateUpdate) for water dinos:
    // - during a swim leg the depth changes gradually over the whole leg, so the dino
    //   swims diagonally instead of first going down and then forward;
    // - the height always stays between the bottom and the surface (no more launching
    //   into the air while eating, which was a feedback loop in the original formula);
    // - every change is smoothed, so shallow water edges no longer snap the dino up/down.
    [HarmonyPatch(typeof(Dinosaur), "LateUpdate")]
    public static class SwimHeightPatch
    {

        // Lowest point a dino dives to, as a fraction of the water depth (1 = surface)
        private static float MinDiveFraction(Dinosaur dino) =>
            1f - SpeciesConfig.Get(dino.m_species, SpeciesSetting.MaxDiveDepth, true);

        private class HeightState
        {
            public bool Initialized;
            public float Fraction;          // current depth as fraction of water depth (1 = surface)
            public float FractionVel;
            public float LegStartFraction;
            public bool WasSwimming;
            public Vector3 LegEnd;
            public float Height;            // final height above the bottom
            public float HeightVel;
            public float BobPhase;
        }

        private static readonly Dictionary<int, HeightState> States = new Dictionary<int, HeightState>();

        // Called when a park is closed (GameCore.OnDestroy), so nothing is kept for dinos that are gone,
        // and when the setting changes, so switching it back on starts from the dino's current height
        internal static void Reset()
        {
            States.Clear();
        }

        private static readonly AccessTools.FieldRef<Dinosaur, float> WaterHeight =
            AccessTools.FieldRefAccess<Dinosaur, float>("m_waterHeight");
        private static readonly AccessTools.FieldRef<Dinosaur, float> WaterHeightVel =
            AccessTools.FieldRefAccess<Dinosaur, float>("m_waterHeightVel");
        private static readonly AccessTools.FieldRef<Dinosaur, float> RandomHeight =
            AccessTools.FieldRefAccess<Dinosaur, float>("m_randomHeight");
        private static readonly AccessTools.FieldRef<BaseAnimal<DinosaurObject>, bool> LockRot =
            AccessTools.FieldRefAccess<BaseAnimal<DinosaurObject>, bool>("m_lockRot");
        private static readonly AccessTools.FieldRef<BaseAnimal<DinosaurObject>, bool> ShouldMove =
            AccessTools.FieldRefAccess<BaseAnimal<DinosaurObject>, bool>("m_shouldMove");

        public static void Postfix(Dinosaur __instance)
        {
            var dino = __instance;
            if (!ModConfig.SmoothSwimDepth.Value || !DinoUtil.IsWaterDino(dino) || !ShouldMove(dino) || Global.Get.m_debugPopUpMode)
                return;

            int id = dino.GetInstanceID();
            if (!States.TryGetValue(id, out var s))
            {
                s = new HeightState { BobPhase = Random.value * 10f };
                States[id] = s;
            }

            // The game has just placed the dino at bottom + base height; recover the bottom.
            Vector3 pos = dino.transform.position;
            float bottomY = pos.y - dino.m_agent.GetBaseHeight();
            // The water shader draws the surface at the LOWEST of the tile's 4 corners + the water
            // depth (GetHeightRawWater is the game's CPU copy of that formula). The bottom under
            // the dino is interpolated and lies higher on slopes, so measure the water column
            // against the drawn surface, not bottom + depth.
            float waterDepth = GameCore.Get.GetWaterFast(pos);
            var tile = new int2(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.z));
            float surfaceY = GameCore.Get.m_superTerrain.GetHeightRawWater(tile) - 32f + waterDepth - 0.1f;
            float depth = waterDepth > 0f ? Mathf.Max(surfaceY - bottomY, 0f) : 0f;
            NavWater water = dino.m_agent.GetWater();

            if (!s.Initialized)
            {
                s.Initialized = true;
                s.Height = dino.m_agent.GetBaseHeight();
                s.Fraction = depth > 0.5f ? Mathf.Clamp01(s.Height / depth) : 1f;
            }

            float desiredHeight;
            if (depth > 0.5f)
            {
                float target = TargetFraction(dino, depth, bottomY);
                bool legStarted = water.m_moving && (!s.WasSwimming || water.m_path[1] != s.LegEnd);

                if (legStarted)
                {
                    s.LegStartFraction = s.Fraction;
                    s.LegEnd = water.m_path[1];
                }

                if (water.m_moving && water.m_distance > 0.01f)
                {
                    // Spread the depth change over the whole leg
                    float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(water.m_timeSwim / water.m_distance));
                    s.Fraction = Mathf.Lerp(s.LegStartFraction, target, t);
                    s.FractionVel = 0f;
                }
                else
                {
                    s.Fraction = Mathf.SmoothDamp(s.Fraction, target, ref s.FractionVel, SpeciesConfig.Get(dino.m_species, SpeciesSetting.IdleDepthChangeTime, true));
                }

                float bob = Mathf.Sin((Time.time + s.BobPhase) * 0.8f) * SpeciesConfig.Get(dino.m_species, SpeciesSetting.BobbingAmount, true) * Mathf.Clamp01(depth - 1f);
                desiredHeight = s.Fraction * depth - 0.2f * Mathf.Clamp01(depth) + dino.m_waterFloatOffset + bob;
                desiredHeight = Mathf.Clamp(desiredHeight, 0f, depth);
            }
            else
            {
                desiredHeight = 0f;
            }
            s.WasSwimming = water.m_moving;

            s.Height = Mathf.SmoothDamp(s.Height, desiredHeight, ref s.HeightVel, ModConfig.DepthSmoothingTime.Value);
            // Hard limit at the surface: where the bottom slopes up quickly the smoothing
            // would otherwise briefly lift the dino out of the water.
            if (depth > 0.5f && s.Height > depth)
            {
                s.Height = depth;
                s.HeightVel = Mathf.Min(s.HeightVel, 0f);
            }

            // Write back so the game's own state agrees with ours
            WaterHeight(dino) = s.Height;
            WaterHeightVel(dino) = 0f;
            dino.m_agent.SetBaseHeight(s.Height);
            float finalY = bottomY + s.Height;
            dino.transform.position = new Vector3(pos.x, finalY, pos.z);

            // Keep the swim path at the same height, otherwise the game thinks the dino is
            // "out of sync" with its path and after 10s warps it back onto the navmesh.
            water.m_pos.y = finalY;
            water.m_path[0].y = finalY;
            water.m_path[1].y = finalY;
            s.LegEnd.y = finalY;
        }

        // Depth to aim for as a fraction of the water depth (1 = just below the surface)
        private static float TargetFraction(Dinosaur dino, float depth, float bottomY)
        {
            Think doing = dino.m_doingThis;

            // Interacting with something (eating etc.): be at the height of the target,
            // instead of the original formula that kept adding 1.2 to its own height.
            if (LockRot(dino) && !doing.m_isFloater)
            {
                float wanted = doing.GetGotoPosition().y - bottomY + 1.2f;
                return Mathf.Clamp(wanted / depth, MinDiveFraction(dino), 1f);
            }

            // Chilling in deep water: dive to a random depth (chosen by the game per walk)
            if (doing.m_zooAction == null && doing.m_do == AnimalAction.Chill && depth > 4f)
                return Mathf.Lerp(MinDiveFraction(dino), 1f, RandomHeight(dino));

            return 1f;
        }
    }
}
