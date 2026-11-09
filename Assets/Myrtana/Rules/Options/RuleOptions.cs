namespace Myrtana.Rules
{
    public enum Difficulty { Easy, Medium, Hard }

    /// <summary>
    /// Game options that change rules (G3 CP 1.75 ge3.ini [Game], gameplay-data/core.json "options"). Defaults are the
    /// owner's decision (gameplay-architecture.md §14): the Steam CP preset with Alternative Balancing and the extended
    /// AI on, our passive-AI fix on, XP modifier 100 %, medium difficulty. Options live in the settings, not in saves.
    /// </summary>
    public sealed class RuleOptions
    {
        public RuleOptions(bool alternativeBalancing = true, bool extendedAI = true, bool fixPassiveExtendedAi = true,
                           int xpModifierPercent = 100, Difficulty difficulty = Difficulty.Medium)
        {
            AlternativeBalancing = alternativeBalancing;
            ExtendedAI = extendedAI;
            FixPassiveExtendedAi = fixPassiveExtendedAi;
            XpModifierPercent = xpModifierPercent < 1 ? 1 : xpModifierPercent > 200 ? 200 : xpModifierPercent;
            Difficulty = difficulty;
        }

        /// <summary>CP "ExtendedContent": the alternative values of every <see cref="Balanced{T}"/> rule.</summary>
        public bool AlternativeBalancing { get; }
        /// <summary>CP "AIMode".</summary>
        public bool ExtendedAI { get; }
        /// <summary>Our fix of "with extended AI, passive NPCs never attack or parry".</summary>
        public bool FixPassiveExtendedAi { get; }
        /// <summary>CP "XPModifier", 1..200; scales automatic kill XP only.</summary>
        public int XpModifierPercent { get; }
        public Difficulty Difficulty { get; }

        public static readonly RuleOptions Default = new RuleOptions();
    }
}
