using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Frame-by-frame trace of the first seconds after start (Docs/performance.md, "Startup"): frame time, the
    /// profiler markers that explain long frames (scene integration, prefab merging, shader/PSO creation, GC …) and the
    /// streamer state (cells loaded, player held, deferred activations pending). Written to Logs/startup_trace.txt
    /// (editor) or startup_trace.txt next to the player. Enabled by the player argument <c>-g3startup</c> or, in the
    /// editor, by the file Temp/g3_startup_trace (created by G3PerfProfile.ArmStartupTrace).
    /// </summary>
    [DefaultExecutionOrder(-10000)]   // first in the frame: the renderer delta then belongs to the frame the recorders report
    public sealed class G3StartupTrace : MonoBehaviour
    {
        public const string EditorFlagFile = "Temp/g3_startup_trace";
        [Tooltip("The trace ends this many seconds after the player is released (the loading screen is gone)…")]
        public float secondsAfterRelease = 30f;
        [Tooltip("…or after this many seconds in total.")]
        public float seconds = 150f;
        [Tooltip("Frames longer than this (ms) are listed with their markers.")]
        public float hitchMs = 25f;

        static readonly string[] Markers =
        {
            "Application.LoadLevelAsync Integrate", "Prefabs.MergePrefabs", "Loading.LockPersistentManager",
            "Loading.AwakeFromLoad", "Physics.BakePhysXCollisionMeshData", "GameObject.ActivateAwakeRecursively",
            "GRD.RegisterMaterialsAndMeshes", "Shader.CreateGPUProgram", "Shader.EditorCompileVariant",
            "CreateGraphicsPipelineImpl", "GraphicsStateCollection.Warmup", "GC.Collect", "GarbageCollectAssetsProfile",
            "SoundManager.IntegrateFMODSound", "Update.ScriptRunBehaviourUpdate", "PreLateUpdate.ScriptRunBehaviourLateUpdate",
            "Loading.ReadObject", "Loading.LoadFileHeaders", "Instantiate", "FixedUpdate.PhysicsFixedUpdate",
            "Gfx.WaitForPresentOnGfxThread", "Gfx.WaitForRenderThread", "Texture.AwakeFromLoad", "Mesh.AwakeFromLoad",
            "Shader.Parse", "RenderPipelineManager.DoRenderLoop_Internal()", "GPUResidentDrawer",
            GrdRegister, GrdProbes, "DispatchProbeUpdateCommand", "Inl_ScriptableRenderContext.Submit", "PxScene.simulate",
            "CellNavMeshes.CollectSources",
        };
        // GPU Resident Drawer work for newly enabled renderers (G3DeferredActivation.MaxRenderersPerFrame).
        const string GrdRegister = "GPUDrivenProcessor.EnableGPUDrivenRenderingAndDispatchRendererData",
                     GrdProbes = "InterpolateProbesAndUpdateTetrahedronCache";

        readonly List<(string name, ProfilerRecorder rec)> _recorders = new List<(string, ProfilerRecorder)>();
        readonly StringBuilder _hitches = new StringBuilder();
        readonly StringBuilder _seconds = new StringBuilder();
        float _t0, _lastHitch;
        int _sec, _secFrames, _frames, _hitchCount;
        float _secMax, _secSum, _readyAt = -1;
        // Frames after the release: what the player feels while the rest of the area streams in.
        int _afterFrames, _after25, _after50, _after100;
        float _afterMax, _afterSum;
        G3WorldStreamer _streamer;
        // Renderers woken by G3DeferredActivation after the release and the GRD cost of the frames they were woken in.
        long _renderersSeen;
        int _wakeFrames, _maxRenderers;
        long _woken;
        double _wakeRegisterMs, _wakeProbesMs, _quietRegisterMs, _quietProbesMs;
        int _quietFrames;
        HashSet<string> _missing;

        static string LogPath => Path.GetFullPath(Path.Combine(Application.dataPath, Application.isEditor ? "../Logs/startup_trace.txt" : "../startup_trace.txt"));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoStart()
        {
            bool on = Application.isEditor
                ? File.Exists(Path.Combine(Application.dataPath, "..", EditorFlagFile))
                : Array.IndexOf(Environment.GetCommandLineArgs(), "-g3startup") >= 0;
            if (!on) return;
            var go = new GameObject("G3StartupTrace");
            DontDestroyOnLoad(go);
            go.AddComponent<G3StartupTrace>();
        }

        void Awake()
        {
            _t0 = Time.realtimeSinceStartup;
            _missing = new HashSet<string>(Markers);
            AddRecorders();
            if (!Application.isEditor && Array.IndexOf(Environment.GetCommandLineArgs(), "-g3bootprofile") >= 0) StartCapture("startup_boot");
            // The player pauses its loop while unfocused (Player Settings runInBackground off): a focus loss then
            // showed up as one multi-second "stall" without markers (perf-5). Keep running and log focus changes.
            Application.runInBackground = true;
        }

        void OnApplicationFocus(bool focus)
        {
            _hitches.Append(string.Format(CultureInfo.InvariantCulture, "t={0,5:F1}s focus {1}", Time.realtimeSinceStartup - _t0, focus ? "gained" : "LOST")).AppendLine();
        }

        // Markers registered by now (C# markers, e.g. the GPU Resident Drawer's, appear only when first used: rescanned at the release).
        void AddRecorders()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var h in handles)
            {
                var name = ProfilerRecorderHandle.GetDescription(h).Name;
                if (!_missing.Remove(name)) continue;
                var rec = new ProfilerRecorder(h, 1, ProfilerRecorderOptions.Default);
                rec.Start();
                _recorders.Add((name, rec));
            }
        }

        double LastMs(string marker)
        {
            foreach (var (n, r) in _recorders) if (n == marker) return r.LastValue * 1e-6;
            return 0;
        }

        void Update()
        {
            float t = Time.realtimeSinceStartup - _t0;
            float dt = Time.unscaledDeltaTime * 1000f;
            if (_streamer == null) _streamer = FindAnyObjectByType<G3WorldStreamer>();
            bool held = _streamer != null && _streamer.IsHolding;
            if (_readyAt < 0 && _frames > 2 && !held)
            {
                _readyAt = t;
                AddRecorders();
                if (_capturing) StopCapture();   // -g3bootprofile: start-up only
                else if (Array.IndexOf(Environment.GetCommandLineArgs(), "-g3startupprofile") >= 0) StartCapture("startup_profile");
            }
            long seen = G3DeferredActivation.RenderersWoken;
            int woken = (int)(seen - _renderersSeen);
            _renderersSeen = seen;
            if (_capturing && _readyAt >= 0 && t > _readyAt + CaptureSeconds) StopCapture();
            _frames++;
            if (_readyAt >= 0 && t > _readyAt && _frames > 1)
            {
                _afterFrames++; _afterSum += dt; _afterMax = Mathf.Max(_afterMax, dt);
                if (dt > 25) _after25++;
                if (dt > 50) _after50++;
                if (dt > 100) _after100++;
                double reg = LastMs(GrdRegister), probes = LastMs(GrdProbes);
                if (woken > 0) { _wakeFrames++; _woken += woken; _wakeRegisterMs += reg; _wakeProbesMs += probes; _maxRenderers = Mathf.Max(_maxRenderers, woken); }
                else { _quietFrames++; _quietRegisterMs += reg; _quietProbesMs += probes; }
            }

            if (dt > hitchMs)
            {
                _hitchCount++;
                _lastHitch = t;
                _hitches.Append(string.Format(CultureInfo.InvariantCulture, "t={0,5:F1}s {1,6:F0} ms {2} renderers {3}|", t, dt, State(held), woken));
                var parts = new List<(string n, double ms)>();
                foreach (var (n, r) in _recorders) { double ms = r.LastValue * 1e-6; if (ms >= 2) parts.Add((n, ms)); }
                parts.Sort((a, b) => b.ms.CompareTo(a.ms));
                foreach (var (n, ms) in parts) _hitches.Append(' ').Append(n).Append('=').Append(ms.ToString("F0", CultureInfo.InvariantCulture));
                _hitches.AppendLine();
            }

            if ((int)t != _sec) FlushSecond(held);
            _secFrames++; _secSum += dt; _secMax = Mathf.Max(_secMax, dt);
            if (t >= seconds || (_readyAt >= 0 && t >= _readyAt + secondsAfterRelease)) Destroy(gameObject);   // writes the trace (OnDestroy)
        }

        // Player argument -g3startupprofile: binary profiler log of the first seconds after the release
        // (startup_profile.raw next to the exe; open it in the editor with ProfilerDriver.LoadProfile / G3PerfProfile.DumpWorst).
        // -g3bootprofile instead: from before the first scene load until the release (startup_boot.raw).
        const float CaptureSeconds = 12f;
        bool _capturing;

        void StartCapture(string file)
        {
            if (Application.isEditor) return;
            UnityEngine.Profiling.Profiler.logFile = Path.GetFullPath(Path.Combine(Application.dataPath, "..", file));
            UnityEngine.Profiling.Profiler.enableBinaryLog = true;
            UnityEngine.Profiling.Profiler.enabled = true;
            _capturing = true;
        }

        void StopCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            UnityEngine.Profiling.Profiler.enabled = false;
            UnityEngine.Profiling.Profiler.enableBinaryLog = false;
            UnityEngine.Profiling.Profiler.logFile = "";
        }

        string State(bool held) => _streamer == null ? "" :
            $"cells {_streamer.LoadedCount}+{_streamer.LoadingCount} {(held ? "HELD " : "")}deferred {G3DeferredActivation.Pending}";

        void FlushSecond(bool held)
        {
            if (_secFrames > 0)
                _seconds.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,3}s {1,4} frames avg {2,6:F1} max {3,6:F0} ms  {4}",
                    _sec, _secFrames, _secSum / _secFrames, _secMax, State(held)));
            _sec = (int)(Time.realtimeSinceStartup - _t0);
            _secFrames = 0; _secSum = 0; _secMax = 0;
        }

        // Also runs when Play mode stops / the player quits early: the trace so far is still written.
        void OnDestroy()
        {
            StopCapture();
            FlushSecond(_streamer != null && _streamer.IsHolding);
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} startup {(Application.isEditor ? "editor" : "player")} {SystemInfo.graphicsDeviceType} {Screen.width}x{Screen.height}");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "player released at {0:F1}s, {1} frames > {2} ms, last at {3:F1}s",
                _readyAt, _hitchCount, hitchMs, _lastHitch));
            if (_afterFrames > 0)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "after release: {0} frames in {1:F0}s avg {2:F1} max {3:F0} ms, >25 ms {4}, >50 ms {5}, >100 ms {6}",
                    _afterFrames, secondsAfterRelease, _afterSum / _afterFrames, _afterMax, _after25, _after50, _after100));
            if (_wakeFrames > 0)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "GRD after release: {0} renderers woken in {1} frames (max {2}/frame, cap {3}); per frame with wakes: register {4:F2} ms, probes {5:F2} ms; " +
                    "without: register {6:F2}, probes {7:F2} ms; extra per renderer ~{8:F0} us",
                    _woken, _wakeFrames, _maxRenderers, G3DeferredActivation.MaxRenderersPerFrame, _wakeRegisterMs / _wakeFrames, _wakeProbesMs / _wakeFrames,
                    _quietFrames > 0 ? _quietRegisterMs / _quietFrames : 0, _quietFrames > 0 ? _quietProbesMs / _quietFrames : 0,
                    1000 * ((_wakeRegisterMs + _wakeProbesMs) - _wakeFrames * (_quietFrames > 0 ? (_quietRegisterMs + _quietProbesMs) / _quietFrames : 0)) / _woken));
            if (_missing.Count > 0) sb.AppendLine("markers not found: " + string.Join(", ", _missing));
            sb.Append(_seconds).Append(_hitches);
            try { File.AppendAllText(LogPath, sb.ToString()); } catch (Exception e) { Debug.LogException(e); }
            Debug.Log("G3StartupTrace written to " + LogPath);
            foreach (var (_, r) in _recorders) r.Dispose();
            _recorders.Clear();
        }
    }
}
