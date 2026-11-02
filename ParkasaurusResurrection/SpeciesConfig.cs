using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Per-species overrides, stored in [Species.<Name>] sections. A section only exists once
    // "Custom settings" has been switched on for that species; without it the species uses the
    // [Swimming] values (swimmers) or the game's own behaviour (everything else).
    internal static class SpeciesConfig
    {
        private const string SectionPrefix = "Species.";

        private static ConfigFile s_config;
        private static readonly Dictionary<Species, ConfigEntry<bool>> Custom = new Dictionary<Species, ConfigEntry<bool>>();
        private static readonly Dictionary<Species, Dictionary<SpeciesSetting, ConfigEntry<float>>> Values =
            new Dictionary<Species, Dictionary<SpeciesSetting, ConfigEntry<float>>>();

        // Sea monsters; semi-aquatic land species are added when they're seen swimming
        private static readonly HashSet<Species> Swimmers = new HashSet<Species>
        {
            Species.Mosasaurus, Species.Elasmosaurus, Species.Plesiosaurus, Species.Atopodentatus,
            Species.Sarcosuchus, Species.Archelon, Species.Dunkleosteus, Species.Liopleurodon,
            Species.Ichthyosaurus, Species.Placodus, Species.Megalodon
        };

        // Built-in custom settings: these species start with CustomSettings on and these values
        // (everything not listed starts at the [Swimming] value)
        private static readonly Dictionary<Species, Dictionary<SpeciesSetting, float>> Presets =
            new Dictionary<Species, Dictionary<SpeciesSetting, float>>
            {
                [Species.Mosasaurus] = new Dictionary<SpeciesSetting, float>
                {
                    [SpeciesSetting.MovementSpeed] = 0.96f,
                    [SpeciesSetting.AnimSpeedMoving] = 0.43f,
                    [SpeciesSetting.AnimSpeedIdle] = 0.34f,
                    [SpeciesSetting.MaxDiveDepth] = 0.75f,
                    [SpeciesSetting.IdleTurnRate] = 62.46f
                }
            };

        internal static IEnumerable<Species> AllSpecies =>
            ((Species[])Enum.GetValues(typeof(Species)))
            .Where(s => s != Species.Generic && s != Species.Count)
            .OrderBy(s => s.ToString());

        internal static bool IsSwimmer(Species species) => Swimmers.Contains(species);

        internal static void RegisterSwimmer(Species species) => Swimmers.Add(species);

        internal static bool HasCustom(Species species) =>
            Custom.TryGetValue(species, out var entry) && entry.Value;

        // Effective value for one dino
        internal static float Get(Species species, SpeciesSetting setting, bool isSwimmer)
        {
            if (HasCustom(species) && Values[species].TryGetValue(setting, out var entry))
                return entry.Value;
            return Default(setting, isSwimmer);
        }

        internal static float Default(SpeciesSetting setting, bool isSwimmer)
        {
            SettingInfo info = ModConfig.Settings[setting];
            return isSwimmer ? info.Global.Value : info.LandDefault;
        }

        internal static ConfigEntry<float> Entry(Species species, SpeciesSetting setting)
        {
            return Values.TryGetValue(species, out var map) && map.TryGetValue(setting, out var entry) ? entry : null;
        }

        // Switches custom settings on/off; the first time, the values start at what the species uses now
        internal static void SetCustom(Species species, bool on)
        {
            EnsureBound(species);
            Custom[species].Value = on;
        }

        private static void EnsureBound(Species species)
        {
            if (Custom.ContainsKey(species))
                return;
            string section = SectionPrefix + species;
            bool swimmer = IsSwimmer(species);
            Presets.TryGetValue(species, out var preset);
            Custom[species] = s_config.Bind(section, "CustomSettings", preset != null,
                "Use the values below instead of the [Swimming] values / the game's behaviour.");
            var map = new Dictionary<SpeciesSetting, ConfigEntry<float>>();
            foreach (var pair in ModConfig.Settings)
            {
                SettingInfo info = pair.Value;
                if (info.SwimmersOnly && !swimmer)
                    continue;
                float start = preset != null && preset.TryGetValue(pair.Key, out float p) ? p : Default(pair.Key, swimmer);
                map[pair.Key] = s_config.Bind(section, info.Key, start,
                    new ConfigDescription(info.Description, new AcceptableValueRange<float>(info.Min, info.Max)));
            }
            Values[species] = map;
        }

        // Binds the species sections that are already in the config file
        internal static void BindExisting(ConfigFile config)
        {
            s_config = config;
            foreach (Species species in Presets.Keys)
                EnsureBound(species);
            if (!File.Exists(config.ConfigFilePath))
                return;
            foreach (Match m in Regex.Matches(File.ReadAllText(config.ConfigFilePath), @"^\[" + Regex.Escape(SectionPrefix) + @"(\w+)\]", RegexOptions.Multiline))
            {
                if (Enum.TryParse(m.Groups[1].Value, out Species species))
                    EnsureBound(species);
            }
        }

        // "TyrannosaurusRex" -> "Tyrannosaurus Rex"
        internal static string DisplayName(Species species) =>
            Regex.Replace(species.ToString(), "(?<=[a-z])(?=[A-Z])", " ");

        // Min/max swim distance for one dino, valid even if min is set above max
        internal static void SwimDistance(Species species, out float min, out float max)
        {
            float a = Get(species, SpeciesSetting.MinSwimDistance, true);
            float b = Get(species, SpeciesSetting.MaxSwimDistance, true);
            min = Mathf.Min(a, b);
            max = Mathf.Max(a, b);
        }
    }
}
