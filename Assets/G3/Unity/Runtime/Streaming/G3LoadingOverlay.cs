using System;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// What the loading screen shows. The G3 look (gui.pak background, slides and bar; Docs/formats-gui.md §6.12) lives
    /// in the game's UI layer and is plugged in through <see cref="G3LoadingOverlay.ViewFactory"/>.
    /// </summary>
    public interface IG3LoadingScreenView
    {
        /// <summary>Shows the screen with a new slide order and an empty bar.</summary>
        void Begin();
        /// <summary>0..1; never decreases between <see cref="Begin"/> and <see cref="End"/>.</summary>
        void SetProgress(float progress);
        void SetOpacity(float alpha);
        /// <summary>Hides the screen (<see cref="Begin"/> shows it again).</summary>
        void End();
    }

    /// <summary>
    /// The loading screen as a view over the loading state: the start-up wait of <see cref="G3StartupLoader"/> on the
    /// same object, or an area wait after a load-game or teleport (<see cref="ShowUntilReady"/>) that lasts until the
    /// streamer has the cells under the hero (<see cref="G3WorldStreamer.IsReady"/>). It never holds or releases the
    /// player itself. Like the original menu it opens and closes at once (fades are optional) and its progress only
    /// grows. The world is still rendered underneath, so shader variants / pipeline states used at the start position
    /// are created while covered. Without a registered view (GUI not imported) it draws a black screen with a bar.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Loading Overlay")]
    public sealed class G3LoadingOverlay : MonoBehaviour
    {
        /// <summary>Creates the skinned view; null (or a null result) keeps the placeholder.</summary>
        public static Func<IG3LoadingScreenView> ViewFactory { get; set; }

        [Tooltip("Fade-in when a load starts during play, seconds (the original shows the screen at once).")]
        public float fadeInSeconds = 0f;
        [Tooltip("Fade-out when the area is ready, seconds (the original hides the screen at once).")]
        public float fadeOutSeconds = 0f;
        [Header("Placeholder (no G3 GUI import)")]
        public Color background = Color.black;
        public Color barBack = new Color(0.25f, 0.22f, 0.18f);
        public Color barFill = new Color(0.78f, 0.66f, 0.42f);

        enum Phase { Hidden, FadingIn, Shown, FadingOut }

        G3StartupLoader _startup;
        G3WorldStreamer _streamer;
        IG3LoadingScreenView _view;
        bool _viewTried, _startupPending, _startupSeen, _waitingForArea;
        Vector3 _areaPosition;
        Phase _phase = Phase.Hidden;
        float _alpha, _progress;

        public bool IsVisible => _phase != Phase.Hidden;

        /// <summary>Shows the loading screen until the cells under <paramref name="position"/> are loaded (load-game,
        /// teleport). The caller holds the hero meanwhile, as before.</summary>
        public static G3LoadingOverlay ShowUntilReady(G3WorldStreamer streamer, Vector3 position)
        {
            var overlay = streamer.GetComponent<G3LoadingOverlay>();
            if (overlay == null) overlay = streamer.gameObject.AddComponent<G3LoadingOverlay>();
            overlay._areaPosition = position;
            overlay._waitingForArea = true;
            if (overlay._phase == Phase.Hidden || overlay._phase == Phase.FadingOut) overlay.Open(overlay.fadeInSeconds <= 0f);
            return overlay;
        }

        void Awake()
        {
            _streamer = GetComponent<G3WorldStreamer>();
            _startup = GetComponent<G3StartupLoader>();
            // Added by the streamer next to a loader that starts this frame: cover the very first frame already.
            _startupPending = _startup != null && _startup.enabled;
            if (_startupPending) Open(instant: true);
        }

        void Open(bool instant)
        {
            bool fresh = _phase == Phase.Hidden;
            _phase = instant ? Phase.Shown : Phase.FadingIn;
            if (instant) _alpha = 1f;
            enabled = true;
            if (!fresh) return;
            _progress = 0f;
            if (!_viewTried) { _viewTried = true; _view = ViewFactory?.Invoke(); }
            _view?.Begin();
            Push();
        }

        void Update()
        {
            if (_phase == Phase.Hidden) { enabled = false; return; }
            bool loading = false;
            float progress = 1f;
            if (_startupPending)
            {
                if (_startup == null || !_startup.isActiveAndEnabled || (_startupSeen && !_startup.IsLoading)) _startupPending = false;
                else
                {
                    _startupSeen |= _startup.IsLoading;
                    loading = true;
                    progress = _startup.Progress;
                }
            }
            if (_waitingForArea)
            {
                if (_streamer == null || _streamer.IsReady(_areaPosition)) _waitingForArea = false;
                else
                {
                    loading = true;
                    progress = Mathf.Min(progress, AreaFraction(_areaPosition));
                }
            }

            float dt = Time.unscaledDeltaTime;
            if (loading)
            {
                _progress = Mathf.Max(_progress, Mathf.Clamp01(progress));
                if (_phase == Phase.FadingOut) _phase = Phase.FadingIn;
                if (_phase == Phase.FadingIn)
                {
                    _alpha += dt / Mathf.Max(0.01f, fadeInSeconds);
                    if (_alpha >= 1f) { _alpha = 1f; _phase = Phase.Shown; }
                }
            }
            else
            {
                _progress = 1f;
                if (fadeOutSeconds <= 0f) { Close(); return; }
                _phase = Phase.FadingOut;
                _alpha -= dt / fadeOutSeconds;
                if (_alpha <= 0f) { Close(); return; }
            }
            Push();
        }

        void Close()
        {
            _phase = Phase.Hidden;
            _alpha = 0f;
            _startupSeen = false;
            _view?.End();
            enabled = false;
        }

        void Push()
        {
            if (_view == null) return;
            _view.SetProgress(_progress);
            _view.SetOpacity(_alpha);
        }

        /// <summary>Loaded share of the cells <see cref="G3WorldStreamer.IsReady"/> waits for: the cell under the
        /// position and every cell whose terrain covers it.</summary>
        float AreaFraction(Vector3 position)
        {
            var manifest = _streamer.manifest;
            if (manifest == null) return 1f;
            var c = manifest.CellOf(position);
            int i = manifest.Index(c.x, c.y);
            if (i < 0 || i >= manifest.cells.Count) return 1f;
            int loaded = 0, total = 0;
            Count(i);
            var coverers = manifest.cells[i].coverers;
            if (coverers != null)
                foreach (int k in coverers)
                    if (k != i && k >= 0 && k < manifest.cells.Count) Count(k);
            return total > 0 ? (float)loaded / total : 1f;

            void Count(int cell)
            {
                if (!manifest.cells[cell].HasScene) return;
                total++;
                if (_streamer.IsLoaded(cell)) loaded++;
            }
        }

        void OnDestroy()
        {
            if (_phase != Phase.Hidden) _view?.End();
        }

        void OnGUI()
        {
            if (_view != null || _phase == Phase.Hidden || Event.current.type != EventType.Repaint) return;
            GUI.depth = -1000;
            float w = Screen.width, h = Screen.height;
            Fill(new Rect(0, 0, w, h), background);
            var bar = new Rect(w * 0.3f, h * 0.88f, w * 0.4f, Mathf.Max(4f, h * 0.008f));
            Fill(bar, barBack);
            Fill(new Rect(bar.x, bar.y, bar.width * _progress, bar.height), barFill);
        }

        void Fill(Rect r, Color c)
        {
            c.a *= _alpha;
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
        }
    }
}
