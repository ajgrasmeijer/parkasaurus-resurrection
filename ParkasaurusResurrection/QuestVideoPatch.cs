using HarmonyLib;

namespace ParkasaurusResurrection
{
    // Hides the small tutorial videos in the quest panel (SubQuestChild.m_breadvideoClip, shown by
    // QuestUI.SetVideo). They are AV1 clips; under Proton, Wine's Media Foundation can't decode them
    // and Unity crashes in WindowsVideoTextureOutput.ReleaseDrawnSamples (the Space mission, which
    // uses them). The game itself skips them the same way when Global.m_singleThreaded is set.
    public static class QuestVideoPatch
    {
        [HarmonyPatch(typeof(QuestUI), nameof(QuestUI.SetVideo))]
        public static class SetVideo
        {
            public static bool Prefix(QuestUI __instance)
            {
                if (!ModConfig.HideQuestVideos.Value)
                    return true;
                if (__instance.m_videoPlayer != null)
                {
                    __instance.m_videoPlayer.Stop();
                    __instance.m_videoPlayer.clip = null;
                }
                if (__instance.m_video != null)
                    __instance.m_video.SetActive(false);
                return false;
            }
        }
    }
}
