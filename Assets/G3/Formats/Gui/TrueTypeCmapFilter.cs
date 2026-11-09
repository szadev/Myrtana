using System;
using System.Collections.Generic;

namespace G3.Formats.Gui
{
    /// <summary>
    /// Removes code points from the character map of a TrueType font. Gothic3.ttf maps Cyrillic (U+0400–U+04FF) to
    /// Greek-like placeholder outlines; without those entries a text engine takes Russian from its fallback fonts, as
    /// the original engine's GDI font linking does. Only the cmap changes: the result has a single Windows Unicode BMP
    /// (3,1) format 4 subtable, all other tables are copied unchanged.
    /// </summary>
    public static class TrueTypeCmapFilter
    {
        /// <summary>Copy of <paramref name="ttf"/> without the code points for which <paramref name="exclude"/> is true.</summary>
        public static byte[] RemoveCodePoints(byte[] ttf, Func<int, bool> exclude)
        {
            var tables = ReadDirectory(ttf);
            if (!tables.TryGetValue("cmap", out var cmap)) throw new InvalidOperationException("font has no cmap table");
            var map = ReadUnicodeMap(ttf, cmap.offset);
            var kept = new SortedDictionary<int, int>();
            foreach (var kv in map)
                if (!exclude(kv.Key)) kept[kv.Key] = kv.Value;
            return Rebuild(ttf, tables, "cmap", WriteCmap(kept));
        }

        /// <summary>Unicode BMP code point → glyph index from the (3,1) or (0,x) format 4 subtable.</summary>
        public static Dictionary<int, int> ReadUnicodeMap(byte[] ttf, int cmapOffset)
        {
            int count = U16(ttf, cmapOffset + 2);
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                int rec = cmapOffset + 4 + i * 8;
                int platform = U16(ttf, rec), encoding = U16(ttf, rec + 2);
                int sub = cmapOffset + (int)U32(ttf, rec + 4);
                if (U16(ttf, sub) != 4) continue;
                bool windowsBmp = platform == 3 && encoding == 1;
                if (windowsBmp || (platform == 0 && best < 0)) best = sub;
                if (windowsBmp) break;
            }
            if (best < 0) throw new InvalidOperationException("font has no Unicode format 4 cmap");

            var map = new Dictionary<int, int>();
            int segCount = U16(ttf, best + 6) / 2;
            int ends = best + 14, starts = ends + segCount * 2 + 2, deltas = starts + segCount * 2, ranges = deltas + segCount * 2;
            for (int s = 0; s < segCount; s++)
            {
                int end = U16(ttf, ends + s * 2), start = U16(ttf, starts + s * 2);
                int delta = U16(ttf, deltas + s * 2), rangeOffset = U16(ttf, ranges + s * 2);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (rangeOffset == 0) glyph = (c + delta) & 0xFFFF;
                    else
                    {
                        glyph = U16(ttf, ranges + s * 2 + rangeOffset + (c - start) * 2);
                        if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    }
                    if (glyph != 0) map[c] = glyph;
                }
            }
            return map;
        }

        static Dictionary<string, (int offset, int length)> ReadDirectory(byte[] ttf)
        {
            int count = U16(ttf, 4);
            var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int rec = 12 + i * 16;
                string tag = System.Text.Encoding.ASCII.GetString(ttf, rec, 4);
                tables[tag] = ((int)U32(ttf, rec + 8), (int)U32(ttf, rec + 12));
            }
            return tables;
        }

        /// <summary>cmap with one (3,1) format 4 subtable: one delta segment per run of consecutive codes and glyphs.</summary>
        static byte[] WriteCmap(SortedDictionary<int, int> map)
        {
            var segs = new List<(int start, int end, int delta)>();
            foreach (var kv in map)
            {
                int delta = (kv.Value - kv.Key) & 0xFFFF;
                if (segs.Count > 0 && segs[^1].end == kv.Key - 1 && segs[^1].delta == delta)
                    segs[^1] = (segs[^1].start, kv.Key, delta);
                else segs.Add((kv.Key, kv.Key, delta));
            }
            segs.Add((0xFFFF, 0xFFFF, 1));

            int segCount = segs.Count;
            int subLength = 16 + segCount * 8;
            var b = new byte[12 + subLength];
            W16(b, 0, 0); W16(b, 2, 1);
            W16(b, 4, 3); W16(b, 6, 1); W32(b, 8, 12);
            int p = 12;
            int pow = 1, log = 0;
            while (pow * 2 <= segCount) { pow *= 2; log++; }
            W16(b, p, 4); W16(b, p + 2, subLength); W16(b, p + 4, 0);
            W16(b, p + 6, segCount * 2); W16(b, p + 8, pow * 2); W16(b, p + 10, log); W16(b, p + 12, segCount * 2 - pow * 2);
            int ends = p + 14, starts = ends + segCount * 2 + 2, deltas = starts + segCount * 2, ranges = deltas + segCount * 2;
            for (int s = 0; s < segCount; s++)
            {
                W16(b, ends + s * 2, segs[s].end);
                W16(b, starts + s * 2, segs[s].start);
                W16(b, deltas + s * 2, segs[s].delta);
                W16(b, ranges + s * 2, 0);
            }
            return b;
        }

        /// <summary>The font with table <paramref name="tag"/> replaced; offsets, checksums and head.checkSumAdjustment recomputed.</summary>
        static byte[] Rebuild(byte[] ttf, Dictionary<string, (int offset, int length)> tables, string tag, byte[] replacement)
        {
            int count = U16(ttf, 4);
            var tags = new List<string>();
            for (int i = 0; i < count; i++) tags.Add(System.Text.Encoding.ASCII.GetString(ttf, 12 + i * 16, 4));

            int size = 12 + count * 16;
            foreach (var t in tags) size += Pad4(t == tag ? replacement.Length : tables[t].length);
            var o = new byte[size];
            Buffer.BlockCopy(ttf, 0, o, 0, 12);
            int data = 12 + count * 16, headOffset = -1;
            for (int i = 0; i < count; i++)
            {
                string t = tags[i];
                int length = t == tag ? replacement.Length : tables[t].length;
                if (t == tag) Buffer.BlockCopy(replacement, 0, o, data, length);
                else Buffer.BlockCopy(ttf, tables[t].offset, o, data, length);
                if (t == "head") { headOffset = data; W32(o, data + 8, 0); }
                int rec = 12 + i * 16;
                Buffer.BlockCopy(ttf, 12 + i * 16, o, rec, 4);
                W32(o, rec + 4, Checksum(o, data, length));
                W32(o, rec + 8, (uint)data);
                W32(o, rec + 12, (uint)length);
                data += Pad4(length);
            }
            if (headOffset >= 0) W32(o, headOffset + 8, 0xB1B0AFBA - Checksum(o, 0, o.Length));
            return o;
        }

        static uint Checksum(byte[] b, int offset, int length)
        {
            uint sum = 0;
            int end = offset + Pad4(length);
            for (int p = offset; p < end; p += 4) sum += U32(b, p);
            return sum;
        }

        static int Pad4(int n) => (n + 3) & ~3;
        static int U16(byte[] b, int p) => (b[p] << 8) | b[p + 1];
        static uint U32(byte[] b, int p) => ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
        static void W16(byte[] b, int p, int v) { b[p] = (byte)(v >> 8); b[p + 1] = (byte)v; }
        static void W32(byte[] b, int p, uint v) { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }
    }
}
