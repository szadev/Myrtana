using System;
using System.IO;
using G3.Formats.Image;

namespace G3.Formats.Gui
{
    /// <summary>
    /// Plain DirectDraw Surface as used by gui.pak (all 77 atlases: DXT3, one mip level, 1024² or 2048²).
    /// Only the top level is read; DXT1/3/5 and uncompressed 32-bit are recognised.
    /// </summary>
    public sealed class DdsImage
    {
        public int Width, Height, MipCount;
        public PixelFormat Format;
        /// <summary>Top mip level, still compressed.</summary>
        public byte[] Level0;

        public static DdsImage Read(byte[] data)
        {
            if (data.Length < 128 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ')
                throw new InvalidDataException("not a DDS file");
            var d = new DdsImage
            {
                Height = BitConverter.ToInt32(data, 12),
                Width = BitConverter.ToInt32(data, 16),
                MipCount = Math.Max(1, BitConverter.ToInt32(data, 28)),
            };
            uint pfFlags = BitConverter.ToUInt32(data, 80);
            uint fourCC = BitConverter.ToUInt32(data, 84);
            if ((pfFlags & 4) != 0)
            {
                switch (fourCC)
                {
                    case 0x31545844: d.Format = PixelFormat.DXT1; break;
                    case 0x33545844: d.Format = PixelFormat.DXT3; break;
                    case 0x35545844: d.Format = PixelFormat.DXT5; break;
                    default: throw new NotSupportedException($"DDS fourCC 0x{fourCC:X8}");
                }
            }
            else if (BitConverter.ToInt32(data, 88) == 32) d.Format = PixelFormat.BGRA32;
            else throw new NotSupportedException("DDS pixel format");
            int size = LevelSize(d.Format, d.Width, d.Height);
            if (128 + size > data.Length) throw new InvalidDataException("DDS truncated");
            d.Level0 = new byte[size];
            Buffer.BlockCopy(data, 128, d.Level0, 0, size);
            return d;
        }

        public static int LevelSize(PixelFormat f, int w, int h)
        {
            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            switch (f)
            {
                case PixelFormat.DXT1: return bw * bh * 8;
                case PixelFormat.DXT3:
                case PixelFormat.DXT5: return bw * bh * 16;
                default: return w * h * 4;
            }
        }

        /// <summary>RGBA32, top row first. DXT3 and BGRA32 only (the formats gui.pak uses).</summary>
        public byte[] ToRgba()
        {
            switch (Format)
            {
                case PixelFormat.DXT3: return Dxt.DecodeDxt3(Level0, Width, Height);
                case PixelFormat.BGRA32:
                {
                    var o = new byte[Level0.Length];
                    for (int i = 0; i < o.Length; i += 4)
                    { o[i] = Level0[i + 2]; o[i + 1] = Level0[i + 1]; o[i + 2] = Level0[i]; o[i + 3] = Level0[i + 3]; }
                    return o;
                }
                default: throw new NotSupportedException($"DdsImage.ToRgba: {Format}");
            }
        }

        /// <summary>Copy a sub-rectangle out of an RGBA32 image.</summary>
        public static byte[] Crop(byte[] rgba, int width, GuiRect r)
        {
            var o = new byte[r.Width * r.Height * 4];
            for (int y = 0; y < r.Height; y++)
                Buffer.BlockCopy(rgba, ((r.Top + y) * width + r.Left) * 4, o, y * r.Width * 4, r.Width * 4);
            return o;
        }
    }
}
