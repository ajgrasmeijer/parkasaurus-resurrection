using HarmonyLib;

namespace ParkasaurusResurrection
{
    [HarmonyPatch(typeof(GameCore), "Update")]
    public static class HotkeyPatch
    {
        public static void Postfix()
        {
            Plugin.CheckHotkeys();
            PerfProbe.Frame();
            PhysicsPatch.UpdateStep();
        }
    }
}
