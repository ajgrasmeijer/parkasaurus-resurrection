using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Money of one day as the Park Info income list shows it: a signed total per category
    // (expenses negative) plus the exhibit donations, which the game shows in their own row
    internal class MoneyDay
    {
        internal const string DonationsName = "Exhibit Donations";

        public readonly Dictionary<string, int> Rows = new Dictionary<string, int>();
        public readonly HashSet<string> Expenses = new HashSet<string>();
        public int Donations;

        public int Total => Rows.Values.Sum() + Donations;

        public void Add(string name, bool revenue, int amount)
        {
            if (revenue && name == DonationsName)
            {
                Donations += amount;
                return;
            }
            if (!revenue)
                Expenses.Add(name);
            Rows.TryGetValue(name, out int total);
            Rows[name] = total + (revenue ? amount : -amount);
        }

        public int Get(string name) => Rows.TryGetValue(name, out int v) ? v : 0;
    }

    // The game keeps per day only its 29 counters (Metric.m_data, an int[50]). For Park Info history
    // the mod adds, at the end-of-day summary, what it needs on top, in the same array so it's saved
    // with the park and the save still loads without the mod:
    //   [30] marker, [31] park appeal, [32] stars, [33] reviews that day, [34] reviews in total,
    //   [50...] the income list: count, then per category: revenue (1) or expense (0), amount,
    //           name length, the name's characters.
    // Days from before this was recorded: the money comes from the game's own week data
    // (SuperGraph.m_currentWeek, one slot per weekday, cleared every 7 days) while that still has
    // the day; appeal and stars aren't known.
    internal static class ParkHistoryData
    {
        private const int Marker = 30, Appeal = 31, Stars = 32, ReviewsToday = 33, ReviewsTotal = 34;
        private const int MarkerValue = 7301; // "recorded by the mod, layout 1"
        private const int MoneyStart = 50;

        [HarmonyPatch(typeof(MoneySummaryUI), nameof(MoneySummaryUI.DoShow))]
        public static class Record
        {
            // The game has just filled m_metricToday; the day's income is still in SuperGraph.
            // Also recorded while History is off, so the days are there when it's switched on.
            public static void Postfix()
            {
                ParkFile park = GameCore.Get != null ? GameCore.Get.m_park : null;
                if (park == null || park.m_metricToday.m_data == null || SuperGraph.Get == null)
                    return;
                DropOldMoney(park);

                var data = new List<int>(park.m_metricToday.m_data);
                while (data.Count < MoneyStart)
                    data.Add(0);
                data.RemoveRange(MoneyStart, data.Count - MoneyStart);
                data[Marker] = MarkerValue;
                data[Appeal] = LiveAppeal(park);
                data[Stars] = LiveStars(park);
                data[ReviewsToday] = park.m_allReviews.Count;
                data[ReviewsTotal] = park.m_allReviewsOnly.Count;

                // Per category and kind, summed over the day's hours
                var money = new Dictionary<(string, bool), int>();
                foreach (KeyValuePair<ItemData, DayData> item in SuperGraph.Get.m_currentDay)
                {
                    bool revenue = item.Key.m_type == SuperGraphType.Revenue;
                    if ((!revenue && item.Key.m_type != SuperGraphType.Expenses) || item.Key.m_rootName == null)
                        continue;
                    money.TryGetValue((item.Key.m_rootName, revenue), out int sum);
                    money[(item.Key.m_rootName, revenue)] = sum + (item.Value.m_dayData?.Sum() ?? 0);
                }
                data.Add(money.Count);
                foreach (KeyValuePair<(string Name, bool Revenue), int> row in money)
                {
                    data.Add(row.Key.Revenue ? 1 : 0);
                    data.Add(row.Value);
                    data.Add(row.Key.Name.Length);
                    data.AddRange(row.Key.Name.Select(c => (int)c));
                }
                park.m_metricToday.m_data = data.ToArray();
            }
        }

        // The income list is only needed for the days Park Info can show; older days keep the rest
        private static void DropOldMoney(ParkFile park)
        {
            if (park.m_metricAll == null)
                return;
            for (int i = 0; i < park.m_metricAll.Count; i++)
            {
                Metric m = park.m_metricAll[i];
                if (m.m_data != null && m.m_data.Length > MoneyStart && park.m_totalDays - m.m_day > ParkHistoryPatch.MaxDaysBack)
                {
                    System.Array.Resize(ref m.m_data, MoneyStart);
                    park.m_metricAll[i] = m; // Metric is a struct
                }
            }
        }

        internal static int LiveAppeal(ParkFile park) => Mathf.RoundToInt(park.m_currentParkAppeal);

        // As the game shows it (ReviewListUI, end-of-day summary)
        internal static int LiveStars(ParkFile park) =>
            GameCore.GetStarRating(park.m_currentParkScore + (park.m_allReviewsOnly.Count > 150 ? 0.5f : 0f));

        // A recorded value of the day 'offset' days ago (offset 0 = now, live)
        internal static bool TryGetAppeal(int offset, out int value) => TryGet(offset, Appeal, LiveAppeal, out value);
        internal static bool TryGetStars(int offset, out int value) => TryGet(offset, Stars, LiveStars, out value);
        internal static bool TryGetReviews(int offset, out int value) =>
            TryGet(offset, ReviewsToday, park => park.m_allReviews.Count, out value);

        private static bool TryGet(int offset, int slot, System.Func<ParkFile, int> live, out int value)
        {
            value = 0;
            ParkFile park = GameCore.Get != null ? GameCore.Get.m_park : null;
            if (park == null)
                return false;
            if (offset == 0)
            {
                value = live(park);
                return true;
            }
            int[] data = ParkHistoryPatch.DayData(offset);
            if (data == null || data.Length < MoneyStart || data[Marker] != MarkerValue)
                return false;
            value = data[slot];
            return true;
        }

        // The income list of the day 'offset' days ago; null if it isn't known
        internal static MoneyDay Money(int offset)
        {
            GameCore core = GameCore.Get;
            ParkFile park = core != null ? core.m_park : null;
            if (park == null || SuperGraph.Get == null)
                return null;
            if (offset == 0)
                return FromGraph(SuperGraph.Get.m_currentDay, d => d.m_dayData?.Sum() ?? 0);

            int[] data = ParkHistoryPatch.DayData(offset);
            if (data != null && data.Length > MoneyStart && data[Marker] == MarkerValue)
                return Decode(data);

            // Still in this week's data? The week is cleared at the start of a day with
            // m_totalDays % 7 == 0, and has one slot per weekday (ParkFile.m_day)
            if (offset > park.m_totalDays % 7)
                return null;
            int slot = ((park.m_day - offset) % 7 + 7) % 7;
            return FromGraph(SuperGraph.Get.m_currentWeek,
                d => d.m_dayData != null && slot < d.m_dayData.Length ? d.m_dayData[slot] : 0);
        }

        private static MoneyDay FromGraph(Dictionary<ItemData, DayData> graph, System.Func<DayData, int> amount)
        {
            var day = new MoneyDay();
            foreach (KeyValuePair<ItemData, DayData> item in graph)
            {
                bool revenue = item.Key.m_type == SuperGraphType.Revenue;
                if ((!revenue && item.Key.m_type != SuperGraphType.Expenses) || item.Key.m_rootName == null)
                    continue;
                day.Add(item.Key.m_rootName, revenue, amount(item.Value));
            }
            return day;
        }

        private static MoneyDay Decode(int[] data)
        {
            var day = new MoneyDay();
            int i = MoneyStart;
            int count = data[i++];
            for (int n = 0; n < count && i + 3 <= data.Length; n++)
            {
                bool revenue = data[i++] == 1;
                int amount = data[i++];
                int length = data[i++];
                if (length < 0 || i + length > data.Length)
                    break;
                var name = new string(data.Skip(i).Take(length).Select(c => (char)c).ToArray());
                i += length;
                day.Add(name, revenue, amount);
            }
            return day;
        }
    }
}
