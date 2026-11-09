using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// story-3 in the story engine: gate-guard warnings (FirstWarn / SecondWarn), NPCs that start a conversation with an
    /// important entry (G3 HasImportantInfo / AssessTalk), QuestRunning repeats, the Animal and Heal conditions and
    /// pickpocketing (Docs/story-coverage.md §5).
    /// </summary>
    public class GuardTheftDialogTests
    {
        static readonly ActorId Gnar = StoryFixture.Npc;

        sealed class Traits : IActorTraits
        {
            public bool Humanoid = true, Fights = true;
            public bool IsHumanoid(ActorId actor) => Humanoid;
            public bool CanFight(ActorId actor) => Fights;
        }

        sealed class FakePickpocket : IPickpocket
        {
            public PickpocketVerdict Next = PickpocketVerdict.Success;
            public readonly HashSet<ActorId> Robbed = new HashSet<ActorId>();
            public bool MayPickpocket(ActorId victim) => !Robbed.Contains(victim);
            public PickpocketVerdict Pickpocket(ActorId victim)
            {
                if (Next == PickpocketVerdict.Success) Robbed.Add(victim);
                return Next;
            }
        }

        [Test]
        public void GateGuard_FirstWarning_IsAnImportantEntry_TheNpcStartsTheTalk_AndTheWarningIsRecorded()
        {
            var world = new RecordingWorldDirector();
            var f = new StoryFixture { WorldDirector = world }
                .Entry("WARN1", DialogEntryKind.Important, owner: ActorId.Player, repeatable: true,
                       condition: new GateGuardMayWarn(ActorRef.Partner),
                       commands: new DialogCommand[] { StoryFixture.NpcSays("Halt"), new GateGuardWarned(ActorRef.Partner) })
                .Entry("WARN2", DialogEntryKind.Important, owner: ActorId.Player, repeatable: true,
                       condition: new GateGuardMayWarn(ActorRef.Partner, second: true),
                       commands: new DialogCommand[] { StoryFixture.NpcSays("ISaidDontGoFurther"), new GateGuardWarned(ActorRef.Partner, second: true) });

            Assert.That(f.Dialog.HasImportantInfo(Gnar), Is.False, "the guard does not watch: nothing important");
            Assert.That(f.Dialog.TalkFirst(Gnar), Is.False);

            world.MayWarn.Add("Gnar 1");
            Assert.That(f.Dialog.HasImportantInfo(Gnar), Is.True);
            Assert.That(f.Dialog.TalkFirst(Gnar), Is.True);
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "Halt" }));
            Assert.That(world.Calls, Does.Contain("warned Gnar 1"));
            Assert.That(f.Dialog.IsActive, Is.False, "nothing else to say: the conversation ends");

            world.MayWarn.Clear();
            world.MayWarn.Add("Gnar 2");
            Assert.That(f.Dialog.TalkFirst(Gnar), Is.True);
            Assert.That(f.FinishLines(), Is.EqualTo(new[] { "ISaidDontGoFurther" }));
            Assert.That(world.Calls, Does.Contain("warned Gnar 2"));
        }

        [Test]
        public void ImportantInfo_IsBlockedByTheNpcsOwnRefusal_ButNotByTheHerosRefusals()
        {
            var refuse = new GameEventFlags();
            var f = new StoryFixture()
                .Entry("HELLO", DialogEntryKind.Important)
                .Entry("GO_AWAY", DialogEntryKind.Refuse, condition: new FlagSet("Refuse"))
                .Entry("HERO_REFUSES", DialogEntryKind.Refuse, owner: ActorId.Player, condition: new FlagSet("HeroRefuses"));
            Assert.That(f.Dialog.HasImportantInfo(Gnar), Is.True);
            f.Flags.Set("HeroRefuses");
            Assert.That(f.Dialog.HasImportantInfo(Gnar), Is.True, "G3 HasImportantInfo ignores PC_Hero's refusals");
            f.Flags.Set("Refuse");
            Assert.That(f.Dialog.HasImportantInfo(Gnar), Is.False);
        }

        [Test]
        public void QuestRunningEntry_RepeatsEveryTwoGameHours_CountedFromTheQuestStart()
        {
            var quest = new Id<QuestDef>("Lardo_Test");
            var entry = new Id<DialogEntryDef>("NEWS");
            var f = new StoryFixture()
                .Quest("Lardo_Test")
                .Entry("NEWS", DialogEntryKind.News, repeatable: true, condition: new QuestRepeatDue(entry, quest, 2),
                       commands: new DialogCommand[] { StoryFixture.NpcSays("Info_MD006"), new RememberRun() });
            var context = f.Conditions();
            var due = new QuestRepeatDue(entry, quest, 2);
            Assert.That(due.Evaluate(context), Is.False, "quest not running");
            f.Quests.TrySetStatus(quest, QuestStatus.Running);
            Assert.That(due.Evaluate(context), Is.False, "just started");
            f.Clock.Skip(2 * 3600);
            Assert.That(due.Evaluate(context), Is.True);

            f.Dialog.Talk(Gnar);
            Assert.That(f.Presenter.Lines.Last().Text.Value, Is.EqualTo("Info_MD006"));
            f.FinishLines();
            Assert.That(f.Memory.TryGetLastRun(entry, out _), Is.True);
            Assert.That(due.Evaluate(context), Is.False, "ran just now");
            f.Clock.Skip(2 * 3600 + 1);
            Assert.That(due.Evaluate(context), Is.True);
        }

        [Test]
        public void AnimalCondition_ACreatureThatCannotFight_OncePerFortyEightHoursAfterTheLastTalk()
        {
            var traits = new Traits { Fights = false, Humanoid = false };
            var f = new StoryFixture { Traits = traits };
            var animal = new AllOf(new Condition[] { new Not(new CanFight(ActorRef.Partner)), new DialogEndedHoursAgo(ActorRef.Partner, 48) });
            f.Entry("MILK", DialogEntryKind.Choice, repeatable: true, condition: animal,
                    commands: new DialogCommand[] { new SaySound(ActorRef.Partner, "sfx_creature_cow_moo_03"), new EndDialog() });
            var sounds = new List<string>();
            f.Events.Subscribe<StorySoundRequested>(e => sounds.Add($"{e.Sound} at {e.At}"));
            var context = f.Conditions();
            Assert.That(animal.Evaluate(context), Is.True, "never talked to");

            f.Dialog.Talk(Gnar); // the only topic runs at once
            Assert.That(sounds, Is.EqualTo(new[] { "sfx_creature_cow_moo_03 at Gnar" }));
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime), "SaySound waits for the sound");
            f.Dialog.Tick(SaySound.WaitSeconds + 0.1f);
            Assert.That(f.Dialog.IsActive, Is.False);

            Assert.That(animal.Evaluate(context), Is.False, "just milked");
            f.Clock.Skip(48 * 3600);
            Assert.That(animal.Evaluate(context), Is.True);
            traits.Fights = true;
            Assert.That(animal.Evaluate(context), Is.False, "a creature that can attack has no Animal topic");
        }

        [Test]
        public void HealCondition_ACompanionAndAHealthPotion()
        {
            var traits = new Traits();
            var f = new StoryFixture { Traits = traits };
            var heal = new CanHeal(ActorRef.Partner);
            var context = f.Conditions();
            Assert.That(heal.Evaluate(context), Is.False);
            f.Party.Join(Gnar, PartyMemberType.Party);
            Assert.That(heal.Evaluate(context), Is.False, "no potion");
            f.Items.Inventories.Give(ActorId.Player, CanHeal.Potion, 1);
            Assert.That(heal.Evaluate(context), Is.True);
            traits.Humanoid = false;
            Assert.That(heal.Evaluate(context), Is.False, "only humanoids drink potions");
        }

        [Test]
        public void Pickpocket_Success_OnceOnly_TooHard_SpeaksTheHerosLine_Caught_Ends()
        {
            var thief = new FakePickpocket();
            var f = new StoryFixture { Pickpocket = thief }
                .Entry("STEAL", condition: new MayPickpocket(ActorRef.Partner), repeatable: true,
                       commands: new DialogCommand[] { new Pickpocket(ActorRef.Partner) })
                .Entry("BYE", DialogEntryKind.Exit, repeatable: true, commands: new DialogCommand[] { new EndDialog() });

            f.Dialog.Talk(Gnar);
            Assert.That(f.OfferedIds(), Does.Contain("STEAL"));
            thief.Next = PickpocketVerdict.TooHard;
            f.Choose("STEAL");
            Assert.That(f.Log.Messages, Has.Some.Contains("Shared line 'TooHard' of player"), "the hero's line (no voice set in this fixture)");
            Assert.That(f.OfferedIds(), Does.Contain("STEAL"), "not robbed: may try again");

            thief.Next = PickpocketVerdict.Success;
            f.Choose("STEAL");
            Assert.That(new MayPickpocket(ActorRef.Partner).Evaluate(f.Conditions()), Is.False, "robbed already");
            f.Dialog.Abort();

            thief.Robbed.Clear();
            thief.Next = PickpocketVerdict.Caught;
            f.Dialog.Talk(Gnar);
            f.Choose("STEAL");
            Assert.That(f.Director.Calls, Has.None.Contains("attack"), "the pickpocket port makes the victim attack (G3 PickPocket's AssessTarget)");
            Assert.That(f.Dialog.IsActive, Is.False, "caught: the dialog ends");
        }

        [Test]
        public void PickpocketRules_LevelGates_SuccessRoll_LootAndXp()
        {
            var none = new HashSet<string>();
            var hard = new HashSet<string> { PickpocketRules.HardPerk };
            var master = new HashSet<string> { PickpocketRules.HardPerk, PickpocketRules.MasterPerk };
            Assert.That(PickpocketRules.VictimLevel(80), Is.EqualTo(50));
            Assert.That(PickpocketRules.Gate(29, none.Contains), Is.Null);
            Assert.That(PickpocketRules.Gate(30, none.Contains), Is.EqualTo(PickpocketVerdict.TooHard));
            Assert.That(PickpocketRules.Gate(44, hard.Contains), Is.Null);
            Assert.That(PickpocketRules.Gate(45, hard.Contains), Is.EqualTo(PickpocketVerdict.Impossible));
            Assert.That(PickpocketRules.Gate(50, master.Contains), Is.Null);
            // rand(100) <= THF/2 - L + 85: THF 20, L 10 → up to 85 succeeds.
            Assert.That(PickpocketRules.Succeeds(20, 10, 85), Is.True);
            Assert.That(PickpocketRules.Succeeds(20, 10, 86), Is.False);
            Assert.That(PickpocketRules.AlternativeBalancingXp(20), Is.EqualTo(10), "5 · ⌊20/7⌋");

            var set = new TreasureSetDef("TS_PickPocket_Test", TreasureUse.Pickpocket, 1, 1,
                                         new[] { new TreasureStack("It_Gold", 10, 0) });
            var stack = PickpocketRules.Loot(set, new SeededRandom(1), out int amount);
            Assert.That(stack.Item, Is.EqualTo("It_Gold"));
            Assert.That(amount, Is.InRange(5, 9), "a/2 + rand(a/2)");
        }
    }
}
