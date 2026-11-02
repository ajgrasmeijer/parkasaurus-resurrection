using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Park Info: the income list in the middle of the overview (MoneySummaryUI), its donations row
    // and total. The game only fills it at the end-of-day tally, so during the day it shows the
    // previous tally.
    // - LiveIncome: during the day it shows today's income and expenses so far
    //   (SuperGraph.m_currentDay), the same way the tally does (MoneySummaryUI.DoShow / AddRow), but
    //   without the tally's side effects (money transfer, achievements).
    // - History (ParkHistoryPatch): it shows the chosen day (ParkHistoryData), and with a compared
    //   day "(+N)" / "(-N)" behind each row and the total.
    // Updated every second while the computer is open. At the tally (day phase 3 and later) the
    // game's own list is left alone.
    public static class LiveIncomePatch
    {
        private const string RowName = "ModLiveIncome";
        private static float s_nextUpdate;

        // While our rows are shown, the game's own rows are hidden and its donations and total kept
        private static bool s_showing;
        private static int s_gameDonations;
        private static string s_gameTotal;

        [HarmonyPatch(typeof(ComputerUI), nameof(ComputerUI.OnEnable))]
        public static class OnOpen
        {
            public static void Postfix()
            {
                ShowNow();
            }
        }

        [HarmonyPatch(typeof(ComputerUI), "Update")]
        public static class WhileOpen
        {
            public static void Postfix() => Update();
        }

        // The tally rebuilds the whole list (and sets the donations and total): nothing of ours to undo
        [HarmonyPatch(typeof(MoneySummaryUI), nameof(MoneySummaryUI.DoShow))]
        public static class Tally
        {
            public static void Postfix() => s_showing = false;
        }

        // Switched in the options menu, or another day chosen: update right away
        internal static void ShowNow()
        {
            s_nextUpdate = 0f;
            Update();
        }

        internal static void ApplyEnabled() => ShowNow();

        private static void Update()
        {
            if (Time.unscaledTime < s_nextUpdate)
                return;
            s_nextUpdate = Time.unscaledTime + 1f;

            GameCore core = GameCore.Get;
            MoneySummaryUI summary = core != null ? core.m_moneySummery : null;
            if (summary == null || summary.m_root == null || core.m_park == null)
                return;

            bool history = ParkHistoryPatch.Active;
            bool live = !history && ModConfig.ParkInfoLiveIncome.Value;
            if (core.m_park.m_dayPhase >= 3 || !(history || live))
            {
                Restore(summary);
                return;
            }
            if (!summary.gameObject.activeInHierarchy)
                return;

            MoneyDay shown = ParkHistoryData.Money(history ? ParkHistoryPatch.View : 0);
            MoneyDay other = history && ParkHistoryPatch.Compare >= 0 ? ParkHistoryData.Money(ParkHistoryPatch.Compare) : null;
            Show(summary, shown ?? new MoneyDay(), shown != null, other);
        }

        private static void Show(MoneySummaryUI summary, MoneyDay day, bool known, MoneyDay other)
        {
            Transform root = summary.m_root.transform;
            if (!s_showing)
            {
                s_showing = true;
                s_gameDonations = summary.m_donation != null ? summary.m_donation.m_money : 0;
                s_gameTotal = summary.m_total != null ? summary.m_total.text : null;
            }

            // Every category of both days, biggest first
            var names = new HashSet<string>(day.Rows.Keys);
            if (other != null)
                names.UnionWith(other.Rows.Keys);

            // Reuse rows by category so the counters don't restart every second; the game's own rows
            // are hidden (the tally replaces them all anyway)
            var rows = new Dictionary<string, ProfitUI>();
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                ProfitUI row = child.GetComponent<ProfitUI>();
                string key = child.name.StartsWith(RowName + ":") ? child.name.Substring(RowName.Length + 1) : null;
                if (key == null)
                    child.gameObject.SetActive(false);
                else if (row == null || !names.Contains(key))
                    Object.Destroy(child.gameObject);
                else
                    rows[key] = row;
            }

            foreach (string name in names.OrderByDescending(day.Get))
            {
                if (!rows.TryGetValue(name, out ProfitUI row))
                {
                    row = Object.Instantiate(summary.m_profitUI, root).GetComponent<ProfitUI>();
                    row.gameObject.name = RowName + ":" + name;
                    row.m_name.supportRichText = true;
                    if (day.Expenses.Contains(name) || (other != null && other.Expenses.Contains(name)))
                        row.m_name.color = Global.Get.m_badColor;
                    rows[name] = row;
                }
                row.transform.SetAsLastSibling();
                // The number is drawn by a counter; the difference goes behind the name
                row.m_name.text = GameCore.Translate(name) +
                                  (other != null ? ParkHistoryPatch.Delta(day.Get(name), other.Get(name), ParkHistoryPatch.SmallSize(row.m_name)) : "");
                SetMoney(row, day.Get(name));
            }

            if (summary.m_donation != null)
            {
                SetMoney(summary.m_donation, day.Donations);
                summary.m_donation.m_name.supportRichText = true;
                summary.m_donation.m_name.text = GameCore.Translate(MoneyDay.DonationsName) +
                    (other != null ? ParkHistoryPatch.Delta(day.Donations, other.Donations, ParkHistoryPatch.SmallSize(summary.m_donation.m_name)) : "");
            }
            if (summary.m_total != null)
            {
                summary.m_total.supportRichText = true;
                summary.m_total.text = !known ? "–" : day.Total +
                    (other != null ? ParkHistoryPatch.Delta(day.Total, other.Total, ParkHistoryPatch.HalfSize(summary.m_total)) : "");
            }
        }

        private static void SetMoney(ProfitUI row, int money)
        {
            if (row.m_money == money)
                return;
            row.m_money = money;
            row.StartMoney();
        }

        // Back to the game's own list
        private static void Restore(MoneySummaryUI summary)
        {
            if (!s_showing)
                return;
            s_showing = false;
            Transform root = summary.m_root.transform;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform child = root.GetChild(i);
                if (child.name.StartsWith(RowName + ":"))
                    Object.Destroy(child.gameObject);
                else
                    child.gameObject.SetActive(true);
            }
            if (summary.m_donation != null)
            {
                SetMoney(summary.m_donation, s_gameDonations);
                summary.m_donation.m_name.text = GameCore.Translate(MoneyDay.DonationsName);
            }
            if (summary.m_total != null && s_gameTotal != null)
                summary.m_total.text = s_gameTotal;
        }

        // Called when a park is closed: its list is gone
        internal static void Reset() => s_showing = false;
    }
}
