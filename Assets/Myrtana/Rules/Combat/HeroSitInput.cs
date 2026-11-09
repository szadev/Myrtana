namespace Myrtana.Rules
{
    /// <summary>The press edges of one frame that matter to a sitting hero (session keys, G3Input / KeyMap).</summary>
    public struct HeroSitPresses
    {
        /// <summary>Session key Use1 (G3 default: left mouse button).</summary>
        public bool Use1;
        /// <summary>Session key Use2 (G3 default: right mouse button).</summary>
        public bool Use2;
        /// <summary>Forward, Backward, StrafeLeft or StrafeRight.</summary>
        public bool Move;
        /// <summary>Session key Up (jump).</summary>
        public bool Jump;
    }

    /// <summary>How the hero sits (or lies) right now.</summary>
    public struct HeroSitState
    {
        /// <summary>Knocked into the sitting position (G3 ZS_SitKnockDown).</summary>
        public bool SittingKnockedDown;
        /// <summary>On a bench, stool or throne (G3 PS_Interact_SitBench/SitStool/SitThrone; par-interact).</summary>
        public bool Seated;
        /// <summary>Knocked out or dead: lies until the vitals get him up (no press helps).</summary>
        public bool KnockedOut;
        /// <summary>The mouse steers the camera (no page or menu has the cursor).</summary>
        public bool CursorLocked;
        /// <summary>A fight or a hit reached the seated hero (a fight against him, a stumble or a knock-down).</summary>
        public bool Disturbed;
    }

    /// <summary>What a frame does to a sitting hero.</summary>
    public enum HeroSitExit
    {
        /// <summary>He keeps sitting (or this is no sit).</summary>
        None,
        /// <summary>ZS_SitKnockDown_GetUpAttack (the primary press).</summary>
        GetUpAttack,
        /// <summary>ZS_SitKnockDown_GetUpParade (the secondary press).</summary>
        GetUpParade,
        /// <summary>PS_Interact_WaitForExit: he stands up from the seat.</summary>
        LeaveSeat,
    }

    /// <summary>
    /// The ways out of sitting (fix-39; Docs/combat.md §9.5, par-interact):
    /// * Knocked into sitting (ZS_SitKnockDown_Loop): only a press edge of Use1 (GetUpAttack) or Use2 (GetUpParade) gets
    ///   him up; movement keys do nothing; no timeout. A press while a page has the cursor belongs to the page. The press
    ///   counts whatever is in the hands (a non-melee config stands up plainly, <see cref="SitKnockDownRules.PlayerGetUp"/>).
    /// * On a seat (PS_Interact_WaitForExit): a movement key, jump, Use1 or Use2 stands him up; so does a fight or a hit
    ///   (the knock-down then takes over the body). Use2: CP 1.75 OnPlayerSecondaryAction (0x10062ff0) queues action 0x66
    ///   on its press edge in task PS_Interact, the exit PS_Interact_WaitForExit and OnInteract_Loop wait for (fix-75;
    ///   the owner asked for it too).
    /// * Knocked out: no press helps; the down state ends by the vitals (ActorVitals.TickDown).
    /// When both apply (hit on a seat into the sitting knock-down) the seat is left first; the next press gets him up.
    /// </summary>
    public static class HeroSitInput
    {
        public static HeroSitExit Decide(in HeroSitState state, in HeroSitPresses presses)
        {
            if (state.KnockedOut) return HeroSitExit.None;
            if (state.Seated)
            {
                if (state.Disturbed) return HeroSitExit.LeaveSeat;
                return presses.Move || presses.Jump || presses.Use1 || presses.Use2 ? HeroSitExit.LeaveSeat : HeroSitExit.None;
            }
            if (!state.SittingKnockedDown || !state.CursorLocked) return HeroSitExit.None;
            if (presses.Use1) return HeroSitExit.GetUpAttack;
            if (presses.Use2) return HeroSitExit.GetUpParade;
            return HeroSitExit.None;
        }
    }
}
