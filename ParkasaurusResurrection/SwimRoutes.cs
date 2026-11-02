using System.Collections.Generic;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Plans a swim route through the water of an exhibit, so swimmers go around corners and
    // headlands instead of stopping at the first shallow tile (the game swims in a straight line,
    // NavWater.SetDestination, and cuts the route off where the water gets shallow).
    // - Tiles: 1x1 m, water at least MinDepth deep, in the same exhibit as the start.
    // - Clearance: distance to the nearest shore per tile; the route prefers tiles with at least the
    //   turning radius of room, so it keeps to the middle of the water.
    // - Free swimming: an end point in a tight corner moves to the nearest roomy spot nearby, so the
    //   dino doesn't end up facing a wall. Other trips (food, drinking, leaving the water) keep the
    //   game's end point.
    // - The tile route is smoothed into a few corner points (straight lines that keep off the shore);
    //   the turning circle of SwimSteeringPatch turns those into flowing curves.
    internal static class SwimRoutes
    {
        internal const float MinDepth = 0.8f; // the game's own limit (NavWater.SetDestination)
        private const int Margin = 12;
        private const int MaxWindow = 160;
        private const int SnapRadius = 6;

        private static int s_x0, s_z0, s_w, s_h;
        private static bool[] s_water;
        private static int[] s_clear;
        private static float[] s_cost;
        private static int[] s_from;
        private static bool[] s_closed;

        // Returns the route as world points: the start, any corner points, and the end point
        internal static bool TryPlan(Vector3 start, Vector3 end, float turnRadius, bool freeSwim, out List<Vector3> points)
        {
            points = null;
            GameCore core = GameCore.Get;
            int size = core.m_gridSize;
            int sx = Mathf.RoundToInt(start.x), sz = Mathf.RoundToInt(start.z);
            int ex = Mathf.RoundToInt(end.x), ez = Mathf.RoundToInt(end.z);
            if (sx < 0 || sz < 0 || sx >= size || sz >= size)
                return false;

            // Window around start and end
            s_x0 = Mathf.Clamp(Mathf.Min(sx, ex) - Margin, 0, size - 1);
            s_z0 = Mathf.Clamp(Mathf.Min(sz, ez) - Margin, 0, size - 1);
            int x1 = Mathf.Clamp(Mathf.Max(sx, ex) + Margin, 0, size - 1);
            int z1 = Mathf.Clamp(Mathf.Max(sz, ez) + Margin, 0, size - 1);
            s_w = x1 - s_x0 + 1;
            s_h = z1 - s_z0 + 1;
            if (s_w > MaxWindow || s_h > MaxWindow)
                return false;
            int n = s_w * s_h;
            Ensure(n);

            Room room = core.GetMyRoom(start);
            for (int z = 0; z < s_h; z++)
                for (int x = 0; x < s_w; x++)
                {
                    int wx = s_x0 + x, wz = s_z0 + z;
                    bool ok = core.GetWaterFast(wx, wz) >= MinDepth
                        && (room == null || core.GetMyRoom(new Vector3(wx, 0f, wz)) == room);
                    s_water[x + z * s_w] = ok;
                }
            int startI = Index(sx, sz);
            s_water[startI] = true; // the dino may be in shallow water right at the edge
            ComputeClearance();

            // End point: the nearest water tile to where the game wants to go
            int goal = NearestWater(ex, ez);
            if (goal < 0)
                return false;
            int desired = Mathf.Clamp(Mathf.RoundToInt(turnRadius), 2, 6);
            if (freeSwim)
                goal = Roomier(goal, desired);

            if (!AStar(startI, goal, desired))
                return false;

            // Tile route, start to goal
            var tiles = new List<int>();
            for (int i = goal; i != startI; i = s_from[i])
                tiles.Add(i);
            tiles.Add(startI);
            tiles.Reverse();

            points = Smooth(tiles, start);
            Vector3 last = points[points.Count - 1];
            // Keep the exact end point when it's in the water and the game asked for it precisely
            if (!freeSwim && goal == Index(ex, ez) && Visible(Tile(goal), new Vector3(end.x, 0f, end.z), 1))
                last = new Vector3(end.x, start.y, end.z);
            points[points.Count - 1] = last;
            return true;
        }

        private static void Ensure(int n)
        {
            if (s_water != null && s_water.Length >= n)
                return;
            s_water = new bool[n];
            s_clear = new int[n];
            s_cost = new float[n];
            s_from = new int[n];
            s_closed = new bool[n];
        }

        private static int Index(int wx, int wz)
        {
            int x = wx - s_x0, z = wz - s_z0;
            if (x < 0 || z < 0 || x >= s_w || z >= s_h)
                return -1;
            return x + z * s_w;
        }

        private static Vector3 Tile(int i) => new Vector3(s_x0 + i % s_w, 0f, s_z0 + i / s_w);

        private static bool Water(int x, int z) => x >= 0 && z >= 0 && x < s_w && z < s_h && s_water[x + z * s_w];

        // Steps to the nearest non-water tile (8 directions). The window edge doesn't count as shore.
        private static void ComputeClearance()
        {
            var queue = new Queue<int>();
            for (int i = 0; i < s_w * s_h; i++)
            {
                if (!s_water[i])
                {
                    s_clear[i] = 0;
                    queue.Enqueue(i);
                }
                else
                {
                    s_clear[i] = int.MaxValue;
                }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % s_w, z = i / s_w;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if ((dx == 0 && dz == 0) || nx < 0 || nz < 0 || nx >= s_w || nz >= s_h)
                            continue;
                        int j = nx + nz * s_w;
                        if (s_clear[j] > s_clear[i] + 1)
                        {
                            s_clear[j] = s_clear[i] + 1;
                            queue.Enqueue(j);
                        }
                    }
            }
            // Water with no shore in the window at all
            for (int i = 0; i < s_w * s_h; i++)
                if (s_clear[i] == int.MaxValue)
                    s_clear[i] = 99;
        }

        private static int NearestWater(int ex, int ez)
        {
            int best = -1;
            float bestDist = float.MaxValue;
            for (int r = 0; r <= Margin && best < 0; r++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r)
                            continue;
                        int i = Index(ex + dx, ez + dz);
                        if (i < 0 || !s_water[i])
                            continue;
                        float d = dx * dx + dz * dz;
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = i;
                        }
                    }
            return best;
        }

        // A nearby tile with room to turn, as close as possible to the original end point
        private static int Roomier(int goal, int desired)
        {
            if (s_clear[goal] >= desired)
                return goal;
            int gx = goal % s_w, gz = goal / s_w;
            int best = goal;
            float bestScore = (desired - s_clear[goal]) * 3f;
            for (int dz = -SnapRadius; dz <= SnapRadius; dz++)
                for (int dx = -SnapRadius; dx <= SnapRadius; dx++)
                {
                    int x = gx + dx, z = gz + dz;
                    if (!Water(x, z))
                        continue;
                    int i = x + z * s_w;
                    float score = Mathf.Sqrt(dx * dx + dz * dz) + Mathf.Max(0, desired - s_clear[i]) * 3f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
            return best;
        }

        // 8-connected A*, no cutting past shore corners; tiles close to the shore cost extra
        private static bool AStar(int start, int goal, int desired)
        {
            int n = s_w * s_h;
            for (int i = 0; i < n; i++)
            {
                s_cost[i] = float.MaxValue;
                s_closed[i] = false;
                s_from[i] = -1;
            }
            var open = new Heap();
            s_cost[start] = 0f;
            open.Push(start, Heuristic(start, goal));
            int gx = goal % s_w, gz = goal / s_w;
            while (open.Count > 0)
            {
                int i = open.Pop();
                if (i == goal)
                    return true;
                if (s_closed[i])
                    continue;
                s_closed[i] = true;
                int x = i % s_w, z = i / s_w;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;
                        int nx = x + dx, nz = z + dz;
                        if (!Water(nx, nz))
                            continue;
                        if (dx != 0 && dz != 0 && (!Water(x + dx, z) || !Water(x, z + dz)))
                            continue;
                        int j = nx + nz * s_w;
                        if (s_closed[j])
                            continue;
                        float step = (dx != 0 && dz != 0) ? 1.4142f : 1f;
                        float tight = Mathf.Max(0, desired - s_clear[j]) / (float)desired;
                        float cost = s_cost[i] + step * (1f + 3f * tight * tight);
                        if (cost < s_cost[j])
                        {
                            s_cost[j] = cost;
                            s_from[j] = i;
                            float h = Mathf.Sqrt((nx - gx) * (nx - gx) + (nz - gz) * (nz - gz));
                            open.Push(j, cost + h);
                        }
                    }
            }
            return false;
        }

        private static float Heuristic(int a, int b)
        {
            int ax = a % s_w, az = a / s_w, bx = b % s_w, bz = b / s_w;
            return Mathf.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
        }

        // String pulling: from each corner point, skip ahead as far as a straight line stays on
        // tiles with enough clearance (2 if possible, else 1)
        private static List<Vector3> Smooth(List<int> tiles, Vector3 start)
        {
            var points = new List<Vector3> { new Vector3(start.x, start.y, start.z) };
            Vector3 from = new Vector3(start.x, 0f, start.z);
            int k = 0;
            while (k < tiles.Count - 1)
            {
                int next = k + 1;
                for (int j = tiles.Count - 1; j > k + 1; j--)
                {
                    if (Visible(from, Tile(tiles[j]), 2) || Visible(from, Tile(tiles[j]), 1))
                    {
                        next = j;
                        break;
                    }
                }
                Vector3 p = Tile(tiles[next]);
                points.Add(new Vector3(p.x, start.y, p.z));
                from = p;
                k = next;
            }
            if (points.Count == 1)
                points.Add(points[0]);
            return points;
        }

        private static bool Visible(Vector3 a, Vector3 b, int minClear)
        {
            Vector3 d = b - a;
            d.y = 0f;
            float len = d.magnitude;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.25f));
            for (int s = 1; s < steps; s++)
            {
                Vector3 p = a + d * (s / (float)steps);
                int i = Index(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.z));
                if (i < 0 || !s_water[i] || s_clear[i] < minClear)
                    return false;
            }
            return true;
        }

        // Small binary min-heap of (index, priority)
        private class Heap
        {
            private readonly List<int> _items = new List<int>();
            private readonly List<float> _prio = new List<float>();
            public int Count => _items.Count;

            public void Push(int item, float prio)
            {
                _items.Add(item);
                _prio.Add(prio);
                int c = _items.Count - 1;
                while (c > 0)
                {
                    int p = (c - 1) / 2;
                    if (_prio[p] <= _prio[c])
                        break;
                    Swap(c, p);
                    c = p;
                }
            }

            public int Pop()
            {
                int top = _items[0];
                int last = _items.Count - 1;
                Swap(0, last);
                _items.RemoveAt(last);
                _prio.RemoveAt(last);
                int c = 0;
                while (true)
                {
                    int l = c * 2 + 1, r = l + 1, m = c;
                    if (l < _items.Count && _prio[l] < _prio[m]) m = l;
                    if (r < _items.Count && _prio[r] < _prio[m]) m = r;
                    if (m == c)
                        break;
                    Swap(c, m);
                    c = m;
                }
                return top;
            }

            private void Swap(int a, int b)
            {
                (_items[a], _items[b]) = (_items[b], _items[a]);
                (_prio[a], _prio[b]) = (_prio[b], _prio[a]);
            }
        }
    }
}
