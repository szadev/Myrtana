namespace Myrtana.Rules
{
    /// <summary>
    /// Getting up from the sitting knock-down (G3 CP 1.75 Script_Game ZS_SitKnockDown, _Loop, _GetUpAttack, _GetUpParade;
    /// Docs/combat.md §1.5, §9.5):
    /// * Only a humanoid with the melee weapon config is knocked into sitting (AssessHit, D &gt; 3).
    /// * ZS_SitKnockDown plays the fall (SitKnockDown combat move), then the sit motion once, then ZS_SitKnockDown_Loop.
    /// * The player stays in the loop until a press edge: primary → GetUpAttack, secondary → GetUpParade (OnPlayerAction /
    ///   OnPlayerSecondaryAction queue them; a press during the fall waits in the queue). There is no timeout.
    /// * An NPC leaves the loop at once: GetUpParade when its current attacker's action is a PowerAttack, PierceAttack
    ///   or HackAttack, else GetUpAttack.
    /// * Both get-up states run their move only with the melee weapon config (FUN_10003240 == 1), else _AI_StandUp.
    /// * _AI_GetUpAttack: gEAction GetUpAttack, hit direction 1, the Hit only (the Recover is a tail), speed 1.0,
    ///   no stamina, action level 2, damage ×1 with the weapon's damage type.
    /// </summary>
    public static class SitKnockDownRules
    {
        /// <summary>The NPC's way up from the sit loop (None = plain stand-up, _AI_StandUp).</summary>
        public static CombatAction NpcGetUp(CombatAction attackerAction, bool meleeConfig)
        {
            if (!meleeConfig) return CombatAction.None;
            return attackerAction == CombatAction.PowerAttack || attackerAction == CombatAction.PierceAttack ||
                   attackerAction == CombatAction.HackAttack
                ? CombatAction.GetUpParade
                : CombatAction.GetUpAttack;
        }

        /// <summary>The player's way up for a press (primary or secondary); None = plain stand-up.</summary>
        public static CombatAction PlayerGetUp(bool primary, bool meleeConfig) =>
            !meleeConfig ? CombatAction.None : primary ? CombatAction.GetUpAttack : CombatAction.GetUpParade;

        /// <summary>
        /// Seconds into the GetUpParade after which it parries: G3 OnAI_GetUpParade sets the ani state Parade (and, for the
        /// player, the "parade raised" flag) once the state time passes 0.25 s.
        /// </summary>
        public const float GetUpParadeRaiseSeconds = 0.25f;

        /// <summary>
        /// The GetUpParade blocks like a raised parade (CanParade: ani state Parade) after <see cref="GetUpParadeRaiseSeconds"/>;
        /// its defence level is then 2 (<see cref="CombatStats.DefenseLevel"/>), so a parried power attack staggers
        /// (HeavyParadeStumble) instead of sitting the fighter down again (fix-67).
        /// </summary>
        public static bool GetUpParadeBlocks(float secondsInGetUpParade) => secondsInGetUpParade > GetUpParadeRaiseSeconds;

        /// <summary>Whether the sitter leaves the loop by itself: an NPC after the sit motion played once, the player never.</summary>
        public static bool GetsUpByItself(bool isPlayer) => !isPlayer;
    }
}
