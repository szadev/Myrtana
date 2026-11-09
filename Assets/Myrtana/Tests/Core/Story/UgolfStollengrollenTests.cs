using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-212: Ugolf's Stollengrollen bet (SKALVERAM3767 "Дай мне шнапс!" with its untitled sub-choice SKALVERAM3768).
    /// CP _AI_UseInventoryItem: below 200 maximum stamina the bottle is used up, the hero collapses and Ugolf (the hero's
    /// current target) says "Адская штука" with his own voice; the bet stays open and the repeatable choice is offered
    /// again (G3: retry with more stamina). With 200 the bet is won and the sub-choice plays at once.
    /// </summary>
    public class UgolfStollengrollenTests
    {
        const string Running = "Ugolf_StollengrollenRunning", Success = "Ugolf_StollengrollenSuccess";

        static StoryFixture Fixture(out System.Func<int> staminaMax)
        {
            var f = new StoryFixture()
                .Define(ItemTests.Item("It_Stollengrollen", 50, kind: "Potion", use: new[] { new AttributeGain("MP", ModifierOp.AddPercentageToVal, 10) }))
                .Define(ItemTests.Item("It_Recipe_Stollengrollen", 100));
            f.Entry("schnaps", repeatable: true, condition: new FlagSet(Running),
                    commands: new DialogCommand[]
                    {
                        StoryFixture.PlayerSays("Give"), StoryFixture.NpcSays("Here"),
                        new GiveItems(ActorRef.Partner, ActorRef.Player, "It_Stollengrollen", 1),
                        new UseItem(ActorRef.Player, "It_Stollengrollen"),
                    });
            f.Entry(new DialogEntryDef(new Id<DialogEntryDef>("won"), StoryFixture.Npc, new Id<DialogEntryDef>("schnaps"),
                                         DialogEntryKind.Choice, 0, default, false, false, false, null, new FlagSet(Success), null,
                                         new DialogCommand[]
                                         {
                                             StoryFixture.NpcSays("Bison"),
                                             new GiveItems(ActorRef.Partner, ActorRef.Player, "It_Recipe_Stollengrollen", 1),
                                         }));
            f.Entry("other", repeatable: true);
            var hero = f;
            staminaMax = () => hero.Hero.Stats.Maximum(ActorVitals.StaminaId);
            return f;
        }

        static StoryTriggers Triggers(StoryFixture f, System.Func<int> staminaMax) =>
            new StoryTriggers(G3StoryTriggersImporter.Rules(), f.Events, f.Flags, null, null, staminaMax);

        static void Stamina(StoryFixture f, int max) => f.Hero.Stats.Set(ActorVitals.StaminaId, new StatValue(max, max));

        [Test]
        public void TooLittleStamina_BottleUsedUp_UgolfSaysHisLine_BetStaysOpen_ChoiceOfferedAgain()
        {
            var f = Fixture(out var sp);
            var _ = f.Dialog;
            using var triggers = Triggers(f, sp);
            Stamina(f, 154); // the owner's hero (save522)
            f.Flags.Set(Running);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("schnaps");
            var lines = new List<DialogLine>();
            while (f.Dialog.IsActive && f.Dialog.Current.Phase == ConversationPhase.AwaitingLine)
            {
                lines.Add(f.Presenter.Lines.Last());
                f.Dialog.CompleteLine();
            }

            var hell = lines.Last();
            Assert.That(hell.Text.Value, Is.EqualTo("INFO_SKALVERAM320060405151107_1"));
            Assert.That(hell.Speaker, Is.EqualTo(StoryFixture.Npc), "CP _AI_Say: Self = the hero's current target");
            Assert.That(hell.Voice, Is.EqualTo("INFO_Hum_Warrior_Dumb_Ugolf_SKALVERAM320060405151107_1"));
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Stollengrollen"), Is.Zero, "the bottle is used up");
            Assert.That(f.Flags.IsSet(Success), Is.False);
            Assert.That(f.Flags.IsSet(Running), Is.True, "the bet stays open");
            Assert.That(f.OfferedIds(), Does.Contain("schnaps"), "G3: Ugolf pours again (the hero needs 200 maximum stamina)");
            Assert.That(f.OfferedIds(), Does.Not.Contain("won"));
        }

        [Test]
        public void EnoughStamina_BetWon_SubChoicePlaysAtOnce_SchnapsGone()
        {
            var f = Fixture(out var sp);
            var _ = f.Dialog;
            using var triggers = Triggers(f, sp);
            Stamina(f, 200);
            f.Flags.Set(Running);
            f.Dialog.Talk(StoryFixture.Npc);
            f.Choose("schnaps");
            var shown = f.FinishLines();
            Assert.That(f.Dialog.Current.Phase, Is.EqualTo(ConversationPhase.AwaitingTime), "the hero's drink motion");
            f.Dialog.Tick(UseItemHandler.HeroUseSeconds + 0.1f);
            shown.AddRange(f.FinishLines());

            Assert.That(shown, Does.Contain("Bison"), "the lone sub-choice plays automatically");
            Assert.That(shown, Does.Not.Contain("INFO_SKALVERAM320060405151107_1"));
            Assert.That(f.Flags.IsSet(Success), Is.True);
            Assert.That(f.Flags.IsSet(Running), Is.False);
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Recipe_Stollengrollen"), Is.EqualTo(1));
            Assert.That(f.Items.Inventories.Of(ActorId.Player).Count("It_Stollengrollen"), Is.Zero);
            if (f.Dialog.IsActive) Assert.That(f.OfferedIds(), Does.Not.Contain("schnaps"), "the bet is over");
        }

        [Test]
        public void Importer_UgolfSaysTheFailureLineWithHisVoice()
        {
            var failure = G3StoryTriggersImporter.Rules().ItemUsed.Single().Failure;
            Assert.That(failure.PartnerSays, Is.True);
            Assert.That(failure.TalkVoice, Is.EqualTo("INFO_Hum_Warrior_Dumb_Ugolf_SKALVERAM320060405151107_1"));
        }
    }
}
