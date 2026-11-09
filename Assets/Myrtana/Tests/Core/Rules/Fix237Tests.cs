using G3.Formats.Animation;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Simulation;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-237: the "Зомби" (species Stalker) were flat navy and never attacked. Material: the .xshmat's switch image wins over
    /// the FXA layer's numbered leftover. Combat: weaponless Stalkers / Ogres / Goblins / Demons take their G3 species
    /// weapon (Script_Game 0x1000b200), the only weapon their rigs have attack motions for.
    /// </summary>
    public class Fix237Tests
    {
        [Test]
        public void SharedMaterial_WinsOverTheNumberedSourceOfItsSwitchImage()
        {
            bool Shipped(string _) => true;
            Assert.That(ActorMaterialChoice.UseShared("G3_Monster_Stalker_Body_Diffuse_S1", "G3_Monster_Stalker_Body_Diffuse_01", Shipped), Is.True);
            Assert.That(ActorMaterialChoice.UseShared("X_Diffuse_S1", "X_Diffuse_S1", Shipped), Is.True, "same image");
            Assert.That(ActorMaterialChoice.UseShared("X_Diffuse_S1", null, Shipped), Is.True, "no layer");
            Assert.That(ActorMaterialChoice.UseShared("G3_Face_Generic_S1", "G3_Human_Head04_Diffuse_02", Shipped), Is.False, "heads keep their layers");
            Assert.That(ActorMaterialChoice.UseShared("G3_Face_Generic_S1", "G3_Human_Head04_Diffuse_02", _ => false), Is.True, "layer image missing");
            Assert.That(ActorMaterialChoice.UseShared("Babe_diffuse_S1", "Babe_diffuse_S2", Shipped), Is.False, "another switch: the layer");
            Assert.That(ActorMaterialChoice.UseShared("Head_Cloth_Diffuse_01", "Head_Cloth_Diffuse_02", Shipped), Is.False, "two numbers: the layer");
        }

        [Test]
        public void SpeciesWeapons_AsG3AssuresThem()
        {
            Assert.That(SpeciesWeaponry.NaturalWeapon("Stalker"), Is.EqualTo(("It_Axe_SpikedClub_01", WeaponQuality.Worn)));
            Assert.That(SpeciesWeaponry.NaturalWeapon("Ogre"), Is.EqualTo(("It_Axe_OgreMorningStar_01", WeaponQuality.Worn)));
            Assert.That(SpeciesWeaponry.NaturalWeapon("Goblin"), Is.EqualTo(("It_1H_Club_01", WeaponQuality.Worn)));
            Assert.That(SpeciesWeaponry.NaturalWeapon("Demon"), Is.EqualTo(("It_2H_DemonSword_01", WeaponQuality.None)));
            Assert.That(SpeciesWeaponry.NaturalWeapon("Zombie"), Is.Null, "zombies fight with the diseased fist");
            Assert.That(SpeciesWeaponry.NaturalWeapon("Wolf"), Is.Null);
        }

        [Test]
        public void Loadout_NaturalWeaponOnlyWithoutAMeleeItem()
        {
            var club = Item("It_Axe_SpikedClub_01", "Axe");
            var withClub = CombatLoadout.Fists.WithNaturalWeapon(club, WeaponQuality.Worn);
            Assert.That(withClub.MeleeItem, Is.SameAs(club));
            Assert.That(withClub.Melee, Is.EqualTo(WeaponClass.Axe));
            Assert.That(withClub.MeleeQuality, Is.EqualTo(WeaponQuality.Worn));
            Assert.That(withClub.Without(new[] { "It_Arrow" }).MeleeQuality, Is.EqualTo(WeaponQuality.Worn), "kept while the club is kept");
            Assert.That(withClub.Without(new[] { "It_Axe_SpikedClub_01" }).Melee, Is.EqualTo(WeaponClass.Fist), "looted: fists");

            var sword = CombatLoadout.From(new[] { Item("It_1H_Sword_01", "OneHanded") });
            Assert.That(sword.WithNaturalWeapon(club, WeaponQuality.Worn), Is.SameAs(sword), "holds a weapon already");
            Assert.That(CombatLoadout.Fists.WithNaturalWeapon(Item("It_Bow", "Bow"), WeaponQuality.None), Is.SameAs(CombatLoadout.Fists));
        }

        [Test]
        public void Session_WeaponlessStalkerFightsWithTheSpikedClub()
        {
            var stalker = new ActorId("Stalker_01");
            var wolf = new ActorId("Wolf_01");
            using (var f = new SessionFixture())
            {
                f.Add(SimulationSchema.NpcFolder, new NpcDef(stalker, new TextId("FO_Stalker"), "", "Stalker",
                                                             combat: new NpcCombatDef(20, 30, "Stalker", humanoid: false)));
                f.Add(SimulationSchema.NpcFolder, new NpcDef(wolf, new TextId("FO_Wolf"), "", "Wolf", combat: new NpcCombatDef(5, 10, "Wolf", humanoid: false)));
                f.Add(RulesSchema.ItemFolder, Item("It_Axe_SpikedClub_01", "Axe"));
                var session = f.NewGame();
                session.Actors.TryGet(stalker, out var s);
                session.Actors.TryGet(wolf, out var w);
                var loadout = CombatWiring.LoadoutOf(s, session.Content.Database);
                Assert.That(loadout.MeleeItem?.Id, Is.EqualTo("It_Axe_SpikedClub_01"));
                Assert.That(loadout.Melee, Is.EqualTo(WeaponClass.Axe), "the 2H field: the Stalker rig's only attack motions");
                Assert.That(session.NpcCombat.LoadoutOf(stalker).MeleeQuality, Is.EqualTo(WeaponQuality.Worn));
                Assert.That(CombatWiring.LoadoutOf(w, session.Content.Database).Melee, Is.EqualTo(WeaponClass.Fist));
            }
        }

        static ItemDef Item(string id, string kind) =>
            new ItemDef(id, new TextId("FO_" + id), default, ItemCategory.Weapon, kind, EquipSlot.MeleeWeapon, 0, 0, null, null,
                        null, null, null, null, null, null, default);
    }
}
