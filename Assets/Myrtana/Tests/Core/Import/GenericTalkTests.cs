using System;
using System.Collections.Generic;
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
    /// fix-158 over the real Gothic 3 data (skipped without an installation): generic NPCs (no infos of their own) are
    /// talkable when one of the hero's generic infos is available for them (G3 gCInfoManager_PS::HasInfos: an owner
    /// without infos uses the PC_Hero list) — the traders that trade from the start get FILLER27, teachers their lessons;
    /// one with nothing at all answers with a StartSayRefuseTalk line and no dialog opens.
    /// </summary>
    public class GenericTalkTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static readonly Id<DialogEntryDef> Trade = new Id<DialogEntryDef>("FILLER27");
        static string _data, _pack;
        GameSession _session;
        RecordingPresenter _presenter;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            _data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(_data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + _data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_generic_talk_pack");
            G3ContentImporter.Run(_data, _pack);
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

        HashSet<ActorId> Owners() =>
            new HashSet<ActorId>(_session.Content.Database.All<DialogEntryDef>()
                                         .Where(e => !e.IsSubChoice && e.Kind != DialogEntryKind.Comment).Select(e => e.Owner));

        List<NpcDef> GenericNpcs()
        {
            var owners = Owners();
            return _session.Content.Database.All<NpcDef>().Where(n => !owners.Contains(n.Id)).ToList();
        }

        List<Id<DialogEntryDef>> HeroTopicsFor(ActorId npc) =>
            _session.Dialog.TopicsFor(npc).Where(e => e.Kind != DialogEntryKind.Exit).Select(e => e.Id).ToList();

        [Test]
        public void GenericNpcs_WithAHeroTopic_AreTalkable()
        {
            var generic = GenericNpcs();
            var talkable = generic.Where(n => _session.Dialog.CanTalkTo(n.Id)).ToList();
            var byTopic = talkable.SelectMany(n => HeroTopicsFor(n.Id).Select(t => t.Name)).GroupBy(t => t)
                                  .OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}");
            var summary = $"generic {generic.Count}, talkable {talkable.Count}: {string.Join(", ", byTopic)}";
            TestContext.WriteLine(summary);
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "f158_generic.txt"), generic.Select(n => "G	" + n.Id.Name + "	" + n.VoiceSet).Concat(
                               new[] { summary }.Concat(talkable.Select(n => n.Id.Name + "\t" + string.Join(",", HeroTopicsFor(n.Id).Select(t => t.Name))))));
            Assert.That(talkable.Count, Is.EqualTo(43), summary);
            Assert.That(talkable.All(n => HeroTopicsFor(n.Id).Contains(Trade)), summary);
            Assert.That(talkable.Count, Is.LessThan(generic.Count), "not every generic NPC has something to say");
            // every talkable generic NPC has a real topic in its list (a trade, a lesson, a pickpocket, a join …)
            foreach (var n in talkable)
                Assert.That(HeroTopicsFor(n.Id), Is.Not.Empty, n.Id.Name);
        }

        [Test]
        public void NamedNpcs_StayTalkable()
        {
            foreach (var owner in Owners().Where(o => !o.IsPlayer && _session.Content.Database.TryGet(o.Name, out NpcDef _)))
                Assert.That(_session.Dialog.CanTalkTo(owner), Is.True, owner.Name);
        }

        [Test]
        public void RebTrader_OffersTrade_WhenHisSectorIsOn()
        {
            var trader = _session.Content.Database.All<NpcDef>().First(n => n.Id.Name.StartsWith("RebTrader_02", StringComparison.Ordinal));
            Assert.That(Owners().Contains(trader.Id), Is.False, "a generic NPC");
            if (!string.IsNullOrEmpty(trader.Sector)) _session.WorldState.Sectors.Set(trader.Sector, true);
            Assert.That(_session.Dialog.CanTalkTo(trader.Id), Is.True);
            _session.Dialog.Talk(trader.Id);
            Assert.That(_session.Dialog.IsActive, Is.True);
            Assert.That(_presenter.Menus.Last().Select(o => o.Entry), Does.Contain(Trade));
        }

        [Test]
        public void GenericPeasant_WithNothingToSay_RefusesWithASharedLine()
        {
            var lines = new List<NpcAmbientLine>();
            _session.Events.Subscribe<NpcAmbientLine>(lines.Add);
            var refused = new List<TalkRefused>();
            _session.Events.Subscribe<TalkRefused>(refused.Add);
            var peasant = GenericNpcs().First(n => n.Id.Name.StartsWith("Reb_Babe_Peasant", StringComparison.Ordinal) && !_session.Dialog.CanTalkTo(n.Id)
                                                   && !string.IsNullOrEmpty(n.VoiceSet));
            _session.Dialog.Talk(peasant.Id);
            Assert.That(_session.Dialog.IsActive, Is.False, "no dialog (G3 AssessTalk: HasInfo false)");
            Assert.That(refused.Select(r => r.Npc), Is.EqualTo(new[] { peasant.Id }));
            Assert.That(lines.Select(l => l.Speaker), Is.EqualTo(new[] { peasant.Id }));
            TestContext.WriteLine($"{peasant.Id}: {lines[0].Line}");
            // the three variants cycle (G3 DAT_10118a84): situational, parting, keep-away
            _session.Dialog.Talk(peasant.Id);
            _session.Dialog.Talk(peasant.Id);
            Assert.That(lines.Count, Is.EqualTo(3));
            // a woman: "BetterGoNow", "BetterNotTalk" (FUN_10052080 / FUN_10052180), unless she sleeps
            if (lines[0].Line != "SleepRefuse")
                Assert.That(lines.Select(l => l.Line).Skip(1), Is.EqualTo(new[] { "BetterGoNow", "BetterNotTalk" }));
        }
    }
}
