using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // The game swims in a straight line and turns the body up to 100 degrees per frame, so
    // swimmers snap to a new direction. This gives them a turning circle instead:
    // - SwimNow: the swim direction turns gradually towards the target, so the dino swims
    //   in an arc, and slows down in sharp turns;
    // - LateUpdate: the body rotation follows at a limited turn rate (also when idle).
    // - SetDestination (SmartRoutes): the route goes through the water around corners
    //   (SwimRoutes), and SwimNow steers along its corner points.
    // - When the arc would run into the shore (e.g. a new trip starting with the head to a wall),
    //   the dino turns slowly on the spot first instead of swimming into it.
    public static class SwimSteeringPatch
    {
        private const float ArriveRadius = 1.5f;
        private const float MinSteerDepth = 0.8f; // the game stops swimming below this depth
        // SmartRoutes needs TurningCircle: its corner points are steered for in SwimNow
        internal static bool SmartRoutesActive => ModConfig.TurningCircle.Value && ModConfig.SmartRoutes.Value;
        private const float MaxPivotTime = 4f;     // seconds of turning on the spot before going straight

        private class SteerState
        {
            public Dinosaur Dino;
            public Vector3 Heading;
            public float Travelled;
            public float Pivot;
            // Planned route (SmartRoutes): corner points, the one being steered for, and the end
            // point it belongs to (if the game changes m_path[1] itself, the route is dropped)
            public List<Vector3> Route;
            public int RouteIndex;
            public Vector3 RouteEnd;
            public Vector3 RequestedEnd;
        }

        // NavWater doesn't know its dino, so we register them from LateUpdate
        private static readonly Dictionary<NavWater, SteerState> Steering = new Dictionary<NavWater, SteerState>();
        private static readonly Dictionary<int, Quaternion> LastRotation = new Dictionary<int, Quaternion>();

        // Called when a park is closed (GameCore.OnDestroy), so nothing is kept for dinos that are gone,
        // and when the setting changes, so no old route or rotation is picked up again
        internal static void Reset()
        {
            Steering.Clear();
            LastRotation.Clear();
        }

        // Max body turn rate (degrees/s), used for turning while idle
        private static float TurnRate(Dinosaur dino) => SpeciesConfig.Get(dino.m_species, SpeciesSetting.IdleTurnRate, true);
        // Radius of the turning circle while swimming (meters)
        private static float TurnRadius(Dinosaur dino) => SpeciesConfig.Get(dino.m_species, SpeciesSetting.TurnRadius, true);

        [HarmonyPatch(typeof(NavWater), nameof(NavWater.SetDestination))]
        public static class SmartRoutes
        {
            public static bool Prefix(NavWater __instance, Vector3 start, Vector3 end)
            {
                NavWater w = __instance;
                if (!SmartRoutesActive || !Steering.TryGetValue(w, out var s) || s.Dino == null)
                    return true;
                // Asked again for the same place while still on the way: keep the current route
                if (s.Route != null && w.m_moving && s.RouteEnd == w.m_path[1] && Flat(end - s.RequestedEnd).sqrMagnitude < 0.25f)
                    return false;
                s.Route = null;
                if (!SwimRoutes.TryPlan(start, end, TurnRadius(s.Dino), IsFreeSwim(s.Dino), out List<Vector3> route))
                    return true; // no water route: the game's straight line

                float length = 0f;
                for (int i = 1; i < route.Count; i++)
                    length += Flat(route[i] - route[i - 1]).magnitude;
                Vector3 goal = route[route.Count - 1];

                w.m_pos = start;
                w.m_path[0] = start;
                w.m_path[1] = goal;
                w.m_distance = length;
                w.m_timeSwim = 0f;
                w.m_moving = true;
                w.m_vel = Flat(route[1] - start).normalized;
                w.m_distanceLeft = length;
                s.Route = route;
                s.RouteIndex = 1;
                s.RouteEnd = goal;
                s.RequestedEnd = end;
                return false;
            }

            // Wandering around (WalkRandom's "Chill"), not heading for food, drink or the shore
            private static bool IsFreeSwim(Dinosaur dino)
            {
                ZooAction action = dino.m_doingThis.m_zooAction; // Think is a struct
                return action == null || action.m_action == AnimalAction.Chill;
            }
        }

        [HarmonyPatch(typeof(NavWater), nameof(NavWater.SwimNow))]
        public static class SwimNowPatch
        {
            // Returning false skips the original straight-line SwimNow
            public static bool Prefix(NavWater __instance, float delta)
            {
                NavWater w = __instance;
                if (!ModConfig.TurningCircle.Value || !w.m_moving || !Steering.TryGetValue(w, out var s) || s.Dino == null)
                    return true;

                // Fresh leg (SetDestination resets m_timeSwim), or switched on halfway a leg:
                // start from where the body points
                if (w.m_timeSwim == 0f || s.Heading.sqrMagnitude < 0.001f)
                {
                    Vector3 fwd = s.Dino.transform.forward;
                    fwd.y = 0f;
                    s.Heading = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Flat(w.m_path[1] - w.m_pos).normalized;
                    s.Travelled = 0f;
                    s.Pivot = 0f;
                }

                List<Vector3> route = s.Route != null && s.RouteEnd == w.m_path[1] ? s.Route : null;
                Vector3 aim = w.m_path[1];
                float rest = 0f; // route length after the point being steered for
                if (route != null)
                {
                    // Switch to the next corner point a bit before reaching this one: that's what
                    // makes the turn start early and the curve flow
                    float lookAhead = Mathf.Clamp(TurnRadius(s.Dino) * 0.6f, 1f, 3.5f);
                    while (s.RouteIndex < route.Count - 1 && Flat(route[s.RouteIndex] - w.m_pos).magnitude < lookAhead)
                        s.RouteIndex++;
                    aim = route[s.RouteIndex];
                    for (int i = s.RouteIndex + 1; i < route.Count; i++)
                        rest += Flat(route[i] - route[i - 1]).magnitude;
                }

                Vector3 toTarget = Flat(aim - w.m_pos);
                float dist = toTarget.magnitude;
                // Safety net: never swim much longer than the straight route would take
                if ((dist < ArriveRadius && rest <= 0f) || s.Travelled > w.m_distance * 2f + 10f)
                {
                    Finish(w);
                    return false;
                }

                Vector3 desired = toTarget / dist;

                // Ease off a little while the target is off to the side or behind
                float align = Vector3.Dot(s.Heading, desired);
                float speedFactor = Mathf.Lerp(SpeciesConfig.Get(s.Dino.m_species, SpeciesSetting.TurnSpeedFactor, true), 1f, Mathf.Clamp01((align + 0.2f) / 1.2f));
                float speed = w.m_speed * speedFactor;

                // Turn rate follows from the turning circle: angular speed = speed / radius
                float maxRadians = speed / TurnRadius(s.Dino) * delta;
                s.Heading = Vector3.RotateTowards(s.Heading, desired, maxRadians, 0f);
                float step = Mathf.Min(delta * speed, dist);

                Vector3 next = w.m_pos + s.Heading * step;
                if (GameCore.Get.GetWaterFast(next) < MinSteerDepth)
                {
                    // The arc would leave the water: turn slowly on the spot towards the target,
                    // then swim on. Only after a while head straight for it, as before.
                    s.Pivot += delta;
                    if (s.Pivot < MaxPivotTime && Vector3.Dot(s.Heading, desired) < 0.98f)
                    {
                        s.Heading = Vector3.RotateTowards(s.Heading, desired, TurnRate(s.Dino) * Mathf.Deg2Rad * delta, 0f);
                        w.m_vel = s.Heading;
                        w.m_timeSwim = Mathf.Max(w.m_timeSwim, 0.001f); // not a fresh leg any more
                        return false;
                    }
                    s.Heading = desired;
                    next = w.m_pos + desired * step;
                    if (GameCore.Get.GetWaterFast(next) < MinSteerDepth)
                    {
                        Finish(w);
                        return false;
                    }
                }
                else
                {
                    s.Pivot = 0f;
                }

                w.m_pos = new Vector3(next.x, w.m_pos.y, next.z);
                w.m_vel = s.Heading;
                s.Travelled += step;
                // m_timeSwim/m_distance is used as leg progress (e.g. by the depth change)
                // (never exactly 0 again, because 0 marks a fresh leg)
                float left = Mathf.Max(dist - step, 0f) + rest;
                w.m_timeSwim = Mathf.Clamp(w.m_distance - left, 0.001f, w.m_distance);
                w.m_distanceLeft = left;
                return false;
            }
        }

        [HarmonyPatch(typeof(Dinosaur), "LateUpdate")]
        public static class SmoothRotation
        {
            public static void Postfix(Dinosaur __instance)
            {
                var dino = __instance;
                if (!DinoUtil.IsWaterDino(dino) || dino.m_agent == null)
                    return;

                NavWater water = dino.m_agent.GetWater();
                if (!Steering.TryGetValue(water, out var s))
                    Steering[water] = s = new SteerState();
                s.Dino = dino;

                int id = dino.GetInstanceID();
                if (!dino.m_water || !ModConfig.TurningCircle.Value)
                {
                    // On land the NavMeshAgent already has a turn rate; with TurningCircle off, the game turns
                    LastRotation.Remove(id);
                    return;
                }

                Quaternion wanted = dino.transform.rotation;
                if (LastRotation.TryGetValue(id, out var last))
                {
                    Quaternion smoothed = Quaternion.RotateTowards(last, wanted, TurnRate(dino) * Time.deltaTime);
                    dino.transform.rotation = smoothed;
                    LastRotation[id] = smoothed;
                }
                else
                {
                    LastRotation[id] = wanted;
                }
            }
        }

        private static void Finish(NavWater w)
        {
            w.m_timeSwim = w.m_distance;
            w.m_distanceLeft = 0f;
            w.m_vel = Vector3.zero;
            w.m_moving = false;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }
    }
}
