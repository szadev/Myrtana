using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Story;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;

namespace Myrtana.Import.G3
{
    /// <summary>
    /// One G3 info (<c>gCInfo_PS</c>) → one <see cref="DialogEntryDef"/>. Everything G3-specific about dialogs is
    /// decided here, once: the entry kind from InfoType/ConditionType, the menu order, one-shot vs repeatable, the menu
    /// title, pre-checks, and the built-in effects G3 applies after an info (start the quest, end the dialog), which
    /// become ordinary commands at the end of the script.
    /// </summary>
    public sealed class InfoImporter
    {
        /// <summary>Fame an enclave-friend topic needs (gameplay-logic.md §9, IS_Running_NoEnclaveReputation).</summary>
        const int FriendFame = 75;
        /// <summary>G3's money item.</summary>
        public const string GoldItem = "It_Gold";
        const string NoEnclaveFameSvm = "NoFriendOfEnclave";
        /// <summary>IS_Running_NoPoliticalReputation: SVM NoFriendOf&lt;ORC|NRD|REB|MID|ASS|NOM&gt;, other alignments IDontTrustYou.</summary>
        const string NoPoliticalFameSvm = "NoFriendOf", NoTrustSvm = "IDontTrustYou";
        static readonly string[] PoliticalRefusalAlignments = { "Orc", "Nrd", "Reb", "Mid", "Ass", "Nom" };
        /// <summary>Shared (SVM) line names and GO_ messages of the refusals (dialog.json "preChecks").</summary>
        const string NotEnoughGoldSvm = "NotEnoughGold", NotEnoughLpSvm = "NotEnoughLP";
        /// <summary>IS_Running_NoLearnRequirements (Game 0x100d9600): GO_NoLearnRequirements and SVM NotReadyYet.</summary>
        const string NotReadyYetSvm = "NotReadyYet";
        /// <summary>IS_Running_NoParty: the partner's refusal when another companion is with the hero.</summary>
        const string JoinRefuseSvm = "JoinRefuse";
        static readonly TextId NotEnoughGoldMessage = new TextId("GO_NotEnoughGold"), NotEnoughLpMessage = new TextId("GO_NotEnoughLP"),
                               NoRequirementsMessage = new TextId("GO_NoLearnRequirements");

        readonly InfoConditionTranslator _conditions;
        readonly InfoCommandTranslator _commands;
        readonly StringsImporter _strings;
        readonly QuestImporter _quests;
        readonly VoiceLines _voices;
        readonly IReadOnlyDictionary<string, ActorId> _actors;
        readonly Func<ActorId, string> _voiceSetOf;
        readonly ImportReport _report;
        readonly Func<ActorId, string> _alignmentOf;

        public InfoImporter(StringsImporter strings, QuestImporter quests, VoiceLines voices,
                            IReadOnlyDictionary<string, ActorId> actorsByG3Name, Func<ActorId, string> voiceSetOf, ItemNames items,
                            ImportReport report, Func<ActorId, string> alignmentOf = null)
        {
            _alignmentOf = alignmentOf ?? (_ => null);
            _strings = strings;
            _quests = quests;
            _voices = voices;
            _actors = actorsByG3Name;
            _voiceSetOf = voiceSetOf;
            _report = report;
            _conditions = new InfoConditionTranslator(quests, items, report);
            _commands = new InfoCommandTranslator(strings, quests, voices, items, report);
        }

        public DialogEntryDef Translate(Info info, IReadOnlyDictionary<string, string> canonicalInfoNames)
        {
            var scope = new InfoScope(info, _actors, _report);
            var commands = new List<DialogCommand>(PaymentOf(info, scope));
            commands.AddRange(DeliveryOf(info, scope));
            TextId description = default;
            foreach (var line in info.Lines)
            {
                if (_commands.IsDescription(line))
                {
                    if (description.IsEmpty) description = _strings.Resolve(line.TextId);
                    continue;
                }
                var command = _commands.Translate(line, scope);
                if (command != null) commands.Add(command);
            }
            commands.AddRange(BuiltInEffects(info, scope));

            var kind = KindOf(info);
            return new DialogEntryDef(
                id: new Id<DialogEntryDef>(info.Name),
                owner: scope.Owner,
                parent: ParentOf(info, canonicalInfoNames),
                kind: kind,
                order: OrderOf(info, scope.OwnerIsPlayer),
                title: description.IsEmpty ? FirstPlayerLine(commands) : description,
                repeatable: IsRepeatable(info, scope.OwnerIsPlayer),
                needsAvailableChild: info.TypeEnum == InfoType.Parent,
                closesSiblings: info.ClearChildren,
                uniqueGroup: UniqueGroupOf(info),
                condition: _conditions.Translate(scope),
                preChecks: PreChecksOf(info, scope),
                commands: commands,
                logQuest: LogQuestOf(info));
        }

        DialogEntryKind KindOf(Info info)
        {
            switch (info.CondTypeEnum)
            {
                case InfoCondType.End: return DialogEntryKind.Exit;
                case InfoCondType.Back: return DialogEntryKind.Back;
                case InfoCondType.BackPrevLevel:
                    _report.Count("StepBack (one level up) imported as Back (to the top level)", info.Name);
                    return DialogEntryKind.Back;
                case InfoCondType.NothingToSay: return DialogEntryKind.Fallback;
            }
            switch (info.TypeEnum)
            {
                case InfoType.Refuse: return DialogEntryKind.Refuse;
                case InfoType.Important: return DialogEntryKind.Important;
                case InfoType.News: return DialogEntryKind.News;
                case InfoType.Comment: return DialogEntryKind.Comment;
                case InfoType.QuestRunning:
                    _report.Count("InfoType QuestRunning (auto-play every 2 game hours) imported as a hidden News entry", info.Name);
                    return DialogEntryKind.News;
                case InfoType.Info:
                case InfoType.Parent: return DialogEntryKind.Choice;
                default:
                    _report.Count("unknown InfoType (imported as a choice)", $"{info.Name}: {info.Type}");
                    return DialogEntryKind.Choice;
            }
        }

        /// <summary>G3 menu order: ConditionType (numeric), then NPC topics before the hero's, then SortID.</summary>
        static long OrderOf(Info info, bool ownerIsPlayer) =>
            ((long)info.ConditionType << 33) | (ownerIsPlayer ? 1L << 32 : 0L) | (uint)info.SortID;

        /// <summary>Infos are one-shot except Refuse/Parent, PartDelivery/End/Back, Permanent ones and the hero's own.</summary>
        static bool IsRepeatable(Info info, bool ownerIsPlayer) =>
            ownerIsPlayer || info.Permanent ||
            info.TypeEnum == InfoType.Refuse || info.TypeEnum == InfoType.Parent ||
            info.CondTypeEnum == InfoCondType.PartDelivery || info.CondTypeEnum == InfoCondType.End ||
            info.CondTypeEnum == InfoCondType.Back || info.CondTypeEnum == InfoCondType.BackPrevLevel;

        /// <summary>G3 lists only the first available End, Trade, MasterThief and NothingToSay info.</summary>
        static string UniqueGroupOf(Info info)
        {
            switch (info.CondTypeEnum)
            {
                case InfoCondType.End:
                case InfoCondType.Trade:
                case InfoCondType.MasterThief:
                case InfoCondType.NothingToSay:
                    return info.CondTypeEnum.ToString();
                default:
                    return null;
            }
        }

        Id<DialogEntryDef> ParentOf(Info info, IReadOnlyDictionary<string, string> canonicalInfoNames)
        {
            if (string.IsNullOrEmpty(info.Parent)) return default;
            if (canonicalInfoNames.TryGetValue(info.Parent, out var name)) return new Id<DialogEntryDef>(name);
            _report.Count("info with an unknown parent (made top-level)", $"{info.Name} → {info.Parent}");
            return default;
        }

        /// <summary>The menu shows what the hero is about to say when the info has no Description line.</summary>
        static TextId FirstPlayerLine(List<DialogCommand> commands) =>
            commands.OfType<Say>().Where(s => s.Speaker.Equals(ActorRef.Player)).Select(s => s.Text).FirstOrDefault();

        /// <summary>Quest-related infos write their lines into the quest's log (dialog.json "questLogCondTypes").</summary>
        Id<QuestDef> LogQuestOf(Info info)
        {
            switch (info.CondTypeEnum)
            {
                case InfoCondType.General:
                case InfoCondType.Overtime:
                case InfoCondType.Open:
                case InfoCondType.Activator:
                case InfoCondType.Running:
                case InfoCondType.Delivery:
                case InfoCondType.Success:
                case InfoCondType.DoCancel:
                case InfoCondType.Ready:
                    return _quests.Resolve(info.Quest);
                default:
                    return default;
            }
        }

        /// <summary>
        /// G3 gCInfo_PS::OnDelivery as the script starts: a Delivery or PartDelivery entry of a HasItems quest takes what
        /// the quest still lacks from the hero (the delivery entries carry no Give lines of their own).
        /// </summary>
        IEnumerable<DialogCommand> DeliveryOf(Info info, InfoScope scope)
        {
            var type = info.CondTypeEnum;
            if (type != InfoCondType.Delivery && type != InfoCondType.PartDelivery) yield break;
            var quest = _quests.Resolve(info.Quest);
            if (!quest.IsEmpty && _quests.KindOf(quest) == QuestObjectiveKind.HasItems)
                yield return new DeliverQuestItems(quest, scope.Npc);
        }

        /// <summary>Quest kinds whose Delivery entries count the entry's owner as delivered (G3 OnDelivery case Report/DriveAway).</summary>
        public static bool CountsOwnerOnDelivery(QuestObjectiveKind kind) =>
            kind == QuestObjectiveKind.Report || kind == QuestObjectiveKind.DriveAway;

        /// <summary>What G3 does after an info by its ConditionType/InfoType (gCInfo_PS::OnEndInfo), as commands.</summary>
        IEnumerable<DialogCommand> BuiltInEffects(Info info, InfoScope scope)
        {
            var type = info.CondTypeEnum;
            var quest = _quests.Resolve(info.Quest);
            if ((type == InfoCondType.Activator || type == InfoCondType.Reactivator) && !quest.IsEmpty)
                yield return new StartQuest(quest);
            // G3 gCInfo_PS::OnDelivery: a Delivery entry of a Report or DriveAway quest counts its owner (+1 on the matching
            // delivery entry, then CheckDeliveryEntitiesStatus) [Game 0x101c2620, case 1/4]. DriveAway: the target was
            // talked off peacefully (Trano BPANKRATZ3844 "go to work"), the other way being a kill (QuestDefeatCounter).
            if (type == InfoCondType.Delivery && CountsOwnerOnDelivery(_quests.KindOf(quest)))
                yield return new ReportToQuest(quest, scope.Npc);
            if (type == InfoCondType.Delivery && (_quests.KindOf(quest) == QuestObjectiveKind.BringNpc || _quests.KindOf(quest) == QuestObjectiveKind.FollowNpc))
                yield return new CompleteDelivery(quest);
            switch (type)
            {
                case InfoCondType.Join: yield return new JoinParty(scope.Npc, PartyMemberType.Party); break;
                case InfoCondType.MobJoin: yield return new JoinParty(scope.Npc, PartyMemberType.Mob); break;
                case InfoCondType.SlaveJoin: yield return new JoinParty(scope.Npc, PartyMemberType.Slave); break;
                case InfoCondType.Dismiss:
                case InfoCondType.MobDismiss: yield return new LeaveParty(scope.Npc, stopBringNpc: false); break;
                case InfoCondType.Wait: yield return new WaitParty(scope.Npc); break;
            }
            if (type == InfoCondType.DoCancel && !quest.IsEmpty)
                yield return new CloseQuest(quest);
            if (type == InfoCondType.Trade)
                yield return new OpenTrade();
            if (type == InfoCondType.Teach && _conditions.LessonOf(info) is Lesson lesson)
                yield return new TeachLesson(lesson, info.GoldCost);
            if (type == InfoCondType.FirstWarn || type == InfoCondType.SecondWarn)
                yield return new GateGuardWarned(scope.Npc, type == InfoCondType.SecondWarn);
            if (info.TypeEnum == InfoType.QuestRunning)
                yield return new RememberRun();
            if (type == InfoCondType.End || type == InfoCondType.NothingToSay || type == InfoCondType.Trade ||
                type == InfoCondType.MasterThief || info.TypeEnum == InfoType.Refuse)
                yield return new EndDialog();
        }

        /// <summary>Pre-checks in G3's order: teaching (requirements, learning points, gold), gold cost, enclave fame.</summary>
        IReadOnlyList<PreCheck> PreChecksOf(Info info, InfoScope scope)
        {
            var checks = new List<PreCheck>();
            var lesson = info.CondTypeEnum == InfoCondType.Teach ? _conditions.LessonOf(info) : null;
            if (lesson != null)
            {
                checks.Add(new LessonCheck(lesson, info.GoldCost, TeachResult.MissingRequirements, new DialogCommand[]
                {
                    // CanLearnSkill prints each missing skill/attribute first, then the IS_ state's message and line
                    new ReportLearnShortfalls(lesson), new ShowMessage(NoRequirementsMessage), new SayShared(scope.Npc, NotReadyYetSvm),
                }));
                checks.Add(new LessonCheck(lesson, info.GoldCost, TeachResult.NotEnoughLearningPoints, Refusal(scope, NotEnoughLpMessage, NotEnoughLpSvm)));
                checks.Add(new LessonCheck(lesson, info.GoldCost, TeachResult.NotEnoughGold, Refusal(scope, NotEnoughGoldMessage, NotEnoughGoldSvm)));
            }
            else if (info.GoldCost > 0)
                checks.Add(new MinGold(info.GoldCost, Refusal(scope, NotEnoughGoldMessage, NotEnoughGoldSvm)));
            if (info.CondTypeEnum == InfoCondType.EnclaveFriend)
                checks.Add(new MinEnclaveFame(scope.Npc, FriendFame, EnclaveRefusal(scope)));
            if (info.CondTypeEnum == InfoCondType.PoliticalFriend)
                checks.Add(new MinPoliticalFame(scope.Npc, FriendFame, PoliticalRefusal(scope)));
            if (info.CondTypeEnum == InfoCondType.Join)
                checks.Add(new PartyHasRoom(scope.Npc, new DialogCommand[] { new PartyRefusal(), new SayShared(scope.Npc, JoinRefuseSvm) }));
            return checks.Count == 0 ? null : checks;
        }

        /// <summary>A failed pre-check: the GO_ message and the partner's shared line (IS_Running_* states).</summary>
        static IReadOnlyList<DialogCommand> Refusal(InfoScope scope, TextId message, string sharedLine) =>
            new DialogCommand[] { new ShowMessage(message), new SayShared(scope.Npc, sharedLine) };

        /// <summary>A gold cost (outside teaching, which pays with the lesson) is paid to the partner first.</summary>
        static IEnumerable<DialogCommand> PaymentOf(Info info, InfoScope scope)
        {
            if (info.GoldCost > 0 && info.CondTypeEnum != InfoCondType.Teach)
                yield return new GiveItems(ActorRef.Player, scope.Npc, GoldItem, info.GoldCost);
        }

        /// <summary>The owner's line for too little political fame (by the owner's alignment).</summary>
        IReadOnlyList<DialogCommand> PoliticalRefusal(InfoScope scope)
        {
            string alignment = scope.OwnerIsPlayer ? null : _alignmentOf(scope.Owner);
            string line = Array.Exists(PoliticalRefusalAlignments, a => a == alignment) ? NoPoliticalFameSvm + alignment.ToUpperInvariant() : NoTrustSvm;
            return new DialogCommand[] { new SayShared(scope.Npc, line) };
        }

        /// <summary>The owner's ambient line "we don't trust you" (SVM_&lt;voice set&gt;_NoFriendOfEnclave).</summary>
        IReadOnlyList<DialogCommand> EnclaveRefusal(InfoScope scope)
        {
            string voiceSet = scope.OwnerIsPlayer ? null : _voiceSetOf(scope.Owner);
            var text = voiceSet == null ? default : _strings.Resolve($"SVM_{voiceSet}_{NoEnclaveFameSvm}");
            if (text.IsEmpty)
            {
                _report.Count("enclave-fame refusal without a line (unknown voice set)", scope.Info.Name);
                return null;
            }
            return new DialogCommand[] { new Say(scope.Npc, text, _voices.Existing(text.Value)) };
        }
    }
}
