using System;
using System.Linq;

namespace Myrtana.Rules
{
    /// <summary>What the XP rules need to know about a defeated NPC.</summary>
    public readonly struct DefeatedVictim
    {
        public DefeatedVictim(int level, bool ambientCreature, string species)
        {
            Level = level;
            AmbientCreature = ambientCreature;
            Species = species;
        }

        /// <summary>The victim's Level property (not the difficulty-scaled current level).</summary>
        public int Level { get; }
        /// <summary>Rats, chickens, birds … (G3 "ambient" creatures).</summary>
        public bool AmbientCreature { get; }
        public string Species { get; }
    }

    /// <summary>The XP formulas of gameplay-logic.md §8 over the numbers of <see cref="ProgressionRules"/>.</summary>
    public static class ProgressionMath
    {
        /// <summary>Cumulative XP at which a hero of <paramref name="level"/> reaches the next level: a·(L+1)·(L+2).</summary>
        public static long XpForNextLevel(ProgressionRules rules, int level) => (long)rules.XpCurveFactor * (level + 1) * (level + 2);

        /// <summary>Kill/defeat XP before the XP-modifier option (§8 "Kill XP").</summary>
        public static int BaseKillXp(KillXpRules rules, RuleOptions options, in DefeatedVictim victim)
        {
            if (victim.AmbientCreature) return rules.AmbientCreature.For(options);
            int livestock = rules.Livestock.For(options);
            if (livestock > 0 && rules.LivestockSpecies.Contains(victim.Species, StringComparer.OrdinalIgnoreCase)) return livestock;
            return Math.Max(rules.PerLevel * victim.Level, rules.Minimum);
        }

        /// <summary>
        /// CP's XP modifier: round(percent · base / 100) to multiples of <see cref="KillXpRules.ModifierRounding"/>
        /// (5·floor(percent·base/500 + 0.5) for rounding 5). At 100 % a multiple of 5 stays unchanged.
        /// </summary>
        public static int ApplyXpModifier(KillXpRules rules, int baseXp, int percent)
        {
            int step = Math.Max(1, rules.ModifierRounding);
            return step * (int)Math.Floor(percent * (double)baseXp / (100.0 * step) + 0.5);
        }

        public static int KillXp(KillXpRules rules, RuleOptions options, in DefeatedVictim victim) =>
            ApplyXpModifier(rules, BaseKillXp(rules, options, victim), options.XpModifierPercent);
    }
}
