using System.Collections.Generic;
using System.Linq;
using Myrtana.Session.Dev;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-57 (editor-runner-31): the suite's progress watchdog, pinned loadouts, the spells loadout, the ranged fight
    /// rules (kill shot counted, melee at the feet, keep drawn in escorts, sheathe after the reload) and the escort's
    /// spots next to a guide.
    /// </summary>
    public sealed class ScenarioRunnerFix57Tests
    {
        // ------------------------------------------------------------------ 1. suite watchdog

        [Test]
        public void Watchdog_ALongStepThatMovesOrLogsIsProgress_AQuietOneStalls()
        {
            var w = new ScenarioWatchdog();
            w.Reset(0f);
            Assert.That(w.Observe(1f, 31, 100, 500f, 1300f), Is.True, "first look");
            // hammerclan_04 step 31 = one escort of ~10 min: the hero walks, the step does not change
            for (int t = 2; t < 900; t += 2)
            {
                w.Observe(t, 31, 100, 500f + t * 0.5f, 1300f); // 1 m per poll: every second poll has gone 2 m
                Assert.That(w.QuietSeconds(t), Is.LessThanOrEqualTo(2f), $"walking at {t} s");
            }
            Assert.That(w.QuietSeconds(900f), Is.LessThan(5f));
            Assert.That(ScenarioWatchdog.Stalled(w.QuietSeconds(900f), 10f), Is.False, "a 15-minute step that walks is never stopped");
            // standing still but logging (defends, talks)
            Assert.That(w.Observe(1000f, 31, 101, 950f, 1300f), Is.True, "a log line");
            Assert.That(w.Observe(1001f, 31, 101, 950.5f, 1300f), Is.False, "0.5 m is no progress");
            Assert.That(w.Observe(1002f, 32, 101, 950.5f, 1300f), Is.True, "the next step");
            // nothing for 10 minutes
            for (int t = 1004; t <= 1602; t += 2) w.Observe(t, 32, 101, 950.5f, 1300f);
            Assert.That(w.QuietSeconds(1602f), Is.EqualTo(600f).Within(0.01f));
            Assert.That(ScenarioWatchdog.Stalled(w.QuietSeconds(1602f), 10f), Is.True);
            Assert.That(ScenarioWatchdog.Stalled(1e6f, 0f), Is.False, "--stall 0: never");
        }

        [Test]
        public void Heartbeat_RunningCarriesTheQuietTime_DoneAndIdlePassThrough_StalledOnceQuiet()
        {
            const string run = "running 31/41 sideq_hammerclan_04: escort Mort to=place:FP_Wolfclan near=5 secs=630";
            Assert.That(ScenarioWatchdog.Heartbeat(run, 12.7f, 10f), Is.EqualTo(run + " | quiet 12 s"));
            Assert.That(ScenarioWatchdog.Heartbeat(run, 600f, 10f), Does.StartWith("STALLED after 600 s without progress: running 31/41"));
            Assert.That(ScenarioWatchdog.Heartbeat("DONE ok x 3/3 steps 1.0 s", 9999f, 10f), Is.EqualTo("DONE ok x 3/3 steps 1.0 s"), "scenario.sh matches DONE*");
            Assert.That(ScenarioWatchdog.Heartbeat(null, 0f, 10f), Is.EqualTo("idle"));
            Assert.That(ScenarioWatchdog.DefaultStallMinutes, Is.EqualTo(10f));
        }

        // ------------------------------------------------------------------ 4. pinned loadouts

        [Test]
        public void Loadout_AScenarioThatPinsItsWeaponsIgnoresTheRotation()
        {
            Assert.That(ScenarioLoadout.Choose(null, "bow", out var note), Is.EqualTo("bow"));
            Assert.That(note, Is.Null);
            Assert.That(ScenarioLoadout.Choose(null, null, out note), Is.Null);
            Assert.That(ScenarioLoadout.Choose("own", "fists", out note), Is.Null, "r31 draw_aim_strafe [fists]: the scenario equips its own weapons");
            Assert.That(note, Does.Contain("fists ignored").And.Contain("its own weapons"));
            Assert.That(ScenarioLoadout.Choose("none", null, out note), Is.Null);
            Assert.That(note, Is.Null);
            Assert.That(ScenarioLoadout.Choose("bow", "fists", out note), Is.EqualTo("bow"));
            Assert.That(note, Does.Contain("pins loadout bow"));
            Assert.That(ScenarioLoadout.Choose("bow", "bows", out note), Is.EqualTo("bow"));
            Assert.That(note, Is.Null, "the same loadout by an alias is no conflict");
            Assert.That(ScenarioParser.ParseJson("{\"name\":\"x\",\"loadout\":\"own\",\"steps\":[\"log a\"]}").Ok, Is.True);
            Assert.That(ScenarioParser.ParseJson("{\"name\":\"x\",\"loadout\":\"lance\",\"steps\":[\"log a\"]}").Ok, Is.False);
        }

        // ------------------------------------------------------------------ 3. spells loadout

        [Test]
        public void SpellsLoadout_TeachesTheSpellsAsG3_PutsThemOnTheBar_CastsFireball()
        {
            var lo = ScenarioLoadout.Find("spells");
            var steps = ScenarioParser.ParseInline(lo.Ops);
            Assert.That(steps.Errors, Is.Empty);
            var taught = steps.Steps.Where(s => s.Op == "teach").Select(s => s.Get("spell")).ToList();
            Assert.That(taught, Is.EquivalentTo(new[] { "It_Spell_Icelance", "It_Spell_Lightningbolt", "It_Spell_Fireball" }),
                        "the real teaching path (TeachingService), not the old 'spell' op that only readied them");
            Assert.That(steps.Steps.Any(s => s.Op == "spell"), Is.False);
            var intel = steps.Steps.Single(s => s.Op == "attr" && s.Get("name") == "INT");
            Assert.That(intel.Int("value", 0), Is.GreaterThanOrEqualTo(60), "Spl_Icelance needs INT 60 (skills.json)");
            Assert.That(intel.Bool("atleast", false), Is.True);
            var mana = steps.Steps.Single(s => s.Op == "attr" && s.Get("name") == "MP");
            Assert.That(mana.Int("value", 0), Is.GreaterThanOrEqualTo(30), "a power cast takes up to maxMana 30");
            Assert.That(lo.QuickItems, Is.EquivalentTo(taught), "every learned spell goes on the quick bar");
            Assert.That(lo.QuickItems.First(), Is.EqualTo(lo.Spell), "the fight's spell gets the first free key");
            Assert.That(ScenarioLoadout.Find("bow").QuickItems, Is.Empty);
        }

        // ------------------------------------------------------------------ 2. ranged fights

        [Test]
        public void RangedHits_CountTheKillShot()
        {
            // r31: "fight IceWolf (Bow): Dead hp=0 shots=1 hits=0 lost=5"
            Assert.That(ScenarioPacing.RangedHits(0, 0, 5), Is.EqualTo(1));
            Assert.That(ScenarioPacing.RangedHits(0, 1, 5), Is.EqualTo(1));
            Assert.That(ScenarioPacing.RangedHits(2, 3, 5), Is.EqualTo(3));
            Assert.That(ScenarioPacing.RangedHits(0, 0, 0), Is.EqualTo(0), "a miss is a miss");
        }

        [Test]
        public void Ranged_AFoeAtTheFeetIsFoughtWithTheMeleeWeapon()
        {
            Assert.That(ScenarioPacing.SwitchToMelee(AttackStyle.Bow, 2.9f, true), Is.True, "r31: Sabertooth at 2.9 m, 6 shots, no hit");
            Assert.That(ScenarioPacing.SwitchToMelee(AttackStyle.Crossbow, ScenarioPacing.MeleeSwitchDistance, true), Is.True);
            Assert.That(ScenarioPacing.SwitchToMelee(AttackStyle.Bow, 6f, true), Is.False, "from 6 m on he shoots");
            Assert.That(ScenarioPacing.SwitchToMelee(AttackStyle.Bow, 1f, false), Is.False, "no melee weapon: he keeps the bow (fists are no better)");
            Assert.That(ScenarioPacing.SwitchToMelee(AttackStyle.Spell, 1f, true), Is.False, "a mage casts at close range");
            Assert.That(ScenarioPacing.SwitchToMelee(AttackStyle.Melee, 1f, true), Is.False);
        }

        [Test]
        public void KeepDrawn_EscortDefendsNeverSheatheByThemselves_TheEscortDoesWhenClear()
        {
            // r31 bow: "fight: sheathe ... (3-4 tries)" x14 between the wolves of one pack
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 0, null, inEscort: true), Is.False);
            Assert.That(ScenarioPacing.SheatheAfterFight("1", 0, null, inEscort: true), Is.True, "sheathe=1 still wins");
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 0, null, inEscort: false), Is.True, "outside an escort: as fix-51");
            Assert.That(ScenarioPacing.SheatheAfterFight(null, 2, null, inEscort: false), Is.False);
            Assert.That(ScenarioPacing.EscortSheathe(true, ScenarioPacing.EscortClearSeconds - 0.1f), Is.False);
            Assert.That(ScenarioPacing.EscortSheathe(true, ScenarioPacing.EscortClearSeconds), Is.True);
            Assert.That(ScenarioPacing.EscortSheathe(false, 100f), Is.False);
            Assert.That(ScenarioPacing.KeepDrawnRadiusFor(AttackStyle.Bow), Is.GreaterThan(20f), "a bow fights from up to 20 m");
            Assert.That(ScenarioPacing.KeepDrawnRadiusFor(AttackStyle.Spell), Is.EqualTo(ScenarioPacing.RangedKeepDrawnRadius));
            Assert.That(ScenarioPacing.KeepDrawnRadiusFor(AttackStyle.Melee), Is.EqualTo(ScenarioPacing.KeepDrawnRadius));
        }

        [Test]
        public void Sheathe_WaitsOutTheReloadWithoutUsingUpItsPresses()
        {
            // r31 crossbow: 4 presses in 0.8 s while the bolt was loaded, then "still armed"
            Assert.That(ScenarioPacing.NextSheathe(drawn: true, busy: false, aimShotReloadOrCast: true, tries: 0), Is.EqualTo(ScenarioPacing.SheatheAct.Wait));
            Assert.That(ScenarioPacing.NextSheathe(true, true, false, 0), Is.EqualTo(ScenarioPacing.SheatheAct.Wait));
            Assert.That(ScenarioPacing.NextSheathe(true, false, false, 0), Is.EqualTo(ScenarioPacing.SheatheAct.Try));
            Assert.That(ScenarioPacing.NextSheathe(true, false, false, ScenarioPacing.SheatheTries), Is.EqualTo(ScenarioPacing.SheatheAct.Wait));
            Assert.That(ScenarioPacing.NextSheathe(false, false, false, 0), Is.EqualTo(ScenarioPacing.SheatheAct.Wait), "the sheathe motion runs");
        }

        // ------------------------------------------------------------------ 5. escort spots next to the guide

        static HeroRoutePoint P(float x, float y, float z) => new HeroRoutePoint(x, y, z);

        [Test]
        public void GuideSpot_OnALedgeAboveTheHeroNeedsHisHeightGroundAndAPath()
        {
            var mort = P(497f, 185f, 1336f);
            var heroBelow = P(508.8f, 155f, 1348.3f);
            // r31 hammerclan_02: (500,185.3,1335) on the rim of Mort's ledge; the hero fell to y 153.8
            Assert.That(GuideSpots.Reject(P(500f, 185.3f, 1335f), mort, heroBelow, 185.3f, 0.1f, true), Does.Contain("NavMesh edge"));
            Assert.That(GuideSpots.Reject(P(500f, 185.3f, 1335f), mort, heroBelow, null, 2f, true), Is.EqualTo("no ground under it"));
            Assert.That(GuideSpots.Reject(P(500f, 185.3f, 1335f), mort, heroBelow, 154f, 2f, true), Does.Contain("off the NavMesh"));
            Assert.That(GuideSpots.Reject(P(503f, 155f, 1340f), mort, heroBelow, 155f, 3f, false), Does.Contain("above/below the guide"),
                        "the terrain below the ledge is no spot next to him");
            Assert.That(GuideSpots.Reject(P(500f, 185.3f, 1335f), mort, heroBelow, 185.2f, 2f, false), Is.EqualTo("no NavMesh path to the guide"));
            Assert.That(GuideSpots.Reject(P(500f, 185.3f, 1335f), mort, heroBelow, 185.2f, 2f, true), Is.Null);
            Assert.That(GuideSpots.Reject(P(510f, 185f, 1336f), mort, heroBelow, 185f, 2f, true), Does.Contain("from the guide"));
            var heroBeside = P(500.5f, 185.3f, 1335.5f);
            Assert.That(GuideSpots.Reject(P(500f, 185.3f, 1335f), mort, heroBeside, 185.3f, 2f, true), Is.EqualTo("where the hero stands"));
            Assert.That(GuideSpots.Fell(185.3f, 153.8f), Is.True);
            Assert.That(GuideSpots.Fell(185.3f, 184.9f), Is.False);
        }

        [Test]
        public void GuideSpot_BehindHimOnHisOwnPath()
        {
            // the guide's trail: route points up to his leg, then his position (NpcView.TryGuideTrail)
            var trail = new List<HeroRoutePoint> { P(0f, 0f, 0f), P(10f, 0f, 0f), P(10f, 2f, 4f), P(10f, 2f, 10f) };
            Assert.That(GuideSpots.BehindOnTrail(trail, 3f, out var a), Is.True);
            Assert.That(a.X, Is.EqualTo(10f).Within(1e-4f));
            Assert.That(a.Z, Is.EqualTo(7f).Within(1e-4f));
            Assert.That(a.Y, Is.EqualTo(2f).Within(1e-4f), "at his height, on the way he came");
            Assert.That(GuideSpots.BehindOnTrail(trail, 8f, out var b), Is.True);
            Assert.That(b.Z, Is.LessThan(4f), "past the trail's corner, back along the slope");
            Assert.That(GuideSpots.BehindOnTrail(trail, 100f, out _), Is.False);
            Assert.That(GuideSpots.BehindOnTrail(new[] { P(0, 0, 0) }, 3f, out _), Is.False);
            Assert.That(GuideSpots.TrailDistances, Is.Ordered);
        }
    }
}
