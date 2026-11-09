namespace Myrtana.Rules
{
    /// <summary>The body stance a human is in (G3 gEAniState: 2 Stand, 3 Sneak, 4 Parade); it picks the motion set.</summary>
    public enum BodyStance { Stand, Sneak, Parade }

    /// <summary>
    /// Sneaking with a weapon and attacking from it (G3 CP 1.75 Script_Game, Docs/combat.md §1.6):
    /// * The sneak key (PS_Normal_Sneak / PS_Melee_Sneak / FUN_10001e20) puts a human with a melee weapon configuration
    ///   (fists included) into the <b>Parade</b> ani-state, anyone else (unarmed, bow, crossbow, spell) into <b>Sneak</b>;
    ///   both with the sneak movement mode. The data has no armed melee Sneak set (no <c>Hero_Sneak_None_1H_*</c>): the
    ///   crouched guard walk is <c>Hero_Parade_&lt;hands&gt;_P0_Move_*</c>.
    /// * The attack commands set the ani-state for the move: _AI_Attack, _AI_PowerAttack, _AI_QuickAttack and
    ///   _AI_SimpleWhirl → Stand (the hero rises for the swing, the Stand_* motions play); _AI_PierceAttack,
    ///   _AI_WhirlAttack, _AI_HackAttack, _AI_FinishingAttack and _AI_Parade → Parade.
    /// * The sneak toggle itself is not touched by an attack; only raising the block (_AI_Parade → FUN_10001e20(0)) and a
    ///   jump end it. So after the swing the hero is back in the sneak stance [interp: the engine owns the stance while
    ///   the toggle is on; the scripts never reset it].
    /// * No sneak damage bonus exists. Sneaking only keeps the victim unaware: the special player hits (Perk_KnockOut
    ///   power attack with impact damage, Perk_Assassin pierce) need the victim in AI mode Routine and the attacker out
    ///   of its field of view (AssessHit; <see cref="CombatResolver"/>).
    /// * Blocking: the Parade ani-state counts as parrying in G3 (IsInParadeMode), so vanilla blocks while sneaking with
    ///   a melee weapon; CP 1.75 (#54): with AB or AAI the hero no longer blocks in the sneak stance.
    /// </summary>
    public static class SneakCombatRules
    {
        /// <summary>The stance of a sneaking human: Parade with a melee weapon (or fists) drawn, else Sneak.</summary>
        public static BodyStance SneakStance(bool meleeDrawn) => meleeDrawn ? BodyStance.Parade : BodyStance.Sneak;

        /// <summary>The stance whose motions a move plays (the ani-state its _AI_ command sets).</summary>
        public static BodyStance MoveStance(CombatAction action)
        {
            switch (action)
            {
                case CombatAction.PierceAttack:
                case CombatAction.WhirlAttack:
                case CombatAction.HackAttack:
                case CombatAction.FinishingAttack:
                case CombatAction.Parade:
                    return BodyStance.Parade;
                default:
                    return BodyStance.Stand;
            }
        }

        /// <summary>The hero stands up for this move when it starts from the sneak stance.</summary>
        public static bool RisesFromSneak(CombatAction action) => MoveStance(action) == BodyStance.Stand;

        /// <summary>The sneak toggle survives the move (only the block, _AI_Parade, ends it).</summary>
        public static bool KeepsSneaking(CombatAction action) => action != CombatAction.Parade && action != CombatAction.GetUpParade;

        /// <summary>The sneak stance blocks melee like a raised parade (vanilla only; CP: not with AB or AAI).</summary>
        public static bool SneakStanceBlocks(RuleOptions options) =>
            options != null && !options.AlternativeBalancing && !options.ExtendedAI;

        /// <summary>
        /// G3 eCEntity::IsInFOV: the target lies in the front half-space of the observer (|azimuth| ≤ 90°). Arguments are
        /// the observer's horizontal forward and the horizontal direction to the target (any length); a target at the
        /// observer's position counts as seen.
        /// </summary>
        public static bool InFieldOfView(float forwardX, float forwardZ, float toX, float toZ)
        {
            if (toX * toX + toZ * toZ < 1e-8f) return true;
            return forwardX * toX + forwardZ * toZ >= 0;
        }

        /// <summary>
        /// G3 AI mode Routine (gEAIMode 0): not fighting or fleeing, not in a reaction (warning, observing, ZS_ReactTo*),
        /// not down or dead, not held by a spell (sleep, freeze: AI mode 7) and not talking.
        /// </summary>
        public static bool InRoutine(bool fighting, bool reacting, bool spellbound, bool talking, bool alive = true) =>
            alive && !fighting && !reacting && !spellbound && !talking;

        /// <summary>The victim may take a special player hit (knock-out, assassination): in Routine and not seeing the attacker.</summary>
        public static bool Unaware(bool victimInRoutine, bool victimSeesAttacker) => victimInRoutine && !victimSeesAttacker;
    }
}
