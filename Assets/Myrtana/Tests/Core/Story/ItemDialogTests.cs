using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class ItemDialogTests
    {
        static readonly Id<QuestDef> BuyFur = new Id<QuestDef>("Gnar_BuyFur");

        static StoryFixture Fixture() => new StoryFixture()
            .Define(ItemTests.Item("It_Gold", 1))
            .Define(ItemTests.Item("It_Wolf_Fur", 50, kind: "TrophyFur"))
            .Define(new SharedLines(SharedLines.DefinitionId, "SVM_{voice}_{line}"));

        static DialogEntryDef Entry(string id, Condition condition = null, IReadOnlyList<PreCheck> preChecks = null, params DialogCommand[] commands) =>
            new DialogEntryDef(new Id<DialogEntryDef>(id), StoryFixture.Npc, default, DialogEntryKind.Choice, 0, default, true, false, false,
                               null, condition, preChecks, commands);

        [Test]
        public void GoldCost_RefusesWithMessageAndSharedLine_ThenPays()
        {
            var f = Fixture();
            var refusal = new DialogCommand[] { new ShowMessage(new TextId("GO_NotEnoughGold")), new SayShared(ActorRef.Partner, "NotEnoughGold") };
            f.Entry(Entry("bribe", null, new PreCheck[] { new MinGold(100, refusal) },
                          new GiveItems(ActorRef.Player, ActorRef.Partner, "It_Gold", 100), StoryFixture.NpcSays("Thanks")));
            f.Entry("other");
            var messages = new List<GameMessage>();
            f.Events.Subscribe<GameMessage>(messages.Add);

            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("bribe");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "SVM_Orc_Warrior_Hard_NotEnoughGold" }));
            Assert.That(f.Presenter.Lines.Last().Voice, Is.EqualTo("SVM_Orc_Warrior_Hard_NotEnoughGold"));
            Assert.That(messages.Single().Text.Value, Is.EqualTo("GO_NotEnoughGold"));

            f.Items.Inventories.Of(ActorId.Player).Add("It_Gold", 150);
            f.Choose("bribe");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "Thanks" }));
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Gold"), Is.EqualTo(50));
            Assert.That(f.Items.Inventories.Of(StoryFixture.Npc).Count("It_Gold"), Is.EqualTo(100));
        }

        [Test]
        public void ItemDelivery_CountsForTheQuestAndSucceedsIt()
        {
            var f = Fixture().Quest(new QuestDef(BuyFur, new TextId("QUEST_FUR"), null,
                new QuestObjective(QuestObjectiveKind.HasItems, new[] { new QuestTarget("It_Wolf_Fur", 20) }, StoryFixture.Npc),
                new QuestRewards(null, null, 2000)));
            var running = new QuestStatusIs(BuyFur, new[] { QuestStatus.Running });
            f.Entry(Entry("deliver", new AllOf(new Condition[] { running, new HasItems(ActorRef.Player, "It_Wolf_Fur", 20) }), null,
                          new GiveItems(ActorRef.Player, ActorRef.Partner, "It_Wolf_Fur", 20)));
            f.Entry("other", repeatable: true);
            f.Entry("other2", repeatable: true);
            var _ = f.Dialog;
            f.Quests.TrySetStatus(BuyFur, QuestStatus.Running);
            f.Items.Inventories.Of(ActorId.Player).Add("It_Wolf_Fur", 19);

            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.OfferedIds(), Does.Not.Contain("deliver"));
            f.Dialog.Abort();

            f.Items.Inventories.Of(ActorId.Player).Add("It_Wolf_Fur", 1);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("deliver");
            Assert.That(f.Quests.StatusOf(BuyFur), Is.EqualTo(QuestStatus.Success));
            Assert.That(f.Hero.Xp, Is.EqualTo(2000));
        }

        /// <summary>G3 OnDelivery: a Delivery entry without Give lines takes what the quest still lacks (part deliveries count).</summary>
        [Test]
        public void DeliverQuestItems_TakesOnlyWhatIsStillMissing()
        {
            var f = Fixture().Quest(new QuestDef(BuyFur, new TextId("QUEST_FUR"), null,
                new QuestObjective(QuestObjectiveKind.HasItems, new[] { new QuestTarget("It_Wolf_Fur", 20) }, StoryFixture.Npc),
                new QuestRewards(null, null, 0)));
            f.Entry(Entry("part", null, null, new DeliverQuestItems(BuyFur, ActorRef.Partner)));
            f.Entry("other", repeatable: true);
            var _ = f.Dialog;
            f.Quests.TrySetStatus(BuyFur, QuestStatus.Running);
            var hero = f.Items.Inventories.Of(ActorId.Player);
            hero.Add("It_Wolf_Fur", 8);

            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("part");
            f.Dialog.Abort();
            Assert.That(hero.Count("It_Wolf_Fur"), Is.EqualTo(0));
            Assert.That(f.Quests.StatusOf(BuyFur), Is.EqualTo(QuestStatus.Running));

            hero.Add("It_Wolf_Fur", 15);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("part");
            Assert.That(hero.Count("It_Wolf_Fur"), Is.EqualTo(3), "only the 12 still missing are taken");
            Assert.That(f.Items.Inventories.Of(StoryFixture.Npc).Count("It_Wolf_Fur"), Is.EqualTo(20));
            Assert.That(f.Quests.StatusOf(BuyFur), Is.EqualTo(QuestStatus.Success));
        }

        /// <summary>
        /// fix-183, G3 AreConditionsFulfilled case 8/9: Delivery and PartDelivery count what the destination already got.
        /// The part delivery needs a piece of an item still missing; run without one it takes nothing and changes nothing.
        /// </summary>
        [Test]
        public void QuestItemConditions_CountTheDelivered_PartDeliveryWithoutNeededItemsIsHarmless()
        {
            var f = Fixture().Quest(new QuestDef(BuyFur, new TextId("QUEST_FUR"), null,
                new QuestObjective(QuestObjectiveKind.HasItems, new[] { new QuestTarget("It_Wolf_Fur", 30), new QuestTarget("It_Bison_Fur", 10) }, StoryFixture.Npc),
                new QuestRewards(null, null, 0)));
            var some = new CarriesQuestItems(BuyFur, QuestItemShare.Some);
            var all = new CarriesQuestItems(BuyFur, QuestItemShare.All);
            f.Entry(Entry("part", new AllOf(new Condition[] { some, new Not(all) }), null, new DeliverQuestItems(BuyFur, ActorRef.Partner)));
            f.Entry(Entry("forced", null, null, new DeliverQuestItems(BuyFur, ActorRef.Partner)));
            f.Entry("other", repeatable: true);
            var _ = f.Dialog;
            f.Quests.TrySetStatus(BuyFur, QuestStatus.Running);
            var hero = f.Items.Inventories.Of(ActorId.Player);
            var context = new ConditionContext(f.World, StoryFixture.Npc);
            hero.Add("It_Wolf_Fur", 32);
            hero.Add("It_Bison_Fur", 5);
            Assert.That(some.Evaluate(context), Is.True);
            Assert.That(all.Evaluate(context), Is.False);

            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("part");
            f.Dialog.Abort();
            Assert.That(f.Quests.ProgressOf(BuyFur).Counters, Is.EqualTo(new[] { 30, 5 }));
            Assert.That(hero.Count("It_Wolf_Fur"), Is.EqualTo(2));
            hero.Add("It_Wolf_Fur", 17);

            Assert.That(some.Evaluate(context), Is.False, "all wolf furs delivered, no bison fur carried");
            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.OfferedIds(), Does.Not.Contain("part"));
            f.Choose("forced");
            f.Dialog.Abort();
            Assert.That(hero.Count("It_Wolf_Fur"), Is.EqualTo(19), "surplus wolf furs stay with the hero");
            Assert.That(f.Quests.ProgressOf(BuyFur).Counters, Is.EqualTo(new[] { 30, 5 }));
            Assert.That(f.Quests.StatusOf(BuyFur), Is.EqualTo(QuestStatus.Running));

            hero.Add("It_Bison_Fur", 5);
            Assert.That(all.Evaluate(context), Is.True, "carried 5 + delivered 5 bison furs cover the 10");
            Assert.That(some.Evaluate(context), Is.True);
        }

        [Test]
        public void TradeTopic_IsOfferedOnlyWhenTradingAndRequestsTheScreen()
        {
            var f = Fixture();
            f.Entry(Entry("trade", new HasRole(ActorRef.Partner, ActorRole.Trade), null, new OpenTrade(), new EndDialog()));
            f.Entry(Entry("enable", null, null, new SetRole(ActorRef.Partner, ActorRole.Trade, true)));
            f.Entry("other");
            var requests = new List<TradeRequested>();
            f.Events.Subscribe<TradeRequested>(requests.Add);

            f.Dialog.Talk(StoryFixture.Npc);
            Assert.That(f.OfferedIds(), Does.Not.Contain("trade"));
            f.Choose("enable");
            Assert.That(f.OfferedIds(), Does.Contain("trade"));
            f.Choose("trade");
            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(requests.Single().Merchant, Is.EqualTo(StoryFixture.Npc));
        }

        /// <summary>
        /// fix-140 [owner]: G3 DoTrade parks the trader in IS_Idle_Trade until the trade page closes and only then completes
        /// the info (its End): a page host holding the talk keeps it active — the partner stays in the talk state — and the
        /// topic's End runs on the page's close.
        /// </summary>
        [Test]
        public void TradeTopic_HeldByThePage_KeepsTheTalkUntilThePageCloses()
        {
            var f = Fixture();
            f.Entry(Entry("trade", null, null, StoryFixture.NpcSays("Wares"), new OpenTrade(), new EndDialog()));
            f.Entry("other");
            var ended = new List<DialogEnded>();
            f.Events.Subscribe<DialogEnded>(ended.Add);
            bool held = false;
            f.Events.Subscribe<TradeRequested>(_ => held = f.Dialog.HoldForPage());

            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("trade");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "Wares" }));
            Assert.That(held, Is.True);
            Assert.That(f.Dialog.IsActive, Is.True, "the talk lasts while the trade page is open");
            Assert.That(f.Dialog.IsHeldByPage, Is.True);
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingPage));
            Assert.That(ended, Is.Empty);
            f.Dialog.CompleteLine();
            f.Dialog.Tick(10f);
            Assert.That(f.Dialog.IsHeldByPage, Is.True, "lines and time do not end the wait, only the page");

            f.Dialog.ResumeAfterPage();
            Assert.That(f.Dialog.IsActive, Is.False, "the trade topic's End runs when the page closes");
            Assert.That(ended.Single().Partner, Is.EqualTo(StoryFixture.Npc));
        }

        [Test]
        public void TradeTopic_AbortedWhileHeld_EndsTheTalk_AndASilentFinishOpensNoPage()
        {
            var f = Fixture();
            f.Entry(Entry("trade", null, null, StoryFixture.NpcSays("Wares"), new OpenTrade(), new EndDialog()));
            f.Entry("other");
            var holds = new List<bool>();
            f.Events.Subscribe<TradeRequested>(_ => holds.Add(f.Dialog.HoldForPage()));

            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("trade");
            f.FinishLines();
            Assert.That(f.Dialog.IsHeldByPage, Is.True);
            f.Dialog.Abort();
            Assert.That(f.Dialog.IsActive, Is.False);
            f.Dialog.ResumeAfterPage(); // the page closes afterwards: nothing to resume
            Assert.That(f.Dialog.IsActive, Is.False);

            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("trade");
            f.Dialog.Abort(); // the hero is taken away during the line: the rest runs silently
            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(holds, Is.EqualTo(new[] { true, false }), "no page is held (opened) for a talk being finished silently");
        }
    }
}
