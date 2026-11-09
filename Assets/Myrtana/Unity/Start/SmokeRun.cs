using System;
using System.Globalization;
using System.IO;
using G3.Unity;
using Myrtana.Session;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>
    /// build-1026a: the automated smoke test of a built player (any build, the demo included), started with
    /// <c>-g3smoke [seconds]</c>, usually together with <c>-batchmode</c> (no window, no input, no focus change):
    /// the logos play, the title starts a new game at once (no AB question), the intro is skipped, the hero stands in the
    /// world for <c>seconds</c> (default 20), then one <c>[Myrtana] smoke:</c> line goes to Player.log and the player quits:
    /// exit code 0 = in the world without errors, 2 = in the world with errors or exceptions logged, 1 = the world was not
    /// reached in 5 minutes. <c>-g3smokeshot file.png</c> also renders the main camera offscreen into that file.
    /// No crash-recovery snapshots are written (the Saves folder is the owner's); autosave is 10 minutes away.
    /// </summary>
    public sealed class SmokeRun : MonoBehaviour
    {
        public const string CommandLineSwitch = "-g3smoke";
        public const string ShotSwitch = "-g3smokeshot";
        const float DefaultSeconds = 20f, TimeoutSeconds = 300f, IntroSkipSeconds = 3f;

        /// <summary>The player was started with <see cref="CommandLineSwitch"/>.</summary>
        public static bool Active { get; private set; }

        float _seconds = DefaultSeconds;
        string _shot;
        bool _newGameAsked;
        float _introAt = -1f, _playingAt = -1f;
        int _frames, _errors;
        string _firstError;
        bool _done;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoStart()
        {
            if (Application.isEditor) return;
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, CommandLineSwitch);
            if (i < 0) return;
            Active = true;
            var go = new GameObject("G3SmokeRun");
            DontDestroyOnLoad(go);
            var run = go.AddComponent<SmokeRun>();
            if (i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0)
                run._seconds = s;
            int j = Array.IndexOf(args, ShotSwitch);
            if (j >= 0 && j + 1 < args.Length) run._shot = args[j + 1];
            Debug.Log($"[Myrtana] smoke: armed ({run._seconds:0} s in the world{(run._shot != null ? ", shot " + run._shot : "")})");
        }

        void Awake() => Application.logMessageReceived += OnLog;
        void OnDestroy() => Application.logMessageReceived -= OnLog;

        void OnLog(string text, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (text.StartsWith("[Myrtana] smoke:", StringComparison.Ordinal)) return;
            if (_errors++ == 0) _firstError = text.Length > 300 ? text.Substring(0, 300) : text;
        }

        void Update()
        {
            if (_done) return;
            Application.runInBackground = true;           // the video screen resets it after the logos
            SaveGameController.CrashRecoveryEnabled = false; // the options applier sets it from the player's choice
            float now = Time.realtimeSinceStartup;
            var start = FindAnyObjectByType<GameStartFlow>();
            var flow = start != null ? start.Flow : null;
            if (flow != null)
            {
                switch (flow.Phase)
                {
                    case GamePhase.Title:
                        if (!_newGameAsked)
                        {
                            _newGameAsked = true;
                            Debug.Log($"[Myrtana] smoke: title at {now:0.0} s, new game");
                            flow.StartNewGame();
                        }
                        break;
                    case GamePhase.Intro:
                        if (_introAt < 0) _introAt = now;
                        else if (now - _introAt > IntroSkipSeconds)
                        {
                            Debug.Log($"[Myrtana] smoke: intro skipped at {now:0.0} s");
                            start.SkipVideo();
                        }
                        break;
                    case GamePhase.Playing:
                        if (LoadingHold.IsHeld) break;
                        if (_playingAt < 0)
                        {
                            _playingAt = now;
                            _frames = 0;
                            Debug.Log($"[Myrtana] smoke: in the world at {now:0.0} s");
                        }
                        _frames++;
                        if (now - _playingAt >= _seconds) Finish(start, now);
                        break;
                }
            }
            if (_playingAt < 0 && now > TimeoutSeconds)
            {
                _done = true;
                Debug.LogError($"[Myrtana] smoke: FAIL world not reached in {TimeoutSeconds:0} s (phase {(flow != null ? flow.Phase.ToString() : "no flow")})");
                Application.Quit(1);
            }
        }

        void Finish(GameStartFlow start, float now)
        {
            _done = true;
            var player = FindAnyObjectByType<G3Player>();
            var streamer = FindAnyObjectByType<G3WorldStreamer>();
            float fps = _frames / Mathf.Max(0.001f, now - _playingAt);
            string shot = _shot != null ? Shot(_shot) : "-";
            bool ok = _errors == 0;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[Myrtana] smoke: {0} hero {1} cells loaded {2} loading {3}, {4:0.0} fps over {5:0} s, {6} errors{7}, shot {8}, api {9}",
                ok ? "OK" : "ERRORS", player != null ? player.transform.position.ToString("F1") : "none",
                streamer != null ? streamer.LoadedCount : -1, streamer != null ? streamer.LoadingCount : -1, fps, now - _playingAt,
                _errors, ok ? "" : " (first: " + _firstError + ")", shot, SystemInfo.graphicsDeviceType));
            Application.Quit(ok ? 0 : 2);
        }

        static string Shot(string path)
        {
            var cam = Camera.main;
            if (cam == null) return "no camera";
            var rt = RenderTexture.GetTemporary(1280, 720, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Destroy(tex);
                return path;
            }
            catch (Exception ex) { return "failed: " + ex.Message; }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
