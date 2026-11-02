using System.Collections.Generic;
using BepInEx.Configuration;

namespace ParkasaurusResurrection
{
    // Shape of the terrain tile center point (QuadTerrainTiles)
    public enum EdgeShape
    {
        Off,         // the game's own diagonal: same shape as without the mod
        KeepValleys, // lowest of both diagonals: basins stay deep
        Even,        // average of the 4 corners: symmetric slopes
        KeepRidges   // highest of both diagonals: ridges stay high
    }

    // Settings that can also be set per species (see SpeciesConfig)
    public enum SpeciesSetting
    {
        MovementSpeed,
        AnimSpeedMoving,
        AnimSpeedIdle,
        MaxDiveDepth,
        IdleDepthChangeTime,
        BobbingAmount,
        MinSwimDistance,
        MaxSwimDistance,
        TurnRadius,
        TurnSpeedFactor,
        IdleTurnRate
    }

    // Everything the menu and config file need to know about one setting
    internal class SettingInfo
    {
        public string Key, Label, Description;
        public float Min, Max;
        public bool SwimmersOnly;
        public ConfigEntry<float> Global; // value for all swimmers (null = none)
        public float LandDefault = 1f;    // value for non-swimmers without custom settings
    }

    // All settings of the mod. BepInEx writes the mod default and the acceptable range above every
    // entry in the config file; the description adds what the game itself does without the mod.
    internal static class ModConfig
    {
        private const string Restart = " (restart required)";
        private const string ParkLoad = " (takes effect when a park is loaded)";

        internal static ConfigEntry<bool> ModEnabled;
        internal static ConfigEntry<bool> DisableGameDebugLog;
        internal static ConfigEntry<bool> WideOptionsMenu;

        internal static ConfigEntry<bool> QuadTerrainTiles;
        internal static ConfigEntry<EdgeShape> SmoothEdges;

        internal static ConfigEntry<bool> SmoothSwimDepth;
        internal static ConfigEntry<float> DepthSmoothingTime;
        internal static ConfigEntry<bool> LongerSwims;
        internal static ConfigEntry<float> MinRouteDepth;
        internal static ConfigEntry<bool> TurningCircle;
        internal static ConfigEntry<bool> SmartRoutes;
        internal static ConfigEntry<bool> SpeedAndAnimation;
        internal static ConfigEntry<float> AnimBlendTime;

        internal static ConfigEntry<bool> DiagonalBuilding;
        internal static ConfigEntry<float> StraightSnapAngle;
        internal static ConfigEntry<bool> ShiftPaintsOneArea;
        internal static ConfigEntry<bool> RemoveItemArea;
        internal static ConfigEntry<bool> ParkInfoHistory;
        internal static ConfigEntry<float> ParkInfoTextScale;
        internal static ConfigEntry<bool> ParkInfoLiveIncome;
        internal static ConfigEntry<bool> ParkInfoExtraColumns;

        internal static ConfigEntry<bool> ScrollableInventory;
        internal static ConfigEntry<bool> CalmScrollWheel;
        internal static ConfigEntry<float> ScrollStep;
        internal static ConfigEntry<bool> HideQuestVideos;
        internal static ConfigEntry<bool> RightClickUnequip;
        internal static ConfigEntry<bool> ShiftEmptiesTrash;
        internal static ConfigEntry<bool> TypeNumbers;
        internal static ConfigEntry<bool> NewNames;
        internal static ConfigEntry<bool> ShowHints;
        internal static ConfigEntry<bool> QuietObjects;
        internal static ConfigEntry<bool> KinematicPeople;
        internal static ConfigEntry<bool> NoAutoSyncTransforms;
        internal static ConfigEntry<bool> PhysicsStepFollowsSpeed;

        internal static ConfigEntry<bool> HideDinoHats;
        internal static ConfigEntry<bool> HideDinoNeckGear;

        internal static ConfigEntry<bool> SwimDiagnostics;
        internal static ConfigEntry<bool> PerfProbe;
        internal static ConfigEntry<float> PerfReportSeconds;

        internal static readonly Dictionary<SpeciesSetting, SettingInfo> Settings = new Dictionary<SpeciesSetting, SettingInfo>();

        internal static void Bind(ConfigFile config)
        {
            ModEnabled = config.Bind("General", "ModEnabled", true,
                "Switch the whole mod on or off. The Resurrection options tab stays available." + Restart);
            DisableGameDebugLog = config.Bind("General", "DisableGameDebugLog", true,
                "Stop the game writing debug messages to Player.log (warnings and errors are kept).");

            WideOptionsMenu = config.Bind("General", "WideOptionsMenu", true,
                "Options menu: make it wider while the Resurrection tab is open, with the settings in two columns. Game: narrow, one column");

            QuadTerrainTiles = config.Bind("Terrain", "QuadTerrainTiles", false,
                "Build terrain tiles from 4 triangles so Smooth edges can shape them. Game: 2 triangles." + ParkLoad);
            SmoothEdges = config.Bind("Terrain", "SmoothEdges", EdgeShape.KeepRidges,
                "Shape of sloped tiles. KeepValleys = basins stay deep, Even = symmetric slopes, " +
                "KeepRidges = ridges stay high, Off = the game's own shape. Game: Off");

            SmoothSwimDepth = config.Bind("Swimming", "SmoothSwimDepth", true,
                "Swimmers change depth smoothly while swimming and never rise above the water.");
            LongerSwims = config.Bind("Swimming", "LongerSwims", true,
                "Swimmers pick swim targets further away instead of right next to them.");
            TurningCircle = config.Bind("Swimming", "TurningCircle", true,
                "Swimmers turn in an arc instead of snapping to a new direction.");
            SmartRoutes = config.Bind("Swimming", "SmartRoutes", true,
                "Swimmers find a route through the water around corners, keep to the open water, and don't end a trip " +
                "in a tight corner. Needs TurningCircle. Game: a straight line that stops at the first shallow spot");
            SpeedAndAnimation = config.Bind("Swimming", "SpeedAndAnimation", true,
                "Use the movement and animation speeds of this mod (swimmers and custom species).");

            DepthSmoothingTime = config.Bind("Swimming", "DepthSmoothingTime", 0.37f,
                new ConfigDescription("How smoothly swimmers follow the ups and downs of the bottom, in seconds. Game: no smoothing in shallow water",
                    new AcceptableValueRange<float>(0f, 5f)));
            MinRouteDepth = config.Bind("Swimming", "MinRouteDepth", 1.2f,
                new ConfigDescription("Swimmers only head for a spot if the water all the way there is at least this deep. Game: 0.8",
                    new AcceptableValueRange<float>(0.8f, 10f)));
            AnimBlendTime = config.Bind("Swimming", "AnimBlendTime", 1f,
                new ConfigDescription("Seconds to smoothly switch between the moving and resting animation speed. Game: switches instantly",
                    new AcceptableValueRange<float>(0f, 10f)));

            Add(config, SpeciesSetting.MovementSpeed, "MovementSpeed", "Movement speed",
                "How fast the dino moves (1 = normal speed). Game: 1",
                0.1f, 3f, 1f, swimmersOnly: false);
            Add(config, SpeciesSetting.AnimSpeedMoving, "AnimSpeedMoving", "Animation speed moving",
                "Speed of the swim or walk animation while moving. Game: 1", 0.05f, 3f, 1f, swimmersOnly: false);
            Add(config, SpeciesSetting.AnimSpeedIdle, "AnimSpeedIdle", "Animation speed resting",
                "Speed of the animation while standing or floating still. Game: 1", 0.05f, 3f, 0.5f, swimmersOnly: false);
            Add(config, SpeciesSetting.MaxDiveDepth, "MaxDiveDepth", "Max dive depth",
                "How deep swimmers go, as a part of the water depth (1 = down to the bottom). Game: 1", 0f, 1f, 0.88f);
            Add(config, SpeciesSetting.IdleDepthChangeTime, "IdleDepthChangeTime", "Depth change time",
                "Seconds a resting swimmer takes to move to a new depth. Game: 4", 0.1f, 20f, 3f);
            Add(config, SpeciesSetting.BobbingAmount, "BobbingAmount", "Bobbing",
                "How much resting swimmers gently bob up and down, in meters. Game: 0", 0f, 1f, 0.12f);
            Add(config, SpeciesSetting.MinSwimDistance, "MinSwimDistance", "Shortest swim trip",
                "Shortest distance of a swim trip, in meters. Game: 0", 1f, 29f, 12f);
            Add(config, SpeciesSetting.MaxSwimDistance, "MaxSwimDistance", "Longest swim trip",
                "Longest distance of a swim trip, in meters. Game: about 12", 1f, 29f, 25f);
            Add(config, SpeciesSetting.TurnRadius, "TurnRadius", "Turn radius",
                "Size of the turning curve while swimming, in meters. Game: 0 (turns on the spot)", 0.5f, 50f, 6f);
            Add(config, SpeciesSetting.TurnSpeedFactor, "TurnSpeedFactor", "Speed in turns",
                "How much speed swimmers keep in sharp turns (1 = full speed). Game: 1", 0.1f, 1f, 0.75f);
            Add(config, SpeciesSetting.IdleTurnRate, "IdleTurnRate", "Turn speed resting",
                "How fast a resting swimmer turns around, in degrees per second. Game: turns instantly", 5f, 720f, 60f);

            DiagonalBuilding = config.Bind("Building", "DiagonalFencesAndPaths", true,
                "Dragging fences or paths diagonally places a staircase instead of a straight line; hold Ctrl to flip it. Game: always straight");

            StraightSnapAngle = config.Bind("Building", "StraightSnapAngle", 10f,
                new ConfigDescription("Diagonal fences and paths: a drag closer than this many degrees to straight stays a straight line. " +
                    "Game: always straight; mod before 0.25: 22.5",
                    new AcceptableValueRange<float>(1f, 44f)));

            ShiftPaintsOneArea = config.Bind("Building", "ShiftPaintsOneArea", true,
                "Hold Shift while painting terrain to only change tiles in the exhibit where the drag starts " +
                "(or only outside exhibits, when it starts outside). Game: paints everything under the brush");
            RemoveItemArea = config.Bind("Building", "RemoveItemArea", true,
                "Remove Item tool: drag a rectangle (from empty ground, or with Shift) to mark everything in it; Alt or Ctrl unmarks. " +
                "Removed with the usual Destroy button. Game: one object at a time");

            ParkInfoHistory = config.Bind("ParkInfo", "History", true,
                "Park Info (office computer): browse the last 6 days and compare them (+N / -N). " +
                "Also keeps recording daily stats after day 100. Game: today only, stops recording at day 100");
            ParkInfoLiveIncome = config.Bind("ParkInfo", "LiveIncome", true,
                "Park Info: fill the income list during the day with today's income and expenses so far. " +
                "Game: empty until the end-of-day tally");
            ParkInfoExtraColumns = config.Bind("ParkInfo", "ExtraColumns", true,
                "Staff and dino lists (Employee View, Park Info): an Occupation column with what each employee is doing, " +
                "and a Hat column with the hat each dino wears. Hover for the full text or the hat's effect. Game: no such columns");
            ParkInfoTextScale = config.Bind("ParkInfo", "TextScale", 0.8f,
                new ConfigDescription("Size of all text on the Park Info screen. Game: 1",
                    new AcceptableValueRange<float>(0.5f, 1f)));
            ParkInfoTextScale.SettingChanged += (s, e) => ParkasaurusResurrection.ParkInfoTextScale.ApplyToAll();

            ScrollableInventory = config.Bind("Interface", "ScrollableInventory", true,
                "Inventory gets extra slots when it's full and scrolls with the mouse wheel, one row per notch. " +
                "Game: 47 slots, the rest stays hidden until a slot frees up");
            CalmScrollWheel = config.Bind("Interface", "CalmScrollWheel", true,
                "Scroll lists move a fixed part of their height per wheel notch. Game: often the whole list per notch");
            ScrollStep = config.Bind("Interface", "ScrollStep", 0.15f,
                new ConfigDescription("Part of the visible list height to scroll per wheel notch (CalmScrollWheel).",
                    new AcceptableValueRange<float>(0.02f, 1f)));
            HideQuestVideos = config.Bind("Interface", "HideQuestVideos", true,
                "Don't play the small tutorial videos in the quest panel. Under Proton they crash the game " +
                "(e.g. in the Space mission). Game: shown");
            RightClickUnequip = config.Bind("Interface", "RightClickUnequip", true,
                "Right-click a hat on a dino or staff member, or an item on an egg, to put it back in the inventory. " +
                "Game: drag it back to an inventory slot");
            ShiftEmptiesTrash = config.Bind("Interface", "ShiftEmptiesTrash", true,
                "Shift + click the trash slot in the Inventory or Storage window to delete everything in it, after a confirmation. " +
                "Game: one item stack at a time");
            TypeNumbers = config.Bind("Interface", "TypeNumbers", true,
                "Click the number of a slider (e.g. the scenery variant) or of a shop item's daily amount to type a value. " +
                "Game: only + / - buttons or dragging");
            KinematicPeople = config.Bind("Performance", "LightPeoplePhysics", true,
                "Visitors, staff and cars get a kinematic physics body (like dinos), and only touch what they need to: " +
                "people only doors, cars nothing. Physics no longer gets heavier with every visitor and car. " +
                "Game: they move as 'static' colliders");
            PhysicsStepFollowsSpeed = config.Bind("Performance", "PhysicsStepFollowsSpeed", true,
                "At higher game speeds, run physics in larger steps so it runs about 50 times per real second instead of " +
                "50 times per game second (115 times per second at 2.3x speed). Game: 50 steps per game second");
            NoAutoSyncTransforms = config.Bind("Performance", "NoAutoSyncTransforms", true,
                "Sync moved objects to physics once per physics step instead of before every raycast (Unity's own default). " +
                "Game: before every raycast");
            NoAutoSyncTransforms.SettingChanged += (s, e) => PhysicsPatch.ApplySync();
            QuietObjects = config.Bind("Interface", "QuietObjects", true,
                "Scenery (trees, bushes, rocks, theme items), dinosaur items (sheds, toys, nests; feeders stay clickable), decorations, lights, bridges, " +
                "benches, bins, heaters, gazebos, picnic tables and doors can't be " +
                "clicked or hovered during normal play, so clicks reach what's behind them. They are clickable with a " +
                "Destruction tool or the Move Item tool. Game: always clickable");
            ShowHints = config.Bind("Interface", "ShowHints", true,
                "Show the controls of this mod's features in the bottom right corner, next to the game's own building tips.");
            NewNames = config.Bind("Names", "NewNames", true,
                "New dinos get pet names (Biscuit, Sir Chomps-a-Lot, Mabel, ...); new staff get the names the game gives dinos " +
                "(Atticus, Hazel, ...), matching their face. Existing names stay. Game: dinos Atticus/Hazel, staff made-up star names");

            HideDinoHats = config.Bind("Visuals", "HideDinoHats", true,
                "Hide the hats dinos wear. The hat's effect keeps working. Game: shown");
            HideDinoNeckGear = config.Bind("Visuals", "HideDinoNeckGear", true,
                "Hide the neck items dinos wear. The item's effect keeps working. Game: shown");
            // Apply right away when changed (menu or reload)
            HideDinoHats.SettingChanged += (s, e) => HideHatsPatch.ApplyToAll();
            HideDinoNeckGear.SettingChanged += (s, e) => HideHatsPatch.ApplyToAll();

            SwimDiagnostics = config.Bind("Debug", "SwimDiagnostics", false,
                "Log swim events to LogOutput.log. Debugging only: writes a lot.");
            PerfProbe = config.Bind("Debug", "PerfProbe", false,
                "Measure what takes time (game scripts, hitches, scene, textures, meshes) and write it to BepInEx/perf/. " +
                "For finding slow parts; makes the game itself a little slower and the start a few seconds longer." + Restart);
            PerfReportSeconds = config.Bind("Debug", "PerfReportSeconds", 60f,
                new ConfigDescription("PerfProbe: seconds between reports.", new AcceptableValueRange<float>(10f, 600f)));

            SpeciesConfig.BindExisting(config);
        }

        // Settings in the [Swimming] section apply to all swimmers; species can override them
        private static void Add(ConfigFile config, SpeciesSetting id, string key, string label, string description,
            float min, float max, float swimmerDefault, bool swimmersOnly = true)
        {
            Settings[id] = new SettingInfo
            {
                Key = key,
                Label = label,
                Description = description,
                Min = min,
                Max = max,
                SwimmersOnly = swimmersOnly,
                Global = config.Bind("Swimming", key, swimmerDefault,
                    new ConfigDescription(description + " (all swimmers)", new AcceptableValueRange<float>(min, max)))
            };
        }
    }
}
