using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using G3.Unity;
using NUnit.Framework;

namespace G3Tests.Formats
{
    /// <summary>Terrain relief knobs (Docs/remaster-world.md section 13): the shipped JSON equals the code defaults; the facts table decodes.</summary>
    [TestFixture]
    public class TerrainReliefJsonTests
    {
        static string Res(string file, [CallerFilePath] string self = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "../../../Assets/G3/Unity/Runtime/Remaster/Resources/" + file));

        [Test]
        public void ShippedJson_EqualsCodeDefaults_AndKnowsEveryField()
        {
            string text = File.ReadAllText(Res("G3TerrainRelief.json"));
            var parsed = JsonSerializer.Deserialize<ReliefSettings>(text, new JsonSerializerOptions { IncludeFields = true })!;
            var keys = new HashSet<string>();
            using (var doc = JsonDocument.Parse(text))
                foreach (var p in doc.RootElement.EnumerateObject()) keys.Add(p.Name);
            var def = ReliefSettings.Default;
            foreach (var f in typeof(ReliefSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.IsTrue(keys.Contains(f.Name), $"G3TerrainRelief.json lacks '{f.Name}'");
                Assert.AreEqual(f.GetValue(def), f.GetValue(parsed), $"'{f.Name}': JSON and code default differ");
                keys.Remove(f.Name);
            }
            Assert.IsEmpty(keys, "unknown keys in G3TerrainRelief.json: " + string.Join(", ", keys));
        }

        [Test]
        public void FactsTable_IndexesEveryCell()
        {
            var bytes = File.ReadAllBytes(Res("G3TerrainReliefFacts.bytes"));
            var index = ReliefFacts.Index(bytes);
            Assert.That(index.Count, Is.EqualTo(782), "one block per landscape cell (G3Cli relief facts)");
            Assert.That(index.ContainsKey("G3_Myrtana_Landscape_Cell_244"), Is.True);
            var (o, n) = index["G3_Myrtana_Landscape_Cell_244"];
            var f = ReliefFacts.Decode(bytes, o, n);
            Assert.That(f.Count, Is.EqualTo(n));
            foreach (var v in f.N) Assert.That(System.Math.Abs(v.Length() - 1), Is.LessThan(1e-4f));
        }
    }
}
