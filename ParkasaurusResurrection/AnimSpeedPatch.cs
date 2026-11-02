using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Movement and animation speed per dino:
    // - swimmers in the water use the [Swimming] values, species with custom settings use their own,
    //   everything else keeps the game's behaviour;
    // - animation speed blends between a "moving" and an "idle" value;
    // - movement speed: in the water it scales the swim speed; on land it scales the NavMeshAgent
    //   speed, except for species whose walk comes from the animation (root motion), where only a
    //   faster animation makes them move faster.
    public static class AnimSpeedPatch
    {
        private static readonly AccessTools.FieldRef<BaseAnimal<DinosaurObject>, Animator> Anim =
            AccessTools.FieldRefAccess<BaseAnimal<DinosaurObject>, Animator>("m_anim");

        // Current animator speed per dino we adjusted, so we can blend and hand control back
        private static readonly Dictionary<int, float> Adjusted = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> BlendVel = new Dictionary<int, float>();

        // Called when a park is closed (GameCore.OnDestroy), so nothing is kept for dinos that are gone
        internal static void Reset()
        {
            Adjusted.Clear();
            BlendVel.Clear();
        }

        // Whether this dino uses mod values right now (swimmer in water, or custom species)
        private static bool Applies(Dinosaur dino, out bool asSwimmer)
        {
            asSwimmer = DinoUtil.IsWaterDino(dino) && dino.m_water;
            return ModConfig.SpeedAndAnimation.Value && (asSwimmer || SpeciesConfig.HasCustom(dino.m_species));
        }

        private static float MovementMultiplier(Dinosaur dino, bool asSwimmer) =>
            SpeciesConfig.Get(dino.m_species, SpeciesSetting.MovementSpeed, asSwimmer);

        [HarmonyPatch(typeof(Dinosaur), nameof(Dinosaur.Update))]
        public static class AnimationSpeed
        {
            public static void Postfix(Dinosaur __instance)
            {
                var dino = __instance;
                Animator anim = Anim(dino);
                // Speed 0 means the game paused the animation on purpose (e.g. pooping)
                if (anim == null || anim.speed == 0f || dino.m_agent == null)
                    return;

                int id = dino.GetInstanceID();
                if (!Applies(dino, out bool asSwimmer))
                {
                    if (Adjusted.Remove(id))
                    {
                        BlendVel.Remove(id);
                        anim.speed = 1f;
                        // The game sets the swim speed only once, when the dino is made
                        dino.m_agent.GetWater().SetSwimSpeed(dino.m_waterSpeed);
                    }
                    return;
                }

                NavWater water = dino.m_agent.GetWater();
                bool moving = dino.m_water ? water.m_moving : dino.m_moving;
                float target = SpeciesConfig.Get(dino.m_species,
                    moving ? SpeciesSetting.AnimSpeedMoving : SpeciesSetting.AnimSpeedIdle, asSwimmer);

                float movement = MovementMultiplier(dino, asSwimmer);
                if (dino.m_water)
                    water.m_speed = dino.m_waterSpeed * movement;
                else if (!dino.m_noRootAnim)
                    target *= movement; // root motion: the animation drives the walk

                if (!Adjusted.TryGetValue(id, out float current))
                    current = target;
                BlendVel.TryGetValue(id, out float vel);
                current = Mathf.SmoothDamp(current, Mathf.Clamp(target, 0.05f, 9f), ref vel, ModConfig.AnimBlendTime.Value);
                Adjusted[id] = current;
                BlendVel[id] = vel;
                anim.speed = current;
            }
        }

        // One-off snapshot of every swimmer's animation state (called when the options menu closes),
        // to find out why some dinos end up in a T-pose
        internal static void LogSnapshot()
        {
            foreach (Dinosaur dino in Object.FindObjectsOfType<Dinosaur>())
            {
                if (!DinoUtil.IsWaterDino(dino))
                    continue;
                Animator anim = Anim(dino);
                if (anim == null)
                {
                    Plugin.Log.LogInfo($"[ANIM] {dino.m_objectData.m_name} ({dino.m_species}): no animator");
                    continue;
                }
                AnimatorStateInfo state = anim.GetCurrentAnimatorStateInfo(0);
                Adjusted.TryGetValue(dino.GetInstanceID(), out float ours);
                Plugin.Log.LogInfo($"[ANIM] {dino.m_objectData.m_name} ({dino.m_species}): enabled={anim.enabled} " +
                                   $"active={anim.isActiveAndEnabled} init={anim.isInitialized} controller={(anim.runtimeAnimatorController != null)} " +
                                   $"speed={anim.speed:F2} ours={ours:F2} state={state.fullPathHash} t={state.normalizedTime:F2} " +
                                   $"layers={anim.layerCount} params={anim.parameterCount} " +
                                   $"velX={SafeFloat(anim, "velX")} velY={SafeFloat(anim, "velY")} " +
                                   $"inWater={dino.m_water} swimming={dino.m_agent?.GetWater().m_moving} tranq={dino.m_objectData.m_isTranqed} " +
                                   $"culling={anim.cullingMode}");
            }
        }

        private static string SafeFloat(Animator anim, string name)
        {
            foreach (AnimatorControllerParameter p in anim.parameters)
                if (p.name == name && p.type == AnimatorControllerParameterType.Float)
                    return anim.GetFloat(name).ToString("F2");
            return "-";
        }

        // Non-root-motion species on land: the game sets a fixed NavMeshAgent speed here
        [HarmonyPatch(typeof(Dinosaur), nameof(Dinosaur.OnAnimatorMove))]
        public static class LandMovementSpeed
        {
            public static void Postfix(Dinosaur __instance)
            {
                var dino = __instance;
                if (!dino.m_noRootAnim || dino.m_water || dino.m_agent == null || !Applies(dino, out bool asSwimmer))
                    return;
                dino.m_agent.Speed(dino.m_nonRootSpeed * MovementMultiplier(dino, asSwimmer));
            }
        }
    }
}
