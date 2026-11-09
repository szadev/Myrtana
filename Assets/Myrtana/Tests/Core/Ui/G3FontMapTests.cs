using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Myrtana.Ui.Skin;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>
    /// The central GUI font mapping (<see cref="G3FontMap"/>) and the coverage of the port's font: every character of the
    /// Russian and English content strings and of the G3 screens' own texts must be in Gothic3_Cyr.ttf's cmap, so no text
    /// falls back to Arial/Tahoma. Data-dependent tests look for the files in this checkout and, from a git worktree, in
    /// the main checkout (the content pack and the GUI import are git-ignored); they are ignored when missing.
    /// </summary>
    public class G3FontMapTests
    {
        [Test]
        public void Table_OriginalFacesAndPortFace()
        {
            Assert.That(G3FontMap.PortFace, Is.EqualTo("Gothic3"));
            Assert.That(G3FontMap.PortFontPath, Is.EqualTo("Assets/G3/Fonts/Gothic3_Cyr.ttf"));
            Assert.That(G3FontMap.Uses.Select(u => u.Element).Distinct().Count(), Is.EqualTo(G3FontMap.Uses.Count), "one row per element");
            foreach (var u in G3FontMap.Uses)
            {
                Assert.That(u.PortFace, Is.EqualTo(G3FontMap.PortFace), u.Element);
                if (u.Source == G3FontSource.Code)
                {
                    Assert.That(u.Original.Face, Is.EqualTo(G3FontMap.Gothic3), u.Element + ": code fonts use the engine's default face");
                    Assert.That(u.Original.Height, Is.GreaterThan(0), u.Element + ": code fonts give the cell height");
                }
                else
                {
                    Assert.That(u.Original.Face, Is.EqualTo(G3FontMap.TimesNewRoman), u.Element + ": every res/theme font is Times New Roman");
                    Assert.That(u.Original.Height, Is.LessThan(0), u.Element + ": res/theme fonts give the em");
                }
            }
        }

        [Test]
        public void Table_TipBoxAndThemeDefaults()
        {
            Assert.That(Use("tip box: title").Original.ToString(), Is.EqualTo("Times New Roman -24 w400 italic"));
            foreach (string e in new[] { "tip box: text", "tip box: check box", "dialog choices", "inventory/trade/loot lists", "tooltips" })
            {
                Assert.That(Use(e).Source, Is.EqualTo(G3FontSource.Theme), e);
                Assert.That(Use(e).Original, Is.SameAs(G3FontMap.ThemeText), e);
            }
            Assert.That(Use("tip box: OK").Original, Is.SameAs(G3FontMap.ThemeButton));
            Assert.That(G3FontMap.ThemeText.PortSize, Is.EqualTo(16f));
            Assert.That(G3FontMap.ThemeText.Bold && !G3FontMap.ThemeText.Italic, Is.True);
            Assert.That(G3FontMap.ThemeButton.PortSize, Is.EqualTo(18f));
            Assert.That(G3FontMap.Resolve(null, button: false), Is.SameAs(G3FontMap.ThemeText));
            Assert.That(G3FontMap.Resolve(null, button: true), Is.SameAs(G3FontMap.ThemeButton));
            var res = new G3Font(G3FontMap.TimesNewRoman, -19, 700);
            Assert.That(G3FontMap.Resolve(res, button: true), Is.SameAs(res));
            Assert.That(G3FontMap.FocusName.PortSize, Is.EqualTo(18f).Within(0.1f));
            Assert.That(G3FontMap.GameMessage.PortSize, Is.EqualTo(15f).Within(0.1f));
            Assert.That(Use("death: \"The End\" (GO_GameOver)").Original, Is.SameAs(G3FontMap.GameMessage));
        }

        [Test]
        public void ParagraphLineHeight_IsTimesGdiLine()
        {
            Assert.That(G3FontMetrics.ParagraphLineHeight(16), Is.EqualTo(18f), "tip box text: 16 px Times bold, 18 px lines");
            Assert.That(G3FontMetrics.ParagraphLineHeight(13), Is.EqualTo(15f), "item description");
            Assert.That(G3FontMetrics.ParagraphLineHeight(14), Is.EqualTo(16f), "book text");
            Assert.That(G3FontMetrics.ParagraphLineHeight(16), Is.LessThan(G3FontMetrics.LineHeight(16)), "tighter than Gothic3's own line");
        }

        /// <summary>The table's res fonts are the ones in the imported res data, and its theme rows have no res font.</summary>
        [Test]
        public void Table_MatchesTheImportedResData()
        {
            string path = FindFile("Assets/G3Imported/Gui/Resources/G3Gui/G3GuiData.json");
            if (path == null) Assert.Ignore("G3GuiData.json not imported (menu G3/GUI/Import)");
            var data = GuiSkinJson.Read(File.ReadAllText(path));
            var windows = new Dictionary<string, WindowData>(StringComparer.Ordinal);
            void Walk(WindowData w)
            {
                if (w.ResName != null && !windows.ContainsKey(w.ResName)) windows[w.ResName] = w;
                if (w.Children != null) foreach (var c in w.Children) Walk(c);
            }
            foreach (var s in data.Screens) Walk(s);
            int checkedRows = 0;
            foreach (var u in G3FontMap.Uses.Where(u => u.Source != G3FontSource.Code && u.Control.StartsWith("IDC_", StringComparison.Ordinal)))
            {
                Assert.That(windows.TryGetValue(u.Control, out var w), Is.True, u.Control + " not in the res data");
                if (u.Source == G3FontSource.Theme)
                    Assert.That(w.Font, Is.Null, u.Control + " has a res font; the table says theme");
                else
                {
                    Assert.That(w.Font, Is.Not.Null, u.Control + " has no res font");
                    Assert.That(w.Font.Size, Is.EqualTo(-u.Original.Height), u.Control);
                    Assert.That(w.Font.Bold, Is.EqualTo(u.Original.Bold), u.Control);
                    Assert.That(w.Font.Italic, Is.EqualTo(u.Original.Italic), u.Control);
                }
                checkedRows++;
            }
            Assert.That(checkedRows, Is.GreaterThan(25));
        }

        /// <summary>Every character of the Russian and English pack texts is drawn by the port's font, none by a fallback.</summary>
        [TestCase("Russian")]
        [TestCase("English")]
        public void PortFont_CoversThePackStrings(string language)
        {
            var cmap = PortCmap();
            string pack = FindFile("Assets/StreamingAssets/Content/Gothic3/pack.json");
            if (pack == null) Assert.Ignore("content pack not built (G3Cli content import)");
            string dir = Path.GetDirectoryName(pack);
            var files = new List<string> { Path.Combine(dir, "strings", language + ".json") };
            string docs = Path.Combine(dir, "documents");
            if (Directory.Exists(docs)) files.AddRange(Directory.GetFiles(docs, "*.json"));
            var missing = new SortedDictionary<int, string>();
            int strings = 0;
            foreach (string file in files.Where(File.Exists))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                foreach (string text in Strings(doc.RootElement))
                {
                    strings++;
                    CollectMissing(text, cmap, missing, Path.GetFileName(file));
                }
            }
            Assert.That(strings, Is.GreaterThan(10000), "the string table was read");
            Assert.That(missing, Is.Empty, "not in Gothic3_Cyr.ttf: " + Describe(missing));
        }

        /// <summary>Texts written in the G3-skinned screens' code (menu labels, "…" for an untitled choice) are covered too.</summary>
        [Test]
        public void PortFont_CoversTheScreensOwnTexts()
        {
            var cmap = PortCmap();
            string root = FindFile("Assets/Myrtana/Unity");
            if (root == null) Assert.Ignore("sources not found");
            var literal = new Regex("\"((?:[^\"\\\\\\n]|\\\\.)*)\"");
            var missing = new SortedDictionary<int, string>();
            foreach (string folder in new[] { "Hud", "Menu", "Dialog", "Screens/G3", "Ui", "Start", "Combat" })
            {
                string dir = Path.Combine(root, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    foreach (string line in File.ReadAllLines(file))
                    {
                        string code = Regex.Replace(line, "//.*", string.Empty);
                        if (code.Contains("Debug.") || code.Contains("Exception(")) continue; // logs are not on screen
                        foreach (Match m in literal.Matches(code))
                            CollectMissing(m.Groups[1].Value, cmap, missing, Path.GetFileName(file));
                    }
                }
            }
            Assert.That(missing, Is.Empty, "not in Gothic3_Cyr.ttf: " + Describe(missing));
        }

        static G3FontUse Use(string element) => G3FontMap.Uses.Single(u => u.Element == element);

        static void CollectMissing(string text, HashSet<int> cmap, SortedDictionary<int, string> missing, string where)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i++) : text[i];
                if (cp < 0x20 || cmap.Contains(cp) || missing.ContainsKey(cp)) continue;
                missing[cp] = where + ": " + (text.Length > 40 ? text.Substring(0, 40) + "…" : text);
            }
        }

        static string Describe(SortedDictionary<int, string> missing) =>
            string.Join("; ", missing.Select(m => $"U+{m.Key:X4} '{char.ConvertFromUtf32(m.Key)}' ({m.Value})"));

        static IEnumerable<string> Strings(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    yield return e.GetString();
                    break;
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                        foreach (string s in Strings(p.Value)) yield return s;
                    break;
                case JsonValueKind.Array:
                    foreach (var v in e.EnumerateArray())
                        foreach (string s in Strings(v)) yield return s;
                    break;
            }
        }

        static HashSet<int> PortCmap()
        {
            string path = FindFile(G3FontMap.PortFontPath);
            if (path == null) Assert.Ignore(G3FontMap.PortFontPath + " not found");
            byte[] ttf = File.ReadAllBytes(path);
            int tables = ttf[4] << 8 | ttf[5], cmap = -1;
            for (int i = 0; i < tables; i++)
            {
                int rec = 12 + i * 16;
                if (Encoding.ASCII.GetString(ttf, rec, 4) == "cmap")
                    cmap = ttf[rec + 8] << 24 | ttf[rec + 9] << 16 | ttf[rec + 10] << 8 | ttf[rec + 11];
            }
            return new HashSet<int>(G3.Formats.Gui.TrueTypeCmapFilter.ReadUnicodeMap(ttf, cmap).Keys);
        }

        /// <summary>
        /// A file or folder of the repository: searched upwards from the test binaries and the working directory, then in
        /// the main checkout when this is a git worktree (".git" is a file "gitdir: &lt;main&gt;/.git/worktrees/&lt;name&gt;").
        /// </summary>
        static string FindFile(string relative)
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                {
                    string p = Path.Combine(d.FullName, relative);
                    if (File.Exists(p) || Directory.Exists(p)) return p;
                    string git = Path.Combine(d.FullName, ".git");
                    if (!File.Exists(git)) continue;
                    string line = File.ReadAllText(git).Trim();
                    int at = line.IndexOf("/.git/worktrees/", StringComparison.Ordinal);
                    if (!line.StartsWith("gitdir:", StringComparison.Ordinal) || at < 0) continue;
                    string main = Path.Combine(line.Substring(7, at - 7).Trim(), relative);
                    if (File.Exists(main) || Directory.Exists(main)) return main;
                }
            }
            return null;
        }
    }
}
