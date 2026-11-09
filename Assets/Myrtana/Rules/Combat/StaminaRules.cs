using System;

namespace Myrtana.Rules
{
    /// <summary>What the per-second stamina tick needs to know about the player (gameplay-logic.md §2.3).</summary>
    public struct PlayerStaminaState
    {
        public bool Sprinting;
        public bool Jumping;
        /// <summary>The speed buff or the bloodfly form (sprinting costs only 1).</summary>
        public bool SpeedBuff;
        public bool HasSprinterPerk;
        public bool Blocking;
        public bool Diseased;
        /// <summary>Temperature above 40 without Perk_ResistHeat, or below −40 without Perk_ResistCold.</summary>
        public bool ExtremeTemperature;
        /// <summary>The Keep-Stamina buff or the god cheat freeze the player's stamina.</summary>
        public bool StaminaFrozen;
        /// <summary>A GUI page is open (AB: no regeneration then).</summary>
        public bool InMenu;
    }

    /// <summary>
    /// The stamina model: what a move costs when it starts, and how stamina changes every second. Moves are never
    /// refused for lack of stamina (G3 has no such check); stamina floors at 0 and only the whirl falls back.
    /// </summary>
    public static class StaminaRules
    {
        public const int NpcRegenPerSecond = 10;

        /// <summary>
        /// Pays a starting move's stamina and returns the move that actually starts: a whirl with no stamina left after
        /// paying becomes a normal attack (combat_moves.json whirlFallback).
        /// </summary>
        public static CombatAction PayForMove(ref int stamina, CombatAction action, CombatRules rules, RuleOptions options,
                                              bool godMode = false)
        {
            if (godMode) return action;
            stamina = Math.Max(0, stamina - rules.StaminaCost(action, options));
            return action == CombatAction.WhirlAttack && stamina <= 0 ? CombatAction.Attack : action;
        }

        /// <summary>Stamina change of the player in one second.</summary>
        public static int PlayerPerSecond(in PlayerStaminaState s, RuleOptions options)
        {
            if (s.StaminaFrozen) return 0;
            bool ab = options.AlternativeBalancing;
            if (ab && s.InMenu) return 0;
            if (s.Sprinting)
            {
                if (s.SpeedBuff) return -1;
                if (s.HasSprinterPerk) return ab ? -4 : -5;
                return ab ? -8 : -10;
            }
            if (s.Jumping) return 0;
            if (ab && s.Blocking) return 1;
            if (s.Diseased) return 1;
            if (s.ExtremeTemperature) return 2;
            return 10;
        }
    }

    /// <summary>
    /// CP 1.75 player attack pressure ("swing counter"): every attack the player starts adds points, −50 per second.
    /// NPCs read it: above 119 a melee NPC may answer with a sprint attack (extended AI); above 180 zombies take no damage.
    /// </summary>
    public sealed class AttackPressure
    {
        public const int SprintAnswerThreshold = 119;
        public const int ZombieImmuneThreshold = 180;
        const int DecayPerSecond = 50;

        public int Value { get; private set; }

        public void OnAttackStarted(CombatAction action) => Value += PointsFor(action);

        public void TickSecond() => Value = Math.Max(0, Value - DecayPerSecond);

        public void Reset() => Value = 0;

        public static int PointsFor(CombatAction action)
        {
            switch (action)
            {
                case CombatAction.Attack: return 85;
                case CombatAction.PowerAttack: return 60;
                case CombatAction.QuickAttack:
                case CombatAction.QuickAttackLeft:
                case CombatAction.QuickAttackRight:
                case CombatAction.SimpleWhirl: return 90;
                case CombatAction.WhirlAttack: return 75;
                case CombatAction.PierceAttack:
                case CombatAction.HackAttack: return 40;
                default: return 0;
            }
        }
    }

    /// <summary>CP MinHitDuration: with AB or extended AI an NPC starts an attack only that long after its last hit.</summary>
    public static class HitCooldown
    {
        public static bool CanAttack(double nowGameSeconds, double lastHitGameSeconds, bool arena, CombatRules rules, RuleOptions options)
        {
            if (arena || !(options.AlternativeBalancing || options.ExtendedAI)) return true;
            return nowGameSeconds >= lastHitGameSeconds + rules.MinHitDurationGameSeconds;
        }
    }
}
