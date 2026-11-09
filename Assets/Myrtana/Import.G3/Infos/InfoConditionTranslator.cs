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
    /// G3 availability rules of an info as Community Patch 1.75 evaluates them (gEInfoCondType plus the Cond* data
    /// fields; gameplay-logic.md §9, Docs/gameplay-data/dialog.json) → one generic condition tree. Rules of later slices
    /// become <see cref="Unsupported"/> nodes, which hide the entry.
    /// </summary>
    public sealed class InfoConditionTranslator
    {
        /// <summary>gEInfoNPCStatus values used by CondSecondaryNPCstates.</summary>
        const string TalkedToPlayerStatus = "4", NotTalkedToPlayerStatus = "5";

        static readonly QuestStatus[] OpenOrRunning = { QuestStatus.Open, QuestStatus.Running };
        /// <summary>CP 1.75 data keys of later slices (items worn, skills, party) that make an info unavailable until supported.</summary>
        static readonly string[] LaterDataKeys = { "CondPlayerSkills", "CondWearsItem", "CondHasSkill", "CondReput", "CondPAL" };

        /// <summary>dialog.json "distances_m": CondOwnerNearEntity, FollowNpc and BringNpc (CP) delivery tests.</summary>
        public const float OwnerNearMetres = 5f, FollowNpcDeliveryMetres = 5f, BringNpcDeliveryMetres = 20f;
        /// <summary>
        /// CondOwnerNearEntity naming an NPC (or the hero): G3 gCInfo_PS::GetDistOwnerToEntity multiplies the distance to an
        /// entity with gCNPC_PS by 0.25, so the 5 m test passes up to 20 m [both builds]. fix-100: Javier's
        /// BPANKRATZ31233 (Kliff near) was missed with Kliff 6.6 m away behind the hero.
        /// </summary>
        public const float OwnerNearNpcMetres = OwnerNearMetres / 0.25f;

        /// <summary>CP: MobJoin needs this quest Running when it exists.</summary>
        const string MobJoinQuest = "Reto_FreeMine";

        readonly QuestImporter _quests;
        readonly ImportReport _report;

        readonly ItemNames _items;

        public InfoConditionTranslator(QuestImporter quests, ItemNames items, ImportReport report)
        {
            _quests = quests;
            _items = items;
            _report = report;
        }

        /// <summary>Null when the entry is always available.</summary>
        public Condition Translate(InfoScope scope)
        {
            var info = scope.Info;
            if (!string.IsNullOrEmpty(info.Quest) && _quests.Resolve(info.Quest).IsEmpty)
            {
                // G3 gCInfo_PS::AreConditionsFulfilled: "Quest with specified name doesn't exist" → never available.
                _report.Count("info refers to a quest that does not exist (never available, as in G3)", $"{info.Name} → {info.Quest}");
                return new Never();
            }
            var parts = new List<Condition>();
            AddIfAny(parts, ForConditionType(scope));
            parts.AddRange(info.CondPlayerKnows.Where(f => f.Length > 0).Select(f => (Condition)new FlagSet(f)));
            parts.AddRange(info.CondPlayerKnowsNot.Where(f => f.Length > 0).Select(f => (Condition)new Not(new FlagSet(f))));
            if (info.TypeEnum == InfoType.QuestRunning) parts.Add(QuestRepeat(info));
            if (info.CondItems.Length > 0) parts.Add(ItemCondition(scope));
            if (!string.IsNullOrEmpty(info.CondOwnerNearEntity))
            {
                string near = info.CondOwnerNearEntity.Trim();
                parts.Add(new Near(scope.OwnerRef, scope.EntityName(near), scope.IsNpcEntity(near) ? OwnerNearNpcMetres : OwnerNearMetres));
            }
            if (info.Raw.TryGetValue("CondPlayerPartyMember", out var members) && members.Length > 0)
                parts.Add(new InPlayerParty(members.Split(';').Select(m => m.Trim()).Where(m => m.Length > 0).Select(scope.Resolve).ToList()));
            foreach (var key in LaterDataKeys)
                if (info.Raw.TryGetValue(key, out var value) && value.Length > 0) parts.Add(NotYet(key, info));
            for (int i = 0; i < info.CondSecondaryNPC.Length; i++)
                parts.Add(SecondaryNpc(scope, info.CondSecondaryNPC[i], i < info.CondSecondaryNPCStates.Length ? info.CondSecondaryNPCStates[i] : ""));
            return parts.Count == 0 ? null : parts.Count == 1 ? parts[0] : new AllOf(parts);
        }

        Condition ForConditionType(InfoScope scope)
        {
            var info = scope.Info;
            switch (info.CondTypeEnum)
            {
                case InfoCondType.General:
                case InfoCondType.EnclaveFriend:   // fame is a pre-check, see InfoImporter
                case InfoCondType.PoliticalFriend:
                case InfoCondType.NothingToSay:
                case InfoCondType.End:
                case InfoCondType.Back:
                    return null;
                case InfoCondType.Hello:
                    return new Not(new TalkedToPlayer(scope.Npc));
                case InfoCondType.Open:
                case InfoCondType.Activator:
                    return QuestOpenAndUnlocked(info);
                case InfoCondType.Overtime:
                    var overdue = _quests.Resolve(info.Quest);
                    return overdue.IsEmpty ? UnknownQuest(info) : new QuestOverdue(overdue);
                case InfoCondType.Running:
                case InfoCondType.DoCancel:
                    return QuestIs(info, QuestStatus.Running);
                case InfoCondType.Delivery:
                    return Delivery(scope);
                case InfoCondType.PartDelivery:
                    return PartDelivery(scope);
                case InfoCondType.Trade:
                    return new HasRole(scope.Npc, ActorRole.Trade);
                case InfoCondType.Teach:
                    var lesson = LessonOf(info);
                    // CP 1.75: a Teach info needs a valid TeachAttrib or a TeachSkill the partner teaches; without
                    // either it is never offered (FILLER64 "master thief" for 5000 gold, Falk/Lukjan BPANKRATZ3238/3249).
                    return lesson == null ? (Condition)new Never() : new LessonOffered(lesson);
                case InfoCondType.Ready:
                    return QuestIs(info, OpenOrRunning);
                case InfoCondType.Success:
                    return QuestIs(info, QuestStatus.Success);
                case InfoCondType.Failed:
                    return QuestIs(info, QuestStatus.Failed);
                case InfoCondType.Cancelled:
                    return QuestIs(info, QuestStatus.Cancelled);
                case InfoCondType.Won:
                    return QuestIs(info, QuestStatus.Won);
                case InfoCondType.Lost:
                case InfoCondType.Reactivator:
                    return QuestIs(info, QuestStatus.Lost);
                case InfoCondType.Fight:
                case InfoCondType.LongTimeNoSee:
                case InfoCondType.MasterThief:
                    return new Never(); // never available in CP 1.75
                case InfoCondType.EnclaveCrime:
                    return new HasGuardNews();
                case InfoCondType.PoliticalCrime:
                    return new HasBossNews();
                case InfoCondType.Join:
                    return new PartyAllows(scope.Npc, PartyOffer.Join);
                case InfoCondType.MobJoin:
                    var mob = new PartyAllows(scope.Npc, PartyOffer.MobJoin);
                    var freeMine = _quests.Resolve(MobJoinQuest);
                    return freeMine.IsEmpty ? (Condition)mob : new AllOf(new Condition[] { mob, new QuestStatusIs(freeMine, new[] { QuestStatus.Running }) });
                case InfoCondType.SlaveJoin:
                    return new PartyAllows(scope.Npc, PartyOffer.SlaveJoin);
                case InfoCondType.Dismiss:
                    return new PartyAllows(scope.Npc, PartyOffer.Dismiss);
                case InfoCondType.MobDismiss:
                    return new PartyAllows(scope.Npc, PartyOffer.MobDismiss);
                case InfoCondType.Wait:
                    return new PartyAllows(scope.Npc, PartyOffer.Wait);
                case InfoCondType.Companion:
                    return new PartyAllows(scope.Npc, PartyOffer.Companion);
                case InfoCondType.FirstWarn:
                    return new GateGuardMayWarn(scope.Npc, second: false);
                case InfoCondType.SecondWarn:
                    return new GateGuardMayWarn(scope.Npc, second: true);
                case InfoCondType.PickPocket:
                    return new MayPickpocket(scope.Npc);
                case InfoCondType.Heal:
                    return new CanHeal(scope.Npc);
                case InfoCondType.Animal:
                    // CP: CanAttack(partner) is false and its last conversation ended 48 game hours ago (or never).
                    return new AllOf(new Condition[] { new Not(new CanFight(scope.Npc)), new DialogEndedHoursAgo(scope.Npc, AnimalRepeatHours) });
                default:
                    return NotYet("G3 condition type " + info.CondTypeEnum, info);
            }
        }

        /// <summary>CP condition type Animal: hours between two uses of an animal (milking a cow).</summary>
        public const double AnimalRepeatHours = 48;
        /// <summary>CP info type QuestRunning: hours between two runs.</summary>
        public const double QuestRunningRepeatHours = 2;

        /// <summary>CP info type QuestRunning: the quest Running and 2 game hours since the entry last ran (or the quest started).</summary>
        Condition QuestRepeat(Info info)
        {
            var quest = _quests.Resolve(info.Quest);
            return quest.IsEmpty ? UnknownQuest(info) : new QuestRepeatDue(new Id<DialogEntryDef>(info.Name), quest, QuestRunningRepeatHours);
        }

        Condition QuestOpenAndUnlocked(Info info)
        {
            var quest = _quests.Resolve(info.Quest);
            if (quest.IsEmpty) return UnknownQuest(info);
            var parts = new List<Condition> { new QuestStatusIs(quest, new[] { QuestStatus.Open }) };
            parts.AddRange(_quests.PrerequisitesOf(quest).Select(p => (Condition)new QuestFinished(p)));
            return parts.Count == 1 ? parts[0] : new AllOf(parts);
        }

        Condition QuestIs(Info info, params QuestStatus[] statuses)
        {
            var quest = _quests.Resolve(info.Quest);
            return quest.IsEmpty ? UnknownQuest(info) : new QuestStatusIs(quest, statuses);
        }

        /// <summary>Delivery: quest Running and the per-type delivery test (dialog.json condTypes "Delivery").</summary>
        Condition Delivery(InfoScope scope)
        {
            var info = scope.Info;
            var quest = _quests.Resolve(info.Quest);
            if (quest.IsEmpty) return UnknownQuest(info);
            var running = new QuestStatusIs(quest, new[] { QuestStatus.Running });
            var kind = _quests.KindOf(quest);
            switch (kind)
            {
                case QuestObjectiveKind.Report:
                    return running; // reporting is the delivery itself
                case QuestObjectiveKind.HasItems:
                    if (!DeliveryItemsKnown(quest)) return new Never();
                    return new AllOf(new Condition[] { running, AtDestination(scope, quest), new CarriesQuestItems(quest, QuestItemShare.All) });
                case QuestObjectiveKind.BringNpc:
                case QuestObjectiveKind.FollowNpc:
                    return new AllOf(new[] { (Condition)running }.Concat(NpcsDelivered(scope, quest, kind)).ToList());
                case QuestObjectiveKind.Defeat:
                    return new AllOf(new Condition[] { running, NotYet("delivery test of a " + kind + " quest", info) });
                default:
                    // G3 AreConditionsFulfilled: the other quest types (Kill, DriveAway, Arena …) only need the quest Running.
                    return running;
            }
        }

        /// <summary>
        /// The delivery test of a BringNpc quest (CP: every delivery NPC led by the player and within 20 m of the
        /// destination) or a FollowNpc quest (every delivery entity within 5 m of the destination).
        /// </summary>
        IEnumerable<Condition> NpcsDelivered(InfoScope scope, Id<QuestDef> quest, QuestObjectiveKind kind)
        {
            var source = _quests.Source(quest);
            string destination = scope.EntityName(source.DestinationEntity);
            bool bring = kind == QuestObjectiveKind.BringNpc;
            foreach (var entity in source.DeliveryEntities.Where(e => !string.IsNullOrEmpty(e)))
            {
                var npc = scope.Resolve(entity);
                if (bring) yield return new InPlayerParty(new[] { npc });
                yield return new Near(npc, destination, bring ? BringNpcDeliveryMetres : FollowNpcDeliveryMetres);
            }
        }

        /// <summary>
        /// PartDelivery: a HasItems quest Running, the hero carries some of what is still missing but not all of it (G3
        /// AreConditionsFulfilled case 9: HasNPCpartOfRequiredItems and not HaveNPCsAllRequiredItems, both net of the
        /// quest's delivery counters).
        /// </summary>
        Condition PartDelivery(InfoScope scope)
        {
            var info = scope.Info;
            var quest = _quests.Resolve(info.Quest);
            if (quest.IsEmpty) return UnknownQuest(info);
            if (_quests.KindOf(quest) != QuestObjectiveKind.HasItems) return NotYet("part delivery of a non-item quest", info);
            if (!DeliveryItemsKnown(quest)) return new Never();
            return new AllOf(new Condition[]
            {
                new QuestStatusIs(quest, new[] { QuestStatus.Running }), AtDestination(scope, quest),
                new CarriesQuestItems(quest, QuestItemShare.Some), new Not(new CarriesQuestItems(quest, QuestItemShare.All)),
            });
        }

        /// <summary>Every delivery item of the quest is a known item (else the delivery can never be tested).</summary>
        bool DeliveryItemsKnown(Id<QuestDef> quest)
        {
            var source = _quests.Source(quest);
            foreach (var entity in source.DeliveryEntities)
                if (_items.Item(entity) == null)
                {
                    _report.Count("delivery of an unknown item", quest.Name + ": " + entity);
                    return false;
                }
            return true;
        }

        /// <summary>Only the quest's destination takes the delivery (the hero's own topics check the partner at runtime).</summary>
        Condition AtDestination(InfoScope scope, Id<QuestDef> quest)
        {
            var destination = _quests.Source(quest).DestinationEntity;
            if (string.IsNullOrEmpty(destination)) return new AllOf(null);
            var actor = scope.Resolve(destination);
            if (scope.OwnerIsPlayer) return new PartnerIs(actor.Resolve(default));
            return actor.Equals(scope.Npc) ? new AllOf(null) : (Condition)new Never();
        }

        /// <summary>
        /// CondItems: the container (an actor) holds at least the amount of each item; amount 0 = none at all. The container
        /// may be a ';' list, one entry per item like the amounts ("PC_Hero;PC_Hero" for Xardas' and Adanos' gate's staff +
        /// scepter); the last entry covers the remaining items.
        /// </summary>
        Condition ItemCondition(InfoScope scope)
        {
            var info = scope.Info;
            var containers = (info.CondItemContainer ?? "").Split(';');
            var parts = new List<Condition>();
            for (int i = 0; i < info.CondItems.Length; i++)
            {
                string container = containers[Math.Min(i, containers.Length - 1)].Trim();
                var holder = scope.Resolve(container.Length == 0 ? "player" : container);
                if (info.CondItems[i].Length == 0) continue;
                string item = _items.Item(info.CondItems[i]);
                int amount = i < info.CondItemAmounts.Length && int.TryParse(info.CondItemAmounts[i], out int a) ? a : 1;
                if (item == null)
                {
                    // A template that does not exist (FILLER137: It_Abbas_Artefakt) is never held: CP's "amount 0 = must not
                    // have it" is always true, "at least n" never.
                    _report.Count("item condition with an unknown item (none held)", info.Name + ": " + info.CondItems[i]);
                    if (amount > 0) return new Never();
                    continue;
                }
                parts.Add(new HasItems(holder, item, amount));
            }
            return parts.Count == 1 ? parts[0] : new AllOf(parts);
        }

        /// <summary>The lesson of a Teach info (TeachSkill, or TeachAttrib with TeachAttribValue learning points); null if none.</summary>
        public Lesson LessonOf(Info info)
        {
            if (!string.IsNullOrEmpty(info.TeachSkill))
            {
                string skill = _items.Skill(info.TeachSkill);
                if (skill != null) return new Lesson(null, 0, skill);
                _report.Count("Teach info of an unknown skill", info.Name + ": " + info.TeachSkill);
                return null;
            }
            return string.IsNullOrEmpty(info.TeachAttrib) || info.TeachAttribValue <= 0 ? null : new Lesson(info.TeachAttrib, info.TeachAttribValue, null);
        }

        Condition SecondaryNpc(InfoScope scope, string npc, string status)
        {
            var actor = scope.Resolve(npc);
            if (status == TalkedToPlayerStatus) return new TalkedToPlayer(actor);
            if (status == NotTalkedToPlayerStatus) return new Not(new TalkedToPlayer(actor));
            if (int.TryParse(status, out int state) && state >= 0 && state <= (int)ActorCondition.Dead) return new ActorIs(actor, (ActorCondition)state);
            return NotYet("NPC status " + status, scope.Info);
        }

        /// <summary>A quest condition type without a quest name: G3 finds no quest and the info is never available.</summary>
        Condition UnknownQuest(Info info)
        {
            _report.Count("quest condition type without a quest (never available, as in G3)", $"{info.Name} → {info.Quest}");
            return new Never();
        }

        Condition NotYet(string feature, Info info)
        {
            _report.Count("condition not supported yet: " + feature, info.Name);
            return new Unsupported(feature);
        }

        static void AddIfAny(List<Condition> list, Condition c)
        {
            if (c != null) list.Add(c);
        }
    }
}
