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
    /// Content test over the real Gothic 3 data (skipped without an installation; set G3_DATA to point elsewhere):
    /// imports the pack once and plays Slice 1's conversation with Gnar through the real DialogService.
    /// </summary>
    public class GnarDialogTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static readonly ActorId Gnar = new ActorId("Gnar");

        static string _pack;
        GameSession _session;
        RecordingPresenter _presenter;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_gnar_pack");
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

        List<string> Menu() => _session.Dialog.Current.Options.Select(o => _session.Localizer.Text(o.Title)).ToList();

        void Choose(string title) =>
            _session.Dialog.Choose(_session.Dialog.Current.Options.First(o => _session.Localizer.Text(o.Title) == title).Entry);

        [Test]
        public void Greeting_ThenEnterCastle_StartsTheQuest()
        {
            _session.Dialog.Talk(Gnar);
            Assert.That(Lines(), Is.EqualTo(new[] { "Стоять!", "Чего тебе?" }));
            Assert.That(_presenter.Lines[0].Voice, Is.EqualTo("INFO_Orc_Warrior_Hard_Gnar_FILLER20060207172543"));
            Assert.That(Menu(), Is.EqualTo(new[] { "Я хочу пройти в крепость!", "Ты пропустишь меня в крепость?", "Закончить" }));

            Choose("Я хочу пройти в крепость!");
            Assert.That(Lines(), Is.EqualTo(new[] { "Я хочу пройти в крепость!", "Нам не нужен всякий сброд!", "Нам нужны воины и охотники." }));
            Assert.That(_session.Quests.StatusOf(new Id<QuestDef>("Gnar_EnterCastle")), Is.EqualTo(QuestStatus.Running));
            Assert.That(_session.Flags.IsSet("Gnar_NotEnter"), Is.True);
            Assert.That(Menu(), Has.Member("Что в этой крепости?"));
        }

        [Test]
        public void EnclaveFriendTopic_IsRefusedWithoutFame()
        {
            _session.Dialog.Talk(Gnar);
            Lines();
            Choose("Ты пропустишь меня в крепость?");
            Assert.That(Lines(), Is.EqualTo(new[] { "Мы не знаем, можно ли тебе доверять." }));
            Assert.That(Menu(), Has.Member("Ты пропустишь меня в крепость?"));
        }

        [Test]
        public void SecondConversation_SkipsTheGreeting()
        {
            _session.Dialog.Talk(Gnar);
            Lines();
            Choose("Закончить");
            Assert.That(_session.Dialog.IsActive, Is.False);

            _session.Dialog.Talk(Gnar);
            Assert.That(Lines(), Is.Empty);
            Assert.That(Menu().Last(), Is.EqualTo("Закончить"));
        }
    }
}
