using System;
using System.Collections.Generic;
using G3.Formats.Trees;

namespace G3.Tools.SptBake
{
    /// <summary>
    /// Reads the SpeedTreeRT geometry of one computed tree into a <see cref="BakedTree"/>. The SGeometry layout
    /// (0x120 bytes) was mapped from its constructor and the engine's eCSpeedTreeRenderBuffer::Create*Geometry readers:
    /// <code>
    /// 0x000 SIndexed branches   0x03C SIndexed fronds    (0x3C bytes each)
    ///   +00 i32 discreteLod  +04 u16 stripCount  +08 u16* stripLengths  +0C u16** strips  +10 u16 vertexCount
    ///   +14 u32* colors (null: dynamic lighting)  +18 f3* normals  +1C f3* binormals  +20 f3* tangents  +24 f3* coords
    ///   +28 f2* texcoords  +2C f2* shadow texcoords (unused)  +30 f* windWeights  +34 u8* windMatrixIndices  +38 f alphaTest
    /// 0x078 SLeaf (current)     0x0B4 SLeaf (blend partner, unused here)   (0x3C bytes each)
    ///   +00 bool active  +04 f alphaTest  +08 i32 discreteLod  +0C u16 leafCount  +10 u8* mapIndices  +14 u8* clusterIndices
    ///   +18 f3* centers  +1C f2[4]** texcoords  +20 f4[4]** cornerOffsets  +24 u32* colors  +28 f3* normals
    ///   +2C f3* binormals  +30 f3* tangents  +34 f* windWeights  +38 u8* windMatrixIndices
    /// 0x0F0 SBillboard 360°  0x100 SBillboard 2  0x110 SBillboard horizontal   (0x10 bytes each)
    ///   +00 bool active  +04 f2[4]* texcoords  +08 f3[4]* coords  +0C f alphaTest
    /// </code>
    /// </summary>
    public sealed unsafe class TreeBaker
    {
        // CSpeedTreeRT (0x9C bytes) fields used here.
        const int TreeLodBlock = 0x00, TreeBounds = 0x40, TreeBillboardImages = 0x4C, TreeCollision = 0x54, TreeFrondLods = 0x60;
        // Block at *(tree+0): LOD state.
        const int LodValue = 0x14, LodFarField = 0x40, LodNearField = 0x44, BranchLods = 0x70, LeafLods = 0xB4;
        // Billboard switches the engine sets around GetGeometry(8) in eCWraper_SpeedTreeRT::GetBillboardGeometry.
        const uint GlobalBillboardFlag = 0x10602D9F, GlobalBillboardLod = 0x105FD410;
        const uint FnGetCollisionObject = 0x102B85A0;   // ECX = index; stack (tree, int* type, f3* pos, f3* dims); pops 16

        readonly SpeedTreeRuntime _rt;
        readonly NativeCall _call = new NativeCall();

        public TreeBaker(SpeedTreeRuntime rt) { _rt = rt; }

        public BakedTree Bake(string name, byte[] spt)
        {
            uint tree = (uint)_rt.LoadAndCompute(spt);
            uint lodBlock = Ptr(tree + TreeLodBlock);
            var t = new BakedTree { Name = name };
            float* bb = (float*)Ptr(tree + TreeBounds);
            for (int i = 0; i < 3; i++) { t.BoundsMin[i] = bb[i]; t.BoundsMax[i] = bb[3 + i]; }
            float heightM = (bb[4] - bb[1]) / 100f;
            t.SptLodFar = *(float*)(lodBlock + LodFarField);
            t.SptLodNear = *(float*)(lodBlock + LodNearField);
            t.LodFar = t.SptLodFar * heightM;    // engine: eCWraper_SpeedTreeRT::Read
            t.LodNear = t.SptLodNear * heightM;

            byte* g = stackalloc byte[SpeedTreeRuntime.GeometrySize];
            int branchLods = *(ushort*)(lodBlock + BranchLods);
            for (int lod = 0; lod < branchLods; lod++)
            {
                _rt.GetGeometry((IntPtr)tree, g, 1, lod, -1, -1);
                var m = ReadIndexed(g + 0x00, lod);
                if (m != null) t.Branches.Add(m);
            }
            int frondLods = *(ushort*)(tree + TreeFrondLods);
            for (int lod = 0; lod < frondLods; lod++)
            {
                _rt.GetGeometry((IntPtr)tree, g, 2, -1, lod, -1);
                var m = ReadIndexed(g + 0x3C, lod);
                if (m != null) t.Fronds.Add(m);
            }
            int leafLods = *(ushort*)(lodBlock + LeafLods);
            for (int lod = 0; lod < leafLods; lod++)
            {
                _rt.GetGeometry((IntPtr)tree, g, 4, -1, -1, lod);
                var l = ReadLeaves(g + 0x78, lod);
                if (l != null) t.Leaves.Add(l);
            }
            t.Billboard = ReadBillboard(tree, lodBlock, g);
            ReadCollision(tree, t);
            return t;
        }

        BakedMeshLod ReadIndexed(byte* s, int lod)
        {
            int stripCount = *(ushort*)(s + 0x04);
            int n = *(ushort*)(s + 0x10);
            if (stripCount == 0 || n == 0) return null;
            var m = new BakedMeshLod
            {
                Lod = lod,
                AlphaTest = *(float*)(s + 0x38),
                Normals = Copy(s + 0x18, 3 * n),
                Binormals = Copy(s + 0x1C, 3 * n),
                Tangents = Copy(s + 0x20, 3 * n),
                Positions = Copy(s + 0x24, 3 * n),
                Uv = Copy(s + 0x28, 2 * n),
                WindWeight = Copy(s + 0x30, n),
                WindMatrix = CopyBytes(s + 0x34, n),
            };
            ushort* lengths = (ushort*)*(uint*)(s + 0x08);
            ushort** strips = (ushort**)*(uint*)(s + 0x0C);
            var tris = new List<int>();
            for (int k = 0; k < stripCount; k++)
            {
                ushort* st = strips[k];
                for (int i = 0; i + 2 < lengths[k]; i++)
                {
                    int a = st[i], b = st[i + 1], c = st[i + 2];
                    if (a == b || b == c || a == c) continue;   // degenerate joints between strips
                    if ((i & 1) == 0) { tris.Add(a); tris.Add(b); tris.Add(c); }
                    else { tris.Add(b); tris.Add(a); tris.Add(c); }
                }
            }
            m.Triangles = tris.ToArray();
            OrientLikeNormals(m);
            return m;
        }

        /// <summary>Make the winding clockwise around the vertex normals (Unity front face) for the whole LOD.</summary>
        static void OrientLikeNormals(BakedMeshLod m)
        {
            double agree = 0;
            var p = m.Positions; var nrm = m.Normals; var t = m.Triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                int a = t[i] * 3, b = t[i + 1] * 3, c = t[i + 2] * 3;
                double ux = p[b] - p[a], uy = p[b + 1] - p[a + 1], uz = p[b + 2] - p[a + 2];
                double vx = p[c] - p[a], vy = p[c + 1] - p[a + 1], vz = p[c + 2] - p[a + 2];
                double fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx;   // Unity: front normal = cross(b−a, c−a)
                agree += fx * (nrm[a] + nrm[b] + nrm[c]) + fy * (nrm[a + 1] + nrm[b + 1] + nrm[c + 1]) + fz * (nrm[a + 2] + nrm[b + 2] + nrm[c + 2]);
            }
            if (agree >= 0) return;
            for (int i = 0; i < t.Length; i += 3) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
        }

        static BakedLeafLod ReadLeaves(byte* s, int lod)
        {
            int n = *(ushort*)(s + 0x0C);
            if (s[0] == 0 || n == 0) return null;
            var l = new BakedLeafLod
            {
                Lod = lod,
                AlphaTest = *(float*)(s + 0x04),
                MapIndex = CopyBytes(s + 0x10, n),
                Cluster = CopyBytes(s + 0x14, n),
                Centers = Copy(s + 0x18, 3 * n),
                Colors = new uint[n],
                Normals = Copy(s + 0x28, 3 * n),
                Binormals = Copy(s + 0x2C, 3 * n),
                Tangents = Copy(s + 0x30, 3 * n),
                WindWeight = Copy(s + 0x34, n),
                WindMatrix = CopyBytes(s + 0x38, n),
                Uv = new float[8 * n],
                Corners = new float[8 * n],
            };
            uint* colors = (uint*)*(uint*)(s + 0x24);
            float** uv = (float**)*(uint*)(s + 0x1C);
            float** corners = (float**)*(uint*)(s + 0x20);
            for (int i = 0; i < n; i++)
            {
                l.Colors[i] = colors != null ? colors[i] : 0xFFFFFFFF;
                for (int k = 0; k < 8; k++) l.Uv[8 * i + k] = uv[i][k];
            }
            CardSpaceCorners(corners, n, l.Corners);
            return l;
        }

        /// <summary>
        /// SpeedTree returns leaf corners already turned towards its current camera (the billboard table). Re-express them
        /// in the card plane: x along the camera right axis, y along the camera up axis (world up projected, no roll), so
        /// the shader can rebuild them for any camera. Corner order is top-left, top-right, bottom-right, bottom-left.
        /// </summary>
        static void CardSpaceCorners(float** corners, int n, float[] dst)
        {
            // Card normal (camera direction) from the largest card.
            double nx = 0, ny = 0, nz = 0, best = 0;
            for (int i = 0; i < n; i++)
            {
                float* c = corners[i];
                double ax = c[4] - c[0], ay = c[5] - c[1], az = c[6] - c[2];     // c1 − c0
                double bx = c[12] - c[0], by = c[13] - c[1], bz = c[14] - c[2];  // c3 − c0
                double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
                double len = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (len > best) { best = len; nx = cx / len; ny = cy / len; nz = cz / len; }
            }
            if (best == 0) { Array.Clear(dst, 0, dst.Length); return; }
            // up = world Y projected onto the card plane; right = up × normal; then fix handedness so corners 0→1 run +x.
            double ux = -ny * nx, uy = 1 - ny * ny, uz = -ny * nz;
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-6) { ux = 0; uy = 0; uz = 1; ul = 1; }
            ux /= ul; uy /= ul; uz /= ul;
            double rx = uy * nz - uz * ny, ry = uz * nx - ux * nz, rz = ux * ny - uy * nx;
            double winding = 0;
            for (int i = 0; i < n; i++)
            {
                float* c = corners[i];
                for (int k = 0; k < 4; k++)
                {
                    dst[8 * i + 2 * k] = (float)(c[4 * k] * rx + c[4 * k + 1] * ry + c[4 * k + 2] * rz);
                    dst[8 * i + 2 * k + 1] = (float)(c[4 * k] * ux + c[4 * k + 1] * uy + c[4 * k + 2] * uz);
                }
                // shoelace: TL→TR→BR→BL is clockwise in (x right, y up) → negative area
                for (int k = 0; k < 4; k++)
                {
                    int j = (k + 1) & 3;
                    winding += dst[8 * i + 2 * k] * dst[8 * i + 2 * j + 1] - dst[8 * i + 2 * j] * dst[8 * i + 2 * k + 1];
                }
            }
            if (winding > 0)
                for (int i = 0; i < dst.Length; i += 2) dst[i] = -dst[i];
        }

        BakedBillboard ReadBillboard(uint tree, uint lodBlock, byte* g)
        {
            uint images = Ptr(tree + TreeBillboardImages);
            if (images == 0 || *(int*)(images + 8) <= 0) return null;
            float saved = *(float*)(lodBlock + LodValue);
            *(byte*)GlobalBillboardFlag = 1;
            *(float*)(lodBlock + LodValue) = *(float*)GlobalBillboardLod;
            _rt.GetGeometry((IntPtr)tree, g, 8, -1, -1, -1);
            *(byte*)GlobalBillboardFlag = 0;
            *(float*)(lodBlock + LodValue) = saved;
            byte* b = g + 0xF0;
            if (b[0] == 0) return null;
            float* coords = (float*)*(uint*)(b + 0x08);
            // SpeedTree writes its Z-up billboard quad (0, ±size1 / 2, {size2, 0}) from the tree's bounding box, but Genome
            // runs SpeedTreeRT in a Y-up coordinate system, so the two sizes come out swapped: size1 is the vertical
            // extent (it equals the geometry height of every tree) and size2 the horizontal one. Checked against the
            // billboard images in Unity: the swapped quad matches the mesh silhouettes, the literal one squashes firs
            // and turns wide bushes into columns.
            float vertical = 2 * Math.Abs(coords[1]), horizontal = Math.Max(coords[2], coords[8]);
            var bb = new BakedBillboard
            {
                AlphaTest = *(float*)(b + 0x0C),
                HalfWidth = horizontal * 0.5f,
                Top = vertical,
                Bottom = 0,
            };
            int count = *(int*)(images + 8);
            float* uv = (float*)Ptr(images + 0x0C);
            bb.ImageUvs = new float[8 * count];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < 8; k++) bb.ImageUvs[8 * i + k] = uv[8 * i + k];   // 0x20 bytes per image
            return bb;
        }

        void ReadCollision(uint tree, BakedTree t)
        {
            uint vec = Ptr(tree + TreeCollision);
            if (vec == 0 || Ptr(vec + 4) == 0) return;
            int count = (int)((Ptr(vec + 8) - Ptr(vec + 4)) / 0x1C);
            int type;
            float* pos = stackalloc float[3];
            float* dims = stackalloc float[3];
            for (int i = 0; i < count; i++)
            {
                type = -1;
                _call.Call(FnGetCollisionObject, new NativeCall.Regs { Ecx = (uint)i }, tree, (uint)&type, (uint)pos, (uint)dims);
                var c = new BakedCollision { Type = type };
                for (int k = 0; k < 3; k++) { c.Position[k] = pos[k]; c.Dimensions[k] = dims[k]; }
                t.Collision.Add(c);
            }
        }

        static uint Ptr(uint address) => *(uint*)address;

        static float[] Copy(byte* field, int count)
        {
            float* src = (float*)*(uint*)field;
            var a = new float[count];
            if (src != null) for (int i = 0; i < count; i++) a[i] = src[i];
            return a;
        }

        static byte[] CopyBytes(byte* field, int count)
        {
            byte* src = (byte*)*(uint*)field;
            var a = new byte[count];
            if (src != null) for (int i = 0; i < count; i++) a[i] = src[i];
            return a;
        }
    }
}
