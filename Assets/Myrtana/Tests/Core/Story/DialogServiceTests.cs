using System.Linq;
using Myrtana.Core;
using Myrtana.Story;
using NUnit.Framework;
using static Myrtana.Tests.StoryFixture;

namespace Myrtana.Tests
{
    public class DialogServiceTests
    {
        static DialogCommand[] Lines(params string[] texts) => texts.Select(t => (DialogCommand)NpcSays(t)).ToArray();

        [Test]
        public void Start_PlaysFirstRefuseFirstImportantAndAllNews_ThenShowsMenu()
        {
            var f = new StoryFixture()
                .Entry("news2", DialogEntryKind.News, order: 20, commands: Lines("n2"))
                .Entry("news1", DialogEntryKind.News, order: 10, commands: Lines("n1"))
                .Entry("imp1", DialogEntryKind.Important, order: 5, commands: Lines("i1"))
                .Entry("imp2", DialogEntryKind.Important, order: 6, commands: Lines("i2"))
                .Entry("a", order: 1)
                .Entry("b", order: 2);

            f.Dialog.Talk(Npc);

            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "i1", "n1", "n2" }));
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Refuse_EndsTheConversation()
        {
            var f = new StoryFixture()
                .Entry("refuse", DialogEntryKind.Refuse, commands: new DialogCommand[] { NpcSays("go away"), new EndDialog() })
                .Entry("a").Entry("b");

            f.Dialog.Talk(Npc);
            f.FinishLines();

            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Presenter.Closed, Is.EqualTo(1));
        }

        [Test]
        public void Menu_IsSortedByOrder_AndPlayerTopicsJoinEveryConversation()
        {
            var f = new StoryFixture()
                .Entry("end", DialogEntryKind.Exit, order: 100, owner: ActorId.Player, repeatable: true, commands: new DialogCommand[] { new EndDialog() })
                .Entry("late", order: 50)
                .Entry("early", order: 10);

            f.Dialog.Talk(Npc);

            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "early", "late", "end" }));
            Assert.That(f.Dialog.Current.Options.Last().IsExit, Is.True);
        }

        [Test]
        public void UniqueGroup_ListsOnlyTheFirstEntry_AndFallbackOnlyWhenNothingElse()
        {
            var f = new StoryFixture()
                .Entry("end1", DialogEntryKind.Exit, order: 90, uniqueGroup: "End", repeatable: true)
                .Entry("end2", DialogEntryKind.Exit, order: 91, uniqueGroup: "End", repeatable: true)
                .Entry("nothing", DialogEntryKind.Fallback, order: 80, repeatable: true)
                .Entry("topic", order: 10);

            f.Dialog.Talk(Npc);

            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "topic", "end1" }));
        }

        [Test]
        public void OneShotEntry_Disappears_RepeatableStays()
        {
            var f = new StoryFixture()
                .Entry("once", order: 1, commands: Lines("x"))
                .Entry("again", order: 2, repeatable: true, commands: Lines("y"))
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true, commands: new DialogCommand[] { new EndDialog() });

            f.Dialog.Talk(Npc);
            f.Choose("once");
            f.FinishLines();

            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "again", "end" }));
            Assert.That(f.Memory.WasGiven(new Id<DialogEntryDef>("once")), Is.True);
        }

        [Test]
        public void SubChoices_FollowTheirParent_AndALoneAnswerPlaysAutomatically()
        {
            var f = new StoryFixture()
                .Entry("ask", order: 1, commands: new DialogCommand[] { PlayerSays("q"), new SetFlag("asked") })
                .Entry("answer", parent: "ask", commands: Lines("a"))
                .Entry("other", order: 2)
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true);

            f.Dialog.Talk(Npc);
            f.Choose("ask");

            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "q", "a" }));
            Assert.That(f.Flags.IsSet("asked"), Is.True);
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "other", "end" }), "back to the top level after the answer");
        }

        /// <summary>
        /// G3 OnInfoScriptFinished: a list of exactly one info plays at once, menu title or not (Xardas FILLER12 →
        /// FILLER13); two or more are offered.
        /// </summary>
        [Test]
        public void ALoneTitledSubChoice_PlaysAutomatically_TwoAreOffered()
        {
            var f = new StoryFixture()
                .Entry("artefacts", order: 1, commands: Lines("p"))
                .Entry("thirdWay", parent: "artefacts", commands: new DialogCommand[] { PlayerSays("what will you do"), new SetFlag("third") })
                .Entry("ask", order: 2, commands: Lines("q"))
                .Entry("yes", parent: "ask", order: 1, commands: Lines("y"))
                .Entry("no", parent: "ask", order: 2, commands: Lines("n"))
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true);

            f.Dialog.Talk(Npc);
            f.Choose("artefacts");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "p", "what will you do" }));
            Assert.That(f.Flags.IsSet("third"), Is.True);

            f.Choose("ask");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "yes", "no" }));
        }

        [Test]
        public void ParentNeedingAChild_IsHidden_WhenOnlyBackChildrenAreAvailable()
        {
            var f = new StoryFixture()
                .Entry("teachMenu", order: 1, needsChild: true, repeatable: true)
                .Entry("lesson", parent: "teachMenu", condition: new Unsupported("teaching"))
                .Entry("back", DialogEntryKind.Back, parent: "teachMenu", repeatable: true)
                .Entry("topic", order: 2)
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true);

            f.Dialog.Talk(Npc);

            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "topic", "end" }));
        }

        [Test]
        public void ClosesSiblings_RemovesTheOtherSubChoices()
        {
            var f = new StoryFixture()
                .Entry("q", order: 1, commands: Lines("q"))
                .Entry("lie", parent: "q", order: 1, closesSiblings: true, commands: Lines("lie"))
                .Entry("truth", parent: "q", order: 2, closesSiblings: true, commands: Lines("truth"));

            f.Dialog.Talk(Npc);
            f.Choose("q");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "lie", "truth" }));
            f.Choose("lie");
            f.FinishLines();

            Assert.That(f.Memory.WasGiven(new Id<DialogEntryDef>("truth")), Is.True);
        }

        [Test]
        public void SubChoiceWithoutChildren_ShowsItsSiblingsAgain_BackReturnsToTheTopLevel()
        {
            // G3 OnInfoScriptFinished: Marik's "play the spy" sets the flag his sibling topic needs (side-walk)
            var f = new StoryFixture()
                .Entry("q", order: 1, commands: Lines("q"))
                .Entry("spy", parent: "q", order: 1, commands: new DialogCommand[] { NpcSays("spy"), new SetFlag("Spy") })
                .Entry("maybe", parent: "q", order: 2, condition: new FlagSet("Spy"), commands: Lines("maybe"))
                .Entry("back", DialogEntryKind.Back, parent: "q", order: 3, repeatable: true)
                .Entry("other", order: 2, commands: Lines("o"))
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true);

            f.Dialog.Talk(Npc);
            f.Choose("q");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "spy", "back" }));
            f.Choose("spy");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "maybe", "back" }));
            f.Choose("back");
            f.FinishLines();
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "other", "end" }));
        }

        [Test]
        public void FailedPreCheck_PlaysRefusal_KeepsEntry_AndReturnsToTopLevel()
        {
            var fame = new MinEnclaveFame(ActorRef.Partner, 75, new DialogCommand[] { NpcSays("no trust") });
            var f = new StoryFixture()
                .Entry("letMeIn", order: 1, preChecks: new PreCheck[] { fame }, commands: Lines("welcome"))
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true);

            f.Dialog.Talk(Npc);
            f.Choose("letMeIn");

            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "no trust" }));
            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "letMeIn", "end" }));
            Assert.That(f.Memory.WasGiven(new Id<DialogEntryDef>("letMeIn")), Is.False);

            f.Reputation.Fame = 75;
            f.Choose("letMeIn");
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "welcome" }));
        }

        [Test]
        public void Ending_MarksThePartnerAsTalkedTo_SoHelloEntriesDoNotRepeat()
        {
            var f = new StoryFixture()
                .Entry("hello", DialogEntryKind.News, condition: new Not(new TalkedToPlayer(ActorRef.Partner)), commands: Lines("halt"))
                .Entry("topic", order: 1, repeatable: true)
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true, commands: new DialogCommand[] { new EndDialog() });

            f.Dialog.Talk(Npc);
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "halt" }));
            f.Choose("end");
            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Actors.HasTalkedToPlayer(Npc), Is.True);

            f.Dialog.Talk(Npc);
            Assert.That(f.FinishLines(), Is.Empty);
        }

        [Test]
        public void StartQuest_SetsTheQuestRunning()
        {
            var f = new StoryFixture()
                .Quest("Gnar_EnterCastle")
                .Entry("accept", order: 1, commands: new DialogCommand[] { NpcSays("ok"), new StartQuest(new Id<QuestDef>("Gnar_EnterCastle")) })
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true);

            f.Dialog.Talk(Npc);
            f.Choose("accept");
            f.FinishLines();

            Assert.That(f.Quests.StatusOf(new Id<QuestDef>("Gnar_EnterCastle")), Is.EqualTo(QuestStatus.Running));
        }

        [Test]
        public void LeaveSubmenu_ReturnsToTopLevelEvenWithSubChoices()
        {
            var f = new StoryFixture()
                .Entry("menu", order: 1, repeatable: true, commands: new DialogCommand[] { new LeaveSubmenu() })
                .Entry("child", parent: "menu", repeatable: true)
                .Entry("child2", parent: "menu", repeatable: true)
                .Entry("end", DialogEntryKind.Exit, order: 9, repeatable: true);

            f.Dialog.Talk(Npc);
            f.Choose("menu");

            Assert.That(f.OfferedIds(), Is.EqualTo(new[] { "menu", "end" }));
        }

        [Test]
        public void ChoosingSomethingNotOffered_IsIgnored()
        {
            var f = new StoryFixture().Entry("a", order: 1).Entry("b", order: 2);
            f.Dialog.Talk(Npc);

            f.Choose("nonexistent");

            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingChoice));
        }

        [Test]
        public void RepeatingAutomaticEntries_WithoutLines_AreStoppedAsALoop()
        {
            var f = new StoryFixture().Entry("loop", DialogEntryKind.News, repeatable: true, commands: new DialogCommand[] { new SetFlag("x") });

            f.Dialog.Talk(Npc);

            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Log.Messages.Any(m => m.Contains("loops")), Is.True);
        }

        [Test]
        public void SynchronousPresenter_CompletingInsideShowLine_StillAdvances()
        {
            var f = new StoryFixture().Entry("n", DialogEntryKind.News, commands: Lines("one", "two"));
            f.Presenter.OnLine = () => f.Dialog.CompleteLine();

            f.Dialog.Talk(Npc);

            Assert.That(f.Presenter.Lines.Select(l => l.Text.Value), Is.EqualTo(new[] { "one", "two" }));
            Assert.That(f.Dialog.IsActive, Is.False, "nothing left to offer → closed");
        }

        [Test]
        public void NotSupportedCommand_IsSkippedWithALogMessage()
        {
            var f = new StoryFixture()
                .Entry("n", DialogEntryKind.News, commands: new DialogCommand[] { new NotSupported("GiveXP", "100"), NpcSays("after") });

            f.Dialog.Talk(Npc);

            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "after" }));
            Assert.That(f.Log.Messages.Any(m => m.Contains("GiveXP")), Is.True);
        }

        [Test]
        public void SpeakerRoles_ResolveToThePartnerAndThePlayer()
        {
            var f = new StoryFixture().Entry("n", DialogEntryKind.News, commands: new DialogCommand[] { NpcSays("a"), PlayerSays("b") });

            f.Dialog.Talk(Npc);

            Assert.That(f.Presenter.Lines[0].Speaker, Is.EqualTo(Npc));
            f.Dialog.CompleteLine();
            Assert.That(f.Presenter.Lines[1].Speaker, Is.EqualTo(ActorId.Player));
        }

        /// <summary>G3 never cuts an info short: an aborted entry stays given and its remaining commands still run (silently).</summary>
        [Test]
        public void Abort_MidEntry_RunsTheRemainingCommandsWithoutLines()
        {
            var f = new StoryFixture().Quest("Q_Reddock")
                .Entry("arrive", DialogEntryKind.Important, commands: new DialogCommand[]
                {
                    StoryFixture.NpcSays("a1"), StoryFixture.NpcSays("a2"), new EndDialog(),
                    new SetRoutine(ActorRef.Partner, "Reddock"), new StartQuest(new Id<QuestDef>("Q_Reddock")),
                });
            Assert.That(f.Dialog.TalkFirst(StoryFixture.Npc), Is.True);
            Assert.That(f.Presenter.Lines.Count, Is.EqualTo(1), "first line shown");

            f.Dialog.Abort();

            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Presenter.Lines.Count, Is.EqualTo(1), "the rest is not presented");
            Assert.That(f.Director.Calls, Does.Contain("routine Gnar Reddock"));
            Assert.That(f.Quests.StatusOf(new Id<QuestDef>("Q_Reddock")), Is.EqualTo(QuestStatus.Running));
            Assert.That(f.Dialog.HasImportantInfo(StoryFixture.Npc), Is.False, "given");
        }

        [Test]
        public void Discard_RunsNothingMore()
        {
            var f = new StoryFixture().Quest("Q_Reddock")
                .Entry("arrive", DialogEntryKind.Important, commands: new DialogCommand[]
                    { StoryFixture.NpcSays("a1"), new StartQuest(new Id<QuestDef>("Q_Reddock")) });
            f.Dialog.TalkFirst(StoryFixture.Npc);
            f.Dialog.Discard();
            Assert.That(f.Dialog.IsActive, Is.False);
            Assert.That(f.Quests.StatusOf(new Id<QuestDef>("Q_Reddock")), Is.EqualTo(QuestStatus.Open));
        }
    }
}
