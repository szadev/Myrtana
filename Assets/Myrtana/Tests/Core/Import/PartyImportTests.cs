using System;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// Content test over the real Gothic 3 data (skipped without an installation): the party topics, the BringNpc and
    /// FollowNpc deliveries, CondOwnerNearEntity and the named places as the importer translates them (Docs/party.md).
    /// </summary>
    public class PartyImportTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static string _pack;
        static IContentDatabase _db;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_party_pack");
            G3ContentImporter.Run(data, _pack);
            _db = GameContent.Load(new IContentSource[] { new DirectoryContentSource(_pack) }).Database;
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            if (_pack != null && Directory.Exists(_pack)) Directory.Delete(_pack, true);
        }

        static DialogEntryDef Entry(string id) => _db.TryGet(new Id<DialogEntryDef>(id), out DialogEntryDef e) ? e : null;

        static string Tree(Condition c) => c switch
        {
            null => "",
            AllOf all => "all(" + string.Join(",", all.Conditions.Select(Tree)) + ")",
            Near n => $"near({n.Actor},{n.Entity},{n.Metres})",
            InPlayerParty p => "led(" + string.Join(",", p.Actors) + ")",
            PartyAllows a => $"offer({a.Actor},{a.Offer})",
            QuestStatusIs q => $"quest({q.Quest.Name}:{string.Join("|", q.Statuses)})",
            _ => c.GetType().Name,
        };

        [Test]
        public void HeroJoinTopic_OffersJoin_ChecksTheRoom_AndJoins()
        {
            var join = Entry("SKALVERAM336");
            Assert.That(Tree(join.Condition), Is.EqualTo("offer(partner,Join)"));
            Assert.That(join.PreChecks.OfType<PartyHasRoom>().Count(), Is.EqualTo(1));
            Assert.That(join.Commands.OfType<JoinParty>().Single().Type, Is.EqualTo(PartyMemberType.Party));
            Assert.That(Tree(Entry("KALVERAMX231").Condition), Is.EqualTo("offer(partner,Wait)"));
            Assert.That(Entry("KALVERAMX231").Commands.OfType<WaitParty>().Count(), Is.EqualTo(1));
            Assert.That(Entry("KALVERAMX233").Commands.OfType<JoinParty>().Single().Type, Is.EqualTo(PartyMemberType.Slave));
        }

        [Test]
        public void GrompelDelivery_NeedsTragakLedAndNear_AndCompletesTheDelivery()
        {
            var delivery = Entry("BPANKRATZ31607");
            Assert.That(Tree(delivery.Condition), Is.EqualTo(
                "all(all(quest(Tragak_BringHimToGrompel:Running),led(Tragak),near(Tragak,Grompel,20)),near(Grompel,Tragak,20),ActorIs)"));
            Assert.That(delivery.Commands.Last(), Is.TypeOf<CompleteDelivery>());
        }

        /// <summary>fix-100: CondOwnerNearEntity naming an NPC is 20 m (G3 scales NPC distances by 0.25).</summary>
        [Test]
        public void JavierDelivery_NeedsKliffLedAndWithinTwentyMetres()
        {
            var delivery = Entry("BPANKRATZ31233");
            Assert.That(Tree(delivery.Condition), Is.EqualTo(
                "all(all(quest(Kliff_BringHimToJavierJavier:Running),led(Kliff),near(Kliff,Javier,20)),near(Javier,Kliff,20),ActorIs)"));
            Assert.That(delivery.Commands.Last(), Is.TypeOf<CompleteDelivery>());
        }

        [Test]
        public void MortArrival_IsNearTheWolfclanPoint_AndThePointIsImported()
        {
            Assert.That(Tree(Entry("SKALVERAM3524").Condition), Is.EqualTo("all(quest(Mort_GuideWolf:Running),near(Mort,FP_Wolfclan,5))"));
            Assert.That(_db.TryGet("FP_Wolfclan", out WorldPlaceDef wolfclan), Is.True);
            Assert.That(_db.TryGet("Ardea_4Friends", out WorldPlaceDef ardea), Is.True, "the opening's meeting point");
            Assert.That(ardea.Position.Length, Is.EqualTo(3));
        }

        [Test]
        public void LardoTopic_NeedsLesterInTheParty_AndLetsHimGo()
        {
            var lardo = Entry("NEWM1056");
            Assert.That(Tree(lardo.Condition), Is.EqualTo("led(Lester)"));
            Assert.That(lardo.Commands.OfType<LeaveParty>().Single().StopBringNpc, Is.True);
            Assert.That(Entry("NEWM1055").Commands.OfType<JoinParty>().Single().Type, Is.EqualTo(PartyMemberType.Slave));
        }
    }
}
