using System;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-147 content test over the real Gothic 3 data (skipped without an installation): freeing Gorn in the orc camp at
    /// Gotha. Gorn_FreeHim is a Report quest whose target is Potros; the paid way reports to him (BPANKRATZ31217, 2000
    /// gold). Killing Potros before that fails it, as in G3: CP gCQuest_PS::OnNPCKilled 0x10159110, type Report, a target
    /// killed before its counter is reached → Open: Obsolete, else Failed. Gorn is free either way: "You're free now"
    /// (BPANKRATZ31196) needs Gorn_FreeHim finished, and Failed is final. The kill also wins Potros_DefeatHim (a Defeat
    /// quest counts kills, OnNPCKilled types 2-4).
    /// </summary>
    public class GornPotrosTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static readonly ActorId Potros = new ActorId("Potros");
        static readonly ActorId Gorn = new ActorId("Gorn");
        static readonly Id<QuestDef> FreeGorn = new Id<QuestDef>("Gorn_FreeHim");
        static readonly Id<QuestDef> DefeatPotros = new Id<QuestDef>("Potros_DefeatHim");
        static readonly Id<QuestDef> GothaParty = new Id<QuestDef>("Gorn_GothaParty");

        static string _pack;
        GameSession _session;
        RecordingPresenter _presenter;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_gorn_pack");
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
            _presenter = new RecordingPresenter();
            _session = GameSession.NewGame(content, new SessionPorts(_presenter, null), new GameSettings(new LanguageSettings("Russian", "Russian"), randomSeed: 1));
            // Gorn told his story in the cell and Potros heard the hero out: the owner's state at Day 9 12:05 (save145).
            _session.Flags.Set("Gorn_GothaPrison");
            _session.Flags.Set("Potros_HisDuties");
            _session.Flags.Set("Potros_GornWillBePunished");
            _session.Quests.TrySetStatus(FreeGorn, QuestStatus.Running);
        }

        QuestDef Quest(Id<QuestDef> id) => _session.Content.Database.TryGet(id, out QuestDef q) ? q : null;

        /// <summary>Talks to the NPC (twice when its greeting opens a sub-level first) and tells whether the entry is offered.</summary>
        bool Offered(ActorId npc, string entry)
        {
            for (int talk = 0; talk < 2; talk++)
            {
                if (_session.Dialog.IsActive) _session.Dialog.Abort();
                _session.Dialog.Talk(npc);
                RunLines();
                if (_session.Dialog.IsActive && _session.Dialog.Current.Options.Any(o => o.Entry.Name == entry)) return true;
            }
            return false;
        }

        void RunLines()
        {
            while (_session.Dialog.IsActive && _session.Dialog.Current.Phase == ConversationPhase.AwaitingLine) _session.Dialog.CompleteLine();
        }

        /// <summary>Gorn sits in his cell at Gotha (a new game has him with the friends at Ardea, whose News ends the talk).</summary>
        void PutGornInHisCell()
        {
            // places.json Gotha_Gorn_Prison_Sit_Ground
            Assert.That(_session.Npcs.CorrectPose(Gorn, new WorldPose(364.68f, 53.13f, 411.22f, 0)), Is.True);
        }

        void KillPotros() =>
            _session.Events.Publish(new ActorDefeated(Potros, byPlayerParty: true, killed: true, victimLevel: 20, ambientCreature: false, species: "Orc"));

        [Test]
        public void TheQuests_AsImported()
        {
            var free = Quest(FreeGorn);
            Assert.That(free.Objective.Kind, Is.EqualTo(QuestObjectiveKind.Report));
            Assert.That(free.Objective.Targets.Select(t => t.Target), Is.EqualTo(new[] { "Potros" }));
            var defeat = Quest(DefeatPotros);
            Assert.That(defeat.Objective.Kind, Is.EqualTo(QuestObjectiveKind.Defeat));
            Assert.That(defeat.Objective.Targets.Select(t => t.Target), Is.EqualTo(new[] { "Potros" }));
        }

        [Test]
        public void Bribe_FreesGorn_WithSuccess()
        {
            _session.Flags.Set("Potros_Corrupt"); // "How much?" (BPANKRATZ31216): 2000 gold
            var purse = _session.Items.Inventories.Of(ActorId.Player);
            int before = purse.Count("It_Gold");
            purse.Add("It_Gold", 2000);
            Assert.That(Offered(Potros, "BPANKRATZ31217"), Is.True, "Here, take the gold");
            _session.Dialog.Choose(new Id<DialogEntryDef>("BPANKRATZ31217"));
            RunLines();
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Success));
            Assert.That(purse.Count("It_Gold"), Is.EqualTo(before), "2000 gold to Potros");
            PutGornInHisCell();
            Assert.That(Offered(Gorn, "BPANKRATZ31196"), Is.True, "You're free now");
        }

        [Test]
        public void KillingPotros_FailsTheQuest_AsG3_ButGornIsFreeAnyway()
        {
            KillPotros();
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Failed), "Report target killed before the report");
            Assert.That(_session.Quests.StatusOf(DefeatPotros), Is.EqualTo(QuestStatus.Success), "a Defeat quest counts the kill, even unaccepted");
            Assert.That(_session.Quests.TrySetStatus(FreeGorn, QuestStatus.Success), Is.False, "Failed is final (dev console too)");

            Assert.That(_session.Quests.StatusOf(GothaParty), Is.EqualTo(QuestStatus.Open));
            PutGornInHisCell();
            Assert.That(Offered(Gorn, "BPANKRATZ31196"), Is.True, "You're free now: Gorn_FreeHim finished (Failed counts)");
            _session.Dialog.Choose(new Id<DialogEntryDef>("BPANKRATZ31196"));
            RunLines();
            Assert.That(_session.Flags.IsSet("Gorn_FreeAgain"), Is.True);
        }

        [Test]
        public void KillingPotrosBeforeTheQuest_OnlyObsoletesIt()
        {
            var content = _session.Content;
            _session = GameSession.NewGame(content, new SessionPorts(_presenter, null), new GameSettings(new LanguageSettings("Russian", "Russian"), randomSeed: 1));
            KillPotros();
            Assert.That(_session.Quests.StatusOf(FreeGorn), Is.EqualTo(QuestStatus.Obsolete), "Open → Obsolete, no failure message");
        }
    }
}
