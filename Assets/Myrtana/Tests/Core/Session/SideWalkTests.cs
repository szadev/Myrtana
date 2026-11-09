using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// The side-quest walk (Docs/story-walk.md §5): CP script triggers, chest quest destinations, and the walk rules for
    /// several-target reports, lost quests and script triggers.
    /// </summary>
    public class SideWalkTests
    {
        sealed class Areas : IAreaLocator
        {
            public readonly HashSet<(string, string)> Inside = new HashSet<(string, string)>();
            public bool TryIsInside(ActorId actor, string area, out bool inside)
            {
                inside = Inside.Contains((actor.Name, area));
                return true;
            }
        }

        static readonly StoryTriggerRules Rules = new StoryTriggerRules(StoryTriggerRules.DefinitionId,
            new[] { new ItemReceivedTrigger("It_Book_CPT_05", new[] { "CPT_Quest_NPC_05" }, new[] { "eff_x" }) },
            new[] { new ItemUsedTrigger("It_Stollengrollen", 200, new[] { "Ugolf_StollengrollenSuccess" }, new[] { "Ugolf_StollengrollenRunning" }) },
            new[] { new AreaEnteredTrigger("Lago_Slave_03", "Nav_Zone_Lago_Stollen", new[] { "Emaciated_Ready" }) });

        [Test]
        public void Triggers_ReceivedItemSwitchesSector_UsedItemNeedsStamina_AreaSetsFlagOncePerEntry()
        {
            var events = new EventBus();
            var flags = new GameEventFlags();
            var sectors = new List<string>();
            var effects = new List<string>();
            var areas = new Areas();
            int stamina = 150;
            events.Subscribe<StoryEffectStarted>(e => effects.Add(e.Effect));
            using var triggers = new StoryTriggers(Rules, events, flags, s => { sectors.Add(s); return true; }, areas, () => stamina);

            events.Publish(new ItemsTransferred(new ActorId("Stalker"), new ActorId("Gnar"), "It_Book_CPT_05", 1));
            Assert.That(sectors, Is.Empty, "only the hero's books count");
            events.Publish(new ItemsTransferred(new ActorId("Stalker"), ActorId.Player, "It_Book_CPT_05", 1));
            Assert.That(sectors, Is.EqualTo(new[] { "CPT_Quest_NPC_05" }));
            Assert.That(effects, Is.EqualTo(new[] { "eff_x" }));

            flags.Set("Ugolf_StollengrollenRunning");
            events.Publish(new ItemUsed("It_Stollengrollen"));
            Assert.That(flags.IsSet("Ugolf_StollengrollenSuccess"), Is.False, "150 maximum stamina is too little");
            stamina = 200;
            events.Publish(new ItemUsed("It_Stollengrollen"));
            Assert.That(flags.IsSet("Ugolf_StollengrollenSuccess"), Is.True);
            Assert.That(flags.IsSet("Ugolf_StollengrollenRunning"), Is.False);

            triggers.Check();
            Assert.That(flags.IsSet("Emaciated_Ready"), Is.False);
            areas.Inside.Add(("Lago_Slave_03", "Nav_Zone_Lago_Stollen"));
            triggers.Check();
            Assert.That(flags.IsSet("Emaciated_Ready"), Is.True);
            flags.Clear("Emaciated_Ready");
            triggers.Check();
            Assert.That(flags.IsSet("Emaciated_Ready"), Is.False, "fires once per entry");
        }

        [Test]
        public void Importer_StatesTheCpScriptTriggers()
        {
            var rules = G3StoryTriggersImporter.Rules();
            Assert.That(rules.ItemReceived.Select(t => t.Item + ">" + t.Sectors.Single()),
                        Is.EqualTo(new[] { "It_Book_CPT_01>CPT_Quest_NPC_01", "It_Book_CPT_02>CPT_Quest_NPC_02", "It_Book_CPT_03>CPT_Quest_NPC_03",
                                           "It_Book_CPT_04>CPT_Quest_NPC_04", "It_Book_CPT_Teleport>CPT_Quest_NPC_05" }));
            Assert.That(rules.ItemUsed.Single().MinStaminaMax, Is.EqualTo(200));
            Assert.That(rules.AreaEntered.Single().SetFlags, Is.EqualTo(new[] { "Emaciated_Ready" }));
        }

        [Test]
        public void Session_HeroReceivingACptBook_SwitchesTheQuestSectorOn()
        {
            using var f = new SessionFixture()
                .Add(StorySchema.TriggerFolder, Rules)
                .Add(SimulationSchema.SectorFolder, new WorldSectorDef("CPT_Quest_NPC_05", false));
            var session = f.NewGame();
            Assert.That(session.WorldState.Sectors.IsEnabled("CPT_Quest_NPC_05"), Is.False);
            session.Items.Inventories.Give(ActorId.Player, "It_Book_CPT_05", 1);
            Assert.That(session.WorldState.Sectors.IsEnabled("CPT_Quest_NPC_05"), Is.True);
        }

        [Test]
        public void PlunderFromAChest_CountsItsContainerAsTheNamedChest()
        {
            var q = new Id<QuestDef>("KapDun_Chest");
            var plunder = new QuestDef(q, new TextId("QUEST_X"), null,
                                       new QuestObjective(QuestObjectiveKind.Plunder, new[] { new QuestTarget("It_Gold", 500) }, new ActorId("Esiel_Chest")));
            var f = new StoryFixture().Quest(plunder);
            var _ = f.Dialog;
            f.Quests.TrySetStatus(q, QuestStatus.Running);
            var container = InteractiveObjects.ContainerOf("821368a5");
            using var counter = new QuestItemCounter(f.Quests, new[] { plunder }, f.Events, a => a == container ? new ActorId("Esiel_Chest") : a);

            f.Events.Publish(new ItemsTransferred(container, ActorId.Player, "It_Gold", 500));

            Assert.That(f.Quests.StatusOf(q), Is.EqualTo(QuestStatus.Success));
        }

        [Test]
        public void Walk_SeveralTargetReportNeedsEveryReport_FollowQuestsCanBeLost_TriggersProduceFacts()
        {
            var report = new QuestDef(new Id<QuestDef>("Flint_BoozeDelivery"), new TextId("Q"), null,
                                      new QuestObjective(QuestObjectiveKind.Report, new[] { new QuestTarget("Gnar", 1), new QuestTarget("Gorok", 1) }, default));
            var follow = new QuestDef(new Id<QuestDef>("Mort_GuideFire"), new TextId("Q"), null,
                                      new QuestObjective(QuestObjectiveKind.FollowNpc, new[] { new QuestTarget("Gnar", 1) }, new ActorId("FP_Feuerclan")));
            DialogEntryDef Entry(string id, string owner, params DialogCommand[] commands) =>
                new DialogEntryDef(new Id<DialogEntryDef>(id), new ActorId(owner), default, DialogEntryKind.Choice, 0, new TextId("T"), false, false, false,
                                   null, null, null, commands);
            using var f = new SessionFixture()
                .Add(StorySchema.QuestFolder, report, follow)
                .Add(StorySchema.TriggerFolder, Rules)
                .Add(StorySchema.DialogFolder,
                     Entry("start", "Gnar", new StartQuest(report.Id), new StartQuest(follow.Id)),
                     Entry("gnarReports", "Gnar", new ReportToQuest(report.Id, ActorRef.Partner)));
            var builder = new StoryWalkBuilder(f.Content.Database);
            var walk = builder.Build().Run();

            Assert.That(walk.Has(WalkFacts.Reported(report.Id.Name, "Gnar")), Is.True);
            Assert.That(walk.Has(WalkFacts.Quest(report.Id.Name, "Success")), Is.False, "Gorok has no report entry");
            Assert.That(walk.Has(WalkFacts.Quest(follow.Id.Name, "Lost")), Is.True);
            Assert.That(walk.Rules.Single(r => r.Id == "received It_Book_CPT_05").Effects, Is.EqualTo(new[] { "sector:CPT_Quest_NPC_05" }));

            var check = new QuestCheck(f.Content.Database, builder, walk).Check(report);
            Assert.That(check.Starts && !check.Succeeds, Is.True);
            Assert.That(check.Roots, Has.Some.Contains("Gorok"));
        }
    }
}
