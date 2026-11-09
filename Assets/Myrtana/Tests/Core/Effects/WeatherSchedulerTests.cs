using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G3.Formats.Environment;
using G3.Formats.Pak;
using G3.Weather;
using NUnit.Framework;

namespace Myrtana.Tests.Effects
{
    /// <summary>G3 weather selection (eCWeatherEnvironment::Process) and the precipitation rule (Docs/formats-weather.md §7).</summary>
    public class WeatherSchedulerTests
    {
        static readonly WeatherChance[] Myrtana =
        {
            new WeatherChance("Foggy", 0.05f, 2 / 24f, 4 / 24f),
            new WeatherChance("Rainy", 0.2f, 1 / 24f, 4 / 24f),
            new WeatherChance("Sunny", 0.75f, 4 / 24f, 20 / 24f),
            new WeatherChance("Snowy(Nordmar)", 0f, 0, 0),
        };

        static Func<float> Seq(params float[] v)
        {
            int i = 0;
            return () => v[Math.Min(i++, v.Length - 1)];
        }

        [Test]
        public void StartsInTheMostProbableStateFadedOverItsMaximum()
        {
            // r for Foggy 0.9 (fails), Rainy 0.9 (fails): nothing qualifies → Sunny → Sunny
            var s = new WeatherScheduler("Myrtana", Myrtana, Seq(0.9f, 0.9f));
            s.Update(10.0);
            Assert.AreEqual("Sunny", s.CurrentState);
            Assert.AreEqual("Sunny", s.NextState);
            Assert.AreEqual(20 / 24.0, s.Duration, 1e-6);
            Assert.AreEqual(0f, s.Progress);
        }

        [Test]
        public void BestScoreWinsAndSetsTheFadeDuration()
        {
            // Foggy r 0.04 → score 0.8; Rainy r 0.1 → score 0.5; duration rand 0.5 for Foggy → 3 h
            var s = new WeatherScheduler("Myrtana", Myrtana, Seq(0.04f, 0.5f, 0.1f, 0.0f));
            s.Update(1.0);
            Assert.AreEqual("Sunny", s.CurrentState);
            Assert.AreEqual("Foggy", s.NextState);
            Assert.AreEqual(3 / 24.0, s.Duration, 1e-6);
            s.Update(1.0 + 1.5 / 24);
            Assert.AreEqual(0.5f, s.Progress, 1e-4);
            var w = new Dictionary<string, float>();
            s.AddWeights(1f, w);
            Assert.AreEqual(0.5f, w["Sunny"], 1e-4);
            Assert.AreEqual(0.5f, w["Foggy"], 1e-4);
        }

        [Test]
        public void AFinishedFadeMovesOn()
        {
            var s = new WeatherScheduler("Myrtana", Myrtana, Seq(0.9f, 0.1f, 0.0f, 0.9f, 0.9f));
            s.Update(0);                       // Sunny → Rainy over 1 h (rand 0)
            Assert.AreEqual("Rainy", s.NextState);
            s.Update(1 / 24.0);                // complete
            Assert.AreEqual(1f, s.Progress);
            s.Update(1 / 24.0 + 0.001);        // reselect: Rainy current; Foggy 0.9 fails, Sunny 0.9 qualifies (0.75 < 0.9? no)
            Assert.AreEqual("Rainy", s.CurrentState);
            Assert.AreEqual("Rainy", s.NextState, "nothing qualified: stays");
        }

        [Test]
        public void ClockGoingBackReselects()
        {
            var s = new WeatherScheduler("Myrtana", Myrtana, Seq(0.9f));
            s.Update(5.0);
            s.Update(4.0);
            Assert.AreEqual(4.0, s.Start, 1e-9);
        }

        [Test]
        public void PrecipitationNeedsADominantState()
        {
            var w = new Dictionary<string, float> { ["Rainy"] = 0.75f, ["Sunny"] = 0.25f };
            Assert.IsTrue(WeatherRules.Dominant(w, out var st, out var k));
            Assert.AreEqual("Rainy", st);
            Assert.AreEqual(0.5f, k, 1e-6);
            Assert.IsFalse(WeatherRules.Dominant(new Dictionary<string, float> { ["Rainy"] = 0.5f, ["Sunny"] = 0.5f }, out _, out _));
        }

        [Test]
        public void TopTwoAndNormalize()
        {
            var n = WeatherRules.Normalize(new Dictionary<string, float> { ["A"] = 2, ["B"] = 1, ["C"] = 1 });
            Assert.AreEqual(0.5f, n["A"], 1e-6);
            WeatherRules.TopTwo(n, out var a, out var b, out var blend);
            Assert.AreEqual("A", a);
            Assert.AreEqual(1 / 3f, blend, 1e-5);
        }

        [Test]
        public void NormalizeInto_ReusesAndClearsTheBuffer()
        {
            var buffer = new Dictionary<string, float> { ["Old"] = 1 };
            var n = WeatherRules.Normalize(new Dictionary<string, float> { ["A"] = 3, ["B"] = 1 }, buffer);
            Assert.AreSame(buffer, n);
            Assert.AreEqual(2, n.Count);
            Assert.AreEqual(0.75f, n["A"], 1e-6);
            Assert.AreEqual(0, WeatherRules.Normalize(new Dictionary<string, float> { ["A"] = 0 }, buffer).Count);
        }

        [Test]
        public void DayTimeAdvancesOverMidnight()
        {
            Assert.AreEqual(1.0 + 1 / 24.0, WeatherRules.AdvanceDayTime(1.0, 23f, 0f), 1e-9);
            Assert.AreEqual(1.0 + 2 / 24.0, WeatherRules.AdvanceDayTime(1.0, 10f, 12f), 1e-9);
        }

        [Test]
        public void LongRunMatchesTheProbabilities()
        {
            var rng = new Random(7);
            var s = new WeatherScheduler("Myrtana", Myrtana, () => (float)rng.NextDouble());
            var time = new Dictionary<string, double>();
            double step = 0.25 / 24;
            for (double t = 0; t < 2000; t += step)
            {
                s.Update(t);
                var w = new Dictionary<string, float>();
                s.AddWeights(1, w);
                foreach (var kv in w) time[kv.Key] = (time.TryGetValue(kv.Key, out var o) ? o : 0) + kv.Value * step;
            }
            double total = time.Values.Sum();
            Assert.Greater(time["Sunny"] / total, 0.6);
            Assert.That(time["Rainy"] / total, Is.InRange(0.05, 0.3));
            Assert.Less(time.TryGetValue("Foggy", out var f) ? f / total : 0, 0.1);
        }

        // fix-236 (owner: "why so much bad weather?"): time shares of the CP 1.75 environments under G3's rule, from an
        // independent literal port of eCWeatherEnvironment::Process (CP 1.75 Engine.dll 0x1028f780, 20 000 days, 1-min steps):
        // share of time a state is above 50 % (= its curves dominate; for rain/snow: precipitation falls).
        static readonly WeatherChance[] MyrtanaCp =
        {
            new WeatherChance("Foggy", 0.05f, 2 / 24f, 4 / 24f), new WeatherChance("Rainy", 0.2f, 1 / 24f, 4 / 24f),
            new WeatherChance("Sunny", 0.75f, 4 / 24f, 20 / 24f),
        };
        static readonly WeatherChance[] VarantCp =
        {
            new WeatherChance("Sunny(Varant)", 0.8f, 7 / 24f, 20 / 24f), new WeatherChance("Sandstormy(Varant)", 0.2f, 1 / 24f, 3 / 24f),
        };
        static readonly WeatherChance[] NordmarCp =
        {
            new WeatherChance("Snowstorm(Nordmar)", 0.15f, 1 / 24f, 3 / 24f), new WeatherChance("Snowy(Nordmar)", 0.2f, 3 / 24f, 12 / 24f),
            new WeatherChance("Sunny(Nordmar)", 0.65f, 4 / 24f, 15 / 24f),
        };

        static Dictionary<string, double> DominantShares(WeatherChance[] env, int seed, double days, WeatherMode mode = WeatherMode.Original)
        {
            var rng = new Random(seed);
            var s = new WeatherScheduler("env", env, () => rng.Next(32768) / 32767f) { Mode = mode };
            var share = new Dictionary<string, double>();
            double step = 1.0 / 24 / 12, n = 0;
            var w = new Dictionary<string, float>();
            for (double t = 1; t < 1 + days; t += step, n++)
            {
                s.Update(t);
                w.Clear();
                s.AddWeights(1, w);
                foreach (var kv in w) if (kv.Value > 0.5f) share[kv.Key] = (share.TryGetValue(kv.Key, out var o) ? o : 0) + 1;
            }
            foreach (var k in share.Keys.ToList()) share[k] /= n;
            return share;
        }

        static double Get(Dictionary<string, double> d, string k) => d.TryGetValue(k, out var v) ? v : 0;

        [TestCase("Myrtana", "Sunny", 0.844)]
        [TestCase("Myrtana", "Rainy", 0.124)]
        [TestCase("Myrtana", "Foggy", 0.032)]
        [TestCase("Varant", "Sunny(Varant)", 0.883)]
        [TestCase("Varant", "Sandstormy(Varant)", 0.117)]
        [TestCase("Nordmar", "Sunny(Nordmar)", 0.717)]
        [TestCase("Nordmar", "Snowy(Nordmar)", 0.197)]
        [TestCase("Nordmar", "Snowstorm(Nordmar)", 0.086)]
        public void TimeSharesMatchG3(string region, string state, double g3Share)
        {
            var env = region == "Myrtana" ? MyrtanaCp : region == "Varant" ? VarantCp : NordmarCp;
            var shares = DominantShares(env, 236, 3000);
            Assert.That(Get(shares, state), Is.EqualTo(g3Share).Within(0.02), $"{region} {state}");
        }

        [Test]
        public void RainEpisodeLastsAboutSevenHoursLikeG3()
        {
            // G3's durations are fade times: rain is above 50 % from the middle of Sunny→Rainy (1–4 h) to the middle of
            // Rainy→Sunny (Sunny's 4–20 h) — about 1.25 + 6 h. The owner's session (save704: Sunny→Rainy 3.5 h, Rainy→Sunny
            // 12.6 h) rained from ~9:45 to ~17:45.
            var rng = new Random(5);
            var s = new WeatherScheduler("Myrtana", MyrtanaCp, () => rng.Next(32768) / 32767f);
            double step = 1.0 / 24 / 12, run = 0, total = 0;
            int episodes = 0;
            var w = new Dictionary<string, float>();
            for (double t = 1; t < 3001; t += step)
            {
                s.Update(t);
                w.Clear();
                s.AddWeights(1, w);
                bool rain = w.TryGetValue("Rainy", out var r) && r > 0.5f;
                if (rain) run += step;
                else if (run > 0) { total += run; episodes++; run = 0; }
            }
            Assert.That(total / episodes * 24, Is.InRange(6.0, 9.0));
        }

        // weather-opt: the Remaster "Weather: Fair" option. Halving the non-sunny chances alone gives Myrtana ~92 % / Varant ~94 %
        // / Nordmar ~84 % sunny; the 6 h cap of the fade back to sunny ends the grey tail earlier while the sunny period keeps
        // its drawn length, which adds ~3 % more (independent Python port of the same rule, 3000 days, 5-min steps:
        // 94.9 / 96.8 / 86.4 %).
        [TestCase("Myrtana", "Sunny", 0.949)]
        [TestCase("Varant", "Sunny(Varant)", 0.968)]
        [TestCase("Nordmar", "Sunny(Nordmar)", 0.864)]
        public void FairTimeShares(string region, string sunny, double expected)
        {
            var env = region == "Myrtana" ? MyrtanaCp : region == "Varant" ? VarantCp : NordmarCp;
            double fair = Get(DominantShares(env, 236, 3000, WeatherMode.Fair), sunny);
            double original = Get(DominantShares(env, 236, 3000), sunny);
            Assert.That(fair, Is.EqualTo(expected).Within(0.02), $"{region} fair");
            Assert.Greater(fair, original + 0.05, $"{region}: fair is clearly sunnier than G3");
        }

        [Test]
        public void FairRainEpisodesAreShorter()
        {
            double Episode(WeatherMode mode)
            {
                var rng = new Random(5);
                var s = new WeatherScheduler("Myrtana", MyrtanaCp, () => rng.Next(32768) / 32767f) { Mode = mode };
                double step = 1.0 / 24 / 12, run = 0, total = 0;
                int episodes = 0;
                var w = new Dictionary<string, float>();
                for (double t = 1; t < 3001; t += step)
                {
                    s.Update(t);
                    w.Clear();
                    s.AddWeights(1, w);
                    if (w.TryGetValue("Rainy", out var r) && r > 0.5f) run += step;
                    else if (run > 0) { total += run; episodes++; run = 0; }
                }
                return total / episodes * 24;
            }
            double g3 = Episode(WeatherMode.Original), fair = Episode(WeatherMode.Fair);
            Assert.Less(fair, g3 - 1.5, $"G3 {g3:F1} h, fair {fair:F1} h");
            // ~1.25 h into rain + at most 3 h of the capped fade, plus the Rainy periods kept when no state qualified: ~5 h
            Assert.That(fair, Is.InRange(4.0, 5.5), "fair rain episode, h");
        }

        // Myrtana (CP 1.75): Foggy 0, Rainy 1, Sunny 2.
        static WeatherScheduler RainyEndingAt(double end, WeatherMode mode, params float[] rand)
        {
            var s = new WeatherScheduler("Myrtana", MyrtanaCp, Seq(rand)) { Mode = mode };
            s.Restore(1, 1, end - 1 / 24.0, 1 / 24.0);   // Rainy, period over at "end"
            s.Update(end);                               // period complete
            return s;
        }

        [Test]
        public void FairCapsTheFadeBackToSunnyAndHoldsTheSunnyPeriod()
        {
            // draw at day 2: Foggy r 0.9 fails; Sunny r 0.5 wins (score 0.67), duration rand 1 → 20 h
            var s = RainyEndingAt(2.0, WeatherMode.Fair, 0.9f, 0.5f, 1f, 0.99f, 0.99f);
            s.Update(2.0 + 1e-6);
            Assert.AreEqual("Rainy", s.CurrentState);
            Assert.AreEqual("Sunny", s.NextState);
            Assert.AreEqual(20 / 24.0, s.Duration, 1e-6, "period: the drawn 20 h");
            Assert.AreEqual(6 / 24.0, s.Fade, 1e-9, "fade capped at 6 h");
            s.Update(2.0 + 3 / 24.0);
            Assert.AreEqual(0.5f, s.Progress, 1e-4, "half way after 3 h");
            s.Update(2.0 + 10 / 24.0);
            Assert.AreEqual(1f, s.Progress);
            Assert.AreEqual("Sunny", s.NextState, "sunny holds after the fade");
            s.Update(2.0 + 19.9 / 24);
            Assert.AreEqual("Rainy", s.CurrentState, "no new draw before the drawn period is over");
            s.Update(2.0 + 20.01 / 24);                  // period over
            s.Update(2.0 + 20.02 / 24);                  // draw
            Assert.AreEqual("Sunny", s.CurrentState, "next draw at 20 h");
        }

        [Test]
        public void OriginalFadesOverTheWholeDuration()
        {
            var s = RainyEndingAt(2.0, WeatherMode.Original, 0.9f, 0.5f, 1f);
            s.Update(2.0 + 1e-6);
            Assert.AreEqual("Sunny", s.NextState);
            Assert.AreEqual(20 / 24.0, s.Fade, 1e-6);
            Assert.AreEqual(s.Duration, s.Fade);
            s.Update(2.0 + 10 / 24.0);
            Assert.AreEqual(0.5f, s.Progress, 1e-4);
        }

        [Test]
        public void FairLeavesShortFadesAndFadesAwayFromSunnyAlone()
        {
            // Rainy → Sunny drawn at 4 h: below the cap, unchanged
            var s = RainyEndingAt(2.0, WeatherMode.Fair, 0.9f, 0.5f, 0f);
            s.Update(2.0 + 1e-6);
            Assert.AreEqual(4 / 24.0, s.Fade, 1e-6);
            // Sunny → Rainy: never capped (Rainy r 0.05 ≤ 0.1, duration rand 1 → 4 h; Foggy fails)
            var t = new WeatherScheduler("Myrtana", MyrtanaCp, Seq(0.9f, 0.05f, 1f)) { Mode = WeatherMode.Fair };
            t.Restore(2, 2, 1.0, 1 / 24.0);
            t.Update(1.0 + 1 / 24.0);
            t.Update(1.0 + 1 / 24.0 + 1e-6);
            Assert.AreEqual("Rainy", t.NextState);
            Assert.AreEqual(t.Duration, t.Fade);
        }

        [Test]
        public void FairHalvesTheBadChances()
        {
            Assert.AreEqual(0.1f, WeatherFair.Probability(WeatherMode.Fair, "Rainy", 0.2f), 1e-6);
            Assert.AreEqual(0.075f, WeatherFair.Probability(WeatherMode.Fair, "Snowstorm(Nordmar)", 0.15f), 1e-6);
            Assert.AreEqual(0.8f, WeatherFair.Probability(WeatherMode.Fair, "Sunny(Varant)", 0.8f), 1e-6);
            Assert.AreEqual(0.2f, WeatherFair.Probability(WeatherMode.Original, "Rainy", 0.2f), 1e-6);
            // from Sunny: Rainy r 0.15 qualifies in G3 (≤ 0.2) but not in Fair (> 0.1)
            foreach (var mode in new[] { WeatherMode.Original, WeatherMode.Fair })
            {
                var s = new WeatherScheduler("Myrtana", MyrtanaCp, Seq(0.9f, 0.15f, 0.5f)) { Mode = mode };
                s.Restore(2, 2, 1.0, 1 / 24.0);
                s.Update(1.0 + 1 / 24.0);
                s.Update(1.0 + 1 / 24.0 + 1e-6);
                Assert.AreEqual(mode == WeatherMode.Fair ? "Sunny" : "Rainy", s.NextState, mode.ToString());
            }
        }

        [Test]
        public void SwitchingModeDoesNotChangeARunningFade()
        {
            var s = RainyEndingAt(2.0, WeatherMode.Original, 0.9f, 0.5f, 1f);
            s.Update(2.0 + 1e-6);
            s.Mode = WeatherMode.Fair;
            s.Update(2.0 + 10 / 24.0);
            Assert.AreEqual(20 / 24.0, s.Fade, 1e-6);
            Assert.AreEqual(0.5f, s.Progress, 1e-4);
        }

        [Test]
        public void CappedFadeSurvivesSaveAndOldSavesFadeOverTheDuration()
        {
            var s = RainyEndingAt(2.0, WeatherMode.Fair, 0.9f, 0.5f, 1f);
            s.Update(2.0 + 1e-6);
            string saved = WeatherScheduleSnapshot.Encode(new[] { s.Capture() });
            Assert.AreEqual(6, saved.Split('|').Length);
            var back = WeatherScheduleSnapshot.Decode(saved).Single();
            var r = new WeatherScheduler("Myrtana", MyrtanaCp, Seq(0.9f));
            Assert.IsTrue(r.Restore(back));
            Assert.AreEqual(6 / 24.0, r.Fade, 1e-9);
            Assert.AreEqual(20 / 24.0, r.Duration, 1e-6);
            r.Update(2.0 + 3 / 24.0);
            Assert.AreEqual(0.5f, r.Progress, 1e-4);

            var old = WeatherScheduleSnapshot.Decode("Myrtana|Rainy|Sunny|2|0.5").Single();   // before weather-opt
            Assert.AreEqual(0.5, old.Fade);
            var o = new WeatherScheduler("Myrtana", MyrtanaCp, Seq(0.9f));
            Assert.IsTrue(o.Restore(old));
            Assert.AreEqual(0.5, o.Fade);
            Assert.AreEqual(5, WeatherScheduleSnapshot.Encode(new[] { o.Capture() }).Split('|').Length, "uncapped: old format");
        }

        [Test]
        public void WeatherRowTextsAndPref()
        {
            Assert.AreEqual("Погода", G3.Unity.RemasterOptions.WeatherName("Russian"));
            Assert.AreEqual("Weather", G3.Unity.RemasterOptions.WeatherName("English"));
            Assert.AreEqual("Оригинал", G3.Unity.RemasterOptions.WeatherValue(0, "Russian"));
            Assert.AreEqual("Ясная", G3.Unity.RemasterOptions.WeatherValue(1, "Russian"));
            Assert.AreEqual("Original", G3.Unity.RemasterOptions.WeatherValue(0, "English"));
            Assert.AreEqual("Fair", G3.Unity.RemasterOptions.WeatherValue(1, "English"));
            Assert.AreEqual(0, G3.Unity.RemasterOptions.WeatherMode(null, 0), "default: G3");
            Assert.AreEqual(1, G3.Unity.RemasterOptions.WeatherMode(null, 1));
            Assert.AreEqual(0, G3.Unity.RemasterOptions.WeatherMode(null, 7));
            Assert.AreEqual(1, G3.Unity.RemasterOptions.WeatherMode("on", 0));
            Assert.AreEqual(0, G3.Unity.RemasterOptions.WeatherMode("0", 1));
            Assert.AreEqual("G3.Remaster.Weather", G3.Unity.RemasterOptions.WeatherPrefKey);
        }

        [Test]
        public void CommunityPatchWeatherFileHasLoopsAndCave()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "Library.pak"))) Assert.Ignore("Gothic 3 data not found");
            using var fs = new G3FileSystem(data, "Library");
            var e = fs.GetByName("WeatherStates.g3ws");
            if (e == null) Assert.Ignore("vanilla data: no WeatherStates.g3ws in Library.pak");
            var w = WeatherStatesFile.Read(e.Read());
            var rainy = w.States.First(s => s.Name == "Rainy");
            Assert.AreEqual("rain_medium_01.ogg", rainy.LoopSample);
            Assert.Greater(rainy.ParticlesPerSecond, 0);
            Assert.IsTrue(w.Environments.Any(x => x.Name == "Cave"));
        }
    }
}
