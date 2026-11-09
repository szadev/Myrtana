using Myrtana.Core;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class ConditionTests
    {
        static readonly Id<QuestDef> Quest = new Id<QuestDef>("Q");
        static readonly Id<QuestDef> Before = new Id<QuestDef>("Before");

        StoryFixture _f;

        [SetUp]
        public void SetUp() => _f = new StoryFixture().Quest("Q", "Before").Quest("Before");

        bool Eval(Condition c) => c.Evaluate(_f.Conditions());

        [Test]
        public void FlagSet_FollowsSetAndClear()
        {
            var c = new FlagSet("Gnar_NotEnter");
            Assert.That(Eval(c), Is.False);
            _f.Flags.Set("Gnar_NotEnter");
            Assert.That(Eval(c), Is.True);
            _f.Flags.Clear("Gnar_NotEnter");
            Assert.That(Eval(c), Is.False);
        }

        [Test]
        public void Logic_AllAnyNot()
        {
            _f.Flags.Set("a");
            var a = new FlagSet("a");
            var b = new FlagSet("b");
            Assert.That(Eval(new AllOf(new Condition[] { a, b })), Is.False);
            Assert.That(Eval(new AllOf(new Condition[] { a })), Is.True);
            Assert.That(Eval(new AllOf(new Condition[0])), Is.True);
            Assert.That(Eval(new AnyOf(new Condition[] { a, b })), Is.True);
            Assert.That(Eval(new AnyOf(new Condition[0])), Is.False);
            Assert.That(Eval(new Not(b)), Is.True);
        }

        [Test]
        public void QuestStatusIs_MatchesAnyListedStatus()
        {
            var openOrRunning = new QuestStatusIs(Quest, new[] { QuestStatus.Open, QuestStatus.Running });
            var running = new QuestStatusIs(Quest, new[] { QuestStatus.Running });
            Assert.That(Eval(openOrRunning), Is.True, "every quest starts Open");
            Assert.That(Eval(running), Is.False);
            _f.Quests.TrySetStatus(Quest, QuestStatus.Running);
            Assert.That(Eval(running), Is.True);
            _f.Quests.TrySetStatus(Quest, QuestStatus.Success);
            Assert.That(Eval(openOrRunning), Is.False);
        }

        [Test]
        public void QuestFinished_IsTrueForFinalStatusesOnly()
        {
            var finished = new QuestFinished(Before);
            Assert.That(Eval(finished), Is.False);
            _f.Quests.TrySetStatus(Before, QuestStatus.Running);
            Assert.That(Eval(finished), Is.False);
            _f.Quests.TrySetStatus(Before, QuestStatus.Lost);
            Assert.That(Eval(finished), Is.False, "Lost can be reactivated");
            _f.Quests.TrySetStatus(Before, QuestStatus.Running);
            _f.Quests.TrySetStatus(Before, QuestStatus.Cancelled);
            Assert.That(Eval(finished), Is.True);
        }

        [Test]
        public void TalkedToPlayer_ResolvesThePartnerRole()
        {
            var c = new TalkedToPlayer(ActorRef.Partner);
            Assert.That(Eval(c), Is.False);
            _f.Actors.MarkTalkedToPlayer(StoryFixture.Npc);
            Assert.That(Eval(c), Is.True);
            Assert.That(Eval(new TalkedToPlayer(ActorRef.To(new ActorId("Mojok")))), Is.False);
        }

        [Test]
        public void Never_IsFalse() => Assert.That(Eval(new Never()), Is.False);

        [Test]
        public void Unsupported_IsFalse_AndLogged()
        {
            Assert.That(Eval(new Unsupported("items")), Is.False);
            Assert.That(_f.Log.Messages, Has.Some.Contains("items"));
        }
    
        [Test]
        public void Explain_ListsTheFalseLeavesWithTheQuestStatus_FixFortyEight()
        {
            // r29 silden_02: "condition false" alone hid that Quadir_HuntSlave was no longer Running
            _f.Flags.Set("a");
            var c = new AllOf(new Condition[] { new FlagSet("a"), new QuestStatusIs(Quest, new[] { QuestStatus.Running }), new FlagSet("b") });
            var why = ConditionExplain.FalseLeaves(c, _f.Conditions());
            Assert.That(why, Has.Count.EqualTo(2));
            Assert.That(why[0], Does.Contain("quest Q is Open, not Running"));
            Assert.That(why[1], Is.EqualTo("FlagSet"));
            _f.Quests.TrySetStatus(Quest, QuestStatus.Running);
            _f.Flags.Set("b");
            Assert.That(ConditionExplain.FalseLeaves(c, _f.Conditions()), Is.Empty);
        }
}
}
