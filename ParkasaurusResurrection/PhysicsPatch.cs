using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Physics was 13-15% of the frame time in a busy park, growing with the number of visitors
    // (PerfProbe, 2026-10-02: 38 ms/s at 30 visitors, 166 ms/s at 296). Causes:
    // - Visitors, staff and cars move around with colliders but without a Rigidbody (two BoxColliders
    //   per person, layers People and Selector; several per car, layer Car). PhysX treats those as
    //   static, and every move makes it update its tree of ~10,000 static colliders.
    // - Only a kinematic Rigidbody (as the game gives dinos) made it worse (measured 281-310 ms/s):
    //   the People layer collides with 10 layers, so every person was then paired with all the
    //   buildings and parking spots around it.
    // So LightPeoplePhysics does both: a kinematic Rigidbody for people and cars, and the People
    // layer only collides with Door (doors open through OnTriggerStay), Car with nothing. Raycasts and
    // overlap checks (clicking, dinos looking for visitors) use their own layer masks and aren't
    // affected by the collision matrix. A ragdoll is a separate object with its own bodies.
    // - Physics.autoSyncTransforms is on: every raycast first syncs all moved transforms to physics.
    //   Off (Unity's default since 2018.3), transforms are synced once before each physics step.
    public static class PhysicsPatch
    {
        [HarmonyPatch(typeof(Human), nameof(Human.Start))]
        public static class VisitorBody
        {
            public static void Postfix(Human __instance) => AddKinematicBody(__instance.gameObject);
        }

        [HarmonyPatch(typeof(Employee), nameof(Employee.Start))]
        public static class StaffBody
        {
            public static void Postfix(Employee __instance) => AddKinematicBody(__instance.gameObject);
        }

        [HarmonyPatch(typeof(Car), nameof(Car.Start))]
        public static class CarBody
        {
            public static void Postfix(Car __instance) => AddKinematicBody(__instance.gameObject);
        }

        // The game's own collision matrix rows for People and Car, to put back when switched off
        private static bool[] s_gamePeople, s_gameCar;

        // On: people only meet doors, cars meet nothing. Off: the game's own collisions.
        internal static void SetCollisionLayers(bool light)
        {
            int people = LayerMask.NameToLayer("People");
            int car = LayerMask.NameToLayer("Car");
            int door = LayerMask.NameToLayer("Door");
            if (s_gamePeople == null)
            {
                s_gamePeople = new bool[32];
                s_gameCar = new bool[32];
                for (int layer = 0; layer < 32; layer++)
                {
                    s_gamePeople[layer] = people >= 0 && Physics.GetIgnoreLayerCollision(people, layer);
                    s_gameCar[layer] = car >= 0 && Physics.GetIgnoreLayerCollision(car, layer);
                }
            }
            // Car first: where People and Car meet, the People row decides
            for (int layer = 0; layer < 32; layer++)
            {
                if (car >= 0)
                    Physics.IgnoreLayerCollision(car, layer, light || s_gameCar[layer]);
            }
            for (int layer = 0; layer < 32; layer++)
            {
                if (people >= 0)
                    Physics.IgnoreLayerCollision(people, layer, light ? layer != door : s_gamePeople[layer]);
            }
            Plugin.Log.LogInfo(light
                ? $"Light people physics: People layer {people} only meets Door layer {door}, Car layer {car} meets nothing"
                : "Light people physics off: the game's own collision layers");
        }

        // The bodies we added, so switching off removes only those
        private static readonly List<Rigidbody> Added = new List<Rigidbody>();

        // Switched in the options menu: also handle everyone who's already there
        internal static void ApplyKinematicPeople()
        {
            bool on = ModConfig.KinematicPeople.Value;
            SetCollisionLayers(on);
            if (on)
            {
                foreach (Human human in Object.FindObjectsOfType<Human>())
                    AddKinematicBody(human.gameObject);
                foreach (Employee employee in Object.FindObjectsOfType<Employee>())
                    AddKinematicBody(employee.gameObject);
                foreach (Car car in Object.FindObjectsOfType<Car>())
                    AddKinematicBody(car.gameObject);
            }
            else
            {
                foreach (Rigidbody body in Added)
                {
                    if (body != null)
                        Object.Destroy(body);
                }
                Added.Clear();
            }
        }

        // Called when a park is closed: its people and cars are gone
        internal static void Reset() => Added.Clear();

        private static void AddKinematicBody(GameObject go)
        {
            if (!ModConfig.KinematicPeople.Value || go.GetComponent<Rigidbody>() != null)
                return;
            var body = go.AddComponent<Rigidbody>();
            Added.Add(body);
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
        }

        private static float s_baseStep;

        // Every frame (HotkeyPatch): physics step = the game's step x game speed (at least 1x), at most 0.1 s
        internal static void UpdateStep()
        {
            if (!Plugin.ModEnabledAtStart)
                return;
            if (s_baseStep <= 0f)
                s_baseStep = Time.fixedDeltaTime;
            float wanted = ModConfig.PhysicsStepFollowsSpeed.Value
                ? Mathf.Min(s_baseStep * Mathf.Max(1f, Time.timeScale), 0.1f)
                : s_baseStep;
            if (!Mathf.Approximately(Time.fixedDeltaTime, wanted))
                Time.fixedDeltaTime = wanted;
        }

        internal static void ApplySync()
        {
            Physics.autoSyncTransforms = !(Plugin.ModEnabledAtStart && ModConfig.NoAutoSyncTransforms.Value);
        }
    }
}
