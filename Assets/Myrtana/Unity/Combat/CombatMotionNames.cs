using G3.Unity.Characters;
using Myrtana.Rules;
using Myrtana.Simulation;

namespace Myrtana.Unity
{
    /// <summary>
    /// Rule vocabulary → G3 motion vocabulary (Docs/combat.md §9): which G3 action shows a <see cref="CombatAction"/> or a
    /// <see cref="HitReaction"/>, and which hand field a weapon class puts into the motion names. Lists are tried in order;
    /// the first motion the rig has wins.
    /// </summary>
    public static class CombatMotionNames
    {
        /// <summary>Hand field of a weapon class in motion names (<see cref="WeaponHold.MotionField"/>: axes 2H, staves and halberds Staff).</summary>
        public static string HandField(WeaponClass weapon) => WeaponHold.MotionField(weapon);

        public static string[] Actions(CombatAction action)
        {
            switch (action)
            {
                case CombatAction.PowerAttack:
                case CombatAction.HackAttack: return new[] { "PowerAttack", "Attack" };
                case CombatAction.SprintAttack: return new[] { "SprintAttack", "PowerAttack", "Attack" };
                case CombatAction.QuickAttack:
                case CombatAction.QuickAttackLeft: return new[] { "QuickAttackL", "QuickAttackR", "Attack" };
                case CombatAction.QuickAttackRight: return new[] { "QuickAttackR", "QuickAttackL", "Attack" };
                case CombatAction.SimpleWhirl:
                case CombatAction.WhirlAttack: return new[] { "WhirlAttack", "TwirlAttack", "PowerAttack", "Attack" };
                case CombatAction.PierceAttack: return new[] { "PierceAttack", "Attack" };
                // G3 sAICombatMoveStart has no fallback: without a FinishingAttack motion (orcs with a 1H weapon, fists) the move
                // fails and _AI_FinishingAttack kills nothing (combat-6, Docs/combat.md §5.5).
                case CombatAction.FinishingAttack: return new[] { "FinishingAttack" };
                case CombatAction.GetUpAttack: return new[] { "GetUpAttack", "Attack" };
                case CombatAction.JumpBack: return new[] { "JumpBack" };
                default: return new[] { "Attack" };
            }
        }

        /// <summary>The survivor reactions; knock-downs and deaths go through the down states instead.</summary>
        public static string[] Reaction(HitReaction reaction, bool rightSide)
        {
            string side = rightSide ? "R" : "L", other = rightSide ? "L" : "R";
            switch (reaction)
            {
                case HitReaction.QuickStumble: return new[] { "QuickStumble", "LightStumble", "Stumble" + side, "Stumble" + other };
                case HitReaction.Stumble: return new[] { "Stumble" + side, "Stumble" + other, "QuickStumble" };
                case HitReaction.PierceStumble: return new[] { "PierceStumble", "Stumble" + side, "Stumble" + other };
                case HitReaction.ParadeStumble:
                case HitReaction.HeavyParadeStumble: return new[] { "ParadeStumble" + side, "ParadeStumble" + other, "QuickStumble", "Stumble" + side };
                default: return null;
            }
        }

        /// <summary>Down stance of a knock-down or knock-out reaction, null for the others.</summary>
        public static string DownStance(HitReaction reaction)
        {
            switch (reaction)
            {
                case HitReaction.SitKnockDown: return "SitKnockDown";
                case HitReaction.LieKnockDown: return "LieKnockDown";
                case HitReaction.Unconscious: return "LieKnockOut";
                case HitReaction.PiercedKnockOut: return "LiePiercedKO";
                default: return null;
            }
        }

        public static FighterAction ToFighterAction(CombatAction action)
        {
            switch (action)
            {
                case CombatAction.Attack: return FighterAction.Attack;
                case CombatAction.PowerAttack: return FighterAction.PowerAttack;
                case CombatAction.SprintAttack: return FighterAction.SprintAttack;
                case CombatAction.QuickAttack: return FighterAction.QuickAttack;
                case CombatAction.QuickAttackLeft: return FighterAction.QuickAttackLeft;
                case CombatAction.QuickAttackRight: return FighterAction.QuickAttackRight;
                case CombatAction.SimpleWhirl: return FighterAction.SimpleWhirl;
                case CombatAction.WhirlAttack: return FighterAction.WhirlAttack;
                case CombatAction.PierceAttack: return FighterAction.PierceAttack;
                case CombatAction.HackAttack: return FighterAction.HackAttack;
                case CombatAction.FinishingAttack: return FighterAction.FinishingAttack;
                case CombatAction.GetUpAttack: return FighterAction.GetUpAttack;
                case CombatAction.Parade: return FighterAction.Parade;
                case CombatAction.GetUpParade: return FighterAction.GetUpParade;
                case CombatAction.Jump: return FighterAction.Jump;
                case CombatAction.JumpBack: return FighterAction.JumpBack;
                default: return FighterAction.None;
            }
        }

        public static MovePhase ToMovePhase(G3SwingPhase phase)
        {
            switch (phase)
            {
                case G3SwingPhase.Raise: return MovePhase.Raise;
                case G3SwingPhase.Hit: return MovePhase.Hit;
                case G3SwingPhase.Recover: return MovePhase.Recover;
                default: return MovePhase.None;
            }
        }
    }
}
