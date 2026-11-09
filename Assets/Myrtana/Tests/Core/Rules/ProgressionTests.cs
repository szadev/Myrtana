using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    public class ProgressionTests
    {
        static readonly ProgressionRules Rules = TestRules.Progression();

        sealed class Setup
        {
            public readonly EventBus Events = new EventBus();
            public readonly ProgressionService Progression;
            public readonly List<LevelReached> Levels = new List<LevelReached>();

            public Setup(RuleOptions options = null)
            {
                var db = new ContentDatabase();
                foreach (var a in TestRules.Attributes()) db.Put(a);
                options = options ?? RuleOptions.Default;
                Progression = new ProgressionService(Hero.CreateNew(db, Rules, options), Rules, options, Events);
                Events.Subscribe<LevelReached>(Levels.Add);
            }

            public Hero Hero => Progression.Hero;
        }

        // progression.json "xpNext" table
        [TestCase(1, 1500)]
        [TestCase(2, 3000)]
        [TestCase(10, 33000)]
        [TestCase(30, 248000)]
        [TestCase(60, 945500)]
        public void XpForNextLevel_MatchesTheG3Table(int level, long xp) =>
            Assert.That(ProgressionMath.XpForNextLevel(Rules, level), Is.EqualTo(xp));

        [Test]
        public void NewHero_StartsWithTheCpValues()
        {
            var hero = new Setup().Hero;
            Assert.That(hero.Level, Is.EqualTo(1));
            Assert.That(hero.Xp, Is.EqualTo(0));
            Assert.That(hero.Stats.Value("HP"), Is.EqualTo(200));
            Assert.That(hero.Stats.Maximum("HP"), Is.EqualTo(200));
            Assert.That(hero.Stats.Value("SP"), Is.EqualTo(150), "Alternative Balancing is on by default");
            Assert.That(new Setup(new RuleOptions(alternativeBalancing: false)).Hero.Stats.Value("SP"), Is.EqualTo(100));
            Assert.That(hero.Stats.Value("STR"), Is.EqualTo(100));
        }

        [Test]
        public void Award_RaisesLevelsAndGivesLearningPoints()
        {
            var s = new Setup();
            s.Progression.AwardXp(1499);
            Assert.That(s.Hero.Level, Is.EqualTo(1));
            s.Progression.AwardXp(1);
            Assert.That(s.Hero.Level, Is.EqualTo(2));
            Assert.That(s.Hero.LearningPoints, Is.EqualTo(10));
            Assert.That(s.Levels.Count, Is.EqualTo(1));
            Assert.That(s.Hero.Stats.Value("HP"), Is.EqualTo(200), "levels do not raise HP");
        }

        [Test]
        public void OneAward_RaisesAtMostTwoLevels_TheRestComesWithTheNextAward()
        {
            var s = new Setup();
            s.Progression.AwardXp(10000); // enough for level 5
            Assert.That(s.Hero.Level, Is.EqualTo(3));
            s.Progression.AwardXp(5);
            Assert.That(s.Hero.Level, Is.EqualTo(5));
            Assert.That(s.Hero.LearningPoints, Is.EqualTo(40));
        }

        [Test]
        public void PerkLearn_AddsALearningPointPerLevel()
        {
            var s = new Setup();
            s.Progression.LearnSkill("Perk_Learn");
            s.Progression.AwardXp(1500);
            Assert.That(s.Hero.LearningPoints, Is.EqualTo(11));
        }

        [Test]
        public void LearningPoints_AreSpentOnlyWhenEnough()
        {
            var s = new Setup();
            s.Progression.AddLearningPoints(5);
            Assert.That(s.Progression.TrySpendLearningPoints(6), Is.False);
            Assert.That(s.Progression.TrySpendLearningPoints(5), Is.True);
            Assert.That(s.Hero.LearningPoints, Is.EqualTo(0));
        }

        // progression.json "killXp"
        [TestCase(1, false, "Human", false, 50)]
        [TestCase(12, false, "Human", false, 120)]
        [TestCase(3, true, "Rat", false, 25)]
        [TestCase(3, true, "Rat", true, 10)]
        [TestCase(1, false, "Pig", false, 50)]
        [TestCase(1, false, "Pig", true, 20)]
        [TestCase(20, false, "Cow", true, 20)]
        public void KillXp_FollowsDefaultAndAlternativeBalancing(int level, bool ambient, string species, bool ab, int xp)
        {
            var options = new RuleOptions(alternativeBalancing: ab);
            Assert.That(ProgressionMath.KillXp(Rules.KillXp, options, new DefeatedVictim(level, ambient, species)), Is.EqualTo(xp));
        }

        [TestCase(100, 120, 120)]
        [TestCase(150, 120, 180)]
        [TestCase(1, 120, 0)]
        [TestCase(33, 50, 15)]   // 5·floor(33·50/500 + 0.5) = 5·floor(3.8) = 15
        [TestCase(200, 25, 50)]
        public void XpModifier_RoundsToMultiplesOfFive(int percent, int baseXp, int expected) =>
            Assert.That(ProgressionMath.ApplyXpModifier(Rules.KillXp, baseXp, percent), Is.EqualTo(expected));

        [Test]
        public void KillXp_IsPaidOncePerVictimAndOnlyForThePlayersSide()
        {
            var s = new Setup();
            using (new KillXpAwarder(s.Progression, s.Events))
            {
                var wolf = new ActorId("Wolf_01");
                s.Events.Publish(new ActorDefeated(wolf, byPlayerParty: false, killed: true, victimLevel: 10, ambientCreature: false, species: "Wolf"));
                Assert.That(s.Hero.Xp, Is.EqualTo(0));
                s.Events.Publish(new ActorDefeated(wolf, true, false, 10, false, "Wolf"));
                s.Events.Publish(new ActorDefeated(wolf, true, true, 10, false, "Wolf"));
                Assert.That(s.Hero.Xp, Is.EqualTo(100));
            }
        }

        [Test]
        public void Boosts_FollowTheRulesAndXarExistsOnlyWithAlternativeBalancing()
        {
            var s = new Setup();
            Assert.That(s.Progression.Boost("STR"), Is.True);
            Assert.That(s.Hero.Stats.Value("STR"), Is.EqualTo(125));
            Assert.That(s.Hero.Stats.Value("SMT"), Is.EqualTo(35));
            Assert.That(s.Hero.Stats.Maximum("HP"), Is.EqualTo(250));
            Assert.That(s.Hero.Stats.Value("HP"), Is.EqualTo(250));
            Assert.That(s.Progression.Boost("XAR"), Is.True);

            var vanilla = new Setup(new RuleOptions(alternativeBalancing: false));
            Assert.That(vanilla.Progression.Boost("XAR"), Is.False);
            Assert.That(vanilla.Progression.Boost("Nonsense"), Is.False);
        }
    }
}
