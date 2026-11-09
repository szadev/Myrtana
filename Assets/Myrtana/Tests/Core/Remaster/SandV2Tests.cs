using System;
using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Remaster desert sand (Docs/remaster-world.md section 22): layer classes, desert region, print fill clock,
    /// ripple tile (forks, lee, seamless), warp, patches, fades, sand prints in the shared grid, the grain tile, wisp amount.</summary>
    public class SandV2Tests
    {
        [SetUp]
        public void Defaults() => WorldRemasterLook.Current = WorldLookSettings.Default;

        // ------------------------------------------------------------------ classification

        [Test]
        public void LayerClass_SandVsRockVsPath()
        {
            Assert.That(SandV2.LayerClass("G3_Nature_Dunes_PlainSand_01_Diffuse_01"), Is.EqualTo(SandV2.Loose), "open dunes (11 terrain materials)");
            Assert.That(SandV2.LayerClass("G3_Nature_Dunes_WaveSand_01_Diffuse_01"), Is.EqualTo(SandV2.Loose), "the rippled dune layer");
            Assert.That(SandV2.LayerClass("G3_Nature_Dunes_PlainSand_02_Diffuse_03"), Is.EqualTo(SandV2.Wet), "shore sand (Plainsand_To_Wetsand)");
            Assert.That(SandV2.LayerClass("G3_Nature_Dunes_PlainSand_02_Diffuse_01"), Is.EqualTo(SandV2.Packed), "the path (PlainSand_to_Path B)");
            Assert.That(SandV2.LayerClass("G3_Architecture_SandGround_01_Diffuse_01"), Is.EqualTo(SandV2.Packed), "town sand ground");
            Assert.That(SandV2.LayerClass("g3_nature_dunes_plainsand_01_diffuse_01"), Is.EqualTo(SandV2.Loose), "case-insensitive");
            foreach (var notSand in new[]
                     {
                         "G3_Nature_Wall_Stone_07_Diffuse_04", "G3_Nature_Earthground_01_Diffuse_01", "G3_Nature_Ground_Riverstone_02_Diffuse_02",
                         "G3_Architecture_GroundStone_03_Diffuse_01", "G3_Architecture_SandStone_05_Diffuse_01", "G3_Nature_Ground_Grass_07_Diffuse_01",
                         "G3_Nature_Dunes_Detail_01_Diffuse_01", "G3_Nature_Ground_Snow_01_Diffuse_01", "G3_Nature_Ground_Gravel_01_Diffuse_01", null, "",
                     })
                Assert.That(SandV2.LayerClass(notSand), Is.EqualTo(SandV2.None), notSand ?? "null");
            Assert.That(SandV2.TakesPrints(SandV2.Loose) && SandV2.TakesPrints(SandV2.Wet), Is.True);
            Assert.That(SandV2.TakesPrints(SandV2.Packed) || SandV2.TakesPrints(SandV2.None), Is.False, "no prints on paths or rock");
            Assert.That(SandV2.Ripples(SandV2.Loose), Is.True);
            Assert.That(SandV2.Ripples(SandV2.Wet) || SandV2.Ripples(SandV2.Packed), Is.False, "wet and trodden sand do not ripple");
        }

        [Test]
        public void Region_OnlyVarant()
        {
            Assert.That(SandV2.RegionWeight("Varant"), Is.EqualTo(1f));
            Assert.That(SandV2.RegionWeight("varant_south"), Is.EqualTo(1f));
            Assert.That(SandV2.RegionWeight("Myrtana"), Is.EqualTo(0f), "Myrtana's coast sand stays as it was");
            Assert.That(SandV2.RegionWeight("Nordmar"), Is.EqualTo(0f));
            Assert.That(SandV2.RegionWeight(null), Is.EqualTo(0f));
            Assert.That(SnowV2.RegionWeight("Varant"), Is.EqualTo(0f), "the snow channel stays Nordmar's");
        }

        [Test]
        public void Dilate_GrowsTheDesertByOneCell()
        {
            var m = new byte[5 * 4];
            m[1 * 5 + 2] = 255;
            SandV2.Dilate(m, 5, 4);
            Assert.That(m[0 * 5 + 1] == 255 && m[2 * 5 + 3] == 255 && m[1 * 5 + 2] == 255, Is.True, "neighbours");
            Assert.That(m[3 * 5 + 2], Is.EqualTo(0), "two cells away");
            Assert.That(m[1 * 5 + 4], Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ wind clock (track fill rate)

        [Test]
        public void StormShare_FromTheMix()
        {
            var mix = new List<KeyValuePair<string, float>>
            {
                new KeyValuePair<string, float>("Sunny(Varant)", 1f), new KeyValuePair<string, float>("Sandstormy(Varant)", 3f),
            };
            Assert.That(SandV2.StormShare(mix), Is.EqualTo(0.75f).Within(1e-5));
            Assert.That(SandV2.StormShare(new List<KeyValuePair<string, float>>()), Is.EqualTo(0f));
            Assert.That(SandV2.StormShare("Sunny(Varant)", "Sandstormy(Varant)", 0.4f), Is.EqualTo(0.4f).Within(1e-5));
            Assert.That(SandV2.StormShare("Sandstormy(Varant)", "", 0.7f), Is.EqualTo(1f), "no blend state: the state itself");
            Assert.That(SandV2.StormShare("Snowstorm(Nordmar)", null, 0), Is.EqualTo(0f), "a snow storm blows no sand");
        }

        [Test]
        public void FillRate_CalmSlow_WindFaster_StormFastest_AndFasterThanSnow()
        {
            var s = WorldRemasterLook.Current;
            Assert.That(SandV2.FillSeconds(0, 0.1f), Is.EqualTo(s.sandTrailRefill).Within(0.01f), "calm: sandTrailRefill");
            Assert.That(SandV2.FillSeconds(1, 0.1f), Is.EqualTo(s.sandTrailRefillStorm).Within(0.01f), "full sandstorm");
            float prev = float.MaxValue;
            for (float w = 0; w <= 1.6f; w += 0.1f)
            {
                float t = SandV2.FillSeconds(0, w);
                Assert.That(t, Is.LessThanOrEqualTo(prev + 1e-4f), "more wind never fills slower");
                prev = t;
            }
            Assert.That(SandV2.FillSeconds(0, 1.2f), Is.EqualTo(s.sandTrailRefill / 2.5f).Within(0.01f), "strong wind: 2.5x");
            Assert.That(SandV2.FillSeconds(0.5f, 0.1f), Is.LessThan(SandV2.FillSeconds(0, 1.2f)), "half a storm beats a gusty day");
            Assert.That(s.sandTrailRefill, Is.LessThan(s.snowTrailRefill), "sand fills in faster than snow (no snowfall needed)");
            // a print is fully gone (age 1) after FillSeconds
            double clock = 0;
            float dt = 0.1f;
            int steps = (int)Math.Round(SandV2.FillSeconds(0, 0.1f) / dt);
            for (int i = 0; i < steps; i++) clock = SandV2.ClockStep(clock, 0, 0.1f, dt);
            Assert.That(clock, Is.EqualTo(1.0).Within(1e-3));
            Assert.That(SnowV2.Fade((float)clock), Is.EqualTo(0f).Within(1e-4), "filled in");
            Assert.That(SnowV2.Fade(0.2f), Is.EqualTo(1f), "fresh print: sharp");
            Assert.That(SandV2.ClockStep(5, 1, 1, -1), Is.EqualTo(5.0), "no negative time");
        }

        [Test]
        public void SandPrintDepth_ShallowerThanSnow()
        {
            var s = WorldRemasterLook.Current;
            Assert.That(SandV2.PrintDepthRel(true) * s.snowTrailDepth, Is.EqualTo(s.sandTrailDepth).Within(1e-5), "the grid stores depth / snow depth");
            Assert.That(SandV2.PrintDepthRel(true), Is.LessThan(1f), "sand prints shallower than snow ones (sand-3: 6.5 vs 8 cm)");
            Assert.That(SandV2.PrintDepthRel(false), Is.LessThan(SandV2.PrintDepthRel(true)), "paws a little shallower");
            Assert.That(SandV2.IsSandPrint(SandV2.SandKind + SnowV2.Paw) && !SandV2.IsSandPrint(SnowV2.Round), Is.True);
            Assert.That(SandV2.BaseKind(SandV2.SandKind + SnowV2.Paw), Is.EqualTo(SnowV2.Paw));
            Assert.That(SandV2.BaseKind(SnowV2.Boot), Is.EqualTo(SnowV2.Boot));
        }

        [Test]
        public void Grid_SandPrintsAgeOnTheSandClock_SnowOnTheSnowClock()
        {
            var g = new SnowPrintGrid(64, 256, 64);
            g.Add(1, 1, 0, 1, 0.14f, 0.055f, 0, 10f, 1f, SnowV2.Boot, 1, 0.6f);                         // snow, stamped at snow clock 10
            g.Add(5, 5, 0, 1, 0.14f, 0.055f, 0, 2f, 0.3f, SnowV2.Boot + SandV2.SandKind, 1, 0.8f);      // sand, stamped at sand clock 2
            // snow clock 10.2 (fresh), sand clock 3 (age 1: filled)
            var snow = g.Sample(1, 0, 1.03f, 10.2f, SnowV2.Wall, 3f);
            var sand = g.Sample(5, 0, 5.03f, 10.2f, SnowV2.Wall, 3f);
            Assert.That(snow.depth, Is.GreaterThan(0.9f), "snow print still sharp");
            Assert.That(sand.depth, Is.EqualTo(0f).Within(1e-4), "sand print filled in by the wind");
            var sandFresh = g.Sample(5, 0, 5.03f, 10.2f, SnowV2.Wall, 2.1f);
            Assert.That(sandFresh.depth, Is.EqualTo(0.3f).Within(0.02f), "fresh sand print at its shallow depth (boot shape)");
            // a new sand print in the same cell must not take the live snow print's slot as "gone": ages by its own clock
            Assert.That(g.SnowNow, Is.EqualTo(10f));
            Assert.That(g.SandNow, Is.EqualTo(2f));
        }

        // ------------------------------------------------------------------ ripples (sand-2: Swift-Hohenberg tile, warped fields)

        const int Rn = 128;   // a small tile for the tests (16 crests); the game uses SandV2.RippleSize

        static float Quantile(List<float> v, float q)
        {
            v.Sort();
            return v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)(v.Count * q))];
        }

        [Test]
        public void RippleTile_DeterministicSeamless_CrestsAcrossTheWind()
        {
            var a = SandRipples.Generate(Rn, 5u);
            Assert.That(a, Is.EqualTo(SandRipples.Generate(Rn, 5u)), "deterministic");
            Assert.That(a.Length, Is.EqualTo(Rn * Rn * 4));
            var (gu, gv) = SandRipples.Slopes(Rn, 5u);
            double su = 0, sv = 0, mu = 0, mv = 0;
            for (int i = 0; i < gu.Length; i++) { su += Math.Abs(gu[i]); sv += Math.Abs(gv[i]); mu += gu[i]; mv += gv[i]; }
            Assert.That(su / gu.Length, Is.GreaterThan(0.25), "real relief");
            Assert.That(su / sv, Is.GreaterThan(2.0), "the slope points along the wind (+u): crests run across it");
            Assert.That(Math.Abs(mu / gu.Length) + Math.Abs(mv / gv.Length), Is.LessThan(0.01), "no net tilt (integrable field)");
            // seamless: the height step across the wrap edge is like the steps inside
            var h = SandRipples.Height(Rn, 5u);
            double inner = 0, wrap = 0;
            for (int y = 0; y < Rn; y++)
            {
                for (int x = 0; x < Rn - 1; x++) inner += Math.Abs(h[y * Rn + x] - h[y * Rn + x + 1]);
                wrap += Math.Abs(h[y * Rn + Rn - 1] - h[y * Rn]);
            }
            Assert.That(wrap / Rn, Is.LessThan(inner / (Rn * (Rn - 1)) * 1.6 + 1e-3), "no seam at the tile edge (u)");
            inner = wrap = 0;
            for (int x = 0; x < Rn; x++)
            {
                for (int y = 0; y < Rn - 1; y++) inner += Math.Abs(h[y * Rn + x] - h[(y + 1) * Rn + x]);
                wrap += Math.Abs(h[(Rn - 1) * Rn + x] - h[x]);
            }
            Assert.That(wrap / Rn, Is.LessThan(inner / (Rn * (Rn - 1)) * 1.6 + 1e-3), "no seam at the tile edge (v)");
            Assert.Throws<ArgumentException>(() => SandRipples.Generate(100, 1u));
            Assert.That(SandRipples.CrestsPerTile(1024), Is.EqualTo(128));
        }

        [Test]
        public void RippleTile_CrestsBendForkAndEnd_NotAGrating()
        {
            var h = SandRipples.Height(Rn, 9u);
            // crests per row (upward zero crossings along the wind): about one per wavelength, but not the same in every row
            // (a fork / an end adds or drops one: dislocations), unlike parallel sines
            var counts = new List<float>();
            for (int y = 0; y < Rn; y++)
            {
                int c = 0;
                for (int x = 0; x < Rn; x++) if (h[y * Rn + x] < 0 && h[y * Rn + (x + 1) % Rn] >= 0) c++;
                counts.Add(c);
            }
            float mean = 0;
            foreach (var c in counts) mean += c;
            mean /= Rn;
            Assert.That(mean, Is.EqualTo(Rn / (float)SandRipples.WavePixels).Within(Rn / (float)SandRipples.WavePixels * 0.2f), "one crest per wavelength");
            var distinct = new HashSet<float>(counts);
            Assert.That(distinct.Count, Is.GreaterThanOrEqualTo(2), "crest counts differ between rows: crests fork and end");
            // sinuous: a row correlates poorly with a row 4 wavelengths across (parallel straight crests would give ~1)
            double cor = 0, n0 = 0, n1 = 0;
            int d = SandRipples.WavePixels * 4;
            for (int y = 0; y < Rn; y++)
                for (int x = 0; x < Rn; x++)
                {
                    float p = h[y * Rn + x], q = h[((y + d) % Rn) * Rn + x];
                    cor += p * q; n0 += p * p; n1 += q * q;
                }
            Assert.That(cor / Math.Sqrt(n0 * n1), Is.LessThan(0.6), "crests bend: no regular grating");
        }

        [Test]
        public void RippleTile_LeeSteeperThanStoss()
        {
            var (gu, _) = SandRipples.Slopes(Rn, 5u);
            var up = new List<float>();
            var down = new List<float>();
            foreach (var g in gu) if (g > 0) up.Add(g); else down.Add(-g);
            Assert.That(Quantile(down, 0.95f), Is.GreaterThan(Quantile(up, 0.95f) * 1.3f), "downwind (lee) flank steeper");
        }

        [Test]
        public void RippleWarp_JacobianExact_BendsModerate()
        {
            float wave = WorldRemasterLook.Current.sandRippleWave, e = 1e-3f;
            float maxBend = 0, maxStretch = 0;
            for (int i = 0; i < 400; i++)
            {
                float ra = 1300.37f + i * 0.71f, rc = -420.9f + (i % 37) * 1.13f;
                var w = SandV2.RippleWarp(ra, rc, wave, 3u);
                var pa = SandV2.RippleWarp(ra + e, rc, wave, 3u); var ma = SandV2.RippleWarp(ra - e, rc, wave, 3u);
                var pc = SandV2.RippleWarp(ra, rc + e, wave, 3u); var mc = SandV2.RippleWarp(ra, rc - e, wave, 3u);
                Assert.That(w.dAa, Is.EqualTo((pa.wA - ma.wA) / (2 * e)).Within(0.02f), $"dAa {i}");
                Assert.That(w.dAc, Is.EqualTo((pc.wA - mc.wA) / (2 * e)).Within(0.02f), $"dAc {i}");
                Assert.That(w.dCa, Is.EqualTo((pa.wC - ma.wC) / (2 * e)).Within(0.02f), $"dCa {i}");
                Assert.That(w.dCc, Is.EqualTo((pc.wC - mc.wC) / (2 * e)).Within(0.02f), $"dCc {i}");
                maxBend = Math.Max(maxBend, Math.Abs(w.dAc));
                maxStretch = Math.Max(maxStretch, Math.Abs(w.dAa));
            }
            Assert.That(maxBend, Is.InRange(0.12f, 0.5f), "crests turn by up to ~10-25 degrees");
            Assert.That(maxStretch, Is.InRange(0.08f, 0.45f), "the wavelength varies by up to ~10-30 %");
        }

        [Test]
        public void RipplePatches_PartlySmooth_BlendBothFields()
        {
            float cov = WorldRemasterLook.Current.sandRippleCoverage;
            int full = 0, smooth = 0, n = 0, a = 0, b = 0;
            float min = 1;
            for (int i = 0; i < 120; i++)
                for (int j = 0; j < 120; j++)
                {
                    float x = -2000 + i * 3.1f, z = 500 + j * 2.9f;
                    float p = SandV2.RipplePatch(x, z, cov);
                    min = Math.Min(min, p);
                    if (p > 0.6f) full++;
                    if (p < 0.4f) smooth++;
                    float t = SandV2.RippleBlend(x, z);
                    if (t < 0.1f) a++;
                    if (t > 0.9f) b++;
                    n++;
                }
            Assert.That(full / (float)n, Is.EqualTo(cov).Within(0.2f), "about the coverage rippled");
            Assert.That(smooth / (float)n, Is.GreaterThan(0.06f), "areas nearly smooth (plus the layer edges and slopes the shader fades)");
            Assert.That(min, Is.GreaterThanOrEqualTo(0.25f - 1e-5f), "never perfectly flat");
            Assert.That(a / (float)n, Is.GreaterThan(0.1f), "field A alone somewhere");
            Assert.That(b / (float)n, Is.GreaterThan(0.1f), "field B alone somewhere");
            Assert.That(SandV2.RipplePatch(1, 1, 0), Is.LessThan(0.3f), "coverage 0: smooth");
        }

        [Test]
        public void RippleFades_DistanceCoreAndGlobals()
        {
            Assert.That(SandV2.DistanceFade(5), Is.EqualTo(1f));
            Assert.That(SandV2.DistanceFade(30), Is.EqualTo(0f), "gone by ~28 m");
            Assert.That(SandV2.CoreFade(1), Is.EqualTo(1f));
            Assert.That(SandV2.CoreFade(0.6f), Is.EqualTo(0f), "a layer edge (grass, rock, path near): no ripples");
            var s = WorldRemasterLook.Current;
            var g = SandV2.Sand5Global;
            Assert.That(g[0], Is.EqualTo(s.sandRippleWave * 128).Within(1e-4f), "tile = 128 wavelengths");
            Assert.That(g[1], Is.EqualTo(2 * Math.PI * s.sandRippleHeight / s.sandRippleWave).Within(1e-4f));
            Assert.That(g[1] * s.sandRipples, Is.InRange(0.3f, 0.7f), "moderate default slope (patches and fades take it down)");
            Assert.That(SandV2.RippleTile(0.1f), Is.EqualTo(12.8f).Within(1e-4f));
        }

        [Test]
        public void HeatHaze_DaytimeDesertClearAirOnly()
        {
            Assert.That(SandV2.HeatHaze(60, 1, 0), Is.EqualTo(1f).Within(1e-5), "noon in Varant");
            Assert.That(SandV2.HeatHaze(-20, 1, 0), Is.EqualTo(0f), "night: off");
            Assert.That(SandV2.HeatHaze(5, 1, 0), Is.EqualTo(0f), "low sun (dawn, dusk): off");
            Assert.That(SandV2.HeatHaze(60, 0, 0), Is.EqualTo(0f), "Myrtana / Nordmar: off");
            Assert.That(SandV2.HeatHaze(60, 1, 1), Is.EqualTo(0f), "a sandstorm hides it");
            WorldRemasterLook.Current = new WorldLookSettings { sandHeatHaze = 0 };
            Assert.That(SandV2.HeatHaze(60, 1, 0), Is.EqualTo(0f), "json off");
        }

        [Test]
        public void MicroTile_OffWithoutTile_SizeAndHeightPassed()
        {
            Assert.That(SandV2.Sand7Global(false, 2, 0.006f), Is.EqualTo(new float[4]), "no scan tile: procedural grain only");
            var g = SandV2.Sand7Global(true, 2, 0.006f);
            Assert.That(g[0], Is.EqualTo(0.5f).Within(1e-6), "1 / size");
            Assert.That(g[3], Is.EqualTo(0.006f).Within(1e-6), "parallax height");
            WorldRemasterLook.Current = new WorldLookSettings { sandMicro = 0 };
            Assert.That(SandV2.Sand7Global(true, 2, 0.006f), Is.EqualTo(new float[4]), "json off");
        }

        [Test]
        public void SlopeFade_FlatKeeps_SteepAndLeeLose()
        {
            var (wx, wz) = SandV2.WindDir(0);   // towards +Z
            Assert.That(SandV2.SlopeFade(0, 1, 0, wx, wz), Is.EqualTo(1f).Within(1e-5), "flat");
            float s30 = (float)Math.Sin(30 * Math.PI / 180), c30 = (float)Math.Cos(30 * Math.PI / 180);
            float s15 = (float)Math.Sin(15 * Math.PI / 180), c15 = (float)Math.Cos(15 * Math.PI / 180);
            Assert.That(SandV2.SlopeFade(0, c15, -s15, wx, wz), Is.GreaterThan(0.9f), "15 deg windward slope (normal leans upwind): kept");
            Assert.That(SandV2.SlopeFade(0, c15, s15, wx, wz), Is.LessThan(0.5f), "15 deg lee slope (falls away downwind): mostly smoothed");
            Assert.That(SandV2.SlopeFade(s15, c15, 0, wx, wz), Is.GreaterThan(0.9f), "15 deg slope across the wind: kept");
            Assert.That(SandV2.SlopeFade(0, c30, -s30, wx, wz), Is.LessThan(0.35f), "30 deg: nearly gone");
            Assert.That(SandV2.SlopeFade(0, 0.8f, -0.6f, wx, wz), Is.EqualTo(0f).Within(1e-5), "steeper than the angle of repose: none");
            Assert.That(SandV2.FootprintFade(0.01f, 0.11f), Is.EqualTo(1f), "pixel 1 cm, wave 11 cm: drawn");
            Assert.That(SandV2.FootprintFade(0.06f, 0.11f), Is.EqualTo(0f), "pixel over half a wave: gone (no moire)");
        }

        // ------------------------------------------------------------------ grain tile

        [Test]
        public void GrainTile_SeamlessMeanOneUnitNormals()
        {
            const int n = 64;
            var a = SandDetail.Generate(n, 7u);
            var b = SandDetail.Generate(n, 7u);
            Assert.That(a, Is.EqualTo(b), "deterministic");
            Assert.That(SandDetail.ChannelMean(a, 3), Is.EqualTo(0.5f).Within(0.006f), "albedo factor mean 1: G3's colour stays");
            Assert.That(SandDetail.ChannelMean(a, 2), Is.EqualTo(0.5f).Within(0.006f), "height mean");
            Assert.That(SandDetail.ChannelMean(a, 0), Is.EqualTo(0.5f).Within(0.03f), "normals average flat");
            Assert.That(SandDetail.ChannelMean(a, 1), Is.EqualTo(0.5f).Within(0.03f));
            // seamless: the step across the wrap edge is like the steps inside
            double inner = 0, wrap = 0;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n - 1; x++) inner += Math.Abs(a[(y * n + x) * 4 + 2] - a[(y * n + x + 1) * 4 + 2]);
                wrap += Math.Abs(a[(y * n + n - 1) * 4 + 2] - a[(y * n) * 4 + 2]);
            }
            inner /= n * (n - 1); wrap /= n;
            Assert.That(wrap, Is.LessThan(inner * 1.6 + 2), "no seam at the tile edge");
            // grains: real contrast, some dark mineral grains
            int dark = 0;
            for (int i = 3; i < a.Length; i += 4) if (a[i] < 64) dark++;
            Assert.That(dark, Is.GreaterThan(0));
            for (int i = 0; i < a.Length; i += 4)
            {
                float nx = a[i] / 255f * 2 - 1, ny = a[i + 1] / 255f * 2 - 1;
                Assert.That(nx * nx + ny * ny, Is.LessThan(1.02f), "a tangent normal's xy fits the unit sphere");
            }
            Assert.Throws<ArgumentException>(() => SandDetail.Generate(100, 1u));
        }

        // ------------------------------------------------------------------ wisps, globals

        [Test]
        public void Wisps_CalmFloor_MoreInGusts_FullInAStorm()
        {
            float calm = WorldLookSettings.Default.sandWispCalm;
            Assert.That(SandV2.WispAmount(0.1f, 0), Is.EqualTo(calm).Within(1e-6), "Sunny(Varant) breeze: sand creeps over the crests (sand-3)");
            Assert.That(SandV2.WispAmount(0.4f, 0), Is.EqualTo(calm).Within(1e-6), "gusts of a sunny day");
            Assert.That(SandV2.WispAmount(0.9f, 0), Is.InRange(calm + 0.01f, 0.75f), "strong gusts: more");
            WorldRemasterLook.Current = new WorldLookSettings { sandWispCalm = 0 };
            Assert.That(SandV2.WispAmount(0.1f, 0), Is.EqualTo(0f), "sand-2 behaviour with sandWispCalm 0");
            WorldRemasterLook.Current = WorldLookSettings.Default;
            Assert.That(SandV2.WispAmount(1.2f, 1), Is.EqualTo(1f), "sandstorm");
            WorldRemasterLook.Current = new WorldLookSettings { sandWisps = 0 };
            Assert.That(SandV2.WispAmount(1.2f, 1), Is.EqualTo(0f), "json off");
        }

        [Test]
        public void Globals_OffIsZero_KnobsClamped()
        {
            Assert.That(SandV2.SandGlobal(false), Is.EqualTo(new float[4]));
            Assert.That(SandV2.TrailGlobal(false, 3), Is.EqualTo(new float[4]));
            Assert.That(SandV2.SandGlobal(true)[0], Is.EqualTo(1f));
            var t = SandV2.TrailGlobal(true, 3.5f);
            Assert.That(t[1], Is.EqualTo(3.5f));
            Assert.That(t[2], Is.EqualTo(SandV2.PrintDepthRel(true)));
            var c = new WorldLookSettings { sandRippleWave = 0, sandTrailRefill = -5, sandWispCount = 100000, sandSmoothness = 2 }.Clamped();
            Assert.That(c.sandRippleWave, Is.EqualTo(0.03f));
            Assert.That(c.sandTrailRefill, Is.EqualTo(1f));
            Assert.That(c.sandWispCount, Is.EqualTo(512));
            Assert.That(c.sandSmoothness, Is.EqualTo(0.6f));
            Assert.That(SandV2.Sand4Global[0], Is.LessThan(0.15f), "dry sand is matte");
        }

        // ------------------------------------------------------------------ sand-3 (22.10)

        [Test]
        public void ScanSet_OffWithoutSet_SizesAndMidScale()
        {
            Assert.That(SandV2.Sand8Global(false, 2.14f, 1.8f, 2.48f), Is.EqualTo(new float[4]), "no scan set: sand-2 look");
            var g = SandV2.Sand8Global(true, 2, 1.5f, 2.5f);
            Assert.That(g[0], Is.EqualTo(0.5f).Within(1e-6), "1 / fine size");
            Assert.That(g[1], Is.EqualTo(1f / (1.5f * WorldLookSettings.Default.sandScanMidScale)).Within(1e-6), "mid sampled larger");
            Assert.That(g[2], Is.EqualTo(0.4f).Within(1e-6), "1 / coarse size");
            Assert.That(g[3], Is.EqualTo(1f), "full scan share");
            WorldRemasterLook.Current = new WorldLookSettings { sandScan = 0 };
            Assert.That(SandV2.Sand8Global(true, 2, 1.5f, 2.5f), Is.EqualTo(new float[4]), "json off");
            Assert.That(SandV2.Sand10Global(0.004f)[3], Is.EqualTo(0.004f).Within(1e-6), "fine scan height x sandScanParallax 1");
        }

        [Test]
        public void Grade_KeepsLuminance_TowardsGolden()
        {
            WorldRemasterLook.Current = new WorldLookSettings { sandGrade = 0.5f, sandBright = 1f };
            float r = 0.58f, g = 0.19f, b = 0.06f;   // G3's orange sand (linear)
            var c = SandV2.Grade(r, g, b);
            float l0 = 0.2126f * r + 0.7152f * g + 0.0722f * b, l1 = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            Assert.That(l1, Is.EqualTo(l0).Within(0.01f), "luminance kept (gold target has luminance ~1)");
            Assert.That(c.g / c.r, Is.GreaterThan(g / r), "less red, more golden");
            WorldRemasterLook.Current = new WorldLookSettings { sandGrade = 0, sandBright = 1f };
            var o = SandV2.Grade(r, g, b);
            Assert.That(o.r, Is.EqualTo(r).Within(1e-6)); Assert.That(o.b, Is.EqualTo(b).Within(1e-6));
        }

        [Test]
        public void NoiseTile_MatchesNoiseD_AndWraps()
        {
            var t = SandV2.NoiseTile();
            int r = SandV2.NoiseTileRes, n = SandV2.NoiseTileCells * r;
            Assert.That(t.Length, Is.EqualTo(n * n * 4));
            foreach (var (x, y) in new[] { (0, 0), (5, 9), (130, 77), (n - 1, n - 2) })
            {
                if (x / r >= SandV2.NoiseTileCells - 1 || y / r >= SandV2.NoiseTileCells - 1) continue;   // the wrapped cell
                var e = SandV2.NoiseD((float)x / r, (float)y / r, 0);
                int o = (y * n + x) * 4;
                Assert.That(t[o], Is.EqualTo(e.v).Within(1e-5), "value");
                Assert.That(t[o + 1], Is.EqualTo(e.dx).Within(1e-4), "d/dx");
                Assert.That(t[o + 2], Is.EqualTo(e.dy).Within(1e-4), "d/dy");
            }
            // periodic: the last cell's far corner is the first cell's lattice value
            int last = ((3 * r) * n + (n - 1)) * 4;
            float fx = (float)(r - 1) / r, ux = fx * fx * (3 - 2 * fx);
            float a0 = SandV2.Hash(SandV2.NoiseTileCells - 1, 3, 0), b0 = SandV2.Hash(0, 3, 0);
            Assert.That(t[last], Is.EqualTo(a0 + (b0 - a0) * ux).Within(1e-5), "wrapped lattice");
        }

        [Test]
        public void RippleFarFade_AndSand3Knobs()
        {
            WorldRemasterLook.Current = new WorldLookSettings { sandRippleFar = 40 };
            Assert.That(SandV2.FarFade(5), Is.EqualTo(1f));
            Assert.That(SandV2.FarFade(40), Is.EqualTo(0f).Within(1e-6));
            Assert.That(SandV2.FarFade(25), Is.InRange(0.05f, 0.95f));
            var c = new WorldLookSettings { sandScan = 5, sandGrade = -1, sandRippleFar = 1, sandMegaScale = 0 }.Clamped();
            Assert.That(c.sandScan, Is.EqualTo(1f)); Assert.That(c.sandGrade, Is.EqualTo(0f));
            Assert.That(c.sandRippleFar, Is.EqualTo(10f)); Assert.That(c.sandMegaScale, Is.EqualTo(1f));
            Assert.That(SandV2.Sand12Global[2], Is.EqualTo(1f), "POM on by default");
            Assert.That(SandV2.Sand11Global[1], Is.GreaterThan(3f), "mega ripples several wavelengths");
        }
    }
}
