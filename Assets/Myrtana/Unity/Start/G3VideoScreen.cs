using System;
using System.Collections.Generic;
using System.IO;
using Myrtana.Unity.Ui.Skin;
using Myrtana.Ui.Skin;
using Myrtana.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace Myrtana.Unity
{
    /// <summary>
    /// Full-screen video in front of everything (G3's intro/credits menus and the start-up logos; Docs/game-start.md
    /// "Videos"). G3 ships Bink 1 files (<c>Data/Video/*.bik</c>) that Unity cannot play; the owner converts them once
    /// to <c>StreamingAssets/Video/&lt;name&gt;.webm</c> (VP8, or .mp4 H.264) with the commands in the doc. A missing file
    /// is reported once and skipped. G3 skips on any key. The start-up logos do the same (<see cref="VideoSkip.AnyPress"/>,
    /// fix-68: a press or click skips each logo; a press in the frame a video starts does not count); the intro and the
    /// credits, at the owner's earlier request, need Space (or Esc) held for <see cref="HoldToSkip"/> seconds, with a hint
    /// and a fill bar while held, so a stray key never skips them (<see cref="VideoSkip.Hold"/>). The video
    /// keeps its aspect ratio on black and runs on unscaled time, so it plays while the game is paused.
    /// </summary>
    public sealed class G3VideoScreen : MonoBehaviour
    {
        /// <summary>Above the menu (120).</summary>
        public const int SortingOrder = 130;
        public const string Folder = "Video";
        public const float HoldToSkip = 1f;
        VideoSkip _skip = VideoSkip.Hold;
        int _playFrame = -1;
        /// <summary>Seconds the hint stays after a key press that did not complete a hold.</summary>
        const float HintTime = 2.5f;
        static readonly string[] Extensions = { ".webm", ".mp4", ".mov" };
        static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        VisualElement _root, _picture, _hint, _fill;
        Label _hintText;
        Label _caption;
        float _captionUntil;
        float _held, _hintUntil;
        VideoPlayer _player;
        AudioSource _audio;
        RenderTexture _texture;
        Action _finished;
        bool _started;
        /// <summary>The player finished preparing the current (or preloaded) video.</summary>
        bool _prepared;
        /// <summary>A video prepared ahead (<see cref="Preload"/>) that <see cref="Play"/> takes over without preparing again.</summary>
        string _preloaded;
        // Watchdog (the editor runner saw "prepared, Play() called, isPlaying=false, frame 0" under the loading hold).
        float _playedAt;
        long _lastFrame = -1;
        int _retries;
        string _video;
        bool _forcedBackground;
        float _preparingSince;
        /// <summary>A video that does not finish preparing within this many seconds is skipped.</summary>
        const float PrepareTimeout = 10f;
        /// <summary>Seconds after Play() for the first frame (see <see cref="Watchdog"/>).</summary>
        const float FirstFrameTimeout = 2f;
        /// <summary>Seconds a playing video may show the same frame.</summary>
        const float StallTimeout = 3f;

        /// <summary>The converted file of a G3 video ("G3_Intro"), or null.</summary>
        public static string PathOf(string video)
        {
            if (string.IsNullOrEmpty(video)) return null;
            foreach (var ext in Extensions)
            {
                string path = Path.Combine(Application.streamingAssetsPath, Folder, video + ext);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public static bool Exists(string video) => PathOf(video) != null;

        public static G3VideoScreen Create(Transform parent)
        {
            var go = new GameObject("G3 video");
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<G3VideoScreen>();
            var doc = G3PanelSettings.CreateDocument("G3 video document", GuiPanelKind.Screen, go.transform, SortingOrder);
            screen._root = new VisualElement { name = "video" };
            screen._root.style.position = Position.Absolute;
            screen._root.style.left = screen._root.style.top = screen._root.style.right = screen._root.style.bottom = 0;
            screen._root.style.backgroundColor = Color.black;
            screen._root.style.display = DisplayStyle.None;
            screen._picture = new VisualElement { name = "picture", pickingMode = PickingMode.Ignore };
            screen._picture.style.position = Position.Absolute;
            screen._root.Add(screen._picture);
            screen.BuildHint();
            doc.rootVisualElement.Add(screen._root);
            screen._audio = go.AddComponent<AudioSource>();
            screen._audio.playOnAwake = false;
            screen._audio.ignoreListenerPause = true;
            screen._player = go.AddComponent<VideoPlayer>();
            screen._player.playOnAwake = false;
            screen._player.isLooping = false;
            screen._player.renderMode = VideoRenderMode.RenderTexture;
            screen._player.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            screen._player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            // A frame on screen before the clock runs, and late frames dropped instead of holding the clock back.
            screen._player.waitForFirstFrame = true;
            screen._player.skipOnDrop = true;
            screen._player.playbackSpeed = 1f;
            screen._player.prepareCompleted += screen.OnPrepared;
            screen._player.started += p =>
            {
                Debug.Log($"[Myrtana] video {screen._video}: started (frame {p.frame}, time {p.time:0.00})");
                StartupTimeline.Mark($"video {screen._video} started");
            };
            screen._player.clockResyncOccurred += (p, t) => Debug.Log($"[Myrtana] video {screen._video}: clock resync at {t:0.00} s");
            screen._player.loopPointReached += _ => screen.Finish();
            screen._player.errorReceived += (_, message) => { Debug.LogWarning("[Myrtana] video: " + message); screen.Finish(); };
            return screen;
        }

        public bool IsPlaying => _finished != null;

        /// <summary>
        /// The start-up (fix-98): the screen stays black from the first frame (G3 opens on black before its logos; the
        /// camera, the sky and the HUD are hidden behind it) and <paramref name="video"/> — the first logo — is prepared
        /// while the content packs load, so it starts the moment the flow asks for it. The cover stays until
        /// <see cref="Play"/> (or <see cref="Stop"/>).
        /// </summary>
        public void Preload(string video)
        {
            if (IsPlaying) return;
            _root.style.display = DisplayStyle.Flex;
            _hint.style.display = DisplayStyle.None;
            string path = PathOf(video);
            if (path == null) return;
            _video = video;
            _preloaded = video;
            _prepared = false;
            _started = false;
            Configure(path);
            Debug.Log($"[Myrtana] video {video}: preparing ahead (start-up, behind black)");
        }

        /// <summary>The player's outputs and source, then Prepare().</summary>
        void Configure(string path)
        {
            // Everything the player outputs to is set before Prepare: changing the target texture or the audio tracks of a
            // prepared player makes it prepare again, and a Play() issued meanwhile can be lost.
            _player.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _player.url = path;
            EnsureTexture(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            _player.targetTexture = _texture;
            _player.controlledAudioTrackCount = 1;
            _player.EnableAudioTrack(0, true);
            _player.SetTargetAudioSource(0, _audio);
            _picture.style.backgroundImage = Background.FromRenderTexture(_texture);
            _preparingSince = Time.unscaledTime;
            StartupTimeline.Mark($"video {_video} prepare");
            _player.Prepare();
        }

        /// <summary>Plays the video; <paramref name="finished"/> runs when it ends, is skipped or cannot be played.</summary>
        public void Play(string video, Action finished, VideoSkip skip = VideoSkip.Hold)
        {
            bool ahead = _preloaded != null && _preloaded == video && !IsPlaying;
            if (!ahead) Stop();
            _preloaded = null;
            string path = PathOf(video);
            if (path == null)
            {
                if (Reported.Add(video))
                    Debug.Log($"[Myrtana] video {video} skipped: no {Folder}/{video}.webm in StreamingAssets " +
                              "(convert Data/Video/" + video + ".bik, see Docs/game-start.md).");
                finished?.Invoke();
                return;
            }
            _finished = finished;
            _skip = skip;
            _playFrame = Time.frameCount; // a press in this frame belongs to what came before (it may have skipped the last logo)
            _started = false;
            // An unfocused editor or window must not stop the clip (Application.runInBackground; restored at Stop).
            if (!_forcedBackground && !Application.runInBackground)
            {
                Application.runInBackground = true;
                _forcedBackground = true;
            }
            _held = 0f;
            _hintUntil = 0f;
            _hint.style.display = DisplayStyle.None;
            _root.style.display = DisplayStyle.Flex;
            _video = video;
            _retries = 0;
            _lastFrame = -1;
            if (ahead)
            {
                _preparingSince = Time.unscaledTime; // the timeout counts from the request
                Debug.Log($"[Myrtana] video {video}: taken over from the start-up preload (prepared {_prepared})");
                return;                               // Update starts it once prepared (and the splash is gone)
            }
            _prepared = false;
            _picture.style.backgroundImage = StyleKeyword.None;
            Configure(path);
        }

        /// <summary>
        /// rebrand: a centred caption over the playing video for <paramref name="seconds"/> (the remaster's credits over
        /// the first credits video); gone with <see cref="Stop"/> or the time.
        /// </summary>
        public void Caption(string text, float seconds)
        {
            if (_root == null || string.IsNullOrEmpty(text)) return;
            if (_caption == null)
            {
                _caption = new Label { name = "caption", pickingMode = PickingMode.Ignore };
                _caption.style.position = Position.Absolute;
                _caption.style.left = 0;
                _caption.style.right = 0;
                _caption.style.top = Length.Percent(34);
                _caption.style.unityTextAlign = TextAnchor.MiddleCenter;
                _caption.style.color = new Color(0.85f, 0.8f, 0.65f);
                _caption.style.backgroundColor = new Color(0f, 0f, 0f, 0.82f);
                _caption.style.paddingTop = _caption.style.paddingBottom = 18;
                _caption.style.whiteSpace = WhiteSpace.Normal;
                G3Text.Font(_caption, 22);
                _root.Add(_caption);
            }
            _caption.text = text;
            _caption.style.display = DisplayStyle.Flex;
            _captionUntil = Time.unscaledTime + seconds;
        }

        /// <summary>Ends the video without calling back.</summary>
        public void Stop()
        {
            if (_caption != null) _caption.style.display = DisplayStyle.None;
            _finished = null;
            _preloaded = null;
            _prepared = false;
            if (_forcedBackground)
            {
                Application.runInBackground = false;
                _forcedBackground = false;
            }
            if (_player != null && (_player.isPlaying || _player.isPrepared)) _player.Stop();
            if (_root != null) _root.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// fix-137: black in front of everything while no video plays (the game being built after the logos: long
        /// main-thread frames, the title not up yet), never a camera's empty view. <see cref="Uncover"/> or the next video
        /// lifts it. G3's splash logo itself floats on the desktop in the launcher's window (Tools/Launcher, fix-152).
        /// </summary>
        public void Cover()
        {
            if (IsPlaying || _root == null) return;
            _root.style.display = DisplayStyle.Flex;
            _hint.style.display = DisplayStyle.None;
            if (_preloaded == null) _picture.style.backgroundImage = StyleKeyword.None; // not the last video's last frame
        }

        /// <summary>Lifts a <see cref="Cover"/> (no effect on a playing or preloading video).</summary>
        public void Uncover()
        {
            if (IsPlaying || _preloaded != null || _root == null) return;
            _root.style.display = DisplayStyle.None;
        }

        void OnPrepared(VideoPlayer player)
        {
            if (_finished == null && _preloaded == null) return;
            StartupTimeline.Mark($"video {_video} prepared");
            _prepared = true; // Update starts it: not under the Unity splash, and a preloaded one only once Play() asks
        }

        /// <summary>The Unity splash screen is gone (a player draws it over the first frames while scripts run).</summary>
        static bool SplashDone => Application.isEditor || SplashScreen.isFinished;

        void StartPlayback()
        {
            var player = _player;
            _aspect = player.width > 0 && player.height > 0 ? (float)player.width / player.height : 16f / 9f;
            Fit();
            player.Play();
            StartupTimeline.Mark($"video {_video} Play()");
            _started = true;
            _playedAt = Time.unscaledTime;
            Debug.Log($"[Myrtana] video {_video}: prepared {player.width}x{player.height}, {player.frameCount} frames, " +
                      $"audio tracks {player.audioTrackCount}; Play() -> isPlaying={player.isPlaying} (timeScale {Time.timeScale}, " +
                      $"listener pause {AudioListener.pause})");
        }

        float _aspect = 16f / 9f;

        /// <summary>The render target: screen-sized, the video is scaled into it and the picture keeps the video's aspect.</summary>
        void EnsureTexture(int w, int h)
        {
            if (_texture != null && _texture.width == w && _texture.height == h) return;
            if (_texture != null) _texture.Release();
            _texture = new RenderTexture(w, h, 0) { name = "G3 video" };
            _texture.Create();
            // A new render texture holds whatever the memory held: black until the first video frame arrives.
            var active = RenderTexture.active;
            RenderTexture.active = _texture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = active;
        }

        /// <summary>
        /// The first frame must arrive within <see cref="FirstFrameTimeout"/> s of Play() and a playing video must not stand
        /// still for <see cref="StallTimeout"/> s; otherwise the video is skipped with one log line, so the game never
        /// hangs behind a black video. The player runs on UnscaledGameTime with its sound through an AudioSource, the
        /// combination that plays in builds (G3VideoDiagnostics, editor-runner-11: DSPTime stays at frame 0, and in this
        /// editor no combination ever delivers a frame). A build gets one more Play() before the skip (a lost Play()).
        /// </summary>
        void Watchdog()
        {
            long frame = _player.frame;
            float now = Time.unscaledTime;
            if (frame != _lastFrame && frame > 0)
            {
                _lastFrame = frame;
                _playedAt = now;
                return;
            }
            bool firstFrame = _lastFrame <= 0;
            // fix-170: on the last frame the clip is over; the end event does not always come (editor), do not wait 3 s
            if (!firstFrame && VideoEnd.Reached(frame, _player.frameCount, now - _playedAt))
            {
                Finish();
                return;
            }
            float limit = firstFrame ? FirstFrameTimeout : StallTimeout;
            if (now - _playedAt < limit) return;
            if (firstFrame && !Application.isEditor && _retries == 0)
            {
                _retries = 1;
                _player.Pause();
                _player.Play();
                _playedAt = now;
                return;
            }
            Debug.LogWarning(firstFrame
                ? $"[Myrtana] video {_video} skipped: no frame {limit:0} s after Play() ({(Application.isEditor ? "editor" : "player")}, " +
                  $"isPlaying {_player.isPlaying}, isPaused {_player.isPaused}, mode {_player.timeUpdateMode}; G3VideoDiagnostics, Docs/game-start.md)"
                : $"[Myrtana] video {_video} skipped: stalled at frame {frame} for {limit:0} s");
            Finish();
        }

        /// <summary>The video's aspect ratio, as large as fits, centred.</summary>
        void Fit()
        {
            float sw = _root.resolvedStyle.width, sh = _root.resolvedStyle.height;
            if (float.IsNaN(sw) || sw <= 0) { sw = Screen.width; sh = Screen.height; }
            float aspect = _aspect;
            float w = sw, h = sw / aspect;
            if (h > sh) { h = sh; w = sh * aspect; }
            _picture.style.width = w;
            _picture.style.height = h;
            _picture.style.left = (sw - w) / 2;
            _picture.style.top = (sh - h) / 2;
        }

        void Update()
        {
            StartupTimeline.PollSplash();
            if (_caption != null && _caption.style.display.value == DisplayStyle.Flex && Time.unscaledTime > _captionUntil)
                _caption.style.display = DisplayStyle.None;
            if (_finished == null) return;
            if (!SplashDone) { _preparingSince = Time.unscaledTime; return; } // no skip, no start under the splash
            if (!_started && _prepared)
            {
                StartPlayback();
                _playFrame = Time.frameCount; // a press in the frame the picture appears does not skip it
                return;
            }
            if (_started)
            {
                Fit();
                Watchdog();
                if (_finished == null) return;
            }
            else if (Time.unscaledTime - _preparingSince > PrepareTimeout)
            {
                Debug.LogWarning($"[Myrtana] video {_video} skipped: not prepared after {PrepareTimeout:0} s");
                Finish();
                return;
            }
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            var mouse = Mouse.current;
            float now = Time.unscaledTime;
            // gamepad-1: A or B held = Space / Esc held, any pad button = a key press
            bool holding = kb != null && (kb.spaceKey.isPressed || kb.escapeKey.isPressed) ||
                           global::G3.Unity.G3Pad.Down(G3.Player.PadButton.A) || global::G3.Unity.G3Pad.Down(G3.Player.PadButton.B);
            bool pressed = (kb != null && kb.anyKey.wasPressedThisFrame) || (mouse != null && mouse.leftButton.wasPressedThisFrame) ||
                           global::G3.Unity.G3Pad.AnyPressed;
            if (_skip == VideoSkip.AnyPress)
            {
                // G3 start-up logos: one key press or click skips (fix-68); no hint needed.
                if (pressed && Time.frameCount != _playFrame) Finish();
                return;
            }
            if (pressed || holding) _hintUntil = now + HintTime;
            _held = holding ? _held + Time.unscaledDeltaTime : 0f;
            if (_held >= HoldToSkip) { Finish(); return; }
            bool show = holding || now < _hintUntil;
            _hint.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
            {
                _hintText.text = LanguagePreferences.Load().TextLanguage == "Russian"
                    ? "Удерживайте пробел, чтобы пропустить" : "Hold Space to skip";
                _fill.style.width = Length.Percent(100f * Mathf.Clamp01(_held / HoldToSkip));
            }
        }

        /// <summary>Bottom-centre hint with a bar that fills while the skip key is held.</summary>
        void BuildHint()
        {
            _hint = new VisualElement { name = "skip-hint", pickingMode = PickingMode.Ignore };
            _hint.style.position = Position.Absolute;
            _hint.style.bottom = 40;
            _hint.style.left = 0;
            _hint.style.right = 0;
            _hint.style.alignItems = Align.Center;
            _hint.style.display = DisplayStyle.None;
            _hintText = new Label { pickingMode = PickingMode.Ignore };
            _hintText.style.color = new Color(0.85f, 0.8f, 0.65f);
            G3Text.Font(_hintText, 18); // the port's own skip hint: regular, not the inherited theme bold
            _hintText.style.unityTextAlign = TextAnchor.MiddleCenter;
            _hint.Add(_hintText);
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.width = 240;
            bar.style.height = 6;
            bar.style.marginTop = 6;
            bar.style.backgroundColor = new Color(1f, 1f, 1f, 0.15f);
            _fill = new VisualElement { pickingMode = PickingMode.Ignore };
            _fill.style.height = Length.Percent(100);
            _fill.style.width = 0;
            _fill.style.backgroundColor = new Color(0.85f, 0.7f, 0.35f);
            bar.Add(_fill);
            _hint.Add(bar);
            _root.Add(_hint);
        }

        void Finish()
        {
            var finished = _finished;
            Stop();
            finished?.Invoke();
        }

        void OnDestroy()
        {
            if (_texture != null) _texture.Release();
        }
    }
}
