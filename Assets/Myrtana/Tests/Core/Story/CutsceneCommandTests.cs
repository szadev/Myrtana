using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// story-2 (Docs/story-coverage.md): the cutscene/epilogue commands, the world-state commands and the quest counters
    /// for EnterArea, Arena and FreeEnclave quests.
    /// </summary>
    public class CutsceneCommandTests
    {
        // ------------------------------------------------------------------ cutscene / epilogue

        [Test]
        public void EpilogueSlide_ShowsPictureAndSubtitle_SaysTheLine_ThenPausesUntilTheTimeIsUp()
        {
            var f = new StoryFixture().Entry("Slide", DialogEntryKind.News, commands: new DialogCommand[]
            {
                new ShowPicture("G3_Epilog_01.dds"), new ShowSubtitle(new TextId("INFO_Slide")), StoryFixture.PlayerSays("INFO_Slide"),
                new SetFlag("Epilog_01"), new PlayMusic("Extro"), new WaitInScript(2000), new SetFlag("After"),
            });
            var shown = new List<string>();
            f.Events.Subscribe<StoryPictureShown>(e => shown.Add("picture " + e.Image));
            f.Events.Subscribe<StorySubtitleShown>(e => shown.Add("subtitle " + e.Text.Value));
            f.Events.Subscribe<StoryMusicRequested>(e => shown.Add("music " + e.Situation));

            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "INFO_Slide" }));
            Assert.That(shown, Is.EqualTo(new[] { "picture G3_Epilog_01.dds", "subtitle INFO_Slide", "music Extro" }));
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime));
            Assert.That(f.Flags.IsSet("Epilog_01"), Is.True);
            Assert.That(f.Flags.IsSet("After"), Is.False);

            f.Dialog.Tick(1.5f);
            Assert.That(f.Flags.IsSet("After"), Is.False, "2000 ms are not up after 1.5 s");
            f.Dialog.Tick(0.6f);
            Assert.That(f.Flags.IsSet("After"), Is.True);
            Assert.That(f.Dialog.IsActive, Is.False, "nothing else to say: the conversation ends");
        }

        [Test]
        public void ManyTimedSlides_RunThrough_WithoutTheLoopGuard()
        {
            var f = new StoryFixture();
            for (int i = 0; i < 80; i++)
                f.Entry("Slide" + i, DialogEntryKind.News, order: i, commands: new DialogCommand[] { new WaitInScript(100) });
            f.Dialog.Talk(StoryFixture.Npc);
            int ticks = 0;
            while (f.Dialog.IsActive && ticks++ < 200) f.Dialog.Tick(0.1f);
            Assert.That(ticks, Is.EqualTo(80));
            Assert.That(f.Log.Messages, Has.None.Contains("loops without player input"));
        }

        [Test]
        public void EndGame_EndsTheConversation_AndAsksForTheEnd()
        {
            var f = new StoryFixture().Entry("End", DialogEntryKind.News, commands: new DialogCommand[] { new EndGame(), new SetFlag("Never") });
            int ends = 0;
            f.Events.Subscribe<GameEndRequested>(_ => ends++);
            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(ends, Is.EqualTo(1));
            Assert.That(f.Dialog.IsActive, Is.False);
        }

        [Test]
        public void StartEffect_NamesTheResolvedEntity()
        {
            var f = new StoryFixture().Entry("Fx", DialogEntryKind.News, commands: new DialogCommand[]
            {
                new StartEffect(ActorRef.Partner, "eff_event_innos_burst_01"), new StartEffect(new ActorRef("Erzofen_Effekt"), "eff_event_fireoven_burst_01"),
            });
            var effects = new List<string>();
            f.Events.Subscribe<StoryEffectStarted>(e => effects.Add(e.Effect + "@" + e.At));
            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(effects, Is.EqualTo(new[] { "eff_event_innos_burst_01@Gnar", "eff_event_fireoven_burst_01@Erzofen_Effekt" }));
        }

        // ------------------------------------------------------------------ world state

        [Test]
        public void WorldStateCommands_ReachTheWorldDirector()
        {
            var world = new RecordingWorldDirector();
            var f = new StoryFixture { WorldDirector = world }.Entry("World", DialogEntryKind.News, commands: new DialogCommand[]
            {
                new SetSectorStatus("G3_Myrtana_01_Gotha_NPC_02", true), new OpenDoor("MoraSul_TempleDoor_Tester_01"),
                new SetEnclaveFlag("Silden", EnclaveFlag.Revolution, true), new SetEnclaveFlag(ActorRef.Partner.Value, EnclaveFlag.Raid, true),
                new StartRevolution(ActorRef.Partner), new SetClass(ActorRef.Partner, "Paladin"), new SetGuardStatus(ActorRef.Partner, false),
            });
            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(world.Calls, Is.EqualTo(new[]
            {
                "sector G3_Myrtana_01_Gotha_NPC_02 True", "door MoraSul_TempleDoor_Tester_01", "Silden Revolution True", "Gnar Raid True",
                "revolution Gnar", "class Gnar Paladin", "guard Gnar False",
            }));
        }

        // ------------------------------------------------------------------ quest counters

        static QuestDef QuestOf(string id, QuestObjectiveKind kind, string destination, params string[] targets) =>
            new QuestDef(new Id<QuestDef>(id), new TextId("QUEST_" + id), null,
                         new QuestObjective(kind, targets.Select(t => new QuestTarget(t, 1)).ToList(), new ActorId(destination)));

        [Test]
        public void EnterArea_CountsWhenTheEntityIsInTheArea_WhileCountersAreActive()
        {
            var f = new StoryFixture().Quest(QuestOf("Xardas_FindXardas", QuestObjectiveKind.EnterArea, "Xardas_Tower", "player"));
            var _ = f.Dialog;
            var areas = new FakeAreas();
            var counter = new QuestAreaCounter(f.Quests, new[] { QuestOf("Xardas_FindXardas", QuestObjectiveKind.EnterArea, "Xardas_Tower", "player") }, areas);
            var quest = new Id<QuestDef>("Xardas_FindXardas");
            f.Quests.TrySetStatus(quest, QuestStatus.Running);

            counter.Check();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Running), "the hero is not there yet");
            areas.Inside.Add(("player", "Xardas_Tower"));
            counter.Check();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Success));
        }

        [Test]
        public void Arena_OpponentDown_Wins_HeroDownOrLeaving_Loses()
        {
            var def = QuestOf("Curtis_Arena_Goran", QuestObjectiveKind.Arena, "Faring_Arena_Village", "Goran");
            var f = new StoryFixture().Quest(def);
            var _ = f.Dialog;
            var areas = new FakeAreas();
            bool fighting = false;
            var ended = new List<ActorId>();
            var messages = new List<TextId>();
            f.Events.Subscribe<GameMessage>(m => messages.Add(m.Text));
            var counter = new QuestArenaCounter(f.Quests, new[] { def }, f.Events, areas, id => fighting && id.Name == "Goran", ended.Add);
            var quest = def.Id;
            var goran = new ActorId("Goran");

            f.Quests.TrySetStatus(quest, QuestStatus.Running);
            f.Events.Publish(new PlayerDefeated(false));
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Running), "a knock-out outside the arena fight does not count");

            fighting = true;
            areas.Inside.Add(("player", "Faring_Arena_Village"));
            areas.Inside.Add(("Goran", "Faring_Arena_Village"));
            counter.Check();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Running));
            areas.Inside.Remove(("player", "Faring_Arena_Village"));
            counter.Check();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Lost), "the hero left the ring");
            Assert.That(messages, Is.EqualTo(new[] { QuestArenaCounter.ArenaWarning }), "CP: GO_ArenaWarning");
            Assert.That(ended, Is.EqualTo(new[] { goran }), "CP: the opponent stops attacking");

            Assert.That(counter.LastLoss, Does.Contain("left the arena Faring_Arena_Village"), "fix-46: the reason is kept for the logs");

            f.Quests.TrySetStatus(quest, QuestStatus.Running); // Reactivator: "I want to fight Goran again"
            f.Events.Publish(new PlayerDefeated(false));
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Lost), "the hero went down in the fight");
            Assert.That(counter.LastLoss, Does.Contain("defeated"));

            f.Quests.TrySetStatus(quest, QuestStatus.Running);
            f.Events.Publish(new ActorDefeated(goran, true, false, 5, false, "Human"));
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Success));
            counter.Dispose();
        }

        [Test]
        public void ArenaZone_CentreAndEdgeDistance()
        {
            // fix-46 r28 ishtar_01: Arena_Ishtar's outline (13 x 10 m); the runner's tp keeps the hero 2 m from its edge on
            // the opponent's centre side
            var outline = new[] { -2068.788f, -1589.0985f, -2072.9285f, -1579.9226f, -2060.5466f, -1574.2288f, -2055.9688f, -1582.7339f };
            Assert.That(Myrtana.Simulation.ZoneMap.Centre(outline, out float cx, out float cz), Is.True);
            Assert.That(cx, Is.EqualTo(-2064.56f).Within(0.01f));
            Assert.That(cz, Is.EqualTo(-1581.50f).Within(0.01f));
            Assert.That(Myrtana.Simulation.ZoneMap.Contains(outline, cx, cz), Is.True);
            Assert.That(Myrtana.Simulation.ZoneMap.EdgeDistance(outline, cx, cz), Is.GreaterThan(4f));
            Assert.That(Myrtana.Simulation.ZoneMap.EdgeDistance(outline, -2067.0f, -1580.8f), Is.LessThan(3.5f),
                        "r28: the tp spot 2.5 m in front of Emet was 3.3 m from the edge");
            Assert.That(Myrtana.Simulation.ZoneMap.Centre(new float[0], out _, out _), Is.False);
        }

        [Test]
        public void Arena_OnlyLeavingCounts_NotStandingOutsideBeforeSteppingIn()
        {
            var def = QuestOf("Curtis_Arena_Goran", QuestObjectiveKind.Arena, "Faring_Arena_Village", "Goran");
            var f = new StoryFixture().Quest(def);
            var _ = f.Dialog;
            var areas = new FakeAreas();
            bool fighting = true;
            var counter = new QuestArenaCounter(f.Quests, new[] { def }, f.Events, areas, id => fighting && id.Name == "Goran");
            var quest = def.Id;
            f.Quests.TrySetStatus(quest, QuestStatus.Running);

            // Goran starts from his waiting spot beside the ring, the hero is not in yet either.
            counter.Check();
            counter.Check();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Running), "outside from the start is not leaving");

            areas.Inside.Add(("player", "Faring_Arena_Village"));
            counter.Check();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Running), "Goran still on his way in");
            areas.Inside.Add(("Goran", "Faring_Arena_Village"));
            counter.Check();
            areas.Inside.Remove(("Goran", "Faring_Arena_Village"));
            counter.Check();
            Assert.That(f.Quests.StatusOf(quest), Is.EqualTo(QuestStatus.Success), "Goran stepped in and left the ring");
            counter.Dispose();
        }

        [Test]
        public void FreeEnclave_CountsEachLiberatedEnclave()
        {
            var one = QuestOf("Ardea_Revolution", QuestObjectiveKind.FreeEnclave, null, "Ardea");
            var many = QuestOf("Innos_FreeMyrtana", QuestObjectiveKind.FreeEnclave, null, "Faring", "Geldern");
            var f = new StoryFixture().Quest(one).Quest(many);
            var _ = f.Dialog;
            using (new QuestEnclaveCounter(f.Quests, new[] { one, many }, f.Events))
            {
                f.Quests.TrySetStatus(many.Id, QuestStatus.Running);
                f.Events.Publish(new EnclaveLiberated("Ardea"));
                Assert.That(f.Quests.StatusOf(one.Id), Is.EqualTo(QuestStatus.Success), "counts while Open as in G3");
                f.Events.Publish(new EnclaveLiberated("Faring"));
                Assert.That(f.Quests.StatusOf(many.Id), Is.EqualTo(QuestStatus.Running));
                f.Events.Publish(new EnclaveLiberated("Geldern"));
                Assert.That(f.Quests.StatusOf(many.Id), Is.EqualTo(QuestStatus.Success));
            }
        }

        [Test]
        public void KillQuest_CountsNamesakesOfTheTarget()
        {
            var def = new QuestDef(new Id<QuestDef>("Kaelin_KillOrks"), new TextId("QUEST_Kaelin_KillOrks"), null,
                                   new QuestObjective(QuestObjectiveKind.Kill, new[] { new QuestTarget("Orc_Raider_Elite_02_Fireclan", 2) }, default));
            var f = new StoryFixture().Quest(def);
            var _ = f.Dialog;
            f.Quests.TrySetStatus(def.Id, QuestStatus.Running);
            f.Events.Publish(new ActorDefeated(new ActorId("Orc_Raider_Elite_02_Fireclan"), true, true, 10, false, "Orc"));
            f.Events.Publish(new ActorDefeated(new ActorId("Orc_Raider_Elite_02_Fireclan#d97bc176"), true, true, 10, false, "Orc"));
            Assert.That(f.Quests.StatusOf(def.Id), Is.EqualTo(QuestStatus.Success), "G3 counts kills by entity name");
        }

        sealed class FakeAreas : IAreaLocator
        {
            public readonly HashSet<(string Actor, string Area)> Inside = new HashSet<(string, string)>();

            public bool TryIsInside(ActorId actor, string area, out bool inside)
            {
                inside = Inside.Contains((actor.Name, area));
                return true;
            }
        }
    }

    public sealed class RecordingWorldDirector : IWorldDirector
    {
        public readonly List<string> Calls = new List<string>();

        public void SetSectorEnabled(string sector, bool enabled) => Calls.Add($"sector {sector} {enabled}");
        public bool OpenDoor(string door)
        {
            Calls.Add($"door {door}");
            return true;
        }
        public void SetEnclaveFlag(string enclaveOrMember, EnclaveFlag flag, bool value) => Calls.Add($"{enclaveOrMember} {flag} {value}");
        public void StartRevolution(ActorId member) => Calls.Add($"revolution {member}");
        public void SetClass(ActorId npc, string npcClass) => Calls.Add($"class {npc} {npcClass}");
        public void SetImmortal(ActorId npc, bool immortal) => Calls.Add($"immortal {npc} {immortal}");
        public bool Wear(ActorId npc, string item)
        {
            Calls.Add($"wear {npc} {item}");
            return item.StartsWith("It_", System.StringComparison.Ordinal);
        }
        public void SetGuardActive(ActorId guard, bool active) => Calls.Add($"guard {guard} {active}");
        /// <summary>The guards that may warn (stage 1 or 2) in this test.</summary>
        public readonly HashSet<string> MayWarn = new HashSet<string>();
        public bool GateGuardMayWarn(ActorId guard, bool second) => MayWarn.Contains($"{guard} {(second ? 2 : 1)}");
        public void GateGuardWarned(ActorId guard, bool second) => Calls.Add($"warned {guard} {(second ? 2 : 1)}");
    }
}
