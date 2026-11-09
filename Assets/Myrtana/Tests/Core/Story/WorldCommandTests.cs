using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Story commands and checks added by the story coverage milestone (Docs/story-coverage.md).</summary>
    public class WorldCommandTests
    {
        static readonly Id<QuestDef> Hunt = new Id<QuestDef>("Gnar_Hunt");

        static StoryFixture Fixture() => new StoryFixture()
            .Define(ItemTests.Item("It_Gold", 1))
            .Define(ItemTests.Item("It_Key", 5))
            .Define(ItemTests.Item("It_Potion_Health", 250, use: new[] { new AttributeGain("HP", ModifierOp.AddPercentageToVal, 50) }))
            .Define(new SharedLines(SharedLines.DefinitionId, "SVM_{voice}_{line}"));

        static DialogEntryDef Entry(string id, Condition condition = null, IReadOnlyList<PreCheck> preChecks = null, params DialogCommand[] commands) =>
            new DialogEntryDef(new Id<DialogEntryDef>(id), StoryFixture.Npc, default, DialogEntryKind.Choice, 0, default, true, false, false,
                               null, condition, preChecks, commands);

        static StoryFixture Run(params DialogCommand[] commands)
        {
            var f = Fixture();
            f.Entry(Entry("topic", null, null, commands));
            f.Entry("other", repeatable: true);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("topic");
            f.FinishLines();
            return f;
        }

        [Test]
        public void StartFight_AsksTheDirectorWithTheReason()
        {
            var f = Run(new StartFight(ActorRef.Partner, ActorRef.Player, AttackReason.Duel));
            Assert.That(f.Director.Calls, Is.EqualTo(new[] { "attack Gnar player Duel" }));
        }

        [Test]
        public void SetRoutine_SetAlignment_Heal_AreDirected()
        {
            var f = Run(new SetRoutine(ActorRef.Partner, "Free"), new SetAlignment(ActorRef.Partner, "Reb"),
                        new SetAlignment(ActorRef.Partner, null), new HealActor(ActorRef.Player));
            Assert.That(f.Director.Calls, Is.EqualTo(new[] { "routine Gnar Free", "alignment Gnar Reb", "alignment Gnar none", "heal player" }));
        }

        [Test]
        public void Sleep_SleepsAndEndsTheConversation()
        {
            var f = Run(new Sleep(8));
            Assert.That(f.Director.Calls, Is.EqualTo(new[] { "sleep 8" }));
            Assert.That(f.Dialog.IsActive, Is.False);
        }

        [Test]
        public void SetRole_SwitchesThePartyMobAndSlaveryFlags()
        {
            var f = Run(new SetRole(ActorRef.Partner, ActorRole.Party, true), new SetRole(ActorRef.Partner, ActorRole.Slavery, true));
            var roles = f.Items.Roles;
            Assert.That(roles.Has(StoryFixture.Npc, ActorRole.Party), Is.True);
            Assert.That(roles.Has(StoryFixture.Npc, ActorRole.Slavery), Is.True);
            Assert.That(roles.Has(StoryFixture.Npc, ActorRole.Mob), Is.False);
        }

        [Test]
        public void SharedLineVariants_PickOneOfTheNumberedLines()
        {
            var f = Fixture();
            f.Entry(Entry("topic", null, null, new SayShared(ActorRef.Partner, "KeepItUp", 5)));
            f.Entry("other", repeatable: true);
            var seen = new HashSet<string>();
            for (int i = 0; i < 20; i++)
            {
                f.Dialog.Talk(StoryFixture.Npc);
                f.Choose("topic");
                seen.UnionWith(f.FinishLines());
                f.Dialog.Abort();
            }
            Assert.That(seen, Has.All.Match("^SVM_Orc_Warrior_Hard_KeepItUp_0[0-4]$"));
            Assert.That(seen.Count, Is.GreaterThan(1), "a random variant each time");
        }

        [Test]
        public void RemoveItems_GiveAll_TakeAndHandOverItems()
        {
            var f = Fixture();
            f.Entry(Entry("topic", null, null, new RemoveItems(ActorRef.Player, "It_Key", 1), new GiveAllItems(ActorRef.Partner, ActorRef.Player)));
            f.Entry("other", repeatable: true);
            var _ = f.Dialog;
            f.Items.Inventories.Of(ActorId.Player).Add("It_Key", 2);
            f.Items.Inventories.Of(StoryFixture.Npc).Add("It_Gold", 30);
            f.Items.Inventories.Of(StoryFixture.Npc).Add("It_Potion_Health", 2);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("topic");

            var hero = f.Items.Inventories.Of(ActorId.Player);
            Assert.That(hero.Count("It_Key"), Is.EqualTo(1));
            Assert.That(hero.Count("It_Gold"), Is.EqualTo(30));
            Assert.That(hero.Count("It_Potion_Health"), Is.EqualTo(2));
            Assert.That(f.Items.Inventories.Of(StoryFixture.Npc).Stacks, Is.Empty);
        }

        [Test]
        public void UseItem_TheHeroGetsAndUsesIt_AnNpcOnlyShowsIt()
        {
            var f = Fixture();
            f.Entry(Entry("topic", null, null, new UseItem(ActorRef.Player, "It_Potion_Health"), new UseItem(ActorRef.Partner, "It_Key")));
            f.Entry("other", repeatable: true);
            var shown = new List<ActorUsedItem>();
            f.Events.Subscribe<ActorUsedItem>(shown.Add);
            var used = new List<ItemUsed>();
            f.Events.Subscribe<ItemUsed>(used.Add);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("topic");

            Assert.That(used.Select(u => u.Item), Is.EqualTo(new[] { "It_Potion_Health" }));
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Potion_Health"), Is.Zero, "created, then used up");
            // fix-153: the hero's use is shown too (his drink motion), and the script waits for it before the NPC's
            Assert.That(shown.Select(u => u.Actor), Is.EqualTo(new[] { ActorId.Player }));
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime), "G3: the script waits for the hero's use motion");
            f.Dialog.Tick(UseItemHandler.HeroUseSeconds + 0.1f);
            Assert.That(shown.Select(u => u.Actor), Is.EqualTo(new[] { ActorId.Player, StoryFixture.Npc }));
            Assert.That(f.Items.Inventories.Of(StoryFixture.Npc).Count("It_Key"), Is.Zero);
            Assert.That(f.Director.Calls, Does.Contain($"heal {StoryFixture.Npc}"), "CP RefreshHitPoints of an NPC user");
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime), "G3: the script waits for the NPC's use motion");
            f.Dialog.Tick(UseItemHandler.NpcUseSeconds + 0.1f);
            Assert.That(f.Dialog.Current == null || f.Dialog.Current.Phase != ConversationPhase.AwaitingTime, Is.True);
        }

        [Test]
        public void MinPoliticalFame_RefusesBelowTheThreshold()
        {
            var f = Fixture();
            var check = new MinPoliticalFame(ActorRef.Partner, 75, new DialogCommand[] { new SayShared(ActorRef.Partner, "NoFriendOfORC") });
            f.Entry(Entry("friend", null, new PreCheck[] { check }, StoryFixture.NpcSays("Welcome")));
            f.Entry("other", repeatable: true);
            f.Reputation.PoliticalFame = 74;
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("friend");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "SVM_Orc_Warrior_Hard_NoFriendOfORC" }));

            f.Reputation.PoliticalFame = 75;
            f.Choose("friend");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "Welcome" }));
        }

        [Test]
        public void QuestOverdue_OnlyWhileRunningPastTheTimeLimit()
        {
            var f = Fixture().Quest(new QuestDef(Hunt, new TextId("QUEST_Hunt"), null, timeLimitHours: 48));
            var overdue = new QuestOverdue(Hunt);
            var context = f.Conditions();
            Assert.That(overdue.Evaluate(context), Is.False, "Open");
            f.Quests.TrySetStatus(Hunt, QuestStatus.Running);
            f.Clock.Skip(47 * 3600);
            Assert.That(overdue.Evaluate(context), Is.False);
            f.Clock.Skip(2 * 3600);
            Assert.That(overdue.Evaluate(context), Is.True);
            f.Quests.TrySetStatus(Hunt, QuestStatus.Success);
            Assert.That(overdue.Evaluate(context), Is.False, "finished");
        }
    }

    public class QuestDefeatCounterTests
    {
        static readonly ActorId Wolf = new ActorId("Wolf_01"), Thief = new ActorId("Thief"), Courier = new ActorId("Courier");

        static StoryFixture Fixture(QuestObjectiveKind kind, ActorId destination, params ActorId[] targets)
        {
            var f = new StoryFixture().Quest(new QuestDef(new Id<QuestDef>("Q"), new TextId("QUEST_Q"), null,
                new QuestObjective(kind, targets.Select(t => new QuestTarget(t.Name, 1)).ToList(), destination)));
            var _ = f.Dialog;
            return f;
        }

        static void Defeat(StoryFixture f, ActorId victim, bool killed) =>
            f.Events.Publish(new ActorDefeated(victim, byPlayerParty: true, killed: killed, victimLevel: 5, ambientCreature: false, species: "Human"));

        static QuestStatus Status(StoryFixture f) => f.Quests.StatusOf(new Id<QuestDef>("Q"));

        [Test]
        public void Kill_CountsKillsOnly_AndSucceeds()
        {
            var f = Fixture(QuestObjectiveKind.Kill, default, Wolf, Thief);
            f.Quests.TrySetStatus(new Id<QuestDef>("Q"), QuestStatus.Running);
            Defeat(f, Wolf, killed: false);
            Defeat(f, Wolf, killed: true);
            Assert.That(Status(f), Is.EqualTo(QuestStatus.Running));
            Defeat(f, Thief, killed: true);
            Assert.That(Status(f), Is.EqualTo(QuestStatus.Success));
        }

        [Test]
        public void Defeat_CountsAKnockOut_EvenBeforeTheQuestIsTaken()
        {
            var f = Fixture(QuestObjectiveKind.Defeat, default, Thief);
            Defeat(f, Thief, killed: false);
            Assert.That(Status(f), Is.EqualTo(QuestStatus.Success));
        }

        [Test]
        public void DeadDestination_FailsAnItemQuest_ObsoleteWhileOpen()
        {
            var running = Fixture(QuestObjectiveKind.HasItems, Courier, new ActorId("It_Letter"));
            running.Quests.TrySetStatus(new Id<QuestDef>("Q"), QuestStatus.Running);
            Defeat(running, Courier, killed: false);
            Assert.That(Status(running), Is.EqualTo(QuestStatus.Running), "a knock-out does not fail it");
            Defeat(running, Courier, killed: true);
            Assert.That(Status(running), Is.EqualTo(QuestStatus.Failed));

            var open = Fixture(QuestObjectiveKind.HasItems, Courier, new ActorId("It_Letter"));
            Defeat(open, Courier, killed: true);
            Assert.That(Status(open), Is.EqualTo(QuestStatus.Obsolete));
        }

        [Test]
        public void Report_FailsOnlyWhenTheTargetDiesBeforeTheReport()
        {
            var f = Fixture(QuestObjectiveKind.Report, default, Courier, Thief);
            f.Quests.TrySetStatus(new Id<QuestDef>("Q"), QuestStatus.Running);
            f.Quests.Count(new Id<QuestDef>("Q"), Courier.Name, 1);
            Defeat(f, Courier, killed: true);
            Assert.That(Status(f), Is.EqualTo(QuestStatus.Running), "already reported to");
            Defeat(f, Thief, killed: true);
            Assert.That(Status(f), Is.EqualTo(QuestStatus.Failed));
        }

        [Test]
        public void FollowNpc_FailsWhenTheFirstNpcDies()
        {
            var f = Fixture(QuestObjectiveKind.FollowNpc, new ActorId("Camp"), Courier);
            f.Quests.TrySetStatus(new Id<QuestDef>("Q"), QuestStatus.Running);
            Defeat(f, Courier, killed: true);
            Assert.That(Status(f), Is.EqualTo(QuestStatus.Failed));
        }
    }
}
