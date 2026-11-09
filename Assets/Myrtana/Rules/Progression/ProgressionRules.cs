using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// Numbers of the experience and level system (G3: gameplay-data/progression.json), one definition per content
    /// (id "progression"). The formulas are code (<see cref="ProgressionMath"/>); every constant they use is here.
    /// </summary>
    public sealed class ProgressionRules : IDefinition
    {
        public const string DefinitionId = "progression";

        [JsonConstructor]
        public ProgressionRules(string id, int xpCurveFactor, int maxLevelsPerAward, int learningPointsPerLevel,
                                string learningPerk, int learningPerkBonus, KillXpRules killXp,
                                IReadOnlyList<StartingAttribute> heroStart,
                                IReadOnlyDictionary<string, Balanced<IReadOnlyList<AttributeGain>>> attributeBoosts)
        {
            Id = id;
            XpCurveFactor = xpCurveFactor;
            MaxLevelsPerAward = maxLevelsPerAward;
            LearningPointsPerLevel = learningPointsPerLevel;
            LearningPerk = learningPerk;
            LearningPerkBonus = learningPerkBonus;
            KillXp = killXp;
            HeroStart = heroStart ?? Array.Empty<StartingAttribute>();
            AttributeBoosts = attributeBoosts ?? new Dictionary<string, Balanced<IReadOnlyList<AttributeGain>>>();
        }

        public string Id { get; }
        /// <summary>a in "XP needed for the next level = a·(L+1)·(L+2)" (cumulative XP).</summary>
        public int XpCurveFactor { get; }
        /// <summary>Most levels one XP award can raise.</summary>
        public int MaxLevelsPerAward { get; }
        public int LearningPointsPerLevel { get; }
        /// <summary>Perk that adds <see cref="LearningPerkBonus"/> learning points per level.</summary>
        public string LearningPerk { get; }
        public int LearningPerkBonus { get; }
        public KillXpRules KillXp { get; }
        /// <summary>The hero's attributes at the start of a new game (vitals start full).</summary>
        public IReadOnlyList<StartingAttribute> HeroStart { get; }
        /// <summary>Named attribute boosts of the dialog command "boost attributes" (G3 BoostAttribs STR|DEX|INT|XAR).</summary>
        public IReadOnlyDictionary<string, Balanced<IReadOnlyList<AttributeGain>>> AttributeBoosts { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>XP for defeating an NPC (progression.json "killXp").</summary>
    public sealed class KillXpRules
    {
        [JsonConstructor]
        public KillXpRules(int perLevel, int minimum, Balanced<int> ambientCreature, Balanced<int> livestock,
                           IReadOnlyList<string> livestockSpecies, int modifierRounding)
        {
            PerLevel = perLevel;
            Minimum = minimum;
            AmbientCreature = ambientCreature;
            Livestock = livestock;
            LivestockSpecies = livestockSpecies ?? Array.Empty<string>();
            ModifierRounding = modifierRounding;
        }

        /// <summary>XP = max(perLevel · victim level, minimum) …</summary>
        public int PerLevel { get; }
        public int Minimum { get; }
        /// <summary>… except for ambient creatures (a flat value) …</summary>
        public Balanced<int> AmbientCreature { get; }
        /// <summary>… and livestock (0 = no special value: the normal formula).</summary>
        public Balanced<int> Livestock { get; }
        public IReadOnlyList<string> LivestockSpecies { get; }
        /// <summary>The XP modifier option rounds kill XP to multiples of this.</summary>
        public int ModifierRounding { get; }
    }

    /// <summary>A start value; a vital starts with value = maximum.</summary>
    public sealed class StartingAttribute
    {
        [JsonConstructor]
        public StartingAttribute(string attribute, Balanced<int> value)
        {
            Attribute = attribute;
            Value = value;
        }

        public string Attribute { get; }
        public Balanced<int> Value { get; }
    }

    /// <summary>A permanent change of one attribute.</summary>
    public sealed class AttributeGain
    {
        [JsonConstructor]
        public AttributeGain(string attribute, ModifierOp op, int amount)
        {
            Attribute = attribute;
            Op = op;
            Amount = amount;
        }

        public string Attribute { get; }
        public ModifierOp Op { get; }
        public int Amount { get; }
    }
}
