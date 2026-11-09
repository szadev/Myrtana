using System.Collections.Generic;
using System.Linq;
using Myrtana.Session.Dev;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Pure parts of the main-story walk (Docs/story-walk.md): the fixpoint walk, menu paths, story targets, ops.</summary>
    public class StoryWalkTests
    {
        static WalkRule Rule(string id, WalkNeed need, params string[] effects) => new WalkRule(id, "entry", "Npc", need, effects);

        [Test]
        public void Walk_ReachesFactsInRounds_AndNamesWhatABlockedRuleMisses()
        {
            var rules = new[]
            {
                Rule("start", WalkNeed.Of("npc:Xardas"), "quest:Q=Running"),
                Rule("objective", WalkNeed.All(new[] { WalkNeed.Of("quest:Q=Running"), WalkNeed.Of("item:Robe") }), "quest:Q=Success"),
                Rule("source", WalkNeed.Always, "item:Robe"),
                Rule("blocked", WalkNeed.All(new[] { WalkNeed.Of("quest:Q=Success"), WalkNeed.Of("flag:Never") }), "flag:End"),
            };
            var walk = new StoryWalk(rules, new[] { "npc:Xardas" }).Run();
            Assert.That(walk.DepthOf("npc:Xardas"), Is.EqualTo(0));
            Assert.That(walk.DepthOf("quest:Q=Running"), Is.EqualTo(1));
            Assert.That(walk.DepthOf("quest:Q=Success"), Is.EqualTo(2));
            Assert.That(walk.Has("flag:End"), Is.False);
            Assert.That(walk.Missing(rules[3]), Is.EqualTo(new[] { "flag:Never" }));
            Assert.That(walk.ProducerOf("quest:Q=Success").Id, Is.EqualTo("objective"));
            Assert.That(walk.ChainTo("quest:Q=Success").Select(r => r.Id), Is.EqualTo(new[] { "start", "source", "objective" }));
        }

        [Test]
        public void Need_AnyAllAlwaysNever_Simplify()
        {
            Assert.That(WalkNeed.All(new[] { WalkNeed.Always, WalkNeed.Always }).Kind, Is.EqualTo(WalkNeed.NeedKind.True));
            Assert.That(WalkNeed.All(new[] { WalkNeed.Of("a"), WalkNeed.Never }).Kind, Is.EqualTo(WalkNeed.NeedKind.False));
            Assert.That(WalkNeed.Any(new[] { WalkNeed.Of("a"), WalkNeed.Always }).Kind, Is.EqualTo(WalkNeed.NeedKind.True));
            var any = WalkNeed.Any(new[] { WalkNeed.All(new[] { WalkNeed.Of("a"), WalkNeed.Of("b") }), WalkNeed.Of("c") });
            var have = new HashSet<string> { "a" };
            Assert.That(any.Holds(have.Contains), Is.False);
            Assert.That(any.Missing(have.Contains), Is.EqualTo(new[] { "b" }).Or.EqualTo(new[] { "c" }));
            have.Add("c");
            Assert.That(any.Holds(have.Contains), Is.True);
            Assert.That(any.ToString(), Is.EqualTo("a & b | c"));
        }

        [Test]
        public void PathTo_FollowsParentsRootFirst_AndStopsAtCycles()
        {
            var parents = new Dictionary<string, string> { ["FILLER13"] = "FILLER12", ["X"] = "Y", ["Y"] = "X" };
            string Parent(string id) => parents.TryGetValue(id, out var p) ? p : null;
            Assert.That(StoryScenario.PathTo("FILLER13", Parent), Is.EqualTo(new[] { "FILLER12", "FILLER13" }));
            Assert.That(StoryScenario.PathTo("FILLER12", Parent), Is.EqualTo(new[] { "FILLER12" }));
            Assert.That(StoryScenario.PathTo("X", Parent), Is.EqualTo(new[] { "Y", "X" }));
        }

        [Test]
        public void NextChoice_JumpsToTheDeepestOffered_PassesPlayed_AndReportsStuckNodes()
        {
            var path = new[] { "P", "C", "D" };
            Assert.That(StoryScenario.NextChoice(path, 0, n => n == "P", n => false, out _), Is.EqualTo(0));
            Assert.That(StoryScenario.NextChoice(path, 0, n => n == "C" || n == "P", n => false, out _), Is.EqualTo(1));
            // the parent already ran (one-shot, given earlier) and its child is not on screen: stuck at the parent
            Assert.That(StoryScenario.NextChoice(path, 0, n => false, n => n == "P", out string why), Is.EqualTo(-1));
            Assert.That(why, Does.Contain("'C'"));
            Assert.That(StoryScenario.NextChoice(path, 0, n => false, n => true, out _), Is.EqualTo(3));
            Assert.That(StoryScenario.NextChoice(path, 2, n => n == "P", n => false, out why), Is.EqualTo(-1));
            Assert.That(why, Does.Contain("'D'"));
        }

        [Test]
        public void Targets_PlaceZoneObject()
        {
            Assert.That(StoryScenario.TryParseTarget("place:Adanos_Gate", out var k, out var n) && k == "place" && n == "Adanos_Gate", Is.True);
            Assert.That(StoryScenario.TryParseTarget("ZONE:Xardas_Tower", out k, out n) && k == "zone" && n == "Xardas_Tower", Is.True);
            Assert.That(StoryScenario.TryParseTarget("obj:Erzofen", out k, out _) && k == "obj", Is.True);
            Assert.That(StoryScenario.TryParseTarget("spot:erzofen.stand", out _, out _), Is.False);
            Assert.That(StoryScenario.TryParseTarget("npc:Gorn", out _, out _), Is.False);
            Assert.That(StoryScenario.TryParseTarget("place:", out _, out _), Is.False);
            Assert.That(StoryScenario.List(" a, b,,c "), Is.EqualTo(new[] { "a", "b", "c" }));
        }

        [Test]
        public void ZoneInside_CentroidOrWidestChordForConcaveOutlines()
        {
            var square = new[] { 0f, 0f, 10f, 0f, 10f, 10f, 0f, 10f };
            Assert.That(StoryScenario.TryInside(square, out float x, out float z), Is.True);
            Assert.That(x, Is.EqualTo(5f).Within(1e-4));
            Assert.That(z, Is.EqualTo(5f).Within(1e-4));
            // a U: the centroid (in the gap) lies outside; the point found must be inside
            var u = new[] { 0f, 0f, 30f, 0f, 30f, 30f, 20f, 30f, 20f, 10f, 10f, 10f, 10f, 30f, 0f, 30f };
            Assert.That(StoryScenario.TryInside(u, out x, out z), Is.True);
            Assert.That(StoryScenario.Contains(u, x, z), Is.True, $"({x},{z})");
            Assert.That(StoryScenario.TryInside(new[] { 0f, 0f, 1f, 1f }, out _, out _), Is.False);
        }

        [Test]
        public void StoryOps_BindTheirArguments()
        {
            var spec = ScenarioParser.ParseInline("ask Xardas FILLER13 end=0 until=flag:Epilog_Adanos; played FILLER12,FILLER13; topics Xardas\n" +
                                                  "loot obj:ChestPod_02; pickup It_Teleport_VangardTempel radius=20; raid Faring max=50\n" +
                                                  "tp place:Adanos_Gate; tp zone:Xardas_Tower; quest A,B Success; flag X,Y");
            Assert.That(spec.Errors, Is.Empty);
            Assert.That(spec.Steps[0].Get("npc"), Is.EqualTo("Xardas"));
            Assert.That(spec.Steps[0].Get("entry"), Is.EqualTo("FILLER13"));
            Assert.That(spec.Steps[0].Bool("end", true), Is.False);
            Assert.That(spec.Steps[0].Get("until"), Is.EqualTo("flag:Epilog_Adanos"));
            Assert.That(spec.Steps[1].Get("entries"), Is.EqualTo("FILLER12,FILLER13"));
            Assert.That(spec.Steps[3].Get("target"), Is.EqualTo("obj:ChestPod_02"));
            Assert.That(spec.Steps[4].Float("radius", 0), Is.EqualTo(20f));
            Assert.That(spec.Steps[5].Int("max", 0), Is.EqualTo(50));
            Assert.That(spec.Steps[6].Get("target"), Is.EqualTo("place:Adanos_Gate"));
            Assert.That(spec.Steps[8].Get("id"), Is.EqualTo("A,B"));
            Assert.That(ScenarioParser.ParseInline("ask Xardas").Errors, Is.Not.Empty);
        }
    }
}
