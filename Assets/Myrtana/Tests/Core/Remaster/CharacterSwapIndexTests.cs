using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The runtime character set's lazy swaps (Docs/remaster-characters.md §6.6): matching by names, loading on first use.</summary>
    public class CharacterSwapIndexTests
    {
        [Test]
        public void Find_PrefersSameMaterials_ElseSameMesh_ByNameVerticesAndSkinning()
        {
            var ix = new CharacterSwapIndex();
            ix.Add(0, "G3_Hero_Body_Rebel", 2400, true, new[] { "Rebel_S1" });
            ix.Add(1, "G3_Hero_Body_Rebel", 2400, true, new[] { "Rebel_S2" });
            ix.Add(2, "G3_Head_Hero_Hero_01", 900, true, new string[0]);   // any materials
            Assert.That(ix.Count, Is.EqualTo(3));

            Assert.That(ix.Find("G3_Hero_Body_Rebel", 2400, true, new[] { "Rebel_S2" }, out bool exact), Is.EqualTo(1));
            Assert.That(exact, Is.True);
            Assert.That(ix.Find("G3_Hero_Body_Rebel", 2400, true, new[] { "Rebel_S3" }, out exact), Is.EqualTo(0), "another switch: first entry of the mesh");
            Assert.That(exact, Is.False);
            Assert.That(ix.Find("G3_Head_Hero_Hero_01", 900, true, new[] { "whatever" }, out exact), Is.EqualTo(2));
            Assert.That(exact, Is.True, "an entry without materials matches any");

            Assert.That(ix.Find("G3_Hero_Body_Rebel", 2401, true, new[] { "Rebel_S1" }, out _), Is.EqualTo(-1), "same name, other mesh");
            Assert.That(ix.Find("G3_Hero_Body_Rebel", 2400, false, new[] { "Rebel_S1" }, out _), Is.EqualTo(-1), "static vs skinned");
            Assert.That(ix.Find(null, 0, true, null, out _), Is.EqualTo(-1));
        }

        sealed class A { public readonly string P; public A(string p) { P = p; } }

        [Test]
        public void Cache_LoadsOnce_OnFirstUse_AndForgetsOnClear()
        {
            int loads = 0;
            var store = new Dictionary<string, A> { ["S/mesh"] = new A("S/mesh"), ["S/mat"] = new A("S/mat") };
            var c = new LazyAssetCache<A>(p => { loads++; return store.TryGetValue(p, out var a) ? a : null; });
            Assert.That(c.Loaded, Is.EqualTo(0));
            var m = c.Get("S/mesh");
            Assert.That(c.Get("S/mesh"), Is.SameAs(m));
            Assert.That(loads, Is.EqualTo(1));
            Assert.That(c.Contains(m), Is.True);
            Assert.That(c.Get("S/missing"), Is.Null);
            Assert.That(c.Get("S/missing"), Is.Null);
            Assert.That(loads, Is.EqualTo(2), "a failed load is not retried");
            Assert.That(c.Get(""), Is.Null);
            Assert.That(c.Loaded, Is.EqualTo(1));

            Assert.That(c.GetAll(new[] { "S/mesh", "S/mat" }).Length, Is.EqualTo(2));
            Assert.That(c.GetAll(new[] { "S/mesh", "S/missing" }), Is.Null, "all or nothing");
            Assert.That(c.GetAll(new[] { "S/mesh", "S/missing" }, all: false)[1], Is.Null);

            c.Clear();
            Assert.That(c.Loaded, Is.EqualTo(0));
            Assert.That(c.Contains(m), Is.False);
            c.Get("S/mesh");
            Assert.That(loads, Is.EqualTo(4), "loaded again after Clear");
        }
    }
}
