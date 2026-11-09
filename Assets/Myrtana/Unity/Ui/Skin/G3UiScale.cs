using System.Collections.Generic;
using Myrtana.Ui.Skin;
using UnityEngine;
using UnityEngine.UIElements;

namespace Myrtana.Unity.Ui.Skin
{
    /// <summary>
    /// Scale of every G3 panel (<see cref="UiScaleRules"/>): 100 % fits the 1024×768 GUI to the screen height; the
    /// player's GUI size (menu Options → Video) multiplies the HUD and the pages around that (pages never beyond the
    /// fit), full-screen 4:3 screens always fit. Windows keep their original anchors inside the wider panel. Re-applied
    /// when the screen size or the GUI size changes.
    /// </summary>
    public static class G3UiScale
    {
        static readonly List<(PanelSettings panel, GuiPanelKind kind)> Panels = new List<(PanelSettings, GuiPanelKind)>();
        static int _percent = 100;
        static int _appliedWidth = -1;
        static int _appliedHeight = -1;
        static int _appliedPercent = -1;

        /// <summary>GUI size in percent (the menu option PlayerOptions.UiScale; 100 = fit to the screen height).</summary>
        public static int Percent
        {
            get => _percent;
            set
            {
                _percent = value;
                Apply();
            }
        }

        /// <summary>Panel scale of a <paramref name="kind"/> panel for the current screen.</summary>
        public static float For(GuiPanelKind kind) => UiScaleRules.PanelScale(kind, _percent, Screen.width, Screen.height);

        internal static void Register(PanelSettings settings, GuiPanelKind kind)
        {
            Panels.Add((settings, kind));
            settings.scale = For(kind);
            Driver.Ensure();
        }

        /// <summary>Re-applies when the screen size or the percentage changed (the driver calls it every frame).</summary>
        static void Refresh()
        {
            if (Screen.width != _appliedWidth || Screen.height != _appliedHeight || _percent != _appliedPercent) Apply();
        }

        static void Apply()
        {
            _appliedWidth = Screen.width;
            _appliedHeight = Screen.height;
            _appliedPercent = _percent;
            Panels.RemoveAll(p => p.panel == null);
            foreach (var (panel, kind) in Panels) panel.scale = For(kind);
        }

        /// <summary>Entering Play without a domain reload keeps statics: forget the panels of the previous session.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnPlay()
        {
            Panels.Clear();
            _percent = 100;
            _appliedWidth = _appliedHeight = _appliedPercent = -1;
        }

        /// <summary>Follows resolution changes (window resize, Game view size) in Play.</summary>
        sealed class Driver : MonoBehaviour
        {
            static Driver _instance;

            public static void Ensure()
            {
                if (_instance != null || !Application.isPlaying) return;
                var go = new GameObject("G3 UI scale") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<Driver>();
            }

            void LateUpdate() => Refresh();
        }
    }
}
