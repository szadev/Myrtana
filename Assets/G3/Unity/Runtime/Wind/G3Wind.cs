using UnityEngine;
using UnityEngine.Rendering;

namespace G3.Unity
{
    /// <summary>
    /// Global wind for trees (G3/WindLit) and the vegetation grid (G3/Vegetation): writes the _G3Wind* shader
    /// globals (see Shaders/Wind/G3Wind.hlsl) every frame / before every camera render. Defaults are the
    /// original Gothic 3 eCSpeedTreeWind_PS values of World_MCP (SpeedTree SpeedWind units: strength 0..1,
    /// frequencies per minute, angles in degrees); <see cref="bendScale"/> etc. tone them down to G3's gentle look.
    /// Optionally follows the current G3Environment weather (rain / snow storm / sand storm = stronger wind).
    /// The wind clock (_G3WindTime) is driven here so it can be frozen or set from editor scripts
    /// (<see cref="timeOverride"/>) for deterministic captures.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class G3Wind : MonoBehaviour
    {
        public static G3Wind Active { get; private set; }

        [Header("Direction")]
        [Tooltip("Wind azimuth in degrees (0 = towards +Z, 90 = towards +X).")]
        public float azimuth = 60f;
        [Tooltip("Slow random wander of the direction, degrees.")]
        public float azimuthWander = 15f;

        [Header("G3 eCSpeedTreeWind_PS (World_MCP)")]
        [Tooltip("WindStrange: base wind strength (SpeedWind 0..1).")]
        [Range(0, 1)] public float windStrength = 0.1f;
        [Tooltip("GustStrength: min / max additional strength of a gust.")]
        public Vector2 gustStrength = new Vector2(0.1f, 0.5f);
        [Tooltip("GustDuration: min / max seconds.")]
        public Vector2 gustDuration = new Vector2(0.1f, 3f);
        [Tooltip("GustFrequency: gusts per minute.")]
        public float gustFrequency = 30f;
        [Tooltip("MaxBendAngle: trunk bend at strength 1, degrees.")]
        public float maxBendAngle = 20f;
        [Tooltip("StrengthAdjustmentExponent.")]
        public float strengthExponent = 1f;
        [Tooltip("BendLow / BendHigh (x): sway amount at strength 0 / 1.")]
        public Vector2 bendLowHigh = new Vector2(0.2f, 0.5f);
        [Tooltip("LeafRustling (y) amplitude-ish at low / high strength, scaled by leafFlutterScale (metres).")]
        public Vector2 leafRustleLowHigh = new Vector2(0.5f, 1f);
        [Tooltip("LeafRustlingFrequency: per minute at strength 0 / 1.")]
        public Vector2 leafRustleFrequency = new Vector2(100f, 500f);

        [Header("Look (multipliers on the G3 values)")]
        [Range(0, 2)] public float bendScale = 0.3f;
        [Range(0, 2)] public float swayScale = 1f;
        [Tooltip("Branch bob amplitude as a fraction of the tree height, per unit strength.")]
        [Range(0, 0.05f)] public float branchAmplitude = 0.005f;
        [Tooltip("Leaf flutter amplitude in metres at rustle 1.")]
        [Range(0, 0.2f)] public float leafFlutterScale = 0.006f;
        [Tooltip("Upper limit of the leaf flutter frequency in Hz (G3's rustle rates reach ~8 Hz, which reads as shaking).")]
        [Range(0.2f, 8f)] public float maxLeafFlutterHz = 1.5f;
        [Tooltip("Grass bend per metre of plant height at strength 1.")]
        [Range(0, 2)] public float grassBend = 0.45f;
        [Range(0, 1)] public float grassSway = 0.12f;
        [Tooltip("Grass sway frequency, rad/s.")]
        public float grassFrequency = 2.1f;
        [Tooltip("Length of travelling gust fronts (m) and their speed (m/s).")]
        public float gustWaveLength = 40f;
        public float gustWaveSpeed = 9f;
        [Range(0, 1)] public float gustContrast = 0.6f;

        [Header("Weather")]
        [Tooltip("Scale the wind with the current G3Environment weather state.")]
        public bool followWeather = true;
        public G3Environment environment;
        [Tooltip("Extra multiplier (e.g. for tests). 0 = calm.")]
        [Range(0, 5)] public float strengthMultiplier = 1f;

        [Header("Clock")]
        public bool animateInEditMode = true;
        [Tooltip(">= 0: freeze the wind clock at this time (seconds). Used for deterministic captures.")]
        public float timeOverride = -1f;

        // Read-only state (current frame)
        [System.NonSerialized] public float currentStrength, currentGust, currentWeatherFactor = 1, currentTime;

        float _weatherSmoothed = -1, _nextEnvSearch;

        static readonly int TimeId = Shader.PropertyToID("_G3WindTime");
        static readonly int TimePrevId = Shader.PropertyToID("_G3WindTimePrev");
        // remaster TAA (Docs/remaster-world.md 11.7): the clock of the previous frame for the motion-vector passes
        float _clockNow = float.NaN, _clockPrev;
        int _clockFrame = -1;
        static readonly int DirId = Shader.PropertyToID("_G3WindDirection");
        static readonly int TreeId = Shader.PropertyToID("_G3WindTree");
        static readonly int LeafId = Shader.PropertyToID("_G3WindLeaf");
        static readonly int GrassId = Shader.PropertyToID("_G3WindGrass");
        static readonly int GustId = Shader.PropertyToID("_G3WindGust");
        static readonly int CloudDriftId = Shader.PropertyToID("_G3CloudDrift");

        // fix-146: cloud travel = integral of the wind direction (G3.Weather.CloudDrift), not dir(t) * t
        readonly G3.Weather.CloudDrift _clouds = new G3.Weather.CloudDrift();

        void OnEnable()
        {
            Active = this;
            RenderPipelineManager.beginContextRendering += OnBeginContext;
            Apply();
        }

        void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContext;
            if (Active == this) Active = null;
            Shader.SetGlobalVector(DirId, Vector4.zero);
            Shader.SetGlobalVector(TreeId, Vector4.zero);
            Shader.SetGlobalVector(LeafId, Vector4.zero);
            Shader.SetGlobalVector(GrassId, Vector4.zero);
            Shader.SetGlobalVector(GustId, Vector4.zero);
            Shader.SetGlobalVector(CloudDriftId, Vector4.zero);
        }

        void Update() => Apply();

        void OnBeginContext(ScriptableRenderContext ctx, System.Collections.Generic.List<Camera> cams) => Apply();

        float Clock()
        {
            if (timeOverride >= 0) return timeOverride;
            if (Application.isPlaying) return Time.time;
            return animateInEditMode ? (float)(Time.realtimeSinceStartupAsDouble % 100000.0) : 0f;
        }

        /// <summary>Wind multiplier of a weather state name (G3 states: Sunny, Rainy, Foggy, Snowy, Snowstorm, Sandstormy ...).</summary>
        public static float WeatherFactor(string state)
        {
            if (string.IsNullOrEmpty(state)) return 1f;
            // Called 2-4 times per frame: case-insensitive search instead of a lower-cased copy (perf-7).
            if (Has(state, "sandstorm")) return 5f;
            if (Has(state, "snowstorm")) return 4.5f;
            if (Has(state, "storm")) return 4f;
            if (Has(state, "rain")) return 2.5f;
            if (Has(state, "snow")) return 1.8f;
            if (Has(state, "fog")) return 0.5f;
            return 1f;
        }

        static bool Has(string s, string part) => s.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;

        float ResolveWeather()
        {
            if (!followWeather) return 1f;
            if (environment == null && Time.realtimeSinceStartup >= _nextEnvSearch)
            {
                _nextEnvSearch = Time.realtimeSinceStartup + 2f;   // don't search every frame in scenes without one
                environment = FindAnyObjectByType<G3Environment>();
            }
            if (environment == null) return 1f;
            var mix = environment.StateMix;
            if (mix.Count > 0)
            {
                // fix-215: the whole state mix (no jump when the second and third state swap at a zone edge)
                float sum = 0, f = 0;
                for (int i = 0; i < mix.Count; i++)
                {
                    float w = mix[i].Value;
                    if (w <= 0) continue;
                    string n = mix[i].Key;
                    var st = environment.weather != null ? environment.weather.Resolve(n) : null;
                    f += w * WeatherFactor(st != null ? st.name : n);
                    sum += w;
                }
                if (sum > 0) return f / sum;
            }
            string a = environment.weatherState, b = environment.blendState;
            if (environment.weather != null)
            {
                var sa = environment.weather.Resolve(a); if (sa != null) a = sa.name;
                if (!string.IsNullOrEmpty(b)) { var sb = environment.weather.Resolve(b); if (sb != null) b = sb.name; }
            }
            float fa = WeatherFactor(a);
            return string.IsNullOrEmpty(b) ? fa : Mathf.Lerp(fa, WeatherFactor(b), Mathf.Clamp01(environment.blend));
        }

        /// <summary>Smooth deterministic gust envelope 0..1 at time t (gusts per minute, durations from G3).</summary>
        float GustEnvelope(float t)
        {
            float rate = Mathf.Max(0.01f, gustFrequency) / 60f;                  // gusts per second
            float meanDur = Mathf.Max(0.05f, (gustDuration.x + gustDuration.y) * 0.5f);
            // Perlin noise with period ~1/rate; threshold keeps roughly duty = rate * duration "on"
            float duty = Mathf.Clamp(rate * meanDur, 0.05f, 0.9f);
            float n = Mathf.PerlinNoise(t * rate * 1.7f, 0.37f) * 0.7f + Mathf.PerlinNoise(t * rate * 4.3f, 5.1f) * 0.3f;
            float thr = 1f - duty;
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((n - thr * 0.75f) / Mathf.Max(0.05f, 1f - thr * 0.75f)));
        }

        public void Apply()
        {
            float t = Clock();
            currentTime = t;

            float wf = ResolveWeather();
            if (_weatherSmoothed < 0 || !Application.isPlaying) _weatherSmoothed = wf;
            else _weatherSmoothed = Mathf.MoveTowards(_weatherSmoothed, wf, Time.deltaTime * 0.3f);
            currentWeatherFactor = _weatherSmoothed;

            float k = strengthMultiplier * _weatherSmoothed;
            float baseS = windStrength * k;
            float gustAmp = Mathf.Lerp(gustStrength.x, gustStrength.y, Mathf.PerlinNoise(t * 0.021f, 9.3f)) * Mathf.Sqrt(Mathf.Max(0, k));
            float gust = GustEnvelope(t) * gustAmp;
            float total = baseS + gust;
            currentStrength = total;
            currentGust = gust;

            float az = (azimuth + (Mathf.PerlinNoise(t * 0.013f, 2.7f) * 2f - 1f) * azimuthWander) * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Sin(az), 0, Mathf.Cos(az));

            float exp = Mathf.Max(0.01f, strengthExponent);
            float maxBend = maxBendAngle * Mathf.Deg2Rad * bendScale;
            float steady = maxBend * Mathf.Pow(Mathf.Clamp01(baseS), exp);
            float gustBend = maxBend * (Mathf.Pow(Mathf.Clamp01(total), exp) - Mathf.Pow(Mathf.Clamp01(baseS), exp));
            float sway = maxBend * swayScale * Mathf.Lerp(bendLowHigh.x, bendLowHigh.y, Mathf.Clamp01(total)) * 0.35f;
            float branch = branchAmplitude * (0.25f + Mathf.Clamp01(total));

            float rustle = Mathf.Lerp(leafRustleLowHigh.x, leafRustleLowHigh.y, Mathf.Clamp01(total));
            float leafAmp = leafFlutterScale * rustle * (0.4f + Mathf.Clamp01(total));
            float leafFreq = Mathf.Min(Mathf.Lerp(leafRustleFrequency.x, leafRustleFrequency.y, Mathf.Clamp01(total)) / 60f,
                                       maxLeafFlutterHz) * 2f * Mathf.PI;

            if (timeOverride >= 0) _clouds.Freeze(t, dir.x, dir.z);
            else _clouds.Advance(t, dir.x, dir.z);

            Shader.SetGlobalFloat(TimeId, t);
            if (Time.frameCount != _clockFrame)
            {
                _clockPrev = float.IsNaN(_clockNow) ? t : _clockNow;
                _clockFrame = Time.frameCount;
            }
            _clockNow = t;
            Shader.SetGlobalFloat(TimePrevId, _clockPrev);
            // xz travel (s x unit dir), w = 1: G3/Sky uses it instead of its own dir * _Time fallback
            Shader.SetGlobalVector(CloudDriftId, new Vector4((float)_clouds.X, 0, (float)_clouds.Z, 1));
            Shader.SetGlobalVector(DirId, new Vector4(dir.x, 0, dir.z, total));
            Shader.SetGlobalVector(TreeId, new Vector4(steady, sway, gustBend, branch));
            Shader.SetGlobalVector(LeafId, new Vector4(leafAmp, leafFreq, 0, 0));
            Shader.SetGlobalVector(GrassId, new Vector4(grassBend * baseS, grassSway * (0.4f + Mathf.Clamp01(total)), grassBend * gust, grassFrequency));
            Shader.SetGlobalVector(GustId, new Vector4(2f * Mathf.PI / Mathf.Max(1f, gustWaveLength), gustWaveSpeed, gustContrast, 0));
        }
    }
}
