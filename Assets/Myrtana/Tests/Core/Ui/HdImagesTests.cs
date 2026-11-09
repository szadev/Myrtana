using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>map-hd: which pictures get a 4x copy, where it loads from, and the switch (default on, env overrides).</summary>
    public sealed class HdImagesTests
    {
        [Test]
        public void Wanted_MapsAndQuestLogMap()
        {
            Assert.IsTrue(HdImages.Wanted("hud_maps", "G3_Map_World.tga"));
            Assert.IsTrue(HdImages.Wanted("HUD_MAPS", "G3_Map_Caves.tga"));
            Assert.IsTrue(HdImages.Wanted("hud_backgrounds", "G3_HUD_View_World_Back.tga"));
            Assert.IsFalse(HdImages.Wanted("hud_backgrounds", "G3_HUD_View_Map_Back.tga"));
            Assert.IsFalse(HdImages.Wanted("hud_icons", "G3_HUD_Marker.tga"));
        }

        [Test]
        public void ResourcePath_ByKey() =>
            Assert.AreEqual("G3Gui/ImagesHD/g3_map_world", HdImages.ResourcePath("G3_Map_World.tga"));

        [Test]
        public void Resolve_DefaultOn_EnvWins()
        {
            Assert.IsTrue(HdImages.Resolve(null, null, null));
            Assert.IsFalse(HdImages.Resolve(null, null, 0));
            Assert.IsTrue(HdImages.Resolve(null, null, 1));
            Assert.IsFalse(HdImages.Resolve(null, "0", 1));
            Assert.IsTrue(HdImages.Resolve("on", "0", 0));
            Assert.IsFalse(HdImages.Resolve("off", null, null));
            Assert.IsTrue(HdImages.Resolve("junk", null, null));
        }

        [Test]
        public void SizeMatches_ExactlyFourTimes()
        {
            Assert.IsTrue(HdImages.SizeMatches(510, 510, 2040, 2040));
            Assert.IsTrue(HdImages.SizeMatches(580, 599, 2320, 2396));
            Assert.IsFalse(HdImages.SizeMatches(580, 599, 2320, 2400));
        }
    }
}
