using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// Remaster desert sand (Docs/remaster-world.md section 22): part of the Ground remaster (WorldParts.Terrain; no row of
    /// its own; json <c>sand</c>, env G3_REMASTER_SAND=0 for A/B). Varant's sand ground layers (class per layer in the v2
    /// copies' <c>_V2Snow.zw</c>, <see cref="SandV2.LayerClass"/>, set here at runtime from G3's image stems and by the
    /// builder) get a grain detail (a tile generated at start on a worker, <see cref="SandDetail"/>), a matte BRDF, wind
    /// ripples across the prevailing wind (G3Wind's base azimuth), a faint sparkle in direct sun, and footprints: the snow
    /// print grid (<see cref="G3SnowV2"/>, which runs while snow or sand is on) takes sand prints on their own wind clock
    /// (<see cref="SandV2.ClockStep"/>: minutes in calm air, seconds in a sandstorm; never persistent). Wind-blown wisps near
    /// the ground in strong wind / sandstorms: <see cref="G3SandWisps"/>. Gated by the desert region (G of the region map):
    /// Myrtana's coast sand and Nordmar look as before. Ticked by G3SnowV2's driver. Off = every global zero.
    /// </summary>
    public static class G3SandV2
    {
        public const string EnvVar = "G3_REMASTER_SAND";

        static readonly int SandId = Shader.PropertyToID("_G3Sand");
        static readonly int Sand2Id = Shader.PropertyToID("_G3Sand2");
        static readonly int Sand3Id = Shader.PropertyToID("_G3Sand3");
        static readonly int Sand4Id = Shader.PropertyToID("_G3Sand4");
        static readonly int Sand5Id = Shader.PropertyToID("_G3Sand5");
        static readonly int Sand6Id = Shader.PropertyToID("_G3Sand6");
        static readonly int RippleId = Shader.PropertyToID("_G3SandRipple");
        static readonly int Sand7Id = Shader.PropertyToID("_G3Sand7");
        static readonly int MicroId = Shader.PropertyToID("_G3SandMicro");
        static readonly int TrailId = Shader.PropertyToID("_G3SandTrail");
        static readonly int Sand8Id = Shader.PropertyToID("_G3Sand8"), Sand9Id = Shader.PropertyToID("_G3Sand9"), Sand10Id = Shader.PropertyToID("_G3Sand10");
        static readonly int Sand11Id = Shader.PropertyToID("_G3Sand11"), Sand12Id = Shader.PropertyToID("_G3Sand12"), Sand13Id = Shader.PropertyToID("_G3Sand13");
        static readonly int NoiseId = Shader.PropertyToID("_G3SandNoise"), NoisePId = Shader.PropertyToID("_G3SandNoiseP");
        static Texture2D _noise;
        static readonly int[] ScanIds =
        {
            Shader.PropertyToID("_G3SandScanFineA"), Shader.PropertyToID("_G3SandScanFineN"), Shader.PropertyToID("_G3SandScanMidA"),
            Shader.PropertyToID("_G3SandScanMidN"), Shader.PropertyToID("_G3SandScanCoarseA"), Shader.PropertyToID("_G3SandScanCoarseN"),
        };
        static readonly int GrainId = Shader.PropertyToID("_G3SandGrain");
        static readonly int AlbedoId = Shader.PropertyToID("_G3SandAlbedo");
        static readonly int V2SnowId = Shader.PropertyToID("_V2Snow");

        static bool? _option;
        static bool _active;
        static double _clock;
        static float _storm, _wind = 0.1f;
        static string _state = "", _blendState = "";
        static float _blend;
        static IReadOnlyList<KeyValuePair<string, float>> _mix;
        static Texture2D _grain, _flat, _ripple;
        static Task<byte[]> _grainJob, _rippleJob;
        static int _sandLayers, _sandMaterials;

        /// <summary>Grain tile resolution (px per side).</summary>
        public const int GrainSize = 1024;

        /// <summary>Env G3_REMASTER_SAND (0 = off for A/B shots); default on (it follows the Ground part).</summary>
        public static bool Option
        {
            get
            {
                if (_option == null)
                    _option = RemasterOptions.Resolve(new[] { Environment.GetEnvironmentVariable(EnvVar) }, true);
                return _option.Value;
            }
        }

        /// <summary>Runner / tools: forces the sand on or off (not stored) and re-applies.</summary>
        public static void SetOption(bool on)
        {
            _option = on;
            Apply(G3WorldRemaster.Parts);
            G3SnowV2.Apply(G3WorldRemaster.Parts);   // the print grid runs while snow or sand is on
            Debug.Log($"[Remaster] sand {(on ? "on" : "off")}: {Describe()}");
        }

        /// <summary>True while the sand terms are applied (option, json sand, world v2 Ground on, play mode).</summary>
        public static bool Active => _active;

        /// <summary>Sand prints are made (sand on, json sandPrints).</summary>
        public static bool PrintsActive => _active && WorldRemasterLook.Current.sandPrints;

        /// <summary>The sand print clock (refill units; wrapped for the GPU by <see cref="SnowV2.Wrapped"/>).</summary>
        public static double Clock => _clock;

        /// <summary>Sandstorm share of the current weather (0..1).</summary>
        public static float Storm => _storm;

        /// <summary>G3Wind strength now (with gusts).</summary>
        public static float Wind => _wind;

        /// <summary>Called by <see cref="G3WorldV2.Apply(WorldParts)"/> with the parts that are on, before <see cref="G3SnowV2.Apply"/>
        /// (which keeps the print grid running while snow or sand is on).</summary>
        public static void Apply(WorldParts parts)
        {
            var c = WorldRemasterLook.Current;
            bool on = Option && c.sand && c.v2 && c.v2Terrain && (parts & WorldParts.Terrain) != 0 && Application.isPlaying;
            if (on == _active) { SetGlobals(); return; }
            _active = on;
            G3Environment.Applied -= OnEnvironment;
            if (on)
            {
                G3Environment.Applied += OnEnvironment;
                SetClasses();
                EnsureGrain();
            }
            else { G3SandWisps.Release(); G3SandDust.Release(); G3SandHaze.Release(); G3SandFx.Release(); G3CoveredMap.Release(); }
            SetGlobals();
        }

        public static string Describe()
        {
            if (!_active) return Option ? "sand off (needs world v2 Ground)" : "sand off (G3_REMASTER_SAND)";
            var set = G3WorldRemaster.Set;
            return $"sand on: {_sandLayers} sand layers in {_sandMaterials} materials, scans {(set != null && set.HasSandScans ? "set" : "none")}, grain {(_grain != null ? GrainSize + " px" : "pending")}, " +
                   $"ripples {(_ripple != null ? SandV2.RippleSize + " px" : "pending")}, storm {_storm:0.00}, wind {_wind:0.00}, print fill {SandV2.FillSeconds(_storm, _wind):0} s, clock {_clock:0.000}, {G3SandWisps.Describe()}, {G3SandHaze.Describe()}";
        }

        static Vector4 V(float[] a) => new Vector4(a[0], a[1], a[2], a[3]);

        static (float x, float z) WindDir()
        {
            var w = G3Wind.Active;
            return SandV2.WindDir(w != null ? w.azimuth : WorldRemasterLook.Current.sandWindAngle);   // the base azimuth: ripples do not wander
        }

        static void SetGlobals()
        {
            var (wx, wz) = WindDir();
            Shader.SetGlobalVector(SandId, V(SandV2.SandGlobal(_active)));
            Shader.SetGlobalVector(Sand2Id, V(SandV2.Sand2Global(wx, wz)));
            Shader.SetGlobalVector(Sand3Id, V(SandV2.Sand3Global));
            Shader.SetGlobalVector(Sand4Id, V(SandV2.Sand4Global));
            // the ripples stay off (strength 0) until their tile is there
            var s5 = SandV2.Sand5Global;
            Shader.SetGlobalVector(Sand5Id, _ripple != null ? V(s5) : new Vector4(s5[0], 0, s5[2], 0));
            Shader.SetGlobalVector(Sand6Id, V(SandV2.Sand6Global));
            var set = G3WorldRemaster.Set;
            var micro = set != null ? set.sandMicro : null;
            Shader.SetGlobalVector(Sand7Id, V(SandV2.Sand7Global(micro != null, set != null ? set.sandMicroSize : 0, set != null ? set.sandMicroHeight : 0)));
            Shader.SetGlobalTexture(MicroId, micro != null ? (Texture)micro : Flat());
            // sand-3 (22.10): the scan-based base set (off = sand-2 look)
            bool scans = set != null && set.HasSandScans;
            var size = scans ? set.sandScanSize : Vector3.zero;
            Shader.SetGlobalVector(Sand8Id, V(SandV2.Sand8Global(scans, size.x, size.y, size.z)));
            Shader.SetGlobalVector(Sand9Id, V(SandV2.Sand9Global));
            Shader.SetGlobalVector(Sand10Id, V(SandV2.Sand10Global(scans ? set.sandScanHeight.x : 0)));
            Shader.SetGlobalVector(Sand11Id, V(SandV2.Sand11Global));
            Shader.SetGlobalVector(Sand12Id, V(SandV2.Sand12Global));
            Shader.SetGlobalVector(Sand13Id, V(SandV2.Sand13Global));
            var noise = Noise();
            Shader.SetGlobalTexture(NoiseId, noise);
            int nt = SandV2.NoiseTileCells * SandV2.NoiseTileRes;
            Shader.SetGlobalVector(NoisePId, new Vector4(SandV2.NoiseTileRes, 1f / nt, SandV2.NoiseTileCells, 1));
            var tex = scans ? new Texture[] { set.sandFineA, set.sandFineN, set.sandMidA, set.sandMidN, set.sandCoarseA, set.sandCoarseN } : null;
            for (int i = 0; i < ScanIds.Length; i++) Shader.SetGlobalTexture(ScanIds[i], tex != null ? tex[i] : Flat());
            Shader.SetGlobalTexture(RippleId, _ripple != null ? (Texture)_ripple : Flat());
            Shader.SetGlobalVector(TrailId, V(SandV2.TrailGlobal(PrintsActive && G3SnowV2.Prints != null, SnowV2.Wrapped(_clock))));
            Shader.SetGlobalTexture(GrainId, _grain != null ? (Texture)_grain : Flat());
        }

        /// <summary>sand-3: the value-noise tile of G3SandNoiseD (512 px RGBAHalf, bilinear, repeat; made once, ~20 ms).</summary>
        static Texture2D Noise()
        {
            if (_noise != null) return _noise;
            int n = SandV2.NoiseTileCells * SandV2.NoiseTileRes;
            var f = SandV2.NoiseTile();
            var h = new ushort[f.Length];
            for (int i = 0; i < f.Length; i++) h[i] = Mathf.FloatToHalf(f[i]);
            _noise = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true)
            {
                name = "G3 Sand Noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave,
            };
            _noise.SetPixelData(h, 0);
            _noise.Apply(false, true);
            return _noise;
        }

        /// <summary>A 1-pixel flat grain (normal up, factor 1) while the tile is generated.</summary>
        static Texture2D Flat()
        {
            if (_flat != null) return _flat;
            _flat = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "G3 Sand Grain (flat)", hideFlags = HideFlags.DontSave };
            _flat.SetPixelData(new byte[] { 128, 128, 128, 128 }, 0);
            _flat.Apply(false, true);
            return _flat;
        }

        static void OnEnvironment(G3Environment env, G3WeatherSample s, float overcast)
        {
            _state = env.weatherState;
            _blendState = env.blendState;
            _blend = env.blend;
            _mix = env.StateMix;
        }

        // ------------------------------------------------------------------ layer classes, grain tile

        /// <summary>
        /// Writes each v2 terrain layer's sand class into <c>_V2Snow.z</c> (layer A) / <c>.w</c> (layer B) from its G3 image
        /// stem (the builder does the same; this covers copies built before section 22, no rebuild needed). Only ground layers
        /// (kind 1); x and y (packed road snow) are kept.
        /// </summary>
        static void SetClasses()
        {
            var set = G3WorldRemaster.Set;
            _sandLayers = _sandMaterials = 0;
            if (set == null || set.v2pairs == null) return;
            Texture albedo = null;
            foreach (var p in set.v2pairs)
            {
                var m = p.v2;
                if (m == null || !m.HasProperty(V2SnowId)) continue;
                int a = ClassOf(m, "_BaseMap", "_V2LayerA"), b = ClassOf(m, "_BaseMap2", "_V2LayerB");
                var v = m.GetVector(V2SnowId);
                if (Mathf.Approximately(v.z, a) && Mathf.Approximately(v.w, b)) { Count(a, b); continue; }
                m.SetVector(V2SnowId, new Vector4(v.x, v.y, a, b));
                Count(a, b);
                if (albedo == null && a == SandV2.Loose) albedo = m.GetTexture("_BaseMap");
            }
            if (albedo == null)
                foreach (var p in set.v2pairs)
                    if (p.v2 != null && p.v2.HasProperty("_BaseMap") && ClassOf(p.v2, "_BaseMap", "_V2LayerA") == SandV2.Loose) { albedo = p.v2.GetTexture("_BaseMap"); break; }
            // G3's own loose sand image: the wisps take its mean colour (mip 15) - colour locked to G3's art
            Shader.SetGlobalTexture(AlbedoId, albedo != null ? albedo : Texture2D.whiteTexture);
        }

        static void Count(int a, int b)
        {
            int n = (a != 0 ? 1 : 0) + (b != 0 ? 1 : 0);
            _sandLayers += n;
            if (n > 0) _sandMaterials++;
        }

        static int ClassOf(Material m, string baseMap, string layer)
        {
            if (!m.HasProperty(baseMap) || !m.HasProperty(layer)) return 0;
            float kind = m.GetVector(layer).z;
            if (kind < 0.5f || kind > 1.5f) return 0;   // ground layers only
            var t = m.GetTexture(baseMap);
            return t != null ? SandV2.LayerClass(t.name) : 0;
        }

        /// <summary>
        /// fx-1: loose-sand share (0/1) of a terrain material's layers A and B by G3's image stems (works on G3's material and
        /// on its v2 copy; a v2 layer that is not a ground layer is not sand; the debug layer swap is folded in). Read by the
        /// relief for its loose-sand mask (<see cref="ReliefGroundMap.Sand"/>: where the streamers may spawn).
        /// </summary>
        public static (float a, float b) LooseLayers(Material m)
        {
            if (m == null || !m.HasProperty("_BaseMap")) return (0, 0);
            float a = LooseOf(m, "_BaseMap", "_V2LayerA");
            float b = m.HasProperty("_BaseMap2") && m.GetTexture("_BaseMap2") != null ? LooseOf(m, "_BaseMap2", "_V2LayerB") : a;
            if (m.HasProperty("_SwapLayers") && m.GetFloat("_SwapLayers") > 0.5f) (a, b) = (b, a);
            return (a, b);
        }

        static float LooseOf(Material m, string baseMap, string layer)
        {
            if (m.HasProperty(layer))
            {
                float kind = m.GetVector(layer).z;
                if (kind < 0.5f || kind > 1.5f) return 0;   // v2: ground layers only
            }
            var t = m.GetTexture(baseMap);
            return t != null && SandV2.Ripples(SandV2.LayerClass(t.name)) ? 1f : 0f;
        }

        static void EnsureGrain()
        {
            if (_grain != null || _grainJob != null) return;
            _grainJob = Task.Run(() => SandDetail.Generate(GrainSize, 0x5A4D5u));   // ~0.2-0.5 s on a worker
            if (_ripple == null && _rippleJob == null)
                _rippleJob = Task.Run(() => SandRipples.Generate(SandV2.RippleSize, 0x51BB1u));   // Swift-Hohenberg, ~1-3 s on workers
        }

        static void FinishRipple()
        {
            if (_rippleJob == null || !_rippleJob.IsCompleted) return;
            var job = _rippleJob;
            _rippleJob = null;
            if (job.IsFaulted || job.Result == null) { Debug.LogWarning($"[G3SandV2] ripple tile failed: {job.Exception?.GetBaseException().Message}"); return; }
            int n = SandV2.RippleSize;
            _ripple = new Texture2D(n, n, TextureFormat.RGBA32, true, true)
            {
                name = "G3 Sand Ripples", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8, hideFlags = HideFlags.DontSave,
            };
            _ripple.SetPixelData(job.Result, 0);
            _ripple.Apply(true, true);   // box mips: the slopes average out to flat at range (no shimmer)
            Shader.SetGlobalTexture(RippleId, _ripple);
        }

        static void FinishGrain()
        {
            if (_grainJob == null || !_grainJob.IsCompleted) return;
            var job = _grainJob;
            _grainJob = null;
            if (job.IsFaulted || job.Result == null) { Debug.LogWarning($"[G3SandV2] grain tile failed: {job.Exception?.GetBaseException().Message}"); return; }
            _grain = new Texture2D(GrainSize, GrainSize, TextureFormat.RGBA32, true, true)
            {
                name = "G3 Sand Grain", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8, hideFlags = HideFlags.DontSave,
            };
            _grain.SetPixelData(job.Result, 0);
            _grain.Apply(true, true);   // box mips: the albedo factor stays 1 and the normals flatten at range
            Shader.SetGlobalTexture(GrainId, _grain);
        }

        // ------------------------------------------------------------------ per frame (G3SnowV2's driver)

        /// <summary>Wind clock of the prints, the grain upload, globals and the wisps; called by G3SnowV2 every frame while sand is on.</summary>
        internal static void Tick(float dt, Camera cam)
        {
            if (!_active) return;
            _storm = _mix != null && _mix.Count > 0 ? SandV2.StormShare(_mix) : SandV2.StormShare(_state, _blendState, _blend);
            var w = G3Wind.Active;
            _wind = w != null ? w.currentStrength : 0.1f;
            _clock = SandV2.ClockStep(_clock, _storm, _wind, dt);
            FinishGrain();
            FinishRipple();
            SetGlobals();
            if (cam != null && G3SnowV2.DesertAt(cam.transform.position.x, cam.transform.position.z) > 0.01f) G3CoveredMap.Tick(cam);   // fx-1: roofs
            G3SandWisps.Tick(cam, SandV2.WispAmount(_wind, _storm));
            G3SandDust.Tick(cam);
            G3SandHaze.Tick(cam);
        }

        /// <summary>End of Play / driver gone.</summary>
        internal static void Shutdown()
        {
            G3SandWisps.Release();
            G3SandDust.Release();
            G3SandHaze.Release();
            G3SandFx.Release();
            G3CoveredMap.Release();
            _active = false;
            G3Environment.Applied -= OnEnvironment;
            Shader.SetGlobalVector(SandId, Vector4.zero);
            Shader.SetGlobalVector(TrailId, Vector4.zero);
            Shader.SetGlobalVector(Sand5Id, Vector4.zero);
            Shader.SetGlobalVector(Sand8Id, Vector4.zero);
            Shader.SetGlobalVector(Sand11Id, Vector4.zero);
            Shader.SetGlobalVector(Sand10Id, Vector4.zero);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _option = null; _active = false; _clock = 0; _storm = 0; _wind = 0.1f; _mix = null;
            _grain = null; _flat = null; _noise = null; _grainJob = null; _ripple = null; _rippleJob = null; _sandLayers = _sandMaterials = 0;
            G3Environment.Applied -= OnEnvironment;
        }
    }
}
