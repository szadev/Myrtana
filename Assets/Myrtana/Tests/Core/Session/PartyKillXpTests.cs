using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-81 (owner: "no XP when a companion lands the final blow"): G3 CP 1.75 Kill (0x1001bc30) / Defeat (0x10027510) pay
    /// the kill XP once (DefeatedByPlayer) when the killer is the player or a member he leads, not a summon (GiveXP
    /// 0x1004e160); CP Respawn clears DefeatedByPlayer of a revived creature, so it pays again. Over a live session:
    /// the strike, <see cref="ActorVitals"/>, <see cref="KillXpAwarder"/> and the XP message event.
    /// </summary>
    public class PartyKillXpTests
    {
        static readonly ActorId Gorn = new ActorId("Gorn"), Wolf = new ActorId("Wolf_01"), Hunter = new ActorId("Hunter_01");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.NpcFolder, new NpcDef(Gorn, new TextId("FO_Gorn"), "Gorn", "Human", "visual", new WorldPose(2, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Hunter, new TextId("FO_Hunter"), null, "Human", "visual", new WorldPose(6, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Wolf, new TextId("FO_Wolf"), null, "Wolf", "visual", new WorldPose(4, 0, 0, 0),
                                                        combat: new NpcCombatDef(5, 20, "Wolf", humanoid: false, animal: true)));

        static Combatant Fighter(ActorId id, string species, bool humanoid, int hp) => new Combatant
        {
            Id = id, IsPlayer = id.IsPlayer, Species = species, Humanoid = humanoid, Level = 20, Strength = 100,
            Weapon = humanoid ? WeaponClass.OneHanded : WeaponClass.Fist, Action = CombatAction.Attack, HitPoints = hp, Stamina = 100,
        };

        static HitOutcome Strike(GameSession s, ActorId attacker, int damage)
        {
            var hit = new HitContext
            {
                Attacker = Fighter(attacker, "Human", true, 400), Victim = Fighter(Wolf, "Wolf", false, s.Combat.Vitals.HitPoints(Wolf)),
                Delivery = HitDelivery.Melee, DamageType = DamageType.Blade, DamageAmount = damage, PlayerLevel = 10,
            };
            HitSides.Fill(s, hit, attacker, Wolf, HitSides.NpcTargetOf(s, attacker), HitSides.NpcTargetOf(s, Wolf));
            if (attacker.IsPlayer) hit.VictimIsTarget = true; // the hero's focus
            return s.Combat.Strike(hit, byPlayerParty: s.Party.Members.DefeatsCountForPlayer(attacker));
        }

        [Test]
        public void TheHeroHitsAWolf_GornKillsIt_TheHeroGetsTheKillXpOnce_WithItsMessage()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Gorn);
                Assert.That(s.NpcCombat.Engage(Gorn, Wolf, AttackReason.Enemy), Is.True);
                Assert.That(s.NpcCombat.Engage(Wolf, ActorId.Player, AttackReason.Angry), Is.True);
                var awards = new List<XpAwarded>();
                using (s.Events.Subscribe<XpAwarded>(awards.Add))
                {
                    long before = s.Hero.Xp;
                    Assert.That(Strike(s, ActorId.Player, 10).Result, Is.EqualTo(HitResult.Hit), "the hero's blow");
                    Assert.That(s.Combat.Vitals.StateOf(Wolf), Is.EqualTo(VitalState.Alive));
                    Assert.That(awards, Is.Empty);
                    s.Combat.Vitals.AddHitPoints(Wolf, 5 - s.Combat.Vitals.HitPoints(Wolf)); // nearly dead
                    var killing = Strike(s, Gorn, 500);
                    Assert.That(killing.Killed, Is.True, "Gorn's blow kills: " + killing);
                    Assert.That(awards.Count, Is.EqualTo(1), "the party's kill pays the hero (GO_XP message event)");
                    Assert.That(s.Hero.Xp - before, Is.EqualTo(awards[0].Amount));
                    Assert.That(awards[0].Amount, Is.GreaterThan(0));
                }
            }
        }

        [Test]
        public void SummonsAndStrangersDoNotPay_TheGuideDoesNotCount()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                Assert.That(s.Party.Members.DefeatsCountForPlayer(ActorId.Player), Is.True);
                Assert.That(s.Party.Members.DefeatsCountForPlayer(Gorn), Is.False, "not in the party");
                s.Party.JoinCompanion(Gorn);
                Assert.That(s.Party.Members.DefeatsCountForPlayer(Gorn), Is.True);
            }
        }

        [Test]
        public void ARevivedCreature_PaysAgain_LikeCpRespawnClearingDefeatedByPlayer()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                var awards = new List<XpAwarded>();
                using (s.Events.Subscribe<XpAwarded>(awards.Add))
                {
                    s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
                    s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
                    Assert.That(awards.Count, Is.EqualTo(1), "once per NPC");
                    s.Combat.Vitals.Forget(Wolf); // NpcLifeServices.Revive: alive at full strength
                    s.Events.Publish(new CorpseRevived(Wolf));
                    s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
                    Assert.That(awards.Count, Is.EqualTo(2), "the revived wolf pays its XP again");
                }
            }
        }

        // ------------------------------------------------------------------ [owner, fix-93] the hero hurt it, anyone finished it

        static long HeroKillXp(SessionFixture f)
        {
            var s = f.NewGame();
            long before = s.Hero.Xp;
            s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
            return s.Hero.Xp - before;
        }

        [Test]
        public void TheHeroHurtsAWolf_AStrangerKillsIt_TheHeroGetsTheFullKillXpOnce()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                var awards = new List<XpAwarded>();
                using (s.Events.Subscribe<XpAwarded>(awards.Add))
                {
                    Assert.That(Strike(s, ActorId.Player, 10).Damage, Is.GreaterThan(0), "any damage of the hero's");
                    Assert.That(s.Combat.Vitals.WasHurtByPlayer(Wolf), Is.True);
                    s.Combat.Vitals.AddHitPoints(Wolf, 5 - s.Combat.Vitals.HitPoints(Wolf));
                    Assert.That(s.NpcCombat.Engage(Hunter, Wolf, AttackReason.Enemy), Is.True, "a hunter, not of the party");
                    var killing = Strike(s, Hunter, 500);
                    Assert.That(killing.Killed, Is.True);
                    Assert.That(awards.Count, Is.EqualTo(1), "the hunter's kill pays the hero: he practically killed it");
                    Assert.That(awards[0].Amount, Is.EqualTo(HeroKillXp(f)), "the full kill XP, as for his own kill");
                    s.Combat.Vitals.Kill(Wolf, byPlayerParty: true);
                    Assert.That(awards.Count, Is.EqualTo(1), "never paid twice");
                }
            }
        }

        [Test]
        public void AStrangersKill_OfAWolfTheHeroNeverHurt_PaysNothing()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                var awards = new List<XpAwarded>();
                using (s.Events.Subscribe<XpAwarded>(awards.Add))
                {
                    Assert.That(s.NpcCombat.Engage(Hunter, Wolf, AttackReason.Enemy), Is.True);
                    s.Combat.Vitals.AddHitPoints(Wolf, 5 - s.Combat.Vitals.HitPoints(Wolf)); // hurt by something else
                    var killing = Strike(s, Hunter, 500);
                    Assert.That(killing.Killed, Is.True, "the hunter's blow kills: " + killing);
                    Assert.That(awards, Is.Empty);
                }
            }
        }

        [Test]
        public void AHurtWolfDyingWithoutAKiller_Burns_PaysNothing_AsInG3()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                var awards = new List<XpAwarded>();
                using (s.Events.Subscribe<XpAwarded>(awards.Add))
                {
                    Strike(s, ActorId.Player, 10);
                    s.Combat.Vitals.Kill(Wolf, byPlayerParty: false); // StatusEffects.DiesOfBurns: no killer
                    Assert.That(awards, Is.Empty);
                }
            }
        }

        [Test]
        public void TheHurtMark_SurvivesSaveAndLoad()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                Strike(s, ActorId.Player, 10);
                s.NpcCombat.DisengageAll(); // saving is blocked during a fight
                var loaded = f.Reload(s);
                Assert.That(loaded.Combat.Vitals.WasHurtByPlayer(Wolf), Is.True);
            }
        }
    }
}
