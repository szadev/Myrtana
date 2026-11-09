using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Effects;
using G3.Formats.Audio;
using G3.Formats.Effects;
using G3.Formats.Pak;
using G3.Formats.Templates;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>G3 particle emitter rules (Docs/formats-effects.md §3) and the eCParticle_PS reader on the real data.</summary>
    public class EffectRulesTests
    {
        [Test]
        public void ScreenParts_ByTemplateName_ProjectedOntoTheNearPlane()
        {
            Assert.IsTrue(ScreenSpriteRules.IsScreenTemplate("VFX_Magic_SoulTravel_ScreenLoop_01"));
            Assert.IsTrue(ScreenSpriteRules.IsScreenTemplate("VFX_Magic_SoulTravel_WhiteBlend_01"));
            Assert.IsTrue(ScreenSpriteRules.IsScreenTemplate("VFX_Magic_Electricity_HeavenZap_Screen_01"));
            Assert.IsFalse(ScreenSpriteRules.IsScreenTemplate("VFX_Magic_SoulTravel_Ghost_01"));
            Assert.IsFalse(ScreenSpriteRules.IsScreenTemplate(null));
            // a 80 m sprite 4 m ahead, 1 m up: on the plane at 0.5 m the same screen rectangle (scale 0.125)
            Assert.IsTrue(ScreenSpriteRules.ProjectToPlane(0f, 1f, 4f, 80f, 0.5f, out float cx, out float cy, out float half));
            Assert.AreEqual(0f, cx, 1e-5f);
            Assert.AreEqual(0.125f, cy, 1e-5f);
            Assert.AreEqual(5f, half, 1e-5f);
            Assert.IsFalse(ScreenSpriteRules.ProjectToPlane(0f, 0f, -2f, 80f, 0.5f, out _, out _, out _), "behind the camera");
        }

        [Test]
        public void ScreenSprites_RollAroundTheirCentre_WithoutTheAdditiveGain()
        {
            ScreenSpriteRules.Corner(1f, 2f, 3f, 0f, 0, out float x, out float y);
            Assert.AreEqual(-2f, x, 1e-5f); Assert.AreEqual(-1f, y, 1e-5f);
            ScreenSpriteRules.Corner(1f, 2f, 3f, 0f, 2, out x, out y);
            Assert.AreEqual(4f, x, 1e-5f); Assert.AreEqual(5f, y, 1e-5f);
            // a quarter turn: bottom-left goes to bottom-right
            ScreenSpriteRules.Corner(0f, 0f, 1f, 90f, 0, out x, out y);
            Assert.AreEqual(1f, x, 1e-5f); Assert.AreEqual(-1f, y, 1e-5f);
            Assert.AreEqual(1f, ScreenSpriteRules.ScreenGain);
            Assert.Less(ScreenSpriteRules.ScreenGain, EmitterRules.AdditiveGain);
        }

        [Test]
        public void RateFallsBackToMaxOverLifetime()
        {
            Assert.AreEqual(5f, EmitterRules.EmissionRate(25, 5, 2));
            Assert.AreEqual(50f, EmitterRules.EmissionRate(100, 0, 2));
        }

        [Test]
        public void OneShotIsABurstWhenTheRateIsHuge()
        {
            Assert.AreEqual(0f, EmitterRules.OneShotEmitSeconds(50, 10000));
            Assert.AreEqual(2f, EmitterRules.OneShotEmitSeconds(20, 10), 1e-5);
        }

        [Test]
        public void FrameGridCountsCuts()
        {
            // torch: NumU 5, NumV 1, frames 0..11
            Assert.AreEqual(12, EmitterRules.FrameCount(5, 1));
            Assert.AreEqual(1, EmitterRules.FrameCount(0, 0));
            Assert.AreEqual(0f, EmitterRules.FrameAt(0f, 5, 1, 0, 11));
            Assert.AreEqual(6f, EmitterRules.FrameAt(0.5f, 5, 1, 0, 11));
            Assert.AreEqual(11f, EmitterRules.FrameAt(1f, 5, 1, 0, 11));
            Assert.AreEqual(3f, EmitterRules.FrameAt(0.7f, 3, 1, 3, 3));
            Assert.AreEqual(7f, EmitterRules.FrameAt(1f, 3, 1, 0, 20), "end clamps to the grid");
        }

        [Test]
        public void RepeatsWrapTime()
        {
            Assert.AreEqual(0.4f, EmitterRules.RepeatTime(0.4f, 0), 1e-6);
            Assert.AreEqual(0.6f, EmitterRules.RepeatTime(0.3f, 1), 1e-5);
            Assert.AreEqual(0.2f, EmitterRules.RepeatTime(0.6f, 1), 1e-5);
            Assert.AreEqual(1f, EmitterRules.RepeatTime(1f, 1));
        }

        [Test]
        public void FadeFactorsStartAndEndAtTheirFactor()
        {
            // factor 1 = no fade (the retail default)
            Assert.AreEqual(1f, EmitterRules.FadeFactor(0f, 2f, true, 1f, 1f, true, 1f, 1f));
            // fade in from 0 over 1 s
            Assert.AreEqual(0f, EmitterRules.FadeFactor(0f, 2f, true, 1f, 0f, false, 0, 0), 1e-6);
            Assert.AreEqual(0.5f, EmitterRules.FadeFactor(0.5f, 2f, true, 1f, 0f, false, 0, 0), 1e-6);
            Assert.AreEqual(1f, EmitterRules.FadeFactor(1.5f, 2f, true, 1f, 0f, false, 0, 0), 1e-6);
            // fade out to 0 from 1 s to 2 s
            Assert.AreEqual(0.5f, EmitterRules.FadeFactor(1.5f, 2f, false, 0, 0, true, 1f, 0f), 1e-6);
            Assert.AreEqual(0f, EmitterRules.FadeFactor(2f, 2f, false, 0, 0, true, 1f, 0f), 1e-6);
        }

        [Test]
        public void DrawStylesBlend()
        {
            var add = EmitterRules.Blend((int)DrawStyle.Brighten);
            Assert.AreEqual(BlendFactor.SrcAlpha, add.Src);
            Assert.AreEqual(BlendFactor.One, add.Dst);
            Assert.IsTrue(add.FogToBlack);
            var alpha = EmitterRules.Blend((int)DrawStyle.AlphaBlend);
            Assert.AreEqual(BlendFactor.OneMinusSrcAlpha, alpha.Dst);
            Assert.IsFalse(EmitterRules.Blend((int)DrawStyle.Invisible).Visible);
        }

        [Test]
        public void SampleTimesRepeatAndResample()
        {
            var t = EmitterRules.SampleTimes(new[] { 0f, 0.5f, 1f }, 1, 8);
            CollectionAssert.AreEqual(new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }, t);
            var many = EmitterRules.SampleTimes(Enumerable.Range(0, 20).Select(i => i / 19f).ToList(), 0, 8);
            Assert.AreEqual(8, many.Count);
        }

        [Test]
        public void DirectedVelocityPointsAwayFromTheOwner()
        {
            float vx = 3, vy = 0, vz = 4;
            ParticleSpawn.DirectedVelocity(1, 0, 10, 0, ref vx, ref vy, ref vz);
            Assert.AreEqual(5f, vy, 1e-5);
            Assert.AreEqual(0f, vx, 1e-5);
            vx = 3; vy = 0; vz = 4;
            ParticleSpawn.DirectedVelocity(2, 0, 10, 0, ref vx, ref vy, ref vz);
            Assert.AreEqual(-5f, vy, 1e-5);
        }

        [Test]
        public void SpherePointStaysInTheShell()
        {
            var rng = new Random(3);
            for (int i = 0; i < 200; i++)
            {
                ParticleSpawn.SpherePoint(() => (float)rng.NextDouble(), 5, 10, out var x, out var y, out var z);
                float r = (float)Math.Sqrt(x * x + y * y + z * z);
                Assert.That(r, Is.InRange(4.999f, 10.001f));
            }
        }

        [Test]
        public void Range3IsMinVectorThenMaxVector()
        {
            var v = new byte[24];
            float[] f = { -1, -5, -1, 1, 100, 1 };
            for (int i = 0; i < 6; i++) BitConverter.GetBytes(f[i]).CopyTo(v, i * 4);
            var r = new Range1[3];
            Range3.Read(v, r);
            Assert.AreEqual(-1f, r[0].Min); Assert.AreEqual(1f, r[0].Max);
            Assert.AreEqual(-5f, r[1].Min); Assert.AreEqual(100f, r[1].Max);
            Assert.AreEqual(-1f, r[2].Min); Assert.AreEqual(1f, r[2].Max);
        }

        [Test]
        public void ZeroMaxVelocityMeansNoLimit()
        {
            Assert.IsTrue(float.IsPositiveInfinity(EmitterRules.VelocityLimit(0, 0, 0)));
            Assert.AreEqual(10000f, EmitterRules.VelocityLimit(1e4f, 1e4f, 1e4f));
        }

        [Test]
        public void CollidingParticlesLandOrBounce()
        {
            // blood: MaxCollisions 1 → stops where it hits
            Assert.IsTrue(EmitterRules.Collision(true, true, true, 1, 1, 1, 1, out float b, out float d));
            Assert.AreEqual(0f, b); Assert.AreEqual(1f, d);
            // footstep dust: 2 collisions, damping x/z 0.5, y 0.8 → keep 50 % sideways, 80 % up
            Assert.IsTrue(EmitterRules.Collision(true, true, true, 2, 0.5f, 0.8f, 0.5f, out b, out d));
            Assert.AreEqual(0.5f, d, 1e-4f);
            Assert.AreEqual(0.8f, b * (1 - d), 1e-4f);
            // off: no collision, or dynamic objects only
            Assert.IsFalse(EmitterRules.Collision(false, true, true, 1, 1, 1, 1, out b, out d));
            Assert.IsFalse(EmitterRules.Collision(true, false, true, 1, 1, 1, 1, out b, out d));
        }

        [Test]
        public void StretchedDropsDieWhereTheyStop()
        {
            // fix-132: blood drops (Stretch, MaxCollisions 1) die at the hit instead of flickering on the ground
            EmitterRules.Collision(true, true, true, 1, 1, 1, 1, out float b, out float d);
            Assert.IsTrue(EmitterRules.DiesAtHit(true, true, b, d));
            // billboards (fire rain, summon stones) land and stay; bouncing stretched sparks keep bouncing
            Assert.IsFalse(EmitterRules.DiesAtHit(false, true, b, d));
            EmitterRules.Collision(true, true, true, 2, 0.5f, 0.8f, 0.5f, out b, out d);
            Assert.IsFalse(EmitterRules.DiesAtHit(true, true, b, d));
            Assert.IsFalse(EmitterRules.DiesAtHit(true, false, 0, 1));
        }

        [Test]
        public void SpritesRollAroundZ()
        {
            Assert.AreEqual(2, EmitterRules.SpinAxis(-3.14f, 3.14f, 0, 0));
            Assert.AreEqual(2, EmitterRules.SpinAxis(0, 0, -0.01f, 0.01f));
            Assert.AreEqual(0, EmitterRules.SpinAxis(0, 0, 0, 0));
        }

        [Test]
        public void AdditiveStylesAreBrighterAndDrawAfterAlphaBlend()
        {
            Assert.Greater(EmitterRules.ColorGain((int)DrawStyle.Brighten), 1f);
            Assert.AreEqual(1f, EmitterRules.ColorGain((int)DrawStyle.AlphaBlend));
            Assert.Greater(EmitterRules.QueueOffset((int)DrawStyle.Brighten), EmitterRules.QueueOffset((int)DrawStyle.AlphaBlend));
        }

        [Test]
        public void FaintAdditiveStacks_AreDimmedToG3sGammaSum_DenseFlamesKeepTheirAlpha()
        {
            // fix-124: torch BigGlow VFX_Env_Fire_Torch_Glow_01 (max 5, 1/s, life 2-3 s, alpha 0.1, Brighten)
            float glowN = EmitterRules.SteadyCount(5, 1f, true, 2.5f);
            Assert.AreEqual(2.5f, glowN, 1e-4f);
            float k = EmitterRules.AdditiveAlphaScale((int)DrawStyle.Brighten, glowN, 0.1f);
            Assert.AreEqual(Math.Pow(0.25, 1.2) / EmitterRules.AdditiveGain, k, 1e-4);
            // stack brightness after the fix (linear) = G3's gamma-space sum (0.25 sRGB)^2.2
            Assert.AreEqual(Math.Pow(0.25, 2.2), glowN * 0.1f * k * EmitterRules.AdditiveGain, 1e-4);
            // torch flame VFX_Env_Fire_Torch_04 (300/s, life 0.2-0.4 s, alpha 1) and sparks (one-shot 4, alpha 1): unchanged
            Assert.AreEqual(1f, EmitterRules.AdditiveAlphaScale((int)DrawStyle.Brighten, EmitterRules.SteadyCount(300, 300f, true, 0.3f), 1f));
            Assert.AreEqual(1f, EmitterRules.AdditiveAlphaScale((int)DrawStyle.Brighten, EmitterRules.SteadyCount(4, 4f, false, 5f), 1f));
            // alpha-blended styles are never touched
            Assert.AreEqual(1f, EmitterRules.AdditiveAlphaScale((int)DrawStyle.AlphaBlend, 1f, 0.1f));
        }

        [Test]
        public void Revolution_IsRadiansPerSecond_AboutTheParticlesOwnCentreOnWorldAxes()
        {
            // fix-179: DoProcessV2 feeds RevolutionsPerSecond × factor × dt to sin/cos unconverted (no 2π)
            Assert.AreEqual(0.65f * 0.5f, EmitterRules.RevolutionRadians(0.65f, 1f, 0.5f), 1e-6f);
            Assert.AreEqual(0f, EmitterRules.RevolutionRadians(0.65f, 0f, 0.5f), 1e-6f);
            // only world-space (Independent) emitters are revolved by the runtime; Relative keep Unity's local orbital
            Assert.IsTrue(EmitterRules.RevolvesInWorld(true, true));
            Assert.IsFalse(EmitterRules.RevolvesInWorld(true, false));
            Assert.IsFalse(EmitterRules.RevolvesInWorld(false, true));
            // a quarter turn about y round (1, 2, 3): +z goes to +x (Unity/G3 left-handed), the height stays
            float x = 1f, y = 5f, z = 4f;
            EmitterRules.Revolve(ref x, ref y, ref z, 1f, 2f, 3f, 0f, (float)(Math.PI / 2), 0f);
            Assert.AreEqual(2f, x, 1e-5f); Assert.AreEqual(5f, y, 1e-5f); Assert.AreEqual(3f, z, 1e-5f);
            // the radius about the centre is kept for any angle
            x = 1.3f; y = 0.2f; z = -0.4f;
            EmitterRules.Revolve(ref x, ref y, ref z, 0f, 0f, 0f, 0.3f, 1.1f, -0.7f);
            Assert.AreEqual(Math.Sqrt(1.3 * 1.3 + 0.2 * 0.2 + 0.4 * 0.4), Math.Sqrt(x * x + y * y + z * z), 1e-5);
        }

        [Test]
        public void FirePaletteLightsAreFireLights()
        {
            Assert.IsTrue(FireLightRules.IsFireColor(0.992f, 0.769f, 0.529f));   // torch, camp fire, bowl, fireplace
            Assert.IsTrue(FireLightRules.IsFireColor(1f, 0.796f, 0.533f));       // candle
            Assert.IsTrue(FireLightRules.IsFireColor(1f, 0.808f, 0.345f));       // fire arrow
            Assert.IsFalse(FireLightRules.IsFireColor(0.545f, 0.875f, 0.792f));  // glow-worms
            Assert.IsFalse(FireLightRules.IsFireColor(0.941f, 0.976f, 1f));      // white
            Assert.IsFalse(FireLightRules.IsFireColor(0.235f, 0.235f, 0.31f));   // ambient
            for (float t = 0; t < 20; t += 0.037f)
                Assert.That(FireLightRules.Flicker(t, 1.7f), Is.InRange(1f - FireLightRules.FlickerDepth - 1e-4f, 1f + 1e-4f));
        }

        // ------------------------------------------------------------------ real data

        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";

        static string Data()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Templates.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            return data;
        }

        static TemplateArchive s_templates;
        static TemplateArchive Templates()
        {
            if (s_templates != null) return s_templates;
            using var fs = new G3FileSystem(Data(), "Templates");
            return s_templates = TemplateArchive.Load(fs.Files.Select(e => new KeyValuePair<string, Func<byte[]>>(e.Path, e.Read)));
        }

        [Test]
        public void TorchEmitterReadsAsInTheEditor()
        {
            var f = Templates().Get("VFX_Env_Fire_Torch_01");
            Assert.NotNull(f);
            var d = VfxTemplateDef.From(f).Emitter;
            Assert.AreEqual("VFX_Env_Torch_01.tga", d.Texture);
            Assert.AreEqual(DrawStyle.Brighten, d.DrawStyle);
            Assert.AreEqual(25, d.MaxParticles);
            Assert.AreEqual(5f, d.ParticlesPerSecond);
            Assert.AreEqual(2f, d.Lifetime.Max);
            Assert.AreEqual(15f, d.Acceleration[1]);
            Assert.AreEqual(5, d.NumU);
            Assert.AreEqual(11, d.SubdivisionEnd);
            Assert.IsTrue(d.UseSubdivisionScale);
            Assert.AreEqual(2, d.SubdivisionScale.Count);
            Assert.AreEqual(11f, d.SubdivisionScale[1].A);
            Assert.AreEqual(5, d.ColorScale.Count > 0 ? 5 : 0, "colour scale present (unused: UseColorScale off)");
        }

        [Test]
        public void TorchFlamesRiseAndHaveTheirSize()
        {
            // bCRange3 = min vector, max vector: the hand torch's flames shoot up to 1 m/s upwards (G3 y up), not sideways.
            var d = VfxTemplateDef.From(Templates().Get("VFX_Env_Fire_Torch_04")).Emitter;
            Assert.AreEqual(-5f, d.StartVelocity[1].Min); Assert.AreEqual(100f, d.StartVelocity[1].Max);
            Assert.AreEqual(-1f, d.StartVelocity[2].Min); Assert.AreEqual(1f, d.StartVelocity[2].Max);
            Assert.AreEqual(15f, d.StartSize[0].Min); Assert.AreEqual(23f, d.StartSize[0].Max);
            var w = VfxTemplateDef.From(Templates().Get("VFX_Env_Fire_Torch_01")).Emitter;   // wall torch flame: 15–20 × 25–40 cm
            Assert.AreEqual(15f, w.StartSize[0].Min); Assert.AreEqual(20f, w.StartSize[0].Max);
            Assert.AreEqual(25f, w.StartSize[1].Min); Assert.AreEqual(40f, w.StartSize[1].Max);
            Assert.AreEqual(-3f, w.StartVelocity[1].Min); Assert.AreEqual(3f, w.StartVelocity[1].Max);
        }

        [Test]
        public void BurningBodyFlames_AreG3sFireTarget()
        {
            // fix-179: eff_magic_firespell_target_01 spawns VFX_Magic_Fire_FireTarget_01 on six bones; one emitter:
            var d = VfxTemplateDef.From(Templates().Get("VFX_Magic_Fire_FireTarget_01")).Emitter;
            Assert.AreEqual(20, d.MaxParticles); Assert.AreEqual(20f, d.ParticlesPerSecond);
            Assert.AreEqual(0.5f, d.Lifetime.Min); Assert.AreEqual(1f, d.Lifetime.Max);
            Assert.AreEqual(70f, d.StartSize[0].Min); Assert.AreEqual(180f, d.StartSize[0].Max);
            Assert.IsTrue(d.UniformSize); Assert.IsTrue(d.UseSizeScale);
            Assert.AreEqual(0.4f, d.SizeScale[0].A, 1e-6);
            Assert.AreEqual(ParticleSpace.Independent, d.Space);
            Assert.IsTrue(d.UseRevolution);
            Assert.AreEqual(0.5f, d.RevolutionsPerSecond[1].Min, 1e-6); Assert.AreEqual(0.8f, d.RevolutionsPerSecond[1].Max, 1e-6);
            Assert.IsTrue(EmitterRules.RevolvesInWorld(d.UseRevolution, d.Space == ParticleSpace.Independent));
            Assert.IsTrue(d.AlphaTest);
            Assert.AreEqual(DrawStyle.Brighten, d.DrawStyle);
        }

        [Test]
        public void FireballHoldHasOrangeColourScale()
        {
            var d = VfxTemplateDef.From(Templates().Get("VFX_Magic_Fire_Fireball_Hold_01")).Emitter;
            Assert.IsTrue(d.UseColorScale);
            Assert.AreEqual(3, d.ColorScale.Count);
            Assert.AreEqual(1f, d.ColorScale[0].A, 1e-6);     // red
            Assert.AreEqual(0.8f, d.ColorScale[0].B, 1e-6);   // green
            Assert.AreEqual(0f, d.ColorScale[0].D, 1e-6);     // alpha starts at 0
            Assert.IsTrue(d.UseSizeScale);
            Assert.AreEqual(0.4f, d.SizeScale[0].A, 1e-6);
            Assert.AreEqual(ParticleSpace.Independent, d.Space);
        }

        /// <summary>
        /// fix-182 [owner]: the torch halo is one template, the only VFX_Env_* one with the BigGlow texture, carries no light,
        /// and belongs to the carried torch (eff_item_torch_02) only: wall torches (eff_item_torch_01) and other fires keep
        /// every part when the "Torch glow" option hides it.
        /// </summary>
        [Test]
        public void TorchGlow_IsTheCarriedTorchsOnlyBigGlowPart_WithoutLight()
        {
            Assert.IsTrue(EmitterRules.IsTorchGlow("VFX_Env_Fire_Torch_Glow_01"));
            Assert.IsFalse(EmitterRules.IsTorchGlow("VFX_Env_Fire_Torch_04"));
            Assert.IsFalse(EmitterRules.IsTorchGlow(null));
            var envBigGlow = new List<string>();
            foreach (var f in Templates().Files)
            {
                if (f.IsDeleted || !f.Name.StartsWith("VFX_Env_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!f.Headers.Any(h => h.Get("eCParticle_PS") != null)) continue;
                var d = VfxTemplateDef.From(f);
                if (d.Emitter != null && string.Equals(d.Emitter.Texture, "VFX_Magic_BigGlow_01.tga", StringComparison.OrdinalIgnoreCase))
                    envBigGlow.Add(f.Name);
            }
            CollectionAssert.AreEqual(new[] { EmitterRules.TorchGlowTemplate }, envBigGlow);
            var glow = VfxTemplateDef.From(Templates().Get(EmitterRules.TorchGlowTemplate));
            Assert.IsNull(glow.Light, "the torch's light is the item's, not the halo's");
            Assert.AreEqual(DrawStyle.Brighten, glow.Emitter.DrawStyle);

            using var fs = new G3FileSystem(Data(), "Library");
            var map = EffectMap.Read(fs.GetByName("g3.efm").Read());
            string Name(EffectSpawn s) => Templates().Get(s.Template)?.Name ?? s.Template.ToString();
            var carried = map.Get("eff_item_torch_02").Spawns.Select(Name).ToList();
            Assert.Contains(EmitterRules.TorchGlowTemplate, carried);
            Assert.Contains("VFX_Env_Fire_Torch_04", carried, "the flame stays");
            foreach (var e in new[] { "eff_item_torch_01", "eff_env_fire_bowl_01", "eff_env_fire_campfire_02", "eff_env_fire_fireplace_01" })
                Assert.IsFalse(map.Get(e).Spawns.Select(Name).Any(EmitterRules.IsTorchGlow), e);
        }

        [Test]
        public void EveryEffectTemplateParses()
        {
            int n = 0;
            foreach (var f in Templates().Files)
            {
                if (f.IsDeleted || !f.Headers.Any(h => h.Get("eCParticle_PS") != null)) continue;
                Assert.DoesNotThrow(() => VfxTemplateDef.From(f), f.Name);
                n++;
            }
            Assert.Greater(n, 400);
        }

        [Test]
        public void EffectMapKeepsSpawnLinks()
        {
            using var fs = new G3FileSystem(Data(), "Library");
            var map = EffectMap.Read(fs.GetByName("g3.efm").Read());
            var heal = map.Get("eff_magic_fullheal_load_01");
            Assert.AreEqual(2, heal.Spawns.Count);
            Assert.AreEqual(2, heal.Spawns[0].Link);
            Assert.AreEqual("Hero_Right_Hand_Hand_1", heal.Spawns[0].BoneName);
            var impact = map.Get("eff_magic_fireball_impact_01");
            Assert.IsTrue(impact.Spawns.All(s => s.Link == 0 && !s.AutoKill));
            var hit = map.Get("eff_col_wh_02_me_fl");
            Assert.IsTrue(hit.Spawns.Any(s => Templates().Get(s.Template)?.Name == "VFX_Col_Fight_Blood_01"), "body hits spawn blood");
        }

        [Test]
        public void HeatHaze_EmitsOnlyNear()
        {
            Assert.AreEqual(EmitterRules.HeatHazeDistance, EmitterRules.PartCullDistance(true, 90f));
            Assert.AreEqual(90f, EmitterRules.PartCullDistance(false, 90f));
            Assert.AreEqual(20f, EmitterRules.PartCullDistance(true, 20f), "a shorter system cull distance wins");
        }
    }
}
