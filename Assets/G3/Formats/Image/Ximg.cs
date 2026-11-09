using System;
using System.IO;
using System.Text;

namespace G3.Formats.Image
{
    public enum PixelFormat { DXT1, DXT3, DXT5, BGRA32 }

    public sealed class ImageResource
    {
        public int Width, Height;
        public PixelFormat Format;
        public bool IsCubemap;
        /// <summary>Mip levels, largest first (the file stores them smallest first). Cubemaps: face 0 only.</summary>
        public byte[][] Mips;

        /// <summary>All mips concatenated largest-first, as Texture2D.LoadRawTextureData expects.</summary>
        public byte[] RawData()
        {
            int total = 0;
            foreach (var m in Mips) total += m.Length;
            var buf = new byte[total];
            int o = 0;
            foreach (var m in Mips) { Buffer.BlockCopy(m, 0, buf, o, m.Length); o += m.Length; }
            return buf;
        }
    }

    /// <summary>.ximg — GENOMFLE + fixed "G3IMG" header, DXT payload (Docs/formats-mesh-material-texture.md §5).</summary>
    public static class Ximg
    {
        const int PixelStart = 0x57;

        public static ImageResource Read(byte[] data)
        {
            if (data.Length < PixelStart || Encoding.ASCII.GetString(data, 0, 8) != "GENOMFLE"
                || Encoding.ASCII.GetString(data, 0x26, 5) != "G3IMG")
                throw new InvalidDataException("not a G3IMG file");

            int end = BitConverter.ToInt32(data, 0x0A);
            var img = new ImageResource
            {
                Width = BitConverter.ToInt32(data, 0x2F),
                Height = BitConverter.ToInt32(data, 0x33),
                IsCubemap = data[0x3B] != 0,
            };
            int mipCount = Math.Max(1, BitConverter.ToInt32(data, 0x3F));
            uint fourCC = BitConverter.ToUInt32(data, 0x47);
            switch (fourCC)
            {
                case 0x31545844: img.Format = PixelFormat.DXT1; break; // "DXT1"
                case 0x33545844: img.Format = PixelFormat.DXT3; break;
                case 0x35545844: img.Format = PixelFormat.DXT5; break;
                case 21: img.Format = PixelFormat.BGRA32; break;        // D3DFMT_A8R8G8B8
                default: throw new NotSupportedException($"ximg format 0x{fourCC:X8}");
            }

            // Stored smallest first; the largest level ends at the string table (face 0 for cubemaps is the first chain).
            int chain = 0;
            for (int i = 0; i < mipCount; i++) chain += LevelSize(img, i);
            int pos = img.IsCubemap ? PixelStart + chain : end;
            img.Mips = new byte[mipCount][];
            for (int i = 0; i < mipCount; i++)
            {
                int size = LevelSize(img, i);
                pos -= size;
                img.Mips[i] = new byte[size];
                Buffer.BlockCopy(data, pos, img.Mips[i], 0, size);
            }
            return img;
        }

        public static int LevelSize(ImageResource img, int level)
        {
            int w = Math.Max(1, img.Width >> level), h = Math.Max(1, img.Height >> level);
            if (img.Format == PixelFormat.BGRA32) return w * h * 4;
            int blocks = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
            return blocks * (img.Format == PixelFormat.DXT1 ? 8 : 16);
        }
    }

    public static class Dxt
    {
        /// <summary>
        /// DXT1/3/5 or BGRA32 level → RGBA32, rows as stored (top row first for G3 images). Integer palette interpolation
        /// (c0·2/3 + c1/3 truncated); DXT1 blocks with c0 &lt;= c1 have the transparent black fourth colour.
        /// </summary>
        public static byte[] DecodeRgba(byte[] src, int width, int height, PixelFormat fmt)
        {
            if (fmt == PixelFormat.DXT3) return Dxt.DecodeDxt3(src, width, height);
            var dst = new byte[width * height * 4];
            if (fmt == PixelFormat.BGRA32)
            {
                for (int i = 0; i < dst.Length; i += 4) { dst[i] = src[i + 2]; dst[i + 1] = src[i + 1]; dst[i + 2] = src[i]; dst[i + 3] = src[i + 3]; }
                return dst;
            }
            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4), bs = fmt == PixelFormat.DXT1 ? 8 : 16;
            var alpha = new byte[16];
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int o = (by * bw + bx) * bs;
                if (fmt == PixelFormat.DXT5)
                {
                    byte a0 = src[o], a1 = src[o + 1];
                    var pal = new byte[8]; pal[0] = a0; pal[1] = a1;
                    if (a0 > a1) for (int i = 1; i < 7; i++) pal[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                    else { for (int i = 1; i < 5; i++) pal[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5); pal[6] = 0; pal[7] = 255; }
                    ulong bits = 0;
                    for (int i = 0; i < 6; i++) bits |= (ulong)src[o + 2 + i] << (8 * i);
                    for (int i = 0; i < 16; i++) alpha[i] = pal[(bits >> (3 * i)) & 7];
                    o += 8;
                }
                ushort c0 = (ushort)(src[o] | src[o + 1] << 8), c1 = (ushort)(src[o + 2] | src[o + 3] << 8);
                var c = new byte[16];
                Rgb565(c0, c, 0); Rgb565(c1, c, 4);
                bool four = c0 > c1 || fmt != PixelFormat.DXT1;
                for (int k = 0; k < 3; k++)
                {
                    if (four) { c[8 + k] = (byte)((2 * c[k] + c[4 + k]) / 3); c[12 + k] = (byte)((c[k] + 2 * c[4 + k]) / 3); }
                    else { c[8 + k] = (byte)((c[k] + c[4 + k]) / 2); c[12 + k] = 0; }
                }
                c[3] = c[7] = c[11] = 255; c[15] = (byte)(four ? 255 : 0);
                uint idx = (uint)(src[o + 4] | src[o + 5] << 8 | src[o + 6] << 16 | src[o + 7] << 24);
                for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px, y = by * 4 + py;
                    if (x >= width || y >= height) continue;
                    int i = py * 4 + px, s = (int)((idx >> (2 * i)) & 3) * 4, d = (y * width + x) * 4;
                    dst[d] = c[s]; dst[d + 1] = c[s + 1]; dst[d + 2] = c[s + 2];
                    dst[d + 3] = fmt == PixelFormat.DXT5 ? alpha[i] : c[s + 3];
                }
            }
            return dst;
        }

        static void Rgb565(ushort v, byte[] d, int o)
        {
            int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
            d[o] = (byte)(r << 3 | r >> 2); d[o + 1] = (byte)(g << 2 | g >> 4); d[o + 2] = (byte)(b << 3 | b >> 2);
        }

        /// <summary>Decode a DXT3 (BC2) level to RGBA32 (Unity has no BC2 TextureFormat).</summary>
        public static byte[] DecodeDxt3(byte[] src, int width, int height)
        {
            var dst = new byte[width * height * 4];
            var colors = new byte[16];
            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = (by * bw + bx) * 16;
                ulong alpha = BitConverter.ToUInt64(src, b);
                ushort c0 = BitConverter.ToUInt16(src, b + 8), c1 = BitConverter.ToUInt16(src, b + 10);
                uint bits = BitConverter.ToUInt32(src, b + 12);
                Expand565(c0, colors, 0);
                Expand565(c1, colors, 4);
                for (int k = 0; k < 3; k++)
                {
                    colors[8 + k] = (byte)((2 * colors[k] + colors[4 + k]) / 3);
                    colors[12 + k] = (byte)((colors[k] + 2 * colors[4 + k]) / 3);
                }
                for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px, y = by * 4 + py;
                    if (x >= width || y >= height) continue;
                    int i = py * 4 + px;
                    int ci = (int)((bits >> (2 * i)) & 3) * 4;
                    int o = (y * width + x) * 4;
                    dst[o] = colors[ci];
                    dst[o + 1] = colors[ci + 1];
                    dst[o + 2] = colors[ci + 2];
                    dst[o + 3] = (byte)(((alpha >> (4 * i)) & 0xF) * 17);
                }
            }
            return dst;
        }

        static void Expand565(ushort c, byte[] dst, int o)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            dst[o] = (byte)((r << 3) | (r >> 2));
            dst[o + 1] = (byte)((g << 2) | (g >> 4));
            dst[o + 2] = (byte)((b << 3) | (b >> 2));
        }

        /// <summary>
        /// Convert a Genome DXT5nm normal map (R=0, X in alpha, Y in green, D3D "Y down") in place into what Unity's
        /// UnpackNormalmapRGorAG expects: R forced to 1 (so x = r·a = a) and green inverted (Y up).
        /// BC3 colour blocks are always 4-colour mode, so editing the endpoints is safe.
        /// </summary>
        public static void FixNormalDxt5(byte[] data, bool flipGreen)
        {
            for (int b = 0; b + 16 <= data.Length; b += 16)
            for (int e = 8; e <= 10; e += 2)
            {
                int c = data[b + e] | (data[b + e + 1] << 8);
                c |= 0xF800;
                if (flipGreen) c = (c & ~0x07E0) | ((63 - ((c >> 5) & 63)) << 5);
                data[b + e] = (byte)c;
                data[b + e + 1] = (byte)(c >> 8);
            }
        }
    }
}
