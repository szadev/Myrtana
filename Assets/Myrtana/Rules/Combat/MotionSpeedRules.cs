using System;

namespace Myrtana.Rules
{
    /// <summary>The motion a speed factor is asked for (the G3 gEAction subset of combat_moves.json animationSpeedFactor).</summary>
    public enum CombatMotion
    {
        Other,
        Attack,
        PowerAttack,
        SprintAttack,
        QuickAttack,
        SimpleWhirl,
        WhirlAttack,
        PierceAttack,
        HackAttack,
        FinishingAttack,
        Parade,
        Stumble,
        ParadeStumble,
        QuickStumble,
        HeavyParadeStumble,
        PierceStumble,
        /// <summary>The crossbow reload.</summary>
        Cock,
        /// <summary>Down states, death, aiming and reloading: never sped up.</summary>
        Fixed,
    }

    /// <summary>Who plays a combat motion, for its speed factor.</summary>
    public struct MotionSpeedQuery
    {
        public CombatMotion Motion;
        /// <summary>The power attack's wind-up (Raise) phase, as opposed to its swing.</summary>
        public bool WindUp;
        public bool IsPlayer;
        public string Species;
        public WeaponClass RightHand;
        /// <summary>A one-handed weapon in each hand (G3 dual wielding).</summary>
        public bool DualOneHanded;
        /// <summary>The player's current stamina points (AB stamina penalty).</summary>
        public int Stamina;
        public bool Diseased;
        public bool Arena;
        public bool HasPerkCrossbow2, HasPerkCrossbow3;
    }

    /// <summary>
    /// Playback speed of combat motions (combat_moves.json animationSpeedFactor [data]): the hit timing is the motion's
    /// duration scaled by this factor. With AB on, the player uses the AB table minus a penalty for low stamina; NPCs
    /// use the main table and its modifiers (diseased, arena on Hard).
    /// </summary>
    public static class MotionSpeedRules
    {
        public static float Factor(in MotionSpeedQuery q, RuleOptions options)
        {
            if (q.Motion == CombatMotion.Fixed) return 1f;
            if (q.Motion == CombatMotion.Cock) return CockFactor(q);
            if (q.IsPlayer && options.AlternativeBalancing) return Math.Max(0.1f, AbPlayerBase(q, options) + StaminaPenalty(q));
            if (string.Equals(q.Species, "Troll", StringComparison.Ordinal)) return options.AlternativeBalancing || options.ExtendedAI ? 1f : 1.5f;
            float f = q.Motion == CombatMotion.SprintAttack ? 1.5f : TableFactor(q, options);
            if (!q.IsPlayer && q.Diseased) f *= 0.75f;
            if (q.Arena && options.Difficulty == Difficulty.Hard) f *= 1.25f;
            return f;
        }

        static float CockFactor(in MotionSpeedQuery q)
        {
            if (!q.IsPlayer) return RangedRules.NpcCockSpeed;
            return q.HasPerkCrossbow3 ? 2f : q.HasPerkCrossbow2 ? 1.5f : 1f;
        }

        static float AttackFactor(in MotionSpeedQuery q)
        {
            switch (q.RightHand)
            {
                case WeaponClass.OneHanded: return 0.6f; // left hand None, shield, torch or another 1H
                case WeaponClass.TwoHanded:
                case WeaponClass.Axe:
                case WeaponClass.Staff:
                case WeaponClass.Halberd: return 0.7f;
                default: return 1f;
            }
        }

        static float TableFactor(in MotionSpeedQuery q, RuleOptions options)
        {
            bool orc = string.Equals(q.Species, "Orc", StringComparison.Ordinal);
            switch (q.Motion)
            {
                case CombatMotion.Attack:
                    float a = AttackFactor(q);
                    return a == 1f && string.Equals(q.Species, "Zombie", StringComparison.Ordinal) ? 1.4f : a;
                case CombatMotion.PowerAttack:
                    if (q.WindUp) return orc ? 1f : 1.5f;
                    return orc ? 0.7f : q.DualOneHanded ? 0.9f : 1f;
                case CombatMotion.SimpleWhirl: return 1.3f;
                case CombatMotion.Parade: return q.IsPlayer && options.ExtendedAI ? 2.3f : 1.3f;
                case CombatMotion.Stumble: return 1.3f;
                case CombatMotion.ParadeStumble:
                case CombatMotion.QuickStumble: return 2f;
                case CombatMotion.HeavyParadeStumble: return q.DualOneHanded ? 0.7f : 1f;
                default: return 1f;
            }
        }

        static float AbPlayerBase(in MotionSpeedQuery q, RuleOptions options)
        {
            switch (q.Motion)
            {
                case CombatMotion.FinishingAttack: return 1f;
                case CombatMotion.Attack: return AttackFactor(q);
                case CombatMotion.PowerAttack: return q.WindUp ? 1.5f : q.DualOneHanded ? 0.9f : 1f;
                case CombatMotion.SimpleWhirl: return 1.3f;
                case CombatMotion.Parade: return options.ExtendedAI ? 2.3f : 1.3f;
                case CombatMotion.ParadeStumble:
                case CombatMotion.QuickStumble: return 2f;
                case CombatMotion.HeavyParadeStumble: return q.DualOneHanded ? 0.7f : 1f;
                case CombatMotion.Stumble: return 1.3f;
                default: return 1f; // quick, whirl, pierce, hack attacks, pierce stumble, others
            }
        }

        /// <summary>AB: the player's motions slow down when stamina points run low.</summary>
        static float StaminaPenalty(in MotionSpeedQuery q)
        {
            int sp = q.Stamina;
            switch (q.Motion)
            {
                case CombatMotion.Attack:
                case CombatMotion.Parade: return sp < 20 ? -0.1f : sp < 50 ? -0.05f : 0f;
                case CombatMotion.PowerAttack:
                case CombatMotion.HackAttack: return sp < 30 ? -0.1f : sp < 50 ? -0.05f : 0f;
                case CombatMotion.QuickAttack: return sp < 20 ? -0.3f : sp < 40 ? -0.15f : 0f;
                case CombatMotion.SimpleWhirl:
                case CombatMotion.PierceAttack: return sp < 20 ? -0.14f : sp < 45 ? -0.07f : 0f;
                case CombatMotion.WhirlAttack: return sp < 40 ? -0.14f : sp < 70 ? -0.07f : 0f;
                case CombatMotion.ParadeStumble:
                case CombatMotion.HeavyParadeStumble:
                case CombatMotion.Stumble:
                case CombatMotion.PierceStumble: return sp < 15 ? -0.1f : 0f;
                case CombatMotion.QuickStumble: return sp < 10 ? -0.2f : 0f;
                default: return 0f;
            }
        }

        /// <summary>The speed-table motion of a combat move.</summary>
        public static CombatMotion MotionOf(CombatAction action)
        {
            switch (action)
            {
                case CombatAction.Attack: return CombatMotion.Attack;
                case CombatAction.PowerAttack: return CombatMotion.PowerAttack;
                case CombatAction.SprintAttack: return CombatMotion.SprintAttack;
                case CombatAction.QuickAttack:
                case CombatAction.QuickAttackLeft:
                case CombatAction.QuickAttackRight: return CombatMotion.QuickAttack;
                case CombatAction.SimpleWhirl: return CombatMotion.SimpleWhirl;
                case CombatAction.WhirlAttack: return CombatMotion.WhirlAttack;
                case CombatAction.PierceAttack: return CombatMotion.PierceAttack;
                case CombatAction.HackAttack: return CombatMotion.HackAttack;
                case CombatAction.FinishingAttack: return CombatMotion.FinishingAttack;
                case CombatAction.Parade: return CombatMotion.Parade;
                // GetUpAttack/GetUpParade (gEAction 30/31) are not in G3's speed table (FUN_100042a0): 1.0.
                default: return CombatMotion.Other;
            }
        }

        /// <summary>The speed-table motion of a hit reaction.</summary>
        public static CombatMotion MotionOf(HitReaction reaction)
        {
            switch (reaction)
            {
                case HitReaction.Stumble: return CombatMotion.Stumble;
                case HitReaction.ParadeStumble: return CombatMotion.ParadeStumble;
                case HitReaction.QuickStumble: return CombatMotion.QuickStumble;
                case HitReaction.HeavyParadeStumble: return CombatMotion.HeavyParadeStumble;
                case HitReaction.PierceStumble: return CombatMotion.PierceStumble;
                case HitReaction.None:
                case HitReaction.Flinch: return CombatMotion.Other;
                default: return CombatMotion.Fixed;
            }
        }
    }
}
