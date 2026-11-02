using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Names:
    // - Staff get the names the game gives dinos (Resources NamesDino / NamesDinoFemale: Atticus,
    //   Hazel, ...), matching their face (EmployeeInfo.m_sex, 1 = female). The game generates staff
    //   names from star and city names (Names0 through a Markov chain, NChainGenerator.GetNextN).
    // - Dinos get pet names from the lists below (GameCore.GetDinoName: eggs, nests, new dinos).
    // Existing names in a save don't change. Not used in the Chinese version (Global.m_china).
    public static class NamesPatch
    {
        private static string[] s_staffMale;
        private static string[] s_staffFemale;

        [HarmonyPatch(typeof(EmployeeUI), nameof(EmployeeUI.MakeEmployee))]
        public static class StaffName
        {
            public static void Postfix(ref EmployeeInfo newEmp)
            {
                if (!ModConfig.NewNames.Value || Global.m_china)
                    return;
                if (s_staffMale == null)
                {
                    s_staffMale = Load("NamesDino");
                    s_staffFemale = Load("NamesDinoFemale");
                }
                string[] list = newEmp.m_sex == 1 ? s_staffFemale : s_staffMale;
                if (list.Length > 0)
                    newEmp.m_name = Pick(list, UsedStaffNames());
            }
        }

        [HarmonyPatch(typeof(GameCore), nameof(GameCore.GetDinoName))]
        public static class DinoName
        {
            public static bool Prefix(bool female, ref string __result)
            {
                if (!ModConfig.NewNames.Value || Global.m_china)
                    return true;
                string[] own = female ? Female : Male;
                // Two thirds from the male/female list, one third from the shared one
                string[] list = UnityEngine.Random.value < 0.66f ? own : Shared;
                __result = Pick(list, UsedDinoNames());
                return false;
            }
        }

        private static string[] Load(string resource)
        {
            var asset = Resources.Load(resource) as TextAsset;
            if (asset == null)
                return Array.Empty<string>();
            return asset.text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
        }

        // A random name, preferring one nobody in the park has yet
        private static string Pick(string[] list, HashSet<string> used)
        {
            string name = list[UnityEngine.Random.Range(0, list.Length)];
            for (int i = 0; i < 20 && used.Contains(name); i++)
                name = list[UnityEngine.Random.Range(0, list.Length)];
            return name;
        }

        private static HashSet<string> UsedDinoNames()
        {
            var used = new HashSet<string>();
            foreach (Dinosaur dino in UnityEngine.Object.FindObjectsOfType<Dinosaur>())
                if (dino.m_objectData != null && dino.m_objectData.m_name != null)
                    used.Add(dino.m_objectData.m_name);
            return used;
        }

        private static HashSet<string> UsedStaffNames()
        {
            var used = new HashSet<string>();
            foreach (Employee employee in UnityEngine.Object.FindObjectsOfType<Employee>())
                if (employee.m_objectData?.m_info?.m_name != null)
                    used.Add(employee.m_objectData.m_info.m_name);
            return used;
        }

        private static readonly string[] Male =
        {
            "Barnaby", "Bartholomew", "Mortimer", "Percival", "Reginald", "Archibald", "Cornelius", "Ignatius",
            "Thaddeus", "Fitzgerald", "Montgomery", "Humphrey", "Ferdinand", "Wilbur", "Winston", "Rupert",
            "Otis", "Milo", "Ziggy", "Bruno", "Rocco", "Moose", "Tank", "Bubba", "Chester", "Hank", "Waldo",
            "Ozzy", "Pippin", "Spike", "Fang", "Diesel", "Turbo", "Bronco", "Meatloaf", "Kevin", "Gary",
            "Nigel", "Clive", "Trevor", "Derek", "Doug", "Big Steve", "Mr. Pickles", "Sir Chomps-a-Lot",
            "Lord Nibbles", "Baron Snuggles", "Count Snackula", "Captain Wiggles", "Professor Snuffles",
            "Sergeant Stomps", "Dr. Stompington", "Duke Fluffington", "King Crunch", "Old Gregory",
            "Little Jimmy", "Uncle Burt", "Gordo", "Rolo", "Taco Tuesday", "Hamish", "Fergus", "Angus",
            "Boris", "Igor", "Vladimir", "Gustav", "Sven", "Lars", "Bjorn", "Klaus", "Helmut", "Enzo",
            "Guido", "Giuseppe", "Pablo", "Chuck", "Buzz", "Rex", "Tex", "Earl", "Merle", "Floyd",
        };

        private static readonly string[] Female =
        {
            "Mabel", "Matilda", "Gertrude", "Ethel", "Agatha", "Beatrix", "Henrietta", "Petunia", "Marigold",
            "Buttercup", "Dolly", "Peaches", "Lulu", "Coco", "Fifi", "Gigi", "Bijou", "Trixie", "Roxy",
            "Ruby", "Olive", "Poppy", "Penny", "Rosie", "Tilly", "Winnie", "Nellie", "Queenie", "Bertha",
            "Edna", "Doris", "Myrtle", "Prudence", "Philippa", "Wilhelmina", "Ophelia", "Esmeralda",
            "Lady Nibbles", "Princess Chompy", "Duchess Wiggles", "Countess Crunch", "Madame Stomp",
            "Dame Chompsalot", "Granny Smith", "Auntie Pam", "Miss Fluffington", "Queen Snackalot",
            "Snickerdoodle", "Sugarplum", "Lollipop", "Taffy", "Truffle", "Praline", "Meringue", "Bonbon",
            "Cupcake", "Jellybean", "Honeybun", "Cinnamon", "Saffron", "Paprika", "Pistachia", "Petra",
            "Brunhilde", "Helga", "Ingrid", "Greta", "Svea", "Babette", "Colette", "Fiona", "Gloria",
            "Bubbles", "Twinkle", "Sparkle", "Glitter", "Pixie", "Daphne", "Peggy", "Barbara",
        };

        private static readonly string[] Shared =
        {
            "Biscuit", "Pickles", "Noodle", "Waffles", "Nugget", "Muffin", "Pretzel", "Tater Tot", "Meatball",
            "Dumpling", "Pudding", "Crumpet", "Bagel", "Toast", "Nacho", "Burrito", "Pancake", "Flapjack",
            "Peanut", "Cashew", "Gumdrop", "Marshmallow", "Sprinkles", "Brownie", "Cookie", "Fudge", "Mochi",
            "Wasabi", "Tofu", "Kimchi", "Ravioli", "Gnocchi", "Pesto", "Nutmeg", "Ginger", "Pepper",
            "Mustard", "Ketchup", "Churro", "Falafel", "Hummus", "Pierogi", "Wonton", "Gyoza", "Strudel",
            "Squiggles", "Wiggles", "Snuggles", "Puddles", "Sprout", "Pebble", "Boulder", "Tumbleweed",
            "Thistle", "Acorn", "Pinecone", "Sputnik", "Rocket", "Comet", "Zigzag", "Doodle", "Scribbles",
            "Kazoo", "Banjo", "Ukulele", "Tuba", "Chompers", "Stompy", "Rumble", "Grumbles", "Snorkel",
            "Wobble", "Fizz", "Fidget", "Gizmo", "Widget", "Sprocket", "Bumble", "Mango", "Kiwi", "Coconut",
            "Turnip", "Radish", "Parsnip", "Zucchini", "Rutabaga", "Kumquat", "Butterscotch", "Toffee",
            "Sir Barksalot", "Chewy", "Nibbles", "Munchkin", "Gobbles", "Smidge", "Tiny", "Mr. Snuffles",
        };
    }
}
