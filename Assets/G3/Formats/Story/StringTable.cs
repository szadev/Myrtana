using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using G3.Formats.Genome;

namespace G3.Formats.Story
{
    /// <summary>One localized string: text + stage direction ("mocking", "angry") per language column.</summary>
    public sealed class LocString
    {
        public string Id;
        public string[] Text;
        public string[] Stage;
        /// <summary>Number of ';'-separated fields on the INI line (2 × language count when regular).</summary>
        public int FieldCount;
    }

    /// <summary>
    /// Gothic 3 localization table (Docs/formats-story.md §1). Two on-disk forms:
    /// <list type="bullet">
    /// <item><c>Strings/stringtable.ini</c> — INI, single-byte (cp1252) or UTF-16LE with BOM;
    /// <c>ID=text0;stage0;text1;stage1;…</c>, one text/stage pair per language of <c>Languages=</c>.</item>
    /// <item><c>Projects_compiled/stringtable.bin</c> — GENOMFLE, UTF-16 strings, per-language sparse maps.</item>
    /// </list>
    /// Ids are case-insensitive (the engine compares them that way).
    /// </summary>
    public sealed class StringTable
    {
        public readonly List<string> Languages = new List<string>();
        public string CurrentLanguage;
        public int Revision;
        public readonly Dictionary<string, LocString> Entries = new Dictionary<string, LocString>(StringComparer.OrdinalIgnoreCase);
        /// <summary>INI lines whose field count is not 2 × languages (data errors in the shipped table; mapped positionally).</summary>
        public readonly List<string> Irregular = new List<string>();
        /// <summary>INI sections other than LocAdmin_Languages / LocAdmin_Strings (e.g. empty LocAdmin_Revisions).</summary>
        public readonly List<string> OtherSections = new List<string>();

        public int LanguageIndex(string language) => Languages.FindIndex(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase));

        /// <summary>Text for <paramref name="id"/> in the first of <paramref name="languages"/> that has a non-empty value; null if none.</summary>
        public string Get(string id, params string[] languages)
        {
            if (string.IsNullOrEmpty(id) || !Entries.TryGetValue(id, out var e)) return null;
            foreach (var lang in languages)
            {
                int i = LanguageIndex(lang);
                if (i >= 0 && i < e.Text.Length && !string.IsNullOrEmpty(e.Text[i])) return e.Text[i];
            }
            return null;
        }

        public string GetStage(string id, string language)
        {
            if (string.IsNullOrEmpty(id) || !Entries.TryGetValue(id, out var e)) return null;
            int i = LanguageIndex(language);
            return i >= 0 && i < e.Stage.Length ? e.Stage[i] : null;
        }

        /// <summary>
        /// Overlay another table (e.g. UpdatePack <c>stringtableMod.ini</c>, or a translation with extra languages).
        /// Languages are matched by name; new languages are appended; non-empty texts of <paramref name="other"/> win.
        /// </summary>
        public void Merge(StringTable other)
        {
            var map = new int[other.Languages.Count];
            for (int i = 0; i < map.Length; i++)
            {
                int j = LanguageIndex(other.Languages[i]);
                if (j < 0) { Languages.Add(other.Languages[i]); j = Languages.Count - 1; }
                map[i] = j;
            }
            foreach (var e in Entries.Values) Resize(e, Languages.Count);
            foreach (var o in other.Entries.Values)
            {
                if (!Entries.TryGetValue(o.Id, out var e))
                {
                    e = new LocString { Id = o.Id, Text = new string[Languages.Count], Stage = new string[Languages.Count] };
                    Entries[o.Id] = e;
                }
                for (int i = 0; i < map.Length && i < o.Text.Length; i++)
                {
                    if (!string.IsNullOrEmpty(o.Text[i])) e.Text[map[i]] = o.Text[i];
                    if (!string.IsNullOrEmpty(o.Stage[i])) e.Stage[map[i]] = o.Stage[i];
                }
            }
        }

        static void Resize(LocString e, int n)
        {
            if (e.Text.Length >= n) return;
            Array.Resize(ref e.Text, n);
            Array.Resize(ref e.Stage, n);
        }

        // ------------------------------------------------------------------ INI

        /// <summary>
        /// Parse stringtable.ini. Encoding: UTF-16LE if the file starts with FF FE, otherwise the given
        /// single-byte code page (1252 for the shipped Western table; 1251 for Russian community tables).
        /// </summary>
        public static StringTable ReadIni(byte[] data, int singleByteCodePage = 1252)
        {
            string text = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE
                ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
                : SingleByte.Decode(data, 0, data.Length, singleByteCodePage);
            var t = new StringTable();
            string section = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    if (section != "LocAdmin_Languages" && section != "LocAdmin_Strings") t.OtherSections.Add(section);
                    continue;
                }
                int eq = line.IndexOf('=');
                string key = eq < 0 ? line : line.Substring(0, eq);
                string value = eq < 0 ? string.Empty : line.Substring(eq + 1);
                if (section == "LocAdmin_Languages")
                {
                    if (key == "Languages") t.Languages.AddRange(value.Split(';'));
                    else if (key == "CurrentLanguage") t.CurrentLanguage = value;
                    else if (key == "CurrentRevision") int.TryParse(value, out t.Revision);
                    else throw new InvalidDataException("stringtable.ini: unknown LocAdmin_Languages key " + key);
                }
                else if (section == "LocAdmin_Strings")
                {
                    int n = t.Languages.Count;
                    if (n == 0)
                    {
                        // stringtableMod.ini has no language header: infer the column count from the first line
                        // (the UpdatePack mod table is EN;IT;FR;DE;ES;CZ;HU;PL;RU;ZH).
                        n = (value.Split(';').Length) / 2;
                        for (int i = 0; i < n; i++) t.Languages.Add(i < DefaultModLanguages.Length ? DefaultModLanguages[i] : "Lang" + i);
                    }
                    var f = value.Split(';');
                    var e = new LocString { Id = key, Text = new string[n], Stage = new string[n], FieldCount = f.Length };
                    for (int i = 0; i < n; i++)
                    {
                        e.Text[i] = 2 * i < f.Length ? f[2 * i] : string.Empty;
                        e.Stage[i] = 2 * i + 1 < f.Length ? f[2 * i + 1] : string.Empty;
                    }
                    if (f.Length != 2 * n) t.Irregular.Add(key);
                    t.Entries[key] = e;
                }
                else throw new InvalidDataException($"stringtable.ini: line outside known section: {key}");
            }
            if (t.CurrentLanguage == null && t.Languages.Count > 0) t.CurrentLanguage = t.Languages[0];
            return t;
        }

        /// <summary>Column order of UpdatePack/Z_UpdatePack_SupportMod/stringtableMod.ini (headerless; inferred from content).</summary>
        public static readonly string[] DefaultModLanguages =
            { "English", "Italian", "French", "German", "Spanish", "Czech", "Hungarian", "Polish", "Russian", "Chinese" };

        // ------------------------------------------------------------------ compiled .bin

        /// <summary>
        /// Parse Projects_compiled/stringtable.bin (GENOMFLE). Layout (§1.2):
        /// u16 1, u8 1, u32 langCount, langCount × { str lang, u16 2, str lang, u8 1, u32 n,
        /// n × { str id, u16 1, u16 bytes, UTF-16 text, u16 bytes, UTF-16 stage } },
        /// trailer { u32 revision, str current, str current, u8 1, u32 0 }. Parses to the exact payload end.
        /// </summary>
        public static StringTable ReadBin(byte[] data)
        {
            var g = new GenomeReader(data);
            if (!g.IsGenomFile) throw new InvalidDataException("stringtable.bin: not GENOMFLE");
            var r = g.R;
            var t = new StringTable();
            Expect(r.ReadUInt16(), 1, "version");
            Expect(r.ReadByte(), 1, "flag");
            int langs = r.ReadInt32();
            var perLang = new List<KeyValuePair<string, string>[]>();
            var stages = new List<string[]>();
            for (int l = 0; l < langs; l++)
            {
                string lang = g.Str();
                Expect(r.ReadUInt16(), 2, "lang map version");
                string lang2 = g.Str();
                if (lang2 != lang) throw new InvalidDataException("stringtable.bin: language name mismatch");
                Expect(r.ReadByte(), 1, "lang map flag");
                int n = r.ReadInt32();
                var arr = new KeyValuePair<string, string>[n];
                var st = new string[n];
                for (int i = 0; i < n; i++)
                {
                    string id = g.Str();
                    Expect(r.ReadUInt16(), 1, "entry version");
                    string text = Encoding.Unicode.GetString(r.ReadBytes(r.ReadUInt16()));
                    st[i] = Encoding.Unicode.GetString(r.ReadBytes(r.ReadUInt16()));
                    arr[i] = new KeyValuePair<string, string>(id, text);
                }
                t.Languages.Add(lang);
                perLang.Add(arr);
                stages.Add(st);
            }
            t.Revision = r.ReadInt32();
            t.CurrentLanguage = g.Str();
            g.Str();
            Expect(r.ReadByte(), 1, "trailer flag");
            Expect(r.ReadInt32(), 0, "trailer count");
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"stringtable.bin: {g.PayloadEnd - g.Position} unread bytes");
            for (int l = 0; l < langs; l++)
                for (int i = 0; i < perLang[l].Length; i++)
                {
                    var id = perLang[l][i].Key;
                    if (!t.Entries.TryGetValue(id, out var e))
                        t.Entries[id] = e = new LocString { Id = id, Text = new string[langs], Stage = new string[langs], FieldCount = 2 * langs };
                    e.Text[l] = perLang[l][i].Value;
                    e.Stage[l] = stages[l][i];
                }
            return t;
        }

        static void Expect(long got, long want, string what)
        {
            if (got != want) throw new InvalidDataException($"stringtable.bin: {what} = {got}, expected {want}");
        }
    }

    /// <summary>Single-byte Windows code pages without depending on System.Text.Encoding.CodePages (Unity/Mono safe).</summary>
    public static class SingleByte
    {
        // 0x80..0x9F of cp1252 (U+FFFD = undefined); 0xA0..0xFF equal Latin-1.
        static readonly string Cp1252High =
            "\u20AC\uFFFD\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\uFFFD\u017D\uFFFD" +
            "\uFFFD\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\uFFFD\u017E\u0178";

        // 0x80..0xBF of cp1251; 0xC0..0xFF = U+0410..U+044F.
        static readonly string Cp1251High =
            "\u0402\u0403\u201A\u0453\u201E\u2026\u2020\u2021\u20AC\u2030\u0409\u2039\u040A\u040C\u040B\u040F" +
            "\u0452\u2018\u2019\u201C\u201D\u2022\u2013\u2014\uFFFD\u2122\u0459\u203A\u045A\u045C\u045B\u045F" +
            "\u00A0\u040E\u045E\u0408\u00A4\u0490\u00A6\u00A7\u0401\u00A9\u0404\u00AB\u00AC\u00AD\u00AE\u0407" +
            "\u00B0\u00B1\u0406\u0456\u0491\u00B5\u00B6\u00B7\u0451\u2116\u0454\u00BB\u0458\u0405\u0455\u0457";

        public static string Decode(byte[] data, int offset, int count, int codePage)
        {
            if (codePage != 1252 && codePage != 1251 && codePage != 28591)
                throw new NotSupportedException("code page " + codePage);
            var c = new char[count];
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                if (b < 0x80 || codePage == 28591) c[i] = (char)b;
                else if (codePage == 1252) c[i] = b < 0xA0 ? Cp1252High[b - 0x80] : (char)b;
                else c[i] = b < 0xC0 ? Cp1251High[b - 0x80] : (char)(0x0410 + (b - 0xC0));
            }
            return new string(c);
        }
    }
}
