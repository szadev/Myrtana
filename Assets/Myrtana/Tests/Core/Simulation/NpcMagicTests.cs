using System.Collections.Generic;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>NPC magic (combat_ai.json moveSelection.magic, weaponChoice mage/paladin/IceGolem, spells; Docs/combat.md §12).</summary>
    public class NpcMagicTests
    {
        static readonly CombatAiRules Ai = TestRules.CombatAi();
        static readonly ActorId Mage = new ActorId("KDF_01"), Xardas = new ActorId("Xardas");
        static readonly NpcCombatDef FireMage = new NpcCombatDef(0, 30, "Human", humanoid: true, mage: true, @class: NpcClass.Mage);
        static readonly NpcCombatDef Paladin = new NpcCombatDef(0, 30, "Human", humanoid: true, @class: NpcClass.Paladin);
        static readonly NpcCombatDef Warrior = new NpcCombatDef(0, 30, "Human", humanoid: true, @class: NpcClass.Warrior);
        static readonly NpcCombatDef GoblinShaman = new NpcCombatDef(0, 20, "Goblin", humanoid: false, mage: true, @class: NpcClass.Mage);
        static readonly CombatLoadout Staff = CombatLoadout.From(new[]
        {
            new ItemDef("It_Staff_Firemage_01", new TextId("x"), default, ItemCategory.Weapon, "Staff", EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, null, default),
        });

        static readonly Dictionary<string, SpellDef> Spells = new Dictionary<string, SpellDef>
        {
            ["It_Spell_Fireball"] = Spell("Spl_Fireball", SpellTarget.Focus, SpellCastType.Power),
            ["It_Spell_Icelance"] = Spell("Spl_Icelance", SpellTarget.Focus, SpellCastType.Power),
            ["It_Spell_Healing"] = Spell("Spl_Cure", SpellTarget.Caster, SpellCastType.Regular),
            ["It_Spell_ArmyOfDarkness"] = Spell("Spl_SummonArmyOfDarkness", SpellTarget.Caster, SpellCastType.Regular),
        };

        static SpellDef Spell(string id, SpellTarget target, SpellCastType type) =>
            new SpellDef(id, null, null, type, target, SpellKind.Projectile, SpellDeity.Innos, 15, 30, SpellDelivery.Projectile, null, null, 0,
                         null, null, null);

        static NpcCombatBrain Brain(NpcCombatDef def, IRandom random = null, ActorId? self = null, RuleOptions options = null)
        {
            options = options ?? RuleOptions.Default;
            var brain = new NpcCombatBrain(self ?? Mage, def, Staff, Ai, TestRules.Combat(), options, random ?? new SeededRandom(5),
                                           new CombatCoordinator(Ai.Token, options))
            {
                Spells = item => item != null && Spells.TryGetValue(item, out var s) ? s : null,
            };
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            return brain;
        }

        static CombatSituation Situation(float distance, double now = 1, int hp = 100, bool freeBack = true) => new CombatSituation
        {
            Now = now, GameNow = now * 12, Distance = distance, LineOfSight = true, FreeBack = freeBack, FreeBackLeft = true, FreeBackRight = true,
            TargetIsPlayer = true, TargetState = VitalState.Alive, TargetKillable = true, TargetHumanoid = true, TargetSpecies = "Human",
            TargetHoldsMelee = true, TargetEngagedWithSelf = true, AttitudeToTarget = Attitude.Hostile, PlayerLevel = 10, HitPointsPercent = hp,
        };

        [Test]
        public void Mage_MeleeWhenThePlayerIsClose_MagicBeyond_CastsAFocusSpellAtOnce()
        {
            var brain = Brain(FireMage);
            Assert.That(brain.CanCast, Is.True);
            Assert.That(brain.Tick(Situation(200)).Weapon, Is.EqualTo(WeaponMode.Melee), "player within 250");
            var far = Brain(FireMage);
            var order = far.Tick(Situation(1500));
            Assert.That(order.Weapon, Is.EqualTo(WeaponMode.Magic));
            Assert.That(order.Spell, Is.EqualTo("It_Spell_Fireball").Or.EqualTo("It_Spell_Icelance"));
            Assert.That(order.Move, Is.EqualTo(CombatMove.Cast).Or.EqualTo(CombatMove.Fwd).Or.EqualTo(CombatMove.Back), "kiting or a first cast");
        }

        [Test]
        public void Mage_PlayerBetween250And400_MeleeUnlessASummonerBossCanSummon()
        {
            Assert.That(Brain(FireMage).Tick(Situation(300)).Weapon, Is.EqualTo(WeaponMode.Melee));
            var xardas = Brain(FireMage, self: Xardas);
            Assert.That(xardas.Tick(Situation(300)).Weapon, Is.EqualTo(WeaponMode.Magic));
            Assert.That(xardas.State.Spell, Is.EqualTo("It_Spell_ArmyOfDarkness"));
            Assert.That(xardas.State.Move, Is.EqualTo(CombatMove.Cast), "a caster spell is cast at once");
            var withSummons = Situation(300);
            withSummons.HasSummons = true;
            Assert.That(Brain(FireMage, self: Xardas).Tick(withSummons).Weapon, Is.EqualTo(WeaponMode.Melee));
        }

        [Test]
        public void FocusSpell_TooClose_BacksOff_ThenCasts_RepeatedSpellsWaitSometimes()
        {
            // too close (300 vs an NPC-free player at 300 would be melee; use a non-humanoid target)
            var s = Situation(300);
            s.TargetHumanoid = false;
            s.TargetIsPlayer = false;
            var backs = Brain(FireMage, new ScriptedRandom(0, 0, 0)); // spell pick, back roll 0 < 80
            Assert.That(backs.Tick(s).Move, Is.EqualTo(CombatMove.Back));
            var casts = Brain(FireMage, new ScriptedRandom(0, 95, 0)); // back roll 95 fails, hit cooldown ready: cast
            Assert.That(casts.Tick(s).Move, Is.EqualTo(CombatMove.Cast));
            Assert.That(casts.State.LastSpell, Is.EqualTo(casts.State.Spell));
        }

        [Test]
        public void HurtCaster_HealsWithHalfChance_AndPaladinsCastOnlyFromAfar()
        {
            var hurt = Brain(FireMage, new ScriptedRandom(10));
            var order = hurt.Tick(Situation(1500, hp: 30));
            Assert.That(order.Spell, Is.EqualTo("It_Spell_Healing"));
            Assert.That(order.Move, Is.EqualTo(CombatMove.Cast));

            Assert.That(Brain(Paladin).Tick(Situation(600)).Weapon, Is.EqualTo(WeaponMode.Melee));
            Assert.That(Brain(Paladin).Tick(Situation(1500)).Weapon, Is.EqualTo(WeaponMode.Magic));
            Assert.That(Brain(Warrior).CanCast, Is.False);
            Assert.That(Brain(Warrior).Tick(Situation(1500)).Weapon, Is.EqualTo(WeaponMode.Melee), "no bow: melee");
        }

        [Test]
        public void MonsterCasters_UseTheirOwnSpell_PowerCastSometimes()
        {
            var s = Situation(1500);
            s.TargetHumanoid = false;
            var shaman = Brain(GoblinShaman, new ScriptedRandom(0, 99, 99, 99, 10));
            var order = shaman.Tick(s);
            Assert.That(order.Spell, Is.EqualTo("It_Spell_Fireball"));
            if (order.Move == CombatMove.Cast) Assert.That(order.PowerCast, Is.True, "roll 10 < 20 %");
        }

        [Test]
        public void NoMagicContent_NobodyCasts()
        {
            var brain = new NpcCombatBrain(Mage, FireMage, Staff, Ai, TestRules.Combat(), RuleOptions.Default, new SeededRandom(1),
                                           new CombatCoordinator(Ai.Token, RuleOptions.Default));
            brain.Engage(ActorId.Player, AttackReason.Enemy, 0);
            Assert.That(brain.Tick(Situation(1500)).Weapon, Is.EqualTo(WeaponMode.Melee));
        }

        sealed class ScriptedRandom : IRandom
        {
            readonly Queue<int> _ints;
            public ScriptedRandom(params int[] ints) => _ints = new Queue<int>(ints);
            public int Next(int maxExclusive) => (_ints.Count > 0 ? _ints.Dequeue() : 0) % maxExclusive;
            public float NextFloat() => 0.5f;
            public RandomState Save() => default;
            public void Load(RandomState state) { }
        }
    }
}
