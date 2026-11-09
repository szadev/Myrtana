using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using G3.Formats.Genome;

namespace G3.Formats.Story
{
    /// <summary>gEInfoCondType (GameEnum.h) — when/how an info becomes available.</summary>
    public enum InfoCondType
    {
        Crime = 0, Fight = 1, Hello = 2, General = 3, Overtime = 4, Open = 5, Activator = 6, Running = 7,
        Delivery = 8, PartDelivery = 9, Success = 10, DoCancel = 11, Failed = 12, Cancelled = 13, Join = 14,
        Dismiss = 15, Teach = 16, Trade = 17, PickPocket = 18, Ready = 19, Lost = 20, Reactivator = 21, Won = 22,
        MasterThief = 23, EnclaveFriend = 24, PoliticalFriend = 25, FirstWarn = 26, SecondWarn = 27, MobJoin = 28,
        SlaveJoin = 29, LongTimeNoSee = 30, EnclaveCrime = 31, PoliticalCrime = 32, MobDismiss = 33, Wait = 34,
        Heal = 35, Animal = 36, Companion = 37, NotFriend = 38, NotFriendOrThief = 39,
        NothingToSay = 50, End = 51, Back = 52, BackPrevLevel = 53,
    }

    /// <summary>gEInfoType (GameEnum.h).</summary>
    public enum InfoType { Refuse = 0, Important = 1, News = 2, Info = 3, Parent = 4, Comment = 5, QuestRunning = 7 }

    /// <summary>
    /// One gCInfoScriptLine_PS: a command of the info's script. Field meaning depends on the command
    /// (table in Docs/formats-story.md §2.3), e.g. Say: Entity1 = speaker ("npc"/"player"/NPC name), TextId = stringtable id;
    /// Give: Entity1 → Entity2, Id1 = item template, Id2 = amount; SetGameEvent: Id1 = event name.
    /// </summary>
    public sealed class InfoLine
    {
        public string Command, Entity1, Entity2, Id1, Id2, TextId;
        public override string ToString() => $"{Command}({Entity1},{Entity2},{Id1},{Id2},{TextId})";
    }

    /// <summary>One dialogue option (gCInfo_PS). Children (Parent == Name) are the sub-choices shown after it runs.</summary>
    public sealed class Info
    {
        public string Name;
        public int SortID;
        /// <summary>NPC / interact object the info belongs to ("PC_Hero" = player monologue, "sh" = test storyhelper).</summary>
        public string Owner;
        public bool InfoGiven;
        public string Parent;
        public string Quest;
        public int ConditionType;
        public int Type;
        public int GoldCost;
        public string CondOwnerNearEntity;
        public string Folder;
        public string[] CondPlayerKnows = Array.Empty<string>();
        public string[] CondPlayerKnowsNot = Array.Empty<string>();
        public string CondItemContainer;
        public string[] CondItems = Array.Empty<string>();
        public string[] CondItemAmounts = Array.Empty<string>();
        public string[] CondSecondaryNPC = Array.Empty<string>();
        public string[] CondSecondaryNPCStates = Array.Empty<string>();
        public string TeachSkill;
        public string TeachAttrib;
        public int TeachAttribValue;
        public bool ClearChildren;
        public bool Permanent;
        public readonly List<InfoLine> Lines = new List<InfoLine>();
        /// <summary>Every key/value as stored (INI) — includes legacy keys like TeachSkillIsPerc / PlayerSkills*.</summary>
        public readonly Dictionary<string, string> Raw = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>INI only: the InfoScript_* arrays had different lengths (stale data; missing cells read as "").</summary>
        public bool ScriptArrayMismatch;

        public InfoCondType CondTypeEnum => (InfoCondType)ConditionType;
        public InfoType TypeEnum => (InfoType)Type;
    }

    /// <summary>Readers for Infos/&lt;world&gt;/&lt;Name&gt;_info_&lt;world&gt;.info (INI) and Projects_compiled/compiledinfos_&lt;world&gt;.bin.</summary>
    public static class InfoFile
    {
        /// <summary>Keys the shipped .info files use (anything else is reported by validation).</summary>
        public static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name", "SortID", "Owner", "InfoGiven", "Parent", "Quest", "ConditionType", "InfoType", "GoldCost",
            "CondOwnerNearEntity", "Folder", "CondPlayerKnows", "CondPlayerKnowsNot", "CondItemContainer", "CondItems",
            "CondItemAmounts", "CondSecondaryNPC", "CondSecondaryNPCstates", "TeachSkill", "TeachAttrib", "TeachAttribValue",
            "TeachSkillIsPerc", "TeachSkillIndex", "TeachSkillValue", "CondPlayerSkills", "CondPlayerSkillValues",
            "PlayerSkillsIsPerc", "PlayerSkillsIndex", "PlayerSkillsValue", "ClearChildren", "Permanent",
            "InfoScript_Commands", "InfoScript_Entities1", "InfoScript_Entities2", "InfoScript_IDs1", "InfoScript_IDs2", "InfoScript_Texts",
        };

        /// <summary>Parse one .info file (ASCII/cp1252 INI, section [Info]; "Key" without '=' means empty).</summary>
        public static Info ReadIni(byte[] data)
        {
            var kv = Ini.Parse(data, "Info");
            var i = new Info();
            foreach (var p in kv) i.Raw[p.Key] = p.Value;
            i.Name = kv.Get("Name");
            i.SortID = Ini.Int(kv.Get("SortID"));
            i.Owner = kv.Get("Owner");
            i.InfoGiven = Ini.Bool(kv.Get("InfoGiven"));
            i.Parent = kv.Get("Parent");
            i.Quest = kv.Get("Quest");
            i.ConditionType = Ini.Int(kv.Get("ConditionType"));
            i.Type = Ini.Int(kv.Get("InfoType"));
            i.GoldCost = Ini.Int(kv.Get("GoldCost"));
            i.CondOwnerNearEntity = kv.Get("CondOwnerNearEntity");
            i.Folder = kv.Get("Folder");
            i.CondPlayerKnows = Ini.List(kv.Get("CondPlayerKnows"));
            i.CondPlayerKnowsNot = Ini.List(kv.Get("CondPlayerKnowsNot"));
            i.CondItemContainer = kv.Get("CondItemContainer");
            i.CondItems = Ini.List(kv.Get("CondItems"));
            i.CondItemAmounts = Ini.List(kv.Get("CondItemAmounts"));
            i.CondSecondaryNPC = Ini.List(kv.Get("CondSecondaryNPC"));
            i.CondSecondaryNPCStates = Ini.List(kv.Get("CondSecondaryNPCstates"));
            i.TeachSkill = kv.Get("TeachSkill");
            i.TeachAttrib = kv.Get("TeachAttrib");
            i.TeachAttribValue = Ini.Int(kv.Get("TeachAttribValue"));
            i.ClearChildren = Ini.Bool(kv.Get("ClearChildren"));
            i.Permanent = Ini.Bool(kv.Get("Permanent"));

            var cmds = Ini.List(kv.Get("InfoScript_Commands"));
            string[] Col(string key)
            {
                var a = Ini.ListKeepEmpty(kv.Get(key));
                if (a.Length != cmds.Length && !(a.Length == 0 || (a.Length == 1 && a[0].Length == 0))) i.ScriptArrayMismatch = true;
                return a;
            }
            var e1 = Col("InfoScript_Entities1"); var e2 = Col("InfoScript_Entities2");
            var id1 = Col("InfoScript_IDs1"); var id2 = Col("InfoScript_IDs2"); var tx = Col("InfoScript_Texts");
            string At(string[] a, int n) => n < a.Length ? a[n] : string.Empty;
            for (int n = 0; n < cmds.Length; n++)
                i.Lines.Add(new InfoLine { Command = cmds[n], Entity1 = At(e1, n), Entity2 = At(e2, n), Id1 = At(id1, n), Id2 = At(id2, n), TextId = At(tx, n) });
            return i;
        }

        /// <summary>
        /// Parse compiledinfos_&lt;world&gt;.bin (GENOMFLE): u16 1, u32 count, count × gCInfo_PS property object
        /// (accessor header as GenomeReader.ReadObject, property list, class version; the InfoScript property holds a nested
        /// gCInfoScript_PS with an array of gCInfoScriptLine_PS). Throws unless every object ends exactly at its declared size and
        /// the list ends at the string table.
        /// </summary>
        public static List<Info> ReadCompiled(byte[] data)
        {
            var g = new GenomeReader(data);
            if (!g.IsGenomFile) throw new InvalidDataException("compiledinfos: not GENOMFLE");
            var r = g.R;
            if (r.ReadUInt16() != 1) throw new InvalidDataException("compiledinfos: version");
            int count = r.ReadInt32();
            var list = new List<Info>(count);
            for (int k = 0; k < count; k++)
            {
                var o = g.ReadObject();
                if (o == null || o.ClassName != "gCInfo_PS") throw new InvalidDataException($"compiledinfos: object {k} is {o?.ClassName}");
                if (g.Position != o.End) throw new InvalidDataException($"compiledinfos: object {k} has {o.End - g.Position} extra bytes");
                list.Add(FromObject(g, o));
            }
            if (g.Position != g.PayloadEnd) throw new InvalidDataException($"compiledinfos: {g.PayloadEnd - g.Position} unread bytes");
            return list;
        }

        static Info FromObject(GenomeReader g, PropertyObject o)
        {
            var i = new Info();
            string S(string n) => o.Get(n) is Property p ? g.StrFrom(p.Value) : null;
            int I(string n) => o.Get(n) is Property p ? (p.Type.StartsWith("bTPropertyContainer") ? p.AsEnum() : p.AsInt()) : 0;
            bool B(string n) => o.Get(n) is Property p && p.AsBool();
            string[] A(string n)
            {
                if (!(o.Get(n) is Property p)) return Array.Empty<string>();
                var v = p.Value;
                int c = BitConverter.ToInt32(v, 1);
                var a = new string[c];
                bool isStr = p.Type.Contains("bCString");
                int elem = isStr ? 2 : 4;
                if (5 + c * elem != v.Length) throw new InvalidDataException($"compiledinfos: array {n} size");
                for (int x = 0; x < c; x++)
                    a[x] = isStr ? g.StrFrom(new[] { v[5 + 2 * x], v[6 + 2 * x] }) : BitConverter.ToUInt32(v, 5 + 4 * x).ToString(CultureInfo.InvariantCulture);
                return a;
            }
            i.Name = S("Name"); i.SortID = I("SortID"); i.Owner = S("Owner"); i.InfoGiven = B("InfoGiven");
            i.Parent = S("Parent"); i.Quest = S("Quest"); i.ConditionType = I("ConditionType"); i.Type = I("Type");
            i.GoldCost = I("GoldCost"); i.CondOwnerNearEntity = S("ConditionOwnerNearEntity"); i.Folder = S("Folder");
            i.CondPlayerKnows = A("ConditionPlayerKnows"); i.CondPlayerKnowsNot = A("ConditionPlayerKnowsNot");
            i.CondItemContainer = S("ConditionItemContainer"); i.CondItems = A("CondItems"); i.CondItemAmounts = A("CondItemAmounts");
            i.CondSecondaryNPC = A("CondSecondaryNPC"); i.CondSecondaryNPCStates = A("CondSecondaryNPCStatus");
            i.TeachAttrib = S("TeachAttrib"); i.TeachAttribValue = I("TeachAttribValue");
            i.ClearChildren = B("ClearChildren"); i.Permanent = B("Permanent");
            foreach (var p in o.Properties) i.Raw[p.Key] = p.Value.Type;

            var script = o.Get("InfoScript") ?? throw new InvalidDataException("compiledinfos: no InfoScript");
            // bTPropertyObject<gCInfoScript_PS>: u16 version, u32 size, property list, u16 class version.
            var sr = new Sub(g, script.Value);
            var scriptProps = sr.Object();
            // gCInfoScript_PS writes one extra string after its class version (always "" in shipped data)
            if (sr.Tail.Length != 2) throw new InvalidDataException("compiledinfos: InfoScript tail " + sr.Tail.Length);
            i.Raw["InfoScript.Tail"] = g.StrFrom(sr.Tail);
            foreach (var sp in scriptProps)
            {
                if (sp.Name != "InfoScriptLines") continue;
                var ar = new Sub(g, sp.Value);
                if (ar.R.ReadByte() != 1) throw new InvalidDataException("compiledinfos: line array flag");
                int n = ar.R.ReadInt32();
                for (int x = 0; x < n; x++)
                {
                    // bTAutoPOSmartPtr element prefix: u16 1, u8 1 (= non-null); constant in all shipped data
                    if (ar.R.ReadUInt16() != 1 || ar.R.ReadByte() != 1) throw new InvalidDataException("compiledinfos: smart pointer prefix");
                    var line = new InfoLine();
                    var lineProps = ar.Object();
                    if (ar.Tail.Length != 0) throw new InvalidDataException("compiledinfos: line tail");
                    foreach (var lp in lineProps)
                    {
                        string v = g.StrFrom(lp.Value);
                        switch (lp.Name)
                        {
                            case "Command": line.Command = v; break;
                            case "Entity1": line.Entity1 = v; break;
                            case "Entity2": line.Entity2 = v; break;
                            case "ID1": line.Id1 = v; break;
                            case "ID2": line.Id2 = v; break;
                            case "Text": line.TextId = v; break;
                        }
                    }
                    i.Lines.Add(line);
                }
                ar.End();
            }
            sr.End();
            return i;
        }

        /// <summary>Reader over a property value buffer, sharing the file's string table.</summary>
        sealed class Sub
        {
            readonly GenomeReader _g;
            public readonly BinaryReader R;
            readonly int _len;
            public Sub(GenomeReader g, byte[] v) { _g = g; _len = v.Length; R = new BinaryReader(new MemoryStream(v)); }
            string Str() => _g.StrFrom(R.ReadBytes(2));

            /// <summary>Class-specific bytes after the class version of the last Object() call.</summary>
            public byte[] Tail = Array.Empty<byte>();

            /// <summary>u16 version, u32 size, u16 30, u32 count, properties, u16 class version, class data (→ Tail).</summary>
            public List<(string Name, string Type, byte[] Value)> Object()
            {
                R.ReadUInt16();
                uint size = R.ReadUInt32();
                long end = R.BaseStream.Position + size;
                R.ReadUInt16();
                uint count = R.ReadUInt32();
                var props = new List<(string, string, byte[])>();
                for (int k = 0; k < count; k++)
                {
                    string name = Str(), type = Str();
                    R.ReadUInt16();
                    props.Add((name, type, R.ReadBytes(R.ReadInt32())));
                }
                R.ReadUInt16();
                if (R.BaseStream.Position > end) throw new InvalidDataException("compiledinfos: nested object overrun");
                Tail = R.ReadBytes((int)(end - R.BaseStream.Position));
                return props;
            }

            public void End()
            {
                if (R.BaseStream.Position != _len) throw new InvalidDataException("compiledinfos: nested value not fully read");
            }
        }
    }

    /// <summary>Minimal INI reader for the single-section .info/.quest files.</summary>
    public sealed class Ini : List<KeyValuePair<string, string>>
    {
        public string Get(string key)
        {
            foreach (var p in this) if (p.Key == key) return p.Value;
            return null;
        }

        /// <summary>Parse; the first non-empty line must be [section]. Text is cp1252 (all shipped files are pure ASCII).</summary>
        public static Ini Parse(byte[] data, string section)
        {
            var text = SingleByte.Decode(data, 0, data.Length, 1252);
            var ini = new Ini();
            bool header = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (!header)
                {
                    // Tolerate junk before the header (CP 1.75 ships FILLER47_info_G3_World_01.info as "y[Info]").
                    if (!line.EndsWith("[" + section + "]", StringComparison.Ordinal)) throw new InvalidDataException($"expected [{section}], got {line}");
                    header = true;
                    continue;
                }
                if (line.StartsWith("[")) throw new InvalidDataException("unexpected second section " + line);
                int eq = line.IndexOf('=');
                ini.Add(eq < 0 ? new KeyValuePair<string, string>(line, string.Empty)
                               : new KeyValuePair<string, string>(line.Substring(0, eq), line.Substring(eq + 1)));
            }
            if (!header) throw new InvalidDataException("empty file");
            return ini;
        }

        public static int Int(string s) => string.IsNullOrEmpty(s) ? 0 : int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        public static bool Bool(string s) => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        public static string[] List(string s) => string.IsNullOrEmpty(s) ? Array.Empty<string>() : s.Split(';');
        public static string[] ListKeepEmpty(string s) => s == null ? Array.Empty<string>() : s.Split(';');
    }
}
