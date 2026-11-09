using System;
using System.IO;
using System.Linq;
using G3.Formats.Material;
using G3.Formats.Pak;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// fix-260: the SelfIllumination slot as unlit glow. The fire golem's rock diffuse is near black (mean 0.09); its look is
    /// the lava added by SelfIllumination = Blend(lava, rock diffuse, rock diffuse.a), which the import dropped. Game-data
    /// tests read the installed _compiledMaterial volume (G3_DATA or the Steam path) and are ignored when it is absent.
    /// </summary>
    [TestFixture]
    public class SelfIlluminationTests
    {
        static string Data()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "_compiledMaterial.pak"))) Assert.Ignore("Gothic 3 data not installed");
            return data;
        }

        static MaterialResource Load(string stem)
        {
            using var fs = new G3FileSystem(Data(), "_compiledMaterial");
            var e = fs.GetByName(stem + ".xshmat");
            Assert.That(e, Is.Not.Null, stem);
            return Xshmat.Read(e.Read(), stem);
        }

        [TestCase("G3_Animation_Monster_FireGolem_01_A")]
        [TestCase("G3_Animation_Monster_FireGolem_01_B")]
        public void Fire_golem_glows_with_lava_where_the_rock_alpha_is_low(string stem)
        {
            var m = Load(stem);
            var e = SelfIlluminationMap.ForUnlitEmission(m);
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Glow.Image, Is.EqualTo("G3_Monster_FireGolem_Lava_A1"));
            Assert.That(e.Base.Image, Is.EqualTo("G3_Monster_Golem_Diffuse_S1"));
            Assert.That(e.Weight.Image, Is.EqualTo("G3_Monster_Golem_Diffuse_S1"));
            Assert.That(e.WeightChannel, Is.EqualTo(ColorComponent.A));
            Assert.That(e.IsComposite);
            Assert.That(e.Slot.Bake, Is.SameAs(e));
            Assert.That(m.Diffuse.Image, Is.EqualTo("G3_Monster_Golem_Diffuse_S1"));
        }

        [TestCase("G3_Animation_Monster_IceGolem_01_A")]          // Mul(mask, cube sampler): reflection
        [TestCase("G3_Animation_Monster_Minecrawler_01")]         // Mul(specular, cube sampler)
        [TestCase("G3_Animation_Body_KingGuard_01_A")]            // Mul(cube sampler, specular)
        [TestCase("G3_Animation_Monster_StoneGolem_01_B")]        // Mul(T, T) with a mask the game does not ship
        [TestCase("G3_Animation_Monster_StoneGolem_01_A")]        // no SelfIllumination
        public void Reflections_and_unsupported_graphs_do_not_glow(string stem) =>
            Assert.That(SelfIlluminationMap.ForUnlitEmission(Load(stem)), Is.Null);

        [Test]
        public void World_materials_keep_their_look()
        {
            var snow = Load("G3_Nature_Ground_Snow_01_B");
            Assert.That(snow.Emission, Is.Not.Null);          // parsed …
            Assert.That(SelfIlluminationMap.ForUnlitEmission(snow), Is.Null);   // … but not applied outside actors
            Assert.That(SelfIlluminationMap.ForUnlitEmission(Load("G3_Special_Magic_Barrier_01")), Is.Null);
        }

        [Test]
        public void Only_the_fire_golem_glows_among_actor_materials()
        {
            using var fs = new G3FileSystem(Data(), "_compiledMaterial");
            var glowing = fs.Files.Where(f => f.Path.EndsWith(".xshmat", StringComparison.OrdinalIgnoreCase) &&
                                              Path.GetFileName(f.Path).StartsWith("G3_Animation_", StringComparison.OrdinalIgnoreCase))
                .Select(f => Xshmat.Read(f.Read(), Path.GetFileNameWithoutExtension(f.Path)))
                .Where(m => SelfIlluminationMap.ForUnlitEmission(m) != null).Select(m => m.Name).OrderBy(n => n).ToArray();
            Assert.That(glowing, Is.EqualTo(new[] { "G3_Animation_Monster_FireGolem_01_A", "G3_Animation_Monster_FireGolem_01_B" }));
        }

        static SelfIlluminationMap.Rgba Solid(byte r, byte g, byte b, byte a, int size = 2) =>
            new SelfIlluminationMap.Rgba(Enumerable.Range(0, size * size).SelectMany(_ => new[] { r, g, b, a }).ToArray(), size, size);

        [Test]
        public void Bake_lerps_glow_to_base_by_the_weight_channel()
        {
            var e = new SelfIlluminationMap
            {
                Glow = new TextureSlot { Image = "lava" }, Base = new TextureSlot { Image = "rock" }, Weight = new TextureSlot { Image = "rock" },
            };
            var lava = Solid(200, 40, 20, 255);
            Assert.That(e.Bake(lava, Solid(10, 10, 10, 0), Solid(10, 10, 10, 0), 2, 2).Take(4), Is.EqualTo(new byte[] { 200, 40, 20, 255 }));
            Assert.That(e.Bake(lava, Solid(10, 10, 10, 255), Solid(10, 10, 10, 255), 2, 2).Take(4), Is.EqualTo(new byte[] { 10, 10, 10, 255 }));
            var half = e.Bake(lava, Solid(0, 0, 0, 0), Solid(0, 0, 0, 0, 4).WithAlpha(128), 4, 4);
            Assert.That(half[0], Is.EqualTo(100).Within(1));
            Assert.That(e.BakedStem, Does.EndWith("_SelfIllum"));
        }

        [Test]
        public void Bake_multiplies_the_constant_and_keeps_a_plain_map_unbaked()
        {
            var plain = new SelfIlluminationMap { Glow = new TextureSlot { Image = "glow" } };
            Assert.That(plain.IsComposite, Is.False);
            Assert.That(plain.Slot.Image, Is.EqualTo("glow"));
            Assert.That(plain.Slot.Bake, Is.Null);
            var tinted = new SelfIlluminationMap { Glow = new TextureSlot { Image = "glow" }, Color = new[] { 0.5f, 1f, 0f } };
            Assert.That(tinted.IsComposite);
            Assert.That(tinted.Bake(Solid(200, 200, 200, 255), null, null, 2, 2).Take(3), Is.EqualTo(new byte[] { 100, 200, 0 }));
        }
    }

    static class RgbaTestExtensions
    {
        public static SelfIlluminationMap.Rgba WithAlpha(this SelfIlluminationMap.Rgba img, byte a)
        {
            var px = (byte[])img.Pixels.Clone();
            for (int i = 3; i < px.Length; i += 4) px[i] = a;
            return new SelfIlluminationMap.Rgba(px, img.Width, img.Height);
        }
    }
}
