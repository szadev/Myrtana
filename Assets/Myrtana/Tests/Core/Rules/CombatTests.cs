using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The combat core against the CP 1.75 numbers of gameplay-logic.md §2–3 (AB and extended AI on unless stated).</summary>
    public class CombatTests
    {
        static readonly CombatRules Rules = TestRules.Combat();
        static readonly RuleOptions Vanilla = new RuleOptions(alternativeBalancing: false, extendedAI: false);
        static readonly ActorId Orc = new ActorId("Orc_Warrior");

        static CombatResolver Resolver(RuleOptions options = null, ulong seed = 7) =>
            new CombatResolver(Rules, options ?? RuleOptions.Default, new SeededRandom(seed));

        static Combatant Player(params string[] perks) => new Combatant
        {
            Id = ActorId.Player, IsPlayer = true, Level = 10, Strength = 100, Dexterity = 100, Intelligence = 0,
            Weapon = WeaponClass.OneHanded, Action = CombatAction.Attack, HitPoints = 200, Stamina = 150, Mana = 100,
            Perks = new HashSet<string>(perks),
        };

        /// <summary>An NPC snapshot: protection min(80, 1.5·LevelMax), level for the weapon skill.</summary>
        static Combatant Npc(int levelMax = 20, int level = 0, string species = "Human", bool humanoid = true, int strength = 40) => new Combatant
        {
            Id = Orc, Species = species, Humanoid = humanoid, Level = level, Strength = strength,
            Protection = DamageTypeValues.All(CombatStats.NpcProtection(levelMax)),
            Weapon = WeaponClass.OneHanded, Action = CombatAction.Attack, HitPoints = 400, Stamina = 100, Mana = 1000,
        };

        static HitContext Melee(Combatant attacker, Combatant victim, int damage = 50, DamageType type = DamageType.Blade) =>
            new HitContext { Attacker = attacker, Victim = victim, Delivery = HitDelivery.Melee, DamageType = type, DamageAmount = damage,
                              PlayerLevel = 10 };

        // ------------------------------------------------------------------------------------------- poison and disease (combat-6)

        [TestCase(HitDelivery.Missile, DamageType.Blade, CombatAction.Attack, true)]
        [TestCase(HitDelivery.Melee, DamageType.Impact, CombatAction.Attack, true)]
        [TestCase(HitDelivery.Melee, DamageType.Blade, CombatAction.PierceAttack, true)]
        [TestCase(HitDelivery.Melee, DamageType.Blade, CombatAction.HackAttack, true)]
        [TestCase(HitDelivery.Melee, DamageType.Blade, CombatAction.Attack, false)]
        public void PoisonedWeapon_PoisonsByMissileBluntOrPierce(HitDelivery delivery, DamageType type, CombatAction action, bool poisons)
        {
            var player = Player();
            player.Action = action;
            var hit = Melee(player, Npc(), 10, type);
            hit.Delivery = delivery;
            hit.Quality = WeaponQuality.Poisoned;
            Assert.That(Resolver().Resolve(hit).Poisons, Is.EqualTo(poisons));
        }

        [Test]
        public void Poison_OnlyThePlayersWeaponPoisonsNpcs_EvilIsImmune_ThePerkProtectsThePlayer()
        {
            var npcAttacker = Npc();
            var hit = Melee(npcAttacker, Npc(), 10, DamageType.Impact);
            hit.Quality = WeaponQuality.Poisoned | WeaponQuality.Diseased;
            var o = Resolver().Resolve(hit);
            Assert.That(o.Poisons || o.Diseases, Is.False, "an NPC's weapon does not poison NPCs");

            var evil = Npc();
            evil.Evil = true;
            hit = Melee(Player(), evil, 10, DamageType.Impact);
            hit.Quality = WeaponQuality.Poisoned;
            Assert.That(Resolver().Resolve(hit).Poisons, Is.False, "evil creatures are immune");

            hit = Melee(Npc(), Player(), 10, DamageType.Impact);
            hit.Quality = WeaponQuality.Poisoned | WeaponQuality.Diseased;
            o = Resolver().Resolve(hit);
            Assert.That(o.Poisons && o.Diseases, Is.True, "anyone's poisoned weapon poisons the player");
            hit = Melee(Npc(), Player(CombatStats.ImmuneToPoisonPerk), 10, DamageType.Impact);
            hit.Quality = WeaponQuality.Poisoned | WeaponQuality.Diseased;
            o = Resolver().Resolve(hit);
            Assert.That(o.Poisons, Is.False, "Perk_ImmuneToPoison");
            Assert.That(o.Diseases, Is.True);
        }

        [Test]
        public void Ragdoll_ForStandingDeaths_ImpulseByTheKillersMove()
        {
            // combat-6, ZS_RagDollDead: a standing victim falls as a ragdoll; a lying one (finisher) plays the dying motion.
            Assert.That(RagdollRules.UsesRagdoll(HitReaction.Dead, wasDown: false, live: true), Is.True);
            Assert.That(RagdollRules.UsesRagdoll(HitReaction.Dead, wasDown: true, live: true), Is.False, "ZS_Dead from Down");
            Assert.That(RagdollRules.UsesRagdoll(HitReaction.PiercedDead, wasDown: false, live: true), Is.False, "ZS_PiercedDead");
            Assert.That(RagdollRules.UsesRagdoll(HitReaction.Dead, wasDown: false, live: false), Is.False, "a corpse met again");
            var hack = RagdollRules.Melee(CombatAction.HackAttack, human: false);
            var attack = RagdollRules.Melee(CombatAction.Attack, human: false);
            var pierce = RagdollRules.Melee(CombatAction.PierceAttack, human: true);
            Assert.That(attack.Speed, Is.EqualTo(2.5f).Within(1e-4), "15000");
            Assert.That(attack.Lift, Is.EqualTo(1.5f).Within(1e-4), "150");
            Assert.That(RagdollRules.Melee(CombatAction.Attack, human: true).Lift, Is.EqualTo(1.35f).Within(1e-4), "humans 0.9");
            Assert.That(hack.Speed, Is.GreaterThan(attack.Speed));
            Assert.That(pierce.Lift, Is.EqualTo(0f));
            Assert.That(RagdollRules.Projectile(WeaponClass.Bow).Speed, Is.LessThan(RagdollRules.Projectile(WeaponClass.Crossbow).Speed));
            Assert.That(RagdollRules.Projectile(WeaponClass.Bow).Lift, Is.EqualTo(0f));
            Assert.That(RagdollRules.WithoutKiller(human: false).Speed, Is.EqualTo(attack.Speed).Within(1e-4), "no killer: still pushed");
        }

        // ------------------------------------------------------------------------------------------- damage formula

        [Test]
        public void PlayerMelee_IsWeaponPlusHalfStrength_MinusPercentProtection()
        {
            // base 50 + STR/2 50 = 100; NPC LevelMax 20 → protection 30 % → 70
            var o = Resolver().Resolve(Melee(Player(), Npc()));
            Assert.That(o.Result, Is.EqualTo(HitResult.Hit));
            Assert.That(o.Damage, Is.EqualTo(70));
        }

        [TestCase(CombatAction.PowerAttack, 140)]
        [TestCase(CombatAction.HackAttack, 140)]
        [TestCase(CombatAction.QuickAttack, 35)]
        [TestCase(CombatAction.WhirlAttack, 70)]
        public void ActionModifier_AppliesAfterProtection(CombatAction action, int expected)
        {
            var player = Player();
            player.Action = action;
            var hit = Melee(player, Npc());
            hit.VictimIsTarget = true;
            Assert.That(Resolver().Resolve(hit).Damage, Is.EqualTo(expected));
        }

        [Test]
        public void DualWieldPower_DoublesOnlyInTheSecondPose()
        {
            var player = Player();
            player.Action = CombatAction.PowerAttack;
            player.DualWield = true;
            Assert.That(Resolver().Resolve(Melee(player, Npc())).Damage, Is.EqualTo(70));
            player.SecondPose = true;
            Assert.That(Resolver().Resolve(Melee(player, Npc())).Damage, Is.EqualTo(140));
        }

        [Test]
        public void Quality_SharpAndForgedAddTen_WornHalvesTheBase()
        {
            var hit = Melee(Player(), Npc());
            hit.Quality = WeaponQuality.Sharp | WeaponQuality.Forged; // 50 + 20 + 50 = 120 → −36
            Assert.That(Resolver().Resolve(hit).Damage, Is.EqualTo(84));
            hit.Quality = WeaponQuality.Worn; // 50 − 25 + 50 = 75 → −22
            Assert.That(Resolver().Resolve(hit).Damage, Is.EqualTo(53));
        }

        [Test]
        public void SkillPerks_Add25Each_AndVersusOrcs()
        {
            var victim = Npc(species: "Orc");
            var hit = Melee(Player("Perk_1H_2", "Perk_1H_3", "Perk_OrcSlayer"), victim);
            // 50 + 75 + 50 = 175 → −52 = 123
            Assert.That(Resolver().Resolve(hit).Damage, Is.EqualTo(123));
        }

        /// <summary>
        /// fix-227 (owner: the master archer perk did not change the draw): CP 1.75 Perk_Bow_2/3 only add +25 damage each
        /// (GetPlayerSkillDamageBonus); the draw (PowerAim speed 1.0, full draw 1.95 s) and the power level (FUN_1002afc0
        /// checks melee perks only) stay the same.
        /// </summary>
        [Test]
        public void BowPerks_Add25DamageEach_ButNeitherDrawNorPowerLevel()
        {
            var archer = Player("Perk_Bow_1", "Perk_Bow_2", "Perk_Bow_3");
            archer.Weapon = WeaponClass.Bow;
            var hit = new HitContext { Attacker = archer, Victim = Npc(), Delivery = HitDelivery.Missile, DamageType = DamageType.Missile,
                                       DamageAmount = 50 };
            // 50 + DEX/2 50 + 25 + 25 = 150 → −45
            Assert.That(Resolver().Resolve(hit).Damage, Is.EqualTo(105));
            Assert.That(CombatStats.WeaponSkillLevel(archer), Is.EqualTo(0));
            archer.Weapon = WeaponClass.Crossbow;
            archer.Perks = new HashSet<string> { "Perk_Crossbow_2", "Perk_Crossbow_3" };
            Assert.That(CombatStats.WeaponSkillLevel(archer), Is.EqualTo(0));
            Assert.That(RangedRules.HitMultiplier(WeaponClass.Bow, 1.95f), Is.EqualTo(1f));
            Assert.That(MotionSpeedRules.Factor(new MotionSpeedQuery { Motion = CombatMotion.Fixed, IsPlayer = true }, RuleOptions.Default),
                        Is.EqualTo(1f));
            var melee = Player("Perk_1H_2", "Perk_1H_3");
            Assert.That(CombatStats.WeaponSkillLevel(melee), Is.EqualTo(2), "melee perks still raise the power level");
        }

        [Test]
        public void Headshot_DoublesTheBase()
        {
            var hit = new HitContext
            {
                Attacker = Player(), Victim = Npc(), Delivery = HitDelivery.Missile, DamageType = DamageType.Missile,
                DamageAmount = 50, Headshot = true,
            };
            // 100 + DEX/2 50 = 150 → −45
            Assert.That(Resolver().Resolve(hit).Damage, Is.EqualTo(105));
        }

        [Test]
        public void Species_DoubleAndImmune()
        {
            var skeleton = Npc(species: "Skeleton");
            Assert.That(Resolver().Resolve(Melee(Player(), skeleton, type: DamageType.Impact)).Damage, Is.EqualTo(105)); // 150 → −45
            var arrow = new HitContext { Attacker = Player(), Victim = Npc(species: "Golem", humanoid: false), Delivery = HitDelivery.Missile,
                                         DamageType = DamageType.Missile, DamageAmount = 50 };
            Assert.That(Resolver().Resolve(arrow).Gate, Is.EqualTo(HitGate.Immune));
            arrow.Victim = skeleton;
            Assert.That(Resolver().Resolve(arrow).Gate, Is.EqualTo(HitGate.Immune));
            arrow.FireOrExplosiveProjectile = true;
            Assert.That(Resolver().Resolve(arrow).Result, Is.EqualTo(HitResult.Hit));
        }

        [Test]
        public void NpcAttacker_AB_UsesWeaponShareAndStrength()
        {
            var player = Player();
            player.Protection[DamageType.Blade] = CombatStats.PlayerProtection(50, DamageType.Blade, RuleOptions.Default, _ => false); // 10
            var hit = Melee(Npc(strength: 32), player, damage: 40);
            // trunc(40/9 + 0.9·32) = 33, flat protection 10
            Assert.That(Resolver().Resolve(hit).Damage, Is.EqualTo(23));

            player.Protection[DamageType.Blade] = CombatStats.PlayerProtection(50, DamageType.Blade, Vanilla, _ => false); // 20
            // vanilla: damage = STR 32, 20 % → 26
            Assert.That(Resolver(Vanilla).Resolve(hit).Damage, Is.EqualTo(26));
        }

        [Test]
        public void NpcAttacker_OrcAndFists_AB()
        {
            var orc = Npc(species: "Orc", strength: 40);
            Assert.That(Resolver().Resolve(Melee(orc, Player(), damage: 70)).Damage, Is.EqualTo(40)); // 70/7 + 30
            orc.Weapon = WeaponClass.Fist;
            var npcVictim = Npc(levelMax: 0);
            Assert.That(Resolver().Resolve(Melee(orc, npcVictim)).Damage, Is.EqualTo(20)); // fists vs NPC: STR/2
        }

        [Test]
        public void AB_PlayerVictim_ChipDamageOnlyForHeavyHits()
        {
            var player = Player();
            player.Protection = DamageTypeValues.All(300);
            var attacker = Npc(strength: 300);
            Assert.That(Resolver().Resolve(Melee(attacker, player, damage: 0)).Damage, Is.InRange(2, 5)); // 270 → 1–2 %
            var weak = Npc(strength: 30);
            weak.Action = CombatAction.Attack;
            Assert.That(Resolver().Resolve(Melee(weak, player, damage: 0)).Damage, Is.EqualTo(0));
        }

        [Test]
        public void MonsterVersusHumanoidNpc_IsDividedByTen_SummonedByTwo()
        {
            var wolf = Npc(species: "Wolf", humanoid: false, strength: 40);
            var villager = Npc(levelMax: 0);
            Assert.That(Resolver().Resolve(Melee(wolf, villager)).Damage, Is.EqualTo(4));
            wolf.Summoned = true;
            Assert.That(Resolver().Resolve(Melee(wolf, villager)).Damage, Is.EqualTo(20));
        }

        [Test]
        public void ArmorPerks_DefaultMultiplyAndCap_AbAddsItemShare()
        {
            var victim = Player("Perk_HeavyArmor");
            victim.Armor = ArmorKind.Armor;
            victim.ArmorItemProtection[DamageType.Blade] = 100;
            Assert.That(CombatStats.WithArmorPerks(40, victim, DamageType.Blade, Vanilla), Is.EqualTo(50));
            Assert.That(CombatStats.WithArmorPerks(80, victim, DamageType.Blade, Vanilla), Is.EqualTo(90));
            Assert.That(CombatStats.WithArmorPerks(40, victim, DamageType.Blade, RuleOptions.Default), Is.EqualTo(45));
        }

        [Test]
        public void PlayerProtection_AbFifthForBladeAndImpact_ResistPerkAfterCap()
        {
            Assert.That(CombatStats.PlayerProtection(100, DamageType.Blade, RuleOptions.Default, _ => false), Is.EqualTo(20));
            Assert.That(CombatStats.PlayerProtection(100, DamageType.Fire, RuleOptions.Default, _ => false), Is.EqualTo(40));
            Assert.That(CombatStats.PlayerProtection(300, DamageType.Fire, Vanilla, p => p == "Perk_ResistHeat"), Is.EqualTo(90));
        }

        // ------------------------------------------------------------------------------------------------ blocking

        static Combatant Blocker(Combatant c)
        {
            c.InParade = true;
            c.Action = CombatAction.Parade;
            return c;
        }

        [Test]
        public void MeleeBlock_CostsHalfTheDamageInStamina()
        {
            var victim = Blocker(Npc());
            var o = Resolver().Resolve(Melee(Player(), victim)); // 100 before protection
            Assert.That(o.Result, Is.EqualTo(HitResult.Blocked));
            Assert.That(o.StaminaCost, Is.EqualTo(50));
            Assert.That(o.Damage, Is.EqualTo(0));
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.ParadeStumble));
        }

        [Test]
        public void ShieldBlock_ThreeEighths_OrAQuarterWithThePerk()
        {
            var npc = Blocker(Npc());
            npc.Shield = true;
            Assert.That(Resolver().Resolve(Melee(Player(), npc)).StaminaCost, Is.EqualTo(38));
            var player = Blocker(Player("Perk_Shield_2"));
            player.Shield = true;
            Assert.That(Resolver().Resolve(Melee(Npc(strength: 100), player)).StaminaCost, Is.EqualTo(23)); // AB: trunc(50/9 + 90) = 95 → /2 → /2

        }

        [Test]
        public void BlockShortfall_HurtsOnlyWithoutExtendedAI()
        {
            var victim = Blocker(Npc());
            victim.Stamina = 20;
            Assert.That(Resolver().Resolve(Melee(Player(), victim)).Damage, Is.EqualTo(0));
            var vanilla = Resolver(Vanilla).Resolve(Melee(Player(), victim));
            Assert.That(vanilla.StaminaCost, Is.EqualTo(20));
            Assert.That(vanilla.Damage, Is.EqualTo(30));
        }

        /// <summary>
        /// fix-81 (owner: "the block did not work against a scavenger"): G3 CanParadeMoveOf — an attacker in fist mode that is
        /// not humanoid cannot be parried by anything (animals and monsters strike with PhysicalFist claws and teeth), with or
        /// without a shield, under every rule option; IsInFistMode is false for the Minecrawler, so its claws can be parried.
        /// </summary>
        [TestCase("Scavenger", false, false)]
        [TestCase("Wolf", false, false)]
        [TestCase("Wolf", true, false)]
        [TestCase("Troll", true, false)]
        [TestCase("Minecrawler", false, true)]
        [TestCase("Minecrawler", true, true)]
        public void CreatureAttacks_AreUnblockable_ExceptTheMinecrawlers(string species, bool shield, bool blocked)
        {
            var creature = Npc(species: species, humanoid: false);
            creature.Weapon = WeaponClass.Fist;
            var hero = Blocker(Player());
            hero.Shield = shield;
            foreach (var options in new[] { RuleOptions.Default, Vanilla })
                Assert.That(Resolver(options).Resolve(Melee(creature, hero)).Result,
                            Is.EqualTo(blocked ? HitResult.Blocked : HitResult.Hit), species);
            Assert.That(CombatResolver.IsInFistMode(WeaponClass.Fist, species), Is.EqualTo(species != "Minecrawler"));
        }

        static CombatResolver ShieldBeasts(RuleOptions options = null)
        {
            var r = Resolver(options);
            r.Port.ShieldBlocksCreatures = true;
            return r;
        }

        static Combatant Creature(string species, int level = 0, CombatAction action = CombatAction.Attack)
        {
            var c = Npc(level: level, species: species, humanoid: false);
            c.Weapon = WeaponClass.Fist;
            c.Action = action;
            return c;
        }

        static Combatant ShieldHero(params string[] perks)
        {
            var hero = Blocker(Player(perks));
            hero.Shield = true;
            return hero;
        }

        /// <summary>
        /// fix-109 [owner] option "Shield protects from beasts" (UP 1.04 changelog item 53): off it is CP 1.75 (above); on, a
        /// raised shield blocks a wolf bite as a human attack (stamina with the shield discount, no damage with AAI), a
        /// weapon-only parade still does not, and trolls and monsters go through the shield.
        /// </summary>
        [Test]
        public void ShieldBeasts_Off_IsCp175_ShieldDoesNotStopAWolf()
        {
            Assert.That(new CombatResolver(Rules, RuleOptions.Default, new SeededRandom(7)).Port.ShieldBlocksCreatures, Is.False, "default = original");
            Assert.That(Resolver().Resolve(Melee(Creature("Wolf"), ShieldHero())).Result, Is.EqualTo(HitResult.Hit));
        }

        [Test]
        public void ShieldBeasts_On_ShieldBlocksAWolfBite_LikeAHumanAttack()
        {
            foreach (var options in new[] { RuleOptions.Default, Vanilla })
            {
                var o = ShieldBeasts(options).Resolve(Melee(Creature("Wolf"), ShieldHero()));
                Assert.That(o.Result, Is.EqualTo(HitResult.Blocked));
                Assert.That(o.StaminaCost, Is.GreaterThan(0));
                Assert.That(o.Damage, Is.EqualTo(0));
                Assert.That(o.Reaction, Is.EqualTo(HitReaction.ParadeStumble), "D = 2 - 0");
            }
            int plain = ShieldBeasts().Resolve(Melee(Creature("Wolf"), ShieldHero())).StaminaCost;
            int perk = ShieldBeasts().Resolve(Melee(Creature("Wolf"), ShieldHero(CombatStats.ShieldPerk))).StaminaCost;
            Assert.That(perk, Is.LessThan(plain), "G3 shield rule: Perk_Shield_2 halves the block cost (dmg/4 instead of 3/8)");
            var npc = Blocker(Npc());
            npc.Shield = true;
            Assert.That(ShieldBeasts().Resolve(Melee(Creature("Scavenger"), npc)).Result, Is.EqualTo(HitResult.Blocked), "NPC shield bearers too");
        }

        [Test]
        public void ShieldBeasts_On_WeaponOnlyParadeStillDoesNotStopCreatures()
        {
            var r = ShieldBeasts();
            Assert.That(r.Resolve(Melee(Creature("Wolf"), Blocker(Player()))).Result, Is.EqualTo(HitResult.Hit));
            var twoHanded = ShieldHero();
            twoHanded.Weapon = WeaponClass.TwoHanded;
            Assert.That(r.Resolve(Melee(Creature("Wolf"), twoHanded)).Result, Is.EqualTo(HitResult.Hit), "the shield is on the back with a 2H");
            // UP: the Minecrawler is no longer parried by a weapon (CP: it is, see above) but by a shield
            Assert.That(r.Resolve(Melee(Creature("Minecrawler"), Blocker(Player()))).Result, Is.EqualTo(HitResult.Hit));
            Assert.That(r.Resolve(Melee(Creature("Minecrawler"), ShieldHero())).Result, Is.EqualTo(HitResult.Blocked));
        }

        [Test]
        public void ShieldBeasts_On_TrollsAndMonstersBreakThrough_PowerAttacksStagger()
        {
            var r = ShieldBeasts();
            foreach (var species in new[] { "Troll", "Golem", "Dragon" })
                Assert.That(r.Resolve(Melee(Creature(species, 50, CombatAction.PowerAttack), ShieldHero(CombatStats.ShieldPerk))).Result,
                            Is.EqualTo(HitResult.Hit), species);
            // a strong beast's power attack is blocked but throws the blocker into the heavy parade stumble (D = 4 + 3 - 0 > 2)
            var o = r.Resolve(Melee(Creature("Shadowbeast", 50, CombatAction.PowerAttack), ShieldHero()));
            Assert.That(o.Result, Is.EqualTo(HitResult.Blocked));
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.HeavyParadeStumble));
            // demons and ogres: only with the powerful shield defence (Perk_Shield_2)
            Assert.That(r.Resolve(Melee(Creature("Ogre"), ShieldHero())).Result, Is.EqualTo(HitResult.Hit));
            Assert.That(r.Resolve(Melee(Creature("Demon"), ShieldHero(CombatStats.ShieldPerk))).Result, Is.EqualTo(HitResult.Blocked));
        }

        [Test]
        public void ShieldBeasts_On_ShieldBlocksFistFighters()
        {
            var brawler = Npc();
            brawler.Weapon = WeaponClass.Fist;
            Assert.That(Resolver().Resolve(Melee(brawler, ShieldHero())).Result, Is.EqualTo(HitResult.Hit), "CP: a weapon does not parry fists");
            Assert.That(ShieldBeasts().Resolve(Melee(brawler, ShieldHero())).Result, Is.EqualTo(HitResult.Blocked), "UP: the shield does");
        }

        [Test]
        public void PierceNeedsShield_SprintIsUnblockableWithExtendedAI()
        {
            var player = Player();
            player.Action = CombatAction.PierceAttack;
            Assert.That(Resolver().Resolve(Melee(player, Blocker(Npc()))).Result, Is.EqualTo(HitResult.Hit));
            var sprinter = Npc();
            sprinter.Action = CombatAction.SprintAttack;
            Assert.That(Resolver().Resolve(Melee(sprinter, Blocker(Player()))).Result, Is.EqualTo(HitResult.Hit));
            Assert.That(Resolver(Vanilla).Resolve(Melee(sprinter, Blocker(Player()))).Result, Is.EqualTo(HitResult.Blocked));
        }

        [Test]
        public void MagicBlock_AB_ScalesManaCostWithIntelligence()
        {
            var mage = Npc();
            mage.Mage = true;
            mage.Weapon = WeaponClass.Staff;
            mage.CastStance = true;
            mage.InParade = true;
            mage.Intelligence = 300;
            var spell = new HitContext { Attacker = Npc(strength: 50), Victim = mage, Delivery = HitDelivery.Spell, DamageType = DamageType.Fire };
            var o = Resolver().Resolve(spell);
            Assert.That(o.Result, Is.EqualTo(HitResult.Blocked));
            Assert.That(o.ManaCost, Is.EqualTo(40)); // 50 · (1 − 100/500)
        }

        [Test]
        public void MagicBlock_NeedsTheStaffParadeAndThePerk()
        {
            HitContext Spell(Combatant victim) =>
                new HitContext { Attacker = Npc(strength: 50), Victim = victim, Delivery = HitDelivery.Spell, DamageType = DamageType.Fire };
            var mage = Npc();
            mage.Mage = true;
            mage.Weapon = WeaponClass.Staff;
            mage.CastStance = true;
            Assert.That(Resolver().Resolve(Spell(mage)).Result, Is.Not.EqualTo(HitResult.Blocked), "staff not raised (G3 CanParadeMagic: IsInParadeMode)");
            mage.InParade = true;
            var unseen = Spell(mage);
            unseen.VictimSeesAttacker = false;
            Assert.That(Resolver().Resolve(unseen).Result, Is.Not.EqualTo(HitResult.Blocked), "caster out of view");

            var hero = Player();
            hero.Weapon = WeaponClass.Staff;
            hero.CastStance = true;
            hero.InParade = true;
            hero.Mana = 1000;
            Assert.That(Resolver().Resolve(Spell(hero)).Result, Is.Not.EqualTo(HitResult.Blocked), "the hero needs Perk_StaffMagic");
            var mageHero = Player(StaffParade.Perk);
            mageHero.Weapon = WeaponClass.Staff;
            mageHero.CastStance = true;
            mageHero.InParade = true;
            mageHero.Mana = 1000;
            Assert.That(Resolver().Resolve(Spell(mageHero)).Result, Is.EqualTo(HitResult.Blocked));
        }

        // ---------------------------------------------------------------------------------- reactions and lethality

        [Test]
        public void PowerDelta_SelectsTheStagger()
        {
            var player = Player("Perk_1H_2", "Perk_1H_3");
            player.Action = CombatAction.PowerAttack; // 4 + 2
            var victim = Npc(level: 35);              // skill 2, + shield 1
            victim.Shield = true;
            var o = Resolver().Resolve(Melee(player, victim));
            Assert.That(o.PowerDelta, Is.EqualTo(3));
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.Stumble));
            victim.Shield = false;
            Assert.That(Resolver().Resolve(Melee(player, victim)).Reaction, Is.EqualTo(HitReaction.SitKnockDown));
            victim.MeleeConfig = false; // G3 AssessHit: only the melee weapon config sits down (bow, spell, nothing drawn → Stumble)
            Assert.That(Resolver().Resolve(Melee(player, victim)).Reaction, Is.EqualTo(HitReaction.Stumble));
            victim.MeleeConfig = true;
            player.Action = CombatAction.QuickAttack;
            Assert.That(Resolver().Resolve(Melee(player, victim)).Reaction, Is.EqualTo(HitReaction.QuickStumble));
        }

        /// <summary>
        /// fix-67 (r33 hammerclan_02 R33-2): G3 OnAI_GetUpParade raises the parade 0.25 s into the get-up, so a boss's
        /// power attack during the rest of it is parried (DefenseLevel 2: D 5 → HeavyParadeStumble) instead of sitting the
        /// hero down again; only the first 0.25 s (and a blow from behind) still lands.
        /// </summary>
        [Test]
        public void GetUpParade_ParriesAfterTheRaise_APowerAttackNoLongerSitsTheHeroDownAgain()
        {
            Assert.That(SitKnockDownRules.GetUpParadeBlocks(0.1f), Is.False);
            Assert.That(SitKnockDownRules.GetUpParadeBlocks(0.3f), Is.True);

            var boss = Npc(levelMax: 50, level: 50); // skill 3
            boss.Action = CombatAction.PowerAttack;  // 4 + 3
            var hero = Player();
            hero.Shield = true;
            hero.MeleeConfig = true;
            hero.Action = CombatAction.GetUpParade;  // defence 2
            hero.InParade = SitKnockDownRules.GetUpParadeBlocks(0.6f);
            var o = Resolver().Resolve(Melee(boss, hero));
            Assert.That(o.Result, Is.EqualTo(HitResult.Blocked));
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.HeavyParadeStumble));

            hero.InParade = SitKnockDownRules.GetUpParadeBlocks(0.1f);
            Assert.That(Resolver().Resolve(Melee(boss, hero)).Reaction, Is.EqualTo(HitReaction.SitKnockDown), "still rising");
        }

        [Test]
        public void HumanFights_EndInKnockOut_UnlessHostileOrFinishing()
        {
            var victim = Npc();
            victim.HitPoints = 10;
            var hit = Melee(Player(), victim);
            hit.AttackerToVictim = Attitude.Neutral;
            hit.VictimToAttacker = Attitude.Angry;
            var o = Resolver().Resolve(hit);
            Assert.That(o.Lethal, Is.True);
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.LieKnockDown));
            hit.Attacker.Action = CombatAction.FinishingAttack;
            Assert.That(Resolver().Resolve(hit).Reaction, Is.EqualTo(HitReaction.Dead));
            hit.Attacker.Action = CombatAction.Attack;
            hit.VictimToAttacker = Attitude.Hostile;
            Assert.That(Resolver().Resolve(hit).Reaction, Is.EqualTo(HitReaction.Dead));
        }

        [Test]
        public void BossAndXardas_LethalMeleeKills_NoKnockOut()
        {
            // G3 IsDeadlyDamage (CP @1002db80): IsBoss and the name "Xardas" return "deadly" before the humanoid knock-out
            // rule (fix-41: it was read inverted). Non-hostile humanoid melee knocks a plain NPC out, but kills these two.
            var victim = Npc();
            victim.HitPoints = 10;
            var hit = Melee(Player(), victim);
            hit.AttackerToVictim = hit.VictimToAttacker = Attitude.Neutral;
            Assert.That(Resolver().Resolve(hit).KnockedOut, Is.True, "a plain human is knocked out");
            victim.Boss = true;
            Assert.That(Resolver().Resolve(hit).Killed, Is.True, "a boss dies");
            victim.Boss = false;
            victim.Id = new ActorId("Xardas");
            Assert.That(Resolver().Resolve(hit).Killed, Is.True, "Xardas dies");
            victim.Immortal = true;
            victim.HitPoints = 10;
            Assert.That(Resolver().Resolve(hit).Killed, Is.False, "only Immortal protects him");
        }

        [Test]
        public void Xardas_BittenByAWolf_Dies_LikeAnyKillableNpc()
        {
            // NPC-vs-NPC: a creature's lethal bite on a killable story NPC is deadly (G3 has no other protection).
            var wolf = Npc(species: "Wolf", humanoid: false, strength: 200);
            wolf.Id = new ActorId("IceWolf#1");
            var xardas = Npc();
            xardas.Id = new ActorId("Xardas");
            xardas.FriendType = true;
            xardas.HitPoints = 5;
            var hit = Melee(wolf, xardas);
            Assert.That(Resolver().Resolve(hit).Killed, Is.True);
            hit.PlayerLevel = 2; // Friend type on Medium while the player is below level 3: CanBeKilled false
            Assert.That(Resolver().Resolve(hit).Killed, Is.False);
        }

        [Test]
        public void HeavyLethalBlow_KnocksUnconscious_PierceKnocksPierced()
        {
            var player = Player("Perk_1H_3");
            player.Action = CombatAction.PowerAttack; // D = 6
            var victim = Npc();
            victim.HitPoints = 10;
            var hit = Melee(player, victim);
            hit.AttackerToVictim = hit.VictimToAttacker = Attitude.Neutral;
            Assert.That(Resolver().Resolve(hit).Reaction, Is.EqualTo(HitReaction.Unconscious));
            player.Action = CombatAction.PierceAttack;
            Assert.That(Resolver().Resolve(hit).Reaction, Is.EqualTo(HitReaction.PiercedKnockOut));
        }

        [Test]
        public void Player_CannotBeKilled_BelowLevel3_OnMedium()
        {
            var player = Player();
            player.HitPoints = 5;
            var hit = Melee(Npc(strength: 60), player);
            hit.PlayerLevel = 2;
            Assert.That(Resolver().Resolve(hit).KnockedOut, Is.True);
            var hard = new RuleOptions(difficulty: Difficulty.Hard);
            Assert.That(Resolver(hard).Resolve(hit).Killed, Is.True);
            hit.PlayerLevel = 3;
            Assert.That(Resolver().Resolve(hit).Killed, Is.True);
        }

        [Test]
        public void Immortal_TakesAtMost30_AndNothingLethal()
        {
            var victim = Npc();
            victim.Immortal = true;
            Assert.That(Resolver().Resolve(Melee(Player(), victim)).Damage, Is.EqualTo(30));
            victim.HitPoints = 20;
            var o = Resolver().Resolve(Melee(Player(), victim));
            Assert.That(o.Damage, Is.EqualTo(0));
            Assert.That(o.Lethal, Is.False);
        }

        [Test]
        public void ImpactArrow_KnocksHumansOut_StrongDrawSitsThemDown()
        {
            var victim = Npc();
            var arrow = new HitContext { Attacker = Player(), Victim = victim, Delivery = HitDelivery.Missile, DamageType = DamageType.Impact,
                                         DamageAmount = 50, HitMultiplier = 0.8f };
            var o = Resolver().Resolve(arrow);
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.SitKnockDown));
            Assert.That(o.Damage, Is.EqualTo(40));
            victim.HitPoints = 30;
            Assert.That(Resolver().Resolve(arrow).Reaction, Is.EqualTo(HitReaction.Unconscious));
        }

        [Test]
        public void Fix209_LethalArrow_KillsAnOrc_HostileOrNot_DownedOrSittingOrcTakesNone()
        {
            // CP IsDeadlyDamage: a non-impact projectile is deadly (the knock-out rules are for blunt missiles and melee)
            foreach (var attitude in new[] { Attitude.Hostile, Attitude.Neutral, Attitude.Angry })
            {
                var orc = Npc(species: "Orc");
                orc.HitPoints = 10;
                var arrow = new HitContext { Attacker = Player(), Victim = orc, Delivery = HitDelivery.Missile, DamageType = DamageType.Missile,
                                             DamageAmount = 60, HitMultiplier = 1f, VictimToAttacker = attitude, AttackerToVictim = attitude };
                var o = Resolver().Resolve(arrow);
                Assert.That(o.Lethal, Is.True, attitude.ToString());
                Assert.That(o.Reaction, Is.EqualTo(HitReaction.Dead), attitude.ToString());
                Assert.That(o.Killed, Is.True);
            }
            // DoLogicalDamage: no damage to a victim in AI mode Down or lying (SitKnockDown)
            var down = Npc(species: "Orc");
            down.State = VitalState.Down;
            var shot = new HitContext { Attacker = Player(), Victim = down, Delivery = HitDelivery.Missile, DamageType = DamageType.Missile,
                                        DamageAmount = 60, HitMultiplier = 1f };
            Assert.That(Resolver().Resolve(shot).Result, Is.EqualTo(HitResult.Ignored));
            var sitting = Npc(species: "Orc");
            sitting.KnockedDown = true;
            shot.Victim = sitting;
            Assert.That(Resolver().Resolve(shot).Result, Is.EqualTo(HitResult.Ignored));
        }

        [Test]
        public void Assassin_PierceOnUnawareRoutineVictim_Kills()
        {
            var player = Player("Perk_Assassin");
            player.Action = CombatAction.PierceAttack;
            var victim = Npc();
            victim.InRoutine = true;
            var hit = Melee(player, victim);
            hit.VictimSeesAttacker = false;
            var o = Resolver().Resolve(hit);
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.PiercedDead));
            Assert.That(o.Damage, Is.EqualTo(400));
        }

        [Test]
        public void Gates()
        {
            var hit = Melee(Player(), Npc());
            hit.AttackerSeesVictim = false;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.OutOfView));

            var hack = Player();
            hack.Action = CombatAction.HackAttack;
            hit = Melee(hack, Npc());
            hit.VictimIsTarget = false;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.NotTheTarget));

            hit = Melee(Npc(), Npc());
            hit.VictimIsTarget = false;
            hit.AttackerToVictim = Attitude.Friendly;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.FriendlyFire));

            var down = Npc();
            down.State = VitalState.Down;
            Assert.That(Resolver().Resolve(Melee(Player(), down)).Gate, Is.EqualTo(HitGate.VictimUnavailable));

            var god = Player();
            god.GodMode = true;
            Assert.That(Resolver().Resolve(Melee(Npc(), god)).Gate, Is.EqualTo(HitGate.GodMode));
        }

        [Test]
        public void ProtectedBystander_NeutralTargetedByThePlayer_TakesTheHit()
        {
            // fix-25 okara_01: a neutral boar fighting the guide, focused by the hero, must take his sword hits
            var hit = Melee(Player(), Npc());
            hit.VictimFightsSomeoneElse = true;
            hit.VictimToAttacker = Attitude.Neutral;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.ProtectedBystander), "not targeted: protected");
            hit.AttackerTargetsVictim = true;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.None), "targeted neutral: hit lands");
            hit.VictimToAttacker = Attitude.Friendly;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.ProtectedBystander), "friendly stays protected");
            hit.VictimFightsSomeoneElse = false; // fights the hero or his party
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.None));
        }

        // ------------------------------------------------------------------------------------------- the hero's party (fix-30)

        static readonly CombatAction[] MeleeAttacks =
        {
            CombatAction.Attack, CombatAction.PowerAttack, CombatAction.SprintAttack, CombatAction.QuickAttack,
            CombatAction.QuickAttackLeft, CombatAction.QuickAttackRight, CombatAction.SimpleWhirl, CombatAction.WhirlAttack,
            CombatAction.PierceAttack, CombatAction.HackAttack,
        };

        [Test]
        public void PartyMember_HeroMelee_NeverLands_AnyAttackAttitudeOrTarget()
        {
            // G3 OnHit (CP 0x100ad620): a melee touch on a member of the attacker's party never reaches DoLogicalDamage
            foreach (var action in MeleeAttacks)
            foreach (var attitude in new[] { Attitude.Friendly, Attitude.Neutral, Attitude.Angry, Attitude.Hostile })
            foreach (bool targeted in new[] { false, true })
            {
                var hero = Player();
                hero.Action = action;
                var hit = Melee(hero, Npc());
                hit.VictimInAttackersParty = true;
                hit.VictimToAttacker = hit.AttackerToVictim = attitude;
                hit.AttackerTargetsVictim = hit.VictimIsTarget = targeted;
                var outcome = Resolver().Resolve(hit);
                Assert.That(outcome.Result, Is.EqualTo(HitResult.Ignored), $"{action} {attitude} targeted={targeted}");
                Assert.That(outcome.Gate, Is.EqualTo(HitGate.PartyMember));
                Assert.That(outcome.Damage, Is.EqualTo(0));
            }
        }

        [Test]
        public void PartyMember_MissilesAndSpells_OnlyHurtTheShootersOwnTarget_AreasNever()
        {
            // G3 OnTouchDamage (CP 0x100b47b0): a member of the owner's party that is not its current target is ignored;
            // MagicAreaDamageTick skips the caster's party outright.
            var arrow = Melee(Player(), Npc());
            arrow.Delivery = HitDelivery.Missile;
            arrow.VictimInAttackersParty = true;
            arrow.VictimToAttacker = arrow.AttackerToVictim = Attitude.Friendly;
            Assert.That(Resolver().Resolve(arrow).Gate, Is.EqualTo(HitGate.PartyMember), "a stray arrow");

            var bolt = Melee(Player(), Npc(), type: DamageType.Fire);
            bolt.Delivery = HitDelivery.Spell;
            bolt.SpellProjectile = true;
            bolt.VictimInAttackersParty = true;
            bolt.VictimToAttacker = bolt.AttackerToVictim = Attitude.Friendly;
            Assert.That(Resolver().Resolve(bolt).Gate, Is.EqualTo(HitGate.PartyMember), "a spell missile that strays onto a companion");
            bolt.AttackerTargetsVictim = true;
            Assert.That(Resolver().Resolve(bolt).Result, Is.EqualTo(HitResult.Hit), "aimed at the companion (its focus): G3 lets it land");

            var area = Melee(Player(), Npc(), type: DamageType.Ice);
            area.Delivery = HitDelivery.Spell;
            area.AreaSpell = true;
            area.VictimInAttackersParty = true;
            area.AttackerTargetsVictim = true;
            Assert.That(Resolver().Resolve(area).Gate, Is.EqualTo(HitGate.PartyMember), "an area spell, even on the focus");
            area.VictimInAttackersParty = false;
            Assert.That(Resolver().Resolve(area).Gate, Is.Not.EqualTo(HitGate.PartyMember), "others in the area are hit");
        }

        [Test]
        public void Companion_NeverHurtsTheHero_ItIsNotFighting()
        {
            // the companion's own blows (whirls, arrows, spells) that touch the hero: DoLogicalDamage's friendly fire gate
            foreach (var delivery in new[] { HitDelivery.Melee, HitDelivery.Missile, HitDelivery.Spell })
            {
                var npc = Npc();
                npc.Action = CombatAction.WhirlAttack;
                var hit = Melee(npc, Player());
                hit.Delivery = delivery;
                hit.VictimIsTarget = false; // it fights a wolf
                hit.AttackerToVictim = hit.VictimToAttacker = Attitude.Friendly;
                Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.FriendlyFire), delivery.ToString());
            }
        }

        /// <summary>G3 OnTouchDamage's bystander filter (fix-59): an NPC's touch on a non-target, by the attitudes.</summary>
        static HitContext Bystander(Attitude toTarget, Attitude toVictim, bool victimInCombat = true)
        {
            var hit = Melee(Npc(), Npc());
            hit.VictimIsTarget = false;
            hit.AttackerHasTarget = true;
            hit.AttackerToTarget = toTarget;
            hit.AttackerToVictim = toVictim;
            hit.VictimInCombat = victimInCombat;
            return hit;
        }

        [TestCase(Attitude.Hostile, Attitude.Friendly, true)]
        [TestCase(Attitude.Hostile, Attitude.Neutral, true)]
        [TestCase(Attitude.Hostile, Attitude.Angry, true)]
        [TestCase(Attitude.Hostile, Attitude.Hostile, false)]
        [TestCase(Attitude.Angry, Attitude.Neutral, true)]
        [TestCase(Attitude.Neutral, Attitude.Neutral, true)]
        [TestCase(Attitude.Angry, Attitude.Angry, false)]
        [TestCase(Attitude.Neutral, Attitude.Angry, false)]
        [TestCase(Attitude.Predator, Attitude.Angry, true)]
        [TestCase(Attitude.Hostile, Attitude.Panic, false)]
        public void Bystander_VictimInCombat_SparedWhenTheAttackerLikesItBetterThanItsTarget(Attitude toTarget, Attitude toVictim, bool spared)
        {
            var hit = Bystander(toTarget, toVictim);
            Assert.That(CombatResolver.BystanderSpared(hit), Is.EqualTo(spared));
            if (spared) Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.Bystander));
        }

        [TestCase(Attitude.Hostile, Attitude.Neutral, true)]
        [TestCase(Attitude.Hostile, Attitude.Angry, true)]
        [TestCase(Attitude.Hostile, Attitude.Hostile, false)]
        [TestCase(Attitude.Neutral, Attitude.Neutral, false)]
        [TestCase(Attitude.Angry, Attitude.Friendly, true)]
        public void Bystander_VictimNotInCombat_ComparesItsOwnAttitudeWithTheTargets(Attitude targetToAttacker, Attitude victimToAttacker, bool spared)
        {
            var hit = Bystander(Attitude.Hostile, Attitude.Hostile, victimInCombat: false);
            hit.TargetToAttacker = targetToAttacker;
            hit.VictimToAttacker = victimToAttacker;
            Assert.That(CombatResolver.BystanderSpared(hit), Is.EqualTo(spared));
        }

        [Test]
        public void Bystander_NotFiltered_TargetFightsBackPlayerAreaOrNoTarget()
        {
            var hit = Bystander(Attitude.Hostile, Attitude.Neutral);
            Assert.That(CombatResolver.BystanderSpared(hit), Is.True);
            hit.VictimTargetsAttacker = true;
            Assert.That(CombatResolver.BystanderSpared(hit), Is.False, "the bystander fights the attacker");
            hit = Bystander(Attitude.Hostile, Attitude.Neutral);
            hit.VictimIsTarget = true;
            Assert.That(CombatResolver.BystanderSpared(hit), Is.False, "its own target");
            hit = Bystander(Attitude.Hostile, Attitude.Neutral);
            hit.AttackerHasTarget = false;
            Assert.That(CombatResolver.BystanderSpared(hit), Is.False, "no target: DoLogicalDamage decides");
            Assert.That(Resolver().Resolve(hit).Result, Is.EqualTo(HitResult.Hit), "a neutral non-target takes an NPC's blade");
            hit = Bystander(Attitude.Hostile, Attitude.Neutral);
            hit.Attacker = Player();
            Assert.That(CombatResolver.BystanderSpared(hit), Is.False, "the player's touches have their own rules");
            hit = Bystander(Attitude.Hostile, Attitude.Neutral);
            hit.Delivery = HitDelivery.Spell;
            hit.AreaSpell = true;
            Assert.That(CombatResolver.BystanderSpared(hit), Is.False, "area spells do not touch");
        }

        [Test]
        public void FriendlyFire_NpcSpellMissile_SparesNeutralNonTargetsLikeArrows()
        {
            // DoLogicalDamage: the projectile gate (Friendly or Neutral non-target) comes before the spell gate (Friendly)
            var hit = Melee(Npc(), Npc(), type: DamageType.Fire);
            hit.Delivery = HitDelivery.Spell;
            hit.SpellProjectile = true;
            hit.VictimIsTarget = false;
            hit.AttackerToVictim = Attitude.Neutral;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.FriendlyFire));
            hit.SpellProjectile = false;
            Assert.That(Resolver().Resolve(hit).Gate, Is.EqualTo(HitGate.None), "an instant spell spares only friends");
        }

        [Test]
        public void FireHit_Burns_IceFreezesForAtLeastFiveSeconds()
        {
            var spell = new HitContext { Attacker = Npc(strength: 50), Victim = Player(), Delivery = HitDelivery.Spell, DamageType = DamageType.Fire };
            Assert.That(Resolver().Resolve(spell).Burns, Is.True);
            spell.DamageType = DamageType.Ice;
            Assert.That(Resolver().Resolve(spell).FreezeSeconds, Is.EqualTo(5f));
            spell.Victim = Npc(species: "Troll", humanoid: false);
            Assert.That(Resolver().Resolve(spell).FreezeSeconds, Is.EqualTo(0f));
        }

        // ---------------------------------------------------------------------------------------- stats and stamina

        [Test]
        public void NpcStats_ScaleWithThePlayer()
        {
            Assert.That(CombatStats.NpcCurrentLevel(5, 30, 10, Difficulty.Medium), Is.EqualTo(15));
            Assert.That(CombatStats.NpcCurrentLevel(5, 30, 40, Difficulty.Medium), Is.EqualTo(30));
            Assert.That(CombatStats.NpcCurrentLevel(5, 30, 10, Difficulty.Hard), Is.EqualTo(25));
            Assert.That(CombatStats.NpcAttribute(15), Is.EqualTo(32));
            Assert.That(CombatStats.NpcHitPointsMax(3, false), Is.EqualTo(100));
            Assert.That(CombatStats.NpcHitPointsMax(30, false), Is.EqualTo(600));
            Assert.That(CombatStats.NpcStaminaMax(30, humanoid: true), Is.EqualTo(300));
            Assert.That(CombatStats.NpcProtection(60), Is.EqualTo(80));
        }

        [Test]
        public void MoveCosts_AbValues_AndWhirlFallback()
        {
            int sp = 100;
            Assert.That(StaminaRules.PayForMove(ref sp, CombatAction.Attack, Rules, RuleOptions.Default), Is.EqualTo(CombatAction.Attack));
            Assert.That(sp, Is.EqualTo(88));
            sp = 30;
            Assert.That(StaminaRules.PayForMove(ref sp, CombatAction.WhirlAttack, Rules, RuleOptions.Default), Is.EqualTo(CombatAction.Attack));
            Assert.That(sp, Is.EqualTo(0));
            sp = 50;
            StaminaRules.PayForMove(ref sp, CombatAction.JumpBack, Rules, RuleOptions.Default);
            Assert.That(sp, Is.EqualTo(50));
            StaminaRules.PayForMove(ref sp, CombatAction.JumpBack, Rules, Vanilla);
            Assert.That(sp, Is.EqualTo(40));
        }

        [Test]
        public void PlayerStaminaPerSecond()
        {
            Assert.That(StaminaRules.PlayerPerSecond(new PlayerStaminaState(), RuleOptions.Default), Is.EqualTo(10));
            Assert.That(StaminaRules.PlayerPerSecond(new PlayerStaminaState { Sprinting = true }, RuleOptions.Default), Is.EqualTo(-8));
            Assert.That(StaminaRules.PlayerPerSecond(new PlayerStaminaState { Sprinting = true }, Vanilla), Is.EqualTo(-10));
            Assert.That(StaminaRules.PlayerPerSecond(new PlayerStaminaState { Blocking = true }, RuleOptions.Default), Is.EqualTo(1));
            Assert.That(StaminaRules.PlayerPerSecond(new PlayerStaminaState { Blocking = true }, Vanilla), Is.EqualTo(10));
        }

        [Test]
        public void HitCooldown_HalfARealSecond_ArenaExempt()
        {
            var off = new RuleOptions(alternativeBalancing: false, extendedAI: false);
            Assert.That(HitCooldown.CanAttack(105, 100, false, Rules, RuleOptions.Default), Is.False);
            Assert.That(HitCooldown.CanAttack(106, 100, false, Rules, RuleOptions.Default), Is.True);
            Assert.That(HitCooldown.CanAttack(105, 100, true, Rules, RuleOptions.Default), Is.True);
            Assert.That(HitCooldown.CanAttack(105, 100, false, Rules, off), Is.True);
        }

        [Test]
        public void AttackPressure_AddsPerSwing_DecaysFifty()
        {
            var p = new AttackPressure();
            p.OnAttackStarted(CombatAction.Attack);
            p.OnAttackStarted(CombatAction.QuickAttack);
            Assert.That(p.Value, Is.EqualTo(175));
            p.TickSecond();
            Assert.That(p.Value, Is.EqualTo(125));
        }
    }
}
