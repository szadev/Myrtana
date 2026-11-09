using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>Published after the hero received experience.</summary>
    public readonly struct XpAwarded : IGameEvent
    {
        public XpAwarded(int amount, long total)
        {
            Amount = amount;
            Total = total;
        }

        public int Amount { get; }
        public long Total { get; }
    }

    /// <summary>Published for every level the hero gains.</summary>
    public readonly struct LevelReached : IGameEvent
    {
        public LevelReached(int level, int learningPointsGained)
        {
            Level = level;
            LearningPointsGained = learningPointsGained;
        }

        public int Level { get; }
        public int LearningPointsGained { get; }
    }

    /// <summary>
    /// Published when the hero's attribute was raised by a script-style gain (G3 <c>AddStrength</c> … <c>AddManaPointsMax</c>
    /// → <c>PSPlayerMemory::PrintAddValue</c>, Script 0x10011770): bookstands, orc boulders, BoostAttribs, teaching. The
    /// HUD prints "&lt;attribute long name&gt; + amount = value" (gEGameMessageType 15). <see cref="Value"/> is the value
    /// after the raise (the maximum of a vital).
    /// </summary>
    public readonly struct AttributeRaised : IGameEvent
    {
        public AttributeRaised(string attribute, int amount, int value)
        {
            Attribute = attribute;
            Amount = amount;
            Value = value;
        }

        public string Attribute { get; }
        public int Amount { get; }
        public int Value { get; }
    }

    /// <summary>
    /// Experience, levels and learning points of the hero (gameplay-logic.md §8, CP 1.75): XP is cumulative; one award
    /// raises at most <see cref="ProgressionRules.MaxLevelsPerAward"/> levels (surplus XP raises the next ones on the
    /// next award); each level gives learning points; HP and MP are not raised by levels.
    /// </summary>
    public sealed class ProgressionService
    {
        readonly Hero _hero;
        readonly ProgressionRules _rules;
        readonly RuleOptions _options;
        readonly IEventBus _events;

        public ProgressionService(Hero hero, ProgressionRules rules, RuleOptions options, IEventBus events)
        {
            _hero = hero;
            _rules = rules;
            _options = options;
            _events = events;
        }

        public Hero Hero => _hero;
        public ProgressionRules Rules => _rules;
        public RuleOptions Options => _options;

        public long XpForNextLevel => ProgressionMath.XpForNextLevel(_rules, _hero.Level);

        /// <summary>Gives experience (already in final units: script/quest XP is scaled at import, kill XP by <see cref="KillXp"/>).</summary>
        public void AwardXp(int amount)
        {
            if (amount <= 0) return;
            _hero.Xp += amount;
            _events.Publish(new XpAwarded(amount, _hero.Xp));
            if (!HoldLevelUps)
                for (int i = 0; i < _rules.MaxLevelsPerAward && _hero.Xp >= XpForNextLevel; i++) GainLevel();
            _hero.NotifyChanged();
        }

        /// <summary>
        /// While true, experience is gathered without levelling up (G3: the transformed hero earns XP in the form's memory;
        /// TransformBack gives at most one level, <see cref="GainOneLevelIfDue"/>; Docs/combat.md §12.5).
        /// </summary>
        public bool HoldLevelUps { get; set; }

        /// <summary>One level up when the experience reached the next level (G3 TransformBack); false otherwise.</summary>
        public bool GainOneLevelIfDue()
        {
            if (_hero.Xp < XpForNextLevel) return false;
            GainLevel();
            _hero.NotifyChanged();
            return true;
        }

        public void AddLearningPoints(int amount)
        {
            _hero.LearningPoints += amount;
            _hero.NotifyChanged();
        }

        /// <summary>Spends learning points; false (and nothing spent) when the hero has too few.</summary>
        public bool TrySpendLearningPoints(int amount)
        {
            if (amount < 0 || _hero.LearningPoints < amount) return false;
            _hero.LearningPoints -= amount;
            _hero.NotifyChanged();
            return true;
        }

        /// <summary>A permanent attribute change (quest reward, potion, boost).</summary>
        public void Apply(AttributeGain gain) => _hero.Stats.Apply(gain.Attribute, gain.Op, gain.Amount);

        /// <summary>
        /// A permanent raise the HUD announces (G3 Add&lt;Attribute&gt; script functions, which always end in
        /// <c>PrintAddValue</c>): applies <paramref name="gain"/> and publishes <see cref="AttributeRaised"/>.
        /// </summary>
        public void Raise(AttributeGain gain)
        {
            if (gain == null) return;
            Apply(gain);
            if (gain.Amount == 0) return;
            var stats = _hero.Stats;
            int value = stats.HasMaximum(gain.Attribute) ? stats.Maximum(gain.Attribute) : stats.Value(gain.Attribute);
            _events.Publish(new AttributeRaised(gain.Attribute, gain.Amount, value));
        }

        /// <summary>
        /// A named boost from <see cref="ProgressionRules.AttributeBoosts"/> (info command BoostAttribs: AddStrength,
        /// AddSmithing, AddHitPointsMax …, each printed); false if it does not exist in this mode.
        /// </summary>
        public bool Boost(string name)
        {
            if (!_rules.AttributeBoosts.TryGetValue(name, out var boost)) return false;
            var gains = boost.For(_options);
            if (gains == null || gains.Count == 0) return false;
            foreach (var gain in gains) Raise(gain);
            return true;
        }

        public void LearnSkill(string skill) => _hero.Learn(skill);

        void GainLevel()
        {
            _hero.Level++;
            int points = _rules.LearningPointsPerLevel + (_hero.Knows(_rules.LearningPerk) ? _rules.LearningPerkBonus : 0);
            _hero.LearningPoints += points;
            _events.Publish(new LevelReached(_hero.Level, points));
        }
    }
}
