using System.Linq;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-195 [owner]: shot arrows and bolts are had again (G3 CP 1.75 lets them fade unpickable).</summary>
    public class SpentAmmoTests
    {
        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, ItemTests.Item("Arrow", 1, EquipSlot.Ammo, "Arrow"), ItemTests.Item("FireArrow", 5, EquipSlot.Ammo, "Arrow"),
                 ItemTests.Item("Bolt", 1, EquipSlot.Ammo, "Bolt"), ItemTests.Item("ExplosiveArrow", 10, EquipSlot.Ammo, "Arrow"));

        static readonly float[] Here = { 1f, 2f, 3f };
        static readonly float[] Tilted = { 0.1f, 0.2f, 0.3f, 0.927f };
        static readonly ActorId Wolf = new ActorId("Wolf_01");

        [Test]
        public void Fate_ByRestAndTouch()
        {
            Assert.That(SpentAmmoRules.Fate(ProjectileRest.Vanish, false), Is.EqualTo(SpentAmmoFate.Gone));
            Assert.That(SpentAmmoRules.Fate(ProjectileRest.Vanish, true), Is.EqualTo(SpentAmmoFate.Gone), "the explosive arrow leaves nothing");
            Assert.That(SpentAmmoRules.Fate(ProjectileRest.StickToBone, true), Is.EqualTo(SpentAmmoFate.IntoVictim));
            Assert.That(SpentAmmoRules.Fate(ProjectileRest.Drop, true), Is.EqualTo(SpentAmmoFate.IntoVictim), "a blunt arrow falling off is the victim's");
            Assert.That(SpentAmmoRules.Fate(ProjectileRest.Stick, false), Is.EqualTo(SpentAmmoFate.Lies));
            Assert.That(SpentAmmoRules.Fate(ProjectileRest.Drop, false), Is.EqualTo(SpentAmmoFate.Lies), "a bolt that bounced off");
        }

        [Test]
        public void HitOnACharacter_GoesIntoItsInventory_WithTypeAndQuality()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var fate = SpentAmmo.Recover(s, ActorId.Player, "FireArrow", 2, ProjectileRest.StickToBone, Wolf, Here, null);
            Assert.That(fate, Is.EqualTo(SpentAmmoFate.IntoVictim));
            var inv = s.Items.Inventories.Of(Wolf);
            Assert.That(inv.Count("FireArrow"), Is.EqualTo(1));
            Assert.That(inv.QualityOf("FireArrow"), Is.EqualTo(2));
            Assert.That(s.Items.World.AllDropped, Is.Empty);
        }

        [Test]
        public void StuckInTheLevel_LiesInItsPose_SavedAndPickedUpIntoTheStack()
        {
            using var f = Fixture();
            var s = f.NewGame();
            s.Items.Inventories.Give(ActorId.Player, "Arrow", 9);
            Assert.That(SpentAmmo.Recover(s, ActorId.Player, "Arrow", 0, ProjectileRest.Stick, default, Here, Tilted), Is.EqualTo(SpentAmmoFate.Lies));
            var d = s.Items.World.AllDropped.Single();
            Assert.That(d.Item, Is.EqualTo("Arrow"));
            Assert.That(d.Amount, Is.EqualTo(1));
            Assert.That(d.Spent, Is.True);
            Assert.That(d.Rotation, Is.EqualTo(Tilted));

            var loaded = f.Reload(s);
            Assert.That(loaded.Items.World.TryGetDropped(d.Id, out var back), Is.True, "saved with the dropped items");
            Assert.That(back.Spent, Is.True);
            Assert.That(back.Rotation, Is.EqualTo(Tilted).Within(1e-6f));
            Assert.That(back.Position, Is.EqualTo(Here));
            Assert.That(loaded.Items.World.PickUp(d.Id), Is.True);
            Assert.That(loaded.Items.Inventories.Of(ActorId.Player).Count("Arrow"), Is.EqualTo(10), "stacks into the carried arrows");
        }

        [Test]
        public void NpcShot_BounceOffTheLevel_LiesWithoutPose_FreeToTake()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var archer = new ActorId("Orc_Archer");
            Assert.That(SpentAmmo.Recover(s, archer, "Bolt", 0, ProjectileRest.Drop, default, Here, null), Is.EqualTo(SpentAmmoFate.Lies));
            var d = s.Items.World.AllDropped.Single();
            Assert.That(d.Rotation, Is.Null);
            Assert.That(d.Owner, Is.EqualTo(archer));
            var loaded = f.Reload(s);
            Assert.That(loaded.Items.World.TryGetDropped(d.Id, out var back) && back.Rotation == null && back.Spent, Is.True);
        }

        [Test]
        public void ExplosiveArrow_LeavesNothing()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(SpentAmmo.Recover(s, ActorId.Player, "ExplosiveArrow", 0, ProjectileRest.Vanish, Wolf, Here, null), Is.EqualTo(SpentAmmoFate.Gone));
            Assert.That(s.Items.Inventories.Of(Wolf).Count("ExplosiveArrow"), Is.EqualTo(0));
            Assert.That(s.Items.World.AllDropped, Is.Empty);
        }

        [Test]
        public void TooManyLying_TheOldestSpentGoes_DroppedGearStays()
        {
            using var f = Fixture();
            var s = f.NewGame();
            var world = s.Items.World;
            var gear = world.Drop(Wolf, "Bolt", 3, 0, Here, 0f);
            string removed = null;
            world.Removed += id => removed = id;
            var first = world.DropSpent(ActorId.Player, "Arrow", 0, Here, null, maxLying: 3);
            world.DropSpent(ActorId.Player, "Arrow", 0, Here, null, maxLying: 3);
            world.DropSpent(ActorId.Player, "Arrow", 0, Here, null, maxLying: 3);
            Assert.That(removed, Is.Null);
            var fourth = world.DropSpent(ActorId.Player, "Arrow", 0, Here, null, maxLying: 3);
            Assert.That(removed, Is.EqualTo(first.Id));
            Assert.That(world.TryGetDropped(first.Id, out _), Is.False);
            Assert.That(world.TryGetDropped(fourth.Id, out _), Is.True);
            Assert.That(world.TryGetDropped(gear.Id, out _), Is.True, "only spent ammo is capped");
            Assert.That(world.AllDropped.Count(x => x.Spent), Is.EqualTo(3));
        }
    }
}
