using System;
using System.Collections.Generic;
using System.Linq;
using G3.Unity;
using G3.Weather;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>
    /// fix-215: weather zones layered instead of summed, the time cross-fade of the state weights and the N-state mix
    /// consumers (owner: in Nordmar west of the Hammer clan the weather switched at a visible line).
    /// </summary>
    public class WeatherBlendTests
    {
        static readonly Dictionary<string, float> Env = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // The zones at the owner's spot (G3Cli audio zones): Myrtana_WeatherZone_01 (r 8/10 km, factor 1 everywhere),
        // Nordmar_WeatherZone_04 (r 300/400 m) and a Nordmar_High sphere (r 40/50 m) whose factor ramps over 10 m.
        static List<ZoneWeight> HammerClan(float sphere, float circle = 1f) => new List<ZoneWeight>
        {
            new ZoneWeight("Myrtana", 1f, 10000f),
            new ZoneWeight("Nordmar", circle, 400f),
            new ZoneWeight("Nordmar", sphere, 50f),
        };

        [Test]
        public void Layer_InsideNordmar_IsAllNordmar_WhateverTheSmallZonesDo()
        {
            for (float f = 0; f <= 1.0001f; f += 0.1f)
            {
                float left = WeatherZoneBlend.Layer(HammerClan(f), Env, "Myrtana");
                Assert.That(Env["Nordmar"], Is.EqualTo(1f).Within(1e-5f), $"sphere {f}");
                Assert.That(Env.ContainsKey("Myrtana") ? Env["Myrtana"] : 0f, Is.EqualTo(0f).Within(1e-5f));
                Assert.That(left, Is.EqualTo(0f).Within(1e-5f));
            }
        }

        [Test]
        public void Layer_AtTheRegionEdge_BlendsLinearlyOverTheZoneRamp()
        {
            float prev = 1f;
            for (int i = 0; i <= 100; i++)
            {
                float f = 1f - i / 100f;   // walking out through the 100 m ramp of the Nordmar circle
                WeatherZoneBlend.Layer(HammerClan(0f, f), Env, "Myrtana");
                float n = Env.TryGetValue("Nordmar", out var v) ? v : 0f;
                Assert.That(n, Is.EqualTo(f).Within(1e-5f));
                Assert.That(n + (Env.TryGetValue("Myrtana", out var m) ? m : 0f), Is.EqualTo(1f).Within(1e-5f));
                Assert.That(prev - n, Is.LessThanOrEqualTo(0.0101f), "no step");
                prev = n;
            }
        }

        [Test]
        public void Layer_SmallestZoneFirst_AndTheRestToTheDefault()
        {
            var zones = new List<ZoneWeight>
            {
                new ZoneWeight("Varant", 0.5f, 1000f),
                new ZoneWeight("Cave", 0.4f, 20f),
            };
            float left = WeatherZoneBlend.Layer(zones, Env, "Myrtana");
            Assert.That(zones[0].Environment, Is.EqualTo("Cave"));
            Assert.That(Env["Cave"], Is.EqualTo(0.4f).Within(1e-5f));
            Assert.That(Env["Varant"], Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(Env["Myrtana"], Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(left, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(Env.Values.Sum(), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void Layer_NoZone_IsTheDefault_NoDefault_IsEmpty()
        {
            WeatherZoneBlend.Layer(new List<ZoneWeight>(), Env, "Myrtana");
            Assert.That(Env["Myrtana"], Is.EqualTo(1f));
            WeatherZoneBlend.Layer(new List<ZoneWeight>(), Env, null);
            Assert.That(Env, Is.Empty);
        }

        static Dictionary<string, float> W(params (string k, float v)[] e) =>
            e.ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);

        [Test]
        public void Crossfade_SwapTakesFadeSeconds_Linear_AndKeepsTheSum()
        {
            var x = new WeightCrossfade(6f);
            x.Snap(W(("Sunny", 1f)));
            var to = W(("Snowy(Nordmar)", 1f));
            float t = 0, prevSnow = 0;
            while (t < 5.9f)
            {
                var w = x.Step(to, 0.1f);
                t += 0.1f;
                float snow = w.TryGetValue("Snowy(Nordmar)", out var s) ? s : 0f;
                Assert.That(w.Values.Sum(), Is.EqualTo(1f).Within(1e-4f));
                Assert.That(snow - prevSnow, Is.LessThanOrEqualTo(0.1f / 6f + 1e-5f), "never faster than 1/fade per second");
                Assert.That(snow, Is.EqualTo(t / 6f).Within(1e-3f));
                prevSnow = snow;
            }
            var end = x.Step(to, 0.2f);
            Assert.That(end.Count, Is.EqualTo(1));
            Assert.That(end["Snowy(Nordmar)"], Is.EqualTo(1f));
        }

        [Test]
        public void Crossfade_SlowScheduleFades_PassThrough()
        {
            var x = new WeightCrossfade(6f);
            x.Snap(W(("Sunny", 1f), ("Rainy", 0f)));
            for (int i = 1; i <= 100; i++)
            {
                float r = i * 0.001f;   // a schedule fade: far slower than 1/6 per second at 60 fps
                var w = x.Step(W(("Sunny", 1f - r), ("Rainy", r)), 1f / 60f);
                Assert.That(w["Rainy"], Is.EqualTo(r).Within(1e-6f));
            }
        }

        [Test]
        public void Crossfade_FirstStepAndSnap_TakeTheTarget()
        {
            var x = new WeightCrossfade(6f);
            Assert.That(x.HasValue, Is.False);
            Assert.That(x.Step(W(("Foggy", 1f)), 0.01f)["Foggy"], Is.EqualTo(1f));
            Assert.That(x.Snap(W(("Sunny", 1f))).ContainsKey("Foggy"), Is.False);
        }

        // the owner's line: a Nordmar fade (Sunny → Snowy) at 80 % with Myrtana's share around the third place
        static List<KeyValuePair<string, float>> Mix(float nordmar)
        {
            var d = W(("Sunny(Nordmar)", 0.25f * nordmar), ("Snowy(Nordmar)", 0.75f * nordmar), ("Rainy", 1 - nordmar));
            var l = new List<KeyValuePair<string, float>>();
            WeatherMix.ToList(d, l);
            return l;
        }

        static float Look(string s) => s.StartsWith("Sunny") ? 0f : s.StartsWith("Snowy") ? 1f : 5f;

        [Test]
        public void Mix_IsContinuous_WhenTheSecondAndThirdStateSwap()
        {
            float prevMix = WeatherMix.Mean(Mix(0.7f), Look, 0);
            WeatherRules.TopTwo(Mix(0.7f).ToDictionary(k => k.Key, k => k.Value), out var a0, out var b0, out var bl0);
            float prevTop = Look(a0) + (Look(b0) - Look(a0)) * bl0;
            float maxMix = 0, maxTop = 0;
            for (int i = 1; i <= 200; i++)
            {
                float n = 0.7f + i * 0.001f;
                float m = WeatherMix.Mean(Mix(n), Look, 0);
                WeatherRules.TopTwo(Mix(n).ToDictionary(k => k.Key, k => k.Value), out var a, out var b, out var bl);
                float top = Look(a) + (Look(b) - Look(a)) * bl;
                maxMix = Math.Max(maxMix, Math.Abs(m - prevMix));
                maxTop = Math.Max(maxTop, Math.Abs(top - prevTop));
                prevMix = m; prevTop = top;
            }
            Assert.That(maxMix, Is.LessThan(0.01f), "the whole mix has no step");
            Assert.That(maxTop, Is.GreaterThan(0.5f), "the old top-two did jump (the bug)");
        }

        [Test]
        public void Mix_Snowfall_Wetness_FollowTheWholeMix()
        {
            var l = Mix(1f);   // Snowy 0.75
            Assert.That(SnowV2.Snowfall(l), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(WeatherMix.Dominant(l, SnowV2.SnowfallOf), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(SnowV2.Snowfall(new List<KeyValuePair<string, float>>()), Is.Null);
            Assert.That(WorldV2.WetTarget(Mix(0.6f)), Is.EqualTo(0f).Within(1e-5f), "fix-239: Rainy 40 % does not fall, does not wet");
            Assert.That(WorldV2.WetTarget(Mix(0.2f)), Is.EqualTo(1f).Within(1e-5f), "Rainy 80 % falls at 0.6: wet");
            Assert.That(WorldV2.WetTarget((IReadOnlyList<KeyValuePair<string, float>>)null), Is.Null);
            Assert.That(l[0].Key, Is.EqualTo("Snowy(Nordmar)"), "heaviest first");
        }

        [Test]
        public void Snowfall_FadesIn_NotPops_AcrossTheRegionEdge()
        {
            // Nordmar Snowy at its peak, Myrtana Sunny: walking in over the ramp, then the cross-fade on top
            var x = new WeightCrossfade(6f);
            float prev = -1;
            for (int i = 0; i <= 200; i++)
            {
                WeatherZoneBlend.Layer(HammerClan(0f, i / 200f), Env, "Myrtana");
                var target = W(("Snowy(Nordmar)", Env.TryGetValue("Nordmar", out var n) ? n : 0f), ("Sunny", Env.TryGetValue("Myrtana", out var m) ? m : 0f));
                var w = i == 0 ? x.Snap(target) : x.Step(target, 0.1f);   // ~0.5 m per 0.1 s
                var l = new List<KeyValuePair<string, float>>();
                WeatherMix.ToList(w, l);
                float snow = SnowV2.Snowfall(l) ?? 0f;
                if (prev >= 0) Assert.That(Math.Abs(snow - prev), Is.LessThan(0.02f), $"step {i}");
                prev = snow;
            }
            Assert.That(prev, Is.EqualTo(1f).Within(1e-4f));
        }
    }
}
