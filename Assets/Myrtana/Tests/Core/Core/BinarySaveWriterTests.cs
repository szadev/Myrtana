using System;
using System.IO;
using System.Text;
using Myrtana.Core;
using NUnit.Framework;

namespace Myrtana.Tests
{
    /// <summary>fix-171: the buffered save writer writes exactly the bytes of the BinaryWriter one (saves stay compatible).</summary>
    public class BinarySaveWriterTests
    {
        static void Values(ISaveWriter w, Action flush)
        {
            w.WriteBool(true); w.WriteBool(false);
            w.WriteInt32(0); w.WriteInt32(-1); w.WriteInt32(int.MaxValue); w.WriteInt32(123456789);
            w.WriteUInt64(0); w.WriteUInt64(ulong.MaxValue); w.WriteUInt64(0x0123456789ABCDEFul);
            w.WriteDouble(0); w.WriteDouble(-1.5); w.WriteDouble(double.NaN); w.WriteDouble(1e300);
            w.WriteSingle(0); w.WriteSingle(-2.25f); w.WriteSingle(float.PositiveInfinity); w.WriteSingle(3.4e38f);
            foreach (var s in new[]
            {
                null, "", "a", "Orc_Warrior_04#bf9347b2", "Привет, Миртана", "ä", new string('x', 127), new string('y', 128),
                new string('z', 300) + "Ж", new string('ё', 200), "😀 emoji", "lone \uD800 surrogate",
            })
                w.WriteString(s);
            for (int i = 0; i < 5000; i++) { w.WriteString("NPC_" + i); w.WriteInt32(i); w.WriteSingle(i * 0.25f); }
            flush();
        }

        sealed class Reference : ISaveWriter
        {
            readonly BinaryWriter _w;
            public Reference(Stream o) => _w = new BinaryWriter(o, Encoding.UTF8, leaveOpen: true);
            public void WriteBool(bool v) => _w.Write(v);
            public void WriteInt32(int v) => _w.Write(v);
            public void WriteUInt64(ulong v) => _w.Write(v);
            public void WriteDouble(double v) => _w.Write(v);
            public void WriteSingle(float v) => _w.Write(v);
            public void WriteString(string v) { _w.Write(v != null); if (v != null) _w.Write(v); }
            public void Flush() => _w.Flush();
        }

        [Test]
        public void Buffered_And_Unbuffered_WriteTheBinaryWriterBytes()
        {
            var expected = new MemoryStream(); var r = new Reference(expected); Values(r, r.Flush);
            var buffered = new MemoryStream(); var b = new BinarySaveWriter(buffered, buffered: true); Values(b, b.Flush);
            var direct = new MemoryStream(); var d = new BinarySaveWriter(direct); Values(d, () => { });
            Assert.That(buffered.ToArray(), Is.EqualTo(expected.ToArray()));
            Assert.That(direct.ToArray(), Is.EqualTo(expected.ToArray()), "unbuffered: every value is in the stream at once");
            // and the reader reads it back
            var rd = new BinarySaveReader(new MemoryStream(buffered.ToArray()));
            Assert.That(rd.ReadBool(), Is.True);
        }
    }
}
