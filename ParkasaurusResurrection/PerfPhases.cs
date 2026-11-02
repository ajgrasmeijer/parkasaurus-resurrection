using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ParkasaurusResurrection
{
    // Part of PerfProbe: time spent in each step of Unity's frame (the PlayerLoop), including the
    // parts no script patch can see: physics, animation, rendering, waiting for vSync.
    // A marker is put in front of every step and at the end of every phase; each marker gives the
    // time since the previous one to the step before it. Frames over 50 ms get their top 3 steps logged.
    internal static class PerfPhases
    {
        private const double HitchMs = 50.0;
        private const int MaxHitchLines = 30;

        private static string[] s_labels;
        private static long[] s_total;      // ticks per step since the last report
        private static long[] s_worst;      // worst single frame per step
        private static long[] s_frame;      // ticks per step in the current frame
        private static int s_last = -1;     // step that is running now
        private static long s_lastStamp;
        private static long s_frameStart;
        private static readonly List<string> Hitches = new List<string>();

        internal static int Installs { get; private set; }

        // Other parts of the game (Input System, jobs) replace the PlayerLoop after startup, which
        // drops our markers; PerfProbe calls this every few seconds and it puts them back if needed
        internal static void EnsureInstalled()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            if (root.subSystemList != null && root.subSystemList.Any(p =>
                    p.subSystemList != null && p.subSystemList.Any(x => x.type == typeof(PerfPhases))))
                return;
            Install(root);
        }

        private static void Install(PlayerLoopSystem root)
        {
            var labels = new List<string>();
            PlayerLoopSystem[] phases = root.subSystemList;
            for (int p = 0; p < phases.Length; p++)
            {
                PlayerLoopSystem phase = phases[p];
                string phaseName = phase.type?.Name ?? "?";
                var list = new List<PlayerLoopSystem>();
                if (phase.subSystemList != null)
                    foreach (PlayerLoopSystem step in phase.subSystemList)
                    {
                        if (step.type == typeof(PerfPhases))
                            continue;
                        list.Add(Marker(labels.Count, first: p == 0 && list.Count == 0));
                        labels.Add($"{phaseName}.{step.type?.Name ?? "?"}");
                        list.Add(step);
                    }
                // End of the phase: whatever runs after its last step until the next marker
                list.Add(Marker(labels.Count, first: false));
                labels.Add($"{phaseName} (between phases)");
                phase.subSystemList = list.ToArray();
                phases[p] = phase;
            }
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);

            Installs++;
            s_last = -1;
            s_frameStart = 0;
            s_labels = labels.ToArray();
            s_total = new long[s_labels.Length];
            s_worst = new long[s_labels.Length];
            s_frame = new long[s_labels.Length];
        }

        private static PlayerLoopSystem Marker(int index, bool first)
        {
            return new PlayerLoopSystem
            {
                type = typeof(PerfPhases),
                updateDelegate = () => Mark(index, first),
            };
        }

        private static void Mark(int index, bool first)
        {
            long now = Stopwatch.GetTimestamp();
            if (s_last >= 0)
                s_frame[s_last] += now - s_lastStamp;
            if (first)
                EndFrame(now);
            s_last = index;
            s_lastStamp = now;
        }

        // At the first marker of a frame: book the frame that just ended
        private static void EndFrame(long now)
        {
            if (s_frameStart != 0)
            {
                double frameMs = (now - s_frameStart) * 1000.0 / Stopwatch.Frequency;
                if (frameMs > HitchMs && Hitches.Count < MaxHitchLines)
                {
                    var top = Enumerable.Range(0, s_frame.Length).OrderByDescending(i => s_frame[i]).Take(3)
                        .Select(i => $"{s_labels[i]} {s_frame[i] * 1000.0 / Stopwatch.Frequency:F1}");
                    Hitches.Add($"  {Time.realtimeSinceStartup,9:F1} s  frame {frameMs,6:F1} ms: {string.Join(", ", top)}");
                }
            }
            for (int i = 0; i < s_frame.Length; i++)
            {
                s_total[i] += s_frame[i];
                if (s_frame[i] > s_worst[i])
                    s_worst[i] = s_frame[i];
                s_frame[i] = 0;
            }
            s_frameStart = now;
        }

        internal static void AppendReport(StringBuilder sb, float seconds)
        {
            if (s_labels == null)
                return;
            double freq = Stopwatch.Frequency;
            sb.AppendLine($"Unity frame steps (ms per second | worst frame ms), steps over 0.5 ms per second " +
                          $"(markers placed {Installs}x; after a re-placement the numbers cover part of the period):");
            foreach (int i in Enumerable.Range(0, s_labels.Length).OrderByDescending(i => s_total[i]))
            {
                double perSecond = s_total[i] * 1000.0 / freq / seconds;
                if (perSecond < 0.5)
                    break;
                sb.AppendLine($"  {perSecond,8:F1} | {s_worst[i] * 1000.0 / freq,7:F1}  {s_labels[i]}");
            }
            if (Hitches.Count > 0)
            {
                sb.AppendLine($"Frames over {HitchMs} ms, with their 3 biggest steps (ms), first {MaxHitchLines}:");
                foreach (string line in Hitches)
                    sb.AppendLine(line);
            }
            Hitches.Clear();
            for (int i = 0; i < s_total.Length; i++)
                s_total[i] = s_worst[i] = 0;
        }
    }
}
