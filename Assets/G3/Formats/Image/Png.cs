using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace G3.Formats.Image
{
    /// <summary>
    /// Minimal PNG for RGBA8 images (pure C#, netstandard: zlib framing written by hand around DeflateStream).
    /// <see cref="Encode"/> writes filter 0 rows; <see cref="Decode"/> reads 8-bit RGB/RGBA/grey(+alpha) PNGs with any
    /// row filter, non-interlaced (what Unity's EncodeToPNG and this encoder write). Rows top first, as in the file.
    /// </summary>
    public static class Png
    {
        static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            if (rgba.Length < width * height * 4) throw new ArgumentException("rgba too short");
            var raw = new MemoryStream();
            raw.WriteByte(0x78); raw.WriteByte(0x9C); // zlib header: deflate, 32 K window, default level
            uint a = 1, b = 0;
            using (var z = new DeflateStream(raw, CompressionLevel.Optimal, true))
            {
                var row = new byte[width * 4 + 1];
                for (int y = 0; y < height; y++)
                {
                    row[0] = 0;
                    Buffer.BlockCopy(rgba, y * width * 4, row, 1, width * 4);
                    z.Write(row, 0, row.Length);
                    foreach (var x in row) { a = (a + x) % 65521; b = (b + a) % 65521; }
                }
            }
            uint adler = (b << 16) | a;
            raw.WriteByte((byte)(adler >> 24)); raw.WriteByte((byte)(adler >> 16)); raw.WriteByte((byte)(adler >> 8)); raw.WriteByte((byte)adler);

            var f = new MemoryStream();
            f.Write(Signature, 0, Signature.Length);
            var hdr = new byte[13];
            Be(hdr, 0, (uint)width); Be(hdr, 4, (uint)height); hdr[8] = 8; hdr[9] = 6;
            Chunk(f, "IHDR", hdr);
            Chunk(f, "IDAT", raw.ToArray());
            Chunk(f, "IEND", Array.Empty<byte>());
            return f.ToArray();
        }

        /// <summary>RGBA8 pixels (top row first) of an 8-bit PNG.</summary>
        public static byte[] Decode(byte[] png, out int width, out int height)
        {
            for (int i = 0; i < Signature.Length; i++)
                if (png.Length <= i || png[i] != Signature[i]) throw new InvalidDataException("not a PNG");
            int pos = 8, colorType = 6, depth = 8, interlace = 0;
            width = height = 0;
            var idat = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                int len = (int)ReadBe(png, pos);
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                int body = pos + 8;
                if (type == "IHDR")
                {
                    width = (int)ReadBe(png, body); height = (int)ReadBe(png, body + 4);
                    depth = png[body + 8]; colorType = png[body + 9]; interlace = png[body + 12];
                }
                else if (type == "IDAT") idat.Write(png, body, len);
                else if (type == "IEND") break;
                pos = body + len + 4;
            }
            if (depth != 8 || interlace != 0) throw new NotSupportedException($"PNG depth {depth}, interlace {interlace}");
            int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG colour type {colorType}") };
            var zdata = idat.ToArray();
            var inflated = new MemoryStream();
            using (var z = new DeflateStream(new MemoryStream(zdata, 2, zdata.Length - 2), CompressionMode.Decompress))
                z.CopyTo(inflated);
            var data = inflated.ToArray();
            int stride = width * channels;
            var cur = new byte[stride];
            var prev = new byte[stride];
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int o = y * (stride + 1);
                byte filter = data[o];
                Buffer.BlockCopy(data, o + 1, cur, 0, stride);
                for (int x = 0; x < stride; x++)
                {
                    int left = x >= channels ? cur[x - channels] : 0, up = prev[x], ul = x >= channels ? prev[x - channels] : 0;
                    int v = cur[x];
                    switch (filter)
                    {
                        case 1: v += left; break;
                        case 2: v += up; break;
                        case 3: v += (left + up) >> 1; break;
                        case 4: v += Paeth(left, up, ul); break;
                    }
                    cur[x] = (byte)v;
                }
                for (int x = 0; x < width; x++)
                {
                    int d = (y * width + x) * 4, s = x * channels;
                    switch (channels)
                    {
                        case 1: rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s]; rgba[d + 3] = 255; break;
                        case 2: rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s]; rgba[d + 3] = cur[s + 1]; break;
                        case 3: rgba[d] = cur[s]; rgba[d + 1] = cur[s + 1]; rgba[d + 2] = cur[s + 2]; rgba[d + 3] = 255; break;
                        default: Buffer.BlockCopy(cur, s, rgba, d, 4); break;
                    }
                }
                var t = prev; prev = cur; cur = t;
            }
            return rgba;
        }

        /// <summary>The same image upside down (bottom row first).</summary>
        public static byte[] FlipRows(byte[] rgba, int width, int height)
        {
            var r = new byte[rgba.Length];
            int stride = width * 4;
            for (int y = 0; y < height; y++) Buffer.BlockCopy(rgba, y * stride, r, (height - 1 - y) * stride, stride);
            return r;
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        static void Chunk(Stream s, string type, byte[] body)
        {
            var len = new byte[4]; Be(len, 0, (uint)body.Length); s.Write(len, 0, 4);
            var tb = Encoding.ASCII.GetBytes(type); s.Write(tb, 0, 4); s.Write(body, 0, body.Length);
            uint crc = Crc(Crc(0xFFFFFFFF, tb), body) ^ 0xFFFFFFFF;
            var cb = new byte[4]; Be(cb, 0, crc); s.Write(cb, 0, 4);
        }

        static uint[] _crcTable;

        static uint Crc(uint c, byte[] b)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++) { uint k = n; for (int i = 0; i < 8; i++) k = (k & 1) != 0 ? 0xEDB88320 ^ (k >> 1) : k >> 1; t[n] = k; }
                _crcTable = t;
            }
            foreach (var x in b) c = _crcTable[(c ^ x) & 0xFF] ^ (c >> 8);
            return c;
        }

        static void Be(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
        static uint ReadBe(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    }
}
