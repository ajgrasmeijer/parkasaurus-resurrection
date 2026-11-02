using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Dinosaur.WalkRandom picks a random tile within ~12m, which for swimmers mostly means
    // tiny hops. While WalkRandom runs, we swap its "Chill" target for a point 12-25m away
    // with deep water along the whole straight route (swimming is a straight line in this game),
    // or with SmartRoutes just a deep spot (the route then goes around corners).
    public static class LongerSwimsPatch
    {
        private const int Attempts = 40;

        private static Dinosaur s_walkingDino;

        // Mark which dino is inside WalkRandom (it's a coroutine, so patch its MoveNext)
        [HarmonyPatch]
        public static class WalkRandomMoveNext
        {
            private static FieldInfo s_thisField;

            public static MethodBase TargetMethod()
            {
                MethodInfo moveNext = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Dinosaur), nameof(Dinosaur.WalkRandom)));
                s_thisField = AccessTools.Field(moveNext.DeclaringType, "<>4__this");
                return moveNext;
            }

            public static void Prefix(object __instance)
            {
                s_walkingDino = s_thisField?.GetValue(__instance) as Dinosaur;
            }

            public static void Finalizer()
            {
                s_walkingDino = null;
            }
        }

        [HarmonyPatch(typeof(BaseAnimal<DinosaurObject>), nameof(BaseAnimal<DinosaurObject>.DoingThisSet), typeof(Vector3), typeof(AnimalAction))]
        public static class DoingThisSetPatch
        {
            public static void Prefix(object __instance, ref Vector3 place, AnimalAction doThis)
            {
                // This method is shared by all animals; only touch the dino inside WalkRandom
                if (!ModConfig.LongerSwims.Value || doThis != AnimalAction.Chill || s_walkingDino == null ||
                    !ReferenceEquals(__instance, s_walkingDino))
                    return;

                Dinosaur dino = s_walkingDino;
                if (!DinoUtil.IsWaterDino(dino) || dino.m_inRage)
                    return;
                // Low privacy: the game walks to the most private tile on purpose, leave that alone
                if ((float)dino.m_objectData.m_privacyValue < 0.6f)
                    return;

                Vector3 from = dino.transform.position;
                if (GameCore.Get.GetWaterFast(from) < ModConfig.MinRouteDepth.Value)
                    return;

                if (TryFindSwimTarget(dino, from, out Vector3 target))
                {
                    target.y = place.y;
                    place = target;
                }
            }
        }

        private static bool TryFindSwimTarget(Dinosaur dino, Vector3 from, out Vector3 target)
        {
            Room myRoom = GameCore.Get.GetMyRoom(from);
            var options = new List<Vector3>();

            for (int i = 0; i < Attempts; i++)
            {
                float angle = Random.value * Mathf.PI * 2f;
                SpeciesConfig.SwimDistance(dino.m_species, out float minDist, out float maxDist);
                float dist = Random.Range(minDist, maxDist);
                Vector3 candidate = from + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * dist;

                if (GameCore.Get.GetMyRoom(candidate) != myRoom)
                    continue;
                if (GameCore.Get.GetTileGameObject(new Vector3(Mathf.Round(candidate.x), 0f, Mathf.Round(candidate.z))))
                    continue;
                // With SmartRoutes the dino swims around corners, so only the spot itself must be deep
                if (SwimSteeringPatch.SmartRoutesActive ? GameCore.Get.GetWaterFast(candidate) < ModConfig.MinRouteDepth.Value : !RouteIsDeep(from, candidate))
                    continue;

                options.Add(candidate);
            }

            if (options.Count == 0)
            {
                target = from;
                return false;
            }
            target = options[Random.Range(0, options.Count)];
            return true;
        }

        private static bool RouteIsDeep(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            dir.y = 0f;
            int steps = Mathf.CeilToInt(dir.magnitude);
            dir.Normalize();
            for (int i = 1; i <= steps; i++)
            {
                if (GameCore.Get.GetWaterFast(from + dir * i) < ModConfig.MinRouteDepth.Value)
                    return false;
            }
            return true;
        }
    }
}
