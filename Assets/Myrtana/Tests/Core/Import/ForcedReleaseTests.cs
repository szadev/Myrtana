using System;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-161 content test over the real Gothic 3 data (skipped without an installation). The owner freed Gorn by killing
    /// Potros and found two quests under "Failed": Gorn_FreeHim (G3 fails a Report quest whose target dies, fix-147) and
    /// Gorn_ShowReddock, which Gorn's cell News (BPANKRATZ31192) closes while still Open → Obsolete, as G3 does; G3's quest
    /// log lists only Running / Success / Failed (CP Game.dll 1009f120), so an Obsolete quest is on no tab. The [owner] port
    /// option "Freeing by force counts" (<see cref="ForcedReleases"/>) makes killing the keeper win the release quest.
    /// </summary>
    public class ForcedReleaseTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static readonly ActorId Potros = new ActorId("Potros"), Gorn = new ActorId("Gorn");
        static readonly ActorId Zapotek = new ActorId("Zapotek"), Lars = new ActorId("Lars");
        static readonly Id<QuestDef> FreeGorn = new Id<QuestDef>("Gorn_FreeHim"), FreeLars = new Id<QuestDef>("Lars_FreeHim");
        static readonly Id<QuestDef> ShowReddock = new Id<QuestDef>("Gorn_ShowReddock");

        static string _pack;
        GameSession _session;
        long _xp;
        readonly System.Collections.Generic.List<int> _awards = new System.Collections.Generic.List<int>();

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_f161_pack");
            G3ContentImporter.Run(data, _pack);
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            if (_pack != null && Directory.Exists(_pack)) Directory.Delete(_pack, true);
        }

        [SetUp]
        public void NewGame()
        {
            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(_pack) });
            _session = GameSession.NewGame(content, new SessionPorts(new RecordingPresenter(), null),
                                           new GameSettings(new LanguageSettings("Russian", "Russian"), randomSeed: 1));
            _xp = 0;
            _awards.Clear();
            _session.Events.Subscribe<XpAwarded>(e => { _xp += e.Amount; _awards.Add(e.Amount); });
        }

        bool Offered(ActorId npc, string entry)
        {
            for (int talk = 0; talk < 2; talk++)
            {
                if (_session.Dialog.IsActive) _session.Dialog.Abort();
                _session.Dialog.Talk(npc);
                while (_session.Dialog.IsActive && _session.Dialog.Current.Phase == ConversationPhase.AwaitingLine) _session.Dialog.CompleteLine();
                if (_session.Dialog.IsActive && _session.Dialog.Current.Options.Any(o => o.Entry.Name == entry)) return true;
            }
            return false;
        }

        void Kill(ActorId npc, bool byPlayerParty = true)
        {
            _session.Combat.Vitals.Kill(npc, byPlayerParty); // publishes ActorDefeated
            Assert.That(_session.Combat.Vitals.IsDead(npc), Is.True);
        }

        void GornQuestRunning()
        {
            _session.Flags.Set("Gorn_GothaPrison");
            _session.Flags.Set("Potros_HisDuties");
            _session.Flags.Set("Potros_GornWillBePunished");
            _session.Quests.TrySetStatus(FreeGorn, QuestStatus.Running);
        }

        [Test]
        public void TheReleaseQuests_OfThePack()
        {
            var rules = _session.ForcedReleases.Rules.OrderBy(r => r.Quest.Id.Name, StringComparer.Ordinal)
                                .Select(r => $"{r.Quest.Id.Name}:{r.Target.Name}:{r.Deal.Id.Name}").ToArray();
            Assert.That(rules, Is.EqualTo(new[] { "Gorn_FreeHim:Potros:BPANKRATZ31217", "Lars_FreeHim:Zapotek:BPANKRATZ3267" }),
                        "Report quests with a paid release, freed anyway by quest end / keeper death, no failure branch");
        }

        [Test]
        public void ShowReddock_ClosedInGornsCell_IsObsolete_AndOnNoTab()
        {
            _session.Quests.TrySetStatus(ShowReddock, QuestTransitions.CloseTarget(_session.Quests.StatusOf(ShowReddock))); // BPANKRATZ31192
            Assert.That(_session.Quests.StatusOf(ShowReddock), Is.EqualTo(QuestStatus.Obsolete));
            Assert.That(QuestJournal.GroupOf(QuestStatus.Obsolete), Is.Null);
            Assert.That(QuestJournal.GroupOf(QuestStatus.Cancelled), Is.Null);
            Assert.That(QuestJournal.GroupOf(QuestStatus.Lost), Is.Null);
            Assert.That(QuestJournal.GroupOf(QuestStatus.Won), Is.Null);
            Assert.That(QuestJournal.GroupOf(QuestStatus.Failed), Is.EqualTo(QuestJournal.Group.Failed));
            var journal = QuestJournal.Of(_session);
            Assert.That(journal.Groups.Values.SelectMany(g => g).Any(q => q.Id == ShowReddock), Is.False);
        }

        [Test]
        public void OptionOff_KillingPotros_FailsAsG3()
        {
            GornQuestRunning();
            Kill(Potros);
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Failed));
        }

        [Test]
        public void OptionOn_KillingPotros_FreesGorn_WithTheRewards()
        {
            _session.ForcedReleases.Enabled = true;
            GornQuestRunning();
            int reb = _session.Fame.Of(FameKind.Political, "Reb");
            Kill(Potros);
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Success));
            Assert.That(_awards, Has.Member(1500), "Gorn_FreeHim's XP (besides the kill's and Potros_DefeatHim's)");
            Assert.That(_session.Fame.Of(FameKind.Political, "Reb"), Is.EqualTo(reb + 1));
            Assert.That(_session.Npcs.CorrectPose(Gorn, new WorldPose(364.68f, 53.13f, 411.22f, 0)), Is.True);
            Assert.That(Offered(Gorn, "BPANKRATZ31196"), Is.True, "You're free now, as after the bribe");
        }

        [Test]
        public void OptionOn_KilledByOthers_StillFails()
        {
            _session.ForcedReleases.Enabled = true;
            GornQuestRunning();
            Kill(Potros, byPlayerParty: false);
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Failed));
        }

        [Test]
        public void OptionOn_KillBeforeTheQuest_StaysObsolete()
        {
            _session.ForcedReleases.Enabled = true;
            Kill(Potros);
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Obsolete));
        }

        [Test]
        public void OptionOn_KillingZapotek_FreesLarsOnce()
        {
            _session.ForcedReleases.Enabled = true;
            Assert.That(Offered(Lars, "BPANKRATZ3265"), Is.True, "his News in the pen (BPANKRATZ3263, Lars_InNot), then the request");
            Assert.That(_session.Flags.IsSet("Lars_InNot"), Is.True);
            _session.Dialog.Choose(new Id<DialogEntryDef>("BPANKRATZ3265"));
            while (_session.Dialog.IsActive && _session.Dialog.Current.Phase == ConversationPhase.AwaitingLine) _session.Dialog.CompleteLine();
            if (_session.Dialog.IsActive) _session.Dialog.Abort();
            Assert.That(_session.Quests.StatusOf(FreeLars), Is.EqualTo(QuestStatus.Running));
            Kill(Zapotek);
            Assert.That(_session.Quests.StatusOf(FreeLars), Is.EqualTo(QuestStatus.Success));
            Assert.That(_session.Flags.IsSet("Lars_InNot"), Is.False, "the deal's flag change (BPANKRATZ3267) without the 300 gold");
            Assert.That(Offered(Lars, "NEWM0061"), Is.False, "CP's kill path is not offered as well (no second 500 XP)");
            Assert.That(Offered(Lars, "BPANKRATZ3266"), Is.True, "the Success topic frees him");
        }

        [Test]
        public void SaveWithTheKillFailedQuest_IsRejudged_WhenTheOptionComesOn()
        {
            GornQuestRunning();
            Kill(Potros);
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Failed));
            using (var save = new MemoryStream())
            {
                _session.Save(save, "f161", DateTime.UtcNow);
                save.Position = 0;
                _session = GameSession.Load(_session.Content, new SessionPorts(new RecordingPresenter(), null),
                                            new GameSettings(new LanguageSettings("Russian", "Russian"), randomSeed: 1), save);
            }
            _xp = 0;
            _session.Events.Subscribe<XpAwarded>(e => _xp += e.Amount);
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Failed), "off until the applier sets it");
            _session.ForcedReleases.Enabled = true; // PortOptionsApplier.Bind after the load
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Success));
            Assert.That(_xp, Is.EqualTo(1500));
            Assert.That(QuestJournal.Of(_session).Groups[QuestJournal.Group.Succeeded].Any(q => q.Id == FreeGorn), Is.True);
            _session.ForcedReleases.Enabled = false;
            _session.ForcedReleases.Enabled = true;
            Assert.That(_xp, Is.EqualTo(1500), "once");
        }

        [Test]
        public void PortOption_Row()
        {
            var port = new PortOptions();
            Assert.That(port.ForcedReleaseCounts, Is.True, "[owner] default on");
            Assert.That(PortOptions.RowText(PortOptions.Row.ForcedRelease, "Russian"), Is.EqualTo("Освобождение силой засчитывается"));
            Assert.That(PortOptions.RowText(PortOptions.Row.ForcedRelease, "English"), Is.EqualTo("Freeing by force counts"));
            Assert.That(PortOptions.ValueText(PortOptions.Row.ForcedRelease, false, "Russian"), Is.EqualTo("Нет (оригинал)"));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.ForcedReleaseTop, Is.EqualTo(224f));
            Assert.That(Myrtana.Ui.Skin.VideoPortRowsLayout.RowBottom(Myrtana.Ui.Skin.VideoPortRowsLayout.ForcedReleaseTop),
                        Is.LessThanOrEqualTo(Myrtana.Ui.Skin.VideoPortRowsLayout.CrashRecoveryTop + Myrtana.Ui.Skin.VideoPortRowsLayout.SliderOffset));
        }
    }
}
