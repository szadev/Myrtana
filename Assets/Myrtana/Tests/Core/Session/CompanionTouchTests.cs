using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-59: the hero's side never hurts the hero (or each other) by a stray touch, as in G3 [CP 1.75: OnTouchDamage
    /// 0x100b47b0 party + bystander filters, DoLogicalDamage 0x100b4f50 friendly-fire gates]. The whole path a touch takes:
    /// <see cref="HitSides.Fill"/> over a live session, then <see cref="CombatService.Strike"/>; a dropped touch changes no
    /// vitals and publishes nothing (no reaction, sound, aggro).
    /// </summary>
    public class CompanionTouchTests
    {
        static readonly ActorId Gorn = new ActorId("Gorn"), Kliff = new ActorId("Kliff"), Wolf = new ActorId("Wolf_01");

        static SessionFixture Fixture() => new SessionFixture()
            .Add(SimulationSchema.NpcFolder, new NpcDef(Gorn, new TextId("FO_Gorn"), "Gorn", "Human", "visual", new WorldPose(2, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Kliff, new TextId("FO_Kliff"), "Kliff", "Human", "visual", new WorldPose(3, 0, 0, 0)))
            .Add(SimulationSchema.NpcFolder, new NpcDef(Wolf, new TextId("FO_Wolf"), null, "Wolf", "visual", new WorldPose(4, 0, 0, 0),
                                                        combat: new NpcCombatDef(5, 20, "Wolf", humanoid: false)));

        static Combatant Npc(ActorId id, string species = "Human", bool humanoid = true) => new Combatant
        {
            Id = id, Species = species, Humanoid = humanoid, Level = 20, Strength = 100,
            Weapon = WeaponClass.OneHanded, Action = CombatAction.Attack, HitPoints = 400, Stamina = 100,
        };

        static Combatant Hero() => new Combatant
        {
            Id = ActorId.Player, IsPlayer = true, Species = "Human", Humanoid = true, Level = 10, HitPoints = 200, Stamina = 100,
            Weapon = WeaponClass.Fist,
        };

        /// <summary>A touch of <paramref name="attacker"/>'s blade (or missile / spell missile) on <paramref name="victim"/> as the presenter builds it.</summary>
        static HitContext Touch(GameSession s, Combatant attacker, Combatant victim, HitDelivery delivery = HitDelivery.Melee)
        {
            var hit = new HitContext
            {
                Attacker = attacker, Victim = victim, Delivery = delivery, DamageAmount = 80, PlayerLevel = 10,
                DamageType = delivery == HitDelivery.Spell ? DamageType.Fire : DamageType.Blade,
                SpellProjectile = delivery == HitDelivery.Spell,
            };
            HitSides.Fill(s, hit, attacker.Id, victim.Id, HitSides.NpcTargetOf(s, attacker.Id),
                          victim.Id.IsPlayer ? default : HitSides.NpcTargetOf(s, victim.Id));
            return hit;
        }

        static (HitOutcome outcome, List<HitResolved> published, int hpBefore, int hpAfter) Strike(GameSession s, HitContext hit)
        {
            var published = new List<HitResolved>();
            using (s.Events.Subscribe<HitResolved>(published.Add))
            {
                int before = s.Combat.Vitals.HitPoints(hit.Victim.Id);
                var outcome = s.Combat.Strike(hit, byPlayerParty: true);
                return (outcome, published, before, s.Combat.Vitals.HitPoints(hit.Victim.Id));
            }
        }

        static void AssertDropped((HitOutcome outcome, List<HitResolved> published, int hpBefore, int hpAfter) r, string what)
        {
            Assert.That(r.outcome.Result, Is.EqualTo(HitResult.Ignored), what + ": " + r.outcome);
            Assert.That(r.outcome.Damage, Is.EqualTo(0), what);
            Assert.That(r.outcome.Reaction, Is.EqualTo(HitReaction.None), what + ": no stumble");
            Assert.That(r.hpAfter, Is.EqualTo(r.hpBefore), what + ": hit points");
            Assert.That(r.published, Is.Empty, what + ": nothing published (no reaction, sound, perception, aggro)");
        }

        [Test]
        public void CompanionFightingBesideTheHero_ItsBladeArrowAndSpellNeverHurtHim()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Gorn);
                Assert.That(s.NpcCombat.Engage(Gorn, Wolf, AttackReason.Enemy), Is.True);
                foreach (var delivery in new[] { HitDelivery.Melee, HitDelivery.Missile, HitDelivery.Spell })
                {
                    var r = Strike(s, Touch(s, Npc(Gorn), Hero(), delivery));
                    AssertDropped(r, "Gorn " + delivery + " on the hero");
                    Assert.That(r.outcome.Gate, Is.EqualTo(HitGate.Bystander), delivery.ToString());
                }
                // the hero blocking or not makes no difference: the touch never reaches the block (no stamina cost)
                var parry = Hero();
                parry.InParade = true;
                var blocked = Strike(s, Touch(s, Npc(Gorn), parry));
                AssertDropped(blocked, "Gorn on the parrying hero");
                Assert.That(blocked.outcome.StaminaCost, Is.EqualTo(0));

                // control: the same blade on its own target lands
                var wolf = Strike(s, Touch(s, Npc(Gorn), Npc(Wolf, "Wolf", humanoid: false)));
                Assert.That(wolf.outcome.Result, Is.EqualTo(HitResult.Hit), wolf.outcome.ToString());
            }
        }

        [Test]
        public void CompanionWhoseFoeJustWentDown_FollowThroughSwingSparesTheHero()
        {
            // the bug the owner saw: the fight ended (no target), the swing ran on and cut the hero, because the
            // presentation's "no opponent" counted as "the victim is the target". G3 compares with the current target.
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Gorn);
                s.NpcCombat.Engage(Gorn, Wolf, AttackReason.Enemy);
                s.NpcCombat.Get(Gorn).Disengage();
                Assert.That(HitSides.NpcTargetOf(s, Gorn).IsEmpty, Is.True);
                var hit = Touch(s, Npc(Gorn), Hero());
                Assert.That(hit.VictimIsTarget, Is.False, "no target: the hero is not it");
                var r = Strike(s, hit);
                AssertDropped(r, "follow-through on the hero");
                Assert.That(r.outcome.Gate, Is.EqualTo(HitGate.FriendlyFire), "DoLogicalDamage: NPC melee on a friendly non-target");
            }
        }

        [Test]
        public void Companions_NeverHurtEachOther_FightingOrNot()
        {
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Gorn);
                s.Party.JoinCompanion(Kliff);
                s.NpcCombat.Engage(Gorn, Wolf, AttackReason.Enemy);
                AssertDropped(Strike(s, Touch(s, Npc(Gorn), Npc(Kliff))), "Gorn on Kliff (Kliff not fighting)");
                s.NpcCombat.Engage(Kliff, Wolf, AttackReason.Enemy);
                AssertDropped(Strike(s, Touch(s, Npc(Gorn), Npc(Kliff))), "Gorn on Kliff (both fight the wolf)");
                AssertDropped(Strike(s, Touch(s, Npc(Gorn), Npc(Kliff), HitDelivery.Missile)), "Gorn's arrow on Kliff");
            }
        }

        [Test]
        public void Guide_MeleeAndStrayShotsOnTheHeroHeLeads_AreDropped()
        {
            // G3 StartGuidePlayer: the guide is the hero's party leader; OnTouchDamage drops touches on members of the
            // attacker's party that are not its current target.
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.Members.StartGuide(Gorn, "FP_Somewhere", null);
                s.NpcCombat.Engage(Gorn, Wolf, AttackReason.Enemy);
                foreach (var delivery in new[] { HitDelivery.Melee, HitDelivery.Missile, HitDelivery.Spell })
                {
                    var r = Strike(s, Touch(s, Npc(Gorn), Hero(), delivery));
                    AssertDropped(r, "guide " + delivery);
                    Assert.That(r.outcome.Gate, Is.EqualTo(HitGate.PartyMember), delivery.ToString());
                }
                s.NpcCombat.Get(Gorn).Disengage();
                AssertDropped(Strike(s, Touch(s, Npc(Gorn), Hero())), "guide without a fight");
            }
        }

        [Test]
        public void SummonedCreature_OfTheHero_NeverHurtsHim()
        {
            // CP GetAttitudeToPlayer: a member of the player's party, summoned ones included, is Friendly to him.
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Kliff, PartyMemberType.Summoned);
                s.NpcCombat.Engage(Kliff, Wolf, AttackReason.Enemy);
                AssertDropped(Strike(s, Touch(s, Npc(Kliff), Hero())), "summoned, fighting");
                s.NpcCombat.Get(Kliff).Disengage();
                AssertDropped(Strike(s, Touch(s, Npc(Kliff), Hero())), "summoned, no fight");
            }
        }

        [Test]
        public void HostileFoe_FightingACompanion_StillHurtsTheHeroItTouches()
        {
            // the filter spares only bystanders the attacker likes better than its target: a wolf hostile to everyone
            // that bites the hero while it fights Gorn hurts him (G3 compares attitudes, not sides).
            using (var f = Fixture())
            {
                var s = f.NewGame();
                s.Party.JoinCompanion(Gorn);
                s.NpcCombat.Engage(Wolf, Gorn, AttackReason.Enemy);
                var hit = Touch(s, Npc(Wolf, "Wolf", humanoid: false), Hero());
                hit.AttackerToVictim = hit.AttackerToTarget = Attitude.Hostile; // the species table of the content decides this
                Assert.That(CombatResolver.Gate(hit), Is.EqualTo(HitGate.None));
            }
        }
    }
}
