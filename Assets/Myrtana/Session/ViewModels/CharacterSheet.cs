using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;

namespace Myrtana.Session
{
    /// <summary>One labelled value of a screen ("Сила", "125").</summary>
    public readonly struct LabelledValue
    {
        public LabelledValue(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }

    /// <summary>
    /// What the character screen shows, already in the text language: level, experience, learning points, the
    /// attributes in content order (vitals as value/maximum) and the fame values. Views only lay it out.
    /// </summary>
    public sealed class CharacterSheet
    {
        static readonly TextId LevelText = new TextId("GO_Level"), XpText = new TextId("GO_XP"), NextText = new TextId("GO_XPNext"),
                               LearningText = new TextId("GO_LearningPoints"), AttributesText = new TextId("HL_Attributes"),
                               ExperienceText = new TextId("HL_Experience"), ReputationText = new TextId("HUD_Reputation"),
                               ProtectionText = new TextId("HL_Armor");

        CharacterSheet(string title, string experienceTitle, IReadOnlyList<LabelledValue> experience, string attributesTitle,
                       IReadOnlyList<LabelledValue> attributes, string reputationTitle, IReadOnlyList<LabelledValue> reputation,
                       IReadOnlyDictionary<string, LabelledValue> byAttribute, IReadOnlyDictionary<string, LabelledValue> byFameGroup,
                       string protectionTitle)
        {
            Title = title;
            ExperienceTitle = experienceTitle;
            Experience = experience;
            AttributesTitle = attributesTitle;
            Attributes = attributes;
            ReputationTitle = reputationTitle;
            Reputation = reputation;
            ByAttribute = byAttribute;
            ByFameGroup = byFameGroup;
            ProtectionTitle = protectionTitle;
        }

        public string Title { get; }
        public string ExperienceTitle { get; }
        public IReadOnlyList<LabelledValue> Experience { get; }
        public string AttributesTitle { get; }
        public IReadOnlyList<LabelledValue> Attributes { get; }
        public string ReputationTitle { get; }
        public IReadOnlyList<LabelledValue> Reputation { get; }
        /// <summary>Header of the armour protection block (HL_Armor).</summary>
        public string ProtectionTitle { get; }
        /// <summary>Name and value of each attribute by id ("STR", "HP", "PROT_BLADE" …), for fixed layouts.</summary>
        public IReadOnlyDictionary<string, LabelledValue> ByAttribute { get; }
        /// <summary>Name and value of each fame group by id ("Orc", "Reb", "Faring" …).</summary>
        public IReadOnlyDictionary<string, LabelledValue> ByFameGroup { get; }

        /// <summary>The experience rows in order: level, experience, next level, learning points.</summary>
        public LabelledValue Level => Experience[0];
        public LabelledValue Xp => Experience[1];
        public LabelledValue NextLevel => Experience[2];
        public LabelledValue LearningPoints => Experience[3];

        public static CharacterSheet Of(GameSession session)
        {
            var text = session.Localizer;
            var hero = session.Hero;
            var experience = new[]
            {
                new LabelledValue(text.Text(LevelText), hero.Level.ToString()),
                new LabelledValue(text.Text(XpText), hero.Xp.ToString()),
                new LabelledValue(text.Text(NextText), session.Progression.XpForNextLevel.ToString()),
                new LabelledValue(text.Text(LearningText), hero.LearningPoints.ToString()),
            };
            var attributeDefs = session.Content.Database.All<AttributeDef>().OrderBy(a => a.Order).ToList();
            var byAttribute = attributeDefs.ToDictionary(a => a.Id, a => new LabelledValue(text.Text(a.Name), Format(hero.Stats, a)));
            var attributes = attributeDefs.Select(a => byAttribute[a.Id]).ToList();
            var fame = session.Fame.All(FameKind.Political).Concat(session.Fame.All(FameKind.Enclave)).ToList();
            var byFameGroup = new Dictionary<string, LabelledValue>();
            foreach (var def in session.Content.Database.All<FameGroupDef>())
                byFameGroup[def.Id] = new LabelledValue(GroupName(session, def.Id), session.Fame.Of(def.Kind, def.Id).ToString());
            foreach (var kv in fame) byFameGroup[kv.Key] = new LabelledValue(GroupName(session, kv.Key), kv.Value.ToString());
            var reputation = fame.Select(kv => byFameGroup[kv.Key]).ToList();
            string name = session.Content.Database.TryGet(ActorId.Player.Name, out Simulation.NpcDef heroDef) ? text.Text(heroDef.DisplayName) : string.Empty;
            return new CharacterSheet(name, text.Text(ExperienceText), experience, text.Text(AttributesText), attributes,
                                      text.Text(ReputationText), reputation, byAttribute, byFameGroup, text.Text(ProtectionText));
        }

        /// <summary>The group's name; an enclave no quest names (Ardea, Gotha: their fame comes from a liberation, fix-144) by
        /// its GO_Enclave_ string.</summary>
        static string GroupName(GameSession session, string group)
        {
            if (session.Content.Database.TryGet(group, out FameGroupDef def)) return session.Localizer.Text(def.Name);
            return session.Localizer.TryText(new TextId("GO_Enclave_" + group), out var name) ? name : group;
        }

        static string Format(StatBlock stats, AttributeDef a) =>
            a.HasMaximum ? $"{stats.Value(a.Id)} / {stats.Maximum(a.Id)}" : stats.Value(a.Id).ToString();
    }
}
