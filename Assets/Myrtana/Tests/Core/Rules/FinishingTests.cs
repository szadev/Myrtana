using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The finishing move (Docs/combat.md §5.5): reach, focus, the timed blow, the kill and its events.</summary>
    public class FinishingTests
    {
        static readonly ActorId Bandit = new ActorId("Bandit");

        [Test]
        public void Reach_NeedsADownedKillableFocusWithin300()
        {
            Assert.That(FinishingMove.InReach(VitalState.Down, 300, killable: true), Is.True);
            Assert.That(FinishingMove.InReach(VitalState.Down, 301, killable: true), Is.False);
            Assert.That(FinishingMove.InReach(VitalState.Alive, 100, killable: true), Is.False);
            Assert.That(FinishingMove.InReach(VitalState.Dead, 100, killable: true), Is.False);
            Assert.That(FinishingMove.InReach(VitalState.Down, 100, killable: false), Is.False);
        }

        [Test]
        public void DownedFocus_KeptWhileBlockingAndWithin500()
        {
            Assert.That(FinishingMove.KeepsDownedFocus(500, blockHeld: true), Is.True);
            Assert.That(FinishingMove.KeepsDownedFocus(501, blockHeld: true), Is.False);
            Assert.That(FinishingMove.KeepsDownedFocus(100, blockHeld: false), Is.False);
        }

        [Test]
        public void Strike_LandsOnce_After1point2Seconds()
        {
            var strike = new FinishingStrike();
            strike.Begin(Bandit, 10);
            Assert.That(strike.Tick(11.2), Is.False);
            Assert.That(strike.Tick(11.21), Is.True);
            Assert.That(strike.Tick(12), Is.False, "once");
            Assert.That(strike.End(interrupted: false), Is.False, "already landed");
        }

        [Test]
        public void Strike_InterruptedLandsNothing_NormalEndLandsAtTheEnd()
        {
            var strike = new FinishingStrike();
            strike.Begin(Bandit, 0);
            Assert.That(strike.End(interrupted: true), Is.False);
            Assert.That(strike.Tick(5), Is.False);
            strike.Begin(Bandit, 0);
            Assert.That(strike.End(interrupted: false), Is.True);
            strike.Begin(default(ActorId), 0);
            Assert.That(strike.Pending, Is.False, "no target, no blow");
        }

        [Test]
        public void Lines_FinisherAndVictim()
        {
            Assert.That(CombatLines.OfFinisher(isPlayer: false, humanoid: true), Is.EqualTo("FinishingMove"));
            Assert.That(CombatLines.OfFinisher(isPlayer: false, humanoid: false), Is.Null);
            Assert.That(CombatLines.OfFinisher(isPlayer: true, humanoid: true), Is.Null, "PS_Melee_FinishingAttack says nothing");
            Assert.That(CombatLines.OfVictim(killed: true), Is.EqualTo("Dead"));
            Assert.That(CombatLines.OfVictim(killed: false), Is.EqualTo("Down"));
        }

        // ------------------------------------------------------------------------------------------------- session

        sealed class Recorder
        {
            public readonly List<HitResolved> Hits = new List<HitResolved>();
            public readonly List<ActorDefeated> Defeats = new List<ActorDefeated>();
            public readonly List<CombatShout> Shouts = new List<CombatShout>();
            public readonly List<PlayerDefeated> PlayerDefeats = new List<PlayerDefeated>();

            public Recorder(IEventBus events)
            {
                events.Subscribe<HitResolved>(Hits.Add);
                events.Subscribe<ActorDefeated>(Defeats.Add);
                events.Subscribe<CombatShout>(Shouts.Add);
                events.Subscribe<PlayerDefeated>(PlayerDefeats.Add);
            }
        }

        [Test]
        public void Finish_KillsTheDownedNpc_AsAKillingHitOfTheFinisher_XpOnlyOnce()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                var vitals = session.Combat.Vitals;
                var rec = new Recorder(session.Events);
                var victim = new Combatant { Id = gnar };

                Assert.That(session.Combat.Finish(ActorId.Player, victim, session.Hero.Level, true), Is.False, "not down");

                vitals.AddHitPoints(gnar, -100);
                vitals.KnockOut(gnar, byPlayerParty: true);
                session.Tick(1f);
                long xp = session.Hero.Xp;
                Assert.That(xp, Is.GreaterThan(0), "defeat XP");
                Assert.That(rec.Shouts.Last().Line, Is.EqualTo("Down"));

                Assert.That(session.Combat.Finish(ActorId.Player, victim, session.Hero.Level, true), Is.True);
                session.Tick(1f);
                Assert.That(vitals.IsDead(gnar), Is.True);
                Assert.That(session.Hero.Xp, Is.EqualTo(xp), "XP once per NPC (DefeatedByPlayer)");
                Assert.That(rec.Defeats.Last().Killed, Is.True);
                var hit = rec.Hits.Single();
                Assert.That(hit.Attacker, Is.EqualTo(ActorId.Player));
                Assert.That(hit.Victim, Is.EqualTo(gnar));
                Assert.That(hit.Outcome.Killed, Is.True, "crime and perception see a kill (SendPerceptionMurder)");
                Assert.That(rec.Shouts.Last(), Is.EqualTo(new CombatShout(gnar, "Dead")));

                Assert.That(session.Combat.Finish(ActorId.Player, victim, session.Hero.Level, true), Is.False, "dead already");
            }
        }

        [Test]
        public void Finish_RefusesTheImmortal()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                session.Combat.Vitals.KnockOut(gnar, true);
                Assert.That(session.Combat.Finish(ActorId.Player, new Combatant { Id = gnar, Immortal = true }, session.Hero.Level, true), Is.False);
                Assert.That(session.Combat.Vitals.IsDown(gnar), Is.True);
            }
        }

        [Test]
        public void NpcFinishesThePlayer_GameOver_ButNotALowLevelHeroOnMedium()
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var rec = new Recorder(session.Events);
                var hero = session.Combat.PlayerCombatant(session.Hero);
                session.Combat.Vitals.KnockOut(ActorId.Player, false);
                Assert.That(session.Combat.Finish(SessionFixture.Npc, hero, playerLevel: 1, byPlayerParty: false), Is.False,
                            "Easy/Medium: the hero below level 3 cannot be killed");
                Assert.That(session.Combat.Finish(SessionFixture.Npc, hero, playerLevel: 3, byPlayerParty: false), Is.True);
                Assert.That(rec.PlayerDefeats.Last().Killed, Is.True, "game over");
                Assert.That(rec.Shouts.Last(), Is.EqualTo(new CombatShout(ActorId.Player, "Dead")));
            }
        }
    }
}
