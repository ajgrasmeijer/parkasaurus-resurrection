using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Hides the 3D model of dino head gear (hats) and neck gear. Only the model is hidden: the
    // hat's effect comes from the dino's data (DinosaurObject.m_headItem) and keeps working.
    public static class HideHatsPatch
    {
        // EquipHead creates the model (on load and whenever a hat is put on or taken off)
        [HarmonyPatch(typeof(Dinosaur), nameof(Dinosaur.EquipHead))]
        public static class OnEquip
        {
            public static void Postfix(Dinosaur __instance) => Apply(__instance);
        }

        internal static void ApplyToAll()
        {
            if (!Plugin.ModEnabledAtStart)
                return;
            foreach (Dinosaur dino in Object.FindObjectsOfType<Dinosaur>())
                Apply(dino);
        }

        private static void Apply(Dinosaur dino)
        {
            if (dino.m_headMade == null)
                return;
            ZooItemObject item = dino.m_objectData.m_headItem.GetObj();
            if (item == null)
                return;
            bool hide = item.m_type == ZooItemType.Dino_NeckGear
                ? ModConfig.HideDinoNeckGear.Value
                : ModConfig.HideDinoHats.Value;
            dino.m_headMade.SetActive(!hide);
        }
    }
}
