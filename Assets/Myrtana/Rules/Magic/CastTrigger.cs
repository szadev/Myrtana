namespace Myrtana.Rules
{
    /// <summary>What letting go of the cast button does to a running cast (<see cref="CastTrigger.OnButtonUp"/>).</summary>
    public enum CastButtonUp
    {
        /// <summary>Nothing: the cast runs on through its motion and goes off by itself.</summary>
        Nothing,
        /// <summary>The power cast was let go before it powered up: it restarts as a plain Cast (G3 FullStop → PS_Magic_Cast).</summary>
        RecastPlain,
    }

    /// <summary>
    /// When a cast goes off in G3 CP 1.75 (fix-114) [Script_Game OnMagicHandleInput 0x1007d360, PS_Magic_Cast /
    /// PS_Magic_PowerCast / PS_Magic_QuickCast / PS_Magic_Summon → _AI_Cast = _AI_Cast_Begin + _AI_Cast_Execute]. Pressing
    /// Use1 queues GetCastAction and enters the cast's state; the state runs _AI_Cast to its end: the begin motion (phase
    /// load, for a power cast also power), then the execute motion, whose cast phase lets the spell go. None of these reads
    /// the button: the spell goes off during the cast motion, not when the button is let go. The only use of the release
    /// is in PS_Magic_PowerCast: let go before the state ran 0.7 s, the power cast is stopped and restarted as a plain
    /// PS_Magic_Cast (unpowered); later releases are ignored. NPCs (ZS_Attack_Cast) run the same _AI_Cast.
    /// </summary>
    public static class CastTrigger
    {
        /// <summary>
        /// Whether a cast begun with <paramref name="action"/> goes off by itself at the end of its raise motion: every action
        /// but the power cast, which first waits for its power-up (or the early release).
        /// </summary>
        public static bool GoesOffByItself(CastAction action) => action != CastAction.PowerCast;

        /// <summary>
        /// Whether a cast goes off now (at the end of its raise motion): a plain cast always, a power cast once it powered up
        /// (held <see cref="MagicRules.PowerCastHoldSeconds"/>).
        /// </summary>
        public static bool GoesOff(CastAction action, bool powered) => GoesOffByItself(action) || powered;

        /// <summary>
        /// The cast button was let go <paramref name="heldSeconds"/> after the cast began; <paramref name="powerHoldSeconds"/>:
        /// G3's 0.7 s (<see cref="MagicRules.PowerCastHoldSeconds"/>).
        /// </summary>
        public static CastButtonUp OnButtonUp(CastAction action, bool released, bool powered, double heldSeconds, double powerHoldSeconds) =>
            action == CastAction.PowerCast && !released && !powered && heldSeconds < powerHoldSeconds ? CastButtonUp.RecastPlain : CastButtonUp.Nothing;
    }
}
