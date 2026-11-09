using System;
using System.IO;
using System.Text;

namespace Myrtana.Core
{
    /// <summary>
    /// <see cref="ISaveWriter"/> in <see cref="BinaryWriter"/>'s little-endian format (bool 1 byte, int 4, ulong/double 8,
    /// float 4, string = 7-bit encoded UTF-8 byte count + the bytes; a null flag before every string). fix-171: the values are
    /// encoded into an own byte buffer instead of one <see cref="Stream.Write(byte[], int, int)"/> per value (the recovery
    /// snapshot writes ~100 000 values every 2 minutes: the per-call stream and encoder overhead was most of its 18 ms in the
    /// player). <paramref name="buffered"/> false (the default) hands every value to the stream at once, as BinaryWriter did
    /// (callers that read the stream back without <see cref="Flush"/>); true keeps them until <see cref="Flush"/>
    /// (<see cref="SaveFile.Write(Stream, SaveHeader, System.Collections.Generic.IEnumerable{ISaveParticipant})"/>).
    /// </summary>
    public sealed class BinarySaveWriter : ISaveWriter
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        readonly Stream _out;
        readonly bool _buffered;
        byte[] _buf;
        int _n;

        public BinarySaveWriter(Stream output, bool buffered = false)
        {
            _out = output ?? throw new ArgumentNullException(nameof(output));
            _buffered = buffered;
            _buf = new byte[buffered ? 16 * 1024 : 256];
        }

        void Ensure(int count)
        {
            if (_n + count <= _buf.Length) return;
            Drain();
            if (count > _buf.Length)
            {
                int size = _buf.Length;
                while (size < count) size *= 2;
                _buf = new byte[size];
            }
        }

        void Drain()
        {
            if (_n == 0) return;
            _out.Write(_buf, 0, _n);
            _n = 0;
        }

        void Done()
        {
            if (!_buffered) Drain();
        }

        public void WriteBool(bool value)
        {
            Ensure(1);
            _buf[_n++] = value ? (byte)1 : (byte)0;
            Done();
        }

        public void WriteInt32(int value)
        {
            Ensure(4);
            Put32((uint)value);
            Done();
        }

        public void WriteUInt64(ulong value)
        {
            Ensure(8);
            Put32((uint)value);
            Put32((uint)(value >> 32));
            Done();
        }

        public void WriteDouble(double value) => WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(value));

        public void WriteSingle(float value)
        {
            Ensure(4);
            Put32(new FloatBits { F = value }.U);
            Done();
        }

        void Put32(uint v)
        {
            var b = _buf;
            int n = _n;
            b[n] = (byte)v;
            b[n + 1] = (byte)(v >> 8);
            b[n + 2] = (byte)(v >> 16);
            b[n + 3] = (byte)(v >> 24);
            _n = n + 4;
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteBool(false);
                return;
            }
            int len = value.Length;
            // ASCII (nearly every id): one byte per char, copied while checking; anything else is encoded as UTF-8 again
            if (len < 128)
            {
                Ensure(2 + len);
                var b = _buf;
                int n = _n;
                b[n] = 1;
                b[n + 1] = (byte)len;
                int at = n + 2;
                bool ascii = true;
                for (int i = 0; i < len; i++)
                {
                    char ch = value[i];
                    if (ch >= 0x80) { ascii = false; break; }
                    b[at + i] = (byte)ch;
                }
                if (ascii)
                {
                    _n = at + len;
                    Done();
                    return;
                }
            }
            int bytes = Utf8.GetByteCount(value);
            Ensure(1 + 5 + bytes);
            _buf[_n++] = 1;
            uint v = (uint)bytes;
            while (v >= 0x80) { _buf[_n++] = (byte)(v | 0x80); v >>= 7; }
            _buf[_n++] = (byte)v;
            _n += Utf8.GetBytes(value, 0, len, _buf, _n);
            Done();
        }

        public void Flush()
        {
            Drain();
            _out.Flush();
        }
    }

    /// <summary>The bits of a float (BitConverter.SingleToInt32Bits is not in every Mono profile).</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    struct FloatBits
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float F;
        [System.Runtime.InteropServices.FieldOffset(0)] public uint U;
    }

    public sealed class BinarySaveReader : ISaveReader
    {
        readonly BinaryReader _r;

        public BinarySaveReader(Stream input) => _r = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        public bool ReadBool() => _r.ReadBoolean();
        public int ReadInt32() => _r.ReadInt32();
        public ulong ReadUInt64() => _r.ReadUInt64();
        public double ReadDouble() => _r.ReadDouble();
        public float ReadSingle() => _r.ReadSingle();
        public string ReadString() => _r.ReadBoolean() ? _r.ReadString() : null;
    }
}
