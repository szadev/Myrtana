using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace G3.Unity
{
    /// <summary>
    /// dx12-3 (D3D12 + graphics jobs DEVICE_HUNG, Docs/performance.md "D3D12 GPU hang"): D3D12-safe lifetimes for the GPU
    /// objects the remaster swaps at runtime, and the save-picture capture guard.
    /// <list type="bullet">
    /// <item><see cref="Release(GraphicsBuffer)"/> / <see cref="Release(RenderTexture)"/> / <see cref="Destroy"/>: freed
    /// <see cref="DelayFrames"/> frames later (start of a later frame, before any script), never under a draw queued earlier
    /// in the same frame or a global binding not yet replaced (<see cref="DeferredRelease{T}"/>).</item>
    /// <item><see cref="BeginCapture"/> / <see cref="EndCapture"/>: while a script renders the main camera itself (the save
    /// picture, from Update) the per-camera GPU work keyed to the main camera (grass v2 / scatter indirect culls, GI,
    /// exposure, SSGI and volumetric histories) is skipped: those would run a second time per frame at the picture's
    /// size, swap histories and queue indirect draws that outlive the capture.</item>
    /// </list>
    /// Outside Play everything is immediate.
    /// </summary>
    public static class G3GpuSafe
    {
        public const int DelayFrames = 3;

        static readonly DeferredRelease<GraphicsBuffer> Buffers = new DeferredRelease<GraphicsBuffer>(DelayFrames);
        static readonly DeferredRelease<Object> Objects = new DeferredRelease<Object>(DelayFrames);
        static int _capture;
        static bool _quitting, _inserted;

        /// <summary>True while a script renders a camera into a picture (<see cref="BeginCapture"/>).</summary>
        public static bool Capturing => _capture > 0;

        public static int Pending => Buffers.Count + Objects.Count;

        public static void BeginCapture(string what)
        {
            _capture++;
            G3GpuDiag.Event("capture begin " + what);
        }

        public static void EndCapture(string what)
        {
            if (_capture > 0) _capture--;
            G3GpuDiag.Event("capture end " + what);
        }

        public static void Release(GraphicsBuffer b)
        {
            if (b == null) return;
            if (Immediate) { b.Release(); return; }
            EnsureInserted();
            Buffers.Add(b, Time.frameCount);
            G3GpuDiag.Count("safe.buffer");
        }

        /// <summary>A render texture: released and destroyed later.</summary>
        public static void Release(RenderTexture rt)
        {
            if (rt == null) return;
            Destroy(rt);
        }

        /// <summary>A material, mesh, texture (render textures are released first) destroyed later.</summary>
        public static void Destroy(Object o)
        {
            if (o == null) return;
            if (Immediate) { DestroyNow(o); return; }
            EnsureInserted();
            Objects.Add(o, Time.frameCount);
            G3GpuDiag.Count("safe.object");
        }

        static bool Immediate => _quitting || !Application.isPlaying;

        static void DestroyNow(Object o)
        {
            if (o == null) return;
            if (o is RenderTexture rt) rt.Release();
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        static void ReleaseBuffer(GraphicsBuffer b)
        {
            try { b.Release(); } catch (Exception) { /* already gone */ }
        }

        static void Tick()
        {
            int frame = Time.frameCount;
            G3GpuDiag.EndFrame(frame - 1);
            if (Buffers.Count == 0 && Objects.Count == 0) return;
            int n = Buffers.Drain(frame, ReleaseBuffer) + Objects.Drain(frame, DestroyNow);
            if (n > 0) G3GpuDiag.Event($"safe released {n}");
        }

        static void DrainAll()
        {
            Buffers.DrainAll(ReleaseBuffer);
            Objects.DrainAll(DestroyNow);
        }

        /// <summary>Runs at the start of every frame (PreUpdate, before any script).</summary>
        internal static void EnsureInserted()
        {
            if (_inserted) return;
            var root = PlayerLoop.GetCurrentPlayerLoop();
            if (Contains(root)) { _inserted = true; return; }
            var systems = root.subSystemList;
            if (systems == null) return;
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i].type != typeof(PreUpdate)) continue;
                var sub = new List<PlayerLoopSystem>(systems[i].subSystemList ?? new PlayerLoopSystem[0]);
                sub.Insert(0, new PlayerLoopSystem { type = typeof(G3GpuSafeTick), updateDelegate = Tick });
                systems[i].subSystemList = sub.ToArray();
                root.subSystemList = systems;
                PlayerLoop.SetPlayerLoop(root);
                _inserted = true;
                return;
            }
        }

        static bool Contains(PlayerLoopSystem s)
        {
            if (s.type == typeof(G3GpuSafeTick)) return true;
            if (s.subSystemList != null)
                foreach (var c in s.subSystemList)
                    if (Contains(c)) return true;
            return false;
        }

        struct G3GpuSafeTick { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            DrainAll();
            _capture = 0;
            _quitting = false;
            _inserted = false;
            Application.quitting -= OnQuit;
            Application.quitting += OnQuit;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            EnsureInserted();   // the diagnostics line per frame needs the tick even with nothing to release
        }

        static void OnQuit()
        {
            _quitting = true;
            DrainAll();
        }
    }

    /// <summary>
    /// dx12-3: GPU diagnostics mode (<c>-g3gpudiag</c>; launcher option "GPU diagnostics" adds <c>-force-d3d12-debug</c>,
    /// Unity's D3D12 debug layer with DRED, so a hang reports the failing command). Writes
    /// <c>persistentDataPath/gpudiag.log</c> (the previous run's is kept as gpudiag-prev.log), flushed per line so it
    /// survives a GPU hang: one line per frame with the GPU-side steps of that frame (grass/scatter culls, relief mesh
    /// writes, rain shadow uploads, GI / exposure dispatches, deferred releases, ...), and event lines for buffer creation,
    /// release, uploads and captures with frame numbers. Command buffers of those subsystems get named GPU markers
    /// (BeginSample) so the D3D12 breadcrumbs name the pass. Off: every call is one bool test.
    /// </summary>
    public static class G3GpuDiag
    {
        static readonly GpuDiagFrame Frame = new GpuDiagFrame();
        static StreamWriter _w;

        public static bool On { get; private set; }

        public static string FilePath { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Close();
            On = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            bool on;
            try { on = GpuDiagSwitch.Enabled(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(GpuDiagSwitch.EnvVar)); }
            catch (Exception) { return; }
            if (!on) return;
            try
            {
                string dir = Application.persistentDataPath;
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, "gpudiag.log");
                string prev = Path.Combine(dir, "gpudiag-prev.log");
                if (File.Exists(FilePath)) { if (File.Exists(prev)) File.Delete(prev); File.Move(FilePath, prev); }
                _w = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                On = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[G3GpuDiag] cannot open the log: " + e.Message);
                return;
            }
            Application.quitting -= Close;
            Application.quitting += Close;
            string head = $"[G3GpuDiag] on: {SystemInfo.graphicsDeviceType} {SystemInfo.graphicsDeviceVersion}, threading " +
                          $"{SystemInfo.renderingThreadingMode}, {SystemInfo.graphicsDeviceName} driver {SystemInfo.graphicsDeviceVersion}, " +
                          $"VRAM {SystemInfo.graphicsMemorySize} MB, args: {string.Join(" ", Environment.GetCommandLineArgs(), 1, Math.Max(0, Environment.GetCommandLineArgs().Length - 1))}";
            Debug.Log(head + " -> " + FilePath);
            Write(head);
            G3GpuSafe.EnsureInserted();
        }

        /// <summary>One more of <paramref name="key"/> in this frame's line.</summary>
        public static void Count(string key, int n = 1)
        {
            if (On) Frame.Add(key, n);
        }

        /// <summary>An event line now (frame number and time added).</summary>
        public static void Event(string text)
        {
            if (!On) return;
            Write($"f{Time.frameCount} t{Time.realtimeSinceStartupAsDouble:0.000} {text}");
        }

        /// <summary>A named GPU marker around the following commands (diagnostics only; pair with <see cref="End"/>).</summary>
        public static void Begin(CommandBuffer cmd, string name)
        {
            if (On && cmd != null) cmd.BeginSample(name);
        }

        public static void End(CommandBuffer cmd, string name)
        {
            if (On && cmd != null) cmd.EndSample(name);
        }

        internal static void EndFrame(int frame)
        {
            if (!On) return;   // every frame, so the last line before a hang dates it
            Write(Frame.Take(frame) + $" dt{Time.unscaledDeltaTime * 1000:0.0}");
        }

        static void Write(string line)
        {
            try { _w?.WriteLine(line); }
            catch (Exception) { /* disk full / closed: diagnostics only */ }
        }

        static void Close()
        {
            try { _w?.Dispose(); } catch (Exception) { }
            _w = null;
        }
    }
}
