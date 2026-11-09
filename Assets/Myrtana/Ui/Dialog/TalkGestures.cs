using System;
using System.Collections.Generic;

namespace Myrtana.Ui.Dialog
{
    /// <summary>
    /// The gesture a speaker plays with a line (G3 _AI_Say_Ex, Docs/dialog-presentation.md §3): the motion
    /// <c>&lt;Rig&gt;_&lt;Stance&gt;_&lt;Left&gt;_&lt;Right&gt;_P0_Say_&lt;Phase&gt;_N_Fwd_&lt;NN&gt;</c>. The phase comes from the line's stage
    /// direction (CP 1.75: the Say line's ID2, imported as <c>Say.Stage</c>; 1297 of 13014 lines have one); a line
    /// without one gets a random phase.
    /// A speaker with something in the hands or not standing only plays <c>Ambient</c>.
    /// </summary>
    public static class TalkGestures
    {
        public const string Action = "Say";
        public const string Ambient = "Ambient";

        /// <summary>The eleven Say phases in G3 order (gEPhase 0x10…0x1A).</summary>
        public static readonly IReadOnlyList<string> Phases = new[]
        {
            Ambient, "Proud", "Approve", "Thoughtful", "Angry", "Threaten", "Nervous", "Point", "Preach", "Beg", "Sad",
        };

        static readonly Dictionary<string, string> ByStageDirection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SD_Ambient"] = Ambient, ["SD_Proud"] = "Proud", ["SD_Approve"] = "Approve", ["SD_Thankful"] = "Approve",
            ["SD_Thoughtful"] = "Thoughtful", ["SD_Angry"] = "Angry", ["SD_Threaten"] = "Threaten", ["SD_Nervous"] = "Nervous",
            ["SD_Point"] = "Point", ["SD_Mocking"] = "Point", ["SD_Preach"] = "Preach", ["SD_Beg"] = "Beg", ["SD_Sad"] = "Sad",
        };

        /// <summary>
        /// Phase for a line: Ambient when busy, the stage direction's phase, else a random one other than
        /// <paramref name="previous"/> (the speaker's last phase, so consecutive lines change the gesture).
        /// </summary>
        public static string PickPhase(Random random, bool handsFreeAndStanding, string stageDirection = null, string previous = null)
        {
            if (!handsFreeAndStanding) return Ambient;
            if (!string.IsNullOrEmpty(stageDirection) && ByStageDirection.TryGetValue(stageDirection, out var phase)) return phase;
            int skip = previous == null ? -1 : IndexOf(previous);
            if (skip < 0) return Phases[random.Next(Phases.Count)];
            int i = random.Next(Phases.Count - 1);
            return Phases[i >= skip ? i + 1 : i];
        }

        static int IndexOf(string phase)
        {
            for (int i = 0; i < Phases.Count; i++)
                if (string.Equals(Phases[i], phase, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>
        /// One of the variants (index 00, 01 …) that exist; <paramref name="exists"/> tells whether index i has a motion.
        /// -1 when there is none.
        /// </summary>
        public static int PickVariant(Random random, Func<int, bool> exists, int maxVariants = 8)
        {
            var found = new List<int>();
            for (int i = 0; i < maxVariants; i++)
                if (exists(i)) found.Add(i);
            return found.Count == 0 ? -1 : found[random.Next(found.Count)];
        }
    }
}
