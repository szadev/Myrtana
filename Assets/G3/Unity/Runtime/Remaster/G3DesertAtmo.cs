using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// atmo-1 (Docs/remaster-world.md section 23): the desert atmosphere and light over Varant, part of the Lighting world
    /// part (json <c>atmo</c>, env <c>G3_REMASTER_ATMO=0</c> or <see cref="SetOption"/> for A/B). Region-aware (the region
    /// map's desert weight, per pixel in the haze; at the camera for the light and the grade), on top of G3's day cycle and
    /// weather (sun elevation, Sandstormy share, overcast) and of the v2 look (<see cref="G3WorldV2"/> calls
    /// <see cref="Apply"/> after it subscribed, so this runs after v2 on <see cref="G3Environment.Applied"/>):
    /// <list type="bullet">
    /// <item>Haze: a dust medium (exponential layer + a dense near-ground layer, Mie forward glow, warm tint) and a
    /// sandstorm medium (swirling layers advected by the G3 wind) in the v2 volumetric pass (shafts through the shadow map
    /// come with it), Rayleigh blue over kilometres, on the sky too (horizon band); G3's linear fog pushed out and warmed.</item>
    /// <item>Light: stronger, dust-warmed sun; G3's flat warm ambient partly replaced by blue sky light from above and warm
    /// sand bounce from below (SH); longer shadow distance in the open desert; the sun dimmed in a storm.</item>
    /// <item>Sky: dust aureole round the sun, a hotter disc, fewer clouds (G3/Sky).</item>
    /// <item>Grade: filmic curve with a toe (Lottes; replaces the v2 shoulder), split tone, golden hour, noon key.</item>
    /// <item>Motes: sunlit dust round the camera (storm: flying grains), <see cref="MotesPass"/>.</item>
    /// </list>
    /// Maths: <see cref="DesertAtmo"/> (tested). Off = every global zero, G3/v2 values untouched.
    /// </summary>
    public static class G3DesertAtmo
    {
        public const string EnvVar = "G3_REMASTER_ATMO";

        static readonly int DustId = Shader.PropertyToID("_G3AtmoDust");
        static readonly int StormId = Shader.PropertyToID("_G3AtmoStorm");
        static readonly int HighId = Shader.PropertyToID("_G3AtmoHigh");
        static readonly int TintId = Shader.PropertyToID("_G3AtmoTint");
        static readonly int AmbId = Shader.PropertyToID("_G3AtmoAmb");
        static readonly int StormColId = Shader.PropertyToID("_G3AtmoStormCol");
        static readonly int RayId = Shader.PropertyToID("_G3AtmoRay");
        static readonly int OnId = Shader.PropertyToID("_G3AtmoOn");
        static readonly int GradeId = Shader.PropertyToID("_G3AtmoGrade");
        static readonly int Grade2Id = Shader.PropertyToID("_G3AtmoGrade2");
        static readonly int ShadowTintId = Shader.PropertyToID("_G3AtmoShadowTint");
        static readonly int HighTintId = Shader.PropertyToID("_G3AtmoHighTint");
        static readonly int SkyId = Shader.PropertyToID("_G3AtmoSky");
        static readonly int SkyPBId = Shader.PropertyToID("_G3AtmoSkyPB");
        static readonly int MotesId = Shader.PropertyToID("_G3AtmoMotes");
        static readonly int Motes2Id = Shader.PropertyToID("_G3AtmoMotes2");
        static readonly int Motes3Id = Shader.PropertyToID("_G3AtmoMotes3");
        static readonly int WindDirId = Shader.PropertyToID("_G3WindDirection");
        static readonly int RaysId = Shader.PropertyToID("_G3AtmoRays");
        static readonly int RaysSunId = Shader.PropertyToID("_G3AtmoRaysSun");
        static readonly int RaysColId = Shader.PropertyToID("_G3AtmoRaysCol");
        static readonly int RaysDirId = Shader.PropertyToID("_G3AtmoRaysDir");
        static readonly int RaysTexId = Shader.PropertyToID("_G3AtmoRaysTex");
        static readonly int SkyFogColorId = Shader.PropertyToID("_FogColor");
        static readonly int SkyGradeId = Shader.PropertyToID("_G3SkyGrade");
        static readonly int CapId = Shader.PropertyToID("_G3AtmoCap");

        static bool? _option;
        static bool _active, _hooked;
        static float _w, _storm, _el, _desert, _near;
        static float _nearLookup;
        static int _motes;
        static float _shadowSet = -1;
        static Material _motesMat, _raysMat;
        static MotesPass _motesPass;
        static RaysPass _raysPass;
        static float _rays;
        static readonly Vector3[] _dirs = { Vector3.up, new Vector3(0.7071f, 0.7071f, 0), new Vector3(-0.7071f, 0.7071f, 0), new Vector3(0, 0.7071f, 0.7071f), new Vector3(0, 0.7071f, -0.7071f) };
        static readonly Color[] _shOut = new Color[5];

        /// <summary>Env G3_REMASTER_ATMO (0 = off for A/B shots); default on (it follows the Lighting part).</summary>
        public static bool Option
        {
            get
            {
                if (_option == null) _option = RemasterOptions.Resolve(new[] { Environment.GetEnvironmentVariable(EnvVar) }, true);
                return _option.Value;
            }
        }

        /// <summary>True while the desert atmosphere is applied (option, json atmo, Lighting part, play mode).</summary>
        public static bool Active => _active;

        /// <summary>Runner / tools: forces the desert atmosphere on or off (not stored) and re-applies.</summary>
        public static string SetOption(bool on)
        {
            _option = on;
            Apply(G3WorldV2.LightingOn);
            return Describe();
        }

        /// <summary>One line for the runner.</summary>
        public static string Describe()
        {
            if (!_active) return Option ? "atmo off (needs the Lighting part, json atmo)" : "atmo off (G3_REMASTER_ATMO)";
            return $"atmo on: desert {_desert:0.00} (w {_w:0.00}, near {_near:0.00}), storm {_storm:0.00}, sun {_el:0.0} deg, motes {_motes}, shadows {(_shadowSet > 0 ? _shadowSet.ToString("0") + " m" : "v2")}";
        }

        /// <summary>
        /// Called by <see cref="G3WorldV2.Apply(WorldParts)"/> with the Lighting part's state, after v2 subscribed to
        /// <see cref="G3Environment.Applied"/> (re-subscribes every time so this handler stays after v2's).
        /// </summary>
        public static void Apply(bool lighting)
        {
            bool on = lighting && Option && DesertAtmo.Enabled && Application.isPlaying;
            G3Environment.Applied -= OnEnvironment;
            if (on) G3Environment.Applied += OnEnvironment;
            if (on && !_hooked) { RenderPipelineManager.beginCameraRendering += OnBeginCamera; _hooked = true; }
            if (!on && _hooked) { RenderPipelineManager.beginCameraRendering -= OnBeginCamera; _hooked = false; }
            if (on == _active) return;
            _active = on;
            if (!on)
            {
                RestoreShadows();
                ClearGlobals();
                _w = _storm = _desert = _near = 0;
                _motes = 0;
            }
        }

        static void ClearGlobals()
        {
            foreach (var id in new[] { DustId, StormId, HighId, SkyPBId, TintId, AmbId, StormColId, RayId, OnId, GradeId, Grade2Id, SkyId, MotesId, Motes2Id, Motes3Id, SkyGradeId, CapId })
                Shader.SetGlobalVector(id, Vector4.zero);
            Shader.SetGlobalVector(ShadowTintId, Vector4.one);
            Shader.SetGlobalVector(HighTintId, Vector4.one);
        }

        static Vector4 V(float[] a) => new Vector4(a[0], a[1], a[2], a.Length > 3 ? a[3] : 0);

        // ------------------------------------------------------------------ per frame, after G3Environment + v2

        static void OnEnvironment(G3Environment env, G3WeatherSample s, float overcast)
        {
            if (!_active || !Application.isPlaying) return;
            var c = WorldRemasterLook.Current;
            var cam = Camera.main;
            var eye = cam != null ? cam.transform.position : env.transform.position;

            // weights: the region map at the eye (geography), the weather mix without it
            bool map = G3SnowV2.RegionMapReady;
            // grade-1: the undilated region (a 250 m ramp at the edge): the green border cells (Lago) are not full desert
            _desert = map ? G3SnowV2.DesertCoreAt(eye.x, eye.z) : WorldV2.DesertShare(env.StateMix);
            var mix = env.StateMix;
            _storm = mix != null && mix.Count > 0 ? SandV2.StormShare(mix) : SandV2.StormShare(env.weatherState, env.blendState, env.blend);
            _w = DesertAtmo.Weight(_desert);
            if (Time.realtimeSinceStartup >= _nearLookup)
            {
                // any desert within ~4 km (the haze over Varant seen from its border)
                _nearLookup = Time.realtimeSinceStartup + 0.5f;
                float n = _desert;
                if (map)
                    for (int i = 0; i < 8 && n < 0.01f; i++)
                    {
                        float a = i * Mathf.PI / 4;
                        n = Mathf.Max(n, G3SnowV2.DesertAt(eye.x + Mathf.Cos(a) * 4000, eye.z + Mathf.Sin(a) * 4000), G3SnowV2.DesertAt(eye.x + Mathf.Cos(a) * 1500, eye.z + Mathf.Sin(a) * 1500));
                    }
                _near = n;
            }
            var sun = env.sun;
            // the sun's elevation (G3's light is the moon at night: "below the horizon" then, so no daylight terms)
            _el = sun != null && env.IsDay ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1, 1)) * Mathf.Rad2Deg : -20;
            float storm = _storm * Mathf.Clamp01(_desert * 2);   // a Varant storm state outside the desert map: none

            if (_w > 0)
            {
                // --- sun: stronger in clear desert daylight, warmed by the dust, dimmed in a storm
                if (sun != null)
                {
                    sun.intensity *= DesertAtmo.SunGain(_w, _el, overcast, storm);
                    var t = DesertAtmo.SunTint(_w, _el);
                    var col = sun.color;   // gamma colour (Unity linearizes): tint applied in gamma ~ t^(1/2.2)
                    sun.color = new Color(col.r * Mathf.Pow(t[0], 0.4545f), col.g * Mathf.Pow(t[1], 0.4545f), col.b * Mathf.Pow(t[2], 0.4545f), 1);
                    sun.shadowStrength = Mathf.Lerp(sun.shadowStrength, sun.shadowStrength * 0.55f, Mathf.Clamp01(storm * _w));
                }
                // --- ambient: flat warm fill -> blue sky from above + warm sand bounce from below
                if (env.applyAmbient)
                {
                    var (cut, skyK, bounceK) = DesertAtmo.Ambient(_w, _el, overcast, storm);
                    if (cut > 0 || skyK > 0 || bounceK > 0)
                    {
                        var sh = RenderSettings.ambientProbe;
                        float dc = (sh[0, 0] + sh[1, 0] + sh[2, 0]) / 3f;
                        sh *= 1 - cut;
                        var sky = ((Color)s.skyColor).linear;
                        var hazeC = ((Color)s.hazeColor).linear;
                        var skyCol = Color.Lerp(sky, hazeC, 0.35f);
                        float sl = Mathf.Max(1e-4f, 0.2126f * skyCol.r + 0.7152f * skyCol.g + 0.0722f * skyCol.b);
                        sh.AddDirectionalLight(Vector3.up, new Color(skyCol.r / sl, skyCol.g / sl, skyCol.b / sl), dc * skyK * 2f);
                        if (sun != null)
                        {
                            var sc = sun.color.linear;
                            float sinEl = Mathf.Max(0, Mathf.Sin(_el * Mathf.Deg2Rad));
                            // sunlit sand under the sun: albedo (linear, G3's Varant sand) x irradiance; Lambert out /pi folded into the gain
                            var sand = new Color(0.5f, 0.31f, 0.16f);
                            float e = sun.intensity * sinEl * bounceK * 0.35f;
                            sh.AddDirectionalLight(Vector3.down, new Color(sand.r * sc.r, sand.g * sc.g, sand.b * sc.b), e);
                        }
                        RenderSettings.ambientProbe = sh;
                    }
                }
            }

            // --- G3's linear fog: pushed out (the dust takes over the distance), warmer
            // night: G3's own night (the clear-air haze, the fog push and the grade fade out with the daylight; a storm stays)
            float dayW = DesertAtmo.Day(_el);
            float push = 1 + (DesertAtmo.FogPush(_w, storm) - 1) * dayW;
            if (env.applyFog && push > 1.0001f)
            {
                RenderSettings.fogStartDistance *= push;
                RenderSettings.fogEndDistance *= push;
            }
            var fogLin = RenderSettings.fogColor.linear;
            if (env.applyFog && _w > 0 && c.atmoFogWarm > 0)
            {
                float fl = 0.2126f * fogLin.r + 0.7152f * fogLin.g + 0.0722f * fogLin.b;
                var warm = new Color(fl * c.atmoDustR, fl * c.atmoDustG, fl * c.atmoDustB);
                float wl = 0.2126f * warm.r + 0.7152f * warm.g + 0.0722f * warm.b;
                warm *= fl / Mathf.Max(wl, 1e-4f);
                fogLin = Color.Lerp(fogLin, warm, c.atmoFogWarm * Mathf.Clamp01(_w) * (1 - storm) * DesertAtmo.Day(_el));
                RenderSettings.fogColor = fogLin.gamma;
                var skyMat = RenderSettings.skybox;
                if (skyMat != null && skyMat.HasProperty(SkyFogColorId)) skyMat.SetVector(SkyFogColorId, new Vector4(fogLin.r, fogLin.g, fogLin.b, 1));
            }

            // --- haze medium (per-pixel region weight in the shader)
            bool haze = (_w > 0 || (_near > 0 && map)) && DesertAtmo.Enabled;
            if (haze)
            {
                float k = c.atmoStrength;
                Shader.SetGlobalVector(DustId, V(DesertAtmo.DustGlobal(k * dayW, _storm)));
                Shader.SetGlobalVector(StormId, V(DesertAtmo.StormGlobal(k, _storm)));
                Shader.SetGlobalVector(HighId, V(DesertAtmo.HighGlobal(k * dayW, _storm)));
                Shader.SetGlobalVector(TintId, new Vector4(c.atmoDustR, c.atmoDustG, c.atmoDustB, c.atmoMie));
                // grade-1: the dust radiance capped near G3's horizon sky (no white-out of the whole distance)
                var hz = ((Color)s.hazeColor).linear;
                Shader.SetGlobalVector(CapId, new Vector4(DesertAtmo.HazeRadianceCap(0.2126f * hz.r + 0.7152f * hz.g + 0.0722f * hz.b), 0, 0, 0));
                RenderSettings.ambientProbe.Evaluate(_dirs, _shOut);
                var a = (_shOut[0] + _shOut[1] + _shOut[2] + _shOut[3] + _shOut[4]) * 0.2f;
                // the dust's own colour dominates its sky-lit glow (multiple scattering between the grains and off the sand):
                // the sky's luminance in the dust tint, a quarter of the sky's hue
                float al = 0.2126f * a.r + 0.7152f * a.g + 0.0722f * a.b;
                float tl = Mathf.Max(1e-4f, 0.2126f * c.atmoDustR + 0.7152f * c.atmoDustG + 0.0722f * c.atmoDustB);
                var warm = new Color(al * c.atmoDustR / tl, al * c.atmoDustG / tl, al * c.atmoDustB / tl);
                // (at night the sky's own hue: a warm veil would turn G3's blue night brown)
                float dayK = DesertAtmo.Day(_el);
                var ad = Color.Lerp(warm, new Color(a.r, a.g, a.b), 0.25f + 0.75f * (1 - dayK)) * c.atmoAmbScatter;
                Shader.SetGlobalVector(AmbId, new Vector4(ad.r, ad.g, ad.b, c.atmoRayleigh * k));
                float day = DesertAtmo.Day(_el);
                // G3's Sandstormy fog colour, lit, pulled towards an ochre dust hue (G3's is a greenish yellow)
                float sfl = 0.2126f * fogLin.r + 0.7152f * fogLin.g + 0.0722f * fogLin.b;
                var ochre = new Color(1.35f, 0.92f, 0.5f) * sfl;
                var stormFog = Color.Lerp(fogLin, ochre, 0.75f) * (0.3f + 0.6f * day);
                Shader.SetGlobalVector(StormColId, new Vector4(stormFog.r, stormFog.g, stormFog.b, 0.5f));
                var zen = ((Color)s.skyColor).linear;
                // grade-1: G3's dusk zenith key is lavender: the distance blue in it went purple (de-purpled like the dome)
                var zp = DesertAtmo.DePurple(zen.r, zen.g, zen.b, 1f);
                Shader.SetGlobalVector(RayId, new Vector4(zp[0] * 1.3f, zp[1] * 1.3f, zp[2] * 1.3f, 30000f));
                Shader.SetGlobalVector(OnId, new Vector4(1, map ? 1 : _desert, c.atmoSunScatter, Time.time));
            }
            else Shader.SetGlobalVector(OnId, Vector4.zero);

            // --- grade, sky (grade-1: the dome's de-purple everywhere the remaster lighting runs)
            Shader.SetGlobalVector(SkyGradeId, new Vector4(c.skyDePurple, 0, 0, 0));
            Shader.SetGlobalVector(GradeId, V(DesertAtmo.GradeGlobal(_w, _el, storm)));
            Shader.SetGlobalVector(Grade2Id, V(DesertAtmo.Grade2Global(_w, _el, storm)));
            var (shT, hiT) = DesertAtmo.SplitTints(_w, _el, storm);
            Shader.SetGlobalVector(ShadowTintId, new Vector4(shT[0], shT[1], shT[2], 1));
            Shader.SetGlobalVector(HighTintId, new Vector4(hiT[0], hiT[1], hiT[2], 1));
            Shader.SetGlobalVector(SkyId, V(DesertAtmo.SkyGlobal(_w, _el, storm)));
            if (sun != null && _w > 0)
            {
                var sd = -sun.transform.forward;
                var zl = ((Color)s.skyColor).linear;
                var hl = ((Color)s.hazeColor).linear;
                float lz = 0.2126f * zl.r + 0.7152f * zl.g + 0.0722f * zl.b, lh = 0.2126f * hl.r + 0.7152f * hl.g + 0.0722f * hl.b;
                Shader.SetGlobalVector(SkyPBId, V(DesertAtmo.SkyPBGlobal(_w, _el, storm, sd.x, sd.y, sd.z, lz, lh)));
            }
            else Shader.SetGlobalVector(SkyPBId, Vector4.zero);

            // --- motes
            _motes = DesertAtmo.MoteCount(_w, _el, storm);
            if (_motes > 0)
            {
                var wd = Shader.GetGlobalVector(WindDirId);
                var dir = new Vector3(wd.x, 0, wd.z);
                if (dir.sqrMagnitude < 1e-4f) dir = new Vector3(0.866f, 0, 0.5f);
                dir.Normalize();
                float speed = Mathf.Lerp(0.6f + 1.5f * Mathf.Clamp01(wd.w), 14f, storm);
                Shader.SetGlobalVector(MotesId, new Vector4(c.atmoMoteRange * (1 + storm * 0.6f), Time.time, storm, c.atmoMotes));
                Shader.SetGlobalVector(Motes2Id, new Vector4(dir.x, 0, dir.z, speed));
                Shader.SetGlobalVector(Motes3Id, new Vector4(c.atmoMoteMinPx, c.atmoMoteShaftRadius, c.atmoMoteForward, c.atmoMoteShaft));
            }

            // --- rays: desert daylight, clear air (a storm hides the sun); stronger at low sun
            _rays = Mathf.Clamp01(_w) * c.atmoRays * DesertAtmo.Day(_el) * (1 - storm) * (1 - Mathf.Sqrt(Mathf.Clamp01(overcast))) * (1 + 0.8f * DesertAtmo.Golden(_el));

            UpdateShadows();
        }

        // ------------------------------------------------------------------ shadows (URP asset, while v2 shadows are on)

        static void UpdateShadows()
        {
            var c = WorldRemasterLook.Current;
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (!G3WorldV2.ShadowsOn || urp == null) { _shadowSet = -1; return; }
            float want = DesertAtmo.ShadowDistance(c.v2ShadowDistance, Mathf.Clamp01(_w * (1 - _storm)));
            // the asset's own value (v2 re-applies its distance on a reload): move only on a real change (no per-frame churn)
            if (Mathf.Abs(want - urp.shadowDistance) < 2f) return;
            SetShadowDistance(want);
            _shadowSet = Mathf.Abs(want - c.v2ShadowDistance) < 2f ? -1 : want;
        }

        static void RestoreShadows()
        {
            if (_shadowSet > 0 && G3WorldV2.ShadowsOn) SetShadowDistance(WorldRemasterLook.Current.v2ShadowDistance);
            _shadowSet = -1;
        }

        static void SetShadowDistance(float d)
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null) return;
            var c = WorldRemasterLook.Current;
            urp.shadowDistance = d;
            var sp = WorldV2.CascadeSplits(Mathf.Max(0.3f, Camera.main != null ? Camera.main.nearClipPlane : 0.3f), d, urp.shadowCascadeCount, c.v2ShadowSplitLambda);
            if (sp.Length == 3) urp.cascade4Split = new Vector3(sp[0], sp[1], sp[2]);
            else if (sp.Length == 2) urp.cascade3Split = new Vector2(sp[0], sp[1]);
            else if (sp.Length == 1) urp.cascade2Split = sp[0];
        }

        // ------------------------------------------------------------------ motes pass

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_active || cam.cameraType != CameraType.Game || cam.cullingMask == 0) return;
            if (G3GpuSafe.Capturing) return;   // dx12-4: the save picture renders without rays and motes (dx12-3 capture rule)
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null || data.renderType != CameraRenderType.Base) return;
            BeginRays(cam, data);
            if (_motes <= 0) return;
            if (_motesMat == null)
            {
                var sh = Resources.Load<Shader>("G3AtmoMotes");
                if (sh == null || !sh.isSupported) return;
                _motesMat = new Material(sh) { name = "G3 Atmo Motes", hideFlags = HideFlags.DontSave };
            }
            _motesPass ??= new MotesPass();
            _motesPass.Setup(_motesMat, _motes * 6);
            data.scriptableRenderer.EnqueuePass(_motesPass);
        }

        // ------------------------------------------------------------------ screen-space rays towards the sun

        static void BeginRays(Camera cam, UniversalAdditionalCameraData data)
        {
            var c = WorldRemasterLook.Current;
            var sun = RenderSettings.sun;
            if (_rays <= 0 || sun == null || !sun.isActiveAndEnabled) return;
            Vector3 sd = -sun.transform.forward;
            float front = Mathf.Clamp01((Vector3.Dot(cam.transform.forward, sd) - 0.05f) / 0.35f);
            if (front <= 0) return;
            if (_raysMat == null)
            {
                var sh = Resources.Load<Shader>("G3AtmoRays");
                if (sh == null || !sh.isSupported) return;
                _raysMat = new Material(sh) { name = "G3 Atmo Rays", hideFlags = HideFlags.DontSave };
            }
            var sc = sun.color.linear * sun.intensity;
            Shader.SetGlobalVector(RaysId, new Vector4(_rays, 0.96f, 0.92f, 60f));
            Shader.SetGlobalVector(RaysSunId, new Vector4(0, 0, front, 600f));
            Shader.SetGlobalVector(RaysColId, new Vector4(sc.r * c.atmoDustR, sc.g * c.atmoDustG, sc.b * c.atmoDustB, 0));
            Shader.SetGlobalVector(RaysDirId, new Vector4(sd.x, sd.y, sd.z, 0));
            _raysPass ??= new RaysPass();
            _raysPass.Setup(_raysMat);
            data.scriptableRenderer.EnqueuePass(_raysPass);
        }

        /// <summary>Mask, two radial blurs (1/4 res) and an additive composite (full res), after the transparents.</summary>
        sealed class RaysPass : ScriptableRenderPass
        {
            Material _mat;
            sealed class Data { public TextureHandle src, aux; public Material mat; public int pass; }

            public RaysPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents - 4;
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(Material mat) => _mat = mat;

            void Blit(RenderGraph rg, UniversalResourceData res, string name, TextureHandle src, TextureHandle aux, TextureHandle dst, int pass)
            {
                using (var builder = rg.AddRasterRenderPass<Data>(name, out var d))
                {
                    d.src = src; d.aux = aux; d.mat = _mat; d.pass = pass;
                    builder.UseTexture(src);
                    if (aux.IsValid()) builder.UseTexture(aux);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(dst, 0);
                    builder.SetRenderFunc((Data x, RasterGraphContext ctx) =>
                    {
                        var b = G3PassBlit.Begin(x.src);
                        if (x.aux.IsValid()) b.SetTexture(RaysTexId, (RTHandle)x.aux);
                        G3PassBlit.Draw(ctx.cmd, x.mat, x.pass);
                    });
                }
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_mat == null) return;
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer) return;
                var src = res.activeColorTexture;
                if (!src.IsValid()) return;   // dx12-4: skipped cleanly when the camera has no colour target this frame
                var desc = renderGraph.GetTextureDesc(src);
                if (desc.width < 4 || desc.height < 4) return;
                var q = desc;
                q.name = "G3 Atmo Rays";
                q.width = Mathf.Max(1, desc.width / 4);
                q.height = Mathf.Max(1, desc.height / 4);
                q.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16_SFloat;
                q.depthBufferBits = DepthBits.None;
                q.msaaSamples = MSAASamples.None;
                q.clearBuffer = false;
                q.filterMode = FilterMode.Bilinear;
                var mask = renderGraph.CreateTexture(q);
                var b1 = renderGraph.CreateTexture(q);
                var b2 = renderGraph.CreateTexture(q);
                Blit(renderGraph, res, "G3 Atmo Rays Mask", src, TextureHandle.nullHandle, mask, 0);
                Blit(renderGraph, res, "G3 Atmo Rays Blur 1", mask, TextureHandle.nullHandle, b1, 1);
                Blit(renderGraph, res, "G3 Atmo Rays Blur 2", b1, TextureHandle.nullHandle, b2, 2);
                var full = desc;
                full.name = "G3 Atmo Rays Composite";
                full.clearBuffer = false;
                var target = renderGraph.CreateTexture(full);
                Blit(renderGraph, res, "G3 Atmo Rays Composite", src, b2, target, 3);
                res.cameraColor = target;
            }
        }

        /// <summary>One DrawProcedural of the motes after the transparents (before the sand fx, the haze and the grade).</summary>
        sealed class MotesPass : ScriptableRenderPass
        {
            Material _mat;
            int _vertices;
            sealed class PassData { public Material mat; public int vertices; public Matrix4x4 view, proj; }

            public MotesPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents - 3;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(Material mat, int vertices) { _mat = mat; _vertices = vertices; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_mat == null || _vertices <= 0) return;
                var res = frameData.Get<UniversalResourceData>();
                if (!res.activeColorTexture.IsValid() || !res.activeDepthTexture.IsValid()) return;   // dx12-4
                var camData = frameData.Get<UniversalCameraData>();
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("G3 Atmo Motes", out var pd))
                {
                    pd.mat = _mat;
                    pd.vertices = _vertices;
                    pd.view = camData.GetViewMatrix();
                    pd.proj = GL.GetGPUProjectionMatrix(camData.GetProjectionMatrix(), true);
                    builder.SetRenderAttachment(res.activeColorTexture, 0);
                    builder.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.Read);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        // the camera's matrices again: the full-screen copies before leave others set
                        if (d.mat == null) return;
                        RenderingUtils.SetViewAndProjectionMatrices(ctx.cmd, d.view, d.proj, true);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.mat, 0, MeshTopology.Triangles, d.vertices);
                    });
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            G3Environment.Applied -= OnEnvironment;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            _hooked = false;
            _active = false;
            _option = null;
            _shadowSet = -1;
            _motesPass = null;
            _w = _storm = _desert = _near = _rays = 0;
            _raysPass = null;
            ClearGlobals();
        }
    }
}
