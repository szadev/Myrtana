using System;

namespace G3.Unity
{
    /// <summary>
    /// arch-2 (Docs/remaster-world.md 25.7): sand deposited by Varant sandstorms on cloth (tents, awnings, sheets) and, more
    /// subtly, on the stone ledges. One global deposit 0..1, ticked in game time from the weather's sandstorm share:
    /// it rises during a storm (1 after <c>archSandRise</c> game hours of full storm) and decays afterwards towards the
    /// resting value (<c>archSandRest</c>: tents always a bit dusty) with an e-folding of a third of <c>archSandDecay</c>
    /// (5 % of the excess left after archSandDecay hours). Deterministic: the same weather and clock give the same value.
    /// The weather schedule is saved, its history is not: after a load (or any clock jump over an hour) the deposit is
    /// derived from the weather at that moment (<see cref="Initial"/>). The shader turns it into a per-pixel coverage
    /// (G3/Remaster/WorldV2: a threshold per pixel - steep parts and the apex shed first, folds and low edges keep it).
    /// Engine-free: tested in G3Tests (ArchSandTests).
    /// </summary>
    public static class ArchSand
    {
        static WorldLookSettings S => WorldRemasterLook.Current;

        /// <summary>Storm share below this counts as calm (the decay runs).</summary>
        public const float StormThreshold = 0.05f;

        /// <summary>Largest clock step (game hours) taken as continuous time; more is a jump (load, sleep, teleport).</summary>
        public const float MaxStepHours = 1f;

        /// <summary>The deposit after <paramref name="dtHours"/> game hours with sandstorm share <paramref name="storm"/> (0..1).</summary>
        public static float Step(float deposit, float storm, float dtHours) =>
            Step(deposit, storm, dtHours, S.archSandRise, S.archSandDecay, S.archSandRest);

        /// <summary>Pure form (tests): rise linear in the storm share, exponential decay towards rest, a deposit below rest fills up at the rise rate.</summary>
        public static float Step(float deposit, float storm, float dtHours, float riseHours, float decayHours, float rest)
        {
            deposit = Clamp01(deposit);
            if (!(dtHours > 0)) return deposit;
            storm = Clamp01(storm);
            rest = Clamp01(rest);
            float rise = 1f / Math.Max(riseHours, 1e-3f);
            if (storm > StormThreshold)
                return Clamp01(deposit + storm * rise * dtHours);
            if (deposit < rest)
                return Math.Min(rest, deposit + rise * dtHours);
            float tau = Math.Max(decayHours, 1e-3f) / 3f;
            return rest + (deposit - rest) * (float)Math.Exp(-dtHours / tau);
        }

        /// <summary>The deposit without history (a loaded save, a clock jump): full in a storm, else at rest.</summary>
        public static float Initial(float storm) => Initial(storm, S.archSandRest);

        public static float Initial(float storm, float rest) => Clamp01(storm) > 0.5f ? 1f : Clamp01(rest);

        /// <summary>
        /// Game hours from <paramref name="prevHour"/> to <paramref name="nowHour"/> (time of day 0..24, wraps at midnight);
        /// NaN when it is not a continuous step (backwards, or more than <see cref="MaxStepHours"/>).
        /// </summary>
        public static float HoursBetween(float prevHour, float nowHour)
        {
            if (float.IsNaN(prevHour) || float.IsNaN(nowHour)) return float.NaN;
            float d = nowHour - prevHour;
            if (d < -12f) d += 24f;   // past midnight
            return d >= 0 && d <= MaxStepHours ? d : float.NaN;
        }

        /// <summary>The deposit the shader gets: the pinned json value when set (archSandDeposit >= 0), else the live one.</summary>
        public static float Shown(float live) => S.archSandDeposit >= 0 ? Clamp01(S.archSandDeposit) : Clamp01(live);

        /// <summary>_G3V2ArchSand: x deposit, y cloth amount, z stone share following the deposit, w 1 = on (all 0: sand-3's sand as before).</summary>
        public static float[] Global(float live) => S.v2 && S.arch
            ? new[] { Shown(live), S.archClothSand, S.archStoneSand, 1f }
            : new float[4];

        /// <summary>Strength (0..1) of the sand wisps blown off tent edges: deposit above 0.4 and wind (or a storm).</summary>
        public static float WispAmount(float deposit, float wind, float storm)
        {
            if (S.archSandWisps <= 0) return 0;
            float d = Clamp01((deposit - 0.4f) / 0.3f);
            float w = Math.Max(Clamp01((wind - 0.35f) / 0.4f), Clamp01(storm));
            return d * w;
        }

        static float Clamp01(float v) => float.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
