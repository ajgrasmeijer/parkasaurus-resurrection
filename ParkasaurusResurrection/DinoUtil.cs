namespace ParkasaurusResurrection
{
    internal static class DinoUtil
    {
        // Same check as the game's private Dinosaur.IsWaterDino()
        public static bool IsWaterDino(Dinosaur dino)
        {
            bool water = dino.m_classFlag == DinoClassFlag.Aquatic || dino.m_classFlag == DinoClassFlag.SemiAquatic;
            if (water)
                SpeciesConfig.RegisterSwimmer(dino.m_species);
            return water;
        }
    }
}
