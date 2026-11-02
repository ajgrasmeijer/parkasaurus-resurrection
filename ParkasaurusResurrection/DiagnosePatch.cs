using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Watches every Aquatic/SemiAquatic dino and logs only notable events:
    // a swim leg ending, standing still for a while, and ending up above the water.
    [HarmonyPatch(typeof(Dinosaur), nameof(Dinosaur.Update))]
    public static class DiagnosePatch
    {
        private const float StallSeconds = 5f;
        private const float SummaryInterval = 60f;

        private class DinoState
        {
            public bool WasSwimming;
            public float PlannedDistance;
            public Vector3 SwimStart;
            public Vector3 SwimEnd;
            public Vector3 LastMovePos;
            public float LastMoveTime;
            public bool StallLogged;
            public bool AboveWater;
            public float LastY;
            public float LastJumpLog;
        }

        private static readonly Dictionary<int, DinoState> States = new Dictionary<int, DinoState>();
        private static float s_nextSummary;
        private static readonly HashSet<int> SeenThisSummary = new HashSet<int>();

        // Called when a park is closed (GameCore.OnDestroy), so nothing is kept for dinos that are gone
        internal static void Reset()
        {
            States.Clear();
            SeenThisSummary.Clear();
        }
        private static int s_swimmingCount, s_stalledCount;

        private static readonly AccessTools.FieldRef<Dinosaur, float> WaterHeight =
            AccessTools.FieldRefAccess<Dinosaur, float>("m_waterHeight");
        private static readonly AccessTools.FieldRef<BaseAnimal<DinosaurObject>, bool> LockRot =
            AccessTools.FieldRefAccess<BaseAnimal<DinosaurObject>, bool>("m_lockRot");

        private static ManualLogSource Log => Plugin.Log;

        public static void Postfix(Dinosaur __instance)
        {
            var dino = __instance;
            if (!ModConfig.SwimDiagnostics.Value)
                return;
            if (dino.m_classFlag != DinoClassFlag.Aquatic && dino.m_classFlag != DinoClassFlag.SemiAquatic)
                return;
            if (dino.m_agent == null || GameCore.Get == null)
                return;

            int id = dino.GetInstanceID();
            if (!States.TryGetValue(id, out var s))
            {
                s = new DinoState { LastMovePos = dino.transform.position, LastMoveTime = Time.time, LastY = dino.transform.position.y };
                States[id] = s;
            }

            NavWater water = dino.m_agent.GetWater();
            Vector3 pos = dino.transform.position;

            CheckSwimLeg(dino, s, water);
            CheckStall(dino, s, water, pos);
            CountForSummary(id, water, s);
        }

        private static void CheckSwimLeg(Dinosaur dino, DinoState s, NavWater water)
        {
            if (water.m_moving && !s.WasSwimming)
            {
                s.PlannedDistance = water.m_distance;
                s.SwimStart = water.m_path[0];
                s.SwimEnd = water.m_path[1];
            }
            else if (!water.m_moving && s.WasSwimming)
            {
                Vector3 dir = s.SwimEnd - s.SwimStart;
                dir.y = 0f;
                float depthAhead = dir.sqrMagnitude > 0.01f
                    ? GameCore.Get.GetWaterFast(s.SwimEnd + dir.normalized * 1.5f)
                    : -1f;

                string reason;
                if (s.PlannedDistance < 1f)
                    reason = "barely moved (<1m), blocked right away";
                else if (s.PlannedDistance >= 29f)
                    reason = "hit the 30m limit";
                else if (depthAhead >= 0f && depthAhead < 0.8f)
                    reason = "shallow water/shore ahead";
                else
                    reason = "target reached";

                Log.LogInfo($"[SWIM-STOP] {Name(dino)} | swam {s.PlannedDistance:F1}m | reason: {reason} | " +
                            $"depth here {GameCore.Get.GetWaterFast(dino.transform.position):F1}, ahead {depthAhead:F1} | action: {dino.m_doingThis.m_do}");
            }
            s.WasSwimming = water.m_moving;
        }

        private static void CheckStall(Dinosaur dino, DinoState s, NavWater water, Vector3 pos)
        {
            Vector3 flat = pos - s.LastMovePos;
            flat.y = 0f;
            if (flat.magnitude > 0.5f)
            {
                if (s.StallLogged)
                    Log.LogInfo($"[MOVING-AGAIN] {Name(dino)} moves again after {Time.time - s.LastMoveTime:F0}s standing still");
                s.LastMovePos = pos;
                s.LastMoveTime = Time.time;
                s.StallLogged = false;
                return;
            }

            if (!s.StallLogged && Time.time - s.LastMoveTime > StallSeconds)
            {
                s.StallLogged = true;
                Log.LogInfo($"[STALLED] {Name(dino)} has not moved for {StallSeconds:F0}s | action: {dino.m_doingThis.m_do} | " +
                            $"in water: {dino.m_water}, swimming: {water.m_moving}, on navmesh: {dino.m_agent.IsOnNavMesh()}, " +
                            $"stopped: {dino.m_stopped}, depth {GameCore.Get.GetWaterFast(pos):F1}");
            }
        }

        // Height is measured after LateUpdate (where the swim height is applied), so we
        // see the position that is actually rendered, not an in-between state from Update.
        [HarmonyPatch(typeof(Dinosaur), "LateUpdate")]
        public static class MeasureHeight
        {
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(Dinosaur __instance)
            {
                if (ModConfig.SwimDiagnostics.Value && States.TryGetValue(__instance.GetInstanceID(), out var s) && GameCore.Get != null)
                    CheckHeight(__instance, s, __instance.transform.position);
            }
        }

        private static void CheckHeight(Dinosaur dino, DinoState s, Vector3 pos)
        {
            float depth = GameCore.Get.GetWaterFast(pos);
            float height = WaterHeight(dino);
            bool above = depth > 0.5f && height > depth + 1f;

            if (above && !s.AboveWater)
            {
                Log.LogInfo($"[ABOVE-WATER] {Name(dino)} | height {height:F1} at water depth {depth:F1} | " +
                            $"lockRot: {LockRot(dino)}, action: {dino.m_doingThis.m_do}, target y {dino.m_doingThis.GetGotoPosition().y:F1}, own y {pos.y:F1}");
            }
            else if (!above && s.AboveWater)
            {
                Log.LogInfo($"[BACK-IN-WATER] {Name(dino)}");
            }
            s.AboveWater = above;

            float dt = Time.deltaTime;
            if (dt > 0f)
            {
                float vSpeed = (pos.y - s.LastY) / dt;
                if (Mathf.Abs(vSpeed) > 4f && Time.time - s.LastJumpLog > 2f)
                {
                    s.LastJumpLog = Time.time;
                    Log.LogInfo($"[JUMP] {Name(dino)} | {(vSpeed > 0 ? "up" : "down")} at {Mathf.Abs(vSpeed):F1} m/s ({s.LastY:F1} -> {pos.y:F1}) | " +
                                $"lockRot: {LockRot(dino)}, action: {dino.m_doingThis.m_do}, height {height:F1}, depth {depth:F1}, in water: {dino.m_water}, swimming: {dino.m_agent.GetWater().m_moving}");
                }
            }
            s.LastY = pos.y;
        }

        private static void CountForSummary(int id, NavWater water, DinoState s)
        {
            if (SeenThisSummary.Add(id))
            {
                if (water.m_moving) s_swimmingCount++;
                if (s.StallLogged) s_stalledCount++;
            }

            if (Time.time >= s_nextSummary)
            {
                if (SeenThisSummary.Count > 0)
                    Log.LogInfo($"[SUMMARY] {SeenThisSummary.Count} swimmers | {s_swimmingCount} swimming | {s_stalledCount} stalled >{StallSeconds:F0}s");
                SeenThisSummary.Clear();
                s_swimmingCount = s_stalledCount = 0;
                s_nextSummary = Time.time + SummaryInterval;
            }
        }

        private static string Name(Dinosaur dino)
        {
            return $"{dino.m_objectData.m_name} ({dino.m_species}{(dino.m_isBaby ? ", baby" : "")})";
        }
    }
}
