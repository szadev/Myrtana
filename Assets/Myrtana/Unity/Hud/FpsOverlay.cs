using G3.Unity;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using Myrtana.Unity.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity
{
    /// <summary>
    /// The FPS overlay (fix-172, [owner]; port option "Show FPS" on the Video page, console <c>fps [on|off]</c>, Ctrl+F hides /
    /// shows it while armed, <see cref="FpsOverlayState"/>): four lines in the HUD's top-right corner, right-aligned in the
    /// Gothic3 face at 13 px (gold, G3's 1 px shadow), clear of the HUD (IDD_PAGE_MAIN_2: focus bar 411..612 × 10..29, the
    /// page sidebar from y 76, the bars and compass at the bottom):
    /// <list type="bullet">
    /// <item>FPS over 0.5 s windows (<see cref="FpsSmoother"/>);</item>
    /// <item>frame ms avg · 1 % low · max over the last 5 s (<see cref="FrameTimeWindow"/>);</item>
    /// <item>main-thread CPU and GPU ms (FrameTimingManager, mean of the 0.5 s window; "–" where the platform gives none);</item>
    /// <item>the remaster parts that are on (<see cref="PerfText.RemasterLine"/>).</item>
    /// </list>
    /// Cost: one ring-buffer write and FrameTimingManager.CaptureFrameTimings per frame while shown, the text (and the sort of
    /// the 5 s window) twice a second; the document is made on first show. Hidden: nothing but the Ctrl+F check while armed.
    /// </summary>
    public sealed class FpsOverlay : MonoBehaviour
    {
        /// <summary>Above the game menu (120): the figures stay readable in the options while changing them.</summary>
        public const int SortingOrder = 130;
        const float FontSize = 13f;

        static readonly FpsOverlayState State = new FpsOverlayState();
        static FpsOverlay _instance;

        /// <summary>The port option "Show FPS" (<see cref="PortOptionsApplier"/>).</summary>
        public static bool Option
        {
            get => State.Option;
            set => State.Option = value;
        }

        public static bool Visible => State.Visible;

        /// <summary>Console <c>fps on|off</c> (null toggles); returns a line for the console.</summary>
        public static string Command(bool? on)
        {
            bool shown = State.SetCommand(on);
            return shown ? "fps overlay on (Ctrl+F hides / shows)" : "fps overlay off" + (State.Option ? " (option on: Ctrl+F shows it)" : "");
        }

        readonly FpsSmoother _fps = new FpsSmoother(0.5f);
        readonly FrameTimeWindow _window = new FrameTimeWindow(5f);
        double _cpuSum, _gpuSum;
        int _cpuN, _gpuN;
        UIDocument _doc;
        Label _label;
        bool _shown;
        int _remasterKey = -1;
        string _remaster;

        void Awake() => _instance = this;

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        void Update()
        {
            if (State.Armed)
            {
                var kb = G3Input.GameKeyboard;   // null while the dev console holds the keyboard
                if (kb != null && kb.fKey.wasPressedThisFrame && kb.ctrlKey.isPressed) State.Hotkey();
            }
            bool visible = State.Visible;
            if (visible != _shown) Show(visible);
            if (!visible) return;

            float dt = Time.unscaledDeltaTime;
            // fix-251: a frame that carries a background pause (the window lost focus; runInBackground is off) is not a
            // freeze: it stays out of the max / 1 % low (it showed as "max 6872.8 ms"); G3HitchLog logs it as such
            if (!G3HitchLog.FrameSpansBackground) _window.Add(Time.realtimeSinceStartupAsDouble, dt * 1000f);
            if (LatestTiming(out float cpuMs, out float gpuMs))
            {
                if (cpuMs > 0f) { _cpuSum += cpuMs; _cpuN++; }
                if (gpuMs > 0f) { _gpuSum += gpuMs; _gpuN++; }
            }
            if (!_fps.Add(dt) || _label == null) return;
            float cpu = _cpuN > 0 ? (float)(_cpuSum / _cpuN) : -1f, gpu = _gpuN > 0 ? (float)(_gpuSum / _gpuN) : -1f;
            _cpuSum = _gpuSum = 0;
            _cpuN = _gpuN = 0;
            _label.text = PerfText.Overlay(_fps.Fps, _window.Compute(), cpu, gpu, RemasterText());
        }

        void Show(bool visible)
        {
            _shown = visible;
            if (visible && _doc == null) Build();
            if (_doc != null) _doc.rootVisualElement.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) _window.Clear();
            else if (_label != null && string.IsNullOrEmpty(_label.text)) _label.text = "… FPS";
        }

        void Build()
        {
            _doc = G3PanelSettings.CreateDocument("FPS overlay", GuiPanelKind.Hud, transform, SortingOrder);
            _label = new Label { name = "FpsOverlay", pickingMode = PickingMode.Ignore };
            var s = _label.style;
            s.position = Position.Absolute;
            s.right = 8;
            s.top = 4;
            s.unityTextAlign = TextAnchor.UpperRight;
            s.whiteSpace = WhiteSpace.NoWrap;
            s.paddingLeft = s.paddingRight = s.paddingTop = s.paddingBottom = 0;
            s.marginLeft = s.marginRight = s.marginTop = s.marginBottom = 0;
            G3Text.Font(_label, FontSize);
            G3Text.Color(_label, G3Palette.Gold);
            G3Text.Shadow(_label);
            _doc.rootVisualElement.Add(_label);
        }

        /// <summary>The remaster line, rebuilt only when a part changes.</summary>
        string RemasterText()
        {
            var parts = G3WorldRemaster.Parts;
            bool ch = G3CharacterRemaster.Enabled, sn = G3SnowV2.Active, rl = G3TerrainRelief.Active, mo = G3WorldGi.ModernActive;
            int key = (int)parts | (ch ? 1 << 8 : 0) | (sn ? 1 << 9 : 0) | (rl ? 1 << 10 : 0) | (mo ? 1 << 11 : 0);
            if (key == _remasterKey) return _remaster;
            _remasterKey = key;
            _remaster = RemasterLine();
            return _remaster;
        }

        static readonly FrameTiming[] Timing = new FrameTiming[1];
        static int _timingFrame = -1;
        static bool _timingOk;
        static float _cpuMs, _gpuMs;

        /// <summary>
        /// The newest FrameTimingManager figures (main-thread CPU and GPU ms of a frame a few frames back; ≤ 0 = none),
        /// captured once per frame for every reader (the overlay and the perf report; the manager wants one capture a frame).
        /// Needs Player Settings "Frame Timing Stats" (on); false where the platform gives none.
        /// </summary>
        public static bool LatestTiming(out float cpuMs, out float gpuMs)
        {
            int frame = Time.frameCount;
            if (frame != _timingFrame)
            {
                _timingFrame = frame;
                FrameTimingManager.CaptureFrameTimings();
                _timingOk = FrameTimingManager.GetLatestTimings(1, Timing) > 0;
                _cpuMs = _timingOk ? (float)Timing[0].cpuMainThreadFrameTime : -1f;
                _gpuMs = _timingOk ? (float)Timing[0].gpuFrameTime : -1f;
            }
            cpuMs = _cpuMs;
            gpuMs = _gpuMs;
            return _timingOk;
        }

        /// <summary>The parts that are on now, compact (also the perf report's).</summary>
        public static string RemasterLine()
        {
            var p = G3WorldRemaster.Parts;
            return PerfText.RemasterLine((p & WorldParts.Lighting) != 0, (p & WorldParts.Terrain) != 0, (p & WorldParts.Grass) != 0,
                (p & WorldParts.Rocks) != 0, (p & WorldParts.Buildings) != 0, (p & WorldParts.Trees) != 0,
                G3CharacterRemaster.Enabled, G3SnowV2.Active, G3TerrainRelief.Active, G3WorldGi.ModernActive);
        }
    }
}
