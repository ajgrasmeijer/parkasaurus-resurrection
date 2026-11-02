using HarmonyLib;

namespace ParkasaurusResurrection
{
    // A tranquilized dino can always be picked up. The game ignores a left-click on the selected object
    // (SelectPark.LeftCLick skips Clicked when UICore.m_focused is the clicked object), so a dino whose
    // info panel was open when it got tranquilized didn't react to clicks on it or on its pick-up bubble.
    public static class TranqPickupPatch
    {
        [HarmonyPatch(typeof(SelectPark), nameof(SelectPark.LeftCLick))]
        public static class OnClick
        {
            public static bool Prefix(SelectPark __instance)
            {
                if (UICore.Get.m_deleting || __instance.m_root == null || UICore.Get.m_focused != __instance.m_root)
                    return true;
                Dinosaur dino = __instance.m_root.GetComponent<Dinosaur>();
                if (dino == null || !dino.m_objectData.m_readyToPickup)
                    return true;
                __instance.Clicked();
                return false;
            }
        }
    }
}
