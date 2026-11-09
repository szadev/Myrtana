using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Story;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using QuestStatus = Myrtana.Story.QuestStatus;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// G3 <c>.quest</c> files → <see cref="QuestDef"/>: identity, title, prerequisites, the objective (quest type,
    /// delivery entities with amounts, destination) and the success rewards. Also resolves quest names and types for
    /// the info importer.
    /// </summary>
    public sealed class QuestImporter
    {
        /// <summary>G3 multiplies script and quest XP by 5 when it awards them (progression.json "scriptXp").</summary>
        public const int ScriptXpMultiplier = 5;

        readonly Dictionary<string, Quest> _byName = new Dictionary<string, Quest>(StringComparer.OrdinalIgnoreCase);

        public QuestImporter(IEnumerable<Quest> quests)
        {
            foreach (var q in quests) _byName[q.Name] = q;
        }

        /// <summary>Canonical quest id for a (case-insensitive) G3 quest name; empty if unknown.</summary>
        public Id<QuestDef> Resolve(string name) =>
            !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var q) ? new Id<QuestDef>(q.Name) : default;

        public IReadOnlyList<Id<QuestDef>> PrerequisitesOf(Id<QuestDef> quest) =>
            _byName.TryGetValue(quest.Name, out var q) ? q.FinishedQuests.Select(Resolve).Where(id => !id.IsEmpty).ToList() : new List<Id<QuestDef>>();

        /// <summary>The objective kind of a known quest (None for unknown ones).</summary>
        /// <summary>The G3 quests as read from the install.</summary>
        public IEnumerable<Quest> Sources => _byName.Values;

        public QuestObjectiveKind KindOf(Id<QuestDef> quest) =>
            !quest.IsEmpty && _byName.TryGetValue(quest.Name, out var q) ? KindOf(q.TypeEnum) : QuestObjectiveKind.None;

        /// <summary>The G3 quest record of a known quest (null otherwise).</summary>
        public Quest Source(Id<QuestDef> quest) => !quest.IsEmpty && _byName.TryGetValue(quest.Name, out var q) ? q : null;

        public List<QuestDef> Import(StringsImporter strings, ImportReport report)
        {
            var defs = new List<QuestDef>();
            foreach (var q in _byName.Values.OrderBy(q => q.Name, StringComparer.Ordinal))
            {
                var title = strings.Resolve(q.LogTopic);
                if (title.IsEmpty) report.Count("quest title missing in the string table", q.Name);
                foreach (var pre in q.FinishedQuests.Where(p => Resolve(p).IsEmpty)) report.Count("unknown prerequisite quest", $"{q.Name} → {pre}");
                if (q.JobSuccess != 0) report.Count("quest JobSuccess reward (not imported)", q.Name);
                var id = new Id<QuestDef>(q.Name);
                defs.Add(new QuestDef(id, title, PrerequisitesOf(id), Objective(q, report), Rewards(q), TimeLimitHours(q), q.Folder));
            }
            return defs;
        }

        static QuestObjective Objective(Quest q, ImportReport report)
        {
            var kind = KindOf(q.TypeEnum);
            var targets = new List<QuestTarget>();
            for (int i = 0; i < q.DeliveryEntities.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(q.DeliveryEntities[i])) continue;
                int amount = i < q.DeliveryAmounts.Length ? q.DeliveryAmounts[i] : 1;
                string target = q.DeliveryEntities[i].Trim(); // "Zuben " (Ishtar_Test) would never match its reporter
                // EnterArea: the entity that must enter the area; the G3 hero entity is the player actor.
                if (kind == QuestObjectiveKind.EnterArea && string.Equals(target, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase))
                    target = ActorId.Player.Name;
                targets.Add(new QuestTarget(target, Math.Max(1, amount)));
            }
            if (kind == QuestObjectiveKind.None) report.Count("quest of an unknown type", $"{q.Name}: {q.Type}");
            // The G3 hero entity is the player actor here too: "find X" quests (HasItems to PC_Hero: the artefacts, temple
            // keys, fire mage cups) count what the hero receives (story-4).
            var destination = string.IsNullOrEmpty(q.DestinationEntity) ? default
                : string.Equals(q.DestinationEntity, NpcImporter.G3HeroName, StringComparison.OrdinalIgnoreCase) ? ActorId.Player
                : new ActorId(q.DestinationEntity);
            return new QuestObjective(kind, targets, destination);
        }

        static QuestRewards Rewards(Quest q)
        {
            var fame = new List<FameChange>();
            var political = (PoliticalAlignment)q.PoliticalSuccess;
            if (political != PoliticalAlignment.None && q.PoliticalSuccessAmount != 0)
                fame.Add(new FameChange(FameKind.Political, political.ToString(), q.PoliticalSuccessAmount));
            if (!string.IsNullOrEmpty(q.EnclaveSuccess) && q.EnclaveSuccessAmount != 0)
                fame.Add(new FameChange(FameKind.Enclave, q.EnclaveSuccess, q.EnclaveSuccessAmount));
            var attribute = string.IsNullOrEmpty(q.AttribSuccess) || q.AttribSuccessAmount == 0
                ? null
                : new AttributeGain(q.AttribSuccess, ModifierOp.AddToVal, q.AttribSuccessAmount);
            return new QuestRewards(fame, attribute, q.ExperiencePoints * ScriptXpMultiplier);
        }

        static QuestObjectiveKind KindOf(QuestType type)
        {
            switch (type)
            {
                case QuestType.HasItems: return QuestObjectiveKind.HasItems;
                case QuestType.Report: return QuestObjectiveKind.Report;
                case QuestType.Kill: return QuestObjectiveKind.Kill;
                case QuestType.Defeat: return QuestObjectiveKind.Defeat;
                case QuestType.DriveAway: return QuestObjectiveKind.DriveAway;
                case QuestType.Arena: return QuestObjectiveKind.Arena;
                case QuestType.BringNpc: return QuestObjectiveKind.BringNpc;
                case QuestType.FollowNpc: return QuestObjectiveKind.FollowNpc;
                case QuestType.EnterArea: return QuestObjectiveKind.EnterArea;
                case QuestType.FreeEnclave: return QuestObjectiveKind.FreeEnclave;
                case QuestType.Plunder: return QuestObjectiveKind.Plunder;
                case QuestType.Sparring: return QuestObjectiveKind.Sparring;
                case QuestType.SpellCast: return QuestObjectiveKind.SpellCast;
                default: return QuestObjectiveKind.None;
            }
        }

        /// <summary>G3 RunningTimeYears/Days/Hours in game hours (a G3 year has 365 days); 0 = no limit.</summary>
        static double TimeLimitHours(Quest q) => (q.RunningTimeYears * 365.0 + q.RunningTimeDays) * 24.0 + q.RunningTimeHours;
    }
}
