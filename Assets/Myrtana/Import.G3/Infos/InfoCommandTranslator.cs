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
    /// G3 info script lines (49 command names, formats-story.md §2.3) → typed dialog commands. One small translation
    /// per supported command name; every other command is kept as <see cref="NotSupported"/> and counted in the report.
    /// <c>Description</c> is not a command here: it becomes the entry's menu title (see <see cref="InfoImporter"/>).
    /// </summary>
    public sealed class InfoCommandTranslator
    {
        public const string DescriptionCommand = "Description";

        delegate DialogCommand Translation(InfoLine line, InfoScope scope);

        readonly Dictionary<string, Translation> _translations = new Dictionary<string, Translation>(StringComparer.OrdinalIgnoreCase);
        readonly StringsImporter _strings;
        readonly QuestImporter _quests;
        readonly VoiceLines _voices;
        List<string> _partyWaitVoices;
        readonly ImportReport _report;

        readonly ItemNames _items;

        public InfoCommandTranslator(StringsImporter strings, QuestImporter quests, VoiceLines voices, ItemNames items, ImportReport report)
        {
            _items = items;
            _strings = strings;
            _quests = quests;
            _voices = voices;
            _report = report;
            _translations["Say"] = TranslateSay;
            _translations["SetGameEvent"] = (l, s) => new SetFlag(l.Id1);
            _translations["ClearGameEvent"] = (l, s) => new ClearFlag(l.Id1);
            _translations["RunQuest"] = TranslateRunQuest;
            _translations["End"] = (l, s) => new EndDialog();
            _translations["Back"] = (l, s) => new LeaveSubmenu();
            _translations["GiveXP"] = TranslateGiveXp;
            _translations["BoostAttribs"] = (l, s) => new BoostAttributes(l.Id1);
            _translations["SucceedQuest"] = (l, s) => QuestCommand(l, s, q => new SucceedQuest(q));
            _translations["SuccessQuest"] = _translations["SucceedQuest"];
            _translations["CloseQuest"] = (l, s) => QuestCommand(l, s, q => new CloseQuest(q));
            _translations["FailQuest"] = (l, s) => QuestCommand(l, s, q => new FailQuest(q));
            _translations["AddQuestLog"] = TranslateAddQuestLog;
            _translations["Give"] = TranslateGive;
            _translations["SetTradeEnabled"] = (l, s) => new SetRole(s.Resolve(l.Entity1), ActorRole.Trade, IsTrue(l.Id1));
            _translations["SetTeachEnabled"] = (l, s) => new SetRole(s.Resolve(l.Entity1), ActorRole.Teach, IsTrue(l.Id1));
            _translations["SayGuardNews"] = (l, s) => new SayGuardNews();
            _translations["SayBossNews"] = (l, s) => new SayBossNews();
            _translations["Bribe"] = (l, s) => new Bribe();
            _translations["Excuse"] = (l, s) => new Excuse();
            _translations["ResetCrime"] = (l, s) => new ResetCrime();
            _translations["SetChapter"] = (l, s) => int.TryParse(l.Id1, out int chapter) ? new SetChapter(chapter) : null;
            _translations["SaySVM"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new SayShared(s.Resolve(l.Entity1), l.Id1);
            _translations["SayKeepItUp"] = (l, s) => new SayShared(s.Resolve(l.Entity1), KeepItUpSvm, KeepItUpVariants);
            _translations["SayConfirm"] = (l, s) => new SayShared(s.Resolve(l.Entity1), ConfirmSvm, ConfirmVariants);
            // CP StartSayHoldingPosition: "PartyWait" for a voice with the NPCParty SVM block (not a slave), else "Sure".
            // The voices with a PartyWait recording stand for the block [interp]; the line has no text in the string table.
            _translations["SayHoldingPosition"] = (l, s) => new SayShared(s.Resolve(l.Entity1), "PartyWait", 0, "Sure",
                                                                          _partyWaitVoices ?? (_partyWaitVoices = _voices?.VoiceSetsWith("PartyWait")));
            _translations["ShowGameMessage"] = TranslateShowGameMessage;
            _translations["Attack"] = (l, s) => new StartFight(s.Resolve(l.Entity1), s.Resolve(l.Entity2), AttackReasonOf(l.Id1));
            _translations["SetRoutine"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new SetRoutine(s.Resolve(l.Entity1), l.Id1);
            // CP JoinNPC (63): Entity1 joins the party of Entity2 (unused by the CP 1.75 data; kept for mods).
            _translations["JoinNPC"] = (l, s) => new JoinNpc(s.Resolve(l.Entity1), s.Resolve(l.Entity2));
            _translations["SetPartyEnabled"] = (l, s) => new SetRole(s.Resolve(l.Entity1), ActorRole.Party, IsTrue(l.Id1));
            _translations["SetMobEnabled"] = (l, s) => new SetRole(s.Resolve(l.Entity1), ActorRole.Mob, IsTrue(l.Id1));
            _translations["SetSlaveryEnabled"] = (l, s) => new SetRole(s.Resolve(l.Entity1), ActorRole.Slavery, IsTrue(l.Id1));
            _translations["SetPAL"] = (l, s) => new SetAlignment(s.Resolve(l.Entity1), AlignmentOf(l.Id1));
            _translations["Heal"] = (l, s) => new HealActor(s.Resolve(string.IsNullOrEmpty(l.Entity2) ? "player" : l.Entity2));
            _translations["Sleep"] = (l, s) => int.TryParse(l.Id1, out int hour) ? new Sleep(hour) : null;
            _translations["Erase"] = TranslateErase;
            _translations["GiveAll"] = (l, s) => new GiveAllItems(s.Resolve(l.Entity1), s.Resolve(string.IsNullOrEmpty(l.Entity2) ? l.Id1 : l.Entity2));
            _translations["Use"] = TranslateUse;
            // CP JoinPlayer (E1, ID1): true = E1 joins the hero as a freed slave and follows him, false = StopBringNPC.
            _translations["JoinPlayer"] = (l, s) => IsTrue(l.Id1)
                ? (DialogCommand)new JoinParty(s.Resolve(l.Entity1), PartyMemberType.Slave)
                : new LeaveParty(s.Resolve(l.Entity1), stopBringNpc: true);
            // Cutscene / epilogue (story-2).
            _translations["ShowPicture"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new ShowPicture(l.Id1.Trim());
            _translations["ShowSubtitle"] = TranslateShowSubtitle;
            _translations["InfoWait"] = (l, s) => int.TryParse(l.Id1, out int ms) && ms > 0 ? new WaitInScript(ms) : null;
            _translations["PlayMusic"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new PlayMusic(l.Id1.Trim());
            _translations["EndGame"] = (l, s) => new EndGame();
            _translations["SaySound"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new SaySound(s.Resolve(string.IsNullOrEmpty(l.Entity1) ? "npc" : l.Entity1), l.Id1.Trim());
            _translations["StartEffect"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new StartEffect(s.Resolve(l.Entity1), l.Id1.Trim());
            // World state (story-2).
            _translations["SetSectorStatus"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new SetSectorStatus(l.Id1.Trim(), G3Bool(l.Id2));
            _translations["OpenDoor"] = (l, s) => string.IsNullOrEmpty(l.Entity1) ? null : new OpenDoor(s.EntityName(l.Entity1.Trim()));
            _translations["SetRevolutionEnabled"] = (l, s) => EnclaveFlagCommand(l, s, EnclaveFlag.Revolution);
            _translations["SetRaidEnabled"] = (l, s) => EnclaveFlagCommand(l, s, EnclaveFlag.Raid);
            _translations["StartRevolution"] = (l, s) => new StartRevolution(s.Resolve(l.Entity1));
            // NPC control (dialog-pres-2): FullStop (E1), CP Humanize (E1, ID1 = humanize: Immortal = !ID1), Wear (E1, ID1 item).
            _translations["FullStop"] = (l, s) => string.IsNullOrEmpty(l.Entity1) ? null : new FullStop(s.Resolve(l.Entity1));
            _translations["Humanize"] = (l, s) => string.IsNullOrEmpty(l.Entity1) ? null : new HumanizeActor(s.Resolve(l.Entity1), G3Bool(l.Id1));
            _translations["Wear"] = (l, s) => string.IsNullOrEmpty(l.Id1) ? null : new WearItem(s.Resolve(string.IsNullOrEmpty(l.Entity1) ? "npc" : l.Entity1), l.Id1.Trim());
            _translations["SetClass"] = (l, s) => new SetClass(s.Resolve(l.Entity1), (l.Id1 ?? "").Trim());
            // G3 Pickpocket(E1 = thief, E2 = victim); the thief is always the hero in CP's infos.
            _translations["Pickpocket"] = (l, s) => new Pickpocket(s.Resolve(string.IsNullOrEmpty(l.Entity2) ? "npc" : l.Entity2));
            _translations["SetGuardStatus"] = (l, s) => new SetGuardStatus(s.Resolve(l.Entity1),
                                                                        string.Equals((l.Id1 ?? "").Trim(), "Active", StringComparison.Ordinal));
        }

        /// <summary>G3 info booleans: true when the value is a non-zero integer or contains "rue" (True/true).</summary>
        static bool G3Bool(string value) =>
            !string.IsNullOrEmpty(value) && (int.TryParse(value.Trim(), out int n) ? n != 0 : value.IndexOf("rue", StringComparison.Ordinal) >= 0);

        /// <summary>The entity is the enclave itself ("Silden") or, for "npc", the partner (resolved at runtime).</summary>
        static DialogCommand EnclaveFlagCommand(InfoLine line, InfoScope scope, EnclaveFlag flag)
        {
            return new SetEnclaveFlag(scope.Resolve(line.Entity1?.Trim()).Value, flag, G3Bool(line.Id1));
        }

        DialogCommand TranslateShowSubtitle(InfoLine line, InfoScope scope)
        {
            var text = _strings.Resolve(line.TextId);
            if (!text.IsEmpty) return new ShowSubtitle(text);
            _report.Count("ShowSubtitle with a text missing from the string table", $"{scope.Info.Name}: {line.TextId}");
            return null;
        }

        /// <summary>Shared lines of the canned reactions and their numbered variants (KeepItUp_00 … _04, Confirm_00 … _02).</summary>
        const string KeepItUpSvm = "KeepItUp", ConfirmSvm = "Confirm";
        const int KeepItUpVariants = 5, ConfirmVariants = 3;

        /// <summary>G3 Attack ID1: Duel, Enemy, ReactToWeapon, Guard, Revolution; anything else (or nothing) means kill.</summary>
        static AttackReason AttackReasonOf(string id)
        {
            switch ((id ?? "").Trim().ToLowerInvariant())
            {
                case "duel": return AttackReason.Duel;
                case "enemy": return AttackReason.Enemy;
                case "reacttoweapon": return AttackReason.ReactToWeapon;
                case "guard": return AttackReason.GateGuard; // G3 GetGuardAttackReason(E1) [interp]
                case "revolution": return AttackReason.Revolution;
                default: return AttackReason.Kill;
            }
        }

        /// <summary>G3 SetPAL ID1 (Orc, Nrd, Reb, Mid, Ass, Nom, Out, Slave); anything else means no alignment.</summary>
        static string AlignmentOf(string id)
        {
            foreach (var a in new[] { "Orc", "Nrd", "Reb", "Mid", "Ass", "Nom", "Out", "Slave" })
                if (string.Equals(a, id?.Trim(), StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        DialogCommand TranslateShowGameMessage(InfoLine line, InfoScope scope)
        {
            var text = _strings.Resolve(line.TextId);
            if (!text.IsEmpty) return new ShowMessage(text);
            _report.Count("ShowGameMessage with a text missing from the string table", $"{scope.Info.Name}: {line.TextId}");
            return null;
        }

        /// <summary>Erase holder item amount (amount defaults to 1).</summary>
        DialogCommand TranslateErase(InfoLine line, InfoScope scope)
        {
            string item = _items.Item(line.Id1);
            if (item == null)
            {
                _report.Count("Erase of an unknown item (kept as not supported)", $"{scope.Info.Name}: {line.Id1}");
                return new NotSupported(line.Command + " unknown item", line.Id1);
            }
            return new RemoveItems(scope.Resolve(line.Entity1), item, int.TryParse(line.Id2, out int n) && n > 0 ? n : 1);
        }

        DialogCommand TranslateUse(InfoLine line, InfoScope scope)
        {
            string item = _items.Item(line.Id1);
            var user = scope.Resolve(line.Entity1);
            if (item != null) return new UseItem(user, item);
            if (!user.Equals(ActorRef.Player) && !string.IsNullOrEmpty(line.Id1))
            {
                // A prop template (Templates FakeItems_It_Letter …): G3 creates it on the NPC, who uses it — the
                // reading/drinking animation; nothing enters an inventory, so it needs no item definition.
                _report.Count("Use of a prop item by an NPC (animation only)", $"{scope.Info.Name}: {line.Id1}");
                return new UseItem(user, line.Id1.Trim());
            }
            _report.Count("Use of an unknown item (kept as not supported)", $"{scope.Info.Name}: {line.Id1}");
            return new NotSupported(line.Command + " unknown item", line.Id1);
        }

        public bool IsDescription(InfoLine line) => string.Equals(line.Command, DescriptionCommand, StringComparison.OrdinalIgnoreCase);

        /// <summary>Null when the line carries nothing to run (reported).</summary>
        public DialogCommand Translate(InfoLine line, InfoScope scope)
        {
            if (string.IsNullOrEmpty(line.Command))
            {
                _report.Count("empty script line (skipped)", scope.Info.Name);
                return null;
            }
            if (_translations.TryGetValue(line.Command, out var translate)) return translate(line, scope);
            _report.Count("command not supported yet: " + line.Command, scope.Info.Name);
            var args = new[] { line.Entity1, line.Entity2, line.Id1, line.Id2, line.TextId }.Where(a => !string.IsNullOrEmpty(a));
            return new NotSupported(line.Command, string.Join(" ", args));
        }

        /// <summary>
        /// CP 1.75 keeps a Say line's stage direction in its ID2 (<c>InfoScript_IDs2</c>): <c>SD_Angry</c>, <c>SD_Point</c> …
        /// (Docs/dialog-presentation.md §3). Anything else is not a stage direction.
        /// </summary>
        public static string StageDirection(string id2)
        {
            var s = id2?.Trim();
            return !string.IsNullOrEmpty(s) && s.StartsWith("SD_", System.StringComparison.OrdinalIgnoreCase) ? s : null;
        }

        DialogCommand TranslateSay(InfoLine line, InfoScope scope)
        {
            var text = _strings.Resolve(line.TextId);
            if (text.IsEmpty)
            {
                _report.Count("Say line with a text id missing from the string table", $"{scope.Info.Name}: {line.TextId}");
                return null;
            }
            var speaker = scope.Resolve(line.Entity1);
            string g3Speaker = scope.G3SpeakerName(speaker);
            string voice = _voices.FindLine(text.Value, g3Speaker);
            if (voice == null)
                _report.Count(g3Speaker == null ? "Say line by the conversation partner (voice resolved later)" : "Say line without a recording",
                              $"{scope.Info.Name}: {text.Value}");
            return new Say(speaker, text, voice, StageDirection(line.Id2));
        }

        /// <summary>G3 multiplies the amount by 5 when the command runs; the content stores the final XP.</summary>
        DialogCommand TranslateGiveXp(InfoLine line, InfoScope scope)
        {
            if (int.TryParse(line.Id1, out int amount) && amount > 0) return new GiveXp(amount * QuestImporter.ScriptXpMultiplier);
            _report.Count("GiveXP without a positive amount", $"{scope.Info.Name}: {line.Id1}");
            return null;
        }

        static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

        /// <summary>Give from to item amount (amount defaults to 1).</summary>
        DialogCommand TranslateGive(InfoLine line, InfoScope scope)
        {
            string item = _items.Item(line.Id1);
            if (item == null)
            {
                _report.Count("Give of an unknown item", $"{scope.Info.Name}: {line.Id1}");
                return null;
            }
            int amount = int.TryParse(line.Id2, out int n) && n > 0 ? n : 1;
            return new GiveItems(scope.Resolve(line.Entity1), scope.Resolve(line.Entity2), item, amount);
        }

        DialogCommand QuestCommand(InfoLine line, InfoScope scope, Func<Id<QuestDef>, DialogCommand> create)
        {
            var quest = _quests.Resolve(line.Id1);
            if (!quest.IsEmpty) return create(quest);
            _report.Count(line.Command + " of an unknown quest", $"{scope.Info.Name}: {line.Id1}");
            return null;
        }

        DialogCommand TranslateAddQuestLog(InfoLine line, InfoScope scope)
        {
            var quest = _quests.Resolve(line.Id1);
            var text = _strings.Resolve(line.Id2);
            if (text.IsEmpty) text = _strings.Resolve(line.TextId);
            if (!quest.IsEmpty && !text.IsEmpty) return new AddQuestLog(quest, text);
            _report.Count("AddQuestLog with an unknown quest or a text missing from the string table", $"{scope.Info.Name}: {line.Id1} {line.Id2}");
            return null;
        }

        DialogCommand TranslateRunQuest(InfoLine line, InfoScope scope)
        {
            var quest = _quests.Resolve(line.Id1);
            if (!quest.IsEmpty) return new StartQuest(quest);
            _report.Count("RunQuest of an unknown quest", $"{scope.Info.Name}: {line.Id1}");
            return null;
        }
    }
}
