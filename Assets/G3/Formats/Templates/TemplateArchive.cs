using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using G3.Formats.Genome;
using G3.Formats.Pak;

namespace G3.Formats.Templates
{
    /// <summary>
    /// One header ("template entity") of a .tple file (g3dit LrentNode/template/TemplateEntity.java).
    /// Header 0 is the "item header" (HelperParent = true, its GUID is the one listed in .lrtpldatasc and the
    /// one world entities reference as creator); header 1 is the "reference header" that carries the same
    /// property sets. Docs/formats-items.md §1.
    /// </summary>
    public sealed class TemplateEntity
    {
        public ushort HeaderVersion;          // 62 in all retail files
        public string FileName;               // "_Weapons_1H_Story_It_1H_AncestorSword_01.tple"
        public Guid Guid;
        public bool Enabled, RenderingEnabled, ProcessingDisabled, DeactivationEnabled, Pickable, CollisionEnabled;
        public bool HelperParent;             // true on the item header
        public Guid RefTemplate;              // object groups: header referencing another template (no PS then)
        public float RenderAlpha;
        public ushort InsertType;
        public string Name;                   // entity name = template name, e.g. "It_1H_AncestorSword_01"
        /// <summary>Row-major, row vectors (row 3 = translation), centimetres. Usually identity for items.</summary>
        public float[] World = new float[16];
        public float[] LocalBounds = new float[6];
        public bool Deleted;
        public float UniformScaling = 1;
        public int Parent = -1;
        public readonly List<PropertyObject> PropertySets = new List<PropertyObject>();
        /// <summary>Stream offset right after each PS's class version (start of its post data), parallel to PropertySets.</summary>
        public readonly List<long> PostDataStart = new List<long>();
        /// <summary>Reader the property values must be decoded with (strings are table indices).</summary>
        public GenomeReader Source;

        public PropertyObject Get(string className)
        {
            foreach (var ps in PropertySets) if (ps.ClassName == className) return ps;
            return null;
        }
    }

    /// <summary>A parsed .tple file.</summary>
    public sealed class TemplateFile
    {
        public string Path;                   // pak path (may be null)
        public readonly List<TemplateEntity> Headers = new List<TemplateEntity>();

        public TemplateEntity ItemHeader => Headers.Count > 0 ? Headers[0] : null;
        /// <summary>First header that is not the helper parent (the real entity), else the item header.</summary>
        public TemplateEntity Main
        {
            get
            {
                foreach (var h in Headers) if (!h.HelperParent) return h;
                return ItemHeader;
            }
        }
        public Guid Guid => ItemHeader?.Guid ?? Guid.Empty;
        public string Name => Main?.Name;
        public bool IsDeleted => Path != null && Path.StartsWith("_deleted/", StringComparison.OrdinalIgnoreCase);
        public GenomeReader Source => Main?.Source;

        /// <summary>A property set of the main header (falls back to any header).</summary>
        public PropertyObject Get(string className)
        {
            var ps = Main?.Get(className);
            if (ps != null) return ps;
            foreach (var h in Headers) { ps = h.Get(className); if (ps != null) return ps; }
            return null;
        }

        const uint DeadC0de = 0xDEADC0DE;

        /// <summary>
        /// GENOMFLE + "GENOMETP" u16 0x3E u16 1 + bool indexed [u32 n, n × u16 string-table index] + u32 codeSize
        /// + u32 headerCount + headers + hierarchy pairs (-1,-1 terminated). Throws on anything unexpected;
        /// the payload must be consumed exactly.
        /// </summary>
        public static TemplateFile Read(byte[] data, string path = null)
        {
            if (data == null || data.Length < 30) throw new InvalidDataException("tple: too short");
            if (Encoding.ASCII.GetString(data, 0, 8) == "GENOMETP") data = WrapLegacy(data);
            if (Encoding.ASCII.GetString(data, 0, 8) != "GENOMFLE") throw new InvalidDataException("tple: not a GENOMFLE file");
            var g = new GenomeReader(data);
            int hdr = g.PayloadStart;
            if (Encoding.ASCII.GetString(data, hdr, 8) != "GENOMETP") throw new InvalidDataException("tple: GENOMETP magic missing");
            g.Skip(8);
            ushort ver = g.R.ReadUInt16(); g.R.ReadUInt16();
            if (ver < 0x3B || ver > 0x3E) throw new InvalidDataException($"tple: version {ver}");
            if (g.R.ReadByte() != 0)
            {
                int n = g.R.ReadInt32();
                var idx = new ushort[n];
                bool identity = true;
                for (int i = 0; i < n; i++) { idx[i] = g.R.ReadUInt16(); if (idx[i] != i) identity = false; }
                if (!identity)
                {
                    // Re-index the string table so the stock GenomeReader resolves entries directly.
                    long pos = g.Position;
                    g = new GenomeReader(RemapStringTable(data, idx));
                    g.Position = pos;
                }
            }
            g.R.ReadUInt32(); // size of code section

            var f = new TemplateFile { Path = path };
            int count = g.R.ReadInt32();
            for (int i = 0; i < count; i++) f.Headers.Add(ReadHeader(g));
            for (int p; (p = g.R.ReadInt32()) != -1;)
            {
                int c = g.R.ReadInt32();
                if ((uint)c >= (uint)f.Headers.Count) throw new InvalidDataException("tple: bad hierarchy index");
                f.Headers[c].Parent = p;
            }
            if (g.Position != g.PayloadEnd && g.R.ReadInt32() != -1) throw new InvalidDataException("tple: second -1 missing");
            if (g.Position != g.PayloadEnd)
                throw new InvalidDataException($"tple: {g.PayloadEnd - g.Position} bytes left over");
            return f;
        }

        static TemplateEntity ReadHeader(GenomeReader g)
        {
            var r = g.R;
            var e = new TemplateEntity { Source = g };
            e.HeaderVersion = r.ReadUInt16();
            e.FileName = g.Str();
            e.Guid = g.Guid().Value;
            e.Enabled = r.ReadByte() != 0;
            e.RenderingEnabled = r.ReadByte() != 0;
            e.ProcessingDisabled = r.ReadByte() != 0;
            e.DeactivationEnabled = r.ReadByte() != 0;
            e.Pickable = r.ReadByte() != 0;
            e.CollisionEnabled = r.ReadByte() != 0;
            r.ReadByte();
            e.HelperParent = r.ReadByte() != 0;
            bool hasRef = r.ReadByte() != 0;
            if (hasRef) e.RefTemplate = g.Guid().Value;
            e.RenderAlpha = r.ReadSingle();
            e.InsertType = r.ReadUInt16();
            r.ReadByte(); // last render priority
            e.Name = g.Str();
            for (int i = 0; i < 16; i++) e.World[i] = r.ReadSingle();
            g.Skip(64 + 24);                       // local matrix, world tree box
            for (int i = 0; i < 6; i++) e.LocalBounds[i] = r.ReadSingle();
            g.Skip(24);                            // world node box
            r.ReadSingle();                        // visual LoD factor
            r.ReadByte();
            e.Deleted = r.ReadByte() != 0;
            if (e.HeaderVersion >= 40) r.ReadSingle();       // object cull factor
            if (e.HeaderVersion >= 56) r.ReadUInt32();       // data changed time stamp
            if (e.HeaderVersion >= 58) r.ReadByte();
            if (e.HeaderVersion >= 61) e.UniformScaling = r.ReadSingle();
            if (e.HeaderVersion >= 62) { r.ReadByte(); r.ReadByte(); }
            if (!hasRef)
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    // u16 psVersion, u32 size, entry className (duplicate), sub-object, DEADC0DE
                    r.ReadUInt16();
                    r.ReadUInt32();
                    g.Str();
                    var ps = g.ReadObject();
                    if (ps == null) throw new InvalidDataException("tple: empty property set");
                    long postStart = g.Position;
                    g.Position = ps.End;
                    if (r.ReadUInt32() != DeadC0de) throw new InvalidDataException($"tple: DEADC0DE missing after {ps.ClassName}");
                    e.PropertySets.Add(ps);
                    e.PostDataStart.Add(postStart);
                }
            }
            return e;
        }

        /// <summary>
        /// Legacy layout (only in _deleted/): no GENOMFLE wrapper; "GENOMETP" u16 0x3E u16 1, then the string table
        /// in place (u8 1, u32 n, n × (u16 len, chars)), then u32 codeSize and the headers (strings = u16 indices).
        /// Rewritten into the GENOMFLE layout so the stock GenomeReader can be used.
        /// </summary>
        static byte[] WrapLegacy(byte[] data)
        {
            var r = new BinaryReader(new MemoryStream(data, false));
            r.BaseStream.Position = 12;
            var strings = new List<byte[]>();
            if (r.ReadByte() != 0)
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) strings.Add(r.ReadBytes(r.ReadUInt16()));
            }
            int body = (int)r.BaseStream.Position;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("GENOMFLE")); w.Write((ushort)1); w.Write(0);
            ms.Write(data, 0, 12);
            w.Write((byte)0);                                   // no indexed table
            ms.Write(data, body, data.Length - body);
            int tableOffset = (int)ms.Position;
            w.Write(0xDEADBEEF); w.Write((byte)1); w.Write(strings.Count);
            foreach (var s in strings) { w.Write((ushort)s.Length); w.Write(s); }
            var result = ms.ToArray();
            BitConverter.GetBytes(tableOffset).CopyTo(result, 10);
            return result;
        }

        static byte[] RemapStringTable(byte[] data, ushort[] idx)
        {
            int tableOffset = BitConverter.ToInt32(data, 10);
            var r = new BinaryReader(new MemoryStream(data, tableOffset, data.Length - tableOffset, false));
            r.ReadUInt32(); r.ReadByte();
            int n = r.ReadInt32();
            var strings = new byte[n][];
            for (int i = 0; i < n; i++) strings[i] = r.ReadBytes(r.ReadUInt16());
            var ms = new MemoryStream();
            ms.Write(data, 0, tableOffset);
            var w = new BinaryWriter(ms);
            w.Write(0xDEADBEEF); w.Write((byte)1); w.Write(idx.Length);
            foreach (var i in idx) { w.Write((ushort)strings[i].Length); w.Write(strings[i]); }
            return ms.ToArray();
        }
    }

    /// <summary>
    /// All templates of the Templates volume (Templates.pak + .p00/.p01), indexed by GUID (both headers) and by
    /// template name. Non-deleted templates win on name collisions.
    /// </summary>
    public sealed class TemplateArchive
    {
        public readonly List<TemplateFile> Files = new List<TemplateFile>();
        public readonly List<string> Failures = new List<string>();
        /// <summary>GUID → .tple file name from all .lrtpldatasc lists ("{guid}:0=File.tple").</summary>
        public readonly Dictionary<Guid, string> Listed = new Dictionary<Guid, string>();
        readonly Dictionary<Guid, TemplateFile> _byGuid = new Dictionary<Guid, TemplateFile>();
        readonly Dictionary<string, TemplateFile> _byName = new Dictionary<string, TemplateFile>(StringComparer.OrdinalIgnoreCase);

        public TemplateFile Get(Guid guid) => _byGuid.TryGetValue(guid, out var f) ? f : null;
        public TemplateFile Get(string name) => name != null && _byName.TryGetValue(name, out var f) ? f : null;

        /// <param name="files">(path, loader) of every file of the Templates volume.</param>
        public static TemplateArchive Load(IEnumerable<KeyValuePair<string, Func<byte[]>>> files)
        {
            var a = new TemplateArchive();
            foreach (var kv in files)
            {
                var p = kv.Key;
                if (p.EndsWith(".lrtpldatasc", StringComparison.OrdinalIgnoreCase))
                {
                    ReadList(kv.Value(), a.Listed);
                    continue;
                }
                if (!p.EndsWith(".tple", StringComparison.OrdinalIgnoreCase)) continue;
                TemplateFile f;
                try { f = TemplateFile.Read(kv.Value(), p); }
                catch (Exception ex) { a.Failures.Add($"{p}: {ex.Message}"); continue; }
                a.Add(f);
            }
            return a;
        }

        /// <summary>Load from a G3FileSystem opened on the "Templates" volume.</summary>
        public static TemplateArchive Load(G3FileSystem fs, string volume = "Templates")
        {
            var list = new List<KeyValuePair<string, Func<byte[]>>>();
            string prefix = volume + "/";
            foreach (var e in fs.Files)
            {
                var entry = e;
                list.Add(new KeyValuePair<string, Func<byte[]>>(entry.Path, entry.Read));
            }
            return Load(list);
        }

        void Add(TemplateFile f)
        {
            Files.Add(f);
            foreach (var h in f.Headers)
                if (!_byGuid.TryGetValue(h.Guid, out var old) || (old.IsDeleted && !f.IsDeleted)) _byGuid[h.Guid] = f;
            var name = f.Name;
            if (string.IsNullOrEmpty(name)) return;
            if (!_byName.TryGetValue(name, out var prev) || (prev.IsDeleted && !f.IsDeleted)) _byName[name] = f;
        }

        /// <summary>.lrtpldatasc: "[TemplateList]" then lines "{guid}:0=File.tple" (Windows-1252 text; empty value = removed).</summary>
        public static void ReadList(byte[] data, Dictionary<Guid, string> into)
        {
            foreach (var raw in Encoding.GetEncoding(28591).GetString(data).Split('\n'))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0 || line.StartsWith("[")) continue;
                var key = line.Substring(0, eq).Trim(); int colon = key.IndexOf(':'); if (colon > 0) key = key.Substring(0, colon);
                if (Guid.TryParse(key.Trim('{', '}'), out var guid))
                    into[guid] = line.Substring(eq + 1).Trim();
            }
        }
    }
}
