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
    /// fix-80: signpost arms (G3 Sign_&lt;Place&gt; boards: gCInteraction_PS UseType None with a focus name) are imported as
    /// focus-only objects whose focus name FO_Sign_&lt;Place&gt; exists in Russian and English, with the board's height for
    /// the name's anchor. Content test over the real Gothic 3 data (skipped without an installation; G3_DATA overrides).
    /// </summary>
    public sealed class SignpostImportTests
    {
        const string DefaultData = @"C:\Program Files (x86)\Steam\steamapps\common\Gothic 3\Data";

        [Test]
        public void SignpostArms_AreFocusOnlyObjectsWithTheirPlaceName()
        {
            string data = Environment.GetEnvironmentVariable("G3_DATA") ?? DefaultData;
            if (!File.Exists(Path.Combine(data, "Projects_compiled.pak"))) Assert.Ignore("Gothic 3 data not found: " + data);
            var install = new G3Install(data);
            var report = new ImportReport();
            var table = install.LoadStrings();
            var importer = new InteractiveObjectImporter(new ItemImporter(install, new StringsImporter(table), report), _ => default, report);
            importer.Import(install);

            var signs = importer.Objects.Where(o => o.Kind == InteractiveKind.Sign).ToList();
            Assert.That(signs.Count, Is.GreaterThanOrEqualTo(100), "CP 1.75 world: 146 boards");
            Assert.That(signs.All(s => s.Name.StartsWith("Sign_", StringComparison.Ordinal)), Is.True,
                        string.Join(", ", signs.Select(s => s.Name).Where(n => !n.StartsWith("Sign_", StringComparison.Ordinal)).Distinct()));
            Assert.That(signs.Select(s => s.Name), Does.Contain("Sign_Faring").And.Contain("Sign_Montera"));
            foreach (var s in signs)
            {
                Assert.That(table.Entries.ContainsKey(s.FocusName.Value), Is.True, s.FocusName.Value);
                Assert.That(s.Visual, Does.EndWith(".xcmsh").IgnoreCase);
                Assert.That(s.FocusHeight, Is.InRange(0.1f, 1f), s.Name + ": the board is ~26 cm high");
                Assert.That(s.Lock, Is.Null);
                Assert.That(s.Container, Is.Null);
            }
            Assert.That(table.Get("FO_Sign_Faring", "Russian", "English"), Is.Not.Empty);
            Assert.That(table.Get("FO_Sign_Faring", "English"), Is.EqualTo("Faring"));
            Assert.That(importer.Objects.Any(o => o.Name != null && o.Name.Contains("TempleDoor_Tester") && o.Kind == InteractiveKind.Sign), Is.False,
                        "the temple door testers are actors, not signposts");
        }
    }
}
