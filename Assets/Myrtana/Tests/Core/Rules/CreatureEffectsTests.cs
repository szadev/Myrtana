using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-127: creatures' special hits as in CP 1.75 (the species fist table 0x1000b200, AssessHit's status order, CanBurn,
    /// CanBePoisoned, CanBeDiseased, CanFreeze), the antidote potion (PS_MagicPotion) and the hero's immunities.
    /// </summary>
    public class CreatureEffectsTests
    {
        static readonly ActorId Bloodfly = new ActorId("Bloodfly_fix127");

        [TestCase("Bloodfly", WeaponQuality.Poisoned)]
        [TestCase("SwampLurker", WeaponQuality.Poisoned)]
        [TestCase("ScorpionKing", WeaponQuality.Poisoned)]
        [TestCase("Zombie", WeaponQuality.Diseased)]
        [TestCase("Ripper", WeaponQuality.Diseased)]
        [TestCase("Varan", WeaponQuality.Diseased)]
        [TestCase("Troll", WeaponQuality.Diseased)]
        [TestCase("FireVaran", WeaponQuality.Burning)]
        [TestCase("FireGolem", WeaponQuality.Burning)]
        [TestCase("IceGolem", WeaponQuality.Frozen)]
        [TestCase("Wolf", WeaponQuality.None)]
        [TestCase("Snapper", WeaponQuality.None)]
        [TestCase("Shadowbeast", WeaponQuality.None)]
        [TestCase("Human", WeaponQuality.None)]
        [TestCase(null, WeaponQuality.None)]
        public void SpeciesFist_CarriesG3sQuality(string species, WeaponQuality quality) =>
            Assert.That(SpeciesWeaponry.FistQuality(species), Is.EqualTo(quality));

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder,
                 ItemTests.Item("It_Potion_CurePoison", 200, kind: "Potion"),
                 ItemTests.Item("It_Potion_CureDisease", 200, kind: "Potion"),
                 ItemTests.Item("It_Potion_Health", 50, kind: "Potion", use: new[] { new AttributeGain("HP", ModifierOp.AddToVal, 10) }))
            .Add(RulesSchema.SpellFolder, Cure("Spl_CurePoison", "MagicCurePoison", "It_Potion_CurePoison"),
                 Cure("Spl_CureDisease", "MagicCureDisease", "It_Potion_CureDisease"));

        static SpellDef Cure(string id, string effect, string potion) =>
            new SpellDef(id, new[] { potion, "It_Spell_" + id.Substring(4) }, null, SpellCastType.Regular, SpellTarget.Caster, SpellKind.Target,
                         SpellDeity.Adanos, 20, 20, SpellDelivery.Instant, effect, null, 0, null, null, null);

        /// <summary>A creature's melee blow on the hero: its natural weapon (no item), impact damage, the hero is its target.</summary>
        static HitContext Sting(GameSession s, string species, int damage = 5, params string[] heroPerks)
        {
            var attacker = s.Combat.NpcCombatant(Bloodfly, new NpcCombatDef(level: 1, levelMax: 3, species, humanoid: false), s.Hero.Level);
            attacker.Weapon = WeaponClass.Fist;
            attacker.Action = CombatAction.Attack;
            var hero = s.Combat.PlayerCombatant(s.Hero);
            foreach (var perk in heroPerks) hero.Perks.Add(perk);
            hero.MeleeConfig = true;
            return new HitContext
            {
                Attacker = attacker, Victim = hero, Delivery = HitDelivery.Melee, DamageType = DamageType.Impact, DamageAmount = damage,
                Quality = SpeciesWeaponry.FistQuality(species), AttackerSeesVictim = true, VictimSeesAttacker = true, VictimIsTarget = true,
                AttackerTargetsVictim = true, AttackerToVictim = Attitude.Hostile, VictimToAttacker = Attitude.Hostile, PlayerLevel = s.Hero.Level,
            };
        }

        [Test]
        public void HornetSting_PoisonsTheHeroEveryTime_5HpASecondFor30s_ANewStingRestartsNotStacks()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var status = s.Magic.Status;
            s.Hero.Stats.Apply("HP", ModifierOp.SetVal, s.Combat.Vitals.HitPointsMax(ActorId.Player));
            var outcome = s.Combat.Strike(Sting(s, "Bloodfly"), byPlayerParty: false);
            Assert.That(outcome.Result, Is.EqualTo(HitResult.Hit));
            Assert.That(outcome.Poisons, Is.True, "the bloodfly's fist has the poison quality, no chance roll");
            Assert.That(status.IsPoisoned(ActorId.Player), Is.True);
            Assert.That(status.PoisonSecondsLeft(ActorId.Player), Is.EqualTo(StatusEffects.PlayerPoisonSeconds));

            int hp = s.Combat.Vitals.HitPoints(ActorId.Player);
            for (int i = 0; i < 10; i++) status.Tick(1f);
            Assert.That(hp - s.Combat.Vitals.HitPoints(ActorId.Player), Is.EqualTo(50), "10 s x 5 HP");

            s.Combat.Strike(Sting(s, "Bloodfly"), byPlayerParty: false);
            Assert.That(status.PoisonSecondsLeft(ActorId.Player), Is.EqualTo(30f), "a new sting starts the 30 s again, one dose");
            hp = s.Combat.Vitals.HitPoints(ActorId.Player);
            status.Tick(1f);
            Assert.That(hp - s.Combat.Vitals.HitPoints(ActorId.Player), Is.EqualTo(5), "doses do not stack");
        }

        [Test]
        public void PlainCreatures_DoNotPoison_DiseaseAndFlamesComeFromTheirFists()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var wolf = s.Combat.Resolver.Resolve(Sting(s, "Wolf"));
            Assert.That(wolf.Poisons || wolf.Diseases || wolf.Burns || wolf.FreezeSeconds > 0, Is.False);
            Assert.That(s.Combat.Resolver.Resolve(Sting(s, "Zombie")).Diseases, Is.True);
            Assert.That(s.Combat.Resolver.Resolve(Sting(s, "SwampLurker")).Poisons, Is.True);
            // CanBurn / CanFreeze: a quality weapon only when the blow leaves the victim under 100 / 50 HP
            var heat = Sting(s, "FireVaran");
            heat.Victim.HitPoints = 1000;
            Assert.That(s.Combat.Resolver.Resolve(heat).Burns, Is.False, "far from death: no flames");
            heat.Victim.HitPoints = 60;
            Assert.That(s.Combat.Resolver.Resolve(heat).Burns, Is.True);
            var frost = Sting(s, "IceGolem");
            frost.Victim.HitPoints = 40;
            Assert.That(s.Combat.Resolver.Resolve(frost).FreezeSeconds, Is.EqualTo(5f), "max(5, damage/20)");
        }

        [Test]
        public void HeroPerks_ProtectFromTheCreatureEffects()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Combat.Resolver.Resolve(Sting(s, "Bloodfly", 5, CombatStats.ImmuneToPoisonPerk)).Poisons, Is.False);
            Assert.That(s.Combat.Resolver.Resolve(Sting(s, "Troll", 5, CombatStats.ImmuneToDiseasePerk)).Diseases, Is.False);
            var heat = Sting(s, "FireGolem", 5, CombatStats.ResistHeatPerk);
            heat.Victim.HitPoints = 60;
            Assert.That(s.Combat.Resolver.Resolve(heat).Burns, Is.False, "Perk_ResistHeat");
            var frost = Sting(s, "IceGolem", 5, CombatStats.ResistColdPerk);
            frost.Victim.HitPoints = 40;
            Assert.That(s.Combat.Resolver.Resolve(frost).FreezeSeconds, Is.EqualTo(0f), "Perk_ResistCold");
        }

        [Test]
        public void Antidote_CuresThePoison_ThePotionAgainstDiseases_TheDisease()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var status = s.Magic.Status;
            var bag = s.Items.Inventories.Of(ActorId.Player);
            Assert.That(s.Items.Use.CanUse("It_Potion_CurePoison"), Is.False, "not carried");
            bag.Add("It_Potion_CurePoison", 2);
            bag.Add("It_Potion_CureDisease", 1);
            s.Combat.Strike(Sting(s, "Bloodfly"), byPlayerParty: false);
            Assert.That(status.IsPoisoned(ActorId.Player), Is.True);
            int mana = s.Combat.Vitals.Mana(ActorId.Player);
            Assert.That(s.Items.Use.CanUse("It_Potion_CurePoison"), Is.True, "PS_MagicPotion: the potion's spell is its effect");
            Assert.That(s.Items.Use.Use("It_Potion_CurePoison"), Is.True);
            Assert.That(status.IsPoisoned(ActorId.Player), Is.False);
            Assert.That(bag.Count("It_Potion_CurePoison"), Is.EqualTo(1), "one potion used up");
            Assert.That(s.Combat.Vitals.Mana(ActorId.Player), Is.EqualTo(mana), "a potion costs no mana");

            s.Combat.Strike(Sting(s, "Zombie"), byPlayerParty: false);
            Assert.That(status.Has(ActorId.Player, ActorStatus.Diseased), Is.True);
            s.Items.Use.Use("It_Potion_CureDisease");
            Assert.That(status.Has(ActorId.Player, ActorStatus.Diseased), Is.False);
            Assert.That(bag.Count("It_Potion_CureDisease"), Is.EqualTo(0));
            Assert.That(s.Magic.CastsWhenDrunk("It_Potion_Health"), Is.False, "a plain potion casts nothing");
        }

        [Test]
        public void AssessHitOrder_WeakShotsAndSprintersCatchNothing_PierceStopsDiseaseButNotPoison()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var hit = Sting(s, "Bloodfly");
            hit.Delivery = HitDelivery.Missile;
            hit.HitMultiplier = 0.5f; // D = 0: PipiStumble
            hit.Quality = WeaponQuality.Poisoned;
            Assert.That(s.Combat.Resolver.Resolve(hit).Poisons, Is.False, "a weak draw only makes him flinch");
            hit.HitMultiplier = 1f;
            Assert.That(s.Combat.Resolver.Resolve(hit).Poisons, Is.True);

            hit = Sting(s, "Bloodfly");
            hit.Victim.Action = CombatAction.SprintAttack;
            Assert.That(s.Combat.Resolver.Resolve(hit).Poisons, Is.False, "a sprint attack shrugs the blow off");

            // a diseased blade disease by any blow, but not after the pierce stumble of a humanoid; poison comes first
            var npc = s.Combat.NpcCombatant(SessionFixture.Npc, new NpcCombatDef(5, 10, "Human", humanoid: true), s.Hero.Level);
            npc.Weapon = WeaponClass.OneHanded;
            npc.Action = CombatAction.Attack;
            hit = Sting(s, "Human");
            hit.Attacker = npc;
            hit.DamageType = DamageType.Blade;
            hit.Quality = WeaponQuality.Diseased | WeaponQuality.Poisoned;
            var o = s.Combat.Resolver.Resolve(hit);
            Assert.That((o.Diseases, o.Poisons), Is.EqualTo((true, false)), "a plain sword swing: disease yes, poison no");
            npc.Action = CombatAction.PierceAttack;
            o = s.Combat.Resolver.Resolve(hit);
            Assert.That(o.Reaction, Is.EqualTo(HitReaction.PierceStumble));
            Assert.That((o.Diseases, o.Poisons), Is.EqualTo((false, true)));
        }

        [Test]
        public void FireArrows_SetTheVictimOnFire_FromHalfADraw()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var hit = Sting(s, "Human");
            hit.Delivery = HitDelivery.Missile;
            hit.DamageType = DamageType.Missile;
            hit.Quality = WeaponQuality.Burning;
            hit.HitMultiplier = 0.49f;
            Assert.That(s.Combat.Resolver.Resolve(hit).Burns, Is.False);
            hit.HitMultiplier = 0.5f;
            Assert.That(s.Combat.Resolver.Resolve(hit).Burns, Is.True, "CanBurn: burning missile, hit multiplier >= 0.5");
        }
    }
}
