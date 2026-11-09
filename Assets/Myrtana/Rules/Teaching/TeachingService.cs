using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>What a teacher teaches in one lesson: <see cref="LearningPoints"/> worth of an attribute, or a skill.</summary>
    public sealed class Lesson
    {
        [JsonConstructor]
        public Lesson(string attribute, int learningPoints, string skill)
        {
            Attribute = attribute;
            LearningPoints = learningPoints;
            Skill = skill;
        }

        public string Attribute { get; }
        public int LearningPoints { get; }
        public string Skill { get; }

        [JsonIgnore] public bool IsSkill => !string.IsNullOrEmpty(Skill);
    }

    /// <summary>
    /// Published after a lesson was taught (G3 <c>gCInfo_PS::OnEndInfo</c> 0x101c1270): a skill prints "GO_Learned" + its
    /// name and the learning points left (gEGameMessageType 5); an attribute prints its raise (<see cref="AttributeRaised"/>)
    /// and the learning points left (type 15).
    /// </summary>
    public readonly struct LessonTaught : IGameEvent
    {
        public LessonTaught(string skill, string attribute, int learningPointsLeft)
        {
            Skill = skill;
            Attribute = attribute;
            LearningPointsLeft = learningPointsLeft;
        }

        /// <summary>The skill or spell learned; null for an attribute lesson.</summary>
        public string Skill { get; }
        public string Attribute { get; }
        public int LearningPointsLeft { get; }
    }

    public enum TeachResult { Done, NotTaught, AlreadyKnown, MissingRequirements, NotEnoughLearningPoints, NotEnoughGold }

    /// <summary>What a missing requirement is: a skill (perk) or spell to know first, or attribute points.</summary>
    public enum LearnShortfallKind { Skill, Spell, Attribute }

    /// <summary>
    /// One unmet requirement of a skill lesson, as G3 gCInventory_PS::CanLearnSkill (Game 0x100b5ff0) reports it:
    /// HUD_MissingSkill / HUD_MissingSpell "$(name)" = FO_It_&lt;id&gt;, or HUD_MissingAttrib with $(name) =
    /// ATTRIB_&lt;id&gt; and $(value) = the points still missing (required − current).
    /// </summary>
    public readonly struct LearnShortfall
    {
        [JsonConstructor]
        public LearnShortfall(LearnShortfallKind kind, string id, int missing)
        {
            Kind = kind;
            Id = id;
            Missing = missing;
        }

        public LearnShortfallKind Kind { get; }
        /// <summary>Skill id, or attribute tag ("STR").</summary>
        public string Id { get; }
        /// <summary>Attribute points still missing (0 for a skill).</summary>
        public int Missing { get; }

        public override string ToString() => Kind == LearnShortfallKind.Attribute ? $"{Id}-{Missing}" : $"{Kind}:{Id}";
    }

    /// <summary>
    /// Teaching (gameplay-logic.md §8): lessons cost learning points only (gold comes from the dialog's gold cost, which
    /// the Alternative Balancing surcharge can double). Attribute lessons add their points to value and maximum; skill
    /// lessons need the required skill and attributes. Checks run in G3's order: requirements, learning points, gold.
    /// </summary>
    public sealed class TeachingService
    {
        readonly TeachingRules _rules;
        readonly RuleOptions _options;
        readonly IContentDatabase _content;
        readonly ProgressionService _progression;
        readonly ActorRoles _roles;
        readonly Inventory _heroInventory;
        readonly string _goldItem;
        readonly IEventBus _events;

        public TeachingService(TeachingRules rules, RuleOptions options, IContentDatabase content, ProgressionService progression,
                               ActorRoles roles, Inventory heroInventory, string goldItem, IEventBus events = null)
        {
            _events = events;
            _rules = rules;
            _options = options;
            _content = content;
            _progression = progression;
            _roles = roles;
            _heroInventory = heroInventory;
            _goldItem = goldItem;
        }

        Hero Hero => _progression.Hero;

        /// <summary>The teacher teaches now, has the lesson in its list, and the hero does not know the skill yet.</summary>
        public bool Offers(ActorId teacher, Lesson lesson)
        {
            if (!_roles.Has(teacher, ActorRole.Teach) || !_content.TryGet(teacher.Name, out TeacherDef def)) return false;
            return lesson.IsSkill ? def.Skills.Contains(lesson.Skill) && !Hero.HasLearned(lesson.Skill) : def.Attributes.Contains(lesson.Attribute);
        }

        public int LearningPointCost(Lesson lesson)
        {
            if (lesson.IsSkill) return _content.TryGet(lesson.Skill, out SkillDef skill) ? skill.LearningPoints : 0;
            return lesson.LearningPoints * (Surcharged(lesson) ? Surcharge.LearningPointMultiplier : 1);
        }

        public int GoldCost(Lesson lesson, int baseGold) => baseGold * (Surcharged(lesson) ? Surcharge.GoldMultiplier : 1);

        public TeachResult Check(Lesson lesson, int baseGold)
        {
            if (lesson.IsSkill)
            {
                if (!_content.TryGet(lesson.Skill, out SkillDef skill)) return TeachResult.NotTaught;
                if (Hero.HasLearned(skill.Id)) return TeachResult.AlreadyKnown;
                if (Shortfalls(lesson).Count > 0) return TeachResult.MissingRequirements;
            }
            if (Hero.LearningPoints < LearningPointCost(lesson)) return TeachResult.NotEnoughLearningPoints;
            if (!_heroInventory.Has(_goldItem, GoldCost(lesson, baseGold))) return TeachResult.NotEnoughGold;
            return TeachResult.Done;
        }

        /// <summary>
        /// Every unmet requirement of a skill lesson, in G3's order (required skill, then the attribute pairs); empty for
        /// attribute lessons and for skills the hero can learn. G3 prints all of them before GO_NoLearnRequirements.
        /// </summary>
        public IReadOnlyList<LearnShortfall> Shortfalls(Lesson lesson)
        {
            var list = new List<LearnShortfall>();
            if (lesson == null || !lesson.IsSkill || !_content.TryGet(lesson.Skill, out SkillDef skill)) return list;
            if (!string.IsNullOrEmpty(skill.RequiredSkill) && !Hero.HasLearned(skill.RequiredSkill))
            {
                bool spell = _content.TryGet(skill.RequiredSkill, out SkillDef required) && required.Group == SpellGroup;
                list.Add(new LearnShortfall(spell ? LearnShortfallKind.Spell : LearnShortfallKind.Skill, skill.RequiredSkill, 0));
            }
            foreach (var r in skill.Requirements)
            {
                int have = Hero.Stats.Value(r.Attribute);
                if (have < r.Minimum) list.Add(new LearnShortfall(LearnShortfallKind.Attribute, r.Attribute, r.Minimum - have));
            }
            return list;
        }

        /// <summary>Character-screen group of spells (skills.json "Magic").</summary>
        public const string SpellGroup = "Magic";

        /// <summary>Spends the learning points and gives the lesson (gold is paid by the caller to the teacher).</summary>
        public TeachResult Teach(Lesson lesson)
        {
            var check = Check(lesson, 0);
            if (check != TeachResult.Done) return check;
            _progression.TrySpendLearningPoints(LearningPointCost(lesson));
            if (lesson.IsSkill) _progression.LearnSkill(lesson.Skill);
            else
            {
                int points = AttributeGain(lesson);
                var op = Hero.Stats.HasMaximum(lesson.Attribute) ? ModifierOp.AddToMax : ModifierOp.AddToVal;
                _progression.Raise(new AttributeGain(lesson.Attribute, op, points)); // "<attribute> + n = value"
            }
            _events?.Publish(new LessonTaught(lesson.IsSkill ? lesson.Skill : null, lesson.IsSkill ? null : lesson.Attribute, Hero.LearningPoints));
            return TeachResult.Done;
        }

        /// <summary>The points an attribute lesson adds (learning points × the attribute's rate: 4 for HP/MP/SP); 0 for a skill.</summary>
        public int AttributeGain(Lesson lesson) =>
            lesson == null || lesson.IsSkill ? 0 : lesson.LearningPoints * PointsPerLearningPoint(lesson.Attribute);

        int PointsPerLearningPoint(string attribute) =>
            _rules.PointsPerLearningPoint.TryGetValue(attribute, out int n) ? n : 1;

        TeachingSurcharge Surcharge => _rules.Surcharge.For(_options);

        bool Surcharged(Lesson lesson)
        {
            var s = Surcharge;
            return !lesson.IsSkill && s != null && s.Attributes.Contains(lesson.Attribute) && Hero.Stats.Value(lesson.Attribute) >= s.Threshold;
        }
    }
}
