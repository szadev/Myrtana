using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace G3.Formats.Genome
{
    /// <summary>
    /// Reader over a Genome resource file. Handles both containers (Docs/formats-mesh-material-texture.md §1):
    /// plain archives with inline strings, and GENOMFLE files whose strings are u16 indices into a trailing table.
    /// </summary>
    public sealed class GenomeReader
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public readonly BinaryReader R;
        public readonly byte[] Data;
        readonly string[] _table;

        /// <summary>Offset where the payload starts (0 for plain archives, 14 for GENOMFLE).</summary>
        public readonly int PayloadStart;
        /// <summary>Offset where the payload ends (file end or the GENOMFLE string table).</summary>
        public readonly int PayloadEnd;

        public bool IsGenomFile => _table != null;

        public GenomeReader(byte[] data)
        {
            Data = data;
            R = new BinaryReader(new MemoryStream(data, false), Latin1);
            PayloadEnd = data.Length;
            if (data.Length >= 14 && Encoding.ASCII.GetString(data, 0, 8) == "GENOMFLE")
            {
                int tableOffset = BitConverter.ToInt32(data, 10);
                PayloadStart = 14;
                PayloadEnd = tableOffset;
                R.BaseStream.Position = tableOffset;
                if (R.ReadUInt32() != 0xDEADBEEF) throw new InvalidDataException("GENOMFLE: string table magic mismatch");
                if (R.ReadByte() != 0)
                {
                    int count = R.ReadInt32();
                    _table = new string[count];
                    for (int i = 0; i < count; i++)
                        _table[i] = Latin1.GetString(R.ReadBytes(R.ReadUInt16()));
                }
                else _table = Array.Empty<string>();
            }
            R.BaseStream.Position = PayloadStart;
        }

        public long Position
        {
            get => R.BaseStream.Position;
            set => R.BaseStream.Position = value;
        }

        public void Skip(long n) => R.BaseStream.Position += n;

        public string Str()
        {
            if (_table != null) return _table[R.ReadUInt16()];
            int len = R.ReadUInt16();
            return len == 0 ? string.Empty : Latin1.GetString(R.ReadBytes(len));
        }

        /// <summary>Decode a string stored inside a property value buffer using this file's string mode.</summary>
        public string StrFrom(byte[] value)
        {
            if (value == null || value.Length < 2) return string.Empty;
            if (_table != null) return _table[BitConverter.ToUInt16(value, 0)];
            int len = BitConverter.ToUInt16(value, 0);
            return Latin1.GetString(value, 2, Math.Min(len, value.Length - 2));
        }

        public Guid20 Guid() => new Guid20(R.ReadBytes(16), R.ReadUInt32());

        /// <summary>
        /// Reads a bCAccessorPropertyObject header + property list + class version (§2).
        /// Returns null when the accessor holds no object.
        /// </summary>
        public PropertyObject ReadObject()
        {
            R.ReadUInt16();
            if (R.ReadByte() == 0) return null;
            R.ReadUInt16(); R.ReadByte();
            var o = new PropertyObject { ClassName = Str() };
            R.ReadUInt16(); R.ReadByte(); R.ReadUInt16();
            o.ObjVersion = R.ReadUInt16();
            uint size = R.ReadUInt32();
            o.End = Position + size;
            if (o.ObjVersion == 1) o.ObjectName = Str();
            if (o.ObjVersion <= 0x51) Skip(20);
            ReadProperties(o.Properties);
            o.ClassVersion = R.ReadUInt16();
            return o;
        }

        /// <summary>Property-set block: u16 version (30), u32 count, count × property.</summary>
        public void ReadProperties(Dictionary<string, Property> into)
        {
            R.ReadUInt16();
            uint count = R.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                string name = Str();
                string type = Str();
                R.ReadUInt16();
                int size = R.ReadInt32();
                into[name] = new Property { Type = type, Value = R.ReadBytes(size) };
            }
        }

        /// <summary>eCResourceBase_PS tail (§2b), read right after the class version.</summary>
        public void ReadResourceTail()
        {
            ushort v = R.ReadUInt16();
            if (v >= 0x17) R.ReadUInt32();
            if (v < 0x1E && R.ReadUInt16() > 1) R.ReadByte();
        }

        /// <summary>bTArray prefix: u8 (always 1) + u32 count.</summary>
        public int ArrayCount()
        {
            R.ReadByte();
            return R.ReadInt32();
        }
    }

    public sealed class Property
    {
        public string Type;
        public byte[] Value;

        public float AsFloat() => BitConverter.ToSingle(Value, 0);
        public int AsInt() => Value.Length >= 4 ? BitConverter.ToInt32(Value, 0) : Value[0];
        public bool AsBool() => Value[0] != 0;
        /// <summary>bTPropertyContainer&lt;enum&gt;: u16 version + u32 value.</summary>
        public int AsEnum() => Value.Length >= 6 ? BitConverter.ToInt32(Value, 2) : AsInt();
    }

    public sealed class PropertyObject
    {
        public string ClassName;
        public string ObjectName;
        public ushort ObjVersion;
        public ushort ClassVersion;
        /// <summary>Absolute stream offset of the object's end; always seek here after parsing.</summary>
        public long End;
        public readonly Dictionary<string, Property> Properties = new Dictionary<string, Property>(StringComparer.Ordinal);

        public Property Get(string name) => Properties.TryGetValue(name, out var p) ? p : null;
    }

    /// <summary>bCGuid: 16 bytes + u32 validity flag (only the low byte is meaningful).</summary>
    public readonly struct Guid20 : IEquatable<Guid20>
    {
        public readonly Guid Value;
        public readonly bool Valid;

        public Guid20(byte[] bytes, uint valid)
        {
            Value = new Guid(bytes);
            Valid = (valid & 0xFF) != 0;
        }

        public bool Equals(Guid20 other) => Value == other.Value;
        public override bool Equals(object obj) => obj is Guid20 g && Equals(g);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Valid ? Value.ToString() : "(invalid)";
    }
}
