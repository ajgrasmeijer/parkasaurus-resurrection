using System;
using BepInEx.Configuration;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Features are switched while the game runs (options menu or the reload hotkey): every patch
    // checks its own setting, and this undoes what a feature left behind when it's switched off,
    // or catches up on what it missed when it's switched on.
    // Still needing a restart: ModEnabled (decides whether the patches are applied at all) and
    // PerfProbe. QuadTerrainTiles is decided when a park loads (TerrainQuadPatch.ActiveThisPark).
    internal static class LiveSettings
    {
        internal static void Hook()
        {
            On(ModConfig.DisableGameDebugLog, ApplyGameDebugLog);
            On(ModConfig.SmoothSwimDepth, SwimHeightPatch.Reset);
            On(ModConfig.SwimDiagnostics, DiagnosePatch.Reset);
            On(ModConfig.TurningCircle, SwimSteeringPatch.Reset);
            On(ModConfig.SmartRoutes, SwimSteeringPatch.Reset);
            On(ModConfig.KinematicPeople, PhysicsPatch.ApplyKinematicPeople);
            On(ModConfig.TypeNumbers, NumberInputPatch.ApplyToAll);
            On(ModConfig.ParkInfoHistory, ParkHistoryPatch.ApplyEnabled);
            On(ModConfig.ParkInfoLiveIncome, LiveIncomePatch.ApplyEnabled);
            On(ModConfig.ParkInfoExtraColumns, OverviewColumnsPatch.ApplyEnabled);
            foreach (ConfigEntry<bool> feature in new[]
                     {
                         ModConfig.LongerSwims, ModConfig.SpeedAndAnimation, ModConfig.ShiftPaintsOneArea,
                         ModConfig.ScrollableInventory, ModConfig.CalmScrollWheel, ModConfig.NewNames
                     })
                On(feature, null);
        }

        private static void On(ConfigEntry<bool> feature, Action apply)
        {
            feature.SettingChanged += (s, e) =>
            {
                Plugin.Log.LogInfo($"Feature {(feature.Value ? "ON" : "OFF")}: {feature.Definition.Key}");
                try
                {
                    apply?.Invoke();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Switching {feature.Definition.Key}: {ex}");
                }
            };
        }

        // The game writes thousands of debug messages (e.g. "ShowObjectHover" on every mouse
        // hover) to Player.log. Skipping normal messages saves string building and disk
        // writes; warnings and errors are still logged.
        internal static void ApplyGameDebugLog()
        {
            bool quiet = Plugin.ModEnabledAtStart && ModConfig.DisableGameDebugLog.Value;
            Debug.unityLogger.filterLogType = quiet ? LogType.Warning : LogType.Log;
            Plugin.Log.LogInfo(quiet ? "Game debug log disabled" : "Game debug log enabled");
        }
    }
}
