using System;
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
    /// fix-183 content test over the real Gothic 3 data (skipped without an installation): Jensgar_BringFur (30 ice wolf
    /// furs + 10 bison furs). G3 tests a delivery net of what the destination already got [CP Game.dll
    /// gCInfo_PS::AreConditionsFulfilled 0x101c3370 case 8/9]: PartDelivery SKALVERAM384 needs a piece of an item not yet
    /// delivered in full (HasNPCpartOfRequiredItems 0x10157610) and not everything still missing
    /// (HaveNPCsAllRequiredItems 0x101576d0: carried + counter ≥ amount), Delivery SKALVERAM392 the latter. Owner's
    /// save298: counters 30/5, hero 19 ice wolf furs, no bison fur — the port still offered "Вот несколько шкур".
    /// </summary>
    public class JensgarFurDeliveryTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        const string Wolf = "It_IceWolf_Fur", Bison = "It_Bison_Fur";
        const string Part = "SKALVERAM384", Full = "SKALVERAM392";
        static readonly ActorId Jensgar = new ActorId("Jensgar");
        static readonly Id<QuestDef> BringFur = new Id<QuestDef>("Jensgar_BringFur");

        static string _pack;
        GameSession _session;
        RecordingPresenter _presenter;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_jensgar_pack");
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
            _session.Quests.TrySetStatus(BringFur, QuestStatus.Running);
        }

        int Hero(string item) => _session.Items.Inventories.Of(ActorId.Player).Count(item);
        int[] Counters => _session.Quests.ProgressOf(BringFur)?.Counters ?? new int[2];

        void Carry(int wolf, int bison)
        {
            var inv = _session.Items.Inventories;
            inv.Of(ActorId.Player).Remove(Wolf, Hero(Wolf));
            inv.Of(ActorId.Player).Remove(Bison, Hero(Bison));
            inv.Give(ActorId.Player, Wolf, wolf);
            inv.Give(ActorId.Player, Bison, bison);
        }

        bool Offered(string id)
        {
            if (_session.Dialog.IsActive) _session.Dialog.Abort();
            _session.Dialog.Talk(Jensgar);
            SkipLines();
            return _session.Dialog.IsActive && _session.Dialog.Current.Options.Any(o => o.Entry.Name == id);
        }

        void SkipLines()
        {
            while (_session.Dialog.IsActive && _session.Dialog.Current.Phase == ConversationPhase.AwaitingLine) _session.Dialog.CompleteLine();
        }

        void Choose(string id)
        {
            Assert.That(Offered(id), Is.True, id + " offered");
            _session.Dialog.Choose(new Id<DialogEntryDef>(id));
            SkipLines();
            if (_session.Dialog.IsActive) _session.Dialog.Abort();
        }

        [Test]
        public void Conditions_AreNetOfTheDeliveryCounters()
        {
            DialogEntryDef Entry(string id) => _session.Content.Database.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef e) ? e : null;
            Assert.That(_session.Localizer.Text(Entry(Part).Title), Is.EqualTo("Вот несколько шкур."));
            Assert.That(Flatten(Entry(Part).Condition).OfType<CarriesQuestItems>().Select(c => c.Share), Is.EquivalentTo(new[] { QuestItemShare.Some, QuestItemShare.All }));
            Assert.That(Flatten(Entry(Full).Condition).OfType<CarriesQuestItems>().Select(c => c.Share), Is.EqualTo(new[] { QuestItemShare.All }));
            Assert.That(Flatten(Entry(Part).Condition).OfType<HasItems>(), Is.Empty, "no plain full-amount item checks");
        }

        static System.Collections.Generic.IEnumerable<Condition> Flatten(Condition c)
        {
            yield return c;
            var children = c is AllOf all ? all.Conditions : c is AnyOf any ? any.Conditions : c is Not not ? new[] { not.Condition } : Array.Empty<Condition>();
            foreach (var child in children) foreach (var x in Flatten(child)) yield return x;
        }

        [Test]
        public void OwnerSave298_AllWolvesDelivered_NoBison_NoDeliveryTopic()
        {
            Carry(wolf: 32, bison: 5);
            Choose(Part); // the first part delivery: 30 wolves and the 5 bison furs he had
            Assert.That(Counters, Is.EqualTo(new[] { 30, 5 }));
            Assert.That(Hero(Wolf), Is.EqualTo(2), "only what is still missing is taken");

            Carry(wolf: 19, bison: 0);
            Assert.That(Offered(Part), Is.False, "wolf furs are no longer needed, no bison fur carried (G3 HasNPCpartOfRequiredItems)");
            Assert.That(Offered(Full), Is.False);
            Assert.That(_session.Quests.StatusOf(BringFur), Is.EqualTo(QuestStatus.Running));
        }

        [Test]
        public void RemainingBisonFurs_PartThenFullDelivery_GiveTheReward()
        {
            Carry(wolf: 30, bison: 5);
            Choose(Part);
            Carry(wolf: 19, bison: 3);
            Assert.That(Offered(Part), Is.True, "3 of the 5 missing bison furs");
            Assert.That(Offered(Full), Is.False);
            Choose(Part);
            Assert.That(Counters, Is.EqualTo(new[] { 30, 8 }));
            Assert.That(Hero(Wolf), Is.EqualTo(19), "wolf furs stay with the hero");

            Carry(wolf: 0, bison: 4);
            Assert.That(Offered(Part), Is.False, "he carries everything still missing (G3: not HaveNPCsAllRequiredItems)");
            Assert.That(Offered(Full), Is.True, "Delivery counts what Jensgar already has");
            Choose(Full);
            Assert.That(Hero(Bison), Is.EqualTo(2));
            Assert.That(Hero("It_Bow_Composite"), Is.EqualTo(1), "the reward bow");
            Assert.That(_session.Quests.StatusOf(BringFur), Is.EqualTo(QuestStatus.Success));
        }
    }
}
