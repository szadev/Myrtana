using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using G3.Effects;
using G3.Formats.Audio;
using G3.Formats.Effects;
using G3.Formats.Pak;
using G3.Formats.Templates;
using G3.Formats.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace G3.Unity.Editor
{
    /// <summary>
    /// Imports G3's visual effects (Docs/formats-effects.md): every VFX template spawned by g3.efm or placed in the world
    /// becomes a pooled <see cref="G3Vfx"/> prefab (a Shuriken ParticleSystem configured from eCParticle_PS, custom emission,
    /// the G3/Particle material for its texture and draw style, and an eCPointLight_PS light), plus
    /// <see cref="G3EffectLibrary"/> (effect → spawns) and <see cref="G3WorldEffectsAsset"/> (torches, candles, fires … placed in
    /// the world) under Resources, and the weather's precipitation material and CP 1.75 loop sounds.
    /// Run: menu <c>G3/Effects/Import Effects</c> or <c>G3.Unity.Editor.G3EffectImporter.ImportAll()</c>.
    /// </summary>
    public static class G3EffectImporter
    {
        public const string Root = G3AssetCache.Root + "/Effects";
        public const string PrefabDir = Root + "/Prefabs";
        public const string MaterialDir = Root + "/Materials";
        public const string AudioDir = Root + "/Audio";
        public const string ResourcesDir = Root + "/Resources";
        public const string LibraryPath = ResourcesDir + "/" + G3EffectLibrary.ResourcePath + ".asset";
        public const string WorldPath = ResourcesDir + "/" + G3WorldEffectsAsset.ResourcePath + ".asset";
        const float M = 0.01f;

        /// <summary>
        /// [inferred] StartSize is the particle's full width/height (cm). If imported effects look half as big as in G3,
        /// set 2 here and re-import (Docs/formats-effects.md §3.3).
        /// </summary>
        public static float SizeFactor = 1f;

        [MenuItem("G3/Effects/Import Effects", priority = 260)]
        static void Menu() => Debug.Log(ImportAll());

        public static string ImportAll()
        {
            var sw = Stopwatch.StartNew();
            foreach (var d in new[] { Root, PrefabDir, MaterialDir, AudioDir, ResourcesDir }) Directory.CreateDirectory(d);
            string data = G3WorldImporter.DataDir;
            EffectMap map;
            using (var lib = new G3FileSystem(data, "Library")) map = EffectMap.Read(lib.GetByName("g3.efm").Read());
            TemplateArchive templates;
            using (var fs = new G3FileSystem(data, "Templates")) templates = TemplateArchive.Load(fs);

            var warnings = new List<string>();
            var prefabs = new Dictionary<string, G3Vfx>(StringComparer.OrdinalIgnoreCase);
            var library = LoadOrCreate<G3EffectLibrary>(LibraryPath);
            library.effects.Clear();
            library.templates.Clear();
            int heatHaze = 0, invisible = 0, lights = 0;

            using (var cache = new G3AssetCache(data, new UrpMaterialFactory()))
            {
                var materials = new Dictionary<string, Material>();
                G3Vfx Prefab(TemplateFile f)
                {
                    if (f == null) return null;
                    if (prefabs.TryGetValue(f.Name, out var known)) return known;
                    VfxTemplateDef def;
                    try { def = VfxTemplateDef.From(f); }
                    catch (Exception ex) { warnings.Add($"{f.Name}: {ex.Message}"); prefabs[f.Name] = null; return null; }
                    if (def.Emitter != null && def.Emitter.IsHeatHaze) heatHaze++;
                    if (def.Emitter != null && def.Emitter.DrawStyle == DrawStyle.Invisible) invisible++;
                    if (def.Light != null) lights++;
                    var p = BuildPrefab(def, cache, materials, warnings);
                    prefabs[f.Name] = p;
                    if (p != null) library.templates.Add(p);
                    return p;
                }

                {
                    foreach (var e in map.Effects.Values.OrderBy(e => e.Name))
                    {
                        var entry = new G3EffectEntry { name = e.Name };
                        foreach (var s in e.Spawns)
                        {
                            var p = Prefab(templates.Get(s.Template));
                            if (p == null) continue;
                            entry.spawns.Add(new G3EffectSpawnData
                            {
                                prefab = p, link = s.Link, bone = s.BoneName, autoKill = s.AutoKill, timeOffset = s.TimeOffset,
                                offsetMin = V(s.OffsetMin) * M, offsetMax = V(s.OffsetMax) * M,
                            });
                        }
                        if (entry.spawns.Count > 0) library.effects.Add(entry);
                    }
                }
                AssetDatabase.SaveAssets();

                var world = BakeWorld(templates, map, Prefab, library, out string worldSummary);
                int itemCount = BakeItems(templates, library);
                EditorUtility.SetDirty(library);
                SetupWeather(cache, data, warnings);
                AssetDatabase.SaveAssets();
                int withSpawns = library.effects.Count;
                return $"G3 effects: {withSpawns} effects with visuals, {library.templates.Count} template prefabs " +
                       $"({lights} with light, {heatHaze} heat haze{(Shader.Find(HeatHazeShader) != null ? "" : " skipped")}, {invisible} invisible), {materials.Count} materials; {worldSummary}; " +
                       $"{itemCount} items with effects; " +
                       $"{warnings.Count} warnings{(warnings.Count > 0 ? ": " + string.Join("; ", warnings.Take(12)) : "")}; {sw.Elapsed.TotalSeconds:F1}s";
            }
        }

        // ------------------------------------------------------------------ prefab

        static G3Vfx BuildPrefab(VfxTemplateDef def, G3AssetCache cache, Dictionary<string, Material> materials, List<string> warnings)
        {
            var go = new GameObject(def.Name);
            try
            {
                var vfx = go.AddComponent<G3Vfx>();
                vfx.templateName = def.Name;
                var e = def.Emitter;
                bool show = e != null && e.Enabled && !e.IsHeatHaze && e.DrawStyle != DrawStyle.Invisible && !string.IsNullOrEmpty(e.Texture);
                if (e != null && e.Enabled && e.IsHeatHaze)
                {
                    var haze = GetHeatHazeMaterial(cache, materials, warnings);
                    if (haze != null)
                    {
                        vfx.system = BuildSystem(go, e, haze);
                        vfx.emitter = Spec(e);
                        vfx.hasEmitter = true;
                    }
                }
                if (show)
                {
                    var mat = GetMaterial(e, cache, materials, warnings);
                    if (mat != null)
                    {
                        vfx.system = BuildSystem(go, e, mat);
                        vfx.emitter = Spec(e);
                        vfx.hasEmitter = true;
                    }
                }
                if (def.Light != null && def.Light.Enabled)
                {
                    var l = def.Light;
                    var lgo = new GameObject("Light");
                    lgo.transform.SetParent(go.transform, false);
                    lgo.transform.localPosition = V(l.Offset) * M;
                    var light = lgo.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.color = new Color(l.R, l.G, l.B);
                    light.range = l.Range * M;
                    light.shadows = LightShadows.None;
                    light.enabled = false;
                    lgo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();
                    vfx.pointLight = light;
                    vfx.lightIntensity = EmitterRules.EffectLightIntensity(l.Intensity);
                    vfx.lightEffect = l.Effect;
                    vfx.lightPeriod = l.EffectPeriod;
                    vfx.lightPhase = l.EffectPhase;
                }
                if (!vfx.hasEmitter && vfx.pointLight == null) return null;
                string path = $"{PrefabDir}/{Safe(def.Name)}.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
                return prefab != null ? prefab.GetComponent<G3Vfx>() : null;
            }
            catch (Exception ex) { warnings.Add($"{def.Name}: {ex.Message}"); return null; }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        internal static G3EmitterSpec Spec(ParticleEmitterDef e)
        {
            int max = EmitterRules.CapParticles(e.MaxParticles);
            float lifeMax = Mathf.Max(e.Lifetime.Min, e.Lifetime.Max);
            float rate = EmitterRules.EmissionRate(max, e.ParticlesPerSecond, lifeMax);
            int spinAxis = EmitterRules.SpinAxis(e.StartSpin[2].Min, e.StartSpin[2].Max, e.SpinsPerSecond[2].Min, e.SpinsPerSecond[2].Max);
            return new G3EmitterSpec
            {
                maxParticles = max,
                rate = rate,
                respawn = e.RespawnDeadParticles,
                emitSeconds = e.RespawnDeadParticles ? 0 : EmitterRules.OneShotEmitSeconds(max, rate),
                lifetime = new Vector2(e.Lifetime.Min, e.Lifetime.Max),
                initialDelay = new Vector2(e.InitialDelay.Min, e.InitialDelay.Max),
                warmup = e.RelativeWarmupTime * lifeMax,
                shape = (int)e.StartShape,
                boxMin = new Vector3(e.StartBox[0].Min, e.StartBox[1].Min, e.StartBox[2].Min) * M,
                boxMax = new Vector3(e.StartBox[0].Max, e.StartBox[1].Max, e.StartBox[2].Max) * M,
                sphereRadius = new Vector2(e.StartSphereRadius.Min, e.StartSphereRadius.Max) * M,
                offset = V(e.StartOffset) * M,
                velocityMin = new Vector3(e.StartVelocity[0].Min, e.StartVelocity[1].Min, e.StartVelocity[2].Min) * M,
                velocityMax = new Vector3(e.StartVelocity[0].Max, e.StartVelocity[1].Max, e.StartVelocity[2].Max) * M,
                directionFrom = (int)e.DirectionFrom,
                rotateWithEntity = e.UseRotationFrom == RotationFrom.Entity,
                uniformSize = e.UniformSize,
                sizeMin = new Vector3(e.StartSize[0].Min, e.StartSize[1].Min, e.StartSize[2].Min) * (M * SizeFactor),
                sizeMax = new Vector3(e.StartSize[0].Max, e.StartSize[1].Max, e.StartSize[2].Max) * (M * SizeFactor),
                spin = e.SpinParticles,
                startSpinDegrees = new Vector2(e.StartSpin[spinAxis].Min, e.StartSpin[spinAxis].Max) * 360f,
                spinDegreesPerSecond = new Vector2(e.SpinsPerSecond[spinAxis].Min, e.SpinsPerSecond[spinAxis].Max) * 360f,
                spinClockwise = e.SpinDirection,
                worldSpace = e.Space == ParticleSpace.Independent,
                // fix-179: world-space revolution is G3Vfx's (G3 centre per particle, world axes, radians/s)
                revolve = EmitterRules.RevolvesInWorld(e.UseRevolution, e.Space == ParticleSpace.Independent),
                revolutionMin = new Vector3(e.RevolutionsPerSecond[0].Min, e.RevolutionsPerSecond[1].Min, e.RevolutionsPerSecond[2].Min),
                revolutionMax = new Vector3(e.RevolutionsPerSecond[0].Max, e.RevolutionsPerSecond[1].Max, e.RevolutionsPerSecond[2].Max),
                revolutionCenterMin = new Vector3(e.RevolutionCenterOffset[0].Min, e.RevolutionCenterOffset[1].Min, e.RevolutionCenterOffset[2].Min) * M,
                revolutionCenterMax = new Vector3(e.RevolutionCenterOffset[0].Max, e.RevolutionCenterOffset[1].Max, e.RevolutionCenterOffset[2].Max) * M,
                revolutionScaleX = RevolutionScale(e, k => k.A),
                revolutionScaleY = RevolutionScale(e, k => k.B),
                revolutionScaleZ = RevolutionScale(e, k => k.C),
            };
        }

        static AnimationCurve RevolutionScale(ParticleEmitterDef e, Func<ScaleKey, float> axis) =>
            e.UseRevolution && e.UseRevolutionScale && e.RevolutionScale.Count > 0 ? Curve(e.RevolutionScale, e.RevolutionScaleRepeats, axis) : new AnimationCurve();

        static ParticleSystem BuildSystem(GameObject go, ParticleEmitterDef e, Material mat)
        {
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            float lifeMax = Mathf.Max(0.05f, Mathf.Max(e.Lifetime.Min, e.Lifetime.Max));
            float lifeMean = Mathf.Max(0.05f, (e.Lifetime.Min + e.Lifetime.Max) * 0.5f);

            var main = ps.main;
            main.duration = Mathf.Max(1f, lifeMax);
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = lifeMax;
            main.startSpeed = 0;
            main.startSize3D = !e.UniformSize;
            main.maxParticles = EmitterRules.CapParticles(e.MaxParticles);
            main.simulationSpace = e.Space == ParticleSpace.Independent ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.gravityModifier = 0;
            main.cullingMode = ParticleSystemCullingMode.Automatic;
            // fix-124: faint additive stacks (the torch's BigGlow halo) as dim as G3's gamma-space sum (EmitterRules.AdditiveAlphaScale)
            float peakAlpha = e.UseColorScale && e.ColorScale.Count > 0 ? e.ColorScale.Max(k => k.D) : 1f;
            int capped = EmitterRules.CapParticles(e.MaxParticles);
            float steady = EmitterRules.SteadyCount(capped, EmitterRules.EmissionRate(capped, e.ParticlesPerSecond, lifeMax),
                                                    e.RespawnDeadParticles, lifeMean);
            main.startColor = new Color(1f, 1f, 1f, EmitterRules.AdditiveAlphaScale((int)e.DrawStyle, steady, peakAlpha));

            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;

            // Acceleration (cm/s²)
            var a = V(e.Acceleration) * M;
            if (a.sqrMagnitude > 1e-8f)
            {
                var force = ps.forceOverLifetime;
                force.enabled = true;
                force.space = e.Space == ParticleSpace.Independent ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
                force.x = a.x; force.y = a.y; force.z = a.z;
            }

            // VelocityLoss (UE2 damping) and MaxAbsoluteVelocity
            float drag = EmitterRules.Drag(e.VelocityLoss[0].Mid, e.VelocityLoss[1].Mid, e.VelocityLoss[2].Mid);
            float maxV = EmitterRules.VelocityLimit(e.MaxAbsoluteVelocity[0], e.MaxAbsoluteVelocity[1], e.MaxAbsoluteVelocity[2]) * M;
            if (drag > 1e-4f || maxV < 50f)
            {
                var lim = ps.limitVelocityOverLifetime;
                lim.enabled = true;
                lim.limit = float.IsInfinity(maxV) ? 1e5f : Mathf.Max(0.01f, maxV);
                lim.drag = drag;
                lim.multiplyDragByParticleSize = false;
                lim.multiplyDragByParticleVelocity = false;
                lim.dampen = 0;
            }

            // Collision with the static world: drops/debris land (MaxCollisions 1 = stop) instead of falling through the ground
            if (EmitterRules.Collision(e.UseCollision, e.CollideWithStatic, e.UseMaxCollisions, Mathf.Max(e.MaxCollisions.Min, e.MaxCollisions.Max),
                                       e.DampingFactor[0].Mid, e.DampingFactor[1].Mid, e.DampingFactor[2].Mid, out float bounce, out float dampen))
            {
                var col = ps.collision;
                col.enabled = true;
                col.type = ParticleSystemCollisionType.World;
                col.mode = ParticleSystemCollisionMode.Collision3D;
                col.quality = main.maxParticles <= 100 ? ParticleSystemCollisionQuality.High : ParticleSystemCollisionQuality.Medium;
                // the world (terrain, meshes on Default); not Ignore Raycast, Water or UI
                col.collidesWith = ~((1 << 2) | (1 << 4) | (1 << 5));
                col.enableDynamicColliders = true; // streamed world colliders are created at runtime
                col.bounce = bounce;
                col.dampen = dampen;
                // fix-132: a stretched drop that stops dies at the hit (a stopped stretched sprite flickers)
                col.lifetimeLoss = EmitterRules.DiesAtHit(IsStretched(e.Facing), true, bounce, dampen) ? 1f : 0f;
                col.minKillSpeed = 0;
                col.radiusScale = 0.5f; // sprites rest slightly sunk, not floating half a size above the ground
                col.sendCollisionMessages = false;
            }

            // VelocityScale (one scalar: the mean of the axes) and the revolution (orbital velocity)
            bool useVel = e.UseVelocityScale && e.VelocityScale.Count > 0;
            // fix-179: Unity's orbital only for Relative emitters (G3: centre and rotation in the emitter frame, local orbital
            // matches); world-space revolution is G3Vfx's (EmitterRules.RevolvesInWorld). Rates are radians/s as stored
            // (EmitterRules.RevolutionRadians), not turns: × 2π before.
            bool useRev = e.UseRevolution && !EmitterRules.RevolvesInWorld(e.UseRevolution, e.Space == ParticleSpace.Independent);
            if (useVel || useRev)
            {
                var vel = ps.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.Local;
                vel.x = 0; vel.y = 0; vel.z = 0;
                if (useVel) vel.speedModifier = new ParticleSystem.MinMaxCurve(1f, Curve(e.VelocityScale, e.VelocityScaleRepeats, k => (k.A + k.B + k.C) / 3f));
                if (useRev)
                {
                    float rx = e.RevolutionsPerSecond[0].Mid, ry = e.RevolutionsPerSecond[1].Mid, rz = e.RevolutionsPerSecond[2].Mid;
                    if (e.UseRevolutionScale && e.RevolutionScale.Count > 0)
                    {
                        vel.orbitalX = new ParticleSystem.MinMaxCurve(rx, Curve(e.RevolutionScale, e.RevolutionScaleRepeats, k => k.A));
                        vel.orbitalY = new ParticleSystem.MinMaxCurve(ry, Curve(e.RevolutionScale, e.RevolutionScaleRepeats, k => k.B));
                        vel.orbitalZ = new ParticleSystem.MinMaxCurve(rz, Curve(e.RevolutionScale, e.RevolutionScaleRepeats, k => k.C));
                    }
                    else
                    {
                        vel.orbitalX = new ParticleSystem.MinMaxCurve(e.RevolutionsPerSecond[0].Min, e.RevolutionsPerSecond[0].Max);
                        vel.orbitalY = new ParticleSystem.MinMaxCurve(e.RevolutionsPerSecond[1].Min, e.RevolutionsPerSecond[1].Max);
                        vel.orbitalZ = new ParticleSystem.MinMaxCurve(e.RevolutionsPerSecond[2].Min, e.RevolutionsPerSecond[2].Max);
                    }
                    vel.orbitalOffsetX = e.RevolutionCenterOffset[0].Mid * M;
                    vel.orbitalOffsetY = e.RevolutionCenterOffset[1].Mid * M;
                    vel.orbitalOffsetZ = e.RevolutionCenterOffset[2].Mid * M;
                }
            }

            // Colour (ColorScale × fade in/out, baked with the mean lifetime)
            bool fades = (e.FadeIn && e.FadeInFactor.Any(f => f < 1)) || (e.FadeOut && e.FadeOutFactor.Any(f => f < 1));
            if ((e.UseColorScale && e.ColorScale.Count > 0) || fades)
            {
                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(Gradient(e, lifeMean));
            }

            // Size scale
            if (e.UseSizeScale && e.SizeScale.Count > 0)
            {
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                if (e.UniformSize) size.size = new ParticleSystem.MinMaxCurve(1f, Curve(e.SizeScale, e.SizeScaleRepeats, k => k.A));
                else
                {
                    size.separateAxes = true;
                    size.x = new ParticleSystem.MinMaxCurve(1f, Curve(e.SizeScale, e.SizeScaleRepeats, k => k.A));
                    size.y = new ParticleSystem.MinMaxCurve(1f, Curve(e.SizeScale, e.SizeScaleRepeats, k => k.B));
                    size.z = new ParticleSystem.MinMaxCurve(1f, Curve(e.SizeScale, e.SizeScaleRepeats, k => k.C));
                }
            }

            // Frames: (NumU + 1) × (NumV + 1) tiles (CalcFrame)
            int frames = EmitterRules.FrameCount(e.NumU, e.NumV);
            if (frames > 1)
            {
                var sheet = ps.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.numTilesX = e.NumU + 1;
                sheet.numTilesY = e.NumV + 1;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.cycleCount = 1;
                EmitterRules.FrameRange(e.NumU, e.NumV, e.SubdivisionStart, e.SubdivisionEnd, out int first, out int last);
                float n = frames;
                if (e.UseSubdivisionScale && e.SubdivisionScale.Count > 0)
                    sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, Curve(e.SubdivisionScale, e.SubdivisionScaleRepeats,
                        k => (Mathf.Clamp(k.A, 0, frames - 1) + 0.01f) / n));
                else if (e.RandomSubdivision)
                    sheet.frameOverTime = new ParticleSystem.MinMaxCurve((first + 0.01f) / n, (last + 0.99f) / n);
                else
                {
                    var c = AnimationCurve.Linear(0, (first + 0.01f) / n, 1, Mathf.Min(last + 0.99f, frames - 0.01f) / n);
                    sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f, c);
                }
            }

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.maxParticleSize = 5f;
            r.sortMode = e.DrawStyle == DrawStyle.AlphaBlend ? ParticleSystemSortMode.Distance : ParticleSystemSortMode.None;
            switch (e.Facing)
            {
                case FacingDirection.AlongMovementFacingCamera:
                case FacingDirection.AlongMovementFacingNormal:
                    r.renderMode = ParticleSystemRenderMode.Stretch;
                    r.lengthScale = 1f;
                    r.velocityScale = 0f;
                    break;
                case FacingDirection.SpecifiedNormal:
                    r.renderMode = Mathf.Abs(e.ProjectionNormal[1]) > 0.7f ? ParticleSystemRenderMode.HorizontalBillboard : ParticleSystemRenderMode.VerticalBillboard;
                    break;
                case FacingDirection.PerpendicularToMovement:
                    r.renderMode = ParticleSystemRenderMode.Billboard;
                    r.alignment = ParticleSystemRenderSpace.Velocity;
                    break;
                default:
                    r.renderMode = ParticleSystemRenderMode.Billboard;
                    break;
            }
            return ps;
        }

        static bool IsStretched(FacingDirection f) =>
            f == FacingDirection.AlongMovementFacingCamera || f == FacingDirection.AlongMovementFacingNormal;

        /// <summary>Keys (with repeats) → linear AnimationCurve of one value.</summary>
        static AnimationCurve Curve(List<ScaleKey> keys, float repeats, Func<ScaleKey, float> value)
        {
            var times = keys.Select(k => k.Time).ToList();
            var vals = keys.Select(value).ToList();
            var sample = EmitterRules.SampleTimes(times, repeats, 32);
            var c = new AnimationCurve();
            foreach (var t in sample)
                c.AddKey(new Keyframe(t, EmitterRules.Evaluate(times, vals, EmitterRules.RepeatTime(t, repeats))));
            for (int i = 0; i < c.length; i++)
            {
                UnityEditor.AnimationUtility.SetKeyLeftTangentMode(c, i, UnityEditor.AnimationUtility.TangentMode.Linear);
                UnityEditor.AnimationUtility.SetKeyRightTangentMode(c, i, UnityEditor.AnimationUtility.TangentMode.Linear);
            }
            return c;
        }

        /// <summary>ColorScale (rgba, gamma) × CalcColorFactor fades → Unity gradient (≤ 8 colour and alpha keys).</summary>
        static Gradient Gradient(ParticleEmitterDef e, float life)
        {
            bool cs = e.UseColorScale && e.ColorScale.Count > 0;
            var times = cs ? e.ColorScale.Select(k => k.Time).ToList() : new List<float> { 0, 1 };
            if (e.FadeIn && e.FadeInEndTime > 0) times.Add(Mathf.Clamp01(e.FadeInEndTime / life));
            if (e.FadeOut) times.Add(Mathf.Clamp01(e.FadeOutStartTime / life));
            times = times.Distinct().OrderBy(t => t).ToList();
            var sample = EmitterRules.SampleTimes(times, cs ? e.ColorScaleRepeats : 0, 8);
            var ct = cs ? e.ColorScale.Select(k => k.Time).ToList() : null;
            float Ch(int c, float t)
            {
                float v = 1f;
                if (cs)
                {
                    float rt = EmitterRules.RepeatTime(t, e.ColorScaleRepeats);
                    v = EmitterRules.Evaluate(ct, e.ColorScale.Select(k => c == 0 ? k.A : c == 1 ? k.B : c == 2 ? k.C : k.D).ToList(), rt);
                }
                float age = t * life;
                return v * EmitterRules.FadeFactor(age, life, e.FadeIn, e.FadeInEndTime, e.FadeInFactor[c], e.FadeOut, e.FadeOutStartTime, e.FadeOutFactor[c]);
            }
            var g = new Gradient { mode = GradientMode.Blend };
            g.SetKeys(sample.Select(t => new GradientColorKey(new Color(Ch(0, t), Ch(1, t), Ch(2, t)), t)).ToArray(),
                      sample.Select(t => new GradientAlphaKey(Ch(3, t), t)).ToArray());
            return g;
        }

        static Material GetMaterial(ParticleEmitterDef e, G3AssetCache cache, Dictionary<string, Material> materials, List<string> warnings)
        {
            string stem = Path.GetFileNameWithoutExtension(e.Texture);
            var blend = EmitterRules.Blend((int)e.DrawStyle);
            bool alphaTest = e.AlphaTest || e.DrawStyle == DrawStyle.Regular;
            // Lit sprites (LightingStyle ≠ Disabled: torch/camp-fire smoke …) take the scene's light; additive ones never do.
            bool lit = e.Lighting != LightingStyle.Disabled && !blend.FogToBlack;
            string key = $"{stem}_{e.DrawStyle}{(alphaTest ? "_at" + e.AlphaReference : "")}{(e.DepthWrite ? "_zw" : "")}{(e.DepthTest ? "" : "_nz")}{(e.DisableFogging ? "_nf" : "")}{(lit ? "_lit" : "")}";
            if (materials.TryGetValue(key, out var mat)) return mat;
            var tex = cache.GetImageTexture(stem, false, false);
            if (tex == null) { warnings.Add($"texture {stem} missing ({e.Name})"); materials[key] = null; return null; }
            mat = MakeMaterial($"{MaterialDir}/{Safe(key)}.mat", tex, blend, alphaTest ? e.AlphaReference / 255f : 0f, e.DepthWrite, e.DepthTest, e.DisableFogging,
                               lit, EmitterRules.ColorGain((int)e.DrawStyle), EmitterRules.QueueOffset((int)e.DrawStyle));
            materials[key] = mat;
            return mat;
        }

        public const string HeatHazeShader = "G3/HeatHaze";

        /// <summary>
        /// G3_Special_HeatHaze_01.xshmat (eCShaderParticle, map VFX_Env_HeatHaze_Distort_01) as the G3/HeatHaze screen
        /// distortion (needs URP's opaque texture); null when the shader is missing (the 23 emitters stay skipped).
        /// </summary>
        static Material GetHeatHazeMaterial(G3AssetCache cache, Dictionary<string, Material> materials, List<string> warnings)
        {
            const string key = "G3_Special_HeatHaze_01";
            if (materials.TryGetValue(key, out var mat)) return mat;
            var shader = Shader.Find(HeatHazeShader);
            var tex = shader != null ? cache.GetImageTexture("VFX_Env_HeatHaze_Distort_01", false, false) : null;
            if (shader == null || tex == null)
            {
                if (shader != null) warnings.Add("texture VFX_Env_HeatHaze_Distort_01 missing");
                materials[key] = null;
                return null;
            }
            string path = $"{MaterialDir}/{key}.mat";
            mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = mat == null;
            if (create) mat = new Material(shader);
            else mat.shader = shader;
            mat.mainTexture = tex;
            // runner-15: before the flames (3000/3001): the haze writes the opaque-only scene colour, so drawn after them it erased flame + smoke
            mat.renderQueue = (int)RenderQueue.Transparent - 10;
            if (create) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);
            materials[key] = mat;
            return mat;
        }

        // ------------------------------------------------------------------ item effects (gCEffect_PS / eCPointLight_PS / gCProjectile_PS on items)

        static int BakeItems(TemplateArchive templates, G3EffectLibrary library)
        {
            library.items.Clear();
            foreach (var f in templates.Files.Where(f => !f.IsDeleted))
            foreach (var h in f.Headers.Where(h => !h.HelperParent && h.Get("gCItem_PS") != null))
            {
                var fx = h.Get("gCEffect_PS");
                var light = h.Get("eCPointLight_PS");
                var proj = h.Get("gCProjectile_PS");
                string effect = fx != null ? G3.Formats.Items.Props.Str(h.Source, fx, "Effect") : null;
                string impact = proj != null ? G3.Formats.Items.Props.Str(h.Source, proj, "EffectPointImpact") : null;
                string hit = proj != null ? G3.Formats.Items.Props.Str(h.Source, proj, "EffectTargetHit") : null;
                if (library.Get(effect) == null) effect = null;
                if (library.Get(impact) == null) impact = null;
                if (library.Get(hit) == null) hit = null;
                var l = light != null ? EffectLightDef.Read(light) : null;
                if (l != null && !l.Enabled) l = null;
                if (effect == null && l == null && impact == null && hit == null) continue;
                var d = new G3ItemEffectData
                {
                    template = f.Name, effect = effect, offset = V(G3.Formats.Items.Props.Vec3(fx, "Offset")) * M,
                    impactEffect = impact, hitEffect = hit,
                };
                if (l != null)
                {
                    d.light = true;
                    d.lightColor = new Color(l.R, l.G, l.B);
                    d.lightRange = l.Range * M;
                    d.lightIntensity = EmitterRules.EffectLightIntensity(l.Intensity);
                    d.lightOffset = V(l.Offset) * M;
                    d.lightEffect = l.Effect;
                    d.lightPeriod = l.EffectPeriod;
                    d.lightPhase = l.EffectPhase;
                    d.lightShadows = l.CastShadows;
                }
                library.items.Add(d);
            }
            return library.items.Count;
        }

        internal static Material MakeMaterial(string path, Texture2D tex, BlendSetup blend, float cutoff, bool depthWrite, bool depthTest, bool noFog,
                                              bool lit = false, float gain = 1f, int queueOffset = 0)
        {
            var shader = Shader.Find("G3/Particle");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = mat == null;
            if (create) mat = new Material(shader);
            else mat.shader = shader;
            mat.mainTexture = tex;
            // DDS rows are stored top-down: flip V so frame 0 (top-left in G3) is Unity's top-left tile and sprites stand upright.
            mat.mainTextureScale = new Vector2(1, -1);
            mat.mainTextureOffset = new Vector2(0, 1);
            mat.SetFloat("_SrcBlend", (float)blend.Src);
            mat.SetFloat("_DstBlend", (float)blend.Dst);
            mat.SetFloat("_ZWrite", depthWrite ? 1 : 0);
            mat.SetFloat("_ZTest", depthTest ? (float)CompareFunction.LessEqual : (float)CompareFunction.Always);
            mat.SetFloat("_Cutoff", cutoff);
            SetKeyword(mat, "_ALPHATEST_ON", "_AlphaTest", cutoff > 0);
            SetKeyword(mat, "_G3_FOG_BLACK", "_FogBlack", blend.FogToBlack);
            SetKeyword(mat, "_G3_NO_FOG", "_NoFog", noFog);
            SetKeyword(mat, "_G3_LIT", "_Lit", lit);
            mat.SetColor("_Tint", new Color(gain, gain, gain, 1f));   // > 1 = HDR (additive styles, EmitterRules.AdditiveGain)
            mat.renderQueue = blend.Transparent ? (int)RenderQueue.Transparent + queueOffset : (int)RenderQueue.AlphaTest;
            if (create) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        static void SetKeyword(Material m, string keyword, string prop, bool on)
        {
            m.SetFloat(prop, on ? 1 : 0);
            if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
        }

        // ------------------------------------------------------------------ world placements

        static G3WorldEffectsAsset BakeWorld(TemplateArchive templates, EffectMap map, Func<TemplateFile, G3Vfx> prefab, G3EffectLibrary library, out string summary)
        {
            var asset = LoadOrCreate<G3WorldEffectsAsset>(WorldPath);
            asset.placements.Clear();
            int effects = 0, particles = 0, unknown = 0;
            const string vol = "Projects_compiled/";
            using (var fs = new G3FileSystem(G3WorldImporter.DataDir, "Projects_compiled"))
            {
                // Story-switchable sectors (also those off at the start) are baked too; their placements carry the
                // sector and G3WorldEffects shows them only while G3StorySectors says it is on.
                var layout = WorldLayout.Build(fs.Files.Select(f => f.Path), p => fs.Get(vol + p)?.Read(), includeDisabled: true);
                var story = G3StorySectorList.Load(layout);
                layout.Sectors.RemoveAll(sec => !sec.Enabled && !story.ContainsKey(sec.Name));
                foreach (var s in layout.Sectors)
                foreach (var path in s.Nodes.Concat(s.Lrentdats))
                {
                    var entry = fs.Get(vol + path);
                    if (entry == null) continue;
                    List<WorldEntity> list;
                    try { list = EntityArchive.Read(entry.Read(), false, true); }
                    catch (Exception ex) { Debug.LogWarning($"G3 effects bake: {path}: {ex.Message}"); continue; }
                    if (list == null) continue;
                    string sector = story.TryGetValue(s.Name, out bool atStart) ? s.Name : null;
                    foreach (var e in list)
                    {
                        if (e.RawPropertySets == null || e.IsItem || e.IsNpc || (!e.Enabled && s.Enabled)) continue;
                        foreach (var ps in e.RawPropertySets)
                        {
                            if (ps.ClassName == "gCEffect_PS")
                            {
                                string name = G3.Formats.Items.Props.Str(e.Source, ps, "Effect");
                                if (string.IsNullOrEmpty(name)) continue;
                                if (library.Get(name) == null) { unknown++; continue; }
                                var off = G3.Formats.Items.Props.Vec3(ps, "Offset") ?? new float[3];
                                var rot = Rotation(e.World);
                                asset.placements.Add(new G3WorldEffectPlacement
                                {
                                    name = name, position = new Vector3(e.X, e.Y, e.Z) * M + rot * (V(off) * M), rotation = rot, radius = Radius(name),
                                    sector = sector, enabledAtStart = sector == null || atStart,
                                });
                                effects++;
                            }
                            else if (ps.ClassName == "eCParticle_PS")
                            {
                                var p = prefab(templates.Get(e.Name));
                                if (p == null) { unknown++; continue; }
                                asset.placements.Add(new G3WorldEffectPlacement
                                {
                                    name = e.Name, template = true, position = new Vector3(e.X, e.Y, e.Z) * M, rotation = Rotation(e.World), radius = 40f,
                                    sector = sector, enabledAtStart = sector == null || atStart,
                                });
                                particles++;
                            }
                        }
                    }
                }
            }
            EditorUtility.SetDirty(asset);
            summary = $"world: {effects} placed effects + {particles} placed emitters ({unknown} without visuals)";
            return asset;
        }

        /// <summary>[interp] Streaming radius of a world effect: small lights (candles, glow-worms, flies) 25 m, the rest 50 m.</summary>
        static float Radius(string effect)
        {
            var n = effect.ToLowerInvariant();
            if (n.Contains("candle") || n.Contains("glowworm") || n.Contains("flies") || n.Contains("coalglow")) return 25f;
            return 50f;
        }

        static Quaternion Rotation(float[] m)
        {
            var y = new Vector3(m[4], m[5], m[6]);
            var z = new Vector3(m[8], m[9], m[10]);
            return z.sqrMagnitude > 1e-8f && y.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(z.normalized, y.normalized) : Quaternion.identity;
        }

        // ------------------------------------------------------------------ weather (precipitation + CP loop sounds)

        static void SetupWeather(G3AssetCache cache, string data, List<string> warnings)
        {
            // Re-read the weather states first: the committed G3Weather.asset predated the CP 1.75 (Library) copy and the
            // precipitation fields (Rainy: 3000/s, splash 0 → no splashes; CP: 3100/s, splash 0.4 s × 25, rain loop).
            G3WeatherAsset weather;
            try { weather = G3WeatherImporter.Import(data); }
            catch (Exception ex) { warnings.Add("weather re-import: " + ex.Message); weather = AssetDatabase.LoadAssetAtPath<G3WeatherAsset>(G3WeatherImporter.AssetPath); }
            if (weather == null) { warnings.Add("no G3Weather.asset (run G3/Environment/Import Weather first)"); return; }
            var tex = cache.GetImageTexture("RainAndSnow", false, false);
            if (tex != null)
                weather.precipitationMaterial = MakeMaterial($"{MaterialDir}/G3_Precipitation.mat", tex, EmitterRules.Blend((int)DrawStyle.AlphaBlend), 0, false, true, false);
            else warnings.Add("RainAndSnow texture missing");
            using (var sound = new G3FileSystem(data, "Sound"))
                foreach (var s in weather.states)
                {
                    if (string.IsNullOrEmpty(s.loopSample)) { s.loopClip = null; continue; }
                    var e = sound.GetByName(s.loopSample) ?? sound.GetByName(Path.GetFileNameWithoutExtension(s.loopSample) + ".wav")
                            ?? sound.GetByName(Path.GetFileNameWithoutExtension(s.loopSample) + ".ogg");
                    if (e == null) { warnings.Add($"loop {s.loopSample} not in Sound.pak"); continue; }
                    string path = $"{AudioDir}/{Path.GetFileName(e.Path)}";
                    if (!File.Exists(path)) File.WriteAllBytes(path, e.Read());
                    AssetDatabase.ImportAsset(path);
                    s.loopClip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                }
            EditorUtility.SetDirty(weather);
        }

        // ------------------------------------------------------------------ helpers

        static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null) return a;
            // A file that does not load as T (e.g. baked while its script reference was broken) is replaced.
            if (AssetDatabase.LoadMainAssetAtPath(path) != null || System.IO.File.Exists(path)) AssetDatabase.DeleteAsset(path);
            a = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        static Vector3 V(float[] v) => v == null || v.Length < 3 ? Vector3.zero : new Vector3(v[0], v[1], v[2]);

        static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
