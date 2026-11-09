using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using G3.Formats.Genome;

namespace G3.Formats.Animation
{
    /// <summary>EMotionFX "LMA" chunk ids shared by actors (FXA) and motions (LMA) — Docs/formats-animation.md §2.</summary>
    public enum EmfxChunk
    {
        Node = 0, MotionPart = 1, Anim = 2, Mesh = 3, SkinningInfo = 4, CollisionMesh = 5, Material = 6,
        MaterialLayer = 7, Limit = 8, PhysicsInfo = 9, MeshExpressionPart = 10, ExpressionMotionPart = 11,
        PhonemeMotionData = 12, FxMaterial = 13, SceneInfo = 16,
    }

    /// <summary>Skeleton node (chunk 0 v3). Local transform relative to <see cref="Parent"/>.</summary>
    public sealed class ActorNode
    {
        public string Name;
        public string ParentName;
        public int Parent = -1;
        public V3 Position;
        public Q4 Rotation;
        public Q4 ScaleOrient;
        public V3 Scale;
        public V3 Shear;
    }

    /// <summary>One material group of a mesh chunk (EMotionFX "submesh").</summary>
    public sealed class ActorSubMesh
    {
        public int MaterialId;
        public int UvSets;
        public int VertexStart, VertexCount;   // into the mesh's vertex arrays
        public int IndexStart, IndexCount;     // into ActorMesh.Indices (already offset by VertexStart)
    }

    /// <summary>
    /// Skinned (or rigid) mesh (chunk 3 v3). Vertices are split per submesh ("corners"); <see cref="OrgVertex"/>
    /// links each back to the welded original vertex that carries the skin weights.
    /// Positions/normals are in actor (model) space, centimetres; triangles are D3D clockwise.
    /// </summary>
    public sealed class ActorMesh
    {
        public int Node;
        public int OrgVertexCount;
        public int UvSets;
        public bool IsCollisionMesh;
        public int[] OrgVertex;
        public V3[] Positions;
        public V3[] Normals;
        public float[] Uv0;                 // u,v pairs (null without UV sets)
        public float[] Uv1;
        public int[] Indices;               // triangle list over the mesh's vertex arrays
        public readonly List<ActorSubMesh> SubMeshes = new List<ActorSubMesh>();
        /// <summary>Per original vertex: influences (node index, weight). Null when the mesh has no skin chunk.</summary>
        public SkinInfluence[][] Skin;
    }

    public struct SkinInfluence
    {
        public ushort Node;
        public float Weight;
        public SkinInfluence(ushort node, float weight) { Node = node; Weight = weight; }
    }

    /// <summary>Material chunk (6 v5) plus its layers (chunk 7).</summary>
    public sealed class ActorMaterial
    {
        public float[] Ambient, Diffuse, Specular, Emissive; // rgb
        public float Shine, ShineStrength, Opacity, Ior;
        public bool DoubleSided, Wireframe;
        public char TransparencyType;
        public string Name;
        public string ShaderFile;
        public readonly List<MaterialLayer> Layers = new List<MaterialLayer>();
    }

    public sealed class MaterialLayer
    {
        public int MapType;        // 1 diffuse, 3 normal (bump), 9 specular (observed)
        public byte[] Raw;         // the 27 bytes between type and name, kept verbatim
        public string Texture;
    }

    /// <summary>MaterialsLoDMappings entry: material id → .xshmat name (what the engine renders with).</summary>
    public struct MaterialRef
    {
        public int Lod, MaterialIndex;
        public string Name;
    }

    /// <summary>One <c>eCWrapper_emfx2Actor</c>: "gena" v4 + FXA 1.1 chunk stream + Genome extras.</summary>
    public sealed class EmfxActor
    {
        public readonly List<ActorNode> Nodes = new List<ActorNode>();
        public readonly List<ActorMesh> Meshes = new List<ActorMesh>();
        public readonly List<ActorMaterial> Materials = new List<ActorMaterial>();
        public readonly List<MaterialRef> MaterialRefs = new List<MaterialRef>();
        /// <summary>Per LoD per vertex (ARGB) — "ambient occlusion"/vertex colour attribute.</summary>
        public readonly List<uint[]> VertexColors = new List<uint[]>();
        /// <summary>Per LoD per vertex tangent.</summary>
        public readonly List<V3[]> Tangents = new List<V3[]>();
        /// <summary>Chunks we keep only as counts (id → count), e.g. limits (8), physics (9), scene info (16), morph parts (10).</summary>
        public readonly Dictionary<int, int> OtherChunks = new Dictionary<int, int>();

        public int FindNode(string name)
        {
            for (int i = 0; i < Nodes.Count; i++)
                if (string.Equals(Nodes[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Material (.xshmat stem) for a submesh material id, resolved like the engine (refs first, then material chunk names).</summary>
        public string MaterialName(int materialId)
        {
            foreach (var r in MaterialRefs)
                if (r.MaterialIndex == materialId && !string.IsNullOrEmpty(r.Name))
                    return Path.GetFileNameWithoutExtension(r.Name);
            if (materialId >= 0 && materialId < Materials.Count)
                return Materials[materialId].Name.Replace("|", "");
            return null;
        }
    }

    public sealed class LookAtConstraint
    {
        public string Node;
        public float InterpolationSpeed;
        public V3 Min, Max;            // radians
    }

    /// <summary>.xact — eCResourceAnimationActor_PS (Docs/formats-animation.md §1).</summary>
    public sealed class ActorResource
    {
        public string Name;
        public int Version;
        public uint ResourceSize;
        public float Priority;
        public long NativeFileTime;
        public uint NativeFileSize;
        public V3 BoundsMin, BoundsMax;
        public readonly List<LookAtConstraint> LookAt = new List<LookAtConstraint>();
        public readonly List<EmfxActor> Lods = new List<EmfxActor>();
        public EmfxActor Actor;
    }

    public static class Xact
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static ActorResource Read(byte[] data, string name = null)
        {
            if (data == null || data.Length == 0) return null;
            var g = new GenomeReader(data);
            if (!g.IsGenomFile) throw new InvalidDataException($"{name}: not a GENOMFLE resource");
            var r = g.R;
            var a = new ActorResource { Name = name };
            a.Version = r.ReadUInt16();
            if (a.Version != 54) throw new InvalidDataException($"{name}: actor version {a.Version}");
            a.ResourceSize = r.ReadUInt32();
            a.Priority = r.ReadSingle();
            a.NativeFileTime = r.ReadInt64();
            a.NativeFileSize = r.ReadUInt32();
            a.BoundsMin = V(r); a.BoundsMax = V(r);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                a.LookAt.Add(new LookAtConstraint { Node = g.Str(), InterpolationSpeed = r.ReadSingle(), Min = V(r), Max = V(r) });
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) a.Lods.Add(ReadWrapper(g, name));
            a.Actor = ReadWrapper(g, name);
            if (g.Position != g.PayloadEnd)
                throw new InvalidDataException($"{name}: actor ends at 0x{g.Position:x}, payload ends at 0x{g.PayloadEnd:x}");
            return a;
        }

        static EmfxActor ReadWrapper(GenomeReader g, string name)
        {
            var r = g.R;
            if (r.ReadUInt32() != 0x616E6567) throw new InvalidDataException($"{name}: no 'gena' wrapper at 0x{g.Position - 4:x}");
            int ver = r.ReadUInt16();
            if (ver != 4) throw new InvalidDataException($"{name}: wrapper version {ver}");
            int size = r.ReadInt32();
            long end = g.Position + size;
            if (Latin1.GetString(r.ReadBytes(4)) != "FXA ") throw new InvalidDataException($"{name}: no FXA");
            int hi = r.ReadByte(), lo = r.ReadByte();
            if (hi != 1 || lo != 1) throw new InvalidDataException($"{name}: FXA {hi}.{lo}");

            var act = new EmfxActor();
            while (g.Position < end)
            {
                int id = r.ReadInt32();
                int csize = r.ReadInt32();
                int cver = r.ReadInt32();
                long cend = g.Position + csize;
                if (cend > end) throw new InvalidDataException($"{name}: chunk {id} runs past the FXA");
                switch ((EmfxChunk)id)
                {
                    case EmfxChunk.Node when cver == 3:
                        act.Nodes.Add(new ActorNode
                        {
                            Position = V(r), Rotation = Q(r), ScaleOrient = Q(r), Scale = V(r), Shear = V(r),
                            Name = S32(r), ParentName = S32(r),
                        });
                        break;
                    case EmfxChunk.Mesh when cver == 3:
                        act.Meshes.Add(ReadMesh(r, name));
                        break;
                    case EmfxChunk.SkinningInfo when cver == 1:
                    {
                        int node = r.ReadInt32();
                        ActorMesh mesh = null;
                        for (int i = act.Meshes.Count - 1; i >= 0; i--) if (act.Meshes[i].Node == node) { mesh = act.Meshes[i]; break; }
                        if (mesh == null) throw new InvalidDataException($"{name}: skin for node {node} without mesh");
                        var skin = new SkinInfluence[mesh.OrgVertexCount][];
                        for (int v = 0; v < skin.Length; v++)
                        {
                            int k = r.ReadByte();
                            var inf = new SkinInfluence[k];
                            for (int j = 0; j < k; j++)
                            {
                                ushort b = r.ReadUInt16(); r.ReadUInt16();
                                inf[j] = new SkinInfluence(b, r.ReadSingle());
                            }
                            skin[v] = inf;
                        }
                        mesh.Skin = skin;
                        break;
                    }
                    case EmfxChunk.Material when cver == 5:
                    {
                        var m = new ActorMaterial
                        {
                            Ambient = F(r, 3), Diffuse = F(r, 3), Specular = F(r, 3), Emissive = F(r, 3),
                            Shine = r.ReadSingle(), ShineStrength = r.ReadSingle(), Opacity = r.ReadSingle(), Ior = r.ReadSingle(),
                            DoubleSided = r.ReadByte() != 0, Wireframe = r.ReadByte() != 0, TransparencyType = (char)r.ReadByte(),
                        };
                        r.ReadByte();
                        m.Name = S32(r);
                        m.ShaderFile = S32(r);
                        act.Materials.Add(m);
                        break;
                    }
                    case EmfxChunk.MaterialLayer:
                    {
                        var l = new MaterialLayer { MapType = r.ReadByte(), Raw = r.ReadBytes(27), Texture = S32(r) };
                        if (act.Materials.Count > 0) act.Materials[act.Materials.Count - 1].Layers.Add(l);
                        break;
                    }
                    case EmfxChunk.SceneInfo when cver == 1:
                    {
                        // Exporter info: 8 bytes, then source application, source .max path and export date (S32 each).
                        // The size field is not reliable: G3_Head_Hero_Diego_01 / _Animated_01 store 128 / 144 for
                        // 152 / 186 bytes (a longer .max path than the size was computed for). EMFX reads the fields, so
                        // the chunk ends where they end; trusting the size failed the whole actor: Diego had no head (fix-15).
                        long start = g.Position;
                        try
                        {
                            r.ReadBytes(8);
                            S32(r); S32(r); S32(r);
                            if (g.Position <= end) cend = g.Position;
                        }
                        catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException) { }
                        if (g.Position != cend) g.Position = start;
                        act.OtherChunks[id] = act.OtherChunks.TryGetValue(id, out var ci) ? ci + 1 : 1;
                        break;
                    }
                    default:
                        act.OtherChunks[id] = act.OtherChunks.TryGetValue(id, out var c) ? c + 1 : 1;
                        break;
                }
                if (g.Position > cend) throw new InvalidDataException($"{name}: chunk {id} v{cver} overran by {g.Position - cend}");
                bool parsed = (id == 0 && cver == 3) || (id == 3 && cver == 3) || (id == 4 && cver == 1) || (id == 6 && cver == 5) || id == 7;
                if (parsed && g.Position != cend)
                    throw new InvalidDataException($"{name}: chunk {id} v{cver} left {cend - g.Position} bytes");
                g.Position = cend;
            }
            if (g.Position != end) throw new InvalidDataException($"{name}: FXA chunks end at 0x{g.Position:x}, expected 0x{end:x}");

            for (int i = 0; i < act.Nodes.Count; i++)
            {
                var p = act.Nodes[i].ParentName;
                if (string.IsNullOrEmpty(p)) continue;
                for (int j = 0; j < act.Nodes.Count; j++)
                    if (act.Nodes[j].Name == p) { act.Nodes[i].Parent = j; break; }
            }

            int nm = r.ReadInt32();
            for (int i = 0; i < nm; i++)
                act.MaterialRefs.Add(new MaterialRef { Lod = r.ReadUInt16(), MaterialIndex = r.ReadUInt16(), Name = g.Str() });
            int lods = g.ArrayCount();          // bTArray prefix: u8 1 + u32 count
            for (int i = 0; i < lods; i++)
            {
                int k = g.ArrayCount();
                var col = new uint[k];
                for (int j = 0; j < k; j++) col[j] = r.ReadUInt32();
                act.VertexColors.Add(col);
            }
            for (int i = 0; i < lods; i++)
            {
                var t = new V3[act.VertexColors[i].Length];
                for (int j = 0; j < t.Length; j++) t[j] = V(r);
                act.Tangents.Add(t);
            }
            return act;
        }

        static ActorMesh ReadMesh(BinaryReader r, string name)
        {
            var m = new ActorMesh { Node = r.ReadInt32(), OrgVertexCount = r.ReadInt32() };
            int totalVerts = r.ReadInt32();
            int totalIndices = r.ReadInt32();
            int subs = r.ReadInt32();
            m.UvSets = r.ReadInt32();
            m.IsCollisionMesh = r.ReadByte() != 0;
            r.ReadBytes(3);
            m.OrgVertex = new int[totalVerts];
            m.Positions = new V3[totalVerts];
            m.Normals = new V3[totalVerts];
            if (m.UvSets > 0) m.Uv0 = new float[totalVerts * 2];
            if (m.UvSets > 1) m.Uv1 = new float[totalVerts * 2];
            m.Indices = new int[totalIndices];
            int vb = 0, ib = 0;
            for (int s = 0; s < subs; s++)
            {
                var sm = new ActorSubMesh { MaterialId = r.ReadByte(), UvSets = r.ReadByte() };
                r.ReadBytes(2);
                sm.IndexCount = r.ReadInt32();
                sm.VertexCount = r.ReadInt32();
                sm.VertexStart = vb; sm.IndexStart = ib;
                if (vb + sm.VertexCount > totalVerts || ib + sm.IndexCount > totalIndices)
                    throw new InvalidDataException($"{name}: submesh exceeds mesh totals");
                for (int v = 0; v < sm.VertexCount; v++)
                {
                    int i = vb + v;
                    int org = r.ReadInt32();
                    if (org < 0 || org >= m.OrgVertexCount) throw new InvalidDataException($"{name}: org vertex {org} >= {m.OrgVertexCount}");
                    m.OrgVertex[i] = org;
                    m.Positions[i] = V(r);
                    m.Normals[i] = V(r);
                    for (int u = 0; u < m.UvSets; u++)
                    {
                        float x = r.ReadSingle(), y = r.ReadSingle();
                        if (u == 0) { m.Uv0[i * 2] = x; m.Uv0[i * 2 + 1] = y; }
                        else if (u == 1) { m.Uv1[i * 2] = x; m.Uv1[i * 2 + 1] = y; }
                    }
                }
                if (sm.IndexCount % 3 != 0) throw new InvalidDataException($"{name}: {sm.IndexCount} indices");
                for (int k = 0; k < sm.IndexCount; k++)
                {
                    int idx = r.ReadInt32();
                    if (idx < 0 || idx >= sm.VertexCount) throw new InvalidDataException($"{name}: index {idx} >= {sm.VertexCount}");
                    m.Indices[ib + k] = idx + vb;
                }
                vb += sm.VertexCount; ib += sm.IndexCount;
                m.SubMeshes.Add(sm);
            }
            if (vb != totalVerts || ib != totalIndices) throw new InvalidDataException($"{name}: mesh totals {vb}/{totalVerts} {ib}/{totalIndices}");
            return m;
        }

        internal static V3 V(BinaryReader r) => new V3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        internal static Q4 Q(BinaryReader r) => new Q4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static float[] F(BinaryReader r, int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.ReadSingle(); return a; }

        internal static string S32(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 4096) throw new InvalidDataException($"string length {n}");
            return Latin1.GetString(r.ReadBytes(n));
        }
    }
}
