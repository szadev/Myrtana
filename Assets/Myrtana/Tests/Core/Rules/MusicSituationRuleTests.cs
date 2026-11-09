using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The per-second music situation of HandleMusicSituation (Docs/gameplay-logic.md §11.3).</summary>
    [TestFixture]
    public class MusicSituationRuleTests
    {
        static readonly ActorId Companion = new ActorId("Gorn");

        static MusicNpc Fighter(string id, float metres, ActorId target) => new MusicNpc(new ActorId(id), metres, true, target);

        static string Eval(MusicSituationRule rule, params MusicNpc[] npcs) =>
            rule.Evaluate(false, false, false, npcs, a => a.Equals(Companion));

        [Test]
        public void ExploreWithoutHostilesNearby()
        {
            var rule = new MusicSituationRule();
            Assert.AreEqual("explore", Eval(rule));
            Assert.AreEqual("explore", Eval(rule, Fighter("Wolf#1", 31f, ActorId.Player)));        // beyond 30 m
            Assert.AreEqual("explore", Eval(rule, new MusicNpc(new ActorId("Wolf#1"), 5f, false, ActorId.Player)));
            Assert.AreEqual("explore", Eval(rule, Fighter("Orc#1", 5f, new ActorId("Rebel#2")))); // fights someone else
        }

        [Test]
        public void FightWhenAnNpcWithin30mAttacksThePlayerOrTheParty()
        {
            var rule = new MusicSituationRule();
            Assert.AreEqual("fight", Eval(rule, Fighter("Wolf#1", 30f, ActorId.Player)));
            Assert.AreEqual("fight", Eval(rule, Fighter("Orc#1", 12f, Companion)));
        }

        [Test]
        public void ShowdownBossesByEntityName()
        {
            var rule = new MusicSituationRule();
            Assert.AreEqual("showdown", Eval(rule, Fighter("Wolf#1", 3f, ActorId.Player), Fighter("Dragon#2c56bec4", 20f, ActorId.Player)));
            Assert.AreEqual("showdown", Eval(rule, Fighter("xardas", 20f, ActorId.Player)));
            Assert.IsFalse(MusicSituationRule.IsShowdownBoss(new ActorId("Kanu")));
        }

        [Test]
        public void DeadAndSoulTravelComeFirst_MenuKeepsTheLastAnswer()
        {
            var rule = new MusicSituationRule();
            var wolf = Fighter("Wolf#1", 3f, ActorId.Player);
            Assert.AreEqual("fight", Eval(rule, wolf));
            Assert.AreEqual("fight", rule.Evaluate(true, true, false, new MusicNpc[0]));          // menu open: no change
            Assert.AreEqual("dead", rule.Evaluate(false, true, true, new[] { wolf }));
            Assert.AreEqual("soultravel", rule.Evaluate(false, false, true, new[] { wolf }));
        }

        [Test]
        public void RevolutionWhileAnEnclaveAttacks_VictoryOncePerEnclave()
        {
            var rule = new MusicSituationRule();
            var attacking = new MusicNpc(new ActorId("Rebel#1"), 10f, false, default, "Rebels_Reddock", EnclaveMusicStatus.Attack);
            var liberated = new MusicNpc(new ActorId("Rebel#1"), 10f, false, default, "Rebels_Reddock", EnclaveMusicStatus.Liberated);
            Assert.AreEqual("revolution", Eval(rule, attacking, Fighter("Dragon", 5f, ActorId.Player)));   // first NPC decides
            Assert.AreEqual("revolutionwon", Eval(rule, liberated));
            Assert.AreEqual("explore", Eval(rule, liberated));
            CollectionAssert.Contains(new System.Collections.Generic.List<string>(rule.VictoryPlayed), "Rebels_Reddock");
            var far = new MusicNpc(new ActorId("Rebel#9"), 40f, false, default, "Orcs_Faring", EnclaveMusicStatus.Attack);
            Assert.AreEqual("explore", Eval(rule, far));
        }

        static MusicNpc Leveled(string id, int level) => new MusicNpc(new ActorId(id), 10f, true, ActorId.Player, level: level);

        [Test]
        public void ThreatOption_OffIsG3_AnyAttackerStartsFight()
        {
            var rule = new MusicSituationRule { HeroLevel = 30 };
            Assert.AreEqual("fight", Eval(rule, Leveled("Scavenger#1", 3)));
        }

        [Test]
        public void ThreatOption_WeakAttackersStayExplore_PacksAndUnknownLevelsFight()
        {
            var rule = new MusicSituationRule { ThreatOnly = true, HeroLevel = 30 };
            Assert.AreEqual("explore", Eval(rule, Leveled("Scavenger#1", 5)));
            Assert.AreEqual("explore", Eval(rule, Leveled("Rat#1", 0), Leveled("Scavenger#1", 5)));
            Assert.AreEqual("fight", Eval(rule, Leveled("Wolf#1", 8), Leveled("Wolf#2", 8)), "16 >= 30/2");
            var fresh = new MusicSituationRule { ThreatOnly = true, HeroLevel = 30 };
            Assert.AreEqual("fight", Eval(fresh, Fighter("Orc#1", 5f, ActorId.Player)), "level not known: a threat");
            var early = new MusicSituationRule { ThreatOnly = true, HeroLevel = 1 };
            Assert.AreEqual("fight", Eval(early, Leveled("Scavenger#1", 2)), "early on a scavenger is a threat");
            Assert.AreEqual("explore", Eval(new MusicSituationRule { ThreatOnly = true, HeroLevel = 1 }, Leveled("Rat#1", 0)));
        }

        [Test]
        public void ThreatOption_FightStaysWhileAnyAttackerIsLeft_ShowdownUnchanged()
        {
            var rule = new MusicSituationRule { ThreatOnly = true, HeroLevel = 30 };
            Assert.AreEqual("fight", Eval(rule, Leveled("Wolf#1", 8), Leveled("Wolf#2", 8)));
            Assert.AreEqual("fight", Eval(rule, Leveled("Wolf#2", 8)), "no flapping as the pack thins out");
            Assert.AreEqual("explore", Eval(rule));
            Assert.AreEqual("explore", Eval(rule, Leveled("Wolf#2", 8)));
            Assert.AreEqual("showdown", Eval(rule, Leveled("Dragon", 1)));
            Assert.That(MusicSituationRule.IsThreat(15, 30), Is.True);
            Assert.That(MusicSituationRule.IsThreat(14, 30), Is.False);
        }
    }
}
