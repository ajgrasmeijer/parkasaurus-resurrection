using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    [BepInPlugin("nl.alber.parkasaurusresurrection", "Parkasaurus Resurrection", "0.43.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            TakeOverOldConfig();
            ModConfig.Bind(Config);
            // Settings changed in the options menu are written once when the menu closes
            Config.SaveOnConfigSet = false;
            Log.LogInfo("Parkasaurus Resurrection loaded");

            ModEnabledAtStart = ModConfig.ModEnabled.Value;
            LiveSettings.ApplyGameDebugLog();

            // The Resurrection options tab and the reload hotkey are always there, so the mod can be
            // switched back on from inside the game
            var harmony = new Harmony("nl.alber.parkasaurusresurrection");
            harmony.CreateClassProcessor(typeof(HotkeyPatch)).Patch();
            foreach (Type patch in new[] { typeof(OptionsMenuPatch.BuildTab), typeof(OptionsMenuPatch.RefreshTab), typeof(OptionsMenuPatch.PaneChanged), typeof(OptionsMenuPatch.SaveOnClose) })
                harmony.CreateClassProcessor(patch).Patch();

            if (!ModEnabledAtStart)
            {
                Log.LogInfo("Mod switched off (General.ModEnabled = false)");
                return;
            }

            harmony.CreateClassProcessor(typeof(SceneCleanupPatch)).Patch();
            NavMeshWarningProbe.Start(); // temporary: finds the source of the NavMesh warning flood

            // Every patch is always in place; each feature checks its own setting, so switching it
            // in the options menu works right away (see LiveSettings)
            Patch(harmony, typeof(SwimHeightPatch));
            Patch(harmony, typeof(LongerSwimsPatch.WalkRandomMoveNext), typeof(LongerSwimsPatch.DoingThisSetPatch));
            Patch(harmony, typeof(SwimSteeringPatch.SwimNowPatch), typeof(SwimSteeringPatch.SmoothRotation),
                typeof(SwimSteeringPatch.SmartRoutes));
            Patch(harmony, typeof(AnimSpeedPatch.AnimationSpeed), typeof(AnimSpeedPatch.LandMovementSpeed));
            // Smooth slopes changes the terrain buffers when a park loads, so it's decided per park
            Patch(harmony, typeof(TerrainQuadPatch), typeof(TerrainCenterHeightPatch.EnlargeBuffer),
                typeof(TerrainCenterHeightPatch.UploadCenters));
            Patch(harmony, typeof(TerrainPaintPatch.Preview), typeof(TerrainPaintPatch.DrawArea), typeof(TerrainPaintPatch.Placing));
            Patch(harmony, typeof(InventoryScrollPatch.SlotsAndViewport), typeof(InventoryScrollPatch.ResetOnOpen),
                typeof(InventoryScrollPatch.Wheel), typeof(InventoryScrollPatch.ValidSlot), typeof(InventoryScrollPatch.SaveCompatible));
            Patch(harmony, typeof(ScrollWheelFix));
            harmony.CreateClassProcessor(typeof(HideHatsPatch.OnEquip)).Patch();
            harmony.CreateClassProcessor(typeof(QuestVideoPatch.SetVideo)).Patch();
            harmony.CreateClassProcessor(typeof(RightClickUnequipPatch.OnClick)).Patch();
            harmony.CreateClassProcessor(typeof(EmptyTrashPatch.OnClick)).Patch();
            harmony.CreateClassProcessor(typeof(TranqPickupPatch.OnClick)).Patch();
            harmony.CreateClassProcessor(typeof(RemoveAreaPatch.AfterTool)).Patch();
            harmony.CreateClassProcessor(typeof(ModHints.Show)).Patch();
            foreach (Type patch in new[] { typeof(MoveToolPatch.AddTool), typeof(MoveToolPatch.Choose), typeof(MoveToolPatch.OtherTool),
                         typeof(MoveToolPatch.OtherTab), typeof(MoveToolPatch.BackOut),
                         typeof(MoveToolPatch.SelectClick), typeof(MoveToolPatch.SelectHover), typeof(MoveToolPatch.ThingClick),
                         typeof(MoveToolPatch.ThingHover), typeof(MoveToolPatch.Register), typeof(MoveToolPatch.EveryFrame) })
                harmony.CreateClassProcessor(patch).Patch();
            Patch(harmony, typeof(PhysicsPatch.VisitorBody), typeof(PhysicsPatch.StaffBody), typeof(PhysicsPatch.CarBody));
            if (ModConfig.KinematicPeople.Value)
                PhysicsPatch.SetCollisionLayers(true);
            PhysicsPatch.ApplySync();
            Patch(harmony, typeof(NumberInputPatch.ShopCard), typeof(NumberInputPatch.Slider), typeof(NumberInputPatch.Typing));
            Patch(harmony, typeof(NamesPatch.StaffName), typeof(NamesPatch.DinoName));
            harmony.CreateClassProcessor(typeof(DiagonalBuildPatch.ReadyPlaceEnd)).Patch();
            Patch(harmony, typeof(ParkHistoryPatch.RollingHistory), typeof(ParkHistoryPatch.StatBlock), typeof(ParkHistoryPatch.Bar),
                typeof(ParkHistoryPatch.BarPlacement), typeof(ParkHistoryPatch.ScienceList), typeof(ParkHistoryPatch.Reviews),
                typeof(ParkHistoryData.Record));
            Patch(harmony, typeof(LiveIncomePatch.OnOpen), typeof(LiveIncomePatch.WhileOpen), typeof(LiveIncomePatch.Tally));
            Patch(harmony, OverviewColumnsPatch.Patches);
            harmony.CreateClassProcessor(typeof(ParkInfoTextScale.OnOpen)).Patch();
            harmony.CreateClassProcessor(typeof(ParkInfoTextScale.WhileOpen)).Patch();
            if (ModConfig.PerfProbe.Value)
                PerfProbe.Start(harmony);
            Patch(harmony, typeof(DiagnosePatch), typeof(DiagnosePatch.MeasureHeight));
            LiveSettings.Hook();
        }

        // Until 0.27 the mod was called Sea Monster Fix: on the first start under the new name,
        // copy its settings over (the old file is left as it is)
        private void TakeOverOldConfig()
        {
            string oldPath = Path.Combine(Paths.ConfigPath, "nl.alber.seamonsterfix.cfg");
            if (File.Exists(Config.ConfigFilePath) || !File.Exists(oldPath))
                return;
            File.Copy(oldPath, Config.ConfigFilePath);
            Config.Reload();
            Log.LogInfo("Settings taken over from nl.alber.seamonsterfix.cfg");
        }

        // ModEnabled as it was at startup (patches can't be switched without a restart)
        internal static bool ModEnabledAtStart { get; private set; }

        private static void Patch(Harmony harmony, params Type[] patchClasses)
        {
            foreach (Type patchClass in patchClasses)
                harmony.CreateClassProcessor(patchClass).Patch();
        }

        // Called from GameCore.Update (see HotkeyPatch): the game can destroy BepInEx's own
        // manager object, so a MonoBehaviour Update on the plugin isn't reliable.
        // The game uses Unity's new Input System; the old UnityEngine.Input API throws here.
        internal static void CheckHotkeys()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null || NumberInputPatch.IsTyping())
                return;

            // NUM_0: re-read the config file and apply it (also re-renders the terrain heights)
            if (keyboard.numpad0Key.wasPressedThisFrame)
                ReloadConfig();
        }

        private static void ReloadConfig()
        {
            Instance.Config.Reload();
            // Re-upload the terrain heights so a changed Smooth edges setting shows immediately
            if (GameCore.Get != null)
                GameCore.Get.m_heightGPUSync = true;
            TerrainCenterHeightPatch.VerifyNextUpload = true;
            Log.LogInfo($"Config reloaded (smooth edges: {ModConfig.SmoothEdges.Value})");
        }
    }
}
