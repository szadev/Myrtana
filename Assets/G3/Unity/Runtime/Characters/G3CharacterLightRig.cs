using System;
using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Character lighting for close-ups (Docs/remaster-pipeline.md §9.4): a soft warm fill from the camera side and a cool
    /// rim from behind the subject, both spot lights that only reach renderers on rendering layer
    /// <see cref="RenderingLayer"/> ("Light Layer 7", unused elsewhere): while the rig is on, the subjects' renderers get
    /// that layer added, so the world around them is not lit. Works with the original and the remaster materials alike
    /// (both honour light layers in URP Forward+). No shadows (cost). <b>Off by default</b>: <see cref="Enabled"/>
    /// (env <c>G3_CHARACTER_LIGHT_RIG=1/0</c> wins over the saved option).
    /// Usage: <see cref="Begin"/> with the subjects, <see cref="Place"/> every frame after the camera moved, <see cref="End"/>.
    /// </summary>
    public sealed class G3CharacterLightRig
    {
        public const int RenderingLayer = 7;
        public const uint LayerBit = 1u << RenderingLayer;
        const string PrefKey = "G3.CharacterLightRig";
        const string EnvVar = "G3_CHARACTER_LIGHT_RIG";

        /// <summary>The option (default off).</summary>
        public static bool Enabled
        {
            get
            {
                string env = Environment.GetEnvironmentVariable(EnvVar);
                if (env == "1") return true;
                if (env == "0") return false;
                return PlayerPrefs.GetInt(PrefKey, 0) == 1;
            }
            set => PlayerPrefs.SetInt(PrefKey, value ? 1 : 0);
        }

        public Color FillColor = new Color(1f, 0.93f, 0.85f);
        public float FillIntensity = 0.8f;
        public Color RimColor = new Color(0.78f, 0.86f, 1f);
        public float RimIntensity = 3.0f;
        /// <summary>Reach of both lights, m: enough for a dialogue pair, too short to matter for anything else.</summary>
        public float Range = 6f;

        /// <summary>
        /// v2 hero showcase: fill and rim follow the scene's own sun and ambient light (<see cref="CharacterLightTuning"/>)
        /// instead of the fixed values above (tuned on the test scene: faces washed out at night, lost under a noon sun).
        /// Off = the fixed <see cref="FillIntensity"/>/<see cref="RimIntensity"/>/colours (env <c>G3_CHARACTER_LIGHT_AUTOTUNE=0</c>).
        /// </summary>
        public bool AutoTune = Environment.GetEnvironmentVariable("G3_CHARACTER_LIGHT_AUTOTUNE") != "0";

        /// <summary>The values the last <see cref="Place"/> used (for the test scene / logs).</summary>
        public CharacterLightTuning.Result LastTuning { get; private set; }

        readonly Camera _camera;
        Light _fill, _rim;
        GameObject _root;
        readonly List<(Renderer renderer, uint mask)> _touched = new List<(Renderer, uint)>();
        Transform _focus;
        bool _focusIsHead;

        public G3CharacterLightRig(Camera camera) => _camera = camera;

        public bool IsActive => _root != null && _root.activeSelf;

        /// <summary>Turns the rig on for <paramref name="subjects"/> (character roots); the first is the initial focus.</summary>
        public void Begin(params Transform[] subjects)
        {
            if (_camera == null) return;
            End();
            if (_root == null)
            {
                _root = new GameObject("G3CharacterLightRig") { hideFlags = HideFlags.DontSave };
                _fill = MakeLight("Fill", FillColor, FillIntensity, 70f);
                _rim = MakeLight("Rim", RimColor, RimIntensity, 45f);
            }
            _root.SetActive(true);
            foreach (var s in subjects) AddSubject(s);
            Focus(subjects.Length > 0 ? subjects[0] : null);
        }

        /// <summary>Adds a character (its renderers get the rig's light layer).</summary>
        public void AddSubject(Transform subject)
        {
            if (subject == null) return;
            foreach (var r in subject.GetComponentsInChildren<Renderer>(true))
            {
                if ((r.renderingLayerMask & LayerBit) != 0) continue;
                _touched.Add((r, r.renderingLayerMask));
                r.renderingLayerMask |= LayerBit;
            }
        }

        /// <summary>The character the lights aim at (its head bone when found).</summary>
        public void Focus(Transform subject)
        {
            _focus = subject != null ? Head(subject) : null;
            _focusIsHead = _focus != null && _focus != subject;
        }

        /// <summary>Places the lights for the current camera and focus; call after the camera moved.</summary>
        public void Place()
        {
            if (!IsActive || _focus == null) return;
            var cam = _camera.transform;
            Vector3 target = _focus.position + (_focusIsHead ? Vector3.zero : Vector3.up * 1.7f);
            Vector3 toCam = cam.position - target;
            toCam.y = 0;
            if (toCam.sqrMagnitude < 1e-4f) toCam = -_focus.forward;
            toCam.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, toCam);

            // Fill: beside the camera, a little above eye level, wide and soft.
            Vector3 fillPos = target + toCam * 1.6f + side * 0.9f + Vector3.up * 0.35f;
            _fill.transform.SetPositionAndRotation(fillPos, Quaternion.LookRotation(target - fillPos));
            // Rim: behind the subject, high and to the other side, grazing the silhouette.
            Vector3 rimPos = target - toCam * 1.4f - side * 0.7f + Vector3.up * 0.9f;
            _rim.transform.SetPositionAndRotation(rimPos, Quaternion.LookRotation(target - rimPos));
            if (AutoTune)
            {
                var t = Tune(target, cam.position);
                LastTuning = t;
                _fill.color = new Color(t.FillColor[0], t.FillColor[1], t.FillColor[2]); _fill.intensity = t.FillIntensity;
                _rim.color = new Color(t.RimColor[0], t.RimColor[1], t.RimColor[2]); _rim.intensity = t.RimIntensity;
            }
            else
            {
                _fill.color = FillColor; _fill.intensity = FillIntensity;
                _rim.color = RimColor; _rim.intensity = RimIntensity;
            }
            _fill.range = _rim.range = Range;
        }

        /// <summary>Scene light at the subject: the sun (RenderSettings.sun), the ambient probe, and how much the sun shines
        /// from behind the subject towards the camera.</summary>
        static CharacterLightTuning.Result Tune(Vector3 target, Vector3 camPos)
        {
            var sun = RenderSettings.sun;
            float sunI = 0f, elev = 0f, backlit = 0f;
            var sunCol = new[] { 1f, 0.95f, 0.88f };
            if (sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional)
            {
                Vector3 toSun = -sun.transform.forward;
                sunI = sun.intensity;
                elev = Mathf.Asin(Mathf.Clamp(toSun.y, -1f, 1f)) * Mathf.Rad2Deg;
                var c = sun.color.linear;
                sunCol = new[] { c.r, c.g, c.b };
                Vector3 toCam = camPos - target;
                toCam.y = 0;
                Vector3 sunFlat = new Vector3(toSun.x, 0, toSun.z);
                if (toCam.sqrMagnitude > 1e-4f && sunFlat.sqrMagnitude > 1e-4f)
                    backlit = Mathf.Clamp01(-Vector3.Dot(toCam.normalized, sunFlat.normalized));
            }
            var probe = RenderSettings.ambientProbe;
            var dirs = new[] { Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
            var res = new Color[dirs.Length];
            probe.Evaluate(dirs, res);
            Color amb = Color.black;
            foreach (var r in res) amb += r;
            amb /= dirs.Length;
            float ambLum = 0.2126f * amb.r + 0.7152f * amb.g + 0.0722f * amb.b;
            return CharacterLightTuning.Compute(sunI, elev, sunCol, ambLum, new[] { res[0].r, res[0].g, res[0].b }, backlit);
        }

        /// <summary>Turns the lights off and gives the renderers their layer masks back.</summary>
        public void End()
        {
            foreach (var (r, mask) in _touched) if (r != null) r.renderingLayerMask = mask;
            _touched.Clear();
            if (_root != null) _root.SetActive(false);
            _focus = null;
        }

        /// <summary>Destroys the light objects.</summary>
        public void Dispose()
        {
            End();
            if (_root == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(_root);
            else UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
        }

        Light MakeLight(string name, Color color, float intensity, float angle)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(_root.transform, false);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.color = color;
            l.intensity = intensity;
            l.range = Range;
            l.spotAngle = angle;
            l.innerSpotAngle = angle * 0.5f;
            l.shadows = LightShadows.None;
            l.renderingLayerMask = (int)LayerBit; // URP light layers: only renderers with this bit
            l.renderMode = LightRenderMode.ForcePixel;
            return l;
        }

        /// <summary>The skeleton's head bone (<c>&lt;Rig&gt;_Head_Head_1</c>), else the root (aimed at 1.7 m above it).</summary>
        static Transform Head(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>())
                if (t.name.EndsWith("_Head_Head_1", StringComparison.Ordinal)) return t;
            return root;
        }
    }
}
