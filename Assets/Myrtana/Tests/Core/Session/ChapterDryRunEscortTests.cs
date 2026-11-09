using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// The headless check of generated side-quest scenarios (Docs/story-walk.md §5): an escort needs an NPC that walks
    /// (not frozen) and guides or follows the hero (editor-runner-23: a freeze before the escort, an escort whose quest
    /// start an earlier block had already played).
    /// </summary>
    public class ChapterDryRunEscortTests
    {
        static readonly ActorId Gorn = new ActorId("Gorn");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.NpcFolder, new NpcDef(Gorn, new TextId("FO_Gorn"), "Gorn", "Human", "visual", new WorldPose(20, 0, 0, 0)))
            .Add(RulesSchema.PlaceFolder, new WorldPlaceDef("FP_Reddock", new[] { 100f, 0f, 0f }, 0));

        static ChapterDryRun Run(GameSession s, string steps, out bool ok)
        {
            var run = new ChapterDryRun(s);
            ok = run.Run(ScenarioParser.ParseInline(steps, "t"));
            return run;
        }

        [Test]
        public void Escort_OfAGuide_Passes()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Party.Members.StartGuide(Gorn, "FP_Reddock", null);
            var run = Run(s, "escort Gorn to=place:FP_Reddock", out bool ok);
            Assert.That(ok, Is.True, string.Join("; ", run.Failures));
        }

        [Test]
        public void Escort_OfAFrozenNpc_Fails_UntilUnfrozen()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Party.Members.StartGuide(Gorn, "FP_Reddock", null);
            var run = Run(s, "freeze only=Gorn\nescort Gorn to=place:FP_Reddock", out bool ok);
            Assert.That(ok, Is.False);
            Assert.That(run.Failures[0], Does.Contain("frozen"));

            run = Run(s, "freeze only=Gorn\nunfreeze\nescort Gorn to=place:FP_Reddock", out ok);
            Assert.That(ok, Is.True, string.Join("; ", run.Failures));
        }

        [Test]
        public void Escort_OfAnNpcThatNeitherGuidesNorFollows_Fails()
        {
            using var f = Fixture();
            var run = Run(f.NewGame(), "escort Gorn to=place:FP_Reddock", out bool ok);
            Assert.That(ok, Is.False);
            Assert.That(run.Failures[0], Does.Contain("neither guides nor follows"));
        }
    }
}
