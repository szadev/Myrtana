using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace G3.Unity
{
    /// <summary>
    /// fix-251: the always-on long-frame log of the player and the editor (<see cref="HitchRules"/>): one
    /// <c>[G3Hitch]</c> line per frame over 250 ms with the wall clock, background (focus) pauses told apart from real
    /// freezes, GC counts, managed heap, loaded scenes, streaming state, the camera position and the marks systems left
    /// (<see cref="Mark"/>: scene loads/unloads, saves, asset clean-ups, pipeline rebuilds). Cost per frame: a float compare
    /// and three GC.CollectionCount reads.
    /// </summary>
    [DefaultExecutionOrder(-30000)]
    public sealed class G3HitchLog : MonoBehaviour
    {
        static readonly HitchRules Rules = new HitchRules();
        static G3HitchLog _instance;

        int _gc0, _gc1, _gc2;
        double _blurredAt = -1, _backgroundSeconds;
        int _focusFrames;   // frames still counted as "after a background pause"
        G3WorldStreamer _streamer;

        /// <summary>The frame now running follows a background (focus-loss) pause: its delta time is not a freeze.</summary>
        public static bool FrameSpansBackground => _instance != null && _instance._focusFrames > 0;

        /// <summary>Notes what a system did in this frame; shown if the frame turns out long (main thread).</summary>
        public static void Mark(string what) => Rules.Mark(what);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (_instance != null) return;
            var go = new GameObject("G3HitchLog") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<G3HitchLog>();
        }

        void OnEnable()
        {
            SceneManager.sceneLoaded += OnLoaded;
            SceneManager.sceneUnloaded += OnUnloaded;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneUnloaded -= OnUnloaded;
        }

        static void OnLoaded(Scene s, LoadSceneMode m) => Rules.Mark("loaded " + s.name);
        static void OnUnloaded(Scene s) => Rules.Mark("unloaded " + s.name);

        void OnApplicationFocus(bool focus) => Background(!focus);
        void OnApplicationPause(bool pause) => Background(pause);

        void Background(bool away)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (away) { if (_blurredAt < 0) _blurredAt = now; return; }
            if (_blurredAt < 0) return;
            _backgroundSeconds = now - _blurredAt;
            _blurredAt = -1;
            _focusFrames = 2;   // the resume frame and the one after carry the pause
        }

        void Update()
        {
            double ms = Time.unscaledDeltaTime * 1000.0;
            bool background = _focusFrames > 0 || _blurredAt >= 0;
            var kind = HitchRules.Classify(ms, background);
            if (kind != HitchKind.None && Rules.Allow(Time.realtimeSinceStartupAsDouble))
            {
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                Debug.Log(Rules.Line(kind, ms, DateTime.Now.ToString("HH:mm:ss"), Time.frameCount, _backgroundSeconds,
                                     g0 - _gc0, g1 - _gc1, g2 - _gc2, GC.GetTotalMemory(false) / 1048576.0, Context()));
            }
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            if (_focusFrames > 0) _focusFrames--;
        }

        // the frame's marks are kept one more frame: its length shows in the next frame's delta time
        void LateUpdate() => Rules.EndFrame();

        string Context()
        {
            if (_streamer == null) _streamer = FindAnyObjectByType<G3WorldStreamer>();
            var cam = Camera.main;
            string at = cam != null ? $"at ({cam.transform.position.x:0},{cam.transform.position.y:0},{cam.transform.position.z:0})" : "no camera";
            string streaming = _streamer != null ? $", cells loading {_streamer.LoadingCount}, loaded {_streamer.LoadedCount}" : "";
            return $"{at}, scenes {SceneManager.sceneCount}{streaming}, timeScale {Time.timeScale:0.##}";
        }
    }
}
