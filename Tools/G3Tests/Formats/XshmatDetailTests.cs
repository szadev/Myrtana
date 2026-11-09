using System;
using System.IO;
using G3.Formats.Material;
using G3.Formats.Pak;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>
    /// Detail maps: a diffuse layer that is Mul(base sampler, second sampler) keeps the second map (terrain-detail).
    /// Reads the installed game's _compiledMaterial volume (G3_DATA or the Steam path); ignored when it is absent.
    /// </summary>
    [TestFixture]
    public class XshmatDetailTests
    {
        static MaterialResource Load(string stem)
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
            if (!File.Exists(Path.Combine(data, "_compiledMaterial.pak"))) Assert.Ignore("Gothic 3 data not installed");
            using var fs = new G3FileSystem(data, "_compiledMaterial");
            var e = fs.GetByName(stem + ".xshmat");
            Assert.That(e, Is.Not.Null, stem);
            return Xshmat.Read(e.Read(), stem);
        }

        [Test]
        public void Landscape_layers_keep_the_shared_detail_map()
        {
            var m = Load("G3_Nature_Ground_Forest_01_01_A");
            Assert.That(m.VertexAlphaBlend);
            Assert.That(m.Diffuse.Image, Is.EqualTo("G3_Nature_Ground_Forest_01_Diffuse_01"));
            Assert.That(m.DiffuseDetail.Image, Is.EqualTo("G3_Misc_Detailmap_01_Diffuse_01"));
            Assert.That(m.DiffuseDetail.ScaleU, Is.EqualTo(0.025f).Within(1e-6));
            Assert.That(m.DiffuseDetail2.Image, Is.EqualTo("G3_Misc_Detailmap_01_Diffuse_01"));
            Assert.That(m.Normal2, Is.Not.Null);
        }

        [Test]
        public void Single_layer_detail_and_constant_multiplier()
        {
            var m = Load("G3_Nature_Ground_Forest_01_01_B");
            Assert.That(m.VertexAlphaBlend, Is.False);
            Assert.That(m.DiffuseDetail.Image, Is.EqualTo("G3_Misc_Detailmap_01_Diffuse_01"));
            Assert.That(m.DiffuseMulColor, Is.Null);

            var glass = Load("G3_Special_Window_Glass_01");
            Assert.That(glass.DiffuseDetail, Is.Null);
            Assert.That(glass.DiffuseMulColor[0], Is.EqualTo(0.74f).Within(0.01));
        }

        [Test]
        public void Layer_without_multiply_has_no_detail()
        {
            var m = Load("G3_Architecture_Groundbroken_01_A");   // Blend(Mul(sand, dunes detail), plain, VC.a)
            Assert.That(m.DiffuseDetail.Image, Is.EqualTo("G3_Nature_Dunes_Detail_01_Diffuse_01"));
            Assert.That(m.DiffuseDetail2, Is.Null);
        }
    }
}
