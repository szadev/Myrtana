using System;
using System.Globalization;

namespace G3.Unity
{
    /// <summary>
    /// mt-1 (Docs/multithreading-plan.md, section 7): the bits of the <c>-g3mt</c> switch. Each bit moves one piece of
    /// main-thread work into a Burst job; with the bit off the old managed path runs. The results are the same either way
    /// (the jobs compute what the main thread computed, from the same inputs, at the same point of the frame), so one Play
    /// session can alternate the mask for A/B windows. Engine-free (unit-tested in Tools/G3Tests).
    /// </summary>
    public static class MtBits
    {
        /// <summary>C: NPC view / combat body transforms read in one job per reader point (perception, combat body speed,
        /// focus gather, walker far cull).</summary>
        public const int BodySnapshot = 1;
        /// <summary>D: update-LOD camera distance and tier candidates in the view snapshot job.</summary>
        public const int TierJob = 2;
        /// <summary>E.1: walker evasion candidates from a per-frame grid over the combat body snapshot (needs bit 1).</summary>
        public const int EvadeGrid = 4;
        /// <summary>E.2: batched walker probes (reserved).</summary>
        public const int WalkerProbes = 8;
        /// <summary>F: batched combat line-of-sight (reserved).</summary>
        public const int CombatBatch = 16;
        /// <summary>H: animation job (reserved).</summary>
        public const int AnimJob = 32;
        /// <summary>G: effects job (reserved).</summary>
        public const int EffectsJob = 64;
        /// <summary>Check mode: the managed path runs too and every difference is counted and logged (slow; for tests).</summary>
        public const int Verify = 128;

        /// <summary>The default: C and E.1 (D, bit 2, measured no gain in the editor: off unless asked for); <c>-g3mt 0</c> = the old paths.</summary>
        public const int Default = BodySnapshot | EvadeGrid;

        /// <summary>Environment variable read when <c>-g3mt</c> is not given.</summary>
        public const string EnvVar = "G3_MT";

        /// <summary>Decimal ("3") or hex ("0x83"); negative or unparsable = false.</summary>
        public static bool TryParse(string text, out int mask)
        {
            mask = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
            return ok && mask >= 0;
        }

        /// <summary>The start mask: the command line's when given (non-negative), else the environment's, else <see cref="Default"/>.</summary>
        public static int Resolve(int commandLine, string environment) =>
            commandLine >= 0 ? commandLine : TryParse(environment, out int env) ? env : Default;

        /// <summary>"snap+tier" style list for result lines ("off" for 0).</summary>
        public static string Describe(int mask)
        {
            if (mask == 0) return "off";
            string s = "";
            void Add(int bit, string name) { if ((mask & bit) != 0) s += (s.Length > 0 ? "+" : "") + name; }
            Add(BodySnapshot, "snap"); Add(TierJob, "tier"); Add(EvadeGrid, "evade"); Add(WalkerProbes, "probes");
            Add(CombatBatch, "combat"); Add(AnimJob, "anim"); Add(EffectsJob, "fx"); Add(Verify, "verify");
            return s.Length > 0 ? s : mask.ToString(CultureInfo.InvariantCulture);
        }
    }
}
