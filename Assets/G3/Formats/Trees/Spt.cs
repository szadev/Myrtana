using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace G3.Formats.Trees
{
    /// <summary>Value shape of one .spt token (see <see cref="Spt"/>).</summary>
    public enum SptKind { Marker, Int, Float, Byte, String, Floats, Quads }

    /// <summary>One raw token of the .spt stream. Exactly one value field is meaningful, per <see cref="Kind"/>.</summary>
    public sealed class SptToken
    {
        public int Id;
        public SptKind Kind;
        public int Int;          // Int; Quads: quad count
        public float Float;      // Float
        public byte Byte;        // Byte (bool)
        public string Text;      // String (also BezierSpline text)
        public float[] Floats;   // Floats (fixed count) / Quads (8 per quad)
        public int Offset;       // byte offset of the token id in the file

        public override string ToString()
        {
            switch (Kind)
            {
                case SptKind.Marker: return Id.ToString();
                case SptKind.Int: return $"{Id} i {Int}";
                case SptKind.Float: return $"{Id} f {Float.ToString("R", CultureInfo.InvariantCulture)}";
                case SptKind.Byte: return $"{Id} b {Byte}";
                case SptKind.String: return $"{Id} s \"{Text.Replace("\n", "|").Replace("\t", " ")}\"";
                default:
                    var sb = new StringBuilder($"{Id} {(Kind == SptKind.Quads ? "q" + Int : "f" + Floats.Length)}");
                    foreach (var f in Floats) sb.Append(' ').Append(f.ToString("0.#####", CultureInfo.InvariantCulture));
                    return sb.ToString();
            }
        }
    }

    /// <summary>
    /// SpeedTree 4 curve "BezierSpline min max variance { n  x y tx ty w ... }" stored as text inside the binary stream.
    /// x runs 0..1 along the branch (or level); y is normally 0..1 and scales between <see cref="Min"/> and <see cref="Max"/>;
    /// (tx,ty) is the unit tangent at the control point and w its handle length.
    /// </summary>
    public sealed class BezierSpline
    {
        public float Min, Max, Variance;
        public float[] X, Y, TanX, TanY, Weight;

        public int Count => X?.Length ?? 0;

        public static BezierSpline Parse(string text)
        {
            if (text == null || !text.StartsWith("BezierSpline")) return null;
            var t = text.Substring(12).Split(new[] { ' ', '\t', '\r', '\n', '{', '}' }, StringSplitOptions.RemoveEmptyEntries);
            var inv = CultureInfo.InvariantCulture;
            var s = new BezierSpline
            {
                Min = float.Parse(t[0], inv), Max = float.Parse(t[1], inv), Variance = float.Parse(t[2], inv)
            };
            int n = int.Parse(t[3], inv);
            s.X = new float[n]; s.Y = new float[n]; s.TanX = new float[n]; s.TanY = new float[n]; s.Weight = new float[n];
            for (int i = 0; i < n; i++)
            {
                int o = 4 + i * 5;
                s.X[i] = float.Parse(t[o], inv); s.Y[i] = float.Parse(t[o + 1], inv);
                s.TanX[i] = float.Parse(t[o + 2], inv); s.TanY[i] = float.Parse(t[o + 3], inv);
                s.Weight[i] = float.Parse(t[o + 4], inv);
            }
            return s;
        }

        /// <summary>
        /// Curve shape y(x) for x in 0..1. Each span is a cubic Bézier whose inner handles are the control points'
        /// tangents scaled by their weight (relative to the span width). This is our approximation of SpeedTreeRT's
        /// evaluator (not verified against the runtime); for 2-point "flat" defaults it returns the expected constant/line.
        /// </summary>
        public float Shape(float x)
        {
            if (Count == 0) return 1f;
            if (Count == 1 || x <= X[0]) return Y[0];
            if (x >= X[Count - 1]) return Y[Count - 1];
            int i = 0;
            while (i < Count - 2 && x > X[i + 1]) i++;
            float x0 = X[i], x3 = X[i + 1], dx = x3 - x0;
            if (dx <= 1e-6f) return Y[i + 1];
            float y0 = Y[i], y3 = Y[i + 1];
            float h0 = Weight[i] * dx / Math.Max(Math.Abs(TanX[i]), 1e-3f) * 0.5f;
            float h1 = Weight[i + 1] * dx / Math.Max(Math.Abs(TanX[i + 1]), 1e-3f) * 0.5f;
            float x1 = x0 + TanX[i] * h0, y1 = y0 + TanY[i] * h0;
            float x2 = x3 - TanX[i + 1] * h1, y2 = y3 - TanY[i + 1] * h1;
            x1 = Clamp(x1, x0, x3); x2 = Clamp(x2, x0, x3);
            // invert x(u) by bisection (x is monotonic once the handles are clamped to the span)
            float lo = 0, hi = 1, u = 0.5f;
            for (int k = 0; k < 24; k++)
            {
                u = (lo + hi) * 0.5f;
                if (Cubic(x0, x1, x2, x3, u) < x) lo = u; else hi = u;
            }
            return Cubic(y0, y1, y2, y3, u);
        }

        /// <summary>Min + Shape(x)·(Max − Min), without the random variance.</summary>
        public float Evaluate(float x) => Min + Shape(x) * (Max - Min);

        static float Cubic(float a, float b, float c, float d, float u)
        {
            float v = 1 - u;
            return v * v * v * a + 3 * v * v * u * b + 3 * v * u * u * c + u * u * u * d;
        }

        static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "[{0}..{1} ±{2}, {3} pts]", Min, Max, Variance, Count);
    }

    /// <summary>One generation level (trunk, branch level N, or the leaf level): the 6000–6017 block plus the
    /// per-level 15002/15003 texture twist and 16002–16012 flare/packing parameters.</summary>
    public sealed class SptLevel
    {
        public BezierSpline Disturbance, Gravity, Flexibility, FlexibilityProfile, Length, Radius, RadiusProfile,
            StartAngle, AngleProfile;
        public int CrossSegments, LengthSegments;
        /// <summary>Where the NEXT level's children start/stop along this level (0..1) and how many per unit length.</summary>
        public float ChildFirst, ChildLast, ChildFrequency;
        public float UTile, VTile;
        public bool UAbsolute, VAbsolute;
        // 15002/15003 (same order as levels)
        public bool RandomVOffset;
        public float Twist;
        // 16002–16012 (same order as levels; absent levels keep defaults)
        public float SegmentPack = 1;
        public int FlareCount;
        public float FlareBalance, FlareRadialInfluence, FlareRadialInfluenceVariance, FlareRadialExponent,
            FlareRadialDistance, FlareRadialDistanceVariance, FlareLengthDistance, FlareLengthDistanceVariance,
            FlareLengthExponent;
    }

    /// <summary>A leaf texture map (4000–4007). In G3 the named .tga is NOT shipped; the leaf image comes from the
    /// composite map via <see cref="SpeedTreeDef.LeafQuads"/> (same index).</summary>
    public sealed class SptLeafMap
    {
        public bool Blossom;
        public float[] Color = new float[3];
        public float ColorVariance;
        public string Texture;
        public float[] Origin = new float[3];   // pivot inside the leaf card, 0..1
        public float[] Size = new float[3];     // card size (UI value / 100), relative to tree size
        public float[] SizeUi = new float[3];   // 4006: the same size as typed in SpeedTreeCAD
        public float Unknown4007;
    }

    public sealed class SptFrondMap
    {
        public string Texture;
        public float Unknown14003, SizeFactor, MinOffsetAngle, MaxOffsetAngle;
    }

    /// <summary>SpeedTree lighting material (8003/8005/8009): diffuse, ambient, specular, emissive RGB + shininess.</summary>
    public sealed class SptMaterial
    {
        public float[] Diffuse = new float[3], Ambient = new float[3], Specular = new float[3], Emissive = new float[3];
        public float Shininess;

        internal static SptMaterial From(float[] f)
        {
            var m = new SptMaterial { Shininess = f[12] };
            Array.Copy(f, 0, m.Diffuse, 0, 3); Array.Copy(f, 3, m.Ambient, 0, 3);
            Array.Copy(f, 6, m.Specular, 0, 3); Array.Copy(f, 9, m.Emissive, 0, 3);
            return m;
        }
    }

    public enum SptCollisionKind { Sphere, Capsule, Box }

    /// <summary>Collision primitive in tree units (12002 sphere xyz r, 12003 capsule xyz r len, 12004 box min/max).
    /// SpeedTree space is Z-up.</summary>
    public sealed class SptCollision
    {
        public SptCollisionKind Kind;
        public float[] Values;
    }

    /// <summary>Normalised texture rectangle inside the composite map (8 floats = 4 corners, u,v pairs).</summary>
    public sealed class SptQuad
    {
        public float[] UV = new float[8];
        public float MinU => Math.Min(Math.Min(UV[0], UV[2]), Math.Min(UV[4], UV[6]));
        public float MaxU => Math.Max(Math.Max(UV[0], UV[2]), Math.Max(UV[4], UV[6]));
        public float MinV => Math.Min(Math.Min(UV[1], UV[3]), Math.Min(UV[5], UV[7]));
        public float MaxV => Math.Max(Math.Max(UV[1], UV[3]), Math.Max(UV[5], UV[7]));
    }

    /// <summary>Decoded SpeedTreeRT 4.0 tree description (.spt). Purely parametric: no geometry is stored.</summary>
    public sealed class SpeedTreeDef
    {
        public string Name;
        public string Version;                 // "__IdvSpt_02_"
        public string BranchTexture;           // 2000 (".tga" name; shipped as speedtree/<name>.ximg)
        public float LodFar, LodNear;          // 2001/2003
        public int Seed;                       // 2005
        public float Size, SizeVariance;       // 2006/2007, tree units
        /// <summary>Levels in file order: trunk, branch levels…, and the last entry is the leaf level
        /// (its Length curve = leaf placement distance, its ChildFirst/Last/Frequency are leaf-specific).</summary>
        public readonly List<SptLevel> Levels = new List<SptLevel>();

        // leaves (3000–3010)
        public float BlossomDistance, BlossomWeight, LeafPlacementTolerance, LeafDimming;
        public int BlossomDepth, LeafCollision;   // collision: 0 none, 1 branch, 2 tree
        public bool LeafDimmingOn;
        public readonly List<SptLeafMap> LeafMaps = new List<SptLeafMap>();
        public float[] Unknown5000Block = new float[0];

        // lighting
        public int BranchLighting, LeafLighting, FrondLighting, LeafStaticStyle;  // 0 dynamic, 1 static
        public SptMaterial BranchMaterial, LeafMaterial, FrondMaterial;
        public float LeafLightingAdjust;

        // LOD (9000)
        public int BillboardTransition, BranchLodCount, LeafLodCount;
        public float LeafBillboardRadius, LeafBillboardExponent, LeafBillboardSize, LeafBillboardReduction,
            BranchMinVolume, BranchMaxVolume, BranchFuzziness, BranchRetention, LeafTransition;

        // composite map (10000, 20000)
        public readonly List<SptQuad> LeafQuads = new List<SptQuad>();
        public readonly List<SptQuad> BillboardQuads = new List<SptQuad>();
        public readonly List<SptQuad> FrondQuads = new List<SptQuad>();
        public string CompositeMap;             // 20002 (".dds" name; shipped as speedtree/<name>.ximg)
        public float[] CompositeUV;             // 20005

        // wind
        public int WindFirstLevel;              // 11002
        public float LeafRockScalar, LeafRustleScalar;   // 21000/21001
        public bool PropagateFlexibility;       // 22000

        public readonly List<SptCollision> Collision = new List<SptCollision>();

        // fronds (13000/14000)
        public bool FrondsEnabled;
        public int FrondLevel, FrondType, FrondBlades, FrondProfileSegments, FrondLodCount, FrondMinLengthSegments,
            FrondMinProfileSegments;
        public BezierSpline FrondProfile;
        public float FrondMaxArea, FrondMinArea, FrondFuzziness, FrondRetention;
        public readonly List<SptFrondMap> FrondMaps = new List<SptFrondMap>();

        public int FlareSeed;                   // 16013
        public string SelfShadowMap;            // 18005

        /// <summary>The whole token stream in file order (lossless; unknown-but-typed tokens included).</summary>
        public readonly List<SptToken> Tokens = new List<SptToken>();

        /// <summary>Branch levels (trunk first) = all levels except the trailing leaf level.</summary>
        public int BranchLevelCount => Math.Max(0, Levels.Count - 1);
    }

    /// <summary>
    /// .spt — SpeedTreeRT 4.0 tree file ("__IdvSpt_02_"), as stored in Speedtrees.pak (Docs/formats-speedtree.md).
    /// <code>
    /// file  := token*                      little-endian, no header beyond token 1000
    /// token := u32 id, value               value shape is fixed per id (table below / <see cref="KindOf"/>)
    ///   marker   : no value (block begin/end pairs such as 1002/1003, 1016/1017, 8000/8001 ...)
    ///   int/float: 4 bytes ; byte: 1 byte (bools)
    ///   string   : i32 length + bytes (texture names, and BezierSpline curves as text)
    ///   floats   : fixed count of f32 (4001 3, 8003 13, 12002 4, 12003 5, 12004 6, 20005 8, ...)
    ///   quads    : i32 n + n×8 f32 (10002 leaf, 10003 billboard, 10004 frond rectangles in the composite map)
    /// structure: 1000 "__IdvSpt_02_" 1002 {2000..2007 1014=levels (1016 {6000..6017} 1017)×levels 1015} 1003
    ///            1004 {3000..3010 1009 {1006=n (1007 {4000..4007} 1008)×n} 1010} 1005 1011 {5000..5006} 1012 1001
    ///            8000{lighting} 9000{LOD} 11000{wind} 10000{composite quads} 12000{collision} 13000{fronds, 14000×n}
    ///            15000{(15002 15003)×levels} 16000{(16002..16012)×levels} 16013 16014 18000{} 19000{} 20000{} 21000 21001 22000
    /// </code>
    /// G3 files stop at 22000 (SpeedTreeCAD 4.0); the 25000+ tokens of 4.1/4.2 (roots, mesh leaves, …) are accepted too.
    /// Ids and meanings follow the public spttools FORMAT notes (J. Sadeharju) and were checked against all 98 G3 files.
    /// </summary>
    public static class Spt
    {
        public const string Signature = "__IdvSpt_02_";

        static readonly Dictionary<int, (SptKind kind, int count)> Table = BuildTable();

        static Dictionary<int, (SptKind, int)> BuildTable()
        {
            var t = new Dictionary<int, (SptKind, int)>();
            void K(SptKind k, int n, params int[] ids) { foreach (var i in ids) t[i] = (k, n); }
            void R(SptKind k, int n, int from, int to) { for (int i = from; i <= to; i++) t[i] = (k, n); }
            const SptKind M = SptKind.Marker, I = SptKind.Int, F = SptKind.Float, B = SptKind.Byte, S = SptKind.String, V = SptKind.Floats;

            K(M, 0, 1001, 1002, 1003, 1004, 1005, 1007, 1008, 1009, 1010, 1011, 1012, 1015, 1016, 1017, 7000, 7001);
            foreach (var b in new[] { 8000, 9000, 10000, 11000, 12000, 13000, 14000, 15000, 16000, 18000, 19000, 20000,
                         25000, 26000, 27000, 28000, 29000, 30000, 40000, 50000, 60000, 70000, 71000, 72000, 73000, 74000, 75000 })
                K(M, 0, b, b + 1);
            K(S, 0, 1000); K(I, 0, 1006, 1014);
            K(S, 0, 2000); K(F, 0, 2001, 2003, 2006, 2007); K(B, 0, 2002); K(I, 0, 2004, 2005);
            K(F, 0, 3000, 3002, 3004, 3005, 3007, 3010); K(I, 0, 3001, 3008); K(B, 0, 3003, 3006, 3009);
            K(B, 0, 4000); K(V, 3, 4001, 4004, 4005, 4006); K(F, 0, 4002, 4007); K(S, 0, 4003);
            K(V, 3, 5000, 5001, 5002, 5003, 5004); K(F, 0, 5005); K(B, 0, 5006);
            R(S, 0, 6000, 6007); K(S, 0, 6017); K(I, 0, 6008, 6009); K(F, 0, 6010, 6011, 6012, 6013, 6014); K(B, 0, 6015, 6016);
            K(I, 0, 8002, 8004, 8007, 8008); K(V, 13, 8003, 8005, 8009); K(F, 0, 8006);
            K(I, 0, 9002, 9007, 9011); K(M, 0, 9005, 9006); K(F, 0, 9003, 9004, 9008, 9009, 9010, 9012, 9013, 9014);
            K(SptKind.Quads, 0, 10002, 10003, 10004);
            K(I, 0, 11002);
            K(V, 4, 12002); K(V, 5, 12003); K(V, 6, 12004);
            K(I, 0, 13002, 13003, 13004, 13006, 13008, 13009); K(S, 0, 13005); K(B, 0, 13007); K(F, 0, 13010, 13011, 13012, 13013);
            K(S, 0, 14002); K(F, 0, 14003, 14004, 14005, 14006); K(I, 0, 14007, 14008);
            K(B, 0, 15002); K(F, 0, 15003);
            K(F, 0, 16002, 16004, 16005, 16006, 16007, 16008, 16009, 16010, 16011, 16012, 16014); K(I, 0, 16003, 16013);
            K(V, 3, 18002, 18003, 18004); K(S, 0, 18005);
            K(I, 0, 19002);
            K(S, 0, 20002); K(B, 0, 20003, 20004); K(V, 8, 20005);
            K(F, 0, 21000, 21001); K(B, 0, 22000); K(F, 0, 23002, 23003);
            // SpeedTreeCAD 4.1/4.2 additions (not used by G3, kept so foreign files still tokenize)
            K(F, 0, 25002); K(I, 0, 25003, 25004, 25005, 25006); K(B, 0, 25007);
            K(M, 0, 26002, 26003); K(F, 0, 26004, 26005, 26006, 26007, 26010, 26011, 26015, 26016, 26017, 26021);
            K(I, 0, 26008, 26012); K(B, 0, 26009, 26023); K(S, 0, 26013, 26014, 26018, 26019, 26020, 26022);
            K(B, 0, 27002); K(F, 0, 27003, 27005, 27006); K(I, 0, 27004);
            K(B, 0, 28002); K(F, 0, 28003); K(I, 0, 28004); K(I, 0, 29002); R(F, 0, 30002, 30009);
            K(I, 0, 40002); K(F, 0, 40003, 40004, 40005); K(M, 0, 40006, 40007, 40008);
            K(M, 0, 50002, 50003); K(F, 0, 50004, 50005, 50008, 50010, 50013, 50014, 50015, 50016, 50017);
            K(B, 0, 50006, 50007, 50009, 50011, 50012, 50018);
            K(M, 0, 60002, 60003, 60004, 60005, 60009); K(S, 0, 60006); K(I, 0, 60007, 60008);
            R(S, 0, 70002, 70008);
            K(I, 0, 71001, 71004, 71012, 71013); K(M, 0, 71002, 71005, 71011, 71014, 71015); K(S, 0, 71003);
            K(V, 3, 71006, 71007, 71008, 71009); K(V, 2, 71010);
            K(B, 0, 72001); K(M, 0, 72002, 72004); K(I, 0, 72003); K(F, 0, 72005, 72006);
            K(M, 0, 73002, 73003); K(F, 0, 73004, 73005, 73006); K(F, 0, 74002);
            K(F, 0, 75002, 75003, 75005); K(B, 0, 75004);
            return t;
        }

        /// <summary>Value shape of a token id, or null if unknown (the stream cannot be resynchronised past it).</summary>
        public static SptKind? KindOf(int id) => Table.TryGetValue(id, out var k) ? k.kind : (SptKind?)null;

        public static bool IsSpt(byte[] data) =>
            data != null && data.Length >= 16 && BitConverter.ToInt32(data, 0) == 1000 && BitConverter.ToInt32(data, 4) == 12
            && Encoding.ASCII.GetString(data, 8, 12) == Signature;

        /// <summary>Tokenize the whole file. Throws <see cref="InvalidDataException"/> on an unknown id or truncation.</summary>
        public static List<SptToken> Tokenize(byte[] data)
        {
            var list = new List<SptToken>();
            int p = 0;
            while (p < data.Length)
            {
                Need(data, p, 4);
                var tok = new SptToken { Offset = p, Id = BitConverter.ToInt32(data, p) };
                p += 4;
                if (!Table.TryGetValue(tok.Id, out var k))
                    throw new InvalidDataException($"spt: unknown token {tok.Id} at 0x{tok.Offset:X}");
                tok.Kind = k.kind;
                switch (k.kind)
                {
                    case SptKind.Marker: break;
                    case SptKind.Int: Need(data, p, 4); tok.Int = BitConverter.ToInt32(data, p); p += 4; break;
                    case SptKind.Float: Need(data, p, 4); tok.Float = BitConverter.ToSingle(data, p); p += 4; break;
                    case SptKind.Byte: Need(data, p, 1); tok.Byte = data[p]; p += 1; break;
                    case SptKind.String:
                    {
                        Need(data, p, 4);
                        int n = BitConverter.ToInt32(data, p); p += 4;
                        if (n < 0) throw new InvalidDataException($"spt: bad string length at 0x{p - 4:X}");
                        Need(data, p, n);
                        tok.Text = Encoding.GetEncoding(28591).GetString(data, p, n); p += n;
                        break;
                    }
                    case SptKind.Floats:
                        Need(data, p, 4 * k.count);
                        tok.Floats = ReadFloats(data, p, k.count); p += 4 * k.count;
                        break;
                    case SptKind.Quads:
                    {
                        Need(data, p, 4);
                        int n = BitConverter.ToInt32(data, p); p += 4;
                        if (n < 0 || n > 4096) throw new InvalidDataException($"spt: bad quad count at 0x{p - 4:X}");
                        Need(data, p, 32 * n);
                        tok.Int = n; tok.Floats = ReadFloats(data, p, 8 * n); p += 32 * n;
                        break;
                    }
                }
                list.Add(tok);
            }
            return list;
        }

        /// <summary>Decode a .spt into the structured description (the raw token list is kept in <see cref="SpeedTreeDef.Tokens"/>).</summary>
        public static SpeedTreeDef Read(byte[] data, string name = null)
        {
            if (!IsSpt(data)) throw new InvalidDataException("not a SpeedTree SPT file");
            var t = new SpeedTreeDef { Name = name };
            t.Tokens.AddRange(Tokenize(data));

            SptLevel lvl = null;
            SptLeafMap leaf = null;
            SptFrondMap frond = null;
            int level15 = 0, level16 = 0;
            bool inRoots = false;
            foreach (var k in t.Tokens)
            {
                switch (k.Id)
                {
                    case 1000: t.Version = k.Text; break;
                    case 2000: t.BranchTexture = k.Text; break;
                    case 2001: t.LodFar = k.Float; break;
                    case 2003: t.LodNear = k.Float; break;
                    case 2005: t.Seed = k.Int; break;
                    case 2006: t.Size = k.Float; break;
                    case 2007: t.SizeVariance = k.Float; break;
                    case 40006: inRoots = true; break;     // 4.1+ root level reuses 1016/6000 tokens: ignore
                    case 40007: inRoots = false; break;
                    case 1016: if (!inRoots) { lvl = new SptLevel(); t.Levels.Add(lvl); } else lvl = null; break;
                    case 1017: lvl = null; break;
                    case 3000: t.BlossomDistance = k.Float; break;
                    case 3001: t.BlossomDepth = k.Int; break;
                    case 3002: t.BlossomWeight = k.Float; break;
                    case 3007: t.LeafPlacementTolerance = k.Float; break;
                    case 3008: t.LeafCollision = k.Int; break;
                    case 3009: t.LeafDimmingOn = k.Byte != 0; break;
                    case 3010: t.LeafDimming = k.Float; break;
                    case 1007: leaf = new SptLeafMap(); t.LeafMaps.Add(leaf); break;
                    case 1008: leaf = null; break;
                    case 8002: t.BranchLighting = k.Int; break;
                    case 8003: t.BranchMaterial = SptMaterial.From(k.Floats); break;
                    case 8004: t.LeafLighting = k.Int; break;
                    case 8005: t.LeafMaterial = SptMaterial.From(k.Floats); break;
                    case 8006: t.LeafLightingAdjust = k.Float; break;
                    case 8007: t.LeafStaticStyle = k.Int; break;
                    case 8008: t.FrondLighting = k.Int; break;
                    case 8009: t.FrondMaterial = SptMaterial.From(k.Floats); break;
                    case 9002: t.BillboardTransition = k.Int; break;
                    case 9003: t.LeafBillboardRadius = k.Float; break;
                    case 9004: t.LeafBillboardExponent = k.Float; break;
                    case 9007: t.BranchLodCount = k.Int; break;
                    case 9008: t.BranchMinVolume = k.Float; break;
                    case 9009: t.LeafBillboardSize = k.Float; break;
                    case 9010: t.LeafBillboardReduction = k.Float; break;
                    case 9011: t.LeafLodCount = k.Int; break;
                    case 9012: t.BranchMaxVolume = k.Float; break;
                    case 9013: t.BranchFuzziness = k.Float; break;
                    case 9014: t.BranchRetention = k.Float; break;
                    case 10002: AddQuads(t.LeafQuads, k); break;
                    case 10003: AddQuads(t.BillboardQuads, k); break;
                    case 10004: AddQuads(t.FrondQuads, k); break;
                    case 11002: t.WindFirstLevel = k.Int; break;
                    case 12002: t.Collision.Add(new SptCollision { Kind = SptCollisionKind.Sphere, Values = k.Floats }); break;
                    case 12003: t.Collision.Add(new SptCollision { Kind = SptCollisionKind.Capsule, Values = k.Floats }); break;
                    case 12004: t.Collision.Add(new SptCollision { Kind = SptCollisionKind.Box, Values = k.Floats }); break;
                    case 13002: t.FrondLevel = k.Int; break;
                    case 13003: t.FrondType = k.Int; break;
                    case 13004: t.FrondBlades = k.Int; break;
                    case 13005: t.FrondProfile = BezierSpline.Parse(k.Text); break;
                    case 13006: t.FrondProfileSegments = k.Int; break;
                    case 13007: t.FrondsEnabled = k.Byte != 0; break;
                    case 13009: t.FrondLodCount = k.Int; break;
                    case 13010: t.FrondMaxArea = k.Float; break;
                    case 13011: t.FrondMinArea = k.Float; break;
                    case 13012: t.FrondFuzziness = k.Float; break;
                    case 13013: t.FrondRetention = k.Float; break;
                    case 14007: t.FrondMinLengthSegments = k.Int; break;
                    case 14008: t.FrondMinProfileSegments = k.Int; break;
                    case 14000: frond = new SptFrondMap(); t.FrondMaps.Add(frond); break;
                    case 14001: frond = null; break;
                    case 15002: if (level15 < t.Levels.Count) t.Levels[level15].RandomVOffset = k.Byte != 0; break;
                    case 15003: if (level15 < t.Levels.Count) t.Levels[level15].Twist = k.Float; level15++; break;
                    case 16013: t.FlareSeed = k.Int; break;
                    case 16014: t.LeafTransition = k.Float; break;
                    case 18005: t.SelfShadowMap = k.Text; break;
                    case 20002: t.CompositeMap = k.Text; break;
                    case 20005: t.CompositeUV = k.Floats; break;
                    case 21000: t.LeafRockScalar = k.Float; break;
                    case 21001: t.LeafRustleScalar = k.Float; break;
                    case 22000: t.PropagateFlexibility = k.Byte != 0; break;
                    case 5000: case 5001: case 5002: case 5003: case 5004:
                    {
                        var a = new float[t.Unknown5000Block.Length + 3];
                        Array.Copy(t.Unknown5000Block, a, t.Unknown5000Block.Length);
                        Array.Copy(k.Floats, 0, a, a.Length - 3, 3);
                        t.Unknown5000Block = a;
                        break;
                    }
                }
                if (k.Id >= 16002 && k.Id <= 16012 && !inRoots)
                {
                    if (level16 < t.Levels.Count) Flare(t.Levels[level16], k);
                    if (k.Id == 16012) level16++;
                }
                if (lvl != null) LevelToken(lvl, k);
                if (leaf != null) LeafToken(leaf, k);
                if (frond != null) FrondToken(frond, k);
            }
            return t;
        }

        static void LevelToken(SptLevel l, SptToken k)
        {
            switch (k.Id)
            {
                case 6000: l.Disturbance = BezierSpline.Parse(k.Text); break;
                case 6001: l.Gravity = BezierSpline.Parse(k.Text); break;
                case 6002: l.Flexibility = BezierSpline.Parse(k.Text); break;
                case 6003: l.FlexibilityProfile = BezierSpline.Parse(k.Text); break;
                case 6004: l.Length = BezierSpline.Parse(k.Text); break;
                case 6005: l.Radius = BezierSpline.Parse(k.Text); break;
                case 6006: l.RadiusProfile = BezierSpline.Parse(k.Text); break;
                case 6007: l.StartAngle = BezierSpline.Parse(k.Text); break;
                case 6008: l.CrossSegments = k.Int; break;
                case 6009: l.LengthSegments = k.Int; break;
                case 6010: l.ChildFirst = k.Float; break;
                case 6011: l.ChildLast = k.Float; break;
                case 6012: l.ChildFrequency = k.Float; break;
                case 6013: l.UTile = k.Float; break;
                case 6014: l.VTile = k.Float; break;
                case 6015: l.UAbsolute = k.Byte != 0; break;
                case 6016: l.VAbsolute = k.Byte != 0; break;
                case 6017: l.AngleProfile = BezierSpline.Parse(k.Text); break;
            }
        }

        static void Flare(SptLevel l, SptToken k)
        {
            switch (k.Id)
            {
                case 16002: l.SegmentPack = k.Float; break;
                case 16003: l.FlareCount = k.Int; break;
                case 16004: l.FlareBalance = k.Float; break;
                case 16005: l.FlareRadialInfluence = k.Float; break;
                case 16006: l.FlareRadialInfluenceVariance = k.Float; break;
                case 16007: l.FlareRadialExponent = k.Float; break;
                case 16008: l.FlareRadialDistance = k.Float; break;
                case 16009: l.FlareRadialDistanceVariance = k.Float; break;
                case 16010: l.FlareLengthDistance = k.Float; break;
                case 16011: l.FlareLengthDistanceVariance = k.Float; break;
                case 16012: l.FlareLengthExponent = k.Float; break;
            }
        }

        static void LeafToken(SptLeafMap m, SptToken k)
        {
            switch (k.Id)
            {
                case 4000: m.Blossom = k.Byte != 0; break;
                case 4001: m.Color = k.Floats; break;
                case 4002: m.ColorVariance = k.Float; break;
                case 4003: m.Texture = k.Text; break;
                case 4004: m.Origin = k.Floats; break;
                case 4005: m.Size = k.Floats; break;
                case 4006: m.SizeUi = k.Floats; break;
                case 4007: m.Unknown4007 = k.Float; break;
            }
        }

        static void FrondToken(SptFrondMap m, SptToken k)
        {
            switch (k.Id)
            {
                case 14002: m.Texture = k.Text; break;
                case 14003: m.Unknown14003 = k.Float; break;
                case 14004: m.SizeFactor = k.Float; break;
                case 14005: m.MinOffsetAngle = k.Float; break;
                case 14006: m.MaxOffsetAngle = k.Float; break;
            }
        }

        static void AddQuads(List<SptQuad> dst, SptToken k)
        {
            for (int i = 0; i < k.Int; i++)
            {
                var q = new SptQuad();
                Array.Copy(k.Floats, i * 8, q.UV, 0, 8);
                dst.Add(q);
            }
        }

        static float[] ReadFloats(byte[] d, int p, int n)
        {
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = BitConverter.ToSingle(d, p + 4 * i);
            return f;
        }

        static void Need(byte[] d, int p, int n)
        {
            if (p + n > d.Length) throw new InvalidDataException($"spt: truncated at 0x{p:X}");
        }

        /// <summary>Resource name of a texture referenced by a tree (".tga"/".dds" stripped): the engine looks it up
        /// as an image resource, e.g. speedtree/&lt;name&gt;.ximg in _compiledImage.pak.</summary>
        public static string ImageResourceName(string textureFile) =>
            string.IsNullOrEmpty(textureFile) ? textureFile : Path.GetFileNameWithoutExtension(textureFile);
    }
}
