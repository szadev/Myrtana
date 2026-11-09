using System.Collections.Generic;
using System.Linq;
using Myrtana.Session.Dev;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-54: checkpoints/resume, the NPC dialog handler, loadouts, strict item names (r30 tooling gaps).</summary>
    public class ScenarioRunSupportTests
    {
        [Test]
        public void Progress_RoundTripsThroughJson()
        {
            var p = new ScenarioProgress { Scenario = "sideq_okara_01", State = "running", Step = 40, Steps = 133, Loadout = "bow",
                                           Slot = "scn_sideq_okara_01", ResumeStep = 38, SavedUtc = "2026-10-15 06:02:00Z" };
            p.Vars["g1"] = "Goblin~dev01";
            p.Vars["@calm"] = "Randall,Kent";
            var back = ScenarioProgress.Parse(p.ToJson());
            Assert.That(back.Scenario, Is.EqualTo("sideq_okara_01"));
            Assert.That(back.Step, Is.EqualTo(40));
            Assert.That(back.ResumeStep, Is.EqualTo(38));
            Assert.That(back.Loadout, Is.EqualTo("bow"));
            Assert.That(back.Vars["G1"], Is.EqualTo("Goblin~dev01"), "vars are case-insensitive like the runner's");
            Assert.That(back.Vars["@calm"], Is.EqualTo("Randall,Kent"));
            Assert.That(back.CanResume, Is.True);
            Assert.That(ScenarioProgress.Parse("not json"), Is.Null);
            Assert.That(new ScenarioProgress { Scenario = "x", Slot = "scn_x_ok", ResumeStep = 0 }.CanResume, Is.False, "a pass save is no resume point");
        }

        [Test]
        public void Checkpoints_SlotNamesTimingAndStartStep()
        {
            Assert.That(ScenarioCheckpoints.ValidSlot("scn_chapter_04"), Is.True);
            Assert.That(ScenarioCheckpoints.ValidSlot("../evil"), Is.False);
            Assert.That(ScenarioCheckpoints.ValidSlot("a b"), Is.False);
            Assert.That(ScenarioCheckpoints.RunSlot("sideq_okara_01"), Is.EqualTo("scn_sideq_okara_01"));
            Assert.That(ScenarioCheckpoints.PassSlot("inline"), Is.EqualTo("scn_inline_ok"));
            Assert.That(ScenarioCheckpoints.ValidSlot(ScenarioCheckpoints.RunSlot("x y/z")), Is.True);
            Assert.That(ScenarioCheckpoints.Due(14 * 60f, 15f), Is.False);
            Assert.That(ScenarioCheckpoints.Due(15 * 60f, 15f), Is.True);
            Assert.That(ScenarioCheckpoints.Due(3600f, 0f), Is.False, "off");
            Assert.That(ScenarioCheckpoints.TryStartIndex(1, 10, out int i, out _) && i == 0, Is.True);
            Assert.That(ScenarioCheckpoints.TryStartIndex(7, 10, out i, out _) && i == 6, Is.True);
            Assert.That(ScenarioCheckpoints.TryStartIndex(11, 10, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("10 steps"));
        }

        [Test]
        public void DialogHandler_ExpectedThenTopicThenEnd()
        {
            var options = new List<(string, bool)> { ("BPANKRATZ30", true), ("BPANKRATZ31138", false), ("BPANKRATZ31133", false) };
            var taken = new HashSet<string>();
            Assert.That(DialogAutoAnswer.Pick(options, new[] { "BPANKRATZ31133" }, taken, out var why), Is.EqualTo(2));
            Assert.That(why, Is.EqualTo("expected"));
            Assert.That(DialogAutoAnswer.Pick(options, null, taken, out why), Is.EqualTo(1), "the first non-END option");
            Assert.That(why, Is.EqualTo("topic"));
            taken.Add("BPANKRATZ31138");
            Assert.That(DialogAutoAnswer.Pick(options, null, taken, out _), Is.EqualTo(2), "not the same topic again");
            taken.Add("BPANKRATZ31133");
            Assert.That(DialogAutoAnswer.Pick(options, null, taken, out why), Is.EqualTo(0));
            Assert.That(why, Is.EqualTo("END"));
            var trader = Enumerable.Range(0, 6).Select(n => ("T" + n, false)).Concat(new[] { ("END", true) }).ToList();
            var t2 = new HashSet<string> { "T0", "T1", "T2" };
            Assert.That(DialogAutoAnswer.Pick(trader, null, t2, out why), Is.EqualTo(6), "after MaxTopics topics: END");
            Assert.That(DialogAutoAnswer.Pick(new List<(string, bool)> { ("ONLY", false) }, null, new HashSet<string> { "ONLY" }, out why), Is.EqualTo(0));
            Assert.That(why, Is.EqualTo("only"));
            Assert.That(DialogAutoAnswer.Pick(new List<(string, bool)>(), null, taken, out _), Is.EqualTo(-1));
        }

        [Test]
        public void DialogHandler_GateGuardAnswerFromFixFiftyThree_NoFightTopics()
        {
            var topic = new TalkChoice(false, false, false);
            var friend = new TalkChoice(false, true, false);
            var foe = new TalkChoice(false, false, true);
            var end = new TalkChoice(true, false, false);
            var taken = new HashSet<string>();
            // Sivert: "Friend or foe?" - Friend! switches him off, Foe! is a duel; no End
            var sivert = new List<(string, TalkChoice)> { ("FOE", foe), ("FRIEND", friend) };
            Assert.That(DialogAutoAnswer.Pick(sivert, null, taken, out var why), Is.EqualTo(1));
            Assert.That(why, Is.EqualTo("guard"));
            // Garik: a topic first in the menu, "Why are you stopping me?" passes; it wins over the first topic
            var garik = new List<(string, TalkChoice)> { ("TOPIC", topic), ("WHY", friend), ("END", end) };
            Assert.That(DialogAutoAnswer.Pick(garik, null, taken, out why), Is.EqualTo(1));
            Assert.That(DialogAutoAnswer.Pick(garik, new[] { "TOPIC" }, taken, out why), Is.EqualTo(0), "the scenario's expected answer first");
            Assert.That(why, Is.EqualTo("expected"));
            // a topic never starts a fight; only fights on offer: nothing (abort)
            Assert.That(DialogAutoAnswer.Pick(new List<(string, TalkChoice)> { ("FOE", foe), ("T", topic) }, null, taken, out why), Is.EqualTo(1));
            Assert.That(DialogAutoAnswer.Pick(new List<(string, TalkChoice)> { ("FOE", foe) }, null, taken, out why), Is.EqualTo(-1));
            Assert.That(DialogAutoAnswer.MaxChoices, Is.EqualTo(DialogAutoAnswer.MaxTopics + UnaskedTalk.MaxChoices));
        }

        [Test]
        public void DialogHandler_GraceThenActThenForceCloseAfterThirtySeconds()
        {
            Assert.That(DialogAutoAnswer.Next("AwaitingLine", 0.5f), Is.EqualTo(DialogAct.Wait), "a following wait until=dialog takes it over");
            Assert.That(DialogAutoAnswer.Next("AwaitingLine", 3f), Is.EqualTo(DialogAct.CompleteLine));
            Assert.That(DialogAutoAnswer.Next("AwaitingChoice", 3f), Is.EqualTo(DialogAct.Choose));
            Assert.That(DialogAutoAnswer.Next("Running", 3f), Is.EqualTo(DialogAct.Wait));
            Assert.That(DialogAutoAnswer.Next("AwaitingTime", 30f), Is.EqualTo(DialogAct.ForceClose));
            Assert.That(DialogAutoAnswer.IsDialogOp("ask"), Is.True);
            Assert.That(DialogAutoAnswer.IsDialogOp("escort"), Is.False, "NPCs that talk during an escort are answered");
            Assert.That(DialogAutoAnswer.IsDialogCondition("partner"), Is.True);
            Assert.That(DialogAutoAnswer.IsDialogCondition("given"), Is.False);
        }

        [Test]
        public void DialogHandler_OffByDefaultWhereAScenarioTestsNpcTalks()
        {
            Assert.That(DialogAutoAnswer.DefaultOn(ScenarioParser.ParseInline("tp npc:Kent; wait until=dialog timeout=5 soft=1; lines").Steps), Is.True);
            Assert.That(DialogAutoAnswer.DefaultOn(ScenarioParser.ParseInline("forget BPANKRATZ31157; wait 6; expect partner=Kent").Steps), Is.False);
            Assert.That(DialogAutoAnswer.DefaultOn(ScenarioParser.ParseInline("draw; wait 2; expect !dialog").Steps), Is.False);
            var spec = ScenarioParser.ParseJson("{\"name\":\"x\",\"autodialog\":true,\"answers\":[\"A1\",\"A2\"],\"checkpoint\":10,\"loadout\":\"bow\",\"steps\":[\"forget A1\"]}");
            Assert.That(spec.Errors, Is.Empty);
            Assert.That(spec.AutoDialog, Is.True);
            Assert.That(spec.Answers, Is.EqualTo(new[] { "A1", "A2" }));
            Assert.That(spec.CheckpointMinutes, Is.EqualTo(10f));
            Assert.That(spec.Loadout, Is.EqualTo("bow"));
            Assert.That(ScenarioParser.ParseJson("{\"name\":\"x\",\"checkpoint\":true,\"steps\":[\"log a\"]}").CheckpointMinutes, Is.EqualTo(ScenarioCheckpoints.DefaultMinutes));
            Assert.That(ScenarioParser.ParseJson("{\"name\":\"x\",\"loadout\":\"lance\",\"steps\":[\"log a\"]}").Ok, Is.False);
        }

        [Test]
        public void NewOps_ParseWithSlotsAnswersAndAtLeast()
        {
            var spec = ScenarioParser.ParseInline("save scn_ch04; load scn_ch04; save; loadout crossbow; autodialog off answers=E1,E2; dialogstate; " +
                                                  "attr STR 100 atleast=1; fight Wolf style=bow; escort Mort to=place:X answer=SKAL1; wait until=!frozen timeout=60");
            Assert.That(spec.Errors, Is.Empty);
            Assert.That(spec.Steps[0].Get("slot"), Is.EqualTo("scn_ch04"));
            Assert.That(spec.Steps[2].Has("slot"), Is.False, "plain save = QuickSave");
            Assert.That(spec.Steps[3].Get("name"), Is.EqualTo("crossbow"));
            Assert.That(spec.Steps[4].Bool("on", true), Is.False);
            Assert.That(spec.Steps[4].Get("answers"), Is.EqualTo("E1,E2"));
            Assert.That(spec.Steps[6].Bool("atleast", false), Is.True);
            Assert.That(spec.Steps[7].Get("style"), Is.EqualTo("bow"));
            Assert.That(spec.Steps[8].Get(OpSpec.Answer), Is.EqualTo("SKAL1"), "every op takes answer=");
            Assert.That(ScenarioParser.ParseInline("expect partner=Kent answer=X").Steps[0].Get(OpSpec.Answer), Is.EqualTo("X"));
        }

        [Test]
        public void Loadouts_TenInRotationWithG3PerksAndItemIds()
        {
            Assert.That(ScenarioLoadout.All.Select(l => l.Name),
                        Is.EqualTo(new[] { "1H", "shield", "dual", "2H", "axe", "staff", "bow", "crossbow", "spells", "fists" }));
            Assert.That(ScenarioLoadout.Find("xbow").Name, Is.EqualTo("crossbow"));
            Assert.That(ScenarioLoadout.Find("2h").Name, Is.EqualTo("2H"));
            Assert.That(ScenarioLoadout.Find("16").Name, Is.EqualTo("bow"), "rotation index wraps (loop2 IDX=6 = bow)");
            Assert.That(ScenarioLoadout.Find("lance"), Is.Null);
            Assert.That(ScenarioLoadout.Find("bow").Style, Is.EqualTo(AttackStyle.Bow));
            Assert.That(ScenarioLoadout.Find("spells").Spell, Is.EqualTo("It_Spell_Fireball"));
            foreach (var lo in ScenarioLoadout.All)
            {
                var spec = ScenarioParser.ParseInline(lo.Ops);
                Assert.That(spec.Errors, Is.Empty, lo.Name);
                foreach (var st in spec.Steps.Where(s => s.Op == "learn"))
                foreach (var perk in st.Get("skills").Split(','))
                    Assert.That(perk, Does.Match("^Perk_(1H_1|1H1H_1|Shield_1|Axe_1|Axe_2|Staff_1|Bow_1|Crossbow_1)$"), lo.Name);
                // exact CP 1.75 item ids (r30: "It_Arrow" is no id, the ammunition is "Arrow"/"Bolt")
                foreach (var st in spec.Steps.Where(s => s.Op == "give" || s.Op == "equip"))
                    Assert.That(st.Get("item"), Does.Match("^(It_[A-Za-z0-9_]+|Arrow|Bolt)$"), lo.Name);
                Assert.That(spec.Steps.Any(s => s.Op == "give" && s.Get("item").StartsWith("It_Arrow")), Is.False);
            }
            Assert.That(ScenarioLoadout.Find("crossbow").Ops, Does.Contain("Perk_Crossbow_1").And.Contain("It_Crossbow_01").And.Contain("Bolt"));
            Assert.That(ScenarioLoadout.Find("2H").Ops, Does.Contain("Perk_Axe_1"), "G3: two-handed swords need the axe perk");
        }

        [Test]
        public void ItemLookup_ReportsLooseAndAmbiguousMatches_FixFiftyFour()
        {
            var items = new[] { "Arrow", "Bolt", "It_Book_Arrow", "It_Staff_Mana_01", "It_Staff_Mana_02", "It_1H_Sword_01", "It_2H_Sword_01", "It_Torch" }
                        .Select(i => (i, (string)null)).ToList();
            var r = DevLookup.Resolve(items, "It_Arrow");
            Assert.That(r.Match, Is.EqualTo(LookupMatch.Loose), "r30: It_Arrow silently became It_Book_Arrow");
            Assert.That(r.Safe, Is.False);
            Assert.That(r.Problem, Does.Contain("It_Book_Arrow"));
            Assert.That(DevLookup.Resolve(items, "It_Staff_01").Match, Is.EqualTo(LookupMatch.Loose), "not It_Staff_Mana_01");
            r = DevLookup.Resolve(items, "Sword_01");
            Assert.That(r.Match, Is.EqualTo(LookupMatch.Ambiguous));
            Assert.That(r.Tied, Is.EquivalentTo(new[] { "It_1H_Sword_01", "It_2H_Sword_01" }));
            Assert.That(DevLookup.Resolve(items, "arrow").Match, Is.EqualTo(LookupMatch.Exact), "an id in any case");
            Assert.That(DevLookup.Resolve(items, "torch").Match, Is.EqualTo(LookupMatch.Part));
            Assert.That(DevLookup.Resolve(items, "torch").Safe, Is.True);
            Assert.That(DevLookup.Resolve(items, "It_Staff_Mana").Match, Is.EqualTo(LookupMatch.Ambiguous));
            Assert.That(DevLookup.Resolve(items, "lance").Match, Is.EqualTo(LookupMatch.None));
        }

        [Test]
        public void Draw_WaitsOutAFreeze_FixFiftyFour()
        {
            Assert.That(ScenarioPacing.DrawLimit(0f, 20f), Is.EqualTo(ScenarioPacing.DrawSeconds + 20f), "r30 wolfclan_01: IceGolem ice");
            Assert.That(ScenarioPacing.DrawLimit(0f, 500f), Is.EqualTo(ScenarioPacing.DrawSeconds + ScenarioPacing.DrawWaitFrozenSeconds));
            Assert.That(ScenarioPacing.DrawLimit(5f), Is.EqualTo(ScenarioPacing.DrawSeconds + 5f));
        }
    }
}
