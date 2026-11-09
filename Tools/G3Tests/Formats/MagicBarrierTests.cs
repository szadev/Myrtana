using System;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Material;
using G3.Formats.Pak;
using G3.Formats.World;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-229: the magic barrier dome over Vengard (Docs/formats-world.md §7.6). Rule tests run everywhere; the data tests
    /// read the installed game (G3_DATA or the Steam path) and are ignored when it is absent.
    /// </summary>
    [TestFixture]
    public class MagicBarrierTests
    {
        static string Data()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "Projects_compiled.pak"))) Assert.Ignore("Gothic 3 data not installed");
            return data;
        }

        // ---- visibility rule -------------------------------------------------------------------------------------------

        [Test]
        public void Shown_follows_only_the_entity_flags()
        {
            Assert.That(MagicBarrier.Shown(true, true), Is.True);
            Assert.That(MagicBarrier.Shown(false, true), Is.False);
            Assert.That(MagicBarrier.Shown(true, false), Is.False);
            Assert.That(MagicBarrier.Shown((WorldEntity)null), Is.False);
            Assert.That(MagicBarrier.Shown(new WorldEntity { Enabled = true, RenderingEnabled = true }), Is.True);
        }

        [Test]
        public void Barrier_material_names()
        {
            Assert.That(MagicBarrier.IsBarrierMaterial("G3_Special_Magic_Barrier_01"), Is.True);
            Assert.That(MagicBarrier.IsBarrierMaterial("g3_special_magic_barrier_01"), Is.True);
            Assert.That(MagicBarrier.IsBarrierMaterial("G3_Weapons_Shields_01_A"), Is.False);
            Assert.That(MagicBarrier.IsBarrierMaterial("G3_Special_Window_Glass_01"), Is.False);
            Assert.That(MagicBarrier.IsBarrierMaterial(null), Is.False);
            Assert.That(MagicBarrier.IsBarrierMaterial(""), Is.False);
        }

        // ---- game data ---------------------------------------------------------------------------------------------------

        [Test]
        public void Material_graph_has_glow_swirls_and_brighten()
        {
            using var fs = new G3FileSystem(Data(), "_compiledMaterial");
            var e = fs.GetByName(MagicBarrier.MaterialName + ".xshmat");
            Assert.That(e, Is.Not.Null);
            var m = Xshmat.Read(e.Read(), MagicBarrier.MaterialName);
            Assert.That(m.Blend, Is.EqualTo(BlendMode.Brighten));
            Assert.That(m.SelfIllumination?.Image, Is.EqualTo("G3_Special_Magic_Barrier_05"));
            Assert.That(m.Diffuse?.Image, Is.EqualTo("G3_Special_Magic_Barrier_04"));
            Assert.That(m.Opacity, Is.Not.Null);
            Assert.That(MagicBarrier.IsBarrierMaterial(m.Name), Is.True);
        }

        [Test]
        public void Vengard_barrier_entity_is_on_and_in_the_static_scene()
        {
            using var fs = new G3FileSystem(Data(), "Projects_compiled");
            var files = fs.Files.Where(f => f.Path.IndexOf("SysDyn_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                            f.Path.EndsWith(".lrentdat", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.That(files, Is.Not.Empty);
            WorldEntity barrier = null;
            System.Collections.Generic.List<WorldEntity> archive = null;
            foreach (var f in files)
            {
                var list = EntityArchive.Read(f.Read(), decodeVegetation: false, keepRaw: true);
                var b = list?.FirstOrDefault(x => x.Name == MagicBarrier.VengardEntity);
                if (b != null) { barrier = b; archive = list; break; }
            }
            Assert.That(barrier, Is.Not.Null, "barrier entity in SysDyn");
            Assert.That(barrier.RawPropertySets.Any(p => p.ClassName == MagicBarrier.PropertySet));
            Assert.That(barrier.Resource, Is.EqualTo(MagicBarrier.VengardEntity + ".xcmsh").IgnoreCase);
            Assert.That(MagicBarrier.Shown(barrier), Is.True, "on from the start");
            Assert.That(DynamicObjects.IsStaticMesh(barrier, archive), Is.True, "placed by the world importer");
            // a 360 m sphere around the city centre (Unity ~965, 335)
            Assert.That((barrier.Bounds[3] - barrier.Bounds[0]) / 100f, Is.EqualTo(358.3f).Within(1f));
            Assert.That(barrier.X / 100f, Is.EqualTo(965f).Within(1f));
            Assert.That(barrier.Z / 100f, Is.EqualTo(334.9f).Within(1f));
        }

        [Test]
        public void No_story_data_switches_the_barrier()
        {
            string data = Data();
            int scanned = 0;
            foreach (var vol in new[] { "Infos", "Quests" })
            {
                if (!File.Exists(Path.Combine(data, vol + ".pak"))) continue;
                using var fs = new G3FileSystem(data, vol);
                foreach (var f in fs.Files)
                {
                    var text = Encoding.Latin1.GetString(f.Read());
                    scanned++;
                    Assert.That(text.IndexOf("Magic_Barrier", StringComparison.OrdinalIgnoreCase), Is.LessThan(0), f.Path);
                    Assert.That(text.IndexOf(MagicBarrier.PropertySet, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), f.Path);
                }
            }
            if (scanned == 0) Assert.Ignore("no Infos/Quests volumes");
        }
    }
}
