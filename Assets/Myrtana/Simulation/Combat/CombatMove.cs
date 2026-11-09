using Myrtana.Rules;

namespace Myrtana.Simulation
{
    /// <summary>
    /// A move the NPC combat AI can choose (the G3 gEAction values used by SelectCombatMove): attacks and the parade are
    /// combat actions (<see cref="CombatMoves.ToAction"/>), Shoot and Cast use the ranged weapon or spell, the rest is
    /// footwork. Move is the CP flank/sprint run.
    /// </summary>
    public enum CombatMove
    {
        Wait,
        Fwd,
        Back,
        Left,
        Right,
        TurnLeft,
        TurnRight,
        Move,
        JumpBack,
        Parade,
        Attack,
        PowerAttack,
        QuickAttack,
        SimpleWhirl,
        WhirlAttack,
        PierceAttack,
        HackAttack,
        SprintAttack,
        FinishingAttack,
        /// <summary>Ranged weapon: aim and shoot.</summary>
        Shoot,
        /// <summary>Magic: cast the held spell.</summary>
        Cast,
    }

    /// <summary>
    /// What a fighter is doing as others see it (G3 gEAction subset the move choice reads). The presentation reports it for
    /// the target; the AI classifies it into attacking, moving, parrying, stumbling and so on.
    /// </summary>
    public enum FighterAction
    {
        None,
        Attack,
        PowerAttack,
        QuickAttack,
        QuickAttackRight,
        QuickAttackLeft,
        SimpleWhirl,
        SprintAttack,
        WhirlAttack,
        PierceAttack,
        HackAttack,
        RamAttack,
        FinishingAttack,
        GetUpAttack,
        Parade,
        ParadeRight,
        ParadeLeft,
        GetUpParade,
        QuickParadeStumble,
        ParadeStumble,
        ParadeStumbleRight,
        ParadeStumbleLeft,
        HeavyParadeStumble,
        QuickStumble,
        Stumble,
        StumbleRight,
        StumbleLeft,
        PierceStumble,
        TurnLeft,
        TurnRight,
        JumpBack,
        Fwd,
        Back,
        Left,
        Right,
        Move,
        Jump,
        Slide,
        Fall,
        Aim,
        Shoot,
        Reload,
        Summon,
        Heal,
        Cast,
        Wait,
    }

    /// <summary>The phase of a running move (G3 Raise → Hit → Recover); monsters time their dodges on it.</summary>
    public enum MovePhase { None, Raise, Hit, Recover }

    public static class CombatMoves
    {
        /// <summary>A strike (melee, shot or spell); these wait for the hit cooldown.</summary>
        public static bool IsAttack(this CombatMove m) => m >= CombatMove.Attack;

        /// <summary>A melee strike.</summary>
        public static bool IsMeleeAttack(this CombatMove m) => m >= CombatMove.Attack && m <= CombatMove.FinishingAttack;

        /// <summary>The combat action a move starts (stamina, rules); None for footwork.</summary>
        public static CombatAction ToAction(this CombatMove m)
        {
            switch (m)
            {
                case CombatMove.Attack: return CombatAction.Attack;
                case CombatMove.PowerAttack: return CombatAction.PowerAttack;
                case CombatMove.QuickAttack: return CombatAction.QuickAttack;
                case CombatMove.SimpleWhirl: return CombatAction.SimpleWhirl;
                case CombatMove.WhirlAttack: return CombatAction.WhirlAttack;
                case CombatMove.PierceAttack: return CombatAction.PierceAttack;
                case CombatMove.HackAttack: return CombatAction.HackAttack;
                case CombatMove.SprintAttack: return CombatAction.SprintAttack;
                case CombatMove.FinishingAttack: return CombatAction.FinishingAttack;
                case CombatMove.Parade: return CombatAction.Parade;
                case CombatMove.JumpBack: return CombatAction.JumpBack;
                default: return CombatAction.None;
            }
        }

        /// <summary>What others see while this move runs.</summary>
        public static FighterAction ToFighterAction(this CombatMove m)
        {
            switch (m)
            {
                case CombatMove.Wait: return FighterAction.Wait;
                case CombatMove.Fwd: return FighterAction.Fwd;
                case CombatMove.Back: return FighterAction.Back;
                case CombatMove.Left: return FighterAction.Left;
                case CombatMove.Right: return FighterAction.Right;
                case CombatMove.TurnLeft: return FighterAction.TurnLeft;
                case CombatMove.TurnRight: return FighterAction.TurnRight;
                case CombatMove.Move: return FighterAction.Move;
                case CombatMove.JumpBack: return FighterAction.JumpBack;
                case CombatMove.Parade: return FighterAction.Parade;
                case CombatMove.Attack: return FighterAction.Attack;
                case CombatMove.PowerAttack: return FighterAction.PowerAttack;
                case CombatMove.QuickAttack: return FighterAction.QuickAttack;
                case CombatMove.SimpleWhirl: return FighterAction.SimpleWhirl;
                case CombatMove.WhirlAttack: return FighterAction.WhirlAttack;
                case CombatMove.PierceAttack: return FighterAction.PierceAttack;
                case CombatMove.HackAttack: return FighterAction.HackAttack;
                case CombatMove.SprintAttack: return FighterAction.SprintAttack;
                case CombatMove.FinishingAttack: return FighterAction.FinishingAttack;
                case CombatMove.Shoot: return FighterAction.Shoot;
                case CombatMove.Cast: return FighterAction.Cast;
                default: return FighterAction.None;
            }
        }

        /// <summary>combat_ai.json targetAttackingActions.</summary>
        public static bool IsAttacking(this FighterAction a) =>
            a == FighterAction.Attack || a == FighterAction.PowerAttack || a == FighterAction.QuickAttack ||
            a == FighterAction.QuickAttackRight || a == FighterAction.QuickAttackLeft || a == FighterAction.SimpleWhirl ||
            a == FighterAction.SprintAttack || a == FighterAction.WhirlAttack || a == FighterAction.PierceAttack ||
            a == FighterAction.HackAttack;

        /// <summary>Light attacks (the "target light attack" situation).</summary>
        public static bool IsLightAttack(this FighterAction a) =>
            a == FighterAction.Attack || a == FighterAction.QuickAttack || a == FighterAction.QuickAttackRight ||
            a == FighterAction.QuickAttackLeft || a == FighterAction.SimpleWhirl || a == FighterAction.WhirlAttack;

        /// <summary>Heavy attacks (the "target heavy attack" situation).</summary>
        public static bool IsHeavyAttack(this FighterAction a) =>
            a == FighterAction.PowerAttack || a == FighterAction.PierceAttack || a == FighterAction.RamAttack || a == FighterAction.HackAttack;

        public static bool IsMoving(this FighterAction a) =>
            a == FighterAction.TurnLeft || a == FighterAction.TurnRight || a == FighterAction.JumpBack || a == FighterAction.Fwd ||
            a == FighterAction.Back || a == FighterAction.Left || a == FighterAction.Right || a == FighterAction.Move ||
            a == FighterAction.Jump || a == FighterAction.Slide || a == FighterAction.Fall;

        public static bool IsParading(this FighterAction a) =>
            a == FighterAction.Parade || a == FighterAction.ParadeRight || a == FighterAction.ParadeLeft || a == FighterAction.GetUpParade;

        /// <summary>Lightly stumbling, aiming, shooting or casting: open for a quick strike.</summary>
        public static bool IsBusy(this FighterAction a) =>
            a == FighterAction.QuickParadeStumble || a == FighterAction.ParadeStumble || a == FighterAction.ParadeStumbleRight ||
            a == FighterAction.ParadeStumbleLeft || a == FighterAction.QuickStumble || a == FighterAction.Aim ||
            a == FighterAction.Shoot || a == FighterAction.Reload || a == FighterAction.Summon || a == FighterAction.Heal ||
            a == FighterAction.Wait;

        /// <summary>Heavily staggered: open for a heavy strike.</summary>
        public static bool IsStaggered(this FighterAction a) =>
            a == FighterAction.HeavyParadeStumble || a == FighterAction.Stumble || a == FighterAction.StumbleRight ||
            a == FighterAction.StumbleLeft || a == FighterAction.PierceStumble;
    }
}
