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
    /// Content test over the real Gothic 3 data (skipped without an installation): gate guards with their guard points,
    /// the FirstWarn / SecondWarn, PickPocket, Heal, Animal, QuestRunning and DriveAway infos as the importer translates
    /// them (story-3, Docs/story-coverage.md §5).
    /// </summary>
    public class GuardImportTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        static string _pack;
        static IContentDatabase _db;

        [OneTimeSetUp]
        public void ImportOnce()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Infos.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            _pack = Path.Combine(Path.GetTempPath(), "myrtana_story3_pack");
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
            Not n => "not(" + Tree(n.Condition) + ")",
            GateGuardMayWarn w => $"warn({w.Guard},{(w.Second ? 2 : 1)})",
            QuestStatusIs q => $"quest({q.Quest.Name}:{string.Join("|", q.Statuses)})",
            QuestRepeatDue r => $"repeat({r.Quest.Name},{r.Hours})",
            DialogEndedHoursAgo d => $"ended({d.Actor},{d.Hours})",
            _ => c.GetType().Name,
        };

        [Test]
        public void GateGuards_HaveTheirGuardPoints()
        {
            var guards = _db.All<GateGuardDef>().ToList();
            Assert.That(guards.Count, Is.GreaterThanOrEqualTo(10));
            var faring = guards.Single(g => g.Npc.Name == "Orc_Faring_GateGuard_01");
            Assert.That(faring.ActiveAtStart, Is.True);
            Assert.That(faring.Point.X, Is.EqualTo(602.6).Within(1), "Faring's gate (world metres)");
            Assert.That(guards.Select(g => g.Npc.Name), Does.Contain("Asam").And.Contain("Garik"), "the FirstWarn owners");
        }

        [Test]
        public void WarningInfos_AskTheGuard_AndRecordTheWarning()
        {
            var first = Entry("FILLERX2131"); // the hero's generic "Halt! Don't go further." for every gate guard
            Assert.That(first.Kind, Is.EqualTo(DialogEntryKind.Important));
            Assert.That(Tree(first.Condition), Is.EqualTo("warn(partner,1)"));
            Assert.That(first.Commands.OfType<GateGuardWarned>().Single().Second, Is.False);
            var second = Entry("FILLER429");
            Assert.That(Tree(second.Condition), Is.EqualTo("warn(Raid,2)"), "an NPC's own info names the owner");
            Assert.That(second.Commands.OfType<GateGuardWarned>().Single().Second, Is.True);
        }

        [Test]
        public void SmallConditions_PickpocketHealAnimalQuestRunningDriveAway()
        {
            Assert.That(Tree(Entry("FILLERX2145").Condition), Is.EqualTo("MayPickpocket"));
            Assert.That(Entry("FILLERX2145").Commands.OfType<Pickpocket>().Single().Victim, Is.EqualTo(ActorRef.Partner));
            Assert.That(Tree(Entry("BPANKRATZ31841").Condition), Is.EqualTo("CanHeal"));
            Assert.That(Tree(Entry("NEWM0066").Condition), Is.EqualTo("all(all(not(CanFight),ended(Cow,48)),HasItems)"));
            Assert.That(Entry("NEWM0066").Commands.OfType<SaySound>().Single().Sound, Is.EqualTo("sfx_creature_cow_moo_03"));
            Assert.That(Tree(Entry("NEWM1003").Condition), Is.EqualTo("repeat(Lardo_SpellQuestTest,2)"));
            Assert.That(Entry("NEWM1003").Commands.OfType<RememberRun>().Count(), Is.EqualTo(1));
            Assert.That(Tree(Entry("SKALVERAM320").Condition), Is.EqualTo("quest(Test_DriveAway:Running)"), "DriveAway delivery: Running only");
            Assert.That(_db.All<PickpocketDef>().Count(), Is.GreaterThan(100));
        }
    }
}
