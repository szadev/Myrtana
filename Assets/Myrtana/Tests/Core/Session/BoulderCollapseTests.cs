using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-13: orc boulders (CP OB_Light/Medium/Heavy/Faring_Boulder, the Faring boulder's game event for Zakosh_TeachSmith)
    /// and the Stollengrollen collapse of a hero without the stamina (CP _AI_UseInventoryItem).
    /// </summary>
    public class BoulderCollapseTests
    {
        static InteractiveObjectDef Boulder(string id, string useFunc) =>
            new InteractiveObjectDef(id, "G3_Object_Interact_Animated_OrcBoulder_" + id, InteractiveKind.Boulder, "OrcBoulder",
                                     "G3_OrcBoulder_Body_OrcBoulder.FXA", new float[] { 1, 0, 1 }, null, null, 1f, default, null, null, null, null, useFunc);

        static void Strength(GameSession s, int value) => s.Hero.Stats.Set("STR", new StatValue(value, System.Math.Max(value, s.Hero.Stats.Maximum("STR"))));

        [Test]
        public void Rules_AreTheCpWeights()
        {
            var r = BoulderRules.Default;
            Assert.That(r.Weights.Select(w => $"{w.UseFunc}:{w.TiltStrength}/{w.LiftStrength}"),
                        Is.EqualTo(new[] { "OB_Light:100/150", "OB_Medium:150/200", "OB_Heavy:200/250", "OB_Faring_Boulder:200/250" }));
            Assert.That(r.WeightOf(null).UseFunc, Is.EqualTo("OB_Light"), "no use function: a light boulder");
            Assert.That(r.WeightOf("OB_Faring_Boulder").LiftFlag, Is.EqualTo("Faring_Boulder_Success"));
        }

        [Test]
        public void FaringBoulder_TooHeavyBelow250_LiftSetsTheEvent_FirstLiftRaisesStrengthOnce_Saved()
        {
            using var f = new SessionFixture().Add(Myrtana.Rules.RulesSchema.InteractiveObjectFolder, Boulder("Faring", "OB_Faring_Boulder"), Boulder("Light", null));
            var s = f.NewGame();
            var boulders = s.Interaction.Boulders;
            var lifted = new List<BoulderLifted>();
            s.Events.Subscribe<BoulderLifted>(lifted.Add);

            Strength(s, 199);
            Assert.That(boulders.Check("Faring", BoulderStage.Tilt), Is.EqualTo(BoulderVerdict.TooHeavy));
            Strength(s, 220);
            Assert.That(boulders.Use("Faring"), Is.EqualTo(BoulderVerdict.TooHeavy), "tilts, but lifting needs 250");
            Assert.That(s.Flags.IsSet("Faring_Boulder_Success"), Is.False);

            Strength(s, 250);
            Assert.That(boulders.Use("Faring"), Is.EqualTo(BoulderVerdict.Ok));
            Assert.That(s.Flags.IsSet("Faring_Boulder_Success"), Is.True);
            Assert.That(s.Hero.Stats.Value("STR"), Is.EqualTo(255), "first lift: +5 STR");
            boulders.Use("Faring");
            Assert.That(s.Hero.Stats.Value("STR"), Is.EqualTo(255), "only the first lift of each boulder");
            Assert.That(lifted.Select(l => l.FirstTime), Is.EqualTo(new[] { true, false }));
            Assert.That(boulders.Use("Light"), Is.EqualTo(BoulderVerdict.Ok));
            Assert.That(s.Hero.Stats.Value("STR"), Is.EqualTo(260), "another boulder raises it again");
            Assert.That(boulders.Check("Gnar", BoulderStage.Tilt), Is.EqualTo(BoulderVerdict.NotABoulder));

            var loaded = f.Reload(s);
            Assert.That(loaded.Interaction.Boulders.WasLifted("Faring") && loaded.Interaction.Boulders.WasLifted("Light"), Is.True);
        }

        [Test]
        public void Walk_FaringBoulderProducesItsEvent_DryRunLiftsIt()
        {
            using var f = new SessionFixture().Add(Myrtana.Rules.RulesSchema.InteractiveObjectFolder, Boulder("Faring", "OB_Faring_Boulder"), Boulder("Heavy", "OB_Heavy"));
            var walk = new StoryWalkBuilder(f.Content.Database).Build().Run();
            Assert.That(walk.Has(WalkFacts.Flag("Faring_Boulder_Success")), Is.True);
            Assert.That(walk.Rules.Count(r => r.Id.StartsWith("boulder ")), Is.EqualTo(1), "only story boulders are walk rules");

            var s = f.NewGame();
            var run = new ChapterDryRun(s);
            Assert.That(run.Run(ScenarioParser.ParseInline("attr STR 300\nuse obj:G3_Object_Interact_Animated_OrcBoulder_Faring\nexpect flag:Faring_Boulder_Success", "t")),
                        Is.True, string.Join("; ", run.Failures));
        }

        static readonly StoryTriggerRules Stollengrollen = new StoryTriggerRules(StoryTriggerRules.DefinitionId, null,
            new[]
            {
                new ItemUsedTrigger("It_Stollengrollen", 200, new[] { "Ugolf_StollengrollenSuccess" }, new[] { "Ugolf_StollengrollenRunning" },
                                    new ItemUseFailure(true, "INFO_X_1", "INFO_Player_PC_Hero_X_1")),
            }, null);

        [Test]
        public void Stollengrollen_WithoutTheStamina_TheHeroCollapses()
        {
            using var f = new SessionFixture().Add(StorySchema.TriggerFolder, Stollengrollen);
            var s = f.NewGame();
            var collapsed = new List<HeroCollapsed>();
            s.Events.Subscribe<HeroCollapsed>(collapsed.Add);
            s.Flags.Set("Ugolf_StollengrollenRunning");

            s.Events.Publish(new ItemUsed("It_Stollengrollen"));

            Assert.That(collapsed.Select(c => c.TalkLine), Is.EqualTo(new[] { "INFO_X_1" }));
            Assert.That(s.Combat.Vitals.StateOf(ActorId.Player), Is.EqualTo(VitalState.Down), "DOWN");
            Assert.That(s.Flags.IsSet("Ugolf_StollengrollenSuccess"), Is.False);
            Assert.That(s.Flags.IsSet("Ugolf_StollengrollenRunning"), Is.True, "the bet stays open");
            for (int i = 0; i < 40; i++) s.Tick(0.25f);
            Assert.That(s.Combat.Vitals.StateOf(ActorId.Player), Is.EqualTo(VitalState.Alive), "he gets up again");
        }

        [Test]
        public void Importer_StollengrollenFailureCollapsesWithUgolfsLine()
        {
            var failure = G3StoryTriggersImporter.Rules().ItemUsed.Single().Failure;
            Assert.That(failure.Collapse, Is.True);
            Assert.That(failure.TalkLine, Is.EqualTo("INFO_SKALVERAM320060405151107_1"));
        }
    }
}
