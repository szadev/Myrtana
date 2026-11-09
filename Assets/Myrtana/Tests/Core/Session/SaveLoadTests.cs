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
    public class SaveLoadTests
    {
        static readonly Id<QuestDef> Quest = new Id<QuestDef>("Gnar_EnterCastle");

        SessionFixture _f;

        [SetUp]
        public void SetUp()
        {
            _f = new SessionFixture().Quest(Quest.Name);
            _f.Add(StorySchema.DialogFolder, Entry("hello", DialogEntryKind.News, false, new SetFlag("Gnar_NotEnter"), new StartQuest(Quest)));
        }

        [TearDown]
        public void TearDown() => _f.Dispose();

        static DialogEntryDef Entry(string id, DialogEntryKind kind, bool repeatable, params DialogCommand[] commands) =>
            new DialogEntryDef(new Id<DialogEntryDef>(id), SessionFixture.Npc, default, kind, 0, default,
                               repeatable, false, false, null, null, null, commands);

        GameSession PlayedGame()
        {
            var game = _f.NewGame();
            game.Dialog.Talk(SessionFixture.Npc); // the News entry plays, nothing else to say: the dialog closes
            game.Tick(10f);
            game.Random.Next(100);
            return game;
        }

        [Test]
        public void Load_RestoresStoryState()
        {
            var original = PlayedGame();
            Assert.That(original.Dialog.IsActive, Is.False);

            var loaded = _f.Reload(original);

            Assert.That(loaded.Flags.IsSet("Gnar_NotEnter"), Is.True);
            Assert.That(loaded.Quests.StatusOf(Quest), Is.EqualTo(QuestStatus.Running));
            Assert.That(loaded.Actors.HasTalkedToPlayer(SessionFixture.Npc), Is.True);
            Assert.That(loaded.Clock.GameSeconds, Is.EqualTo(original.Clock.GameSeconds));
            Assert.That(loaded.PlaySeconds, Is.EqualTo(10).Within(1e-6));
            Assert.That(loaded.Dialog.CanTalkTo(SessionFixture.Npc), Is.True, "the NPC still owns the topic");
            loaded.Dialog.Talk(SessionFixture.Npc);
            Assert.That(loaded.Dialog.IsActive, Is.False, "the one-shot News entry was given before the save");
        }

        [Test]
        public void Load_RestoresHeroFameAndQuestLog()
        {
            var original = PlayedGame();
            original.Progression.AwardXp(3200);
            original.Progression.Boost("STR");
            original.Progression.LearnSkill("Perk_Learn");
            original.Fame.Add(FameKind.Enclave, "Faring", 4);
            original.QuestLog.Add(Quest, new QuestLogEntry(SessionFixture.Npc, new TextId("LINE")));

            var loaded = _f.Reload(original);

            Assert.That(loaded.Hero.Level, Is.EqualTo(3));
            Assert.That(loaded.Hero.Xp, Is.EqualTo(3200));
            Assert.That(loaded.Hero.LearningPoints, Is.EqualTo(20));
            Assert.That(loaded.Hero.Stats.Value("STR"), Is.EqualTo(125));
            Assert.That(loaded.Hero.Stats.Maximum("HP"), Is.EqualTo(250));
            Assert.That(loaded.Hero.Knows("Perk_Learn"), Is.True);
            Assert.That(loaded.Fame.Of(FameKind.Enclave, "Faring"), Is.EqualTo(4));
            Assert.That(loaded.QuestLog.EntriesOf(Quest).Single().Text.Value, Is.EqualTo("LINE"));
        }

        [Test]
        public void ProgressionRules_SurviveTheJsonPack()
        {
            var game = _f.NewGame();
            Assert.That(game.Hero.Stats.Value("SP"), Is.EqualTo(150));
            Assert.That(game.Progression.Rules.AttributeBoosts["XAR"].Default, Is.Empty);
            Assert.That(game.Progression.Rules.KillXp.Livestock.AlternativeBalancing, Is.EqualTo(20));
            Assert.That(_f.Content.Problems, Is.Empty);
        }

        [Test]
        public void SaveLoadSave_GivesIdenticalBytes()
        {
            var played = PlayedGame();
            played.Progression.AwardXp(5000);
            played.Fame.Add(FameKind.Political, "Orc", 10);
            var first = SessionFixture.Save(played);
            var second = SessionFixture.Save(_f.Load(first));
            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void SectionsMissingFromTheSave_KeepTheirNewGameState()
        {
            var bytes = SessionFixture.Save(PlayedGame()); // written without the host section
            var host = new HostCounter { Value = 5 };
            _f.HostSections.Add(host);
            _f.Load(bytes);
            Assert.That(host.Value, Is.EqualTo(5));
        }

        [Test]
        public void HostSections_AreSavedAndLoaded()
        {
            var host = new HostCounter { Value = 42 };
            _f.HostSections.Add(host);
            var bytes = SessionFixture.Save(PlayedGame());
            host.Value = 0;
            _f.Load(bytes);
            Assert.That(host.Value, Is.EqualTo(42));
        }

        [Test]
        public void Saving_IsBlockedDuringDialogAndByHostBlockers()
        {
            _f.Add(StorySchema.DialogFolder, Entry("talk", DialogEntryKind.Choice, true), Entry("talk2", DialogEntryKind.Choice, true));
            bool falling = false;
            _f.HostBlockers.Add(SaveBlocker.When(() => falling, "falling"));
            var game = _f.NewGame();
            Assert.That(game.SaveBlockReason, Is.Null);

            game.Dialog.Talk(SessionFixture.Npc);
            game.Dialog.CompleteLine();
            Assert.That(game.Dialog.IsActive, Is.True);
            Assert.That(game.SaveBlockReason, Is.EqualTo(GameSession.BlockedByDialog));
            Assert.Throws<InvalidOperationException>(() => game.Save(new MemoryStream(), "x", default));

            game.Dialog.Abort();
            falling = true;
            Assert.That(game.SaveBlockReason, Is.EqualTo("falling"));
        }

        [Test]
        public void Header_CarriesTitlePlayTimeAndPacks()
        {
            var header = SaveFile.ReadHeader(new MemoryStream(SessionFixture.Save(PlayedGame())));
            Assert.That(header.Title, Is.EqualTo("test"));
            Assert.That(header.PlaySeconds, Is.EqualTo(10).Within(1e-6));
            Assert.That(header.Packs[0].Id, Is.EqualTo("Test"));
            Assert.That(header.SavedAtUtc.Year, Is.EqualTo(2026));
        }

        sealed class HostCounter : ISaveParticipant
        {
            public int Value;
            public string Section => "host.counter";
            public int Version => 1;
            public void Write(ISaveWriter w) => w.WriteInt32(Value);
            public void Read(ISaveReader r, int version) => Value = r.ReadInt32();
        }

        [Test]
        public void Load_RestoresQuickSlots()
        {
            var original = _f.NewGame();
            original.Items.Inventories.Of(ActorId.Player).Add("Apple", 2);
            original.Items.QuickSlots.Assign(9, "Apple");

            var loaded = _f.Reload(original);

            Assert.That(loaded.Items.QuickSlots.ItemAt(9), Is.EqualTo("Apple"));
            Assert.That(loaded.Items.QuickSlots.SlotOf("Apple"), Is.EqualTo(9));
        }

        static readonly SpellDef SummonSkeleton =
            new SpellDef("Spl_SummonSkeleton", new[] { "It_Spl_SummonSkeleton" }, null, SpellCastType.Regular, SpellTarget.Caster, SpellKind.Summon,
                         SpellDeity.Beliar, 60, 60, SpellDelivery.Instant, "MagicSummonSkeleton", "Summon", 0, "Skeleton_Sword", null, null);

        /// <summary>Every participant added since slice 2 in a non-default state: load gives the same state and the same bytes.</summary>
        [Test]
        public void SaveEverything_Load_RestoresTheStateAndGivesIdenticalBytes()
        {
            _f.Add(RulesSchema.MagicRulesFolder, G3MagicImporter.Rules())
              .Add(SimulationSchema.NpcFolder,
                   new NpcDef(new ActorId("Skeleton_Sword#1"), new TextId("FO_Skeleton"), null, "Skeleton", "Hero_Body_Skeleton",
                              new WorldPose(500, 0, 500, 0), combat: new NpcCombatDef(0, 30, "Skeleton", humanoid: false)));
            var game = PlayedGame();
            var skeleton = game.Summoning.Summon(ActorId.Player, SummonSkeleton, 100, new WorldPose(10, 0, 10, 0)).Single();
            game.Combat.Vitals.AddHitPoints(skeleton, -5);
            game.Magic.Status.Poison(ActorId.Player, 20f);
            game.Magic.Status.Set(SessionFixture.Npc, ActorStatus.Diseased);
            game.Combat.Vitals.KnockOut(SessionFixture.Npc, byPlayerParty: true);
            game.Bookstands.MarkUsed("wisdom1");
            game.Bookstands.SetPagesRead("BS_BookOfFlood", 2);
            game.Music.MarkVictoryPlayed("Ardea");
            game.Enclaves.Set("Ardea", new EnclaveState(EnclaveStatus.Flee, false, true));
            Assert.That(game.SaveBlockReason, Is.Null);
            var bytes = SessionFixture.Save(game);

            var loaded = _f.Load(bytes);

            Assert.That(loaded.Actors.Exists(skeleton), Is.True, "summons are saved like every G3 entity");
            Assert.That(loaded.Summoning.SummonerOf(skeleton), Is.EqualTo(ActorId.Player));
            Assert.That(loaded.Party.Members.TypeOf(skeleton), Is.EqualTo(PartyMemberType.Summoned));
            Assert.That(loaded.Npcs.TryGet(skeleton, out var agent), Is.True);
            game.Npcs.TryGet(skeleton, out var before);
            Assert.That(agent.Pose.DistanceTo(before.Pose), Is.LessThan(0.01f));
            Assert.That(loaded.Combat.Vitals.HitPoints(skeleton), Is.EqualTo(game.Combat.Vitals.HitPoints(skeleton)));
            Assert.That(loaded.Magic.Status.PoisonSecondsLeft(ActorId.Player), Is.EqualTo(20f).Within(1e-4));
            Assert.That(loaded.Magic.Status.Has(SessionFixture.Npc, ActorStatus.Diseased), Is.True);
            Assert.That(loaded.Combat.Vitals.StateOf(SessionFixture.Npc), Is.EqualTo(VitalState.Down));
            Assert.That(loaded.Bookstands.IsUsed("wisdom1") && loaded.Bookstands.PagesRead("BS_BookOfFlood") == 2, Is.True);
            Assert.That(loaded.Music.VictoryPlayed, Is.EqualTo(new[] { "Ardea" }));
            Assert.That(loaded.Enclaves.Of("Ardea"), Is.EqualTo(new EnclaveState(EnclaveStatus.Flee, false, true)));
            Assert.That(loaded.SavedRules.AlternativeBalancing, Is.EqualTo(game.Settings.Rules.AlternativeBalancing));
            Assert.That(_f.Log.Messages, Has.None.Contains("cannot be restored"));

            Assert.That(SessionFixture.Save(loaded), Is.EqualTo(bytes), "a loaded game saves to the same bytes");

            var again = _f.Reload(loaded);
            again.Summoning.Summon(ActorId.Player, SummonSkeleton, 100, default);
            Assert.That(again.Actors.Exists(skeleton), Is.True, "the dead creature's body stays a while");
            Assert.That(again.Combat.Vitals.StateOf(skeleton), Is.EqualTo(VitalState.Dead), "a new summon still replaces the restored one");
        }

        static string Rolls(GameSession s, int n = 12)
        {
            var b = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) b.Append(s.Random.Next(1000)).Append(',');
            return b.ToString();
        }

        [Test]
        public void Fix119_TwoLoadsOfOneSave_RollAnew()
        {
            // G3 rolls with rand(), which no save holds: a quickload in front of a pickpocket, a lock, a fight or a chest rolls anew
            using var f = new SessionFixture().Seed(null);
            var game = f.NewGame();
            game.Random.Next(100);
            var save = SessionFixture.Save(game);
            var first = f.Load(save);
            var second = f.Load(save);
            Assert.That(first.RandomSeedPinned || second.RandomSeedPinned, Is.False);
            Assert.That(first.RandomSeed, Is.Not.EqualTo(second.RandomSeed), "every load is a session with a seed of its own");
            Assert.That(Rolls(first), Is.Not.EqualTo(Rolls(second)), "two loads of one save must not replay the same rolls");
            Assert.That(Rolls(f.Load(save)), Is.Not.EqualTo(Rolls(game)), "nor the rolls the saved game made after the save");
        }

        [Test]
        public void Fix119_ANewGame_GetsAFreshSeed_NotOne()
        {
            using var f = new SessionFixture().Seed(null);
            var a = f.NewGame();
            var b = f.NewGame();
            Assert.That(a.RandomSeed, Is.Not.EqualTo(b.RandomSeed));
            Assert.That(a.RandomSeed == 1 && b.RandomSeed == 1, Is.False, "not the fixed seed 1 every new game had before fix-119");
            Assert.That(Rolls(a), Is.Not.EqualTo(Rolls(b)));
        }

        [Test]
        public void Fix119_APinnedSeed_IsReproducible()
        {
            using var f = new SessionFixture().Seed(42);
            var a = f.NewGame();
            var b = f.NewGame();
            Assert.That(a.RandomSeedPinned && a.RandomSeed == 42, Is.True);
            Assert.That(Rolls(a), Is.EqualTo(Rolls(b)), "two new games with one pinned seed roll alike");
            var save = SessionFixture.Save(a);
            Assert.That(Rolls(f.Load(save)), Is.EqualTo(Rolls(f.Load(save))), "two pinned loads of one save roll alike");

            // the scenario runner pins a running session (GameSession.Reseed)
            var c = f.NewGame();
            var d = new SessionFixture().Seed(null);
            using (d)
            {
                var e = d.NewGame();
                c.Reseed(5);
                e.Reseed(5);
                Assert.That(Rolls(c), Is.EqualTo(Rolls(e)));
            }
        }

        [Test]
        public void Fix119_AnOldSave_StillLoads_ItsRandomStateIsSkipped()
        {
            // before fix-119 core.time held the generator state; the format is unchanged, the word is now a constant
            using var f = new SessionFixture().Seed(null);
            var game = f.NewGame();
            game.Tick(5f);
            var bytes = SessionFixture.Save(game);
            var loaded = f.Load(bytes);
            Assert.That(loaded.Clock.GameSeconds, Is.EqualTo(game.Clock.GameSeconds));
            Assert.That(loaded.PlaySeconds, Is.EqualTo(5).Within(1e-6));
            Assert.That(SessionFixture.Save(loaded), Is.EqualTo(bytes), "the reserved word does not depend on the session's seed");
            Assert.That(f.Log.Messages, Has.None.Contains("cannot be restored"));
        }

        [Test]
        public void Fix119_SeedText_PinsANumber_LiveIsFresh()
        {
            Assert.That(SessionSeed.Parse("7"), Is.EqualTo(7UL));
            Assert.That(SessionSeed.Parse(" 0x1F "), Is.EqualTo(31UL));
            Assert.That(SessionSeed.Parse("live"), Is.Null);
            Assert.That(SessionSeed.Parse(""), Is.Null);
            Assert.That(SessionSeed.Parse("-3"), Is.Null);
            Assert.That(SessionSeed.Resolve(9), Is.EqualTo(9UL));
            Assert.That(SessionSeed.Fresh(), Is.Not.EqualTo(SessionSeed.Fresh()));
        }
    }
}
