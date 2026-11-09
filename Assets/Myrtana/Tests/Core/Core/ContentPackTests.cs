using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Session;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class ContentPackTests
    {
        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "myrtana_tests_" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        static DialogEntryDef SampleEntry(string id) => new DialogEntryDef(
            new Id<DialogEntryDef>(id), new ActorId("Gnar"), default, DialogEntryKind.Choice, 42, new TextId("INFO_X"),
            false, false, false, null,
            new AllOf(new Condition[] { new FlagSet("f"), new Not(new TalkedToPlayer(ActorRef.Partner)),
                                        new QuestStatusIs(new Id<QuestDef>("Q"), new[] { QuestStatus.Open, QuestStatus.Lost }) }),
            new PreCheck[] { new MinEnclaveFame(ActorRef.Partner, 75, new DialogCommand[] { new Say(ActorRef.Partner, new TextId("SVM_No"), "SVM_No") }) },
            new DialogCommand[] { new Say(ActorRef.Player, new TextId("INFO_X"), "clip", "SD_Angry"), new SetFlag("f"), new EndDialog(), new NotSupported("GiveXP", "100") });

        string WritePack(string name, params DialogEntryDef[] entries)
        {
            var dir = Path.Combine(_root, name);
            var writer = new ContentPackWriter(dir, GameSchema.Create());
            writer.WriteDefinitions(StorySchema.DialogFolder, "Gnar", entries);
            writer.WriteDefinitions(SimulationSchema.NpcFolder, "npcs", new[] { new NpcDef(new ActorId("Gnar"), new TextId("FO_Gnar"), "Orc") });
            writer.WriteStrings("English", new StringTableFile(new Dictionary<string, string> { ["INFO_X"] = "Halt" }, null));
            writer.Finish(new ContentPackManifest(name, name, 1, ContentPackManifest.CurrentFormat, null, new[] { "English" }));
            return dir;
        }

        [Test]
        public void WrittenPack_LoadsBackWithAllPolymorphicParts()
        {
            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(WritePack("Base", SampleEntry("e1"))) });

            Assert.That(content.Problems, Is.Empty);
            Assert.That(content.Database.TryGet(new Id<DialogEntryDef>("e1"), out var e), Is.True);
            var all = (AllOf)e.Condition;
            Assert.That(all.Conditions[0], Is.TypeOf<FlagSet>());
            Assert.That(((TalkedToPlayer)((Not)all.Conditions[1]).Condition).Actor.IsPartner, Is.True);
            Assert.That(((QuestStatusIs)all.Conditions[2]).Statuses, Is.EqualTo(new[] { QuestStatus.Open, QuestStatus.Lost }));
            var fame = (MinEnclaveFame)e.PreChecks.Single();
            Assert.That(fame.Minimum, Is.EqualTo(75));
            Assert.That(((Say)fame.Refusal[0]).Voice, Is.EqualTo("SVM_No"));
            Assert.That(e.Commands.Select(c => c.GetType()), Is.EqualTo(new[] { typeof(Say), typeof(SetFlag), typeof(EndDialog), typeof(NotSupported) }));
            Assert.That(((Say)e.Commands[0]).Speaker, Is.EqualTo(ActorRef.Player));
            Assert.That(((Say)e.Commands[0]).Stage, Is.EqualTo("SD_Angry"), "stage direction");
            Assert.That(((Say)fame.Refusal[0]).Stage, Is.Null, "no stage direction");
            Assert.That(e.Order, Is.EqualTo(42));
            Assert.That(content.Database.Packs[0].ContentHash, Has.Length.EqualTo(64));
            Assert.That(content.Strings.Load("English").TryGetText("INFO_X", out var text) && text == "Halt", Is.True);
        }

        [Test]
        public void LaterPack_ReplacesAndRemovesById_AndBrokenFilesAreReported()
        {
            var basePack = WritePack("Base", SampleEntry("e1"), SampleEntry("e2"));
            var mod = Path.Combine(_root, "Mod");
            Directory.CreateDirectory(Path.Combine(mod, "dialogs"));
            File.WriteAllText(Path.Combine(mod, "pack.json"), "{ \"id\": \"Mod\", \"version\": 1, \"format\": 1 }");
            File.WriteAllText(Path.Combine(mod, "dialogs", "mine.json"), @"[
              // hand-written: replace e1, remove e2
              { ""id"": ""e1"", ""owner"": ""Gnar"", ""kind"": ""Exit"", ""title"": ""MY_END"",
                ""commands"": [ { ""op"": ""endDialog"" } ] },
              { ""id"": ""e2"", ""$remove"": true },
            ]");
            File.WriteAllText(Path.Combine(mod, "dialogs", "broken.json"), "[ { \"id\": ");

            var content = GameContent.Load(new IContentSource[] { new DirectoryContentSource(basePack), new DirectoryContentSource(mod) });

            Assert.That(content.Database.TryGet(new Id<DialogEntryDef>("e1"), out var e1), Is.True);
            Assert.That(e1.Kind, Is.EqualTo(DialogEntryKind.Exit));
            Assert.That(e1.Condition, Is.Null);
            Assert.That(e1.Commands.Single(), Is.TypeOf<EndDialog>());
            Assert.That(content.Database.TryGet(new Id<DialogEntryDef>("e2"), out _), Is.False);
            Assert.That(content.Problems, Has.Count.EqualTo(1).And.Some.Contains("broken.json"));
            Assert.That(content.Database.Packs.Select(p => p.Id), Is.EqualTo(new[] { "Base", "Mod" }));
        }

        [Test]
        public void Writer_RefusesToClearAFolderThatIsNotAPack()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "important.txt"), "x");
            Assert.Throws<IOException>(() => new ContentPackWriter(_root, GameSchema.Create()));
            Assert.That(File.Exists(Path.Combine(_root, "important.txt")), Is.True);
        }
    }
}
