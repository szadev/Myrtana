using System;
using UnityEngine;

namespace G3.Unity
{
    /// <summary>
    /// mt-1: the runtime <c>-g3mt</c> mask (<see cref="MtBits"/>; command line, else <c>G3_MT</c>, else the default). Read
    /// by the job paths every frame, so an eval / the console can flip it inside one Play session (A/B windows). In
    /// <see cref="MtBits.Verify"/> mode the job paths also run the managed path and count the differences
    /// (<see cref="Mismatches"/>; the first few are logged): every job result is meant to be bitwise the managed one.
    /// </summary>
    public static class G3Mt
    {
        static int s_mask = -1;

        public static int Mask
        {
            get
            {
                if (s_mask < 0) s_mask = MtBits.Resolve(G3RenderTuning.CommandLine.MtMask, Environment.GetEnvironmentVariable(MtBits.EnvVar));
                return s_mask;
            }
            set => s_mask = Math.Max(0, value);
        }

        public static bool On(int bit) => (Mask & bit) != 0;

        public static bool Verifying => (Mask & MtBits.Verify) != 0;

        /// <summary>Values compared in verify mode / found different since the start (or <see cref="ResetCounters"/>).</summary>
        public static long Checks { get; private set; }
        public static long Mismatches { get; private set; }

        public static void ResetCounters()
        {
            Checks = 0;
            Mismatches = 0;
            s_byCategory.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay()
        {
            s_mask = -1;
            ResetCounters();
        }

        static readonly System.Collections.Generic.Dictionary<string, long> s_byCategory = new System.Collections.Generic.Dictionary<string, long>();

        /// <summary>Verify mode: counts one comparison of <paramref name="category"/>; logs the first 10 differences of each.</summary>
        public static void Check(bool same, string category, string detail)
        {
            Checks++;
            if (same) return;
            Mismatches++;
            s_byCategory.TryGetValue(category, out long n);
            s_byCategory[category] = ++n;
            if (n <= 10) Debug.LogWarning("[g3mt] job result differs from the managed path (" + category + "): " + detail);
        }

        public static bool Same(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
        public static bool Same(Quaternion a, Quaternion b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z) && a.w.Equals(b.w);

        public static string Describe()
        {
            var sb = new System.Text.StringBuilder($"mt={Mask} ({MtBits.Describe(Mask)}) checks={Checks} mismatches={Mismatches}");
            foreach (var kv in s_byCategory) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            return sb.ToString();
        }
    }
}
