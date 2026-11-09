namespace Myrtana.Rules
{
    /// <summary>What the action loop starts this frame.</summary>
    public enum ActionLoopStep
    {
        None,
        /// <summary>The Ambient variant (one cycle of it).</summary>
        Ambient,
        /// <summary>Back to the action loop after the Ambient variant.</summary>
        Loop,
    }

    /// <summary>
    /// G3 PS_Interact_Loop (pure): while the hero works at an object the action's Loop repeats, and at the end of each
    /// cycle there is a 10 % chance (<c>GetRandomNumber(100) &lt; 10</c>) of one cycle of the Ambient variant
    /// (GrindStone_Ambient, Anvil_Ambient with its soft hammer taps, Waterpipe_Ambient), after which the loop resumes.
    /// The Ambient motions loop in the data (phase Ambient), so their end is the first wrap of their phase.
    /// Fed once a frame with the current motion's phase.
    /// </summary>
    public sealed class ActionLoop
    {
        public const double AmbientChance = 0.1;

        float _last;

        /// <summary>One Ambient cycle is playing.</summary>
        public bool InAmbient { get; private set; }

        /// <summary>The loop (re)started: phase counting begins again.</summary>
        public void LoopStarted()
        {
            InAmbient = false;
            _last = 0f;
        }

        /// <param name="phase">normalised phase of the current motion (0..1, wraps for loops)</param>
        /// <param name="finished">the current one-shot reached its end</param>
        /// <param name="roll">a uniform random number in [0, 1), drawn per frame</param>
        public ActionLoopStep Step(float phase, bool finished, double roll)
        {
            bool wrapped = phase < _last;
            _last = phase;
            if (InAmbient)
            {
                if (!finished && !wrapped) return ActionLoopStep.None;
                LoopStarted();
                return ActionLoopStep.Loop;
            }
            if (!wrapped || roll >= AmbientChance) return ActionLoopStep.None;
            InAmbient = true;
            _last = 0f;
            return ActionLoopStep.Ambient;
        }

        /// <summary>The pose has no Ambient variant: stay in the loop.</summary>
        public void AmbientMissing() => LoopStarted();
    }
}
