using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// World remaster v2 look (Docs/remaster-world.md section 10), on per world part (fix-71, 10.15.3) while
    /// <c>G3WorldLook.json</c> has <c>"v2": true</c>; <see cref="G3WorldRemasterLook"/> switches it. Lighting part: sun/ambient,
    /// shadows, fog/shafts, v2 clouds; Terrain/Rocks/Buildings: the v2 material globals and wetness; Grass: the dense grass
    /// and grass translucency; Trees: the SpeedTree foliage terms. G3's day cycle and weather
    /// stay the inputs (<see cref="G3Environment.Applied"/>): v2 re-balances sun against ambient (stronger direct light,
    /// darker crisp shadows in clear weather, G3's soft light when overcast), pulls the sun hue towards the air-mass colour,
    /// extends the shadows (150 m, 4096, practical cascade splits), adds height fog with ray-marched sun shafts
    /// (Hidden/G3/Remaster/Volumetrics, two render-graph passes), the volumetric cloud slab in G3/Sky, wetness from G3's
    /// rain, and sets the globals of the v2 materials (G3/Remaster/WorldV2), the grass (<see cref="G3GrassV2"/>) and the
    /// SpeedTree foliage terms. Every feature has its own JSON toggle; maths in <see cref="WorldV2"/> (tested).
    /// Off = every global zero, no passes, URP shadow settings restored, G3Environment's values untouched.
    /// </summary>
    public static class G3WorldV2
    {
        static readonly int SurfaceId = Shader.PropertyToID("_G3V2Surface");
        static readonly int Surface2Id = Shader.PropertyToID("_G3V2Surface2");
        static readonly int WeatherId = Shader.PropertyToID("_G3V2Weather");
        // fix-255: G3/Sky's colours (G3Environment sets them every frame from the weather keys)
        static readonly int[] SkyColorIds = { Shader.PropertyToID("_ZenithColor"), Shader.PropertyToID("_HorizonColor"), Shader.PropertyToID("_FogColor"), Shader.PropertyToID("_CloudColor") };
        static readonly int DebugId = Shader.PropertyToID("_G3V2Debug");
        static readonly int FarId = Shader.PropertyToID("_G3V2Far");
        static readonly int CloudsId = Shader.PropertyToID("_G3V2Clouds");
        static readonly int Clouds2Id = Shader.PropertyToID("_G3V2Clouds2");
        static readonly int FoliageId = Shader.PropertyToID("_G3V2Foliage");
        static readonly int FogId = Shader.PropertyToID("_G3V2Fog");
        static readonly int ShaftId = Shader.PropertyToID("_G3V2Shaft");
        static readonly int FogAmbId = Shader.PropertyToID("_G3V2FogAmb");
        static readonly int StreakFogId = Shader.PropertyToID("_G3StreakFog"), StreakFogAmbId = Shader.PropertyToID("_G3StreakFogAmb");
        static readonly int VolTexId = Shader.PropertyToID("_G3V2VolTex");
        static readonly int CurVolId = Shader.PropertyToID("_G3V2CurVol");
        static readonly int HistVolId = Shader.PropertyToID("_G3V2HistVol");
        static readonly int TemporalId = Shader.PropertyToID("_G3V2Temporal");
        static readonly int PrevVPId = Shader.PropertyToID("_G3V2PrevVP");
        static readonly int LowTexelId = Shader.PropertyToID("_G3V2LowTexel");
        static readonly int ColorId = Shader.PropertyToID("_G3V2Color");
        static readonly int Color2Id = Shader.PropertyToID("_G3V2Color2");
        static readonly int AiId = Shader.PropertyToID("_G3V2Ai");
        static readonly int ArchId = Shader.PropertyToID("_G3V2Arch");   // arch-1 (25): Varant architecture terms
        static readonly int ArchSandId = Shader.PropertyToID("_G3V2ArchSand");   // arch-2 (25.7): storm sand deposit on cloth / ledges
        static float _archSand = float.NaN, _archSandHour = float.NaN;
        static readonly int Arch2Id = Shader.PropertyToID("_G3V2Arch2");   // arch-3 (26): Myrtana moss/dirt, Nordmar frost
        static readonly int MotionVectorsId = Shader.PropertyToID("_G3MotionVectors");

        /// <summary>Per-camera volumetric history (temporal blend at quarter resolution, runner-30 GPU).</summary>
        sealed class History
        {
            public RTHandle a, b;
            public bool valid;
            public Matrix4x4 prevVP;
            public int w, h;
        }
        static readonly Dictionary<Camera, History> _history = new Dictionary<Camera, History>();
        static int _frame;

        static bool _on;            // any v2 part
        static bool _light;         // Lighting part (sun, shadows, volumetrics, clouds)
        static bool _surfaces;      // Terrain / Rocks / Buildings (v2 material globals, wetness)
        static WorldParts _parts;
        static float _wet, _wetTarget, _desert;
        static float _aiSat;                // AI rock saturation of the current region (pass 6), eased
        static G3WorldStreamer _streamer;
        static float _streamerLookup;
        static float _regionLookup;
        static string _region;
        static float _groundY = float.NaN;
        static Material _volMat;
        static VolumetricPass _volPass;
        static ShadowRestore _shadowRestore;

        static int _taaMode;        // 0 off, 1 TAA, 2 STP (section 11.7)
        static UniversalRenderPipelineAsset _stpAsset;
        static UpscalingFilterSelection _stpRestore;
        static int _taaWanted;
        static bool _stpGuardHooked, _stpFallbackLogged;

        /// <summary>
        /// fix-168: the URP asset's filter without the remaster's STP switch (what the options page must treat as the
        /// asset's own value), or <paramref name="current"/> when the remaster does not hold the asset.
        /// </summary>
        public static UpscalingFilterSelection OwnUpscalingFilter(UniversalRenderPipelineAsset asset, UpscalingFilterSelection current) =>
            asset != null && asset == _stpAsset ? _stpRestore : current;

        /// <summary>fix-168: true while the remaster's STP holds this asset (its filter should be STP at 100 % scale).</summary>
        public static bool HoldsStp(UniversalRenderPipelineAsset asset) => asset != null && asset == _stpAsset;

        /// <summary>
        /// Motion-vector mode of the instanced / indirect plants (G3VegetationRenderer, G3GrassV2): Object while the remaster
        /// TAA runs (their MotionVectors passes write the wind motion), else ForceNoMotion as before.
        /// </summary>
        public static MotionVectorGenerationMode PlantMotionMode => _taaMode > 0 ? MotionVectorGenerationMode.Object : MotionVectorGenerationMode.ForceNoMotion;

        /// <summary>The remaster's temporal AA: 0 off (options' FXAA / none), 1 URP TAA, 2 STP.</summary>
        public static int TaaMode => _taaMode;

        /// <summary>True while the v2 layer is applied.</summary>
        public static bool Active => _on;

        /// <summary>atmo-1: the v2 layer's Lighting part is applied (sun, shadows, volumetrics).</summary>
        public static bool LightingOn => _on && _light;

        /// <summary>atmo-1: v2 holds the URP shadow settings (Lighting part with v2Shadows).</summary>
        public static bool ShadowsOn => _on && _light && _shadowRestore != null;

        /// <summary>Current wetness (0..1) from G3's rain.</summary>
        public static float Wetness => _wet;

        /// <summary>Turns the whole v2 layer on or off.</summary>
        public static void Apply(bool on) => Apply(on ? WorldParts.All : WorldParts.None);

        /// <summary>Applies the v2 layer for the given world parts ("v2": false in the JSON = everything off).</summary>
        public static void Apply(WorldParts parts)
        {
            var c = WorldRemasterLook.Current;
            if (!c.v2) parts = WorldParts.None;
            bool on = parts != WorldParts.None;
            if (!on && !_on) { SetGlobals(); SetTaa(0); G3WorldV2Light.Apply(false); G3SkyReflection.Apply(false); G3CloudShadow.Apply(false); G3SandV2.Apply(WorldParts.None); G3SnowV2.Apply(WorldParts.None); G3GroundScatter.Apply(false); G3DesertAtmo.Apply(false); return; }
            _on = on;
            _parts = parts;
            _light = (parts & WorldParts.Lighting) != 0;
            _surfaces = (parts & WorldParts.Surfaces) != 0;
            G3Environment.Applied -= OnEnvironment;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            SetShadows(_light && c.v2Shadows);
            SetGlobals();
            if (_light || _surfaces) G3Environment.Applied += OnEnvironment;   // sun/ambient (lighting), wetness (materials)
            if (_light && (c.v2Fog || c.v2Shafts))
            {
                if (_volMat == null)
                {
                    var sh = Resources.Load<Shader>("G3RemasterVolumetrics");
                    if (sh != null) _volMat = new Material(sh) { name = "G3 Remaster Volumetrics", hideFlags = HideFlags.DontSave };
                }
                if (_volMat != null)
                {
                    _volPass ??= new VolumetricPass(_volMat);
                    RenderPipelineManager.beginCameraRendering += OnBeginCamera;
                }
            }
            SetTaa(_light ? c.v2Taa : 0);
            G3WorldV2Light.Apply(_light);   // remaster-look: sky occlusion + contact shadows (section 11)
            G3SkyReflection.Apply(_light);  // remaster-look2: sky reflection cube (11.9)
            G3CloudShadow.Apply(_light);    // remaster-gi: cloud shadow map (16.4)
            G3GrassV2.SetEnabled((parts & WorldParts.Grass) != 0 && c.v2Grass);
            G3GroundScatter.Apply((parts & WorldParts.Grass) != 0 && c.scatter);   // remaster-veg (section 18): ground cover
            G3SandV2.Apply(parts);          // remaster-sand (section 22): Varant sand on the Ground part (before snow: shared print grid)
            G3SnowV2.Apply(parts);          // remaster-snow (section 12): snow terms on the v2 surfaces + trail map
            G3TreeV2.Refresh();   // v2 models: v2 on + json v2TreeReplace only (RemasterOptions.TreesWanted, trees-v3)
            G3DesertAtmo.Apply(_light);   // atmo-1 (section 23): after v2 subscribed to G3Environment.Applied (runs after it)
        }

        /// <summary>One line for the runner: what is on and the frame estimate.</summary>
        public static string Describe()
        {
            var c = WorldRemasterLook.Current;
            if (!_on) return "v2 off" + (c.v2 ? " (world remaster off)" : " (json v2 false)");
            bool trees = (_parts & WorldParts.Trees) != 0, grass = (_parts & WorldParts.Grass) != 0;
            var parts = new List<string>();
            void P(bool b, string n) { if (b) parts.Add(n); }
            P(_light && c.v2Sun, $"sun x{c.v2SunIntensity:0.##}/amb x{c.v2AmbientScale:0.##}");
            P(_light && c.v2Shadows, $"shadows {c.v2ShadowDistance:0}m/{c.v2ShadowResolution}");
            P(_light && c.v2Ssao, "ssao"); P(_light && c.v2Fog, "fog"); P(_light && c.v2Shafts, $"shafts {c.v2ShaftSteps}"); P(_light && c.v2Clouds, $"clouds {c.v2CloudSteps}");
            P(_light && G3SkyReflection.Active, G3SkyReflection.Describe());
            P(_light && G3CloudShadow.Active, G3CloudShadow.Describe()); P(_light && G3WorldGi.Active, G3WorldGi.Describe());
            P(_light && _taaMode > 0, (_taaMode == 2 ? "stp" : "taa") + $" (mv {c.v2MotionDistance:0} m)");
            P(_light && G3DesertAtmo.Active, G3DesertAtmo.Describe());
            P(_light && c.v2Shoulder, c.v2HighlightDesat > 0 ? $"shoulder+white {c.v2HighlightDesat:0.##}" : "shoulder"); P(_light && G3WorldV2Light.Active, G3WorldV2Light.Describe()); P((_parts & WorldParts.Terrain) != 0 && c.v2Terrain, "terrain");
            P((_parts & (WorldParts.Rocks | WorldParts.Buildings)) != 0 && c.v2Surfaces, "surfaces"); P(_surfaces && c.v2Pbr, "pbr");
            P(_surfaces && c.v2Stochastic, "hex"); P(_surfaces && c.v2Parallax, "pom"); P(_surfaces && c.v2Moss, "moss");
            P(grass && c.v2Grass, $"grass x{c.v2GrassDensity:0.#} ({G3GrassV2.Describe()})");
            P(grass && c.scatter, G3GroundScatter.Describe());
            P((trees || grass) && c.v2Foliage, "foliage"); P(G3SnowV2.Active, G3SnowV2.Describe()); P(G3SandV2.Active, G3SandV2.Describe()); P(c.v2TreeReplace || G3TreeV2.Enabled, $"trees ({G3TreeV2.Describe()})");
            return $"v2 on ({WorldRemasterParts.Describe(_parts)}): {string.Join(", ", parts)}; wet {_wet:0.##} (target {_wetTarget:0.##}, desert {_desert:0.##}); estimated Faring frame {WorldV2.EstimatedFrameMs():0.0} ms";
        }

        static Vector4 V(float[] a) => new Vector4(a[0], a[1], a[2], a[3]);

        /// <summary>Region at the streaming centre (Myrtana / Varant / Nordmar; null outside Play / without a streamer).</summary>
        static string CurrentRegion()
        {
            if (_streamer == null && Time.realtimeSinceStartup >= _streamerLookup)
            {
                _streamerLookup = Time.realtimeSinceStartup + 2f;   // looked up at most every 2 s while there is none
                _streamer = UnityEngine.Object.FindAnyObjectByType<G3WorldStreamer>();
            }
            // the manifest at the streaming centre, not CurrentRegion: that one only moves with the region weather switch
            // (a pinned weather - scenario "weather Sunny" - left it at Myrtana in Varant)
            if (_streamer == null) return null;
            if (Time.realtimeSinceStartup >= _regionLookup)
            {
                _regionLookup = Time.realtimeSinceStartup + 0.5f;
                _region = _streamer.manifest != null ? _streamer.manifest.RegionAt(_streamer.Centre) ?? _region : _streamer.CurrentRegion;
            }
            return _region;
        }

        /// <summary>The globals of the parts applied now (all zero when off): material globals with Terrain/Rocks/Buildings,
        /// clouds with Lighting, foliage per Trees/Grass (<see cref="WorldV2.FoliageGlobalFor"/>).</summary>
        static void SetGlobals()
        {
            bool m = _on && _surfaces;
            Shader.SetGlobalVector(SurfaceId, m ? V(WorldV2.SurfaceGlobal) : Vector4.zero);
            Shader.SetGlobalVector(Surface2Id, m ? V(WorldV2.Surface2Global) : Vector4.zero);
            Shader.SetGlobalVector(WeatherId, m ? V(WorldV2.WeatherGlobal(WorldV2.ShownWetness(_wet, _desert))) : Vector4.zero);
            Shader.SetGlobalVector(DebugId, m ? V(WorldV2.DebugGlobal) : Vector4.zero);
            Shader.SetGlobalVector(FarId, V(WorldV2.FarGlobal));
            Shader.SetGlobalVector(CloudsId, _on && _light ? V(WorldV2.CloudsGlobal) : Vector4.zero);
            Shader.SetGlobalVector(Clouds2Id, _on && _light ? V(WorldV2.Clouds2Global) : Vector4.zero);
            Shader.SetGlobalVector(FoliageId, _on ? V(WorldV2.FoliageGlobalFor((_parts & WorldParts.Trees) != 0, (_parts & WorldParts.Grass) != 0)) : Vector4.zero);
            Shader.SetGlobalVector(ColorId, m ? V(WorldV2.ColorGlobal) : Vector4.zero);
            Shader.SetGlobalVector(Color2Id, m ? V(WorldV2.Color2Global) : Vector4.zero);
            if (_aiSat <= 0) _aiSat = WorldV2.AiSaturationFor(CurrentRegion());
            Shader.SetGlobalVector(AiId, m ? V(WorldV2.AiGlobal(_aiSat)) : Vector4.zero);
            Shader.SetGlobalVector(ArchId, m ? V(WorldV2.ArchGlobal) : Vector4.zero);
            Shader.SetGlobalVector(ArchSandId, m ? V(ArchSand.Global(float.IsNaN(_archSand) ? WorldRemasterLook.Current.archSandRest : _archSand)) : Vector4.zero);
            Shader.SetGlobalVector(Arch2Id, m ? V(WorldV2.ArchGlobal2) : Vector4.zero);
            if (!(_on && _light)) ReleaseHistory();
        }

        // ------------------------------------------------------------------ lighting on top of G3Environment

        static void OnEnvironment(G3Environment env, G3WeatherSample s, float overcast)
        {
            if (!_on || !Application.isPlaying) return;
            var c = WorldRemasterLook.Current;

            // wetness follows G3's rain state (dries slowly)
            // fix-239: only rain that falls (G3's > 50 % rule) wets; the desert share dries fast and never shows wet
            _wetTarget = WorldV2.WetTarget(env.StateMix) ?? WorldV2.WetTarget(env.weatherState, env.blendState, env.blend);   // fix-215: whole mix
            _desert = WorldV2.DesertShare(env.StateMix);
            _wet = WorldV2.WetStep(_wet, _wetTarget, Time.deltaTime, _desert);
            if (_surfaces) Shader.SetGlobalVector(WeatherId, V(WorldV2.WeatherGlobal(WorldV2.ShownWetness(_wet, _desert))));
            if (_surfaces)
            {
                // pass 6: AI rock saturation per region (the streamer's current region; eased, no pop at the border)
                _aiSat = WorldV2.AiSaturationStep(_aiSat, WorldV2.AiSaturationFor(CurrentRegion()), Time.deltaTime);
                Shader.SetGlobalVector(AiId, V(WorldV2.AiGlobal(_aiSat)));
                TickArchSand(env);
            }

            if (!_light || !c.v2Sun) return;
            RainNeutralize(env, overcast);
            var (sunK, ambK, fill) = WorldV2.LightBalance(overcast);
            var sun = env.sun;
            if (sun != null)
            {
                float elev = Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1, 1)) * Mathf.Rad2Deg;
                var col = sun.color;
                var sc = WorldV2.SunColor(col.r, col.g, col.b, elev);
                sun.color = new Color(sc[0], sc[1], sc[2], 1);
                sun.intensity *= sunK;
                sun.shadowStrength = WorldV2.ShadowStrength(sun.shadowStrength, overcast);
            }
            if (env.applyAmbient)
            {
                var sh = RenderSettings.ambientProbe;
                sh *= ambK;
                if (fill > 0)
                {
                    // cool sky fill from above: part of the removed flat ambient comes back directional (shade stays blue, not black)
                    float dc = (sh[0, 0] + sh[1, 0] + sh[2, 0]) / 3f / Mathf.Max(ambK, 0.05f);   // G3's ambient level (DC term)
                    var lin = ((Color)s.skyColor).linear;
                    float lum = Mathf.Max(0.0001f, 0.2126f * lin.r + 0.7152f * lin.g + 0.0722f * lin.b);
                    sh.AddDirectionalLight(Vector3.up, new Color(lin.r / lum, lin.g / lum, lin.b / lum), dc * fill * 2f);
                }
                RenderSettings.ambientProbe = sh;
            }
        }

        /// <summary>
        /// fix-255: G3's overcast keys (Rainy, Foggy) are cyan; under the remaster's sky light and exposure the rain forest went
        /// teal. Sun, ambient probe, fog and G3/Sky's colours are pulled to a neutral-cool grey (<see cref="WorldV2.RainNeutral"/>,
        /// luminance kept). G3Environment re-sets all of them every frame before this runs (no accumulation); remaster off
        /// keeps G3's own look.
        /// </summary>
        static void RainNeutralize(G3Environment env, float overcast)
        {
            float k = WorldV2.RainNeutralK(overcast);
            if (k <= 0.001f) return;
            var sun = env.sun;
            if (sun != null) sun.color = RainNeutralGamma(sun.color, k);
            if (env.applyAmbient)
            {
                var sh = RenderSettings.ambientProbe;
                var f = WorldV2.RainProbeScale(sh[0, 0], sh[1, 0], sh[2, 0], k);
                for (int ch = 0; ch < 3; ch++)
                    for (int i = 0; i < 9; i++) sh[ch, i] *= f[ch];
                RenderSettings.ambientProbe = sh;
                RenderSettings.ambientSkyColor = RainNeutralGamma(RenderSettings.ambientSkyColor, k);
                RenderSettings.ambientEquatorColor = RainNeutralGamma(RenderSettings.ambientEquatorColor, k);
                RenderSettings.ambientGroundColor = RainNeutralGamma(RenderSettings.ambientGroundColor, k);
            }
            if (env.applyFog) RenderSettings.fogColor = RainNeutralGamma(RenderSettings.fogColor, k);
            var sky = RenderSettings.skybox;
            if (env.applySky && sky != null)
                foreach (int id in SkyColorIds)
                    if (sky.HasProperty(id))
                    {
                        var v = sky.GetVector(id);   // linear
                        var n = WorldV2.RainNeutral(v.x, v.y, v.z, k);
                        sky.SetVector(id, new Vector4(n[0], n[1], n[2], v.w));
                    }
        }

        /// <summary>fix-255: <see cref="WorldV2.RainNeutral"/> on a gamma colour (through linear).</summary>
        static Color RainNeutralGamma(Color gamma, float k)
        {
            var l = gamma.linear;
            var n = WorldV2.RainNeutral(l.r, l.g, l.b, k);
            var o = new Color(Mathf.Max(n[0], 0), Mathf.Max(n[1], 0), Mathf.Max(n[2], 0), 1).gamma;
            o.a = gamma.a;
            return o;
        }

        /// <summary>
        /// arch-2 (25.7): the storm sand deposit in game time (<see cref="ArchSand"/>): continuous clock steps tick it, a jump
        /// (load, sleep, teleport, a set hour) derives it from the weather now; sand wisps off the tent edges when it is high and windy.
        /// </summary>
        static void TickArchSand(G3Environment env)
        {
            float storm = env.StateMix != null && env.StateMix.Count > 0 ? SandV2.StormShare(env.StateMix) : SandV2.StormShare(env.weatherState, env.blendState, env.blend);
            float dh = ArchSand.HoursBetween(_archSandHour, env.timeOfDay);
            _archSand = float.IsNaN(_archSand) || float.IsNaN(dh) ? ArchSand.Initial(storm) : ArchSand.Step(_archSand, storm, dh);
            _archSandHour = env.timeOfDay;
            float shown = ArchSand.Shown(_archSand);
            Shader.SetGlobalVector(ArchSandId, V(ArchSand.Global(_archSand)));
            G3ArchSandWisps.Tick(Camera.main, ArchSand.WispAmount(shown, G3SandV2.Wind, G3SandV2.Storm) * (WorldRemasterLook.Current.arch ? 1 : 0));
        }

        /// <summary>The live storm sand deposit (NaN before the first environment tick).</summary>
        public static float ArchSandDeposit => _archSand;

        // ------------------------------------------------------------------ temporal AA (section 11.7)

        static void SetTaa(int mode)
        {
            var c = WorldRemasterLook.Current;
            _taaWanted = mode;
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            // STP only at 100 % render scale with the asset's own filter (an upscaler picked on the options page wins)
            if (mode == 2 && (urp == null || (_stpAsset == null && (urp.renderScale < 0.999f || urp.upscalingFilter == UpscalingFilterSelection.FSR)))) mode = 1;
            // fix-168: STP without its compute shaders (stripped player) or after a render failure -> URP TAA
            if (mode == 2 && !G3StpGuard.Usable)
            {
                if (!_stpFallbackLogged) { _stpFallbackLogged = true; Debug.LogWarning("[G3WorldV2] STP unusable (" + G3StpGuard.Reason + "): remaster AA uses URP TAA"); }
                mode = 1;
            }
            if (mode != 2 && _stpAsset != null)
            {
                // only undo our own switch: an upscaler set meanwhile (options page) stays
                if (_stpAsset.upscalingFilter == UpscalingFilterSelection.STP && (_stpAsset.renderScale >= 0.999f || !G3StpGuard.Usable)) _stpAsset.upscalingFilter = _stpRestore;
                _stpAsset = null;
            }
            if (mode == 2 && _stpAsset == null) { _stpAsset = urp; _stpRestore = urp.upscalingFilter; urp.upscalingFilter = UpscalingFilterSelection.STP; }
            if (!_stpGuardHooked) { _stpGuardHooked = true; G3StpGuard.Failed += () => { if (_taaMode == 2) SetTaa(_taaWanted); }; }
            _taaMode = mode;
            var t = TemporalAA.Settings.Create();
            t.quality = TemporalAAQuality.High;
            t.baseBlendFactor = c.v2TaaBlend;
            t.varianceClampScale = c.v2TaaClamp;
            t.contrastAdaptiveSharpening = c.v2TaaSharpen;
            G3CameraPost.SetRemasterTaa(mode > 0, t);
            Shader.SetGlobalVector(MotionVectorsId, new Vector4(mode > 0 ? c.v2MotionDistance : 0, 0, 0, 0));
        }

        // ------------------------------------------------------------------ shadows (URP asset, restored when off)

        sealed class ShadowRestore
        {
            public UniversalRenderPipelineAsset asset;
            public float distance;
            public int cascades, resolution;
            public Vector3 split4;
            public Vector2 split3;
            public float split2;
            public Vector4 smallMesh;   // fix-171: GRD shadow small-mesh culling (lighting quality)
            public bool smallMeshSet;
        }

        static void SetShadows(bool on)
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (!on)
            {
                if (_shadowRestore != null && _shadowRestore.asset != null)
                {
                    var r = _shadowRestore;
                    r.asset.shadowDistance = r.distance;
                    r.asset.shadowCascadeCount = r.cascades;
                    r.asset.cascade4Split = r.split4;
                    r.asset.cascade3Split = r.split3;
                    r.asset.cascade2Split = r.split2;
                    r.asset.mainLightShadowmapResolution = r.resolution;
                    if (r.smallMeshSet) r.asset.shadowSmallMeshScreenPercentages = r.smallMesh;
                }
                _shadowRestore = null;
                return;
            }
            if (urp == null) return;
            if (_shadowRestore == null)
                _shadowRestore = new ShadowRestore
                {
                    asset = urp, distance = urp.shadowDistance, cascades = urp.shadowCascadeCount, resolution = urp.mainLightShadowmapResolution,
                    split4 = urp.cascade4Split, split3 = urp.cascade3Split, split2 = urp.cascade2Split,
                };
            var c = WorldRemasterLook.Current;
            urp.shadowDistance = c.v2ShadowDistance;
            urp.shadowCascadeCount = c.v2ShadowCascades;
            var sp = WorldV2.CascadeSplits(Mathf.Max(0.3f, Camera.main != null ? Camera.main.nearClipPlane : 0.3f), c.v2ShadowDistance, c.v2ShadowCascades, c.v2ShadowSplitLambda);
            if (sp.Length == 3) urp.cascade4Split = new Vector3(sp[0], sp[1], sp[2]);
            else if (sp.Length == 2) urp.cascade3Split = new Vector2(sp[0], sp[1]);
            else if (sp.Length == 1) urp.cascade2Split = sp[0];
            urp.mainLightShadowmapResolution = c.v2ShadowResolution;
            // fix-171 (lighting quality): small casters culled from the cascades (the 90 m cascades' CPU cost is the casters)
            if (c.v2ShadowSmallMesh >= 0)
            {
                if (!_shadowRestore.smallMeshSet) { _shadowRestore.smallMesh = urp.shadowSmallMeshScreenPercentages; _shadowRestore.smallMeshSet = true; }
                var want = Vector4.Max(_shadowRestore.smallMesh, Vector4.one * c.v2ShadowSmallMesh);
                if (urp.shadowSmallMeshScreenPercentages != want) urp.shadowSmallMeshScreenPercentages = want;
            }
            else if (_shadowRestore.smallMeshSet)
            {
                if (urp.shadowSmallMeshScreenPercentages != _shadowRestore.smallMesh) urp.shadowSmallMeshScreenPercentages = _shadowRestore.smallMesh;
                _shadowRestore.smallMeshSet = false;
            }
        }

        // ------------------------------------------------------------------ volumetrics

        static readonly global::Unity.Profiling.ProfilerMarker PmBeginCamera = new global::Unity.Profiling.ProfilerMarker("G3WorldV2.BeginCamera");
        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            using var _pm = PmBeginCamera.Auto();   // snow3 perf
            if (!_on || !_light || _volMat == null) return;
            if (cam.cullingMask == 0) return; // fix-137: the title's empty camera (TitleStage): no fog/aerial over black (purple start-up screen)
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null || data.renderType != CameraRenderType.Base) return;
            var c = WorldRemasterLook.Current;

            // fog base: the ground under the camera (smoothed), so the fog lies in the valleys wherever the camera is
            var p = cam.transform.position;
            if (cam.cameraType == CameraType.Game && Physics.Raycast(p + Vector3.up * 2, Vector3.down, out var hit, 800f, ~0, QueryTriggerInteraction.Ignore))
                _groundY = float.IsNaN(_groundY) ? hit.point.y : Mathf.Lerp(_groundY, hit.point.y, 0.05f);
            float baseY = (float.IsNaN(_groundY) ? p.y - 2 : _groundY) + c.v2FogBaseOffset;
            float density = c.v2Fog ? WorldV2.FogDensity(RenderSettings.fogEndDistance) : 0;
            if (c.v2Shafts && density <= 0) density = c.v2FogDensity;   // shafts need a medium even with the fog toggle off
            _volMat.SetVector(FogId, new Vector4(density, c.v2FogFalloff, baseY, c.v2FogMax));
            _volMat.SetVector(ShaftId, new Vector4(c.v2Shafts ? c.v2ShaftIntensity : 0, c.v2ShaftAnisotropy, c.v2ShaftSteps, c.v2ShaftDistance));
            var fog = RenderSettings.fogColor.linear;
            float ambK = c.v2Fog ? 0.5f : 0f;
            _volMat.SetVector(FogAmbId, new Vector4(fog.r * ambK, fog.g * ambK, fog.b * ambK, c.v2FogNoise ? 0.6f : 0f));
            // fx-1: the same medium for the sand effects drawn after the composite (G3SandStreak.hlsl)
            Shader.SetGlobalVector(StreakFogId, new Vector4(density, c.v2FogFalloff, baseY, c.v2FogMax));
            Shader.SetGlobalVector(StreakFogAmbId, new Vector4(fog.r * ambK, fog.g * ambK, fog.b * ambK, 0));
            // temporal: previous view-projection of this camera (render-into-texture convention like UNITY_MATRIX_VP)
            if (!_history.TryGetValue(cam, out var hist)) _history[cam] = hist = new History();
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            // dx12-3: the save picture (another size, from Update) neither uses nor reallocates the camera's history
            bool temporal = c.v2VolumetricTemporal && !G3GpuSafe.Capturing;
            _volMat.SetMatrix(PrevVPId, hist.valid && temporal ? hist.prevVP : vp);
            if (temporal) hist.prevVP = vp;
            _frame = (_frame + 1) & 63;
            _volMat.SetVector(TemporalId, new Vector4(temporal && hist.valid ? 0.12f : 1f, _frame, 0, 0));
            _volPass.Setup(hist, c.v2VolumetricDownsample, temporal);
            data.scriptableRenderer.EnqueuePass(_volPass);
        }

        static void ReleaseHistory()
        {
            foreach (var h in _history.Values) { h.a?.Release(); h.b?.Release(); }
            _history.Clear();
        }

        /// <summary>
        /// Ray march at 1/2 or 1/4 resolution (pass 0), optional reprojected temporal blend with a per-camera history
        /// (pass 2: hides the step noise of the quarter-res march, rotating jitter), then depth-aware upsample and composite
        /// (pass 1).
        /// </summary>
        sealed class VolumetricPass : ScriptableRenderPass
        {
            readonly Material _mat;
            History _hist;
            int _down = 4;
            bool _temporal = true;

            sealed class MarchData { public TextureHandle source; public Material material; }
            sealed class TemporalData { public TextureHandle cur, prev; public Material material; public Vector4 texel; }
            sealed class CompositeData { public TextureHandle source, vol; public Material material; }

            public VolumetricPass(Material mat)
            {
                _mat = mat;
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(History hist, int down, bool temporal) { _hist = hist; _down = Mathf.Max(1, down); _temporal = temporal; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer) return;
                var source = res.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(source);

                var small = desc;
                small.name = "G3 V2 Volumetrics (low res)";
                small.width = Mathf.Max(1, desc.width / _down);
                small.height = Mathf.Max(1, desc.height / _down);
                small.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                small.depthBufferBits = DepthBits.None;
                small.msaaSamples = MSAASamples.None;
                small.clearBuffer = false;
                small.filterMode = FilterMode.Point;
                var vol = renderGraph.CreateTexture(small);

                using (var builder = renderGraph.AddRasterRenderPass<MarchData>("G3 V2 Volumetric March", out var md))
                {
                    md.source = source;
                    md.material = _mat;
                    builder.UseTexture(source);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(vol, 0);
                    builder.SetRenderFunc((MarchData d, RasterGraphContext ctx) =>
                        Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, 0));
                }

                var resolved = vol;
                if (_temporal && _hist != null)
                {
                    if (_hist.a == null || _hist.w != small.width || _hist.h != small.height)
                    {
                        _hist.a?.Release(); _hist.b?.Release();
                        _hist.a = RTHandles.Alloc(small.width, small.height, colorFormat: small.colorFormat, filterMode: FilterMode.Bilinear, name: "G3 V2 Vol History A");
                        _hist.b = RTHandles.Alloc(small.width, small.height, colorFormat: small.colorFormat, filterMode: FilterMode.Bilinear, name: "G3 V2 Vol History B");
                        _hist.w = small.width; _hist.h = small.height;
                        _hist.valid = false;
                        _mat.SetVector(TemporalId, new Vector4(1, 0, 0, 0));
                    }
                    var prev = renderGraph.ImportTexture(_hist.a);
                    var cur = renderGraph.ImportTexture(_hist.b);
                    using (var builder = renderGraph.AddRasterRenderPass<TemporalData>("G3 V2 Volumetric Temporal", out var td))
                    {
                        td.cur = vol; td.prev = prev; td.material = _mat; td.texel = new Vector4(1f / small.width, 1f / small.height, small.width, small.height);
                        builder.UseTexture(vol);
                        builder.UseTexture(prev);
                        if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                        builder.UseAllGlobalTextures(true);
                        builder.SetRenderAttachment(cur, 0);
                        builder.SetRenderFunc((TemporalData d, RasterGraphContext ctx) =>
                        {
                            // dx12-2: the ping-pong pair with the draw (on the shared material a draw still in flight
                            // could read the next frame's pair, i.e. its own target as the history)
                            var b = G3PassBlit.Begin(d.cur);
                            b.SetTexture(CurVolId, (RTHandle)d.cur);
                            b.SetTexture(HistVolId, (RTHandle)d.prev);
                            b.SetVector(LowTexelId, d.texel);
                            G3PassBlit.Draw(ctx.cmd, d.material, 2);
                        });
                    }
                    resolved = cur;
                    var t = _hist.a; _hist.a = _hist.b; _hist.b = t;   // this frame's result is next frame's history
                    _hist.valid = true;
                }

                var full = desc;
                full.name = "G3 V2 Volumetrics";
                full.clearBuffer = false;
                var target = renderGraph.CreateTexture(full);
                using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("G3 V2 Volumetric Composite", out var cd))
                {
                    cd.source = source;
                    cd.vol = resolved;
                    cd.material = _mat;
                    builder.UseTexture(source);
                    builder.UseTexture(resolved);
                    if (res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(target, 0);
                    builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
                    {
                        G3PassBlit.Begin(d.source).SetTexture(VolTexId, (RTHandle)d.vol);   // dx12-2: with the draw
                        G3PassBlit.Draw(ctx.cmd, d.material, 1);
                    });
                }
                res.cameraColor = target;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            G3Environment.Applied -= OnEnvironment;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            Application.quitting -= OnQuit;
            Application.quitting += OnQuit;
            _on = false;
            _light = _surfaces = false;
            _parts = WorldParts.None;
            _wet = 0;
            _archSand = _archSandHour = float.NaN;
            _groundY = float.NaN;
            _volPass = null;
            ReleaseHistory();
            SetGlobals();
        }

        static void OnQuit()
        {
            SetShadows(false);
            SetTaa(0);
            Apply(WorldParts.None);
        }
    }
}
