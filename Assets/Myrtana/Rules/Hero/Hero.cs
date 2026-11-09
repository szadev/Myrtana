using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// The player character's rule state: attributes, experience, level, learning points and learned skills (perks
    /// and spells, by item id). NPC stats are derived from their level and are not kept here.
    /// </summary>
    public sealed class Hero
    {
        readonly HashSet<string> _skills = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>G3 skill stack ActivationCount: how many worn items activate each skill (ActivateSkill1/2).</summary>
        readonly Dictionary<string, int> _activations = new Dictionary<string, int>(StringComparer.Ordinal);

        public Hero(StatBlock stats) => Stats = stats;

        public StatBlock Stats { get; }
        public int Level { get; internal set; } = 1;
        /// <summary>Cumulative experience.</summary>
        public long Xp { get; internal set; }
        public int LearningPoints { get; internal set; }

        /// <summary>The learned skills (saved). Skills only activated by worn items are not in it: <see cref="ActiveSkills"/>.</summary>
        public IEnumerable<string> Skills => _skills;

        /// <summary>Learned skills plus the ones worn items activate now (G3 IsSkillActive for every skill).</summary>
        public IEnumerable<string> ActiveSkills => _skills.Concat(_activations.Keys.Where(k => !_skills.Contains(k)));

        /// <summary>
        /// G3 gCInventory_PS::IsSkillActive [Game 0x100b5f80]: the skill stack is learned (+0x54) or its ActivationCount
        /// (+0x58) is at least 1 (a worn item activates it, gCInventorySlot::OnApplyStats). Every gameplay perk check uses
        /// this, so a worn Orc Slayer counts as Perk_OrcSlayer while it is worn.
        /// </summary>
        public bool Knows(string skill) => !string.IsNullOrEmpty(skill) && (_skills.Contains(skill) || _activations.ContainsKey(skill));

        /// <summary>
        /// G3 HasLearnedSkill [Game 0x100b5fc0]: learned only, not activated by an item. Teachers (gCInfo_PS
        /// AreConditionsFulfilled), learn requirements (CanLearnSkill) and the item panel's HUD_Req_Skill colour use it.
        /// </summary>
        public bool HasLearned(string skill) => !string.IsNullOrEmpty(skill) && _skills.Contains(skill);

        /// <summary>True when a worn item (not learning) is what makes the skill active.</summary>
        public bool IsActivatedByItem(string skill) => !HasLearned(skill) && Knows(skill);

        /// <summary>
        /// Replaces the item activations (G3 ActivateSkill/DeactivateSkill on equip/unequip: ActivationCount ±1 per item);
        /// the equipment recomputes the whole set after every change.
        /// </summary>
        internal void SetActivations(IReadOnlyDictionary<string, int> counts)
        {
            bool same = counts.Count == _activations.Count &&
                        counts.All(kv => _activations.TryGetValue(kv.Key, out var n) && n == kv.Value);
            if (same) return;
            _activations.Clear();
            foreach (var kv in counts)
                if (kv.Value > 0) _activations[kv.Key] = kv.Value;
            NotifyChanged();
        }

        /// <summary>Raised after XP, level, learning points or skills change (attribute changes: <see cref="StatBlock.Changed"/>).</summary>
        public event Action Changed;

        internal void Learn(string skill)
        {
            if (_skills.Add(skill)) NotifyChanged();
        }

        internal void NotifyChanged() => Changed?.Invoke();

        /// <summary>A hero at the start of a new game.</summary>
        public static Hero CreateNew(IContentDatabase content, ProgressionRules rules, RuleOptions options)
        {
            var vitals = new HashSet<string>(content.All<AttributeDef>().Where(a => a.HasMaximum).Select(a => a.Id), StringComparer.Ordinal);
            var hero = new Hero(new StatBlock(vitals.Contains));
            foreach (var start in rules.HeroStart)
            {
                int value = start.Value.For(options);
                hero.Stats.Set(start.Attribute, new StatValue(value, vitals.Contains(start.Attribute) ? value : 0));
            }
            return hero;
        }
    }
}
