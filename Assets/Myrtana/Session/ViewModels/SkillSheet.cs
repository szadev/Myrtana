using System.Collections.Generic;
using System.Linq;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>
    /// The skills half of the character page, already in the text language: every skill (perk) of the content by its
    /// G3 group ("Skill0" combat … "Skill6" misc), learned or not, with name, description and requirements. Hidden perks
    /// (no perk item) are left out.
    /// </summary>
    public sealed class SkillSheet
    {
        public sealed class Skill
        {
            public Skill(string id, string name, string description, string icon, bool learned,
                         IReadOnlyList<IReadOnlyList<LabelledValue>> requirements)
            {
                Id = id;
                Name = name;
                Description = description;
                Icon = icon;
                Learned = learned;
                Requirements = requirements;
            }

            public string Id { get; }
            public string Name { get; }
            public string Description { get; }
            /// <summary>Icon of the skill's perk item (<see cref="ItemDef.Icon"/>).</summary>
            public string Icon { get; }
            /// <summary>
            /// Shown lit (not grey): learned, or active through a worn item (G3 list control: normal icon when the stack is
            /// learned or its ActivationCount &gt; 0 [V 100a2c90]; fix-93).
            /// </summary>
            public bool Learned { get; }
            /// <summary>
            /// Requirement table rows by section (<see cref="RequirementRows"/>): the attribute rows, then the required
            /// skill. Each section is followed by a 4 px gap.
            /// </summary>
            public IReadOnlyList<IReadOnlyList<LabelledValue>> Requirements { get; }
        }

        static readonly Core.TextId RequiredSkillText = new Core.TextId("HUD_Req_Skill"), LearningText = new Core.TextId("GO_LearningPoints");

        /// <summary>
        /// The requirement table of the skill box (G3 gCHUDTableSkillStats, CP 1.75 Game.dll 0x100a4f30): one row per
        /// attribute requirement from the last property slot to the first (ReqAttrib4 … ReqAttrib1), label
        /// <c>HUD_Req_&lt;TAG&gt;</c> and the minimum as "%d"; then, in its own section, <c>HUD_Req_Skill</c> with the name of
        /// the skill that must be known first (ReqSkill1, <c>FO_It_&lt;skill&gt;</c>). Empty sections are left out. G3 shows
        /// no learning-point cost here and colours no row by whether it is met. [port] A last section with the learning-point
        /// cost (<c>GO_LearningPoints</c>) follows when <paramref name="learningPoints"/> &gt; 0 (kept at the owner's request).
        /// </summary>
        /// <param name="requirements">Attribute requirements in property slot order (1 … 4).</param>
        /// <param name="attributeLabel">Requirement label of an attribute tag.</param>
        /// <param name="requiredSkillLabel">Label of the required-skill row (HUD_Req_Skill).</param>
        /// <param name="requiredSkillName">Name of the required skill; null or empty = none.</param>
        /// <param name="learningPointsLabel">[port] Label of the learning-point row (GO_LearningPoints).</param>
        /// <param name="learningPoints">[port] Learning-point cost; 0 = no row.</param>
        public static IReadOnlyList<IReadOnlyList<LabelledValue>> RequirementRows(IReadOnlyList<AttributeRequirement> requirements,
                                                                                 System.Func<string, string> attributeLabel,
                                                                                 string requiredSkillLabel, string requiredSkillName,
                                                                                 string learningPointsLabel = null, int learningPoints = 0)
        {
            var sections = new List<IReadOnlyList<LabelledValue>>();
            var attributes = (requirements ?? System.Array.Empty<AttributeRequirement>())
                .Where(r => !string.IsNullOrEmpty(r.Attribute) && r.Minimum > 0)
                .Reverse()
                .Select(r => new LabelledValue(attributeLabel(r.Attribute), r.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();
            if (attributes.Count > 0) sections.Add(attributes);
            if (!string.IsNullOrEmpty(requiredSkillName)) sections.Add(new[] { new LabelledValue(requiredSkillLabel, requiredSkillName) });
            // [port] not in G3's skill box: the cost, after G3's rows in the same table style.
            if (learningPoints > 0)
                sections.Add(new[] { new LabelledValue(learningPointsLabel, learningPoints.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
            return sections;
        }

        SkillSheet(IReadOnlyDictionary<string, IReadOnlyList<Skill>> groups) => Groups = groups;

        /// <summary>Skills by group id in content order; a group without skills is missing.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<Skill>> Groups { get; }

        public IReadOnlyList<Skill> Of(string group) => Groups.TryGetValue(group, out var list) ? list : System.Array.Empty<Skill>();

        public static SkillSheet Of(GameSession session)
        {
            var db = session.Content.Database;
            var text = session.Localizer;
            var groups = new Dictionary<string, List<Skill>>();
            foreach (var def in db.All<SkillDef>())
            {
                if (string.IsNullOrEmpty(def.Group)) continue;
                // G3's character menu shows a perk through its item template: a skill without one (Perk_1H1H_SecretBonus,
                // no It_Perk_*, texts "???") is a hidden perk. It stays in the rules (CombatStats), not on the page.
                if (string.IsNullOrEmpty(def.Item) || !db.TryGet(def.Item, out ItemDef item)) continue;
                string name = ItemLists.Name(session, item);
                string description = item.Description.IsEmpty ? string.Empty : ItemLists.ShownDescription(text.Text(item.Description));
                var requirements = RequirementRows(def.Requirements,
                    tag => db.TryGet(tag, out AttributeDef a) && !a.RequirementName.IsEmpty ? text.Text(a.RequirementName) : tag,
                    text.Text(RequiredSkillText), RequiredSkillName(session, def.RequiredSkill),
                    text.Text(LearningText), def.LearningPoints);
                if (!groups.TryGetValue(def.Group, out var list)) groups[def.Group] = list = new List<Skill>();
                list.Add(new Skill(def.Id, name, description, item.Icon, session.Hero.Knows(def.Id), requirements));
            }
            return new SkillSheet(groups.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Skill>)kv.Value));
        }

        /// <summary>G3 prints loc <c>FO_It_&lt;ReqSkill1 template&gt;</c>, the name of the skill's perk item.</summary>
        static string RequiredSkillName(GameSession session, string skill)
        {
            if (string.IsNullOrEmpty(skill)) return null;
            var db = session.Content.Database;
            if (db.TryGet(skill, out SkillDef required) && !string.IsNullOrEmpty(required.Item) && db.TryGet(required.Item, out ItemDef item))
                return ItemLists.Name(session, item);
            return session.Localizer.TryText(new Core.TextId("FO_It_" + skill), out var name) ? name : skill;
        }
    }
}
