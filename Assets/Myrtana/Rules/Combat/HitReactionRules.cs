namespace Myrtana.Rules
{
    /// <summary>
    /// What a hit reaction does to the hero's input and body (G3 CP 1.75, fix-45; Docs/combat.md §9.7):
    /// <list type="bullet">
    /// <item>AssessHit sets the task ZS_Stumble / ZS_QuickStumble / ZS_PierceStumble / ZS_ParadeStumble /
    /// ZS_HeavyParadeStumble, also for the player: the running move is cut, and OnPlayerAction / OnPlayerSecondaryAction act
    /// only while the task is PS_Melee, so presses during the reaction are ignored (a button still held acts afterwards).</item>
    /// <item>ZS_Stumble, ZS_QuickStumble and ZS_PierceStumble start with ResetAll (the input queue is cleared, the parade
    /// lowered); the parried stumbles do not (<see cref="ResetsInput"/>).</item>
    /// <item>Every _AI_*Stumble plays its phase Hit through sAICombatMoveInstr, which starts the matching Recover by itself
    /// and returns: the reaction is over with its Hit (AssessAgressor → PS_Melee), the Recover plays on as a tail that the next
    /// action or walking cuts (<see cref="RecoverIsTail"/>).</item>
    /// <item>The motion variant (Fwd/Back) follows where the hitter stands (<see cref="PlaysBackVariant"/>, fix-244).</item>
    /// </list>
    /// </summary>
    public static class HitReactionRules
    {
        /// <summary>The reaction's task starts with ResetAll: the queued move is dropped and the parade lowered.</summary>
        public static bool ResetsInput(HitReaction reaction) =>
            reaction != HitReaction.ParadeStumble && reaction != HitReaction.HeavyParadeStumble;

        /// <summary>The stumbles end with their Hit; the Recover is a cosmetic tail (sAICombatMoveStartRecover).</summary>
        public const bool RecoverIsTail = true;

        /// <summary>
        /// fix-244 [CP 1.75 Game.dll sAICombatMoveStart 0x1016abb0]: the direction word of a reaction or fall motion.
        /// "Fwd" by default; for QuickStumble, Stumble/StumbleR/StumbleL, PierceStumble and the falls LieKnockDown (also the
        /// sitting knock-down, which ZS_SitKnockDown plays as LieKnockDown), LieKnockOut, LiePiercedKO, LieDead and
        /// LiePiercedDead it is "Back" when the other entity (the hitter) is in front: dot(own forward, hitter − self) > 0;
        /// no hitter counts as in front (the engine falls back to the body's own forward). The parade stumbles always
        /// play "Fwd". A rig without the Back motion falls back to Fwd. The port had it the other way round (Back only
        /// from behind), so a frontal pierce played PierceStumble_Hit_N_Fwd (hero rig 1.2 s) instead of G3's
        /// PierceStumble_Hit_N_Back (2.8 s): the victim was up again before the attacker's next blow (owner).
        /// </summary>
        public static bool PlaysBackVariant(HitReaction reaction, bool hitterInFront)
        {
            switch (reaction)
            {
                case HitReaction.QuickStumble:
                case HitReaction.Stumble:
                case HitReaction.PierceStumble:
                case HitReaction.SitKnockDown:
                case HitReaction.LieKnockDown:
                case HitReaction.Unconscious:
                case HitReaction.PiercedKnockOut:
                case HitReaction.Dead:
                case HitReaction.PiercedDead:
                    return hitterInFront;
                default:
                    return false;
            }
        }

        /// <summary>G3's test for <see cref="PlaysBackVariant"/>: the dot of the body's forward with the way to the hitter is positive.</summary>
        public static bool HitterInFront(float dotForwardToHitter) => dotForwardToHitter > 0f;

        /// <summary>
        /// The body shows the hit's reaction (stumble or sitting knock-down). Not when it is frozen or this hit freezes it:
        /// G3 ZS_Freeze holds the body at time scale 0 until the freeze ends, so a stumble started there could never play
        /// (fix-48: r29 wolfclan_01, an IceGolem froze the hero, his StumbleL stood at time scale 0 until the 10 s safety end).
        /// </summary>
        public static bool ShowsReaction(bool frozen, float freezeSeconds) => !frozen && !(freezeSeconds > 0f);

        /// <summary>
        /// fix-209 [owner, held intent as fix-197/fix-203]: a ResetAll stumble cut the hero's bow aim while the shoot button
        /// was held and an arrow stays on the string (the stumble does not un-nock it): the aim intent survives the stumble.
        /// The held button raises the bow again once the reaction's Hit is over (Aim_Raise → PowerRaise → Aim, G3 timing:
        /// the draw starts from zero, as a new PS_Ranged_Aim), and a release that comes before the bow is up again queues the
        /// shot, which flies as soon as the raise reaches the draw (G3's one-slot queue: the release enqueues the shot).
        /// Before, the release during the stumble did nothing and every hit cost the shot.
        /// </summary>
        public static bool KeepsAimIntent(HitReaction reaction, bool shootHeld, bool nocked) =>
            ResetsInput(reaction) && shootHeld && nocked;

        /// <summary>
        /// The shoot button was released without a shot (the bow was not up): what is queued. <paramref name="aimQueued"/>:
        /// an aim waits (a press during the draw or reload); <paramref name="aimCutByHit"/>: <see cref="KeepsAimIntent"/>
        /// held for the last stumble and the bow is not raised again yet.
        /// </summary>
        public static RangedReleaseQueue OnReleaseWithoutShot(bool aimQueued, bool aimCutByHit, bool nocked)
        {
            if (aimQueued) return RangedReleaseQueue.Shot;
            if (aimCutByHit && nocked) return RangedReleaseQueue.AimAndShot;
            return RangedReleaseQueue.None;
        }
    }

    /// <summary>What a shoot-button release that shot nothing leaves queued (<see cref="HitReactionRules.OnReleaseWithoutShot"/>).</summary>
    public enum RangedReleaseQueue { None, Shot, AimAndShot }
}
