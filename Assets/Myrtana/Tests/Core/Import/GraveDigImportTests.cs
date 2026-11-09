using System;
using System.IO;
using System.Linq;
using Myrtana.Core;
using Myrtana.Import.G3;
using Myrtana.Rules;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// fix-150 (owner: "I have a shovel but can't dig it up", Gotha catacombs, a stone mound with a helmeted stake at
    /// 339.3, 69.3, 514.9 m): graves are decoration in G3 CP 1.75. All 232 grave meshes (G3_Object_Grave_Cross/Stone_*)
    /// are CStat .node static meshes without gCInteraction_PS, and Script_Game has no grave code; the hero digs only at
    /// UseType PirateTreasure spots (PS_Interact case 0x57). The loot of that grave is the unlocked chest beside it
    /// (G3_Object_Interact_Animated_Chest_01, TS_Treasure_Small). Content test over the real Gothic 3 data (skipped without
    /// an installation; G3_DATA overrides).
    /// </summary>
    public sealed class GraveDigImportTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";
        const string GothaCatacombChest = "8605bce9-4fa0-4c5b-96a8-11f207e55c8c";
        static readonly float[] GothaGrave = { 339.27f, 69.33f, 514.89f };

        [Test]
        public void Graves_AreNoDigSpots_OnlyPirateTreasureIs()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Projects_compiled.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            var install = new G3Install(data);
            var report = new ImportReport();
            var importer = new InteractiveObjectImporter(new ItemImporter(install, new StringsImporter(install.LoadStrings()), report), _ => default, report);
            importer.Import(install);

            var spots = importer.Objects.Where(o => o.Kind == InteractiveKind.Treasure).ToList();
            Assert.That(spots.Count, Is.EqualTo(23), "CP 1.75: 12 PirateTreasure_01 + 11 Treasure_Spot_01");
            Assert.That(spots.All(s => s.UseType == "PirateTreasure"), Is.True);
            Assert.That(spots.All(s => s.Visual.IndexOf("Treasure", StringComparison.OrdinalIgnoreCase) >= 0), Is.True,
                        string.Join(", ", spots.Select(s => s.Visual).Distinct()));
            Assert.That(importer.Objects.Where(o => o.Visual != null && o.Visual.IndexOf("Grave", StringComparison.OrdinalIgnoreCase) >= 0)
                                        .Select(o => o.Name + " " + o.Visual), Is.Empty, "no grave mesh is interactive in G3");
            Assert.That(spots.Min(s => Distance(s.Position, GothaGrave)), Is.GreaterThan(100f), "no dig spot anywhere near Gotha");

            var chest = importer.Objects.SingleOrDefault(o => o.Id == GothaCatacombChest);
            Assert.That(chest, Is.Not.Null, "the chest beside the Gotha catacomb grave");
            Assert.That(chest.Kind, Is.EqualTo(InteractiveKind.Chest));
            Assert.That(Distance(chest.Position, GothaGrave), Is.LessThan(2.5f));
            Assert.That(chest.Lock == null || !chest.Lock.Locked, Is.True, "unlocked");
            Assert.That(chest.Container.TreasureSets, Does.Contain("TS_Treasure_Small"));
        }

        static float Distance(float[] a, float[] b)
        {
            float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
