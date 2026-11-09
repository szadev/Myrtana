using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// A learnable skill (G3 perk or spell): what it costs to learn and what it needs. Its name, icon and description
    /// are those of <see cref="Item"/>, the inventory item that stands for it.
    /// </summary>
    public sealed class SkillDef : IDefinition
    {
        [JsonConstructor]
        public SkillDef(string id, string item, int learningPoints, IReadOnlyList<AttributeRequirement> requirements,
                        string requiredSkill, string group)
        {
            Id = id;
            Item = item;
            LearningPoints = learningPoints;
            Requirements = requirements ?? Array.Empty<AttributeRequirement>();
            RequiredSkill = requiredSkill;
            Group = group;
        }

        public string Id { get; }
        public string Item { get; }
        public int LearningPoints { get; }
        public IReadOnlyList<AttributeRequirement> Requirements { get; }
        /// <summary>Skill that must be known first (null = none).</summary>
        public string RequiredSkill { get; }
        /// <summary>Character screen group ("STR", "DEX", "Magic" …).</summary>
        public string Group { get; }

        string IDefinition.Key => Id;
    }

    /// <summary>Lookup of skill ids in the content (G3 perk names are exact: two-handed swords need Perk_Axe_1, crossbows Perk_Crossbow_1).</summary>
    public static class SkillCatalog
    {
        /// <summary>True when <paramref name="id"/> is a skill of the content. Otherwise <paramref name="suggestion"/> is a skill
        /// that differs only in letter case, or null.</summary>
        public static bool Exists(IContentDatabase db, string id, out string suggestion)
        {
            suggestion = null;
            if (string.IsNullOrEmpty(id) || db == null) return false;
            if (db.TryGet(id, out SkillDef _)) return true;
            foreach (var skill in db.All<SkillDef>())
                if (string.Equals(skill.Id, id, StringComparison.OrdinalIgnoreCase)) { suggestion = skill.Id; break; }
            return false;
        }
    }

    /// <summary>Teaching rules (id "teaching", G3: progression.json "teaching").</summary>
    public sealed class TeachingRules : IDefinition
    {
        public const string DefinitionId = "teaching";

        [JsonConstructor]
        public TeachingRules(string id, IReadOnlyDictionary<string, int> pointsPerLearningPoint, Balanced<TeachingSurcharge> surcharge)
        {
            Id = id;
            PointsPerLearningPoint = pointsPerLearningPoint ?? new Dictionary<string, int>();
            Surcharge = surcharge;
        }

        public string Id { get; }
        /// <summary>Attribute points one learning point buys, by attribute (missing = 1).</summary>
        public IReadOnlyDictionary<string, int> PointsPerLearningPoint { get; }
        /// <summary>Extra cost of high attributes (null = none).</summary>
        public Balanced<TeachingSurcharge> Surcharge { get; }

        string IDefinition.Key => Id;

        public static readonly TeachingRules None = new TeachingRules(DefinitionId, null, default);
    }

    /// <summary>Lessons of <see cref="Attributes"/> cost more once the attribute reaches <see cref="Threshold"/>.</summary>
    public sealed class TeachingSurcharge
    {
        [JsonConstructor]
        public TeachingSurcharge(IReadOnlyList<string> attributes, int threshold, int learningPointMultiplier, int goldMultiplier)
        {
            Attributes = attributes ?? Array.Empty<string>();
            Threshold = threshold;
            LearningPointMultiplier = learningPointMultiplier;
            GoldMultiplier = goldMultiplier;
        }

        public IReadOnlyList<string> Attributes { get; }
        public int Threshold { get; }
        public int LearningPointMultiplier { get; }
        public int GoldMultiplier { get; }
    }
}
