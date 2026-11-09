using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Drives sun/moon light, ambient, fog and sky from Gothic 3 weather curves (G3WeatherAsset) for a time of day.
    /// Follows eCWeatherAdmin::Process (Engine.dll, see Docs/formats-weather.md §6): t = hour / 24, sun arc 6:00–22:00
    /// (pitch 60°), moon arc 22:00–6:00 (pitch 50°), light intensity faded over the last 30° of the arc, and the
    /// eCHemisphere_PS ambient (GeneralAmbient + BackLight + SunLight + GroundLight lobes) fed the same way.
    /// G3 renders in gamma space, so light sums are formed in gamma and converted to linear once; Unity linearizes
    /// Light.color / RenderSettings.fogColor / camera background itself, so those receive the gamma colour.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("G3/G3 Environment")]
    public sealed class G3Environment : MonoBehaviour
    {
        [Header("Data")]
        public G3WeatherAsset weather;
        [Tooltip("Weather state (Sunny, Rainy, Foggy, Sunny(Nordmar) ...) or environment (Myrtana, Varant, Nordmar → most probable state).")]
        public string weatherState = "Myrtana";
        [Tooltip("Optional second state blended in by 'blend' (e.g. Rainy).")]
        public string blendState = "";
        [Range(0, 1)] public float blend;

        [Header("Time (G3: curve time = hour / 24, verified in gCClock_PS::OnProcess)")]
        [Range(0, 24)] public float timeOfDay = 12f;
        public bool animate;
        [Tooltip("Real minutes per game day. G3 gCClock Factor 12 → 120 min.")]
        public float dayLengthMinutes = 120f;

        [Header("Sun / moon path (eCWeatherAdmin::Process)")]
        [Tooltip("Sun arc between these hours (G3: t 0.25 … 0.9167 = 6:00 … 22:00); the moon takes the rest.")]
        public float sunriseHour = 6f;
        public float sunsetHour = 22f;
        [Tooltip("Tilt of the sun arc (G3: 60°) = the sun's elevation at the middle of the day.")]
        public float sunPitch = 60f;
        [Tooltip("Tilt of the moon arc (G3: 50°).")]
        public float moonPitch = 50f;
        [Tooltip("Light fades to 0 over this many degrees of arc before the horizon (G3: 90° − 60° = 30°).")]
        public float horizonFadeDegrees = 30f;
        [Tooltip("Extra yaw of the whole sky (World_MCP world matrix; identity in G3).")]
        public float skyYaw = 0f;

        [Header("Targets")]
        public Light sun;
        public Material skyMaterial;
        public bool applyFog = true;
        public bool applyAmbient = true;
        public bool applySky = true;

        [Header("Tuning (G3 gamma lighting → URP linear)")]
        [Tooltip("Unity light intensity = G3 LightIntensity × horizon fade × this.")]
        public float sunIntensityScale = 0.8f;
        [Tooltip("Extra factor for the moon (night) light.")]
        public float moonIntensityScale = 0.45f;
        [Tooltip("G3 hemisphere ambient (gamma, × AmbientIntensity) is multiplied by this before the gamma→linear conversion.")]
        public float ambientScale = 0.34f;
        [Tooltip("Directionality of the hemisphere lobes: lobe(n) = max(0, 1 + k·dot(n, dir)).")]
        [Range(0, 1)] public float lobeDirectionality = 0.5f;
        [Tooltip("Saturation multiplier / HSV-saturation cap of the sun/moon colour (G3 keys reach S = 1 at night and in Rainy).")]
        [Range(0, 1.5f)] public float lightSaturation = 0.9f;
        [Range(0, 1)] public float lightMaxSaturation = 0.5f;
        [Tooltip("Saturation multiplier / cap of the ambient lobes.")]
        [Range(0, 1.5f)] public float ambientSaturation = 0.85f;
        [Range(0, 1)] public float ambientMaxSaturation = 0.45f;
        [Tooltip("Saturation multiplier / cap of fog and sky colours.")]
        [Range(0, 1.5f)] public float fogSaturation = 1f;
        [Range(0, 1)] public float fogMaxSaturation = 0.45f;
        [Tooltip("Overcast weathers (CloudThickness ≫ 0.6: Rainy, Snowy, storms) get their saturation caps multiplied by (1 − this × overcast) — the Rainy keys are pure cyan.")]
        [Range(0, 1)] public float overcastDesaturation = 0.4f;
        [Tooltip("G3 eCHemisphere_PS.GroundLight of World_MCP (gamma).")]
        public Color groundLight = new Color(0.196f, 0.196f, 0.196f);
        [Tooltip("Fog distances: G3 cm × 0.01 × this (G3 fog is linear and depth based; ×1 is very dense in URP).")]
        public float fogDistanceScale = 3.5f;
        [Tooltip("Lower limit of the fog start (metres) so the foreground stays clear.")]
        public float minFogStart = 10f;
        public float skyExposure = 1f;

        /// <summary>Last applied sample (gamma colours, G3 units) — for debugging/UI.</summary>
        public G3WeatherSample Current { get; private set; }
        public bool IsDay { get; private set; }
        /// <summary>G3 horizon fade of the current light (1 = full, 0 = at the horizon).</summary>
        public float LightFade { get; private set; }

        /// <summary>Bumped when the path/tuning model changes; older serialized components get <see cref="ResetTuning"/>.</summary>
        public const int CurrentTuningVersion = 6;
        [SerializeField, HideInInspector] int tuningVersion;

        void OnEnable()
        {
#if UNITY_EDITOR
            Live.Add(this);
#endif
            MigrateTuning();
            Apply();
        }

        Material _skyInstance; // a copy of skyMaterial, so the time-of-day writes never touch the shared asset
        Material _skyInstanceOf;

        /// <summary>
        /// The material the sky values go to: a copy of <see cref="skyMaterial"/> (DontSave) in Play and in edit mode, so
        /// neither Play (editor-runner-21) nor an edit-mode Apply after a recompile (editor-runner-23: [ExecuteAlways]
        /// OnEnable wrote the asset, saved by the next AssetDatabase.SaveAssets) changes the asset file; it changes only
        /// when someone edits it. In edit mode the scene is saved with the asset as its skybox (sceneSaving swap below).
        /// </summary>
        Material SkyTarget()
        {
            if (skyMaterial == null) return null;
            if ((skyMaterial.hideFlags & HideFlags.DontSave) != 0) return skyMaterial;
            if (_skyInstance == null || _skyInstanceOf != skyMaterial)
            {
                DropSkyInstance();
                _skyInstance = new Material(skyMaterial) { name = skyMaterial.name + " (instance)", hideFlags = HideFlags.DontSave };
                _skyInstanceOf = skyMaterial;
            }
            return _skyInstance;
        }

        void DropSkyInstance()
        {
            if (_skyInstance == null) return;
            if (RenderSettings.skybox == _skyInstance) RenderSettings.skybox = _skyInstanceOf;
            if (Application.isPlaying) Destroy(_skyInstance); else DestroyImmediate(_skyInstance);
            _skyInstance = null;
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            Live.Remove(this);
#endif
            DropSkyInstance(); // a domain reload or a disabled component leaves the asset as the skybox
        }

        void OnDestroy() => DropSkyInstance();

#if UNITY_EDITOR
        static readonly System.Collections.Generic.HashSet<G3Environment> Live = new System.Collections.Generic.HashSet<G3Environment>();
        bool _swappedForSave;

        /// <summary>The scene keeps the asset as its skybox: the DontSave copy is swapped out while a scene is saved.</summary>
        [UnityEditor.InitializeOnLoadMethod]
        static void HookSceneSaves()
        {
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += (scene, path) =>
            {
                foreach (var e in Live)
                    if (e != null && e._skyInstance != null && RenderSettings.skybox == e._skyInstance)
                    {
                        RenderSettings.skybox = e._skyInstanceOf;
                        e._swappedForSave = true;
                    }
            };
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += scene =>
            {
                foreach (var e in Live)
                    if (e != null && e._swappedForSave)
                    {
                        e._swappedForSave = false;
                        if (e._skyInstance != null) RenderSettings.skybox = e._skyInstance;
                    }
            };
        }
#endif

        void OnValidate() { MigrateTuning(); Apply(); }

        void MigrateTuning()
        {
            if (tuningVersion >= CurrentTuningVersion) return;
            ResetTuning();
        }

        /// <summary>Restores the decoded G3 sun path and the default G3 → URP tuning.</summary>
        public void ResetTuning()
        {
            sunriseHour = 6f; sunsetHour = 22f; sunPitch = 60f; moonPitch = 50f; horizonFadeDegrees = 30f; skyYaw = 0f;
            sunIntensityScale = 0.8f; moonIntensityScale = 0.45f; ambientScale = 0.34f; lobeDirectionality = 0.5f;
            lightSaturation = 0.9f; lightMaxSaturation = 0.5f; ambientSaturation = 0.85f; ambientMaxSaturation = 0.45f;
            fogSaturation = 1f; fogMaxSaturation = 0.45f; groundLight = new Color(0.196f, 0.196f, 0.196f);
            fogDistanceScale = 3.5f; minFogStart = 10f; skyExposure = 1f; overcastDesaturation = 0.4f;
            tuningVersion = CurrentTuningVersion;
        }

        void Update()
        {
            if (animate && Application.isPlaying && dayLengthMinutes > 0)
                timeOfDay = Mathf.Repeat(timeOfDay + Time.deltaTime * 24f / (dayLengthMinutes * 60f), 24f);
            Apply();
        }

        public G3WeatherSample Evaluate(float hours)
        {
            if (weather == null) return default;
            float t = Mathf.Repeat(hours, 24f) / 24f;
            if (_mix.Count > 0) return EvaluateMix(t);
            var a = weather.Resolve(weatherState);
            var s = a != null ? a.Sample(t) : default;
            if (blend > 0 && !string.IsNullOrEmpty(blendState))
            {
                var b = weather.Resolve(blendState);
                if (b != null) s = G3WeatherSample.Lerp(s, b.Sample(t), blend);
            }
            return s;
        }

        readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, float>> _mix =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, float>>();

        /// <summary>
        /// fix-215: every weather state with its weight (heaviest first), set by G3WeatherDirector; empty = the two-state
        /// <see cref="weatherState"/> / <see cref="blendState"/> / <see cref="blend"/>. With three or more states (a region
        /// border during a schedule fade, a forced state fading in) the top two dropped a state at once when the second and
        /// third swapped places — the sky, fog and light jumped at a line.
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, float>> StateMix => _mix;

        /// <summary>Sets <see cref="StateMix"/> (copied; null or empty clears it).</summary>
        public void SetStateMix(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, float>> mix)
        {
            _mix.Clear();
            if (mix != null) _mix.AddRange(mix);
        }

        G3WeatherSample EvaluateMix(float t)
        {
            // running lerp: s_k = lerp(s_{k-1}, x_k, w_k / Σw_1..k) is the weighted mean of every state's sample
            G3WeatherSample s = default;
            float cum = 0;
            foreach (var kv in _mix)
            {
                if (kv.Value <= 0) continue;
                var st = weather.Resolve(kv.Key);
                if (st == null) continue;
                cum += kv.Value;
                var x = st.Sample(t);
                s = cum <= kv.Value ? x : G3WeatherSample.Lerp(s, x, kv.Value / cum);
            }
            if (cum > 0) return s;
            var a = weather.Resolve(weatherState);
            return a != null ? a.Sample(t) : default;
        }

        /// <summary>
        /// G3 light arc rotation: RotY(yaw) then RotX(pitch) applied to +Z (bCMatrix::SetToRotationAxisY + RotateAxisX,
        /// row vectors), i.e. a great circle tilted by 'pitch' from the horizon. Forward = the light's travel direction.
        /// </summary>
        public Quaternion ArcRotation(float yawDeg, float pitchDeg) =>
            Quaternion.AngleAxis(skyYaw, Vector3.up) * Quaternion.AngleAxis(pitchDeg, Vector3.right) * Quaternion.AngleAxis(yawDeg, Vector3.up);

        /// <summary>Sun arc yaw (degrees): −90° at sunrise, +90° at sunset; continues below the horizon at night.</summary>
        public float SunYaw(float h) => (h - sunriseHour) / Mathf.Max(0.1f, sunsetHour - sunriseHour) * 180f - 90f;

        /// <summary>Moon arc yaw: −90° at sunset … +90° at sunrise (G3: frac(t + 1/12) / (1/3) · π − π/2).</summary>
        public float MoonYaw(float h)
        {
            float nightLen = Mathf.Max(0.1f, 24f - (sunsetHour - sunriseHour));
            return Mathf.Repeat(h - sunsetHour, 24f) / nightLen * 180f - 90f;
        }

        public void Apply()
        {
            if (weather == null) return;
            var s = Evaluate(timeOfDay);
            Current = s;
            float overcast = Mathf.Clamp01((s.cloudThickness - 0.6f) / 2.4f);
            float satK = 1f - overcastDesaturation * overcast;
            float lightMaxS = lightMaxSaturation * satK, ambMaxS = ambientMaxSaturation * satK, fogMaxS = fogMaxSaturation * satK;

            // --- light arc ---------------------------------------------------------------------------------
            float h = Mathf.Repeat(timeOfDay, 24f);
            IsDay = h > sunriseHour && h < sunsetHour;
            float yaw = IsDay ? SunYaw(h) : MoonYaw(h);
            Quaternion lightRot = ArcRotation(yaw, IsDay ? sunPitch : moonPitch);
            // G3: fade = clamp01((90° − |yaw|) / (90° − 60°))
            float fade = Mathf.Clamp01((90f - Mathf.Abs(yaw)) / Mathf.Max(1f, horizonFadeDegrees));
            LightFade = fade;
            Vector3 toLight = -(lightRot * Vector3.forward);
            // BackLightAxisDirectionOffset = (yaw, 0, 0): horizontal travel direction of the light (= away from it)
            Vector3 backDir = Quaternion.AngleAxis(skyYaw + yaw, Vector3.up) * Vector3.forward;

            if (sun == null) sun = RenderSettings.sun;
            if (sun != null)
            {
                // keep the shadow-casting light from grazing below the terrain (intensity is ~0 there anyway)
                Vector3 fwd = lightRot * Vector3.forward;
                if (fwd.y > -0.07f) { fwd.y = -0.07f; fwd.Normalize(); }
                sun.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                if (sun.useColorTemperature) sun.useColorTemperature = false;
                // Unity linearizes Light.color itself (lightsUseLinearIntensity) → pass the gamma colour.
                sun.color = Opaque(LimitSaturation(s.lightDiffuse, lightSaturation, lightMaxS));
                float k = IsDay ? sunIntensityScale : sunIntensityScale * moonIntensityScale;
                sun.intensity = Mathf.Max(0f, s.lightIntensity * fade * k);
                sun.shadowStrength = Mathf.Clamp01(s.shadowDensity * 1.25f);
                if (sun.shadows == LightShadows.None) sun.shadows = LightShadows.Soft;
                if (RenderSettings.sun != sun) RenderSettings.sun = sun;
            }

            // --- ambient: eCHemisphere_PS as fed by eCWeatherAdmin::Process --------------------------------
            //   GeneralAmbient = AmbientGeneral                        (uniform)
            //   BackLight      = X·(1−f)·½(1−f) + AmbientBack·½(1+f)   (lobe along the light's horizontal travel dir)
            //   SunLight       = X·(1−f)·½(1+f) + AmbientBack·½(1−f)   (lobe towards the light)
            //   GroundLight    = 0.196 grey                            (lobe from below)
            //   Intensity      = AmbientIntensity;  X = LightDiffuse × LightIntensity, f = horizon fade.
            if (applyAmbient)
            {
                Color X = s.lightDiffuse * s.lightIntensity;
                float f = fade;
                Color backL = X * ((1 - f) * 0.5f * (1 - f)) + s.ambientBack * (0.5f * (1 + f));
                Color sunL = X * ((1 - f) * 0.5f * (1 + f)) + s.ambientBack * (0.5f * (1 - f));
                float ai = s.ambientIntensity * ambientScale;
                Color general = LimitSaturation(s.ambientGeneral, ambientSaturation, ambMaxS) * ai;
                backL = LimitSaturation(backL, ambientSaturation, ambMaxS) * ai;
                sunL = LimitSaturation(sunL, ambientSaturation, ambMaxS) * ai;
                Color ground = groundLight * ai;

                // Trilight/Flat would recompute (overwrite) the probe. Mode/flag setters are guarded: Apply runs every frame
                // and each RenderSettings write is a native call with change notifications (perf-7).
                if (RenderSettings.ambientMode != AmbientMode.Custom) RenderSettings.ambientMode = AmbientMode.Custom;
                RenderSettings.ambientSkyColor = Opaque(general + backL + sunL);
                RenderSettings.ambientEquatorColor = Opaque(general + (backL + sunL) * 0.5f + ground * 0.5f);
                RenderSettings.ambientGroundColor = Opaque(general + ground);
                if (RenderSettings.ambientIntensity != 1f) RenderSettings.ambientIntensity = 1f;
                RenderSettings.ambientProbe = HemisphereProbe(general, backL, backDir, sunL, toLight, ground, lobeDirectionality);
            }

            // --- fog (G3: linear, depth based, cm) ------------------------------------------------------------
            Color fogGamma = Opaque(LimitSaturation(s.fogColor, fogSaturation, fogMaxS));
            if (applyFog)
            {
                if (!RenderSettings.fog) RenderSettings.fog = true;
                if (RenderSettings.fogMode != FogMode.Linear) RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = fogGamma; // Unity linearizes it
                float start = Mathf.Max(minFogStart, s.fogStart * 0.01f * fogDistanceScale);
                float end = s.fogEnd * 0.01f * fogDistanceScale;
                RenderSettings.fogStartDistance = start;
                RenderSettings.fogEndDistance = Mathf.Max(end, start + 10f);
            }

            // --- sky (G3/Sky: zenith SkyColor, horizon HazeColor, fog band, overcast from CloudThickness) --------
            if (applySky)
            {
                if (skyMaterial == null)
                {
                    var sh = Shader.Find("G3/Sky");
                    if (sh != null) skyMaterial = new Material(sh) { name = "G3 Sky (runtime)", hideFlags = HideFlags.DontSave };
                }
                var sky = SkyTarget();
                if (sky != null)
                {
                    Vector3 sunDir = -(ArcRotation(SunYaw(h), sunPitch) * Vector3.forward);
                    Vector3 moonDir = -(ArcRotation(MoonYaw(h), moonPitch) * Vector3.forward);
                    SetLinear(sky, SkyIds.Zenith, LimitSaturation(s.skyColor, fogSaturation, fogMaxS + 0.1f));
                    SetLinear(sky, SkyIds.Horizon, LimitSaturation(s.hazeColor, fogSaturation, fogMaxS));
                    SetLinear(sky, SkyIds.Fog, fogGamma);
                    SetLinear(sky, SkyIds.Cloud, LimitSaturation(s.cloudColor, fogSaturation, fogMaxS));
                    SetLinear(sky, SkyIds.SunColor, LimitSaturation(s.lightDiffuse, 1f, 0.6f));
                    sky.SetFloat(SkyIds.Overcast, overcast);
                    sky.SetVector(SkyIds.SunDir, sunDir);
                    sky.SetVector(SkyIds.MoonDir, moonDir);
                    sky.SetFloat(SkyIds.Night, Mathf.Clamp01(-sunDir.y * 6f));
                    sky.SetFloat(SkyIds.Exposure, skyExposure);
                    // Assigning the skybox (even the same one) every frame dirties the environment; only on change.
                    if (RenderSettings.skybox != sky) RenderSettings.skybox = sky;
                }
                var cam = Camera.main;
                if (cam != null && cam.clearFlags == CameraClearFlags.SolidColor)
                    cam.backgroundColor = fogGamma; // gamma, Unity converts
            }
            Applied?.Invoke(this, s, overcast);
        }

        /// <summary>
        /// Raised at the end of every <see cref="Apply"/> with the sample and the overcast (0..1) it used: the world remaster
        /// v2 look (G3WorldV2) adjusts sun, ambient and fog on top of G3's values here. No subscriber = G3's values as is.
        /// </summary>
        public static event System.Action<G3Environment, G3WeatherSample, float> Applied;

        // In a nested class: a MonoBehaviour's own static initializer may run during deserialization, where
        // Shader.PropertyToID is not allowed.
        static class SkyIds
        {
            public static readonly int Zenith = Shader.PropertyToID("_ZenithColor"), Horizon = Shader.PropertyToID("_HorizonColor"),
                Fog = Shader.PropertyToID("_FogColor"), Cloud = Shader.PropertyToID("_CloudColor"),
                SunColor = Shader.PropertyToID("_SunColor"), Overcast = Shader.PropertyToID("_Overcast"),
                SunDir = Shader.PropertyToID("_SunDir"), MoonDir = Shader.PropertyToID("_MoonDir"),
                Night = Shader.PropertyToID("_Night"), Exposure = Shader.PropertyToID("_Exposure");
        }

        static void SetLinear(Material m, int id, Color gamma)
        {
            var c = gamma.linear;
            m.SetVector(id, new Vector4(c.r, c.g, c.b, 1f));
        }

        static Color Opaque(Color c) { c.a = 1f; return c; }

        /// <summary>
        /// Scales a (gamma) colour's saturation around its luma by <paramref name="k"/>, then caps the HSV saturation at
        /// <paramref name="maxS"/> keeping the brightest channel. Documented taming of G3's fully saturated keys.
        /// </summary>
        public static Color LimitSaturation(Color c, float k, float maxS)
        {
            float l = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;
            c = new Color(Mathf.Max(0, l + (c.r - l) * k), Mathf.Max(0, l + (c.g - l) * k), Mathf.Max(0, l + (c.b - l) * k), 1f);
            float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            float mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            if (mx <= 1e-5f) return c;
            float sat = (mx - mn) / mx;
            if (sat <= maxS) return c;
            float t = maxS / sat;
            return new Color(mx - (mx - c.r) * t, mx - (mx - c.g) * t, mx - (mx - c.b) * t, 1f);
        }

        // ------------------------------------------------------------------ SH ambient
        const int ShSamples = 256;
        static Vector3[] s_Dirs;
        static readonly float[] s_Norm = { 1f, 1f / 3f, 1f / 3f, 1f / 3f, 1f / 15f, 1f / 15f, 4f / 5f, 1f / 15f, 4f / 15f };
        static readonly float[] s_B = new float[9];
        static readonly Vector3[] s_Acc = new Vector3[9];

        /// <summary>
        /// Evaluates the G3 hemisphere per direction in gamma space — G + B·lobe(n·dBack) + S·lobe(n·dSun) +
        /// Gr·lobe(−n.y) with lobe(x) = max(0, 1 + k·x) — converts every sample to linear (G3 lights in gamma) and
        /// projects onto the unnormalised basis used by SphericalHarmonicsL2.Evaluate / the URP shaders
        /// (1, y, z, x, xy, yz, 3z²−1, xz, x²−y²), so the probe evaluates to that irradiance directly.
        /// </summary>
        static SphericalHarmonicsL2 HemisphereProbe(Color general, Color back, Vector3 backDir, Color sunL, Vector3 sunDir, Color ground, float k)
        {
            if (s_Dirs == null)
            {
                s_Dirs = new Vector3[ShSamples];
                for (int i = 0; i < ShSamples; i++)
                {
                    float y = 1f - 2f * (i + 0.5f) / ShSamples; // Fibonacci sphere
                    float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                    float phi = i * 2.39996323f;
                    s_Dirs[i] = new Vector3(Mathf.Cos(phi) * r, y, Mathf.Sin(phi) * r);
                }
            }
            System.Array.Clear(s_Acc, 0, 9);
            foreach (var d in s_Dirs)
            {
                Color c = general
                          + back * Mathf.Max(0f, 1f + k * Vector3.Dot(d, backDir))
                          + sunL * Mathf.Max(0f, 1f + k * Vector3.Dot(d, sunDir))
                          + ground * Mathf.Max(0f, 1f - k * d.y);
                c = new Color(Mathf.Max(0, c.r), Mathf.Max(0, c.g), Mathf.Max(0, c.b)).linear;
                var v = new Vector3(c.r, c.g, c.b);
                float x = d.x, y = d.y, z = d.z;
                s_B[0] = 1f; s_B[1] = y; s_B[2] = z; s_B[3] = x; s_B[4] = x * y; s_B[5] = y * z;
                s_B[6] = 3f * z * z - 1f; s_B[7] = x * z; s_B[8] = x * x - y * y;
                for (int i = 0; i < 9; i++) s_Acc[i] += v * s_B[i];
            }
            var sh = new SphericalHarmonicsL2();
            for (int i = 0; i < 9; i++)
            {
                Vector3 v = s_Acc[i] / (ShSamples * s_Norm[i]);
                if (i >= 4) v *= 0.75f; // soften the L2 band (ringing of the clamped lobes)
                sh[0, i] = v.x; sh[1, i] = v.y; sh[2, i] = v.z;
            }
            return sh;
        }
    }
}
