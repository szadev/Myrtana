using G3.Formats.Animation;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>NPC combat facts from the G3 world entities (gCNPC_PS, weaponry) and their use by the session.</summary>
    public class NpcCombatImportTests
    {
        static NpcSpawn Spawn(int species, int level = 0, int levelMax = 20, int type = 0, bool enclave = false, int npcClass = 0,
                              int political = 0, int attitude = 0, bool immortal = false) =>
            new NpcSpawn
            {
                Name = "Test", Species = species, Level = level, LevelMax = levelMax, NpcType = type, HasEnclave = enclave, Class = npcClass,
                Political = political, AttitudeToPlayer = attitude, Immortal = immortal,
            };

        [Test]
        public void FromSpawn_ReadsLevelsRoleClassAndFaction()
        {
            var gnar = NpcCombatImporter.FromSpawn(Spawn(species: 5, level: 15, levelMax: 35, type: 1, enclave: true, npcClass: 3, political: 1));
            Assert.That(gnar.Species, Is.EqualTo("Orc"));
            Assert.That((gnar.Level, gnar.LevelMax), Is.EqualTo((15, 35)));
            Assert.That(gnar.Humanoid && gnar.Guard && !gnar.Boss, Is.True);
            Assert.That(gnar.Class, Is.EqualTo(NpcClass.Warrior));
            Assert.That(gnar.Faction, Is.EqualTo("Orc"));
            Assert.That(gnar.AttitudeToPlayer, Is.Null);

            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, type: 2, enclave: true)).Boss, Is.True, "political NPC of an enclave");
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, type: 2)).Boss, Is.False, "no enclave");
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, type: 3)).FriendType, Is.True);
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, npcClass: 1)).Mage, Is.True);
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, npcClass: 2)).Paladin, Is.True);
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, immortal: true)).Immortal, Is.True);
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, attitude: 6)).AttitudeToPlayer, Is.EqualTo(Attitude.Panic));
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(0, attitude: 5)).AttitudeToPlayer, Is.EqualTo(Attitude.Predator));
        }

        [Test]
        public void FromSpawn_ClassifiesSpeciesLikeTheG3Scripts()
        {
            var wolf = NpcCombatImporter.FromSpawn(Spawn(10));
            Assert.That(wolf.Species, Is.EqualTo("Wolf"));
            Assert.That(wolf.Animal && !wolf.Humanoid && !wolf.GameAnimal, Is.True);
            var deer = NpcCombatImporter.FromSpawn(Spawn(29));
            Assert.That(deer.Species, Is.EqualTo("Deer"));
            Assert.That(deer.Animal && deer.GameAnimal, Is.True);
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(1)).Evil, Is.True, "Skeleton");
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(1)).Humanoid, Is.False, "IsHumanoid is humans and orcs only");
            Assert.That(NpcCombatImporter.FromSpawn(Spawn(26)).AmbientCreature, Is.True, "Meatbug");
        }

        [Test]
        public void Loadout_PicksMeleeShieldRangedAndFittingAmmo()
        {
            var archer = CombatLoadout.From(new[] { Item("It_Bow", "Bow"), Item("It_Arrow", "Arrow"), Item("It_Axe", "Axe"), Item("It_Shield", "Shield") });
            Assert.That(archer.Melee, Is.EqualTo(WeaponClass.Axe));
            Assert.That(archer.HeavyMelee && archer.Shield && archer.CanShoot, Is.True);
            Assert.That(archer.Ranged, Is.EqualTo(WeaponClass.Bow));

            var wrongAmmo = CombatLoadout.From(new[] { Item("It_Crossbow", "CrossBow"), Item("It_Arrow", "Arrow") });
            Assert.That(wrongAmmo.HasRanged && !wrongAmmo.CanShoot, Is.True, "arrows do not fit a crossbow");
            Assert.That(CombatLoadout.From(new[] { Item("It_Broom", "Broom") }).Melee, Is.EqualTo(WeaponClass.Tool));
            Assert.That(CombatLoadout.From(new ItemDef[0]).Melee, Is.EqualTo(WeaponClass.Fist));
        }

        [Test]
        public void Session_UsesTheImportedLevelAndWeaponry()
        {
            var goran = new ActorId("Goran");
            var combat = new NpcCombatDef(10, 30, "Human", humanoid: true, role: NpcRole.Guard, hasEnclave: true, faction: "Orc",
                                          equipment: new[] { "It_1H_Sword_01" });
            using (var f = new SessionFixture())
            {
                f.Add(SimulationSchema.NpcFolder, new NpcDef(goran, new TextId("FO_Goran"), "Goran", "Human", combat: combat));
                f.Add(RulesSchema.ItemFolder, Item("It_1H_Sword_01", "OneHanded"));
                var session = f.NewGame();
                session.Actors.TryGet(goran, out var actor);
                Assert.That(actor.Definition.Combat.Equipment, Is.EqualTo(new[] { "It_1H_Sword_01" }), "survives the content pack");
                Assert.That(actor.Definition.Combat.Faction, Is.EqualTo("Orc"));
                Assert.That(session.Combat.Vitals.HitPointsMax(goran), Is.EqualTo(600), "max(100, 20 · LevelMax 30)");
                Assert.That(CombatWiring.LoadoutOf(actor.Definition, session.Content.Database).Melee, Is.EqualTo(WeaponClass.OneHanded));
                Assert.That(session.NpcCombat.BrainOf(goran).Loadout.Melee, Is.EqualTo(WeaponClass.OneHanded));
            }
        }

        static ItemDef Item(string id, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, null, default);
    }
}
