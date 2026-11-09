using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-241: a lethal plain arrow kills a standing orc, the blunt arrow only knocks him out (the owner's case: his equipped
    /// ammunition was BluntArrow); port option "Arrows finish downed foes" (ranged hits finish a downed killable NPC like the
    /// melee finishing move; off = G3, no damage to a Down victim).
    /// </summary>
    public class Fix241Tests
    {
        static readonly CombatRules Rules = TestRules.Combat();
        static readonly ActorId Orc = new ActorId("Orc_Warrior");

        static CombatResolver Resolver(bool option) =>
            new CombatResolver(Rules, RuleOptions.Default, new SeededRandom(7), new CombatPortOptions { RangedFinishesDowned = option });

        static Combatant Player() => new Combatant
        {
            Id = ActorId.Player, IsPlayer = true, Level = 10, Strength = 100, Dexterity = 100, HitPoints = 200, Stamina = 150,
            Weapon = WeaponClass.Bow, Perks = new HashSet<string>(),
        };

        static Combatant OrcNpc(VitalState state = VitalState.Alive, bool humanoid = true) => new Combatant
        {
            Id = Orc, Species = "Orc", Humanoid = humanoid, HitPoints = state == VitalState.Down ? 0 : 10, State = state,
            Protection = DamageTypeValues.All(CombatStats.NpcProtection(20)),
        };

        static HitContext Arrow(Combatant victim, DamageType type = DamageType.Missile, int playerLevel = 10) => new HitContext
        {
            Attacker = Player(), Victim = victim, Delivery = HitDelivery.Missile, DamageType = type, DamageAmount = 60, HitMultiplier = 1f,
            PlayerLevel = playerLevel,
        };

        [Test]
        public void LethalPlainArrow_KillsAStandingOrc_TheBluntArrowKnocksHimOut()
        {
            var plain = Resolver(true).Resolve(Arrow(OrcNpc()));
            Assert.That(plain.Reaction, Is.EqualTo(HitReaction.Dead));
            Assert.That(plain.Killed, Is.True);
            var blunt = Resolver(true).Resolve(Arrow(OrcNpc(), DamageType.Impact));
            Assert.That(blunt.Killed, Is.False, "G3 IsDeadlyDamage: a blunt missile on a humanoid only knocks out");
            Assert.That(blunt.KnockedOut, Is.True);
            Assert.That(RangedRules.ProjectileDamageType(null), Is.EqualTo(DamageType.Missile), "the plain Arrow has no damage entry: Missile");
        }

        [Test]
        public void Option_Off_IsG3_NoEffectOnADownedVictim()
        {
            var hit = Arrow(OrcNpc(VitalState.Down));
            Assert.That(Resolver(false).FinishesDowned(hit, byPlayerParty: true), Is.False);
            Assert.That(Resolver(false).Resolve(hit).Result, Is.EqualTo(HitResult.Ignored), "DoLogicalDamage: AI mode Down");
            Assert.That(Resolver(true).Resolve(hit).Result, Is.EqualTo(HitResult.Ignored), "ordinary hits still ignore him");
        }

        [Test]
        public void Option_On_ArrowAndSpellMissileFinishTheDownedKillable()
        {
            var r = Resolver(true);
            Assert.That(r.FinishesDowned(Arrow(OrcNpc(VitalState.Down)), true), Is.True, "arrow");
            Assert.That(r.FinishesDowned(Arrow(OrcNpc(VitalState.Down), DamageType.Blade), true), Is.True, "sharp bolt");
            var spell = Arrow(OrcNpc(VitalState.Down), DamageType.Fire);
            spell.Delivery = HitDelivery.Spell;
            spell.SpellProjectile = true;
            Assert.That(r.FinishesDowned(spell, true), Is.True, "fireball");
            spell.SpellProjectile = false;
            spell.AreaSpell = true;
            Assert.That(r.FinishesDowned(spell, true), Is.False, "area spells are not ranged hits");
            var melee = Arrow(OrcNpc(VitalState.Down));
            melee.Delivery = HitDelivery.Melee;
            Assert.That(r.FinishesDowned(melee, true), Is.False, "melee needs the finishing move");
            Assert.That(r.FinishesDowned(Arrow(OrcNpc(VitalState.Alive)), true), Is.False, "standing: an ordinary hit");
            Assert.That(r.FinishesDowned(Arrow(OrcNpc(VitalState.Dead)), true), Is.False);
            Assert.That(r.FinishesDowned(Arrow(OrcNpc(VitalState.Down)), byPlayerParty: false), Is.False, "NPC shooters: G3");
            var noDamage = Arrow(OrcNpc(VitalState.Down));
            noDamage.DamageAmount = 0;
            Assert.That(r.FinishesDowned(noDamage, true), Is.False, "not offensive");
        }

        [Test]
        public void Option_On_SparesTheProtected_TheParty_AndBluntOnHumanoids()
        {
            var r = Resolver(true);
            var immortal = OrcNpc(VitalState.Down);
            immortal.Immortal = true;
            Assert.That(r.FinishesDowned(Arrow(immortal), true), Is.False, "immortal / story-protected");
            var friend = OrcNpc(VitalState.Down);
            friend.FriendType = true;
            Assert.That(r.FinishesDowned(Arrow(friend, playerLevel: 2), true), Is.False,
                        "friend-type NPC, Medium, hero below level 3 (CanBeKilled)");
            Assert.That(r.FinishesDowned(Arrow(friend, playerLevel: 3), true), Is.True);
            var companion = Arrow(OrcNpc(VitalState.Down));
            companion.VictimInAttackersParty = true;
            companion.VictimIsTarget = false;
            Assert.That(r.FinishesDowned(companion, true), Is.False, "his own party (OnTouchDamage)");
            Assert.That(r.FinishesDowned(Arrow(OrcNpc(VitalState.Down), DamageType.Impact), true), Is.False,
                        "the blunt arrow never kills a humanoid (G3 data: Impact missile, IsDeadlyDamage)");
            Assert.That(r.FinishesDowned(Arrow(OrcNpc(VitalState.Down, humanoid: false), DamageType.Impact), true), Is.True,
                        "on a beast it is an ordinary arrow");
            var hero = OrcNpc(VitalState.Down);
            hero.Id = ActorId.Player;
            hero.IsPlayer = true;
            Assert.That(r.FinishesDowned(Arrow(hero), true), Is.False);
        }

        sealed class Recorder
        {
            public readonly List<string> Lines = new List<string>();

            public Recorder(IEventBus events)
            {
                events.Subscribe<HitResolved>(e => Lines.Add($"hit {e.Attacker}->{e.Victim} killed={e.Outcome.Killed}"));
                events.Subscribe<ActorDefeated>(e => Lines.Add($"defeat {e}"));
                events.Subscribe<CombatShout>(e => Lines.Add($"shout {e}"));
            }
        }

        static (List<string> lines, long xp, bool dead) Run(bool ranged, bool option)
        {
            using (var f = new SessionFixture())
            {
                var session = f.NewGame();
                var gnar = SessionFixture.Npc;
                var vitals = session.Combat.Vitals;
                session.Combat.Port.RangedFinishesDowned = option;
                vitals.AddHitPoints(gnar, -100000);
                vitals.KnockOut(gnar, byPlayerParty: true);
                session.Tick(1f);
                var rec = new Recorder(session.Events);
                var victim = new Combatant { Id = gnar, Species = "Orc", Humanoid = true, State = vitals.StateOf(gnar) };
                bool killed = ranged
                    ? session.Combat.FinishByRanged(Arrow(victim, playerLevel: session.Hero.Level), byPlayerParty: true)
                    : session.Combat.Finish(ActorId.Player, victim, session.Hero.Level, true);
                session.Tick(1f);
                Assert.That(killed, Is.EqualTo(vitals.IsDead(gnar)));
                return (rec.Lines, session.Hero.Xp, vitals.IsDead(gnar));
            }
        }

        [Test]
        public void RangedFinish_HasTheMeleeFinishingMovesConsequences()
        {
            var melee = Run(ranged: false, option: true);
            var ranged = Run(ranged: true, option: true);
            Assert.That(melee.dead, Is.True);
            Assert.That(ranged.dead, Is.True);
            Assert.That(ranged.lines, Is.EqualTo(melee.lines), "same killing hit, defeat and cry: crime, enclave, quest kills see the same");
            Assert.That(ranged.lines.Any(l => l.StartsWith("hit player->") && l.EndsWith("killed=True")), Is.True, string.Join("; ", ranged.lines));
            Assert.That(ranged.xp, Is.EqualTo(melee.xp), "XP once, as for the finishing move");
            var off = Run(ranged: true, option: false);
            Assert.That(off.dead, Is.False, "option off: G3, he stays down");
            Assert.That(off.lines, Is.Empty);
        }
    }
}
