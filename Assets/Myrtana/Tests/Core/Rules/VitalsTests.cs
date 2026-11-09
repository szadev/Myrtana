using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>Actor vitals: knock-out and death, the down state, story conditions, events, saving.</summary>
    public class VitalsTests
    {
        static readonly ActorId Bandit = new ActorId("Bandit");
        static readonly ActorId Companion = new ActorId("Gorn");

        sealed class Setup
        {
            public readonly EventBus Events = new EventBus();
            public readonly ActorVitals Vitals;
            public readonly Hero Hero;
            public readonly List<ActorDefeated> Defeats = new List<ActorDefeated>();
            public readonly List<PlayerDefeated> PlayerDefeats = new List<PlayerDefeated>();
            public readonly List<ActorGotUp> GotUp = new List<ActorGotUp>();

            public Setup()
            {
                var db = new ContentDatabase();
                foreach (var a in TestRules.Attributes()) db.Put(a);
                Hero = Hero.CreateNew(db, TestRules.Progression(), RuleOptions.Default);
                Vitals = new ActorVitals(Hero.Stats, Profile, TestRules.Combat(), Events);
                Events.Subscribe<ActorDefeated>(Defeats.Add);
                Events.Subscribe<PlayerDefeated>(PlayerDefeats.Add);
                Events.Subscribe<ActorGotUp>(GotUp.Add);
            }

            static VitalsProfile Profile(ActorId id) =>
                id == Bandit ? new VitalsProfile(300, 150, 1000, 5, false, "Human") :
                id == Companion ? new VitalsProfile(500, 200, 1000, 20, false, "Human", playerParty: true) : null;
        }

        [Test]
        public void RefreshHitPoints_FillsHitPointsAndStamina_KeepsMana_LeavesTheDeadAndThePlayer()
        {
            // fix-175: G3 RefreshHitPoints (Script_Game 0x100ab840) sets SP and HP to their maxima, never MP
            var s = new Setup();
            var v = s.Vitals;
            v.AddHitPoints(Bandit, -200);
            v.AddStamina(Bandit, -100);
            v.AddMana(Bandit, -600);
            v.RefreshHitPoints(Bandit);
            Assert.That(v.HitPoints(Bandit), Is.EqualTo(300));
            Assert.That(v.Stamina(Bandit), Is.EqualTo(150));
            Assert.That(v.Mana(Bandit), Is.EqualTo(400), "mana is left");
            Assert.That(v.Holds(Bandit), Is.True);
            v.AddMana(Bandit, 600);
            v.RefreshHitPoints(Bandit);
            Assert.That(v.Holds(Bandit), Is.False, "back at full strength: no entry");

            int heroHp = v.HitPoints(ActorId.Player);
            v.AddHitPoints(ActorId.Player, -10);
            v.RefreshHitPoints(ActorId.Player);
            Assert.That(v.HitPoints(ActorId.Player), Is.EqualTo(heroHp - 10), "never the player");

            v.Kill(Bandit, byPlayerParty: false);
            v.RefreshHitPoints(Bandit);
            Assert.That(v.IsDead(Bandit), Is.True);
            Assert.That(v.HitPoints(Bandit), Is.EqualTo(0), "the dead stay dead");
        }

        [Test]
        public void UntouchedNpc_IsAliveAtFullStrength()
        {
            var s = new Setup();
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(300));
            Assert.That(s.Vitals.Is(Bandit, ActorCondition.Unharmed), Is.True);
            Assert.That(s.Vitals.Is(new ActorId("Unknown"), ActorCondition.Alive), Is.True);
        }

        [Test]
        public void KnockOut_PublishesDefeat_RegeneratesAndGetsUpAfter30s()
        {
            var s = new Setup();
            s.Vitals.AddHitPoints(Bandit, -300);
            s.Vitals.KnockOut(Bandit, byPlayerParty: true);
            Assert.That(s.Defeats, Has.Count.EqualTo(1));
            Assert.That(s.Defeats[0].Killed, Is.False);
            Assert.That(s.Defeats[0].VictimLevel, Is.EqualTo(5));
            Assert.That(s.Vitals.Is(Bandit, ActorCondition.Defeated), Is.True);
            Assert.That(s.Vitals.Is(Bandit, ActorCondition.Unharmed), Is.False);

            for (int i = 0; i < 29; i++) s.Vitals.Tick(1f);
            Assert.That(s.Vitals.IsDown(Bandit), Is.True);
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(290));
            s.Vitals.Tick(1f);
            Assert.That(s.Vitals.IsDown(Bandit), Is.False);
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(300));
            Assert.That(s.GotUp, Has.Count.EqualTo(1));
            Assert.That(s.Vitals.Is(Bandit, ActorCondition.Defeated), Is.True);
        }

        [Test]
        public void FinishingHold_KeepsThePlayerDown_IndependentOfTheLootHold()
        {
            var s = new Setup();
            s.Vitals.KnockOut(ActorId.Player, false);
            bool finishing = true;
            s.Vitals.FinishingHold = id => finishing && id.IsPlayer;
            s.Vitals.HoldDown = _ => false; // the loot screen closing must not release a running finisher
            s.Vitals.Tick(10f);
            Assert.That(s.Vitals.IsDown(ActorId.Player), Is.True, "the 5 s knock-out ran out while the finisher came");
            finishing = false;
            s.Vitals.Tick(0.5f);
            Assert.That(s.Vitals.IsDown(ActorId.Player), Is.False);
        }

        [Test]
        public void HoldDown_KeepsTheActorDown()
        {
            var s = new Setup();
            s.Vitals.KnockOut(Bandit, false);
            s.Vitals.HoldDown = id => id == Bandit;
            s.Vitals.Tick(60f);
            Assert.That(s.Vitals.IsDown(Bandit), Is.True);
            s.Vitals.HoldDown = _ => false;
            s.Vitals.Tick(0.5f);
            Assert.That(s.Vitals.IsDown(Bandit), Is.False);
        }

        [Test]
        public void ArenaLoser_20Seconds_Regen15()
        {
            var s = new Setup();
            s.Vitals.AddHitPoints(Bandit, -300);
            s.Vitals.KnockOut(Bandit, true, arena: true);
            Assert.That(s.Vitals.DownSecondsLeft(Bandit), Is.EqualTo(20f));
            s.Vitals.Tick(2f);
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(30));
            Assert.That(s.Vitals.DownSecondsLeft(new ActorId("Companion_unknown")), Is.EqualTo(0f));
            s.Vitals.KnockOut(Companion, false);
            Assert.That(s.Vitals.DownSecondsLeft(Companion), Is.EqualTo(5f));
        }

        [Test]
        public void Kill_IsFinal_AndReportedOnce()
        {
            var s = new Setup();
            s.Vitals.Kill(Bandit, true);
            s.Vitals.Kill(Bandit, true);
            s.Vitals.KnockOut(Bandit, true);
            Assert.That(s.Defeats, Has.Count.EqualTo(1));
            Assert.That(s.Defeats[0].Killed, Is.True);
            Assert.That(s.Vitals.Is(Bandit, ActorCondition.Dead), Is.True);
            Assert.That(s.Vitals.Is(Bandit, ActorCondition.Alive), Is.False);
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(0));
        }

        [Test]
        public void Player_KnockedOutFiveSeconds_UsesHeroAttributes()
        {
            var s = new Setup();
            s.Vitals.AddHitPoints(ActorId.Player, -500);
            Assert.That(s.Hero.Stats.Value("HP"), Is.EqualTo(0));
            s.Vitals.KnockOut(ActorId.Player, false);
            Assert.That(s.PlayerDefeats, Has.Count.EqualTo(1));
            Assert.That(s.Defeats, Is.Empty);
            s.Vitals.Tick(5f);
            Assert.That(s.Vitals.IsDown(ActorId.Player), Is.False);
            Assert.That(s.Hero.Stats.Value("HP"), Is.EqualTo(50));
        }

        [Test]
        public void ApplyHit_SpendsStaminaAndKnocksOut()
        {
            var s = new Setup();
            var resolver = new CombatResolver(TestRules.Combat(), RuleOptions.Default, new SeededRandom(1));
            var victim = new Combatant { Id = Bandit, HitPoints = 10, Stamina = 150, Weapon = WeaponClass.OneHanded };
            var attacker = new Combatant { Id = ActorId.Player, IsPlayer = true, Strength = 100, Weapon = WeaponClass.OneHanded, Action = CombatAction.Attack };
            s.Vitals.AddHitPoints(Bandit, -290);
            var hit = new HitContext { Attacker = attacker, Victim = victim, DamageType = DamageType.Blade, DamageAmount = 50,
                                       AttackerToVictim = Attitude.Neutral, VictimToAttacker = Attitude.Neutral, PlayerLevel = 5 };
            var outcome = resolver.Resolve(hit);
            s.Vitals.ApplyHit(hit, outcome, byPlayerParty: true);
            Assert.That(outcome.KnockedOut, Is.True);
            Assert.That(s.Vitals.IsDown(Bandit), Is.True);
            Assert.That(s.Vitals.HitPoints(Bandit), Is.EqualTo(0));
            Assert.That(s.Defeats[0].ByPlayerParty, Is.True);
        }

        // ------------------------------------------------------------------------------------------------- session

        [Test]
        public void Session_StoryAsksTheVitals_AndSavesThem()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                Assert.That(session.Combat.Vitals.HitPointsMax(gnar), Is.EqualTo(100));
                session.Combat.Vitals.AddHitPoints(gnar, -100);
                session.Combat.Vitals.KnockOut(gnar, byPlayerParty: true);
                session.Tick(3f);
                Assert.That(session.Hero.Xp, Is.GreaterThan(0), "defeat XP");

                var loaded = f.Reload(session);
                Assert.That(loaded.Combat.Vitals.IsDown(gnar), Is.True);
                Assert.That(loaded.Combat.Vitals.HitPoints(gnar), Is.EqualTo(30));
                Assert.That(loaded.Combat.Vitals.DownSecondsLeft(gnar), Is.EqualTo(27f).Within(0.01f));
                Assert.That(loaded.Combat.Vitals.Is(gnar, ActorCondition.Defeated), Is.True);
                Assert.That(SessionFixture.Save(loaded), Is.EqualTo(SessionFixture.Save(f.Reload(loaded))));
            }
        }

        [Test]
        public void Session_CannotSaveWhileThePlayerIsDown()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                session.Combat.Vitals.KnockOut(ActorId.Player, false);
                Assert.That(session.SaveBlockReason, Is.EqualTo(CombatWiring.BlockedByKnockOut));
                session.Tick(5f);
                Assert.That(session.SaveBlockReason, Is.Null);
            }
        }

        [Test]
        public void Session_CannotSaveOnceThePlayerIsDead()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                session.Combat.Vitals.Kill(ActorId.Player, false);
                Assert.That(session.SaveBlockReason, Is.EqualTo(CombatWiring.BlockedByDeath));
                session.Tick(60f);
                Assert.That(session.SaveBlockReason, Is.EqualTo(CombatWiring.BlockedByDeath));
            }
        }
    }
}
