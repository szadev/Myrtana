using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace G3.Unity
{
    /// <summary>
    /// The look layer of the world remaster (Docs/remaster-world.md section 7), applied per world part
    /// (<see cref="Apply(WorldParts)"/>, fix-71): the Lighting part gets sky clouds (global <c>_G3RemasterSky</c> read by G3/Sky), aerial perspective (a full-screen pass injected per camera, shader
    /// Hidden/G3/Remaster/Atmosphere), a mild colour grade (pass 1 of the same shader, after the transparents) and a stronger
    /// SSAO on the renderer's existing ScreenSpaceAmbientOcclusion feature (values restored when off and on quit); the
    /// wind global <c>_G3RemasterWind</c> is split: grass waves (G3Wind.hlsl) with the Grass part, leaf rock (G3SpeedTree)
    /// with the Trees part (<see cref="WorldRemasterLook.WindFor"/>). The grade
    /// is its own pass, not a URP Volume (r3): it runs before URP post-processing (on since fix-50, <see cref="G3CameraPost"/>),
    /// which adds only the options' effects (bloom, DoF, grain) and the player's gamma ramp last, never a second grade
    /// (no tonemapping, no colour adjustments). Numbers:
    /// <see cref="WorldRemasterLook"/>, loaded from <c>Resources/G3WorldLook.json</c> (<see cref="Reload"/> re-reads it live; in
    /// the editor straight from disk, env <c>G3_WORLD_LOOK</c> = another JSON file wins). Off = sky/wind globals zero, no
    /// passes, SSAO as authored. The material tuning globals are always set (they only reach the remaster copies).
    /// </summary>
    public static class G3WorldRemasterLook
    {
        static readonly int SkyId = Shader.PropertyToID("_G3RemasterSky");
        static readonly int WindId = Shader.PropertyToID("_G3RemasterWind");
        static readonly int AtmosId = Shader.PropertyToID("_G3Atmos");
        static readonly int AtmosSunId = Shader.PropertyToID("_G3AtmosSun");
        static readonly int ClarityId = Shader.PropertyToID("_G3Clarity");
        static readonly int Clarity2Id = Shader.PropertyToID("_G3Clarity2");
        static readonly int GradeId = Shader.PropertyToID("_G3Grade");
        static readonly int GradeV2Id = Shader.PropertyToID("_G3GradeV2");
        static readonly int VibranceId = Shader.PropertyToID("_G3Vibrance");
        static readonly int DetailTuneId = Shader.PropertyToID("_G3RemasterDetailTune");
        static readonly int SurfaceTuneId = Shader.PropertyToID("_G3RemasterSurfaceTune");
        static readonly int FadeTuneId = Shader.PropertyToID("_G3RemasterFadeTune");
        static readonly int FoliageTuneId = Shader.PropertyToID("_G3RemasterFoliageTune");
        public const string ResourceName = "G3WorldLook";
        public const string EnvVar = "G3_WORLD_LOOK";
        static bool _loaded;
        static string _source = "defaults";

        static WorldParts _parts;
        static Material _atmosMat;
        static AtmospherePass _pass;
        static AtmospherePass _gradePass;
        static readonly List<(object settings, FieldInfo field, float value)> _ssaoRestore = new List<(object, FieldInfo, float)>();

        /// <summary>Any world part applied.</summary>
        public static bool Active => _parts != WorldParts.None;

        /// <summary>The parts applied now.</summary>
        public static WorldParts Parts => _parts;

        static bool Lighting => (_parts & WorldParts.Lighting) != 0;

        /// <summary>Where the current settings came from (file path, resource or "defaults").</summary>
        public static string Source => _source;

        /// <summary>
        /// Re-reads the look JSON and re-applies everything that is on (passes, SSAO, globals). Returns a one-line
        /// summary for the runner. Call after editing G3WorldLook.json in play mode.
        /// </summary>
        public static string Reload()
        {
            Load();
            int swapped = 0;
            var parts = _parts;
            if (parts != WorldParts.None)
            {
                Apply(WorldParts.None);
                Apply(parts);
                swapped = G3WorldRemaster.Refresh();   // v2 material toggles (v2, v2Terrain, v2Surfaces) may have changed
            }
            SetTuneGlobals();
            return Describe() + " | " + G3WorldV2.Describe() + (swapped > 0 ? $" | {swapped} slots re-swapped" : "");
        }

        /// <summary>
        /// fix-171: re-applies the Lighting part with the current <see cref="WorldRemasterLook.Current"/> values (no JSON
        /// re-read, no material swaps): the in-game A/B of <see cref="G3PerfHere"/> flips one lighting feature at a time.
        /// </summary>
        public static void ReapplyLighting()
        {
            var parts = _parts;
            if ((parts & WorldParts.Lighting) == 0) return;
            Apply(parts & ~WorldParts.Lighting);
            Apply(parts);
        }

        /// <summary>The active values in one line (for logs and runner checks).</summary>
        public static string Describe()
        {
            EnsureLoaded();
            var c = WorldRemasterLook.Current;
            return $"look {(Lighting ? "on" : "off")} (parts {WorldRemasterParts.Describe(_parts)}) from {_source}: aerial {(c.aerial ? c.aerialMax : 0):0.###}/{c.aerialDensity:0.#####}/lift {c.aerialLift:0.##}, " +
                   $"clarity {(c.clarity ? c.clarityStrength : 0):0.##}, grade {(c.grade ? $"{c.gradeContrast:0}/{c.gradeSaturation:0}/{c.gradeShadows:0.###}" : "off")}, " +
                   $"ssao {(c.ssao ? $"{c.ssaoIntensity:0.##}/{c.ssaoRadius:0.##}" : "off")}, normal x{c.normal:0.##} meso x{c.meso:0.##} cavity {c.cavity:0.##} " +
                   $"translucency x{c.translucency:0.##} sheen {c.foliageSheen:0.##}";
        }

        static void EnsureLoaded()
        {
            if (!_loaded) Load();
        }

        static void Load()
        {
            _loaded = true;
            var s = WorldLookSettings.Default;
            string text = null;
            _source = "defaults";
            try
            {
                string env = Environment.GetEnvironmentVariable(EnvVar);
                if (!string.IsNullOrEmpty(env) && System.IO.File.Exists(env)) { text = System.IO.File.ReadAllText(env); _source = env; }
#if UNITY_EDITOR
                if (text == null)
                {
                    string path = System.IO.Path.Combine(Application.dataPath, "G3/Unity/Runtime/Remaster/Resources/" + ResourceName + ".json");
                    if (System.IO.File.Exists(path)) { text = System.IO.File.ReadAllText(path); _source = path; }
                }
#endif
                if (text == null)
                {
                    var asset = Resources.Load<TextAsset>(ResourceName);
                    if (asset != null) { text = asset.text; _source = "Resources/" + ResourceName; }
                }
                if (!string.IsNullOrEmpty(text)) JsonUtility.FromJsonOverwrite(text, s);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[G3WorldRemasterLook] look settings unreadable, defaults used: " + e.Message);
                s = WorldLookSettings.Default;
                _source = "defaults";
            }
            _json = s.Clamped();
            WorldRemasterLook.Current = Derive();   // fix-171: the quality level on top of the JSON; opts-1: then the World details
        }

        // ------------------------------------------------------------------ fix-171: lighting quality (Docs/remaster-world.md section 19)

        static WorldLookSettings _json;
        static LightingQuality? _quality, _default;
        static string _qualitySource = "default";

        /// <summary>The Lighting part's quality: env G3_REMASTER_LIGHTING_QUALITY, else the stored choice, else the default
        /// from the Performance preset (<see cref="SetDefaultQuality"/>, High until the game sets it).</summary>
        public static LightingQuality Quality
        {
            get
            {
                if (_quality == null)
                {
                    var env = LightingQualityLevels.Parse(Environment.GetEnvironmentVariable(LightingQualityLevels.EnvVar));
                    int stored = -1;
                    try { stored = PlayerPrefs.HasKey(LightingQualityLevels.PrefKey) ? PlayerPrefs.GetInt(LightingQualityLevels.PrefKey, -1) : -1; } catch (Exception) { }
                    if (env != null) { _quality = env; _qualitySource = "env"; }
                    else if (stored >= 0 && stored <= 3) { _quality = (LightingQuality)stored; _qualitySource = "stored"; }
                    else { _quality = _default ?? LightingQuality.High; _qualitySource = _default != null ? "preset" : "default"; }
                }
                return _quality.Value;
            }
        }

        /// <summary>Where <see cref="Quality"/> came from: env, stored, preset or default.</summary>
        public static string QualitySource { get { var _ = Quality; return _qualitySource; } }

        /// <summary>The Options row: stores the level and re-applies the Lighting part (no material swaps).</summary>
        public static void SetQuality(LightingQuality q, bool persist = true)
        {
            _quality = q;
            _qualitySource = persist ? "stored" : "set";
            if (persist) { try { PlayerPrefs.SetInt(LightingQualityLevels.PrefKey, (int)q); PlayerPrefs.Save(); } catch (Exception) { } }
            Rederive();
            Debug.Log("[Remaster] lighting quality " + LightingQualityLevels.Describe(WorldRemasterLook.Current, q));
        }

        /// <summary>
        /// The game's default level (from its Performance preset, <see cref="LightingQualityLevels.FromPreset"/>): used while
        /// nothing is stored and no variable is set; applied at once when it changes the level in use.
        /// </summary>
        public static void SetDefaultQuality(LightingQuality q)
        {
            _default = q;
            if (_quality != null && _qualitySource != "default" && _qualitySource != "preset") return;
            var before = _quality;
            _quality = null;
            if (before != Quality && _loaded) Rederive();
        }

        static void Rederive()
        {
            EnsureLoaded();
            WorldRemasterLook.Overwrite(Derive());
            ReapplyLighting();
        }

        /// <summary>fix-171 + opts-1: the JSON, then the lighting quality, then the "World details" choices (<see cref="G3DesertOptions"/>).</summary>
        static WorldLookSettings Derive() => G3DesertOptions.ApplyTo(LightingQualityLevels.Apply(_json ?? WorldLookSettings.Default, Quality));

        /// <summary>opts-1: the JSON as read (before quality and options; the saturation row shows its value while unchosen).</summary>
        public static WorldLookSettings JsonSettings { get { EnsureLoaded(); return _json ?? WorldLookSettings.Default; } }

        /// <summary>
        /// opts-1: the "World details" rows changed: re-derives <see cref="WorldRemasterLook.Current"/> and re-applies the
        /// systems they switch (sand + the shared print grid, desert atmosphere); wisps, dust, the storm deposit and the
        /// saturation read <see cref="WorldRemasterLook.Current"/> every frame. No material swaps, no pass rebuild.
        /// </summary>
        public static void ReapplyOptions()
        {
            EnsureLoaded();
            WorldRemasterLook.Overwrite(Derive());
            var parts = G3WorldRemaster.Parts;
            G3SandV2.Apply(parts);
            G3SnowV2.Apply(parts);
            G3DesertAtmo.Apply(G3WorldV2.LightingOn);
        }

        static void SetTuneGlobals()
        {
            Shader.SetGlobalVector(DetailTuneId, V(WorldRemasterLook.DetailTune));
            Shader.SetGlobalVector(SurfaceTuneId, V(WorldRemasterLook.SurfaceTune));
            Shader.SetGlobalVector(FadeTuneId, V(WorldRemasterLook.FadeTune));
            Shader.SetGlobalVector(FoliageTuneId, V(WorldRemasterLook.FoliageTune));
        }

        /// <summary>Turns the whole look layer on or off (idempotent); the parts: <see cref="Apply(WorldParts)"/>.</summary>
        public static void Apply(bool on) => Apply(on ? WorldParts.All : WorldParts.None);

        /// <summary>
        /// Applies the look of the given world parts (idempotent): Lighting = sky clouds, aerial/clarity/grade passes, SSAO;
        /// Grass/Trees = their half of the wind; then the v2 layer per part (<see cref="G3WorldV2.Apply(WorldParts)"/>).
        /// </summary>
        public static void Apply(WorldParts parts)
        {
            parts &= WorldParts.All;
            if (parts == _parts) return;
            bool wasLit = Lighting;
            _parts = parts;
            EnsureLoaded();
            SetTuneGlobals();
            var c = WorldRemasterLook.Current;
            bool on = Lighting;
            Shader.SetGlobalVector(SkyId, on ? V(WorldRemasterLook.Sky) : Vector4.zero);
            Shader.SetGlobalVector(WindId, V(WorldRemasterLook.WindFor((parts & WorldParts.Grass) != 0, (parts & WorldParts.Trees) != 0)));
            if (on != wasLit)
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
                if (on)
                {
                    if (_atmosMat == null)
                    {
                        var sh = Resources.Load<Shader>("G3RemasterAtmosphere");
                        if (sh != null) _atmosMat = new Material(sh) { name = "G3 Remaster Atmosphere", hideFlags = HideFlags.DontSave };
                    }
                    bool shoulder = c.v2 && c.v2Shoulder;
                    if (_atmosMat != null && (c.aerial || c.clarity || c.grade || shoulder))
                    {
                        _pass ??= new AtmospherePass(_atmosMat, 0, RenderPassEvent.BeforeRenderingTransparents, "G3 Remaster Atmosphere");
                        _gradePass ??= new AtmospherePass(_atmosMat, 1, RenderPassEvent.AfterRenderingTransparents, "G3 Remaster Grade");
                        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
                    }
                    if (c.ssao || (c.v2 && c.v2Ssao)) SetSsao(true);
                }
                else SetSsao(false);
            }
            G3WorldV2.Apply(parts);   // v2 (section 10): its own toggles inside, off when "v2" is false
        }

        static Vector4 V(float[] a) => new Vector4(a[0], a[1], a[2], a[3]);

        static readonly global::Unity.Profiling.ProfilerMarker PmBegin = new global::Unity.Profiling.ProfilerMarker("G3WorldRemasterLook.BeginCamera");   // fix-171

        static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!Lighting || _atmosMat == null) return;
            using var _pm = PmBegin.Auto();
            if (cam.cullingMask == 0) return; // fix-137: the title's empty camera (TitleStage): no fog/aerial over black (purple start-up screen)
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.scriptableRenderer == null || data.renderType != CameraRenderType.Base) return;
            var c = WorldRemasterLook.Current;
            _atmosMat.SetVector(AtmosId, new Vector4(c.aerial ? c.aerialDensity : 0, c.aerialFalloff,
                c.aerial ? c.aerialMax : 0, cam.transform.position.y - c.aerialReferenceBelowCamera));
            _atmosMat.SetVector(AtmosSunId, new Vector4(c.aerialSun, c.aerialLift, 0, 0));
            _atmosMat.SetVector(ClarityId, new Vector4(c.clarity ? c.clarityStrength : 0, c.clarityStart, c.clarityFull, c.clarityLimit));
            _atmosMat.SetVector(Clarity2Id, new Vector4(c.clarityRadius, c.clarity ? c.claritySaturation : 0, 0, 0));
            _atmosMat.SetVector(GradeId, V(WorldRemasterLook.Grade));
            _atmosMat.SetVector(GradeV2Id, V(WorldV2.ShoulderParams));
            _atmosMat.SetVector(VibranceId, V(WorldRemasterLook.VibranceGlobal));   // grade-1
            if (c.aerial || c.clarity) data.scriptableRenderer.EnqueuePass(_pass);
            // lighting-new (section 21): eye adaptation + exposure / grade / filmic tonemap of the New lighting
            bool newGrade = G3LightNew.BeginGrade(cam, data.scriptableRenderer, _atmosMat);
            if (c.grade || (c.v2 && c.v2Shoulder) || newGrade) data.scriptableRenderer.EnqueuePass(_gradePass);
        }

        /// <summary>SSAO boost on every renderer of the active URP asset (reflection: the feature's settings are internal).</summary>
        static void SetSsao(bool on)
        {
            if (!on)
            {
                foreach (var (settings, field, value) in _ssaoRestore) field.SetValue(settings, value);
                _ssaoRestore.Clear();
                return;
            }
            if (_ssaoRestore.Count > 0) return;
            try
            {
                var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (urp == null) return;
                var list = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(urp) as ScriptableRendererData[];
                if (list == null) return;
                foreach (var data in list)
                {
                    if (data == null) continue;
                    foreach (var feature in data.rendererFeatures)
                    {
                        if (feature == null || feature.GetType().Name != "ScreenSpaceAmbientOcclusion") continue;
                        var settings = feature.GetType().GetField("m_Settings", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(feature);
                        if (settings == null) continue;
                        var c = WorldRemasterLook.Current;
                        bool v2 = c.v2 && c.v2Ssao;   // v2: SSAO tuned for the scale (stronger, wider, longer falloff)
                        Boost(settings, "Intensity", v2 ? c.v2SsaoIntensity : c.ssaoIntensity);
                        Boost(settings, "Radius", v2 ? c.v2SsaoRadius : c.ssaoRadius);
                        Boost(settings, "DirectLightingStrength", v2 ? c.v2SsaoDirect : c.ssaoDirect);
                        if (v2) Boost(settings, "Falloff", c.v2SsaoFalloff);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[G3WorldRemasterLook] SSAO boost skipped: " + e.Message);
            }
        }

        /// <summary>
        /// remaster-ssgi (section 19): URP's SSAO intensity x <paramref name="k"/> while the screen-space GI of Modern
        /// lighting runs (both darken the ambient; 1 = the boost as set). No-op while the boost is not applied.
        /// </summary>
        public static void ScaleSsao(float k)
        {
            if (Mathf.Approximately(k, _ssaoScale)) return;
            _ssaoScale = k;
            var c = WorldRemasterLook.Current;
            float baseI = c.v2 && c.v2Ssao ? c.v2SsaoIntensity : c.ssaoIntensity;
            foreach (var (settings, field, _) in _ssaoRestore)
                if (field.Name == "Intensity") field.SetValue(settings, baseI * k);
        }
        static float _ssaoScale = 1;

        static void Boost(object settings, string name, float value)
        {
            if (name == "Intensity") value *= _ssaoScale;
            var f = settings.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null || f.FieldType != typeof(float)) return;
            _ssaoRestore.Add((settings, f, (float)f.GetValue(settings)));
            f.SetValue(settings, value);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            Application.quitting -= OnQuit;
            Application.quitting += OnQuit;
            _parts = WorldParts.None;
            _ssaoScale = 1;
            _loaded = false;
            _quality = null;
            _pass = null;
            _gradePass = null;
            Shader.SetGlobalVector(SkyId, Vector4.zero);
            Shader.SetGlobalVector(WindId, Vector4.zero);
        }

        static void OnQuit() => Apply(WorldParts.None);

        /// <summary>
        /// Copy of the camera colour through one pass of Hidden/G3/Remaster/Atmosphere (render graph): pass 0 aerial +
        /// clarity before the transparents, pass 1 the grade after them.
        /// </summary>
        sealed class AtmospherePass : ScriptableRenderPass
        {
            readonly Material _mat;
            readonly int _shaderPass;
            readonly string _name;

            sealed class PassData
            {
                public TextureHandle source;
                public Material material;
                public int pass;
            }

            public AtmospherePass(Material mat, int shaderPass, RenderPassEvent evt, string name)
            {
                _mat = mat;
                _shaderPass = shaderPass;
                _name = name;
                renderPassEvent = evt;
                requiresIntermediateTexture = true;
                if (shaderPass == 0) ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var res = frameData.Get<UniversalResourceData>();
                if (res.isActiveTargetBackBuffer) return;
                var source = res.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(source);
                desc.name = _name;
                desc.clearBuffer = false;
                var target = renderGraph.CreateTexture(desc);
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(_name, out var pd))
                {
                    pd.source = source;
                    pd.material = _mat;
                    pd.pass = _shaderPass;
                    builder.UseTexture(source);
                    if (_shaderPass == 0 && res.cameraDepthTexture.IsValid()) builder.UseTexture(res.cameraDepthTexture);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(target, 0);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                        Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1, 1, 0, 0), d.material, d.pass));
                }
                res.cameraColor = target;
            }
        }
    }
}
