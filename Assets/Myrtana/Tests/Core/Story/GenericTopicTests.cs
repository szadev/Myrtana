using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Simulation;
using Myrtana.Story;
using NUnit.Framework;
using static Myrtana.Tests.StoryFixture;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-158: G3 gCInfoManager_PS::HasInfos — an NPC without infos of its own uses the hero's (PC_Hero) list, so it is
    /// talkable when one of the hero's entries other than a comment, End or Back is available for it; else the talk is
    /// refused (StartSayRefuseTalk) and no dialog opens.
    /// </summary>
    public class GenericTopicTests
    {
        static readonly ActorId Generic = new ActorId("RebTrader_02#b3073412");

        static StoryFixture HeroTopics(bool tradeOn) =>
            new StoryFixture()
                .Entry("trade", order: 17L << 33, owner: ActorId.Player, repeatable: true,
                       condition: tradeOn ? null : new FlagSet("never"), commands: new DialogCommand[] { PlayerSays("show me") })
                .Entry("end", DialogEntryKind.Exit, order: 51L << 33, owner: ActorId.Player, repeatable: true, commands: new DialogCommand[] { new EndDialog() });

        [Test]
        public void GenericNpc_WithAnAvailableHeroTopic_IsTalkable_AndOffersIt()
        {
            var f = HeroTopics(tradeOn: true);
            Assert.That(f.Dialog.CanTalkTo(Generic), Is.True);
            f.Dialog.Talk(Generic);
            Assert.That(f.Dialog.IsActive, Is.True);
            Assert.That(f.Presenter.Menus[0][0].Entry.Name, Is.EqualTo("trade"));
        }

        [Test]
        public void GenericNpc_WithOnlyTheHerosEnd_IsRefused_NoDialog()
        {
            var f = HeroTopics(tradeOn: false);
            var refused = new List<ActorId>();
            f.Events.Subscribe<TalkRefused>(e => refused.Add(e.Npc));
            Assert.That(f.Dialog.CanTalkTo(Generic), Is.False, "End does not count (HasInfos skips CondType End/Back)");
            f.Dialog.Talk(Generic);
            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Presenter.Opened, Is.EqualTo(0));
            Assert.That(refused, Is.EqualTo(new[] { Generic }));
        }

        [Test]
        public void HeroComments_DoNotMakeAnyoneTalkable()
        {
            var f = new StoryFixture().Entry("c", DialogEntryKind.Comment, owner: ActorId.Player, repeatable: true);
            Assert.That(f.Dialog.CanTalkTo(Generic), Is.False);
        }

        [Test]
        public void NamedNpc_WithOwnTopics_StaysTalkable_EvenWithNothingAvailable()
        {
            var f = new StoryFixture().Entry("own", condition: new FlagSet("never"));
            Assert.That(f.Dialog.CanTalkTo(Npc), Is.True);
        }

        [Test]
        public void Refusal_CyclesItsThreeVariants_SleeperDoesNotAdvance()
        {
            var rule = new RefuseTalk();
            var awake = new RefuseTalkFacts { Destination = "Field" };
            Assert.That(rule.Next(new RefuseTalkFacts { Asleep = true }), Is.EqualTo("SleepRefuse"));
            Assert.That(new[] { rule.Next(awake), rule.Next(awake), rule.Next(awake), rule.Next(awake) },
                        Is.EqualTo(new[] { "Farmer", "CertainlyBusy", "DontDisturbMe", "Farmer" }));
        }

        [Test]
        public void Refusal_Situational_FollowsG3Order()
        {
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Companion = true, Guard = true }, 0), Is.EqualTo("NotNow"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Guard = true, Friendly = true }, 0), Is.EqualTo("GuardRefuseFriend"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Guard = true }, 0), Is.EqualTo("GuardRefuse"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Guard = true, DiscussedThefts = 20 }, 0), Is.EqualTo("NoMoreTroubleHere"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { HasEnclave = true, EnclaveSwitched = true }, 0), Is.EqualTo("RevolutionHero"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { HasEnclave = true, EnclaveFled = true, LoyalToEnclave = true }, 0), Is.EqualTo("WhatDoYouWant"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Slave = true, HasEnclave = true, EnclaveRoutine = true, EnclaveRevolution = true }, 0),
                        Is.EqualTo("RevolutionReady"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Destination = "Anvil" }, 0), Is.EqualTo("Smith"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Female = true, Friendly = true }, 1), Is.EqualTo("BetterGoNow"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Friendly = true }, 1), Is.EqualTo("SeeYouLater"));
            Assert.That(RefuseTalk.Line(new RefuseTalkFacts { Friendly = true }, 2), Is.EqualTo("TakeCare"));
        }
    }
}
