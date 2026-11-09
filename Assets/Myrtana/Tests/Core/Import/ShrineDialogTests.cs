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
    /// Content test over the real Gothic 3 data (skipped without an installation; set G3_DATA to point elsewhere): talking
    /// to an Innos statue. G3 ZS_Talk sets PERM_NEWS on every talk start; the statue's News entry FILLERX2 enables its
    /// teaching (its gCNPC_PS TeachSkills) and the lesson menus appear.
    /// </summary>
    public class ShrineDialogTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static readonly ActorId Statue = new ActorId("G3_Object_Interact_Innos_Statue_01");

        static string _pack;
        GameSession _session;
        RecordingPresenter _presenter;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_shrine_pack");
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
        }

        void FinishLines()
        {
            while (_session.Dialog.IsActive && _session.Dialog.Current.Phase == ConversationPhase.AwaitingLine) _session.Dialog.CompleteLine();
        }

        List<string> MenuIds() => _session.Dialog.Current.Options.Select(o => o.Entry.Name).ToList();

        [Test]
        public void InnosStatue_OffersItsLessons()
        {
            Assert.That(_session.Dialog.CanTalkTo(Statue), Is.True);
            _session.Dialog.Talk(Statue);
            FinishLines();

            Assert.That(_session.Dialog.IsActive, Is.True, "the statue's dialog must not end at once");
            Assert.That(_session.Flags.IsSet(DialogService.TalkStartedFlag), Is.False, "FILLERX2 clears PERM_NEWS again");
            var menu = MenuIds();
            Assert.That(menu, Has.Member("FILLERX21"), "ancient magic lessons (Spl_Light …)");
            Assert.That(menu, Has.No.Member("FILLER64"), "a Teach info without a lesson is never offered");

            _session.Dialog.Choose(new Id<DialogEntryDef>("FILLERX21"));
            FinishLines();
            Assert.That(MenuIds(), Has.Member("FILLERX23"), "Light");
        }
    }
}
