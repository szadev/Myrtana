using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-230 (owner: "in the original, not-yet-opened chests are highlighted orange"): the focus-name colour of objects
    /// as G3 CP 1.75 gCGUIEntityRenderer::GetAttitude (Game.dll 0x1007a0c0) picks it.
    /// </summary>
    public sealed class ObjectFocusColourTests
    {
        static ItemDef Item(string id, ItemCategory category) =>
            new ItemDef(id, new TextId("FO_" + id), default, category, "None", EquipSlot.None, 0, 0, null, null,
                        null, null, null, null, null, null, ItemFlags.None);

        static InteractiveObjectDef Obj(string id, InteractiveKind kind, string useType, LockDef @lock, ContainerDef container = null,
                                        DoorDef door = null) =>
            new InteractiveObjectDef(id, "G3_Object_" + id, kind, useType, id + ".FXA", null, null, null, 1, default, @lock, container, door, null);

        static readonly LockDef Open = new LockDef(false, -1, null, 1), Shut = new LockDef(true, 1, null, 1);

        static SessionFixture Fixture() => new SessionFixture()
            .Add(RulesSchema.ItemFolder, Item("It_Gold", ItemCategory.Misc), Item("It_Scroll_Fireball", ItemCategory.Spellbook))
            .Add(RulesSchema.InteractiveObjectFolder,
                 Obj("chest", InteractiveKind.Chest, "Chest", Open, new ContainerDef(null, new[] { new TreasureStack("It_Gold", 5, 0) })),
                 Obj("empty", InteractiveKind.Chest, "Chest", Open, new ContainerDef(null, null)),
                 Obj("spells", InteractiveKind.Chest, "Chest", Open, new ContainerDef(null, new[] { new TreasureStack("It_Scroll_Fireball", 1, 0) })),
                 Obj("locked", InteractiveKind.Chest, "Chest", Shut, new ContainerDef(null, new[] { new TreasureStack("It_Gold", 5, 0) })),
                 Obj("nolock", InteractiveKind.Chest, "Chest", null, new ContainerDef(null, new[] { new TreasureStack("It_Gold", 5, 0) })),
                 Obj("door", InteractiveKind.Door, "Door", Open, door: new DoorDef(true, null, null)),
                 Obj("gate", InteractiveKind.Door, "Door", Shut, door: new DoorDef(true, null, null)),
                 Obj("bed", InteractiveKind.Bed, "Bed", null));

        static FocusRelation Of(GameSession s, string id) =>
            HudFocus.ObjectRelation(s, s.Interaction.Objects.TryGet(id, out var def) ? def : null);

        [Test]
        public void UnopenedChest_IsUnlooted_UntilEmptied()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(Of(s, "chest"), Is.EqualTo(FocusRelation.Unlooted), "plunder not generated yet");
            Assert.That(Of(s, "empty"), Is.EqualTo(FocusRelation.Unlooted), "an empty chest is orange too until it was opened");
            Assert.That(s.Items.Inventories.Holds(InteractiveObjects.ContainerOf("chest")), Is.False, "the colour adds no inventory");
            Assert.That(s.Interaction.Objects.OpenContainer("chest") && s.Interaction.Objects.OpenContainer("empty"), Is.True);
            Assert.That(Of(s, "chest"), Is.EqualTo(FocusRelation.Unlooted), "opened, still holds gold");
            Assert.That(Of(s, "empty"), Is.EqualTo(FocusRelation.Neutral), "opened and empty: tan");
            Assert.That(s.Interaction.Objects.TakeAll("chest"), Is.EqualTo(1));
            Assert.That(Of(s, "chest"), Is.EqualTo(FocusRelation.Neutral), "looted: tan");
            s.Items.Inventories.Give(ActorId.Player, "It_Gold", 1);
            Assert.That(s.Interaction.Objects.Put("chest", "It_Gold", 1), Is.EqualTo(1));
            Assert.That(Of(s, "chest"), Is.EqualTo(FocusRelation.Unlooted), "an item put back makes it orange again");
        }

        [Test]
        public void Spellbooks_DoNotCount()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(s.Interaction.Objects.OpenContainer("spells"), Is.True);
            Assert.That(Of(s, "spells"), Is.EqualTo(FocusRelation.Neutral), "G3 skips stacks of category None (0) and Spellbook (9)");
        }

        [Test]
        public void Locks_AndObjectsWithoutLock()
        {
            using var f = Fixture();
            var s = f.NewGame();
            Assert.That(Of(s, "locked"), Is.EqualTo(FocusRelation.Locked), "a shut lock is red before the chest rule");
            Assert.That(Of(s, "gate"), Is.EqualTo(FocusRelation.Locked));
            Assert.That(Of(s, "door"), Is.EqualTo(FocusRelation.Neutral), "open lock, not a chest: tan");
            Assert.That(Of(s, "nolock"), Is.EqualTo(FocusRelation.Item), "no gCLock_PS: the item colour, even on a chest");
            Assert.That(Of(s, "bed"), Is.EqualTo(FocusRelation.Item));
            Assert.That(HudFocus.ObjectRelation(s, null), Is.EqualTo(FocusRelation.Item));
        }

        [Test]
        public void Palette_IsG3s()
        {
            // G3 renderer attitude 8 (panic, chest with loot) bright orange, 4 (hostile, shut lock) red, 1 tan, 0 cream
            Assert.That(FocusNameColors.Of(FocusAttitude.Panic), Is.EqualTo(new SkinColor(0xFF, 0x6A, 0x22)));
            Assert.That(FocusNameColors.Of(FocusAttitude.Hostile), Is.EqualTo(new SkinColor(0x9F, 0x18, 0x13)));
            Assert.That(FocusNameColors.Of(FocusAttitude.Neutral), Is.EqualTo(new SkinColor(0xE0, 0xC4, 0x95)));
            Assert.That(FocusNameColors.Of(FocusAttitude.Item), Is.EqualTo(new SkinColor(0xFF, 0xFA, 0xC8)));
        }
    }
}
