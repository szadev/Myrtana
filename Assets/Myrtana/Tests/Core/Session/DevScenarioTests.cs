using System;
using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session.Dev;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class DevScenarioTests
    {
        [Test]
        public void Inline_BindsPositionalAndNamedArguments()
        {
            var spec = ScenarioParser.ParseInline("safe; tp spot=deep_water_bloodfly swim=1\n# comment\ntime 22; give It_Torch 2; log \"two words\" here");
            Assert.That(spec.Errors, Is.Empty);
            Assert.That(spec.Steps.Select(s => s.Op), Is.EqualTo(new[] { "safe", "tp", "time", "give", "log" }));
            Assert.That(spec.Steps[1].Get("spot"), Is.EqualTo("deep_water_bloodfly"));
            Assert.That(spec.Steps[1].Bool("swim", false), Is.True);
            Assert.That(spec.Steps[2].Float("hour", 0), Is.EqualTo(22f));
            Assert.That(spec.Steps[3].Get("item"), Is.EqualTo("It_Torch"));
            Assert.That(spec.Steps[3].Int("n", 1), Is.EqualTo(2));
            Assert.That(spec.Steps[4].Get("text"), Is.EqualTo("two words here"));
        }

        [Test]
        public void Inline_ConditionsKeepTheirOperators_AndAliasesResolve()
        {
            var spec = ScenarioParser.ParseInline("wait until=state:Grok=Dead timeout=6; expect enclave:Geldern=Attack; teleport npc:Gorn dist=2; check hp:Grok<10");
            Assert.That(spec.Errors, Is.Empty);
            Assert.That(spec.Steps[0].Get("until"), Is.EqualTo("state:Grok=Dead"));
            Assert.That(spec.Steps[1].Get("cond"), Is.EqualTo("enclave:Geldern=Attack"));
            Assert.That(spec.Steps[2].Op, Is.EqualTo("tp"));
            Assert.That(spec.Steps[2].Get("target"), Is.EqualTo("npc:Gorn"));
            Assert.That(spec.Steps[3].Op, Is.EqualTo("expect"));
            Assert.That(ScenarioParser.ParseInline("expect partner=Kent").Steps[0].Get("cond"), Is.EqualTo("partner=Kent"));
        }

        [Test]
        public void Inline_EveryOpTakesUnless_ExpectToo()
        {
            // fix-25 okara_02: the steps of a kill quest an earlier escort already finished are skipped
            var spec = ScenarioParser.ParseInline("kill Crawler#1 hero unless=quest:Q=Success; expect given:E1 unless=quest:Q=Success; expect partner=Kent");
            Assert.That(spec.Errors, Is.Empty);
            Assert.That(spec.Steps[0].Get(OpSpec.Unless), Is.EqualTo("quest:Q=Success"));
            Assert.That(spec.Steps[0].Get("killer"), Is.EqualTo("hero"));
            Assert.That(spec.Steps[1].Get("cond"), Is.EqualTo("given:E1"));
            Assert.That(spec.Steps[1].Get(OpSpec.Unless), Is.EqualTo("quest:Q=Success"));
            Assert.That(spec.Steps[2].Has(OpSpec.Unless), Is.False);
        }

        [Test]
        public void Inline_ReportsUnknownOpsAndArgumentsAndMissingRequired()
        {
            var spec = ScenarioParser.ParseInline("soar away; tp spot=x colour=red; give");
            Assert.That(spec.Errors.Count, Is.EqualTo(3));
            Assert.That(spec.Errors[0], Does.Contain("unknown op 'soar'"));
            Assert.That(spec.Errors[1], Does.Contain("no argument 'colour'"));
            Assert.That(spec.Errors[2], Does.Contain("needs 'item'"));
        }

        [Test]
        public void Json_AcceptsStringAndObjectSteps()
        {
            var spec = ScenarioParser.ParseJson(@"{
  // comment
  ""name"": ""fire_night"", ""verifies"": ""fire-fx"", ""fresh"": true, ""timeout"": 45,
  ""steps"": [ ""safe; time 22"", { ""do"": ""tp"", ""pos"": [1.5, 2, 3] }, { ""do"": ""shot"", ""name"": ""a"" } ]
}");
            Assert.That(spec.Errors, Is.Empty);
            Assert.That(spec.Name, Is.EqualTo("fire_night"));
            Assert.That(spec.Fresh, Is.True);
            Assert.That(spec.TimeoutSeconds, Is.EqualTo(45f));
            Assert.That(spec.Steps.Select(s => s.Op), Is.EqualTo(new[] { "safe", "time", "tp", "shot" }));
            Assert.That(ScenarioStep.TryVector(spec.Steps[2].Get("pos"), out var p), Is.True);
            Assert.That(p, Is.EqualTo(new[] { 1.5f, 2f, 3f }));
        }

        [Test]
        public void Fix119_ScenarioFiles_PinTheSeed_InlineRunsDoNot()
        {
            Assert.That(ScenarioParser.ParseJson("{ \"name\": \"x\", \"steps\": [ \"safe\" ] }").Seed, Is.EqualTo(ScenarioParser.DefaultSeed));
            Assert.That(ScenarioParser.ParseJson("{ \"name\": \"x\", \"seed\": 42, \"steps\": [ \"safe\" ] }").Seed, Is.EqualTo(42UL));
            var live = ScenarioParser.ParseJson("{ \"name\": \"x\", \"seed\": \"live\", \"steps\": [ \"safe\" ] }");
            Assert.That(live.Seed, Is.Null);
            Assert.That(live.Errors, Is.Empty);
            Assert.That(ScenarioParser.ParseJson("{ \"name\": \"x\", \"seed\": \"abc\", \"steps\": [ \"safe\" ] }").Errors[0], Does.Contain("bad seed"));
            Assert.That(ScenarioParser.ParseInline("safe").Seed, Is.Null, "the dev console leaves the game's random alone");
        }

        [Test]
        public void Json_ReportsMissingNameAndBadSteps()
        {
            Assert.That(ScenarioParser.ParseJson("{ \"steps\": [ 3 ] }").Errors.Count, Is.EqualTo(2));
            Assert.That(ScenarioParser.ParseJson("not json").Errors[0], Does.StartWith("bad JSON"));
            Assert.That(ScenarioParser.ParseJson("{ \"name\": \"x\", \"steps\": [ { \"tp\": 1 } ] }").Errors[0], Does.Contain("without \"do\""));
        }

        [Test]
        public void Tokenize_QuotesGroupSpaces()
        {
            var t = ScenarioParser.Tokenize("log 'a b' text=\"c d\" e");
            Assert.That(t.Select(x => x.text), Is.EqualTo(new[] { "log", "a b", "text=c d", "e" }));
            Assert.That(ScenarioParser.SplitStatements("a; 'b;c' ; // x\n d"), Is.EqualTo(new[] { "a", "'b;c'", "d" }));
        }

        [Test]
        public void Conditions_CompareNumbersBoolsAndText()
        {
            Assert.That(ScenarioCondition.Parse("hp:Grok<10").Test("3"), Is.True);
            Assert.That(ScenarioCondition.Parse("hp:Grok<10").Test("30"), Is.False);
            var c = ScenarioCondition.Parse("state:Bloodfly#01ea064d=Dead");
            Assert.That((c.Kind, c.Arg, c.Op, c.Value), Is.EqualTo(("state", "Bloodfly#01ea064d", "=", "Dead")));
            Assert.That(c.Test("dead"), Is.True);
            Assert.That(ScenarioCondition.Parse("swimming").Test("True"), Is.True);
            Assert.That(ScenarioCondition.Parse("!dialog").Test("False"), Is.True);
            Assert.That(ScenarioCondition.Parse("dist:Gorn<=3").Test("3.0"), Is.True);
            Assert.That(ScenarioCondition.Parse("key:hero~Wade").Test("Hero_Wade_None_None_P0_Move_N_Fwd_00"), Is.True);
            Assert.That(ScenarioCondition.Parse("quest:Gorn_FreeHim!=Running").Test("Success"), Is.True);
            Assert.That(ScenarioCondition.TryParse("!hp:x<3", out _, out var e), Is.False);
            Assert.That(e, Does.Contain("'!'"));
        }

        [Test]
        public void QuestWriter_ArrivalEntryIsTheAskRightAfterTheEscort()
        {
            // fix-38 (r27 morasul_01): the generated scenario waits for the arrival entry before the ask
            var steps = ScenarioParser.ParseInline("escort Cruz to=place:X near=5; expect dist:Cruz<=12; tp npc:Cruz dist=1.5; ask Cruz E1 given=1").Steps;
            Assert.That(QuestScenarioWriter.ArrivalEntry(steps, 0, "Cruz"), Is.Null, "an expect comes first");
            steps = ScenarioParser.ParseInline("escort Cruz to=place:X near=5; tp npc:Cruz dist=1.5; log x; ask Cruz E1 given=1").Steps;
            Assert.That(QuestScenarioWriter.ArrivalEntry(steps, 0, "Cruz"), Is.EqualTo("E1"));
            Assert.That(QuestScenarioWriter.ArrivalEntry(steps, 0, "Kent"), Is.Null);
        }

        [Test]
        public void Conditions_DevSpawnIdsKeepTheirTilde()
        {
            // fix-38 (r27 sneak_knockout, npc_wolves_hunter, npc_orcs_rebels): "~dev01" is part of the actor id
            var c = ScenarioCondition.Parse("state:Kent~dev01=Down");
            Assert.That(c.Kind, Is.EqualTo("state"));
            Assert.That(c.Arg, Is.EqualTo("Kent~dev01"));
            Assert.That(c.Op, Is.EqualTo("="));
            Assert.That(c.Test("Down"), Is.True);
            var r = ScenarioCondition.Parse("routine:Kent~dev01");
            Assert.That(r.Arg, Is.EqualTo("Kent~dev01"));
            Assert.That(r.Test("True"), Is.True);
            var f = ScenarioCondition.Parse("!fighting:Orc_Elite_01~dev01");
            Assert.That(f.Arg, Is.EqualTo("Orc_Elite_01~dev01"));
            Assert.That(f.Test("False"), Is.True);
            var t = ScenarioCondition.Parse("target:Orc_Elite_01~dev01=KingsGuard_01~dev02");
            Assert.That(t.Arg, Is.EqualTo("Orc_Elite_01~dev01"));
            Assert.That(t.Value, Is.EqualTo("KingsGuard_01~dev02"));
            var k = ScenarioCondition.Parse("lastmove~PowerAttack");
            Assert.That(k.Op, Is.EqualTo("~"));
            Assert.That(ScenarioCondition.Parse("key:Kent~dev01~Wade").Value, Is.EqualTo("Wade"));
        }

        [Test]
        public void Vars_ResolveKnownAndReportMissing()
        {
            var vars = new Dictionary<string, string> { ["fly"] = "Bloodfly~dev01", ["spot.npc"] = "Kent" };
            var missing = new List<string>();
            Assert.That(ScenarioVars.Resolve("hp:$fly<5 $spot.npc $nope", vars, missing), Is.EqualTo("hp:Bloodfly~dev01<5 Kent $nope"));
            Assert.That(missing, Is.EqualTo(new[] { "$nope" }));
            var step = ScenarioParser.ParseInline("probe $fly").Steps[0].Map(v => ScenarioVars.Resolve(v, vars));
            Assert.That(step.Get("actor"), Is.EqualTo("Bloodfly~dev01"));
        }

        static SpotCatalog Catalog() => SpotCatalog.Parse(@"{ ""spots"": [
  { ""id"": ""deep_a"", ""kind"": ""water"", ""tags"": [""deep""], ""pos"": [100, 0, 100], ""values"": { ""score"": 1 } },
  { ""id"": ""deep_b"", ""kind"": ""water"", ""tags"": [""deep"", ""bloodfly""], ""pos"": [10, 0, 10], ""npc"": ""Bloodfly#1"",
    ""points"": { ""shore"": [12, 1, 10] }, ""values"": { ""score"": 5, ""npcDist"": 30 } },
  { ""id"": ""field"", ""kind"": ""field"", ""pos"": [0, 50, 0] } ] }");

        [Test]
        public void Spots_SelectByTagsScoreAndDistance()
        {
            var c = Catalog();
            Assert.That(c.Select(SpotQuery.Parse("?water+deep", out _)).Select(s => s.Id), Is.EqualTo(new[] { "deep_b", "deep_a" }));
            var near = SpotQuery.Parse("?deep", out _);
            near.Near = new[] { 95f, 0f, 95f };
            Assert.That(c.Select(near).First().Id, Is.EqualTo("deep_a"));
            near.MaxDistance = 8;
            Assert.That(c.Select(near).Select(s => s.Id), Is.EqualTo(new[] { "deep_a" }));
            Assert.That(c.Select(SpotQuery.Parse("?bloodfly", out _)).Single().Npc, Is.EqualTo("Bloodfly#1"));
        }

        [Test]
        public void Spots_ResolveNamedPoints_MergeAndRoundTrip()
        {
            var c = Catalog();
            Assert.That(c.TryResolve("deep_b.shore", null, out var spot, out var p), Is.True);
            Assert.That((spot.Id, p[0]), Is.EqualTo(("deep_b", 12f)));
            Assert.That(c.TryResolve("deep_b.nothing", null, out _, out _), Is.False);
            Assert.That(c.TryResolve("?field", null, out spot, out p) && p[1] == 50f, Is.True);
            var manual = SpotCatalog.Parse(@"{ ""spots"": [ { ""id"": ""field"", ""kind"": ""field"", ""pos"": [1, 2, 3], ""source"": ""manual"" } ] }");
            c.Merge(manual);
            Assert.That(c.Spots.Count, Is.EqualTo(3));
            Assert.That(c.TryGet("FIELD", out spot) && spot.Source == "manual", Is.True);
            var back = SpotCatalog.Parse(c.ToJson());
            Assert.That(back.Spots.Count, Is.EqualTo(3));
            Assert.That(back.TryGet("deep_b", out spot) && spot.Value("npcDist") == 30f && spot.Points["shore"][0] == 12f, Is.True);
        }

        [Test]
        public void DevNpcs_TemplatesIdsAndAttitude()
        {
            var combat = new NpcCombatDef(5, 10, "Bloodfly", false, boss: true, animal: true, hasEnclave: true, enclave: "Silden");
            var defs = new[]
            {
                new NpcDef(new ActorId("Bloodfly#b2"), default, null, "Bloodfly", combat: combat),
                new NpcDef(new ActorId("Bloodfly#a1"), default, null, "Bloodfly", combat: combat),
                new NpcDef(new ActorId("Gorn"), default, "Gorn", "Human"),
            };
            Assert.That(DevNpcs.FindTemplate(defs, "Bloodfly").Id.Name, Is.EqualTo("Bloodfly#a1"));
            Assert.That(DevNpcs.FindTemplate(defs, "gorn").Id.Name, Is.EqualTo("Gorn"));
            Assert.That(DevNpcs.FindTemplate(defs, "Human").Id.Name, Is.EqualTo("Gorn"));
            Assert.That(DevNpcs.FindTemplate(defs, "Troll"), Is.Null);

            var taken = new HashSet<ActorId> { new ActorId("Bloodfly~dev01") };
            var id = DevNpcs.NewId("Bloodfly#a1", taken.Contains);
            Assert.That(id.Name, Is.EqualTo("Bloodfly~dev02"));
            Assert.That(DevNpcs.IsDevSpawn(id), Is.True);

            var def = DevNpcs.CreatureFrom(defs[1], id, new WorldPose(1, 2, 3, 90), DevNpcs.ParseAttitude("neutral"));
            Assert.That(def.Id, Is.EqualTo(id));
            Assert.That(def.Combat.AttitudeToPlayer, Is.EqualTo(Attitude.Neutral));
            Assert.That(def.Combat.Boss || def.Combat.HasEnclave, Is.False);
            Assert.That(def.Combat.Level, Is.EqualTo(5));
            Assert.That(DevNpcs.ParseAttitude("default"), Is.Null);
            Assert.That(DevNpcs.ParseAttitude("Hostile"), Is.EqualTo(Attitude.Hostile));
        }

        [Test]
        public void Lookup_RanksIdsAndLocalizedNames()
        {
            var items = new[] { ("It_1H_Sword_02", "Ржавый меч"), ("It_1H_Sword_01", "Меч"), ("It_2H_Axe_01", "Топор"), ("It_Torch", "Факел") };
            Assert.That(DevLookup.Rank(items, "It_1H_Sword_01")[0], Is.EqualTo("It_1H_Sword_01"));
            Assert.That(DevLookup.Rank(items, "меч"), Is.EqualTo(new[] { "It_1H_Sword_01", "It_1H_Sword_02" }));
            Assert.That(DevLookup.Rank(items, "torch")[0], Is.EqualTo("It_Torch"));
            Assert.That(DevLookup.Rank(items, "sword 02"), Is.EqualTo(new[] { "It_1H_Sword_02" }));
            Assert.That(DevLookup.Best(items, "axe", out int others), Is.EqualTo("It_2H_Axe_01"));
            Assert.That(others, Is.Zero);
            Assert.That(DevLookup.Best(items, "bow", out _), Is.Null);
            Assert.That(DevLookup.TemplateNames(new[] { "Wolf#1", "Wolf#2", "Gorn" }), Is.EqualTo(new[] { "Wolf", "Gorn" }));
        }

        [Test]
        public void Completion_OpsThenValuesByKind()
        {
            var c = new DevCompletion
            {
                Values = kind => kind == "item" ? new[] { "It_Torch", "It_1H_Sword_01" } : kind == "npc" ? new[] { "Gorn", "Grok", "Wolf" } :
                                 kind == "spot" ? new[] { "wall_torch", "campfire" } : new string[0],
            };
            Assert.That(c.Complete("gi"), Is.EqualTo(new[] { "give" }));
            Assert.That(c.Complete("he"), Does.Contain("heal").And.Contains("help"));
            Assert.That(c.Complete("give tor"), Is.EqualTo(new[] { "It_Torch" }));
            Assert.That(c.Complete("safe; spawn G"), Is.EqualTo(new[] { "Gorn", "Grok" }));
            Assert.That(c.Complete("tp spot=wall"), Is.EqualTo(new[] { "spot=wall_torch" }));
            Assert.That(c.Complete("quest Gorn_FreeHim Su"), Is.EqualTo(new[] { "Success" }));
            Assert.That(c.Complete("time 2"), Is.Empty);
            Assert.That(DevCompletion.CommonPrefix(new[] { "Gorn", "Grok" }), Is.EqualTo("G"));
            Assert.That(ScenarioParser.ParseInline("heal; perk Perk_1H_1; run fire_night; spell fireball").Steps.Select(x => x.Op),
                        Is.EqualTo(new[] { "restore", "learn", "scenario", "spell" }));
        }

        [Test]
        public void Completion_MatchesLocalizedNames()
        {
            var c = new DevCompletion
            {
                Values = kind => kind == "item" ? new[] { "It_Torch", "It_1H_Sword_01", "It_1H_Sword_Rust_01" } : kind == "npc" ? new[] { "Gorn", "Wolf" } : new string[0],
                Names = kind => kind == "item"
                    ? new[] { ("It_Torch", "Факел"), ("It_Torch", "Torch"), ("It_1H_Sword_01", "Меч"), ("It_1H_Sword_01", "Sword"),
                              ("It_1H_Sword_Rust_01", "Ржавый меч"), ("It_1H_Sword_Rust_01", "Rusty sword") }
                    : kind == "npc" ? new[] { ("Wolf", "Волк"), ("Wolf", "Wolf") } : new (string, string)[0],
            };
            Assert.That(c.Complete("give меч"), Is.EqualTo(new[] { "It_1H_Sword_01", "It_1H_Sword_Rust_01" }));
            Assert.That(c.Label("It_1H_Sword_Rust_01"), Is.EqualTo("It_1H_Sword_Rust_01 (Ржавый меч)"));
            Assert.That(c.Complete("give rusty"), Is.EqualTo(new[] { "It_1H_Sword_Rust_01" }));
            Assert.That(c.Complete("spawn волк"), Is.EqualTo(new[] { "Wolf" }));
            Assert.That(c.Complete("give tor"), Is.EqualTo(new[] { "It_Torch" }), "ids still first");
            var tab = new DevTabCycle();
            string line = tab.Tab("give меч", l => c.Complete(l));
            Assert.That(line, Is.EqualTo("give It_1H_Sword_01"));
            Assert.That(tab.Tab(line, l => c.Complete(l)), Is.EqualTo("give It_1H_Sword_Rust_01"));
        }

        [Test]
        public void Tab_CyclesThroughFullMatches()
        {
            var c = new DevCompletion { Values = kind => kind == "npc" ? new[] { "Gorn", "Grok", "Wolf" } : new string[0] };
            var tab = new DevTabCycle();
            string line = tab.Tab("safe; spawn G", l => c.Complete(l));
            Assert.That(line, Is.EqualTo("safe; spawn Gorn"));
            line = tab.Tab(line, l => c.Complete(l));
            Assert.That(line, Is.EqualTo("safe; spawn Grok"));
            line = tab.Tab(line, l => c.Complete(l));
            Assert.That(line, Is.EqualTo("safe; spawn Gorn"), "wraps around");
            Assert.That(tab.Tab(line, l => c.Complete(l), backwards: true), Is.EqualTo("safe; spawn Grok"));
            Assert.That(tab.Tab("spawn W", l => c.Complete(l)), Is.EqualTo("spawn Wolf "), "edited line: new cycle, one match + space");
            Assert.That(tab.Tab("time 2", l => c.Complete(l)), Is.EqualTo("time 2"));
            Assert.That(tab.Matches, Is.Empty);
            Assert.That(DevCompletion.Replace("tp spot=wa", "wall_torch"), Is.EqualTo("tp spot=wall_torch"));
        }

        [Test]
        public void Shot_FramesTheTargetFromTheChosenSide()
        {
            Assert.That(ShotFraming.TryViewOffset("Back", out float back) && back == 180f, Is.True);
            Assert.That(ShotFraming.TryViewOffset("above", out _), Is.False);
            // a target at the origin facing +x (yaw 90): "front" puts the camera on +x, "left" on its left (+z)
            ShotFraming.CameraPosition(0, 0, 0, 90f, 0f, 2f, 1.6f, out float x, out float y, out float z);
            Assert.That(x, Is.EqualTo(2f).Within(1e-4)); Assert.That(y, Is.EqualTo(1.6f)); Assert.That(z, Is.EqualTo(0f).Within(1e-4));
            ShotFraming.TryViewOffset("left", out float left);
            ShotFraming.CameraPosition(0, 0, 0, 90f, left, 2f, 1.6f, out x, out _, out z);
            Assert.That(x, Is.EqualTo(0f).Within(1e-4)); Assert.That(z, Is.EqualTo(2f).Within(1e-4));
            Assert.That(ScenarioParser.ParseInline("shot gorn view=orbit dist=3 target=npc:Gorn").Ok, Is.True);
        }

        [Test]
        public void WalkTo_ReachedStuckAndTimedOut()
        {
            var w = new WalkToProgress(1.5f, 15f);
            Assert.That(w.Update(10f, 0f), Is.EqualTo(WalkToState.Walking));
            Assert.That(w.Update(8f, 1f), Is.EqualTo(WalkToState.Walking));
            Assert.That(w.Update(1.2f, 3f), Is.EqualTo(WalkToState.Reached));

            // r29 hammerclan_04: he stood at the fold without getting closer
            var stuck = new WalkToProgress(1.5f, 15f);
            stuck.Update(5f, 0f);
            Assert.That(stuck.Update(4.8f, 3f), Is.EqualTo(WalkToState.Walking), "0.2 m is no progress yet");
            Assert.That(stuck.Update(4.7f, 4.5f), Is.EqualTo(WalkToState.Stuck));

            var slow = new WalkToProgress(1.5f, 6f);
            float left = 20f;
            WalkToState st = WalkToState.Walking;
            for (float t = 0f; t < 10f && st == WalkToState.Walking; t += 1f, left -= 1f) st = slow.Update(left, t);
            Assert.That(st, Is.EqualTo(WalkToState.TimedOut));
        }

        [Test]
        public void WalkTo_ExpectBlocked_PassesOnlyWhenHeHeldAndDidNotClimb()
        {
            Assert.That(WalkToProgress.Passed(WalkToState.Reached, false, 3f), Is.True);
            Assert.That(WalkToProgress.Passed(WalkToState.Stuck, false, 0f), Is.False);
            Assert.That(WalkToProgress.Passed(WalkToState.Stuck, true, 0.4f), Is.True, "a step at the cliff foot is fine");
            Assert.That(WalkToProgress.Passed(WalkToState.TimedOut, true, 2.5f), Is.False, "climbing the cliff");
            Assert.That(WalkToProgress.Passed(WalkToState.Reached, true, 0f), Is.False, "walked through");
        }

        [Test]
        public void WalkTo_IsAKnownOp()
        {
            var r = ScenarioParser.ParseInline("walkto 434.7,1086.1 secs=12 expect=blocked");
            Assert.That(r.Ok, Is.True);
        }

        /// <summary>Every scenario in Tools/Scenarios parses and its spot references exist in spots.json + spots.manual.json.</summary>
        [Test]
        public void RepoScenarios_ParseAndReferenceKnownSpots()
        {
            string dir = null;
            for (var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); d != null && dir == null; d = d.Parent)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Tools", "Scenarios"))) dir = System.IO.Path.Combine(d.FullName, "Tools", "Scenarios");
            if (dir == null && System.IO.Directory.Exists(System.IO.Path.Combine("Tools", "Scenarios"))) dir = System.IO.Path.Combine("Tools", "Scenarios");
            if (dir == null) Assert.Ignore("Tools/Scenarios not found");
            var spots = new SpotCatalog();
            foreach (var f in new[] { "spots.json", "spots.manual.json" })
            {
                var path = System.IO.Path.Combine(dir, f);
                if (System.IO.File.Exists(path)) spots.Merge(SpotCatalog.Parse(System.IO.File.ReadAllText(path)));
            }
            var files = System.IO.Directory.GetFiles(dir, "*.json").Where(p => !System.IO.Path.GetFileName(p).StartsWith("spots")).ToList();
            Assert.That(files, Is.Not.Empty);
            var problems = new List<string>();
            foreach (var file in files)
            {
                var spec = ScenarioParser.ParseJson(System.IO.File.ReadAllText(file), System.IO.Path.GetFileNameWithoutExtension(file));
                problems.AddRange(spec.Errors.Select(e => spec.Name + ": " + e));
                foreach (var step in spec.Steps)
                foreach (var value in step.Args.Values)
                {
                    string reference = step.Op == "tp" && step.Args.TryGetValue("spot", out var sp) && sp == value ? value
                        : value != null && value.StartsWith("spot:") ? value.Substring(5) : null;
                    if (reference != null && !reference.StartsWith("?") && !spots.TryResolve(reference, null, out _, out _))
                        problems.Add($"{spec.Name}: unknown spot '{reference}' in '{step.Source}'");
                }
                foreach (var step in spec.Steps.Where(x => x.Op == "expect" || x.Op == "wait" && x.Has("until")))
                    if (!ScenarioCondition.TryParse(step.Get("cond") ?? step.Get("until"), out _, out var err)) problems.Add(spec.Name + ": " + err);
            }
            Assert.That(problems, Is.Empty);
        }
    
        [Test]
        public void EscortLegs_RouteHysteresisAndStuckNextToTheWaitingGuide_FixFortyEight()
        {
            // r29 hammerclan_01/02: the hero circled 10.0 m from the waiting Mort (route leg <-> NavMesh leg every frame)
            Assert.That(EscortLegRules.UseRoute(true, 10.5f), Is.True);
            Assert.That(EscortLegRules.UseRoute(true, 10f), Is.False);
            Assert.That(EscortLegRules.UseRoute(false, 12f), Is.False, "off the route it stays off up to 15 m");
            Assert.That(EscortLegRules.UseRoute(false, 15.5f), Is.True);
            Assert.That(EscortLegRules.StuckTeleportToGuide(true, true, 10f), Is.True);
            Assert.That(EscortLegRules.StuckTeleportToGuide(true, false, 10f), Is.False, "a walking guide: route point");
            Assert.That(EscortLegRules.StuckTeleportToGuide(false, true, 10f), Is.False, "leading a follower");
            Assert.That(EscortLegRules.StuckTeleportToGuide(true, true, 5f), Is.False, "close enough: it walks on");
            Assert.That(EscortLegRules.StuckTeleportToGuide(true, true, 40f), Is.False);
        }

        [Test]
        public void PastTheTarget_StraightOnFirst_ThenTurnsBothWays_ThenStands_FixOneEightySix()
        {
            Assert.That(EscortLegRules.PastTurn(0), Is.EqualTo(0f));
            Assert.That(EscortLegRules.PastTurn(1), Is.EqualTo(45f));
            Assert.That(EscortLegRules.PastTurn(2), Is.EqualTo(-45f));
            Assert.That(EscortLegRules.PastTurn(6), Is.EqualTo(-135f));
            Assert.That(EscortLegRules.PastTurn(7), Is.Null, "all tried: stand at the target");
            Assert.That(EscortLegRules.PastTurn(-1), Is.Null);
        }

        [Test]
        public void DrawCutByHits_FreezesTheAttackersEveryThirdCut_FixFortyEight()
        {
            Assert.That(ScenarioPacing.DrawCutsBeforeFreeze, Is.EqualTo(3));
            Assert.That(ScenarioPacing.AttackerFreezeSeconds, Is.GreaterThanOrEqualTo(5f), "G3's shortest freeze");
        }

        /// <summary>fix-48: G3 perk ids in the repo scenarios' <c>learn</c> steps (r29: Perk_2H_1 / Perk_CrossBow_1 do not exist;
        /// two-handed swords need Perk_Axe_1, crossbows Perk_Crossbow_1).</summary>
        [Test]
        public void RepoScenarios_LearnOnlyG3Perks()
        {
            // CP 1.75 skills (content pack skills.json ids)
            var known = new HashSet<string>((
                "1H1H_1 1H1H_2 1H1H_SecretBonus 1H_1 1H_2 1H_3 Acrobatic Assassin Axe_1 Axe_2 Axe_3 Barter Bow_1 Bow_2 Bow_3 " +
                "Crossbow_1 Crossbow_2 Crossbow_3 Darkmage Druid Excuse ExplosiveArrow FireArrow Firemage HeavyArmor Hunter_1 Hunter_2 " +
                "ImmuneToDisease ImmuneToPoison KnockOut Learn LightArmor LockPick_1 LockPick_2 LockPick_3 MasterGladiator MasterMage " +
                "MasterThief Mining OrcHunter OrcSlayer Paladin PickPocket_1 PickPocket_2 PickPocket_3 PoisonArrow PoisonBlade PotionHP " +
                "PotionMP PotionPerm PotionPoison PotionTransform ResistCold ResistHeat SharpArrow SharpenBlade Shield_1 Shield_2 Smith_1 " +
                "Smith_2 Smith_3 Sneak_1 Sneak_2 Sprinter StaffMagic Staff_1 Staff_2 Staff_3 TrophyFur TrophySkin TrophyTeeth Watermage")
                .Split(' ').Select(x => "Perk_" + x), StringComparer.Ordinal);
            string dir = null;
            for (var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory); d != null && dir == null; d = d.Parent)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "Tools", "Scenarios"))) dir = System.IO.Path.Combine(d.FullName, "Tools", "Scenarios");
            if (dir == null) Assert.Ignore("Tools/Scenarios not found");
            var problems = new List<string>();
            foreach (var file in System.IO.Directory.GetFiles(dir, "*.json").Where(p => !System.IO.Path.GetFileName(p).StartsWith("spots")))
            {
                var spec = ScenarioParser.ParseJson(System.IO.File.ReadAllText(file), System.IO.Path.GetFileNameWithoutExtension(file));
                foreach (var step in spec.Steps.Where(x => x.Op == "learn"))
                foreach (var skill in step.Get("skills").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
                    if (skill.StartsWith("Perk_") && !known.Contains(skill)) problems.Add($"{spec.Name}: {skill}");
            }
            Assert.That(problems, Is.Empty);
        }
}
}
