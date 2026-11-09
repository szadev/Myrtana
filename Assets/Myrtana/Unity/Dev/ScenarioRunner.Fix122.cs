#if UNITY_EDITOR || DEVELOPMENT_BUILD || G3_DEMO
using G3.Unity;
using UnityEngine;

namespace Myrtana.Unity.Dev
{
    /// <summary>
    /// fix-122 ops and conditions (Docs/dev-scenarios.md): the [owner] option "Camera in windows" for one scenario, and the
    /// camera's distance / FOV / pose against a mark taken before a page or a use.
    /// </summary>
    public sealed partial class ScenarioRunner
    {
        bool? _windowCamBefore; // fix-122: G3PlayerCamera.OriginalWindowCamera before a scenario's 'windowcam'
        bool _camMarked;
        float _markDist, _markFov, _markZoom;
        Vector3 _markOffset; // camera position relative to the hero's feet
        Quaternion _markRot;

        /// <summary>'windowcam mode=still|original': "Camera in windows" for this scenario only (still = the default, no zoom).</summary>
        internal void SetWindowCam(string mode)
        {
            bool original = (mode ?? "still").ToLowerInvariant() == "original";
            _windowCamBefore ??= G3PlayerCamera.OriginalWindowCamera;
            G3PlayerCamera.OriginalWindowCamera = original;
            Log($"windowcam {(original ? "original (G3 inventory zoom)" : "as in play (no zoom)")}");
        }

        void RestoreWindowCam()
        {
            if (_windowCamBefore == null) return;
            G3PlayerCamera.OriginalWindowCamera = _windowCamBefore.Value;
            _windowCamBefore = null;
        }

        /// <summary>'cammark': remembers the hero camera's boom length, FOV, zoom and pose (relative to the hero) now.</summary>
        internal void CamMark()
        {
            var c = FindAnyObjectByType<G3PlayerCamera>();
            if (c == null || Hero == null) { Fail("cammark: no hero camera"); return; }
            var cam = c.GetComponent<Camera>();
            _markDist = c.CurrentDistance;
            _markFov = cam != null ? cam.fieldOfView : 0f;
            _markZoom = c.distance;
            _markOffset = c.transform.position - Hero.transform.position;
            _markRot = c.transform.rotation;
            _camMarked = true;
            Log($"cammark dist {F2(_markDist)} fov {F2(_markFov)} zoom {F2(_markZoom)} mode {c.mode}");
        }

        /// <summary>
        /// Condition 'camchange:dist|fov|zoom|pos|rot': how far the hero camera is from the last 'cammark' (m, degrees of FOV,
        /// m of wished zoom, m of position relative to the hero, degrees of rotation); "-" without a mark.
        /// </summary>
        string CamChange(string what)
        {
            var c = FindAnyObjectByType<G3PlayerCamera>();
            if (!_camMarked || c == null || Hero == null) return "-";
            var cam = c.GetComponent<Camera>();
            switch ((what ?? "dist").ToLowerInvariant())
            {
                case "fov": return F2(Mathf.Abs((cam != null ? cam.fieldOfView : 0f) - _markFov));
                case "zoom": return F2(Mathf.Abs(c.distance - _markZoom));
                case "pos": return F2(((c.transform.position - Hero.transform.position) - _markOffset).magnitude);
                case "rot": return F2(Quaternion.Angle(c.transform.rotation, _markRot));
                default: return F2(Mathf.Abs(c.CurrentDistance - _markDist));
            }
        }
    }
}
#endif
