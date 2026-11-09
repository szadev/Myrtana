namespace Myrtana.Simulation
{
    /// <summary>
    /// A woken sleeper (ZS_ReactToFootsteps → ZS_ObserveSuspect, Docs/perception-crime.md §2.4): it says
    /// "YouDisturbedMySlumber", stands up and watches the player until the observe loop lets it go back to its routine
    /// (and so to bed).
    /// </summary>
    public static class SleeperRules
    {
        public const string DisturbedLine = "YouDisturbedMySlumber";

        /// <summary>
        /// fix-247, G3 ZS_Talk @10024ff0: the partner of a talk whose last task was ZS_Sleep says <see cref="DisturbedLine"/>,
        /// stands up and only then talks. Ours: it sleeps at its bed or sleeping place now, not already woken and watching
        /// (a reaction task: ZS_ObserveSuspect is its last task then).
        /// </summary>
        public static bool SaysDisturbedOnTalk(bool sleeps, bool inReactionTask) => sleeps && !inReactionTask;
        /// <summary>ZS_ObserveSuspect_Loop re-checks every 4 s of task time.</summary>
        public const double ObserveCheckSeconds = 4;
        /// <summary>After 1 s a target beyond 700 ends the observing (ContinueRoutine).</summary>
        public const double ObserveGraceSeconds = 1;
        public const float ObserveLoseRange = 700f;
        /// <summary>The player this close to an NPC whose destination is a bed keeps it observing [500].</summary>
        public const float BedsideRange = 500f;

        /// <summary>
        /// One pass of ZS_ObserveSuspect_Loop for a woken sleeper: false = ContinueRoutine. Beyond 700 after the first second
        /// it ends; at each 4 s check it goes on while the player (not a friend) is in its indoor zone, or within 500 of the
        /// sleeper whose destination is a bed or sleeping place, or blocks (parade stance).
        /// </summary>
        public static bool KeepsObserving(double taskSeconds, bool checkDue, float distance, bool sameIndoorZone, bool friend,
                                          bool sleepsHere, bool playerParade)
        {
            if (taskSeconds > ObserveGraceSeconds && distance > ObserveLoseRange) return false;
            if (!checkDue) return true;
            return (sameIndoorZone && !friend) || (sleepsHere && distance <= BedsideRange) || playerParade;
        }
    }
}
