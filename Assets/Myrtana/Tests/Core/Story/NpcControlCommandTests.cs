using System.Collections.Generic;
using System.Linq;
using Myrtana.Core;
using Myrtana.Story;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>dialog-pres-2: the NPC control commands FullStop, CP Humanize and Wear.</summary>
    public class NpcControlCommandTests
    {
        [Test]
        public void FullStop_Humanize_Wear_ReachTheDirectors_AndWearCreatesTheItem()
        {
            var world = new RecordingWorldDirector();
            var f = new StoryFixture { WorldDirector = world }.Entry("Orders", DialogEntryKind.News, commands: new DialogCommand[]
            {
                new FullStop(ActorRef.Partner), new HumanizeActor(ActorRef.Partner, false),
                new WearItem(ActorRef.Partner, "It_1H_Wenzel_Sword_01"), new WearItem(ActorRef.Partner, "Fat_Body_NrdElite"),
            });
            var worn = new List<ActorWoreItem>();
            f.Events.Subscribe<ActorWoreItem>(worn.Add);
            f.Dialog.Talk(StoryFixture.Npc);

            Assert.That(f.Director.Calls, Does.Contain($"fullstop {StoryFixture.Npc}"));
            Assert.That(world.Calls, Does.Contain($"immortal {StoryFixture.Npc} True"), "Humanize false = immortal");
            Assert.That(world.Calls, Does.Contain($"wear {StoryFixture.Npc} It_1H_Wenzel_Sword_01"));
            var inventory = f.Items.Inventories.Of(StoryFixture.Npc);
            Assert.That(inventory.Count("It_1H_Wenzel_Sword_01"), Is.EqualTo(1), "CP creates the missing stack");
            Assert.That(inventory.Count("Fat_Body_NrdElite"), Is.Zero, "a body template is no item");
            Assert.That(worn.Select(w => (w.Item, w.IsItem)), Is.EqualTo(new[] { ("It_1H_Wenzel_Sword_01", true), ("Fat_Body_NrdElite", false) }));
        }
    }
}
