using System;

namespace Myrtana.Simulation
{
    /// <summary>
    /// G3 ZS_Smalltalk (CP 1.75 Script_Game): two NPCs at neighbouring smalltalk spots talk to each other. The first
    /// one there (TaskPosition 1) is the leader, the other the partner. ZS_Smalltalk_Leader_Loop: wait 3.0–3.7 s
    /// (3000 + R(9)·88 ms), say an SVM smalltalk line (one of 5 categories, index StatePosition 0..8), wait again, …; after
    /// 120 s of state time both ContinueRoutine (the conversation ends; the routine may send them on or start it anew);
    /// the partner leaves when the leader no longer targets it. Here the two take turns of <see cref="TurnSeconds"/>: the
    /// speaker plays "Say" and its voice set's "Smalltalk_0n" line (<see cref="LineOf"/>), the other listens; both face each
    /// other. An NPC alone at a smalltalk spot stands around [interp: G3 waits there].
    /// </summary>
    public static class Smalltalk
    {
        /// <summary>Interaction names: the spot's own, the listener's, and standing alone.</summary>
        public const string Interaction = "Smalltalk", Listener = "Listener", Alone = "Stand";
        /// <summary>Smalltalk spots of a pair lie this close together (m) [interp: G3 pairs by the routine target].</summary>
        public const float PairDistance = 3.5f;
        /// <summary>
        /// Real seconds of one turn: a recorded smalltalk line (about 4 s) and G3's 3.0–3.7 s wait after it [interp: G3 waits
        /// for the line to end; the line lengths are not known to the simulation].
        /// </summary>
        public const float TurnSeconds = 7f;
        /// <summary>ZS_Smalltalk_Leader_Loop ends the conversation after this much state time (real s).</summary>
        public const float EndSeconds = 120f;
        /// <summary>Smalltalk lines per voice set (Smalltalk_00..09).</summary>
        public const int LineCount = 10;

        public static bool IsSmalltalk(string interaction) =>
            string.Equals(interaction, Interaction, StringComparison.OrdinalIgnoreCase);

        /// <summary>The NPC that arrived first leads (ties: the smaller id, so both sides agree).</summary>
        public static bool IsLeader(double arrivedAt, string id, double partnerArrivedAt, string partnerId) =>
            arrivedAt < partnerArrivedAt || arrivedAt == partnerArrivedAt && string.CompareOrdinal(id, partnerId) < 0;

        /// <summary>The turn number after <paramref name="realSecondsTogether"/>.</summary>
        public static long TurnOf(double realSecondsTogether) => (long)Math.Floor(Math.Max(0, realSecondsTogether) / TurnSeconds);

        /// <summary>
        /// "Smalltalk" (speaks) or "Listener" after <paramref name="realSecondsTogether"/>: the leader speaks the even turns,
        /// the partner the odd ones.
        /// </summary>
        public static string RoleOf(bool leader, double realSecondsTogether)
        {
            bool leaderSpeaks = TurnOf(realSecondsTogether) % 2 == 0;
            return leader == leaderSpeaks ? Interaction : Listener;
        }

        /// <summary>The SVM line of turn <paramref name="turn"/> ("Smalltalk_00" …) [interp: G3's 5 random SVM categories are not decoded; the recorded lines are Smalltalk_00..09].</summary>
        public static string LineOf(long turn) => "Smalltalk_" + (turn % LineCount).ToString("00");

        /// <summary>The conversation is over (both go on with their routine).</summary>
        public static bool Ended(double realSecondsTogether) => realSecondsTogether >= EndSeconds;
    }
}
