using System.Linq;
using G3.Unity;
using Myrtana.Core;
using Myrtana.Rules;
using Myrtana.Session;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The demo build's travel kit and the world remaster switch texts (Docs/build.md).</summary>
    public class DemoBuildTests
    {
        [Test]
        public void KitGivesEachMissingStoneOnce()
        {
            var inventories = new Inventories(new EventBus());
            inventories.Give(ActorId.Player, "It_Teleport_Faring", 1);
            var given = DemoKit.Give(inventories);
            Assert.That(given, Is.EqualTo(new[] { "It_Teleport_Ardea", "It_Teleport_Hammerclan", "It_Teleport_Bakaresh" }));
            foreach (var stone in DemoKit.TeleportStones)
                Assert.That(inventories.Of(ActorId.Player).Count(stone), Is.EqualTo(1), stone);
            Assert.That(DemoKit.Give(inventories), Is.Empty);
        }

        [Test]
        public void KitSkipsStonesTheContentLacks()
        {
            var inventories = new Inventories(new EventBus());
            var given = DemoKit.Give(inventories, id => id != "It_Teleport_Hammerclan");
            Assert.That(given, Has.No.Member("It_Teleport_Hammerclan"));
            Assert.That(given.Count, Is.EqualTo(DemoKit.TeleportStones.Count - 1));
        }

        [Test]
        public void KitStonesAreTheMapTeleportItems() =>
            Assert.That(DemoKit.TeleportStones.All(s => s.StartsWith("It_Teleport_")), Is.True);

        [Test]
        public void RemasterSwitchTexts()
        {
            Assert.That(WorldRemasterToggle.Label(false, "English"), Is.EqualTo("Original"));
            Assert.That(WorldRemasterToggle.Label(true, "English"), Is.EqualTo("Remastered"));
            Assert.That(WorldRemasterToggle.Label(true, "Russian"), Is.EqualTo("Ремастер"));
            Assert.That(WorldRemasterToggle.OptionValue(true, "German"), Is.EqualTo("Remastered world"));
            Assert.That(WorldRemasterToggle.OptionValue(false, "Russian"), Is.EqualTo("Оригинал"));
        }

        [Test]
        public void RemasterLabelShowsForTwoSeconds()
        {
            Assert.That(WorldRemasterToggle.LabelVisible(-1f, 0f), Is.False);
            Assert.That(WorldRemasterToggle.LabelVisible(10f, 10f), Is.True);
            Assert.That(WorldRemasterToggle.LabelVisible(10f, 11.9f), Is.True);
            Assert.That(WorldRemasterToggle.LabelVisible(10f, 12f), Is.False);
        }
    }
}
