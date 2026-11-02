using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace ParkasaurusResurrection
{
    // Measuring tool for finding what makes the game slow ([Debug] PerfProbe, off by default, restart).
    // Writes BepInEx/perf/perf-<date>.log:
    // - every PerfReportSeconds: frame times (average, percentiles, hitches), the game's scripts sorted
    //   by time (all Update/LateUpdate/FixedUpdate methods, all coroutines, and every method of the
    //   heavy systems: paths, NavMesh, terrain, water, grass, minimap), slow single calls, what's in
    //   the scene, and memory;
    // - once per park, 30 s after loading: textures, meshes, shaders and the graphics settings.
    // Times are inclusive: a method's time includes the patched methods it calls. Unity's own work
    // (rendering, physics, animation) isn't patched; it's roughly frame time minus script time.
    public static class PerfProbe
    {
        private const double SlowCallMs = 15.0;
        private const int MaxSlowCallsPerReport = 40;

        private class Stat
        {
            public string Name;
            public long MainTicks, OtherTicks, MainCalls, OtherCalls, MaxTicks;
        }

        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
        private static int s_mainThread;
        private static readonly List<float> Frames = new List<float>(8192);
        private static readonly List<double> GpuFrames = new List<double>(8192);
        private static readonly List<double> CpuFrames = new List<double>(8192);
        private static readonly FrameTiming[] Timing = new FrameTiming[1];
        private static readonly List<string> SlowCalls = new List<string>();
        private static readonly object SlowLock = new object();
        private static float s_reportTimer;
        private static float s_reportStart;
        private static int s_gcStart;
        private static string s_file;
        private static bool s_active;
        private static GameCore s_snapshotFor;
        private static float s_parkSeen;
        private static int s_skipFrames;
        private static float s_phaseCheck;

        internal static void Start(Harmony harmony)
        {
            s_mainThread = Thread.CurrentThread.ManagedThreadId;
            string dir = Path.Combine(Paths.BepInExRootPath, "perf");
            Directory.CreateDirectory(dir);
            s_file = Path.Combine(dir, $"perf-{DateTime.Now:yyyy-MM-dd_HHmmss}.log");

            var sw = Stopwatch.StartNew();
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(PerfProbe), nameof(Prefix)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(PerfProbe), nameof(Postfix)));
            int ok = 0, failed = 0;
            foreach (MethodBase m in Targets())
            {
                Stats[m] = new Stat { Name = NameOf(m) };
                try
                {
                    harmony.Patch(m, prefix, postfix);
                    ok++;
                }
                catch (Exception)
                {
                    Stats.Remove(m);
                    failed++;
                }
            }
            s_active = true;
            s_reportStart = Time.realtimeSinceStartup;
            s_gcStart = GC.CollectionCount(0);
            Write($"Parkasaurus Resurrection performance log, {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                  $"Patched {ok} methods ({failed} could not be patched) in {sw.ElapsedMilliseconds} ms\n");
            Plugin.Log.LogInfo($"PerfProbe: measuring {ok} methods, writing to {s_file}");
        }

        // Update/LateUpdate/FixedUpdate of every game script, every coroutine, and all methods of the
        // systems that are known or suspected to be heavy
        private static IEnumerable<MethodBase> Targets()
        {
            Assembly game = typeof(GameCore).Assembly;
            var heavy = new HashSet<string>
            {
                "SuperPath", "SuperTerrain", "SuperWater", "SuperGrass", "SuperFence", "LocalNavMeshBuilder",
                "MiniMapUI", "NavWalk", "Sector", "Region", "LODSwitcher", "SuperSmoke", "NavMeshSurface",
            };
            // Big classes: only their larger methods (tiny getters run thousands of times per
            // second and measuring them would slow the game down more than they cost)
            var bigOnly = new HashSet<string> { "GameCore", "UICore" };
            var seen = new HashSet<MethodBase>();
            Type[] types;
            try { types = game.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (Type t in types)
            {
                if (t.ContainsGenericParameters)
                    continue;
                bool behaviour = typeof(MonoBehaviour).IsAssignableFrom(t);
                bool coroutine = t.IsNested && t.Name.StartsWith("<") && typeof(IEnumerator).IsAssignableFrom(t);
                bool isHeavy = heavy.Contains(t.Name);
                bool isBig = bigOnly.Contains(t.Name);
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                      BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null)
                        continue;
                    bool want = (behaviour && m.GetParameters().Length == 0 &&
                                 (m.Name == "Update" || m.Name == "LateUpdate" || m.Name == "FixedUpdate"))
                                || (coroutine && m.Name == "MoveNext")
                                || (isHeavy && !m.IsSpecialName && m.Name != ".ctor")
                                || (isBig && !m.IsSpecialName && m.Name != ".ctor" && ILSize(m) >= 120);
                    if (want && seen.Add(m))
                        yield return m;
                }
            }
        }

        private static int ILSize(MethodBase m)
        {
            try { return m.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0; }
            catch (Exception) { return 0; }
        }

        private static string NameOf(MethodBase m)
        {
            Type t = m.DeclaringType;
            if (m.Name == "MoveNext" && t != null && t.IsNested && t.Name.StartsWith("<"))
            {
                int end = t.Name.IndexOf('>');
                string routine = end > 1 ? t.Name.Substring(1, end - 1) : t.Name;
                return $"{t.DeclaringType?.Name}.{routine} (coroutine)";
            }
            return $"{t?.Name}.{m.Name}";
        }

        public static void Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void Postfix(long __state, MethodBase __originalMethod)
        {
            long ticks = Stopwatch.GetTimestamp() - __state;
            if (!Stats.TryGetValue(__originalMethod, out Stat s))
                return;
            bool main = Thread.CurrentThread.ManagedThreadId == s_mainThread;
            if (main)
            {
                s.MainTicks += ticks;
                s.MainCalls++;
            }
            else
            {
                Interlocked.Add(ref s.OtherTicks, ticks);
                Interlocked.Increment(ref s.OtherCalls);
            }
            if (ticks > s.MaxTicks)
                s.MaxTicks = ticks;
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            if (ms >= SlowCallMs)
                lock (SlowLock)
                    if (SlowCalls.Count < MaxSlowCallsPerReport)
                        SlowCalls.Add($"  {Time.realtimeSinceStartup,9:F1} s  {ms,7:F1} ms  {s.Name}{(main ? "" : "  (background thread)")}");
        }

        // Every frame, from GameCore.Update (HotkeyPatch)
        internal static void Frame()
        {
            if (!s_active)
                return;
            if (s_skipFrames > 0)
                s_skipFrames--;
            else
                Frames.Add(Time.unscaledDeltaTime * 1000f);
            // Unity's own CPU/GPU frame times, only filled in when the game enabled frame timing stats
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, Timing) > 0)
            {
                if (Timing[0].gpuFrameTime > 0) GpuFrames.Add(Timing[0].gpuFrameTime);
                if (Timing[0].cpuFrameTime > 0) CpuFrames.Add(Timing[0].cpuFrameTime);
            }

            if (GameCore.Get != null && GameCore.Get != s_snapshotFor)
            {
                if (s_parkSeen <= 0f)
                    s_parkSeen = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - s_parkSeen > 30f)
                {
                    s_snapshotFor = GameCore.Get;
                    s_parkSeen = 0f;
                    SafeWrite(AssetReport);
                    s_skipFrames = 3; // the report itself causes a hitch
                }
            }

            s_phaseCheck -= Time.unscaledDeltaTime;
            if (s_phaseCheck <= 0f)
            {
                s_phaseCheck = 3f;
                try { PerfPhases.EnsureInstalled(); }
                catch (Exception e)
                {
                    s_phaseCheck = float.MaxValue;
                    Plugin.Log.LogWarning($"PerfProbe: Unity frame steps not measured: {e.Message}");
                }
            }

            s_reportTimer += Time.unscaledDeltaTime;
            if (s_reportTimer >= Mathf.Max(10f, ModConfig.PerfReportSeconds.Value))
            {
                s_reportTimer = 0f;
                SafeWrite(Report);
                s_skipFrames = 3;
            }
        }

        private static void SafeWrite(Func<string> make)
        {
            try
            {
                Write(make());
            }
            catch (Exception e)
            {
                Write($"(report failed: {e.GetType().Name}: {e.Message})\n");
            }
        }

        private static void Write(string text)
        {
            File.AppendAllText(s_file, text + "\n");
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static string Report()
        {
            float seconds = Mathf.Max(0.001f, Time.realtimeSinceStartup - s_reportStart);
            var sb = new StringBuilder();
            sb.AppendLine($"===== {DateTime.Now:HH:mm:ss}  report over {seconds:F0} s  " +
                          $"({(GameCore.Get != null ? "in a park" : "menu")}, game speed {Time.timeScale:0.##}x)");

            // Frames
            if (Frames.Count > 0)
            {
                var sorted = Frames.OrderBy(f => f).ToList();
                float P(float q) => sorted[Mathf.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
                double avg = Frames.Average();
                sb.AppendLine($"Frames: {Frames.Count}, {1000.0 / avg:F1} fps average, frame time ms: average {avg:F1}, " +
                              $"median {P(0.5f):F1}, 95% {P(0.95f):F1}, 99% {P(0.99f):F1}, worst {sorted[sorted.Count - 1]:F1}");
                sb.AppendLine($"Hitches: {Frames.Count(f => f > 33.4f)} frames over 33 ms, {Frames.Count(f => f > 100f)} over 100 ms, " +
                              $"{Frames.Count(f => f > 250f)} over 250 ms");
            }

            if (GpuFrames.Count > 0)
                sb.AppendLine($"Unity frame timing: GPU {GpuFrames.Average():F1} ms average ({Pct(GpuFrames, 0.95):F1} at 95%), " +
                              $"CPU {(CpuFrames.Count > 0 ? CpuFrames.Average() : 0):F1} ms average ({Pct(CpuFrames, 0.95):F1} at 95%)");
            else
                sb.AppendLine("Unity frame timing: not available (frame timing stats are off in this build)");
            GpuFrames.Clear();
            CpuFrames.Clear();
            PerfPhases.AppendReport(sb, seconds);

            // Scripts
            long mainTotal = 0;
            foreach (Stat s in Stats.Values)
                mainTotal += s.MainTicks;
            double frameMsTotal = Frames.Sum(f => (double)f);
            sb.AppendLine($"Game scripts on the main thread: {Ms(mainTotal) / seconds:F1} ms per second " +
                          $"(frames took {frameMsTotal / seconds:F0} ms per second; the rest is Unity: rendering, physics, animation, waiting)");
            sb.AppendLine("Top main thread (ms per second | calls per second | average ms | slowest ms):");
            foreach (Stat s in Stats.Values.Where(s => s.MainCalls > 0).OrderByDescending(s => s.MainTicks).Take(35))
                sb.AppendLine($"  {Ms(s.MainTicks) / seconds,8:F2} | {s.MainCalls / seconds,9:F1} | {Ms(s.MainTicks) / s.MainCalls,8:F3} | {Ms(s.MaxTicks),8:F1}  {s.Name}");
            var background = Stats.Values.Where(s => s.OtherCalls > 0).OrderByDescending(s => s.OtherTicks).Take(10).ToList();
            if (background.Count > 0)
            {
                sb.AppendLine("Top background threads (ms per second | calls per second | average ms):");
                foreach (Stat s in background)
                    sb.AppendLine($"  {Ms(s.OtherTicks) / seconds,8:F2} | {s.OtherCalls / seconds,9:F1} | {Ms(s.OtherTicks) / s.OtherCalls,8:F3}  {s.Name}");
            }
            lock (SlowLock)
            {
                if (SlowCalls.Count > 0)
                {
                    sb.AppendLine($"Slow single calls (over {SlowCallMs} ms, first {MaxSlowCallsPerReport}):");
                    foreach (string line in SlowCalls)
                        sb.AppendLine(line);
                }
                SlowCalls.Clear();
            }

            // Scene and memory
            if (GameCore.Get != null)
                sb.AppendLine(SceneCounts());
            sb.AppendLine($"Memory: managed heap {GC.GetTotalMemory(false) / 1048576.0:F0} MB, " +
                          $"Unity allocated {Profiler.GetTotalAllocatedMemoryLong() / 1048576.0:F0} MB, " +
                          $"reserved {Profiler.GetTotalReservedMemoryLong() / 1048576.0:F0} MB, " +
                          $"graphics driver {Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576.0:F0} MB, " +
                          $"garbage collections {GC.CollectionCount(0) - s_gcStart}");

            // Reset for the next period
            foreach (Stat s in Stats.Values)
            {
                s.MainTicks = s.OtherTicks = s.MainCalls = s.OtherCalls = s.MaxTicks = 0;
            }
            Frames.Clear();
            s_reportStart = Time.realtimeSinceStartup;
            s_gcStart = GC.CollectionCount(0);
            return sb.ToString();
        }

        private static double Pct(List<double> values, double q)
        {
            if (values.Count == 0)
                return 0;
            var sorted = values.OrderBy(v => v).ToList();
            return sorted[(int)(q * (sorted.Count - 1))];
        }

        private static string SceneCounts()
        {
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            int visible = renderers.Count(r => r.enabled && r.isVisible);
            int shadowCasters = renderers.Count(r => r.enabled && r.isVisible && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off);
            ParticleSystem[] particles = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
            Animator[] animators = UnityEngine.Object.FindObjectsOfType<Animator>();
            return $"Scene: {UnityEngine.Object.FindObjectsOfType<Dinosaur>().Length} dinos, " +
                   $"{UnityEngine.Object.FindObjectsOfType<Human>().Length} visitors, " +
                   $"{UnityEngine.Object.FindObjectsOfType<Employee>().Length} staff, " +
                   $"{UnityEngine.Object.FindObjectsOfType<Transform>().Length} objects, " +
                   $"{renderers.Length} renderers ({visible} visible, {shadowCasters} visible casting shadows), " +
                   $"{animators.Count(a => a.isActiveAndEnabled)} animators, " +
                   $"{particles.Count(p => p.isPlaying)} particle systems playing, " +
                   $"{lights.Count(l => l.isActiveAndEnabled)} lights ({lights.Count(l => l.isActiveAndEnabled && l.shadows != LightShadows.None)} with shadows), " +
                   $"{UnityEngine.Object.FindObjectsOfType<NavMeshAgent>().Length} NavMesh agents, " +
                   $"{UnityEngine.Object.FindObjectsOfType<AudioSource>().Count(a => a.isPlaying)} Unity audio sources playing\n" +
                   PhysicsCounts();
        }

        // Physics: what's in the simulation, and colliders on things that move without a Rigidbody
        // (PhysX treats those as static and has to rebuild its static tree when they move)
        private static string PhysicsCounts()
        {
            Rigidbody[] bodies = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
            int movingStatic = 0;
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Human>())
                movingStatic += a.GetComponentsInChildren<Collider>().Count(c => c.enabled && c.attachedRigidbody == null);
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Employee>())
                movingStatic += a.GetComponentsInChildren<Collider>().Count(c => c.enabled && c.attachedRigidbody == null);
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Dinosaur>())
                movingStatic += a.GetComponentsInChildren<Collider>().Count(c => c.enabled && c.attachedRigidbody == null);
            var peopleLayers = UnityEngine.Object.FindObjectsOfType<Human>().SelectMany(h => h.GetComponentsInChildren<Collider>())
                .Where(c => c.enabled).GroupBy(c => c.gameObject.layer)
                .Select(g => $"layer {g.Key} ({LayerMask.LayerToName(g.Key)}): {g.Count()}x {string.Join("/", g.Select(c => c.GetType().Name + (c.isTrigger ? " trigger" : "")).Distinct())}" +
                             $", hits layers [{string.Join(",", Enumerable.Range(0, 32).Where(l => !Physics.GetIgnoreLayerCollision(g.Key, l) && colliders.Any(c => c.gameObject.layer == l)))}]");
            var allLayers = colliders.Where(c => c.enabled).GroupBy(c => c.gameObject.layer).OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} {LayerMask.LayerToName(g.Key)}: {g.Count()}");
            return $"Visitor colliders: {string.Join("; ", peopleLayers)}\nColliders per layer: {string.Join(", ", allLayers)}\n" +
                   $"Physics: step {Time.fixedDeltaTime * 1000f:F0} ms game time, max {Time.maximumDeltaTime * 1000f:F0} ms per frame, " +
                   $"{bodies.Length} rigidbodies ({bodies.Count(b => b.isKinematic)} kinematic, {bodies.Count(b => !b.IsSleeping())} awake), " +
                   $"{colliders.Count(c => c.enabled)} colliders ({colliders.Count(c => c.enabled && c.isTrigger)} triggers, " +
                   $"{colliders.Count(c => c.enabled && c.attachedRigidbody == null)} without rigidbody), " +
                   $"{movingStatic} colliders on visitors/staff/dinos without rigidbody, " +
                   $"auto sync transforms {Physics.autoSyncTransforms}, auto simulation {Physics.autoSimulation}";
        }

        // Textures, meshes, shaders and graphics settings
        private static string AssetReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"===== {DateTime.Now:HH:mm:ss}  ASSETS AND GRAPHICS SETTINGS (park loaded)");

            sb.AppendLine($"Screen {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRate} Hz, fullscreen {Screen.fullScreenMode}, " +
                          $"vSync {QualitySettings.vSyncCount}, target fps {Application.targetFrameRate}, quality level " +
                          $"{QualitySettings.names[QualitySettings.GetQualityLevel()]}");
            sb.AppendLine($"Shadows {QualitySettings.shadows}, resolution {QualitySettings.shadowResolution}, distance {QualitySettings.shadowDistance}, " +
                          $"cascades {QualitySettings.shadowCascades}, anti-aliasing {QualitySettings.antiAliasing}x, " +
                          $"anisotropic {QualitySettings.anisotropicFiltering}, texture limit {QualitySettings.masterTextureLimit}, " +
                          $"LOD bias {QualitySettings.lodBias}, pixel lights {QualitySettings.pixelLightCount}, " +
                          $"soft particles {QualitySettings.softParticles}, realtime reflection probes {QualitySettings.realtimeReflectionProbes}");
            foreach (Camera cam in Camera.allCameras)
                sb.AppendLine($"Camera {cam.name}: depth {cam.depth}, HDR {cam.allowHDR}, MSAA {cam.allowMSAA}, path {cam.actualRenderingPath}, " +
                              $"far {cam.farClipPlane}, culling mask {cam.cullingMask:X}, target {(cam.targetTexture != null ? cam.targetTexture.name : "screen")}, " +
                              $"effects: {string.Join(", ", cam.GetComponents<MonoBehaviour>().Where(b => b != null && b.enabled).Select(b => b.GetType().Name))}");

            // Textures
            var textures = Resources.FindObjectsOfTypeAll<Texture>()
                .Select(t => new { t, bytes = TextureBytes(t) })
                .OrderByDescending(x => x.bytes).ToList();
            long texTotal = textures.Sum(x => x.bytes);
            sb.AppendLine($"Textures: {textures.Count}, about {texTotal / 1048576.0:F0} MB in total");
            foreach (var g in textures.GroupBy(x => x.t is Texture2D t2 ? t2.format.ToString() : x.t.GetType().Name)
                         .OrderByDescending(g => g.Sum(x => x.bytes)).Take(12))
                sb.AppendLine($"  {g.Sum(x => x.bytes) / 1048576.0,7:F1} MB  {g.Count(),5}x  {g.Key}");
            int readable = textures.Count(x => x.t is Texture2D t2 && t2.isReadable);
            sb.AppendLine($"  {readable} textures are CPU-readable (they keep a second copy in RAM)");
            sb.AppendLine("Largest textures (MB | size | format | mips | readable):");
            foreach (var x in textures.Take(40))
            {
                Texture t = x.t;
                string format = t is Texture2D t2 ? t2.format.ToString() : t is RenderTexture rt ? "RT " + rt.format : t.GetType().Name;
                bool isReadable = t is Texture2D t3 && t3.isReadable;
                sb.AppendLine($"  {x.bytes / 1048576.0,7:F2} | {t.width}x{t.height} | {format} | {t.mipmapCount} | {(isReadable ? "yes" : "")}  {t.name}");
            }

            // Meshes
            Mesh[] meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            long vertices = meshes.Sum(m => (long)m.vertexCount);
            sb.AppendLine($"Meshes: {meshes.Length}, {vertices / 1000}k vertices in total, {meshes.Count(m => m.isReadable)} CPU-readable");
            sb.AppendLine("Largest meshes (vertices | sub-meshes):");
            foreach (Mesh m in meshes.OrderByDescending(m => m.vertexCount).Take(20))
                sb.AppendLine($"  {m.vertexCount,8} | {m.subMeshCount}  {m.name}");

            // Shaders in use by renderers in the scene
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var shaderUse = new Dictionary<string, int>();
            int materialSlots = 0;
            foreach (Renderer r in renderers)
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null) continue;
                    materialSlots++;
                    string key = mat.shader != null ? mat.shader.name : "(none)";
                    shaderUse[key] = shaderUse.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            sb.AppendLine($"Materials: {Resources.FindObjectsOfTypeAll<Material>().Length} loaded, {materialSlots} material slots on " +
                          $"{renderers.Length} renderers in the scene");
            sb.AppendLine("Shaders by number of renderer slots:");
            foreach (var kv in shaderUse.OrderByDescending(kv => kv.Value).Take(25))
                sb.AppendLine($"  {kv.Value,6}  {kv.Key}");

            // Renderers by type
            sb.AppendLine("Renderers by type: " + string.Join(", ", renderers.GroupBy(r => r.GetType().Name)
                .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")));
            return sb.ToString();
        }

        private static long TextureBytes(Texture t)
        {
            long measured = 0;
            try { measured = Profiler.GetRuntimeMemorySizeLong(t); }
            catch (Exception) { }
            if (measured > 0)
                return measured;
            float bpp = 32f;
            if (t is Texture2D t2)
                bpp = BitsPerPixel(t2.format);
            else if (t is RenderTexture rt)
                bpp = rt.format == RenderTextureFormat.ARGBHalf ? 64f : rt.format == RenderTextureFormat.ARGBFloat ? 128f : 32f;
            double bytes = (double)t.width * t.height * bpp / 8.0;
            if (t.mipmapCount > 1)
                bytes *= 4.0 / 3.0;
            return (long)bytes;
        }

        private static float BitsPerPixel(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.BC4:
                    return 4f;
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.BC5:
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return 8f;
                case TextureFormat.R16:
                case TextureFormat.RHalf:
                case TextureFormat.RGB565:
                case TextureFormat.ARGB4444:
                case TextureFormat.RGBA4444:
                    return 16f;
                case TextureFormat.RGB24:
                    return 24f;
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGFloat:
                    return 64f;
                case TextureFormat.RGBAFloat:
                    return 128f;
                default:
                    return 32f;
            }
        }
    }
}
