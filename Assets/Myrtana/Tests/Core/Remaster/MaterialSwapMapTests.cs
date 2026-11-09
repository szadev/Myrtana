using System.Collections.Generic;
using G3.Unity;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>The runtime character remaster's material table (Docs/remaster-characters.md §6.3).</summary>
    public class MaterialSwapMapTests
    {
        sealed class M { public readonly string N; public M(string n) { N = n; } public override string ToString() => N; }

        sealed class Store
        {
            public readonly Dictionary<string, M> Assets = new Dictionary<string, M>();
            public int Loads;
            public M Load(string key) { Loads++; return Assets.TryGetValue(key, out var m) ? m : null; }
        }

        static MaterialSwapMap<M> Map(Store s) => new MaterialSwapMap<M>(m => m.N, s.Load);

        [Test]
        public void MapsForwardAndBack_SlotBySlot_LoadingOnFirstUse()
        {
            var s = new Store();
            M a = new M("a"), b = new M("b"), c = new M("c");
            s.Assets["R/a_r"] = new M("a_r");
            s.Assets["R/c_r"] = new M("c_r");
            var map = Map(s);
            Assert.That(map.Add("a", "R/a_r") && map.Add("c", "R/c_r"), Is.True);
            Assert.That(s.Loads, Is.EqualTo(0), "nothing is loaded before a renderer needs it");
            var orig = new[] { a, b, c };
            var on = map.Map(orig, true);
            Assert.That(on, Is.EqualTo(new[] { s.Assets["R/a_r"], b, s.Assets["R/c_r"] }));
            Assert.That(orig, Is.EqualTo(new[] { a, b, c }), "the input array is not changed");
            Assert.That(map.Map(on, false), Is.EqualTo(orig));
            Assert.That(map.Map(on, true), Is.EqualTo(on), "mapping on twice is idempotent");
            Assert.That(map.Map(orig, false), Is.EqualTo(orig), "off on an original is a no-op");
            map.Map(new[] { a, a, c }, true);
            Assert.That(s.Loads, Is.EqualTo(2), "each remaster is loaded once");
            Assert.That(map.Loaded, Is.EqualTo(2));
            Assert.That(map.IsRemaster(s.Assets["R/a_r"]) && !map.IsRemaster(a), Is.True);
        }

        [Test]
        public void SameArray_WhenNothingChanges_AndMissingRemasterKeepsOriginal()
        {
            var s = new Store();
            var map = Map(s);
            map.Add("x", "R/missing");
            var x = new M("x");
            var arr = new[] { new M("p"), null, x };
            Assert.That(map.Map(arr, true), Is.SameAs(arr), "an unloadable remaster leaves the original");
            Assert.That(map.Map(null, true), Is.Null);
        }

        [Test]
        public void RejectsAmbiguousPairs_AndSecondInstanceOfOneName()
        {
            var s = new Store();
            s.Assets["R/r"] = new M("r");
            var map = Map(s);
            Assert.That(map.Add("a", "R/r"), Is.True);
            Assert.That(map.Add("b", "R/r"), Is.False, "two originals on one remaster: the way back would be ambiguous");
            Assert.That(map.Add("a", "R/q"), Is.False, "an original maps once");
            Assert.That(map.Add("a", "a"), Is.False);
            Assert.That(map.Add(null, "R/x"), Is.False);
            Assert.That(map.Count, Is.EqualTo(1));
            M a1 = new M("a"), a2 = new M("a");
            var r = map.Forward(a1);
            Assert.That(r, Is.SameAs(s.Assets["R/r"]));
            Assert.That(map.Forward(a2), Is.SameAs(a2), "another instance named 'a' is not swapped (its way back would be lost)");
            Assert.That(map.Back(r), Is.SameAs(a1));
        }

        [Test]
        public void ReleaseLoaded_ForgetsInstances()
        {
            var s = new Store();
            s.Assets["R/r"] = new M("r");
            var map = Map(s);
            map.Add("a", "R/r");
            var a = new M("a");
            map.Forward(a);
            map.ReleaseLoaded();
            Assert.That(map.Loaded, Is.EqualTo(0));
            Assert.That(map.IsRemaster(s.Assets["R/r"]), Is.False);
            map.Forward(a);
            Assert.That(s.Loads, Is.EqualTo(2), "loaded again after a release");
        }
    }
}
