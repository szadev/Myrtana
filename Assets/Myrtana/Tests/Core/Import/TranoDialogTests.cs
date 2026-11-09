using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Session;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-141 content test over the real Gothic 3 data (skipped without an installation): Otis_DriveAwayMerc01 in
    /// Montera. "This hill is too small for the two of us" (BPANKRATZ3839) only sets Trano_WhatNow, as the G3 info does
    /// (no Attack line; designers wrote one where they wanted a fight, e.g. Lukar's FILLER549). The quest ends by
    /// killing Trano or by talking him back to work: BPANKRATZ3844 is a Delivery entry of the DriveAway quest, and G3
    /// gCInfo_PS::OnDelivery counts its owner for Report and DriveAway quests alike [Game 0x101c2620].
    /// </summary>
    public class TranoDialogTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static readonly ActorId Trano = new ActorId("Trano");
        static readonly Id<QuestDef> DriveAway = new Id<QuestDef>("Otis_DriveAwayMerc01");
        static readonly Id<QuestDef> ForceTrano = new Id<QuestDef>("Marik_ForceTrano");

        static string _pack;
        GameSession _session;
        RecordingPresenter _presenter;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_trano_pack");
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
            _session.Quests.TrySetStatus(DriveAway, QuestStatus.Running);
        }

        DialogEntryDef Entry(string id) =>
            _session.Content.Database.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef e) ? e : null;

        List<string> Lines()
        {
            var shown = new List<string>();
            while (_session.Dialog.IsActive && _session.Dialog.Current.Phase == ConversationPhase.AwaitingLine)
            {
                shown.Add(_session.Localizer.Text(_presenter.Lines.Last().Text));
                _session.Dialog.CompleteLine();
            }
            return shown;
        }

        bool Offered(string id) => _session.Dialog.IsActive && _session.Dialog.Current.Options.Any(o => o.Entry.Name == id);

        void Choose(string id)
        {
            Assert.That(Offered(id), Is.True, id + " offered");
            _session.Dialog.Choose(new Id<DialogEntryDef>(id));
        }

        void TalkAndChoose(string id)
        {
            if (!_session.Dialog.IsActive) { _session.Dialog.Talk(Trano); Lines(); }
            Choose(id);
        }

        [Test]
        public void HillTooSmall_OnlySetsTheFlag_TranoDoesNotAttack()
        {
            var hill = Entry("BPANKRATZ3839");
            Assert.That(hill.Commands.OfType<StartFight>(), Is.Empty, "the G3 info has no Attack line");
            Assert.That(hill.Commands.OfType<SetFlag>().Select(c => c.Flag), Is.EqualTo(new[] { "Trano_WhatNow" }));

            TalkAndChoose("BPANKRATZ3839");
            Assert.That(Lines(), Is.EqualTo(new[] { "Этот холм слишком мал для нас двоих.", "Неужели? И что ты теперь будешь делать?" }));
            Assert.That(_session.Flags.IsSet("Trano_WhatNow"), Is.True);
            Assert.That(_session.Quests.StatusOf(DriveAway), Is.EqualTo(QuestStatus.Running));
        }

        [Test]
        public void WorkOrNot_AfterMarikCutHisWage_DrivesHimAway()
        {
            var work = Entry("BPANKRATZ3844");
            Assert.That(work.Commands.OfType<ReportToQuest>().Single().Quest, Is.EqualTo(DriveAway), "Delivery of a DriveAway quest counts Trano");

            TalkAndChoose("BPANKRATZ3839");
            Lines();
            _session.Quests.TrySetStatus(ForceTrano, QuestStatus.Running);
            if (_session.Dialog.IsActive) _session.Dialog.Abort();
            TalkAndChoose("BPANKRATZ3842");
            Lines();
            Assert.That(_session.Flags.IsSet("Trano_PissOff"), Is.True);
            Assert.That(_session.Quests.StatusOf(ForceTrano), Is.EqualTo(QuestStatus.Success));
            Assert.That(_session.Quests.StatusOf(DriveAway), Is.EqualTo(QuestStatus.Running));

            TalkAndChoose("BPANKRATZ3844");
            Assert.That(Lines(), Is.EqualTo(new[] { "Ты будешь работать, или нет?", "Ах, ты! Утихни, псих, я уже пошел." }));
            Assert.That(_session.Dialog.IsActive, Is.False, "the entry ends the talk");
            Assert.That(_session.Quests.StatusOf(DriveAway), Is.EqualTo(QuestStatus.Success));
        }

        [Test]
        public void KillingTrano_DrivesHimAwayToo()
        {
            _session.Events.Publish(new ActorDefeated(Trano, byPlayerParty: true, killed: false, victimLevel: 10, ambientCreature: false, species: "Human"));
            Assert.That(_session.Quests.StatusOf(DriveAway), Is.EqualTo(QuestStatus.Running), "a knock-out does not count (G3 OnNPCDefeated: Defeat quests only)");
            _session.Events.Publish(new ActorDefeated(Trano, byPlayerParty: true, killed: true, victimLevel: 10, ambientCreature: false, species: "Human"));
            Assert.That(_session.Quests.StatusOf(DriveAway), Is.EqualTo(QuestStatus.Success));
        }
    }
}
