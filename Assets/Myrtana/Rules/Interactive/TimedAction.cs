namespace Myrtana.Rules
{
    /// <summary>
    /// G3 <c>_AI_Action</c> / <c>_AI_LoopAction</c> (Script_Game AI_Action.cpp, pure): the tool works of the hero's object
    /// uses (PS_Interact_PickOre: action PickOre, PS_Interact_PirateTreasure: DigGround) call <c>_AI_Action(self, action, 10)</c>.
    /// The 10 is a command <b>time in seconds</b>, not a loop count: after the Begin transition (<c>_AI_ChangeAction</c>)
    /// <c>_AI_LoopAction</c> keeps <c>start = (int) GetStateTime()</c> and, while <c>GetStateTime() &lt;= start + 10</c>, plays one
    /// more cycle: the Ambient variant when <c>GetRandomNumber(100) &lt; 10</c> (phase 0x10), else the Loop (phase 5).
    /// A cycle already begun plays to its end. The hero's PickOre (Begin 0.8 s, Loop 2.0 s, no Ambient) so strikes 5 times.
    /// </summary>
    public readonly struct TimedAction
    {
        /// <summary>The Ambient variant's chance per cycle, in percent (<c>GetRandomNumber(100) &lt; 10</c>).</summary>
        public const int AmbientPercent = 10;

        /// <param name="stateSecondsAtLoop">the use state's time when the loop starts (after the Begin motion)</param>
        /// <param name="seconds">the command time of <c>_AI_Action</c></param>
        public TimedAction(float stateSecondsAtLoop, int seconds)
        {
            Start = (int)stateSecondsAtLoop; // _ftol of the float state time
            Seconds = seconds;
        }

        public int Start { get; }
        public int Seconds { get; }

        /// <summary>The last state time at which one more cycle starts.</summary>
        public int Deadline => Start + Seconds;

        /// <summary>Checked at the start of each cycle (after the previous one ended).</summary>
        public bool PlaysAgain(float stateSeconds) => stateSeconds <= Deadline;

        /// <param name="roll100">a uniform random integer in [0, 100)</param>
        public static bool PicksAmbient(int roll100) => roll100 < AmbientPercent;

        /// <summary>
        /// How many cycles of a <paramref name="loopSeconds"/> loop the action plays (no Ambient), the loop starting at
        /// <paramref name="beginSeconds"/> of state time: PickOre 0.8 + 2.0 → 5, DigGround 0.8 + 2.4 → 4.
        /// </summary>
        public static int Cycles(float beginSeconds, float loopSeconds, int seconds)
        {
            if (loopSeconds <= 0f) return 1;
            var action = new TimedAction(beginSeconds, seconds);
            int n = 0;
            for (float t = beginSeconds; action.PlaysAgain(t) && n < 10000; t += loopSeconds) n++;
            return n;
        }
    }
}
