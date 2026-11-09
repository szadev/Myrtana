using System;
using System.Collections.Generic;
using System.Numerics;

namespace G3.Unity
{
    /// <summary>
    /// Knobs of the terrain relief (Docs/remaster-world.md section 13), JSON <c>Resources/G3TerrainRelief.json</c>
    /// (kept equal to these defaults by a test; <c>G3TerrainRelief.Reload()</c> re-reads it in play mode).
    /// </summary>
    [Serializable]
    public sealed class ReliefSettings
    {
        public bool relief = true;            // master (with the Terrain remaster part)
        public bool near = true;              // dense re-tessellation round the camera (and the focus points)
        // far mesh (camera-independent, built once per cell)
        public float farTolerance = 0.06f;    // m: Phong chord error that splits an edge
        public float farSpacing = 4f;         // m: sample spacing on detailed (steep) ground in the far mesh
        public int farMaxSplit = 4;
        // near region
        public float nearSpacing = 0.4f;     // m at the camera
        public float nearSlope = 0.05f;       // spacing grows by this per metre of distance (reaches farSpacing at farSpacing / nearSlope)
        public int nearMaxSplit = 24;         // per source edge
        public float recenter = 4f;           // m: the camera refine point moves when the camera is this far from it (fix-171: hysteresis, was a 6 m grid)
        public float nearMargin = 8f;         // m (fix-171): the near mesh is as dense as the camera at its centre would need this much closer (the
                                              // live camera can move off the centre until the next batch: the shader's distance fade never outruns the mesh)
        public float macroLambda = 8f;        // m (fix-171): detail octaves at least this long are the same in every mesh (band of the far spacing);
                                              // shorter ones are faded by the live camera distance in the shader (no shape change at rebuilds)
        public float skirt = 1f;              // m (fix-171): skirts below the cell borders (covers a border between cells in different states)
        public float farRadius = 450f;        // m: cells closer than this to the camera get a relief copy (released 300 m further out)
        // surface
        public float phongWalk = 0.5f;        // Phong tessellation strength on edges of walkable faces
        public float phongSteep = 0.75f;      // ... on steep ground / rock
        public float phongCapWalk = 0.04f;    // m: largest Phong bulge over a walkable face's edge (the visual-vs-collision gap)
        public float phongCapWalkOpen = 0.25f; // m: ... away from paths, buildings, objects, rocks, trees (characters stand on the visual ground)
        public float phongCapWalkRemote = 0.5f; // m: ... on remote ground
        public float phongCapSteep = 3f;    // m: ... elsewhere
        public float walkNy = 0.6f;           // face normal.y from which a face is walkable (G3 limit 49 deg = 0.656; a margin); baked into the facts table
        public float ampWalk = 0.025f;        // m: detail amplitude on walkable ground by paths, buildings, objects, NPC spots
        public float ampWalkOpen = 0.12f;     // m: ... on walkable ground away from them (openStart..openEnd m)
        public float ampWalkRemote = 0.25f;   // m: ... on remote ground (ridges, mountain tops: remoteStart..remoteEnd m from any gameplay)
        // swells (pass 2): broad hummocks and hollows on walkable ground (characters, grass and corpses follow the visual surface,
        // G3ReliefGround); amplitude by the distance to what G3 placed on its surface (paths, buildings, objects, rocks, trees)
        public float swell = 1f;              // weight
        public float swellScale = 30f;        // m: largest wavelength
        public int swellOctaves = 3;
        public float swellWalk = 0.03f;       // m: next to paths / buildings / objects / rocks / trees
        public float swellWalkOpen = 0.9f;    // m: open ground (swellStart..swellEnd m from them)
        public float swellWalkRemote = 1.0f; // m: remote ground (remoteStart..remoteEnd m)
        public float swellStart = 2f, swellEnd = 10f;     // m from paths / buildings / objects (rocks, trees: x3 nearer, facts Open)
        public float openStart = 6f, openEnd = 20f;       // m from paths / buildings / objects / NPC spots (facts Open)
        public float remoteStart = 50f, remoteEnd = 100f; // m
        public float ampSteep = 1.5f;         // m: on steep faces
        public float steepStart = 0.62f;      // normal.y where the steep amplitude starts (49 deg walk limit = 0.656)
        public float steepEnd = 0.42f;        // normal.y where it is full
        public float gully = 1f;              // weights of the three detail terms
        public float strata = 0.8f;
        public float lumps = 0.35f;
        public float gullyScale = 24f;        // m: largest gully wavelength
        public int gullyOctaves = 6;
        public float strataScale = 2.6f;      // m: band height
        public float lumpScale = 4f;          // m
        public int lumpOctaves = 4;
        // fix-174 clearance (facts table, ReliefClearance): open edges of the terrain union (G3's tunnel / cave holes, non-conforming
        // cell seams) never move; elsewhere detail + swell and the Phong bulge each use at most this share of the room to the next
        // terrain sheet (along the normal) or placed rock / building mesh (any direction): sheets never cross, meshes stay fitted
        // (clearShare 0 = no cap: the A/B of the runner)
        public bool pinOpenEdges = true;
        public float clearShare = 0.25f;
        // the low-poly horizon (G3LowpolyBackdrop, beyond the loaded cells): Phong only, coarse tolerance
        public bool backdrop = true;
        public float backdropTolerance = 1.5f;  // m: chord error that splits an edge
        public int backdropMaxSplit = 2;
        public float backdropCap = 4f;          // m: largest Phong bulge per edge
        // dune-1 (Docs/remaster-world.md section 23): Varant's baked dune field (Resources/G3DuneField.bytes, G3Cli dune bake);
        // the ground there = G3 + the field's change, the noise relief and Phong faded by its weight, the shading normal
        // from the field's slope (sharp brinks). Off (or no field) = exactly the relief without dunes.
        public bool dunes = true;
        public float duneScale = 1f;          // x the baked change (0..1.5; the bake caps it at 1.5 m)
        public float duneFarSpacing = 3f;     // m: far-mesh spacing on dune sand (2 m: +0.6 ms GPU in the editor view, 3 m: +0.2-0.3 ms)
        public float duneNoiseCut = 1f;       // share of the noise swells / gullies / lumps / strata removed where the dune weight is 1
        public float dunePhongCut = 1f;       // share of the Phong bulge removed on dune edges (Phong rounds the brinks)
        public float duneNormal = 1f;         // x the change's slope added to the shading normal

        /// <summary>dune-1: the baked dune field (not json; set by G3TerrainRelief / tests). Null = no dunes.</summary>
        public DuneField Dunes { get; set; }

        /// <summary>The field the relief evaluates: <see cref="Dunes"/> while <see cref="dunes"/> is on.</summary>
        public DuneField DuneFieldOn => dunes && duneScale > 0 ? Dunes : null;

        public static ReliefSettings Default => new ReliefSettings();

        public ReliefSettings Clamped()
        {
            var s = (ReliefSettings)MemberwiseClone();
            s.farTolerance = C(s.farTolerance, 0.005f, 2); s.farSpacing = C(s.farSpacing, 0.5f, 50);
            s.farMaxSplit = Math.Max(1, Math.Min(16, s.farMaxSplit));
            s.nearSpacing = C(s.nearSpacing, 0.05f, s.farSpacing); s.nearSlope = C(s.nearSlope, 0.001f, 1);
            s.nearMaxSplit = Math.Max(1, Math.Min(64, s.nearMaxSplit)); s.recenter = C(s.recenter, 0.5f, 100); s.farRadius = C(s.farRadius, 50, 20000);
            s.nearMargin = C(s.nearMargin, 0, 50); s.macroLambda = C(s.macroLambda, 1, 1000); s.skirt = C(s.skirt, 0, 10);
            s.phongWalk = C(s.phongWalk, 0, 1); s.phongSteep = C(s.phongSteep, 0, 1);
            s.phongCapWalk = C(s.phongCapWalk, 0, 1); s.phongCapWalkOpen = C(s.phongCapWalkOpen, 0, 2); s.phongCapWalkRemote = C(s.phongCapWalkRemote, 0, 10); s.phongCapSteep = C(s.phongCapSteep, 0, 10);
            s.walkNy = C(s.walkNy, 0, 1);
            s.ampWalk = C(s.ampWalk, 0, 0.5f); s.ampWalkOpen = C(s.ampWalkOpen, 0, 1); s.ampWalkRemote = C(s.ampWalkRemote, 0, 4);
            s.openStart = C(s.openStart, 0, 100); s.openEnd = Math.Max(C(s.openEnd, 0, 100), s.openStart + 0.5f);
            s.remoteStart = C(s.remoteStart, 0, 100); s.remoteEnd = Math.Max(C(s.remoteEnd, 0, 100), s.remoteStart + 0.5f); s.ampSteep = C(s.ampSteep, 0, 4);
            s.steepStart = C(s.steepStart, -1, 1); s.steepEnd = Math.Min(C(s.steepEnd, -1, 1), s.steepStart - 0.01f);
            s.gully = C(s.gully, 0, 4); s.strata = C(s.strata, 0, 4); s.lumps = C(s.lumps, 0, 4);
            s.swell = C(s.swell, 0, 4); s.swellScale = C(s.swellScale, 2, 500); s.swellOctaves = Math.Max(0, Math.Min(6, s.swellOctaves));
            s.swellStart = C(s.swellStart, 0, 100); s.swellEnd = Math.Max(C(s.swellEnd, 0, 100), s.swellStart + 0.5f);
            s.swellWalk = C(s.swellWalk, 0, 1); s.swellWalkOpen = C(s.swellWalkOpen, 0, 3); s.swellWalkRemote = C(s.swellWalkRemote, 0, 5);
            s.gullyScale = C(s.gullyScale, 0.5f, 200); s.gullyOctaves = Math.Max(0, Math.Min(10, s.gullyOctaves));
            s.strataScale = C(s.strataScale, 0.2f, 50); s.lumpScale = C(s.lumpScale, 0.2f, 100); s.lumpOctaves = Math.Max(0, Math.Min(8, s.lumpOctaves));
            s.clearShare = C(s.clearShare, 0, 10);
            s.backdropTolerance = C(s.backdropTolerance, 0.01f, 20); s.backdropMaxSplit = Math.Max(1, Math.Min(8, s.backdropMaxSplit)); s.backdropCap = C(s.backdropCap, 0, 50);
            s.duneScale = C(s.duneScale, 0, 1.5f); s.duneFarSpacing = C(s.duneFarSpacing, 0.5f, 50);
            s.duneNoiseCut = C(s.duneNoiseCut, 0, 1); s.dunePhongCut = C(s.dunePhongCut, 0, 1); s.duneNormal = C(s.duneNormal, 0, 1);
            return s;
        }

        static float C(float v, float lo, float hi) => float.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v;

        /// <summary>The settings of the horizon backdrop: Phong only (no detail, nobody walks there), coarse tolerance and caps.</summary>
        public ReliefSettings ForBackdrop()
        {
            var b = (ReliefSettings)MemberwiseClone();
            b.ampWalk = b.ampWalkOpen = b.ampWalkRemote = b.ampSteep = 0;
            b.swellWalk = b.swellWalkOpen = b.swellWalkRemote = 0;
            b.phongWalk = b.phongSteep;
            b.phongCapWalk = b.phongCapWalkOpen = b.phongCapSteep = backdropCap;
            b.farTolerance = backdropTolerance; b.farMaxSplit = backdropMaxSplit;
            b.near = false;
            b.dunes = false;   // the horizon pieces are not in the dune field's frame of trust (other meshes, other heights)
            return b;
        }

        /// <summary>Distance at which the camera's near spacing reaches <see cref="farSpacing"/> (the near region's radius).</summary>
        public float NearRadius => farSpacing / nearSlope;
    }

    /// <summary>
    /// A point that wants the terrain dense round it: spacing <see cref="Spacing"/> m at the point, growing by
    /// <see cref="Slope"/> per metre; it has no effect from where the spacing reaches the far spacing.
    /// The camera is one (<see cref="ReliefSettings.nearSpacing"/>); the snow footprints add the hero (focus).
    /// </summary>
    public struct ReliefPoint
    {
        public Vector3 Position;
        public float Spacing, Slope;
        /// <summary>The largest spacing it asks for (its radius ends there); <see cref="float.MaxValue"/> = up to the far spacing.</summary>
        public float MaxSpacing;
        /// <summary>True: it also sets the detail's band limit (the camera); false: density only (a focus such as the snow's hero).</summary>
        public bool Shape;
        /// <summary>fix-171: the spacing starts growing only beyond this distance (m; the camera's <see cref="ReliefSettings.nearMargin"/>).</summary>
        public float Margin;

        public ReliefPoint(Vector3 position, float spacing, float slope, bool shape = true, float maxSpacing = float.MaxValue, float margin = 0)
        {
            Position = position; Spacing = spacing; Slope = slope; Shape = shape; MaxSpacing = maxSpacing; Margin = margin;
        }

        /// <summary>The radius beyond which the point changes nothing.</summary>
        public float Radius(float farSpacing)
        {
            float top = Math.Min(farSpacing, MaxSpacing);
            return Spacing >= top ? 0 : Margin + (top - Spacing) / Math.Max(Slope, 1e-4f);
        }

        /// <summary>Wanted spacing at distance <paramref name="d"/> (≥ far spacing outside the radius).</summary>
        public float SpacingAt(float d) => Spacing + Slope * Math.Max(0, d - Margin);
    }

    /// <summary>One output vertex of the relief mesh (matches the runtime vertex layout).</summary>
    public struct ReliefVertex
    {
        public Vector3 Position, Normal;
        public Vector4 Tangent;
        public uint Color;          // RGBA bytes, R in the low byte (Color32 layout)
        public Vector2 Uv0, Uv1;
        /// <summary>x = how far the visual surface is above the collision triangle along its normal (m, signed); y = relief mask
        /// (0 = untouched); z = spacing wanted by a focus point here (m; 0 = no focus: the snow displaces only where 0 &lt; z &lt; 0.24).</summary>
        public Vector3 Uv3;
        // fix-171: the live distance fade (shader G3ReliefMorph.hlsl). The GPU position is Position - FineOffset * Ng (the
        // coarse surface: macro detail, Phong); the shader adds the fine octaves (Fine) and the strata (StrataAmp) with the
        // band of the live camera distance, never finer than Sigma (the spacing this vertex was sampled for).
        public Vector3 Ng;            // displacement direction (smooth normal of the welded geometry: the same for every copy of a point)
        public Vector3 NormalCoarse;  // shading normal without the fine octaves (the shader blends towards it with distance)
        public float FineOffset;      // m along Ng: the fine octaves + strata at Sigma (part of Position)
        public float StrataAmp;       // m: strata amplitude (0 = none)
        public float Sigma;           // m: the sampling spacing of this vertex (uv3.w)
        public FineOctaves Fine;      // m: the raw fine octaves (TerrainRelief.FineSlots)
    }

    /// <summary>fix-171: the detail octaves shorter than <see cref="ReliefSettings.macroLambda"/>, faded in the shader (7 slots).</summary>
    public struct FineOctaves
    {
        public float A, B, C, D, E, F, G;
        public float this[int i]
        {
            get { switch (i) { case 0: return A; case 1: return B; case 2: return C; case 3: return D; case 4: return E; case 5: return F; default: return G; } }
            set { switch (i) { case 0: A = value; break; case 1: B = value; break; case 2: C = value; break; case 3: D = value; break; case 4: E = value; break; case 5: F = value; break; default: G = value; break; } }
        }
        public const int Count = 7;
    }

    /// <summary>fix-171: one detail term's split into the per-octave contributions (TerrainRelief.Detail).</summary>
    public struct ReliefDetail
    {
        public float Coarse;      // m: macro octaves (fixed band) + fine octaves without a slot (at the given spacing)
        public float FineBatch;   // m: the slotted fine octaves at the given spacing
        public float StrataAmp;   // m: amplitude of the strata (evaluated at the coarse point by the caller / the shader)
        public FineOctaves Fine;  // m: the slotted fine octaves at full weight
    }

    /// <summary>
    /// One terrain mesh prepared for the relief: the source arrays (mesh space = world space for landscape cells) plus the
    /// welded topology the surface is defined on. Build once per cell (<see cref="Prepare"/>), then
    /// <see cref="TerrainRelief.BuildFar"/> / <see cref="TerrainRelief.BuildNear"/>.
    /// </summary>
    public sealed class ReliefMesh
    {
        // source
        public Vector3[] Pos, Nrm;
        public Vector4[] Tan;               // may be null
        public Vector2[] Uv0, Uv1;          // Uv1 may be null
        public uint[] Col;                  // may be null (white)
        public int[][] Sub;                 // triangle lists per submesh (Unity winding)
        public float[] SubAmp, SubPhong;    // per submesh material masks (ReliefMaterials)
        public float[] SubLooseA, SubLooseB; // fx-1: per submesh loose-sand share of layer A / B (null = none; ReliefGroundMap.Sand)

        // prepared
        public int[] Weld;                  // vertex -> welded id
        public int WeldCount;
        public Vector3[] WPos, WNrm;        // welded position / averaged normal (geometry)
        public float[] WAmp;                // detail amplitude (m)
        public float[] WSwell;              // swell amplitude (m, walkable ground)
        public float[] WDune;               // dune-1: the dune gate of a welded vertex x the field's weight there (edges: Phong cut, far split); null = no dunes
        public float[] WDuneGate;           // dune-1: the gate alone (0 on masked / pinned ground, less where the clearance is short)
        public ReliefTriGrid Grid;          // XZ buckets of the source triangles (ground queries, ReliefGround)
        public byte[] WFlags;               // ReliefFacts flags
        public int TriCount;
        public int[] TriV, TriSub, TriEdge; // 3 source vertices, submesh, 3 edge ids (edge k = vertex k -> k+1)
        public Vector4[] TriSphere;         // xyz centre, w radius
        public int EdgeCount;
        public int[] EdgeA, EdgeB;          // canonical welded endpoints (A < B by position)
        public float[] EdgeLen;
        public int[] EdgeFar;               // far split count
        public Vector3[] EdgeTerm;          // capped Phong term of the edge (midpoint bulge = |term| / 4)
        public bool[] EdgeBorder;           // fix-171: an open edge on the cell's outline (gets a skirt)
        public Vector3 BoundsMin, BoundsMax;

        public bool Prepared => Weld != null;
    }

    /// <summary>Output of one build: vertices, per submesh indices, and for the far mesh each triangle's index range.</summary>
    public sealed class ReliefOutput
    {
        public ReliefVertex[] Vertices = new ReliefVertex[1024];
        public int VertexCount;
        public List<int>[] Indices;
        public int[] TriStart, TriCount;    // far: per source triangle, its range in Indices[TriSub[t]]
        public bool[] Near;                 // near: the source triangles rebuilt (null = none)
        public int NearTriangles;
        public float MaxOffsetWalk, MaxOffsetAll;   // |visual - collision| (m) on walkable faces / everywhere
        public double SumOffsetWalk; public long CountWalk;
        public Vector3 MaxWalkAt;           // where the largest walkable gap is (audit)
        public long[] WalkHistogram = new long[64];   // walkable |gap| in 1 cm bins (last bin = 63 cm and more)

        public void Reset(int submeshes)
        {
            VertexCount = 0; NearTriangles = 0; MaxOffsetWalk = 0; MaxOffsetAll = 0; SumOffsetWalk = 0; CountWalk = 0;
            Array.Clear(WalkHistogram, 0, WalkHistogram.Length);
            if (Indices == null || Indices.Length != submeshes)
            {
                Indices = new List<int>[submeshes];
                for (int i = 0; i < submeshes; i++) Indices[i] = new List<int>();
            }
            else foreach (var l in Indices) l.Clear();
        }

        public int Add(in ReliefVertex v)
        {
            if (VertexCount == Vertices.Length) Array.Resize(ref Vertices, Vertices.Length * 2);
            Vertices[VertexCount] = v;
            return VertexCount++;
        }

        /// <summary>perf-5: empty for <paramref name="m"/> (near flags cleared, buffers kept); returns itself.</summary>
        public ReliefOutput Cleared(ReliefMesh m)
        {
            Reset(m.Sub.Length);
            TriStart = null; TriCount = null;
            MaxWalkAt = default;
            if (Near == null || Near.Length != m.TriCount) Near = new bool[m.TriCount];
            else Array.Clear(Near, 0, Near.Length);
            return this;
        }

        public int IndexCount
        {
            get { int n = 0; if (Indices != null) foreach (var l in Indices) n += l.Count; return n; }
        }
    }

    /// <summary>Per-material masks of the relief: roads, town ground, architecture and dungeons stay exactly as G3 made them.</summary>
    public static class ReliefMaterials
    {
        static readonly string[] Exact =
        {
            "_Path", "Path_", "City", "Cityground", "Architecture", "Marble", "Plates", "Groundstone", "Dungeon", "House",
            "Bricks", "SandWall", "SandGround", "Street", "Road", "Floor", "Stair", "Ocean", "Water",
        };

        /// <summary>(detail amplitude multiplier, Phong strength multiplier) of a terrain material by name.</summary>
        public static (float amp, float phong) Mask(string material)
        {
            if (string.IsNullOrEmpty(material)) return (1, 1);
            foreach (var w in Exact)
                if (material.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return (0, 0);
            if (material.IndexOf("Gravel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                material.IndexOf("Riverstone", StringComparison.OrdinalIgnoreCase) >= 0) return (0.5f, 1);
            return (1, 1);
        }
    }

    /// <summary>
    /// Engine-free terrain relief (Docs/remaster-world.md section 13): G3's coarse landscape cells re-tessellated with
    /// Phong tessellation (Boubekeur and Alexa 2008, curved silhouettes from the vertex normals) and a band-limited detail
    /// height (<see cref="ReliefNoise"/>) along the smooth normal; the collision (the original triangles) is not touched.
    /// <para>Crack-free by construction: an edge's split count and every point on it depend only on the edge's two
    /// welded endpoints (position, normal, masks) and the global refine points, and edge points are evaluated in a canonical
    /// endpoint order, so two triangles sharing an edge (in one cell, across cells, near and far) produce bit-identical
    /// points. Triangle interiors use a barycentric grid of the largest edge level whose boundary points snap onto the
    /// edges' own splits (degenerate triangles dropped).</para>
    /// <para>Far mesh: every triangle at its far split (curvature / steep detail), only wavelengths ≥ 2 far samples.
    /// Near: the triangles within reach of a refine point re-tessellated down to the point's spacing with the fine octaves;
    /// a far triangle never touches a near one with different data on the shared edge (proof in the doc).</para>
    /// </summary>
    public static class TerrainRelief
    {
        // ------------------------------------------------------------------ preparation

        /// <summary>Lexicographic position order (the canonical endpoint order of an edge, identical in every cell).</summary>
        public static int Compare(Vector3 a, Vector3 b)
        {
            if (a.X != b.X) return a.X < b.X ? -1 : 1;
            if (a.Y != b.Y) return a.Y < b.Y ? -1 : 1;
            if (a.Z != b.Z) return a.Z < b.Z ? -1 : 1;
            return 0;
        }

        /// <summary>Steepness 0 (walkable) .. 1 (steep) from the normal's y.</summary>
        public static float Steep(float ny, ReliefSettings s) => ReliefNoise.SmoothStep(s.steepStart, s.steepEnd, ny);

        /// <summary>
        /// Welds by exact position and builds everything the surface needs: per welded vertex the facts
        /// (<paramref name="facts"/>: the world-wide table of <c>G3Cli relief facts</c>, else this mesh's own faces), the
        /// detail amplitude, the canonical edge table with the capped Phong terms and the far split counts.
        /// <para>Every value of a vertex or an edge comes from the facts of its welded positions only, so a border shared by two
        /// cells, and the duplicated triangles where cells overlap, get bit-identical relief in both.</para>
        /// </summary>
        public static void Prepare(ReliefMesh m, ReliefSettings s, ReliefShared facts = null)
        {
            Weld(m);
            int nw = m.WeldCount, tc = m.TriCount;
            if (facts == null || facts.Count != nw) facts = ReliefFacts.Local(m, s);
            m.WNrm = facts.N;

            // edges
            var edges = new Dictionary<long, int>(tc * 2);
            var ea = new List<int>(tc * 2); var eb = new List<int>(tc * 2); var use = new List<int>(tc * 2);
            for (int t = 0; t < tc; t++)
            {
                for (int j = 0; j < 3; j++)
                {
                    int a = m.Weld[m.TriV[t * 3 + j]], b = m.Weld[m.TriV[t * 3 + (j + 1) % 3]];
                    if (Compare(m.WPos[a], m.WPos[b]) > 0) { int x = a; a = b; b = x; }
                    long key = (long)a * nw + b;
                    if (!edges.TryGetValue(key, out int e))
                    {
                        e = ea.Count; edges[key] = e; ea.Add(a); eb.Add(b); use.Add(0);
                    }
                    use[e]++;
                    m.TriEdge[t * 3 + j] = e;
                }
                Vector3 p0 = m.WPos[m.Weld[m.TriV[t * 3]]], p1 = m.WPos[m.Weld[m.TriV[t * 3 + 1]]], p2 = m.WPos[m.Weld[m.TriV[t * 3 + 2]]];
                var c = (p0 + p1 + p2) / 3;
                float r = Math.Max(Vector3.Distance(c, p0), Math.Max(Vector3.Distance(c, p1), Vector3.Distance(c, p2)));
                m.TriSphere[t] = new Vector4(c, r);
            }
            m.EdgeCount = ea.Count;
            m.EdgeA = ea.ToArray(); m.EdgeB = eb.ToArray();

            // per welded vertex amplitude: the smallest over the faces at that position (any cell): the flattest face of each
            // material class decides (amplitude falls with the face's normal.y); a road / town face there makes it 0
            m.WAmp = new float[nw];
            m.WSwell = new float[nw];
            m.WFlags = facts.Flags;
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (int w = 0; w < nw; w++)
            {
                float a = float.MaxValue;
                if ((facts.Flags[w] & ReliefFacts.Amp0) != 0 || Pinned(facts, w, s)) a = 0;
                else
                {
                    float dist = DistOf(facts, w);
                    if (facts.Ny1[w] != ReliefFacts.None) a = Math.Min(a, FaceAmp(ReliefFacts.NyOf(facts.Ny1[w]), dist, s));
                    if (facts.Ny05[w] != ReliefFacts.None) a = Math.Min(a, 0.5f * FaceAmp(ReliefFacts.NyOf(facts.Ny05[w]), dist, s));
                    if (a == float.MaxValue) a = 0;
                }
                m.WAmp[w] = a;
                float sw = 0;
                if ((facts.Flags[w] & ReliefFacts.Amp0) == 0 && !Pinned(facts, w, s) && s.swell > 0)
                {
                    float dist = DistOf(facts, w);
                    sw = float.MaxValue;
                    if (facts.Ny1[w] != ReliefFacts.None) sw = Math.Min(sw, FaceSwell(ReliefFacts.NyOf(facts.Ny1[w]), dist, s));
                    if (facts.Ny05[w] != ReliefFacts.None) sw = Math.Min(sw, 0.5f * FaceSwell(ReliefFacts.NyOf(facts.Ny05[w]), dist, s));
                    if (sw == float.MaxValue) sw = 0;
                }
                // fix-174: detail + swell within their share of the clearance (scaled together: the noise shape stays)
                float room = Room(facts, w, s);
                if (a + sw > room) { float k = room / (a + sw); a *= k; sw *= k; m.WAmp[w] = a; }
                m.WSwell[w] = sw;
                min = Vector3.Min(min, m.WPos[w]); max = Vector3.Max(max, m.WPos[w]);
            }
            m.BoundsMin = min; m.BoundsMax = max;

            // dune-1: the dune gate per welded vertex: 0 on masked (paths, town...) and pinned (open edge) positions, scaled down
            // where the clearance to another sheet / a placed mesh is short, x the field's weight at the position (a function of
            // the welded facts and the position only: the same in every cell)
            var dunes = s.DuneFieldOn;
            m.WDune = null; m.WDuneGate = null;
            if (dunes != null && m.BoundsMax.X >= dunes.X0 && m.BoundsMin.X <= dunes.X0 + dunes.Nx * dunes.Res &&
                m.BoundsMax.Z >= dunes.Z0 && m.BoundsMin.Z <= dunes.Z0 + dunes.Nz * dunes.Res)
            {
                var wd = new float[nw]; var wg = new float[nw];
                bool any = false;
                for (int w = 0; w < nw; w++)
                {
                    if ((facts.Flags[w] & ReliefFacts.Amp0) != 0 || Pinned(facts, w, s)) continue;
                    float room = Room(facts, w, s);
                    float gate = float.IsPositiveInfinity(room) ? 1 : Math.Max(0, Math.Min(1, room / DuneShape.MaxChange));
                    wg[w] = gate;
                    float fw = dunes.WeightAt(m.WPos[w].X, m.WPos[w].Z);
                    wd[w] = gate * fw;
                    any |= wd[w] > 0;
                }
                if (any) { m.WDune = wd; m.WDuneGate = wg; }
            }

            // fix-171: open edges on the cell's outline (x or z at the bounds): a skirt hangs below them, so a neighbour cell in
            // another state (not refined yet, an older near part, G3's own mesh) never shows a gap (Nordmar crack, build 21a)
            m.EdgeBorder = new bool[m.EdgeCount];
            const float tol = 0.05f;
            bool OnSide(Vector3 pa, Vector3 pb) =>
                (Math.Abs(pa.X - min.X) < tol && Math.Abs(pb.X - min.X) < tol) || (Math.Abs(pa.X - max.X) < tol && Math.Abs(pb.X - max.X) < tol) ||
                (Math.Abs(pa.Z - min.Z) < tol && Math.Abs(pb.Z - min.Z) < tol) || (Math.Abs(pa.Z - max.Z) < tol && Math.Abs(pb.Z - max.Z) < tol);
            for (int e = 0; e < m.EdgeCount; e++)
                m.EdgeBorder[e] = use[e] == 1 && OnSide(m.WPos[m.EdgeA[e]], m.WPos[m.EdgeB[e]]);

            // per edge Phong term (canonical A -> B, symmetric): Phong tessellation = flat + sum over edges of
            // b_i b_j E_ij with E_ij = -((Pj - Pi).Ni) Ni - ((Pi - Pj).Nj) Nj; each term is scaled by the edge's strength and
            // capped (midpoint bulge = |E| / 4): an edge whose both ends touch a walkable face (every edge of a walkable face)
            // bulges at most phongCapWalk, so walkable ground stays within a few cm of its collision even next to a cliff
            m.EdgeLen = new float[m.EdgeCount]; m.EdgeFar = new int[m.EdgeCount]; m.EdgeTerm = new Vector3[m.EdgeCount];
            for (int e = 0; e < m.EdgeCount; e++)
            {
                int a = m.EdgeA[e], b = m.EdgeB[e];
                Vector3 pa = m.WPos[a], pb = m.WPos[b], na = m.WNrm[a], nb = m.WNrm[b];
                m.EdgeLen[e] = Vector3.Distance(pa, pb);
                byte fa = facts.Flags[a], fb = facts.Flags[b];
                bool walk = (fa & ReliefFacts.Walk) != 0 && (fb & ReliefFacts.Walk) != 0;
                float strength = ((fa | fb) & ReliefFacts.Phong0) != 0 || Pinned(facts, a, s) || Pinned(facts, b, s) ? 0 : 1;
                float alpha = strength * (walk ? s.phongWalk : s.phongSteep);
                var E = -(Vector3.Dot(pb - pa, na) * na) - Vector3.Dot(pa - pb, nb) * nb;
                var T = alpha * E;
                float distE = Math.Min(DistOf(facts, a), DistOf(facts, b));
                float bulge = 0.25f * T.Length(), cap = walk ? WalkValue(distE, s.phongCapWalk, s.phongCapWalkOpen, s.phongCapWalkRemote, s) : s.phongCapSteep;
                cap = Math.Min(cap, Math.Min(Room(facts, a, s), Room(facts, b, s)));   // fix-174
                if (bulge > cap) T *= bulge > 0 ? cap / bulge : 0;
                if (m.WDune != null) T *= 1 - s.dunePhongCut * 0.5f * (m.WDune[a] + m.WDune[b]);   // dune-1: Phong rounds the brinks
                m.EdgeTerm[e] = T;
                m.EdgeFar[e] = FarSplit(m, e, s);
            }
            m.Grid = ReliefTriGrid.Build(m);
        }

        /// <summary>Detail amplitude (m) of a face with normal.y <paramref name="ny"/>, <paramref name="open"/> 0 (by gameplay) .. 1 (open ground).</summary>
        public static float FaceAmp(float ny, float dist, ReliefSettings s)
        {
            float walk = WalkValue(dist, s.ampWalk, s.ampWalkOpen, s.ampWalkRemote, s);
            return walk + Math.Max(0, s.ampSteep - walk) * Steep(ny, s);
        }

        /// <summary>Swell amplitude (m) of a face with normal.y <paramref name="ny"/>: walkable ground only (fades out where it steepens).</summary>
        public static float FaceSwell(float ny, float dist, ReliefSettings s)
        {
            float o = ReliefNoise.SmoothStep(s.swellStart, s.swellEnd, dist), r = ReliefNoise.SmoothStep(s.remoteStart, s.remoteEnd, dist);
            return (s.swellWalk + (s.swellWalkOpen - s.swellWalk) * o + (s.swellWalkRemote - s.swellWalkOpen) * r) * (1 - Steep(ny, s));
        }

        /// <summary>A walkable-ground value by the distance to gameplay: near .. open (openStart..openEnd m) .. remote (remoteStart..remoteEnd m).</summary>
        public static float WalkValue(float dist, float near, float open, float remote, ReliefSettings s)
        {
            float o = ReliefNoise.SmoothStep(s.openStart, s.openEnd, dist), r = ReliefNoise.SmoothStep(s.remoteStart, s.remoteEnd, dist);
            return near + (open - near) * o + (remote - open) * r;
        }

        /// <summary>fix-174: an open edge of the terrain union (facts Pin): G3 fitted something against it, it must not move.</summary>
        static bool Pinned(ReliefShared f, int w, ReliefSettings s) => s.pinOpenEdges && (f.Flags[w] & ReliefFacts.Pin) != 0;

        /// <summary>fix-174: how far (m) the relief may move welded position <paramref name="w"/> (its share of the clearance; +inf = no limit).</summary>
        static float Room(ReliefShared f, int w, ReliefSettings s)
        {
            if (f.Clear == null || f.Clear[w] == ReliefClearance.NoLimit || s.clearShare <= 0) return float.PositiveInfinity;   // 0 = off (A/B)
            return s.clearShare * ReliefClearance.Decode(f.Clear[w]);
        }

        static float DistOf(ReliefShared f, int w) => f.Open != null ? f.Open[w] / 255f * ReliefFacts.OpenRange : 0f;

        /// <summary>Exact-position weld and the flat triangle arrays (TriV, TriSub; TriEdge/TriSphere allocated).</summary>
        public static void Weld(ReliefMesh m)
        {
            int nv = m.Pos.Length;
            m.Weld = new int[nv];
            var map = new Dictionary<Vector3, int>(nv);
            var wpos = new List<Vector3>(nv);
            for (int i = 0; i < nv; i++)
            {
                if (!map.TryGetValue(m.Pos[i], out int w))
                {
                    w = wpos.Count;
                    map[m.Pos[i]] = w;
                    wpos.Add(m.Pos[i]);
                }
                m.Weld[i] = w;
            }
            m.WeldCount = wpos.Count;
            m.WPos = wpos.ToArray();
            int tc = 0;
            foreach (var t in m.Sub) tc += t.Length / 3;
            m.TriCount = tc;
            m.TriV = new int[tc * 3]; m.TriSub = new int[tc]; m.TriEdge = new int[tc * 3]; m.TriSphere = new Vector4[tc];
            int ti = 0;
            for (int sm = 0; sm < m.Sub.Length; sm++)
            {
                var tris = m.Sub[sm];
                for (int k = 0; k + 2 < tris.Length; k += 3, ti++)
                {
                    m.TriSub[ti] = sm;
                    for (int j = 0; j < 3; j++) m.TriV[ti * 3 + j] = tris[k + j];
                }
            }
        }

        /// <summary>Far split of an edge: the Phong chord error (quadratic: n = sqrt(err / tol)) and, on detailed ground, the far spacing.</summary>
        public static int FarSplit(ReliefMesh m, int e, ReliefSettings s)
        {
            int a = m.EdgeA[e], b = m.EdgeB[e];
            float err = 0.25f * m.EdgeTerm[e].Length();
            int n = 1;
            if (err > s.farTolerance) n = (int)Math.Ceiling(Math.Sqrt(err / s.farTolerance));
            float amp = Math.Max(Math.Max(m.WAmp[a], m.WAmp[b]), Math.Max(m.WSwell[a], m.WSwell[b]));
            if (amp > 0.1f) n = Math.Max(n, (int)Math.Ceiling(m.EdgeLen[e] / s.farSpacing));
            if (m.WDune != null && Math.Max(m.WDune[a], m.WDune[b]) > 0.02f) n = Math.Max(n, (int)Math.Ceiling(m.EdgeLen[e] / s.duneFarSpacing));   // dune-1
            return Math.Max(1, Math.Min(s.farMaxSplit, n));
        }

        /// <summary>
        /// Wanted spacing at <paramref name="p"/>: the smallest of the refine points that reach it, never above the far spacing.
        /// <paramref name="shapeOnly"/>: only the points that shape the surface (the camera; the band limit of the detail), not the
        /// density-only focus points (snow), so a focus changes how densely the surface is sampled, never the surface itself.
        /// </summary>
        public static float SpacingAt(Vector3 p, ReliefPoint[] points, int count, float farSpacing, bool shapeOnly = false)
        {
            float sp = farSpacing;
            for (int i = 0; i < count; i++)
            {
                if (shapeOnly && !points[i].Shape) continue;
                float d = Vector3.Distance(p, points[i].Position);
                if (d >= points[i].Radius(farSpacing)) continue;
                float v = points[i].SpacingAt(d);
                if (v < sp) sp = v;
            }
            return sp;
        }

        /// <summary>Spacing wanted by the density-only (focus) points at <paramref name="p"/> when it is below the shape points'; 0 = none.</summary>
        public static float FocusSpacingAt(Vector3 p, ReliefPoint[] points, int count, float farSpacing)
        {
            float shape = SpacingAt(p, points, count, farSpacing, true), sp = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (points[i].Shape) continue;
                float d = Vector3.Distance(p, points[i].Position);
                if (d >= points[i].Radius(farSpacing)) continue;
                sp = Math.Min(sp, points[i].SpacingAt(d));
            }
            return sp < shape ? sp : 0;
        }

        static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float l2 = ab.LengthSquared();
            float t = l2 > 1e-12f ? Math.Max(0, Math.Min(1, Vector3.Dot(p - a, ab) / l2)) : 0;
            return Vector3.Distance(p, a + t * ab);
        }

        /// <summary>Spacing wanted along a segment: each point's spacing at its closest point of the segment.</summary>
        static float SegmentSpacing(Vector3 a, Vector3 b, ReliefPoint[] points, int count, float farSpacing)
        {
            float sp = farSpacing;
            for (int i = 0; i < count; i++)
            {
                float d = SegmentDistance(points[i].Position, a, b);
                if (d >= points[i].Radius(farSpacing)) continue;
                sp = Math.Min(sp, points[i].SpacingAt(d));
            }
            return sp;
        }

        /// <summary>Hard cap of the splits of one source edge (a 6 m edge at 4 cm = 150).</summary>
        public const int MaxEdgeSplit = 256;

        /// <summary>Split count of an edge with refine points (≥ its far split; the far split where no point reaches).</summary>
        public static int EdgeSplit(ReliefMesh m, int e, ReliefPoint[] points, int count, ReliefSettings s)
        {
            int n = m.EdgeFar[e];
            if (count == 0) return n;
            float sp = SegmentSpacing(m.WPos[m.EdgeA[e]], m.WPos[m.EdgeB[e]], points, count, s.farSpacing);
            if (sp >= s.farSpacing) return n;
            int k = (int)Math.Ceiling(m.EdgeLen[e] / sp);
            return Math.Max(n, Math.Min(MaxEdgeSplit, k));
        }

        /// <summary>A source triangle is near when any refine point can reach any of its points (bounding sphere test).</summary>
        public static bool IsNear(ReliefMesh m, int t, ReliefPoint[] points, int count, float farSpacing)
        {
            var sph = m.TriSphere[t];
            var c = new Vector3(sph.X, sph.Y, sph.Z);
            for (int i = 0; i < count; i++)
            {
                float r = points[i].Radius(farSpacing);
                if (r <= 0) continue;
                if (Vector3.Distance(c, points[i].Position) - sph.W < r) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ the surface

        /// <summary>
        /// The relief surface of source triangle <paramref name="t"/> at barycentric (b0, b1, b2) (exact zeros on an edge).
        /// Returns the vertex; <paramref name="spacing"/> = local sampling spacing of the shape points (band limit).
        /// </summary>
        /// <summary>Largest slope of the detail used for the shading normal (fix-174).</summary>
        public const float MaxGrad = 1.5f;

        public static ReliefVertex Evaluate(ReliefMesh m, int t, float b0, float b1, float b2, float spacing, ReliefSettings s, out float offset, out bool walk)
            => Evaluate(m, t, 0, 1, 2, b0, b1, b2, spacing, 0, s, out offset, out walk);

        /// <summary>
        /// <see cref="Evaluate(ReliefMesh,int,float,float,float,float,ReliefSettings,out float,out bool)"/> with the triangle's
        /// vertex slots in the order (<paramref name="s0"/>, <paramref name="s1"/>, <paramref name="s2"/>): edge points are evaluated
        /// with the edge's canonical endpoints first and the third weight exactly 0, so every expression is the same in both
        /// triangles of the edge (bit-identical even if the compiler fuses multiply-adds). <paramref name="focus"/> goes to uv3.z.
        /// </summary>
        public static ReliefVertex Evaluate(ReliefMesh m, int t, int s0, int s1, int s2, float b0, float b1, float b2, float spacing, float focus,
                                            ReliefSettings s, out float offset, out bool walk)
        {
            int v0 = m.TriV[t * 3 + s0], v1 = m.TriV[t * 3 + s1], v2 = m.TriV[t * 3 + s2];
            int w0 = m.Weld[v0], w1 = m.Weld[v1], w2 = m.Weld[v2];
            Vector3 P0 = m.WPos[w0], P1 = m.WPos[w1], P2 = m.WPos[w2];
            Vector3 N0 = m.WNrm[w0], N1 = m.WNrm[w1], N2 = m.WNrm[w2];
            // flat (collision) point and Phong point; every sum is over the three vertices in a fixed order with exact
            // zero weights on an edge, so the two triangles of an edge agree bit for bit (two-term sums are commutative)
            var p = Sum(b0, P0, b1, P1, b2, P2);
            float amp = Sum(b0, m.WAmp[w0], b1, m.WAmp[w1], b2, m.WAmp[w2]);
            float swell = m.WSwell != null ? Sum(b0, m.WSwell[w0], b1, m.WSwell[w1], b2, m.WSwell[w2]) : 0;
            // Phong as the sum of the capped edge terms (slot pairs 01, 12, 20; on an edge only its own term is non-zero)
            var T01 = m.EdgeTerm[EdgeOf(m, t, s0, s1)]; var T12 = m.EdgeTerm[EdgeOf(m, t, s1, s2)]; var T20 = m.EdgeTerm[EdgeOf(m, t, s2, s0)];
            var sp = p + (((b0 * b1) * T01 + (b1 * b2) * T12) + (b2 * b0) * T20);
            var ng = Normalize(Sum(b0, N0, b1, N1, b2, N2));
            // dune-1: the baked dune field at the Phong point (its weight fades the noise relief)
            float dg = DuneAt(m, w0, w1, w2, b0, b1, b2, sp, s, out var ds, ref amp, ref swell);
            var D0 = default(ReliefDetail);
            Vector3 grad = Vector3.Zero, gradC = Vector3.Zero;
            bool fine = spacing < s.farSpacing;   // the far mesh (spacing = far) has no fine octaves: they are not evaluated
            if (amp > 0.0005f || swell > 0.0005f)
            {
                Detail(sp, ng, amp, swell, spacing, s, ref D0, fine);
                // analytic-ish normal: finite differences of the detail in the tangent plane (without the strata: the shader adds them)
                var t1 = Normalize(Vector3.Cross(ng, Math.Abs(ng.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
                var t2 = Vector3.Cross(ng, t1);
                float eps = Math.Max(0.04f, spacing * 0.35f);
                var D1 = default(ReliefDetail); var D2 = default(ReliefDetail);
                Detail(sp + eps * t1, ng, amp, swell, spacing, s, ref D1, fine);
                Detail(sp + eps * t2, ng, amp, swell, spacing, s, ref D2, fine);
                float f0 = D0.Coarse + D0.FineBatch, f1 = D1.Coarse + D1.FineBatch, f2 = D2.Coarse + D2.FineBatch;
                grad = ((f1 - f0) / eps) * t1 + ((f2 - f0) / eps) * t2;
                gradC = ((D1.Coarse - D0.Coarse) / eps) * t1 + ((D2.Coarse - D0.Coarse) / eps) * t2;
            }
            // the coarse point (the GPU's position attribute), then the fine octaves and the strata at this vertex's spacing
            float duneUp = DuneAdd(dg, ds, spacing, fine, s, ref D0);
            var pBase = sp + D0.Coarse * ng + duneUp * Vector3.UnitY;
            float strata = D0.StrataAmp > 0 ? D0.StrataAmp * ReliefNoise.Strata(pBase, s.strataScale, spacing) : 0;
            float fineOffset = D0.FineBatch + strata;
            var pos = pBase + fineOffset * ng;
            var face = Vector3.Cross(m.WPos[m.Weld[m.TriV[t * 3 + 1]]] - m.WPos[m.Weld[m.TriV[t * 3]]], m.WPos[m.Weld[m.TriV[t * 3 + 2]]] - m.WPos[m.Weld[m.TriV[t * 3]]]);
            float fl = face.Length();
            face = fl > 1e-8f ? face / fl : ng;
            // Unity winding: cross(p1 - p0, p2 - p0) is the front side (the collision face the character stands on)
            offset = Vector3.Dot(pos - p, face);
            walk = face.Y >= 0.656f;

            var ns = Normalize(Sum(b0, m.Nrm[v0], b1, m.Nrm[v1], b2, m.Nrm[v2]));
            // fix-174: the detail tilts the shading normal at most ~56 deg (|grad| <= 1.5): steeper slopes of the noise on a
            // coarse far triangle turned whole facets away from their face (lit from behind: dark, mirror-like sky reflection)
            float gl = grad.Length();
            if (gl > MaxGrad) grad *= MaxGrad / gl;
            var n = Normalize(ns - grad);
            var nc = Normalize(ns - gradC);
            if (dg > 0 && s.duneNormal > 0)
            {
                // dune-1: the change's slope tilts the shading normal (heightfield: n ~ (-hx, 1, -hz) -> (-hx - dx, 1, -hz - dz)), also
                // the coarse normal the shader fades to: the raised crests, slip faces and dunelets are shaded at every distance
                float k = dg * s.duneScale * s.duneNormal;
                var d = new Vector3(ds.Gx, 0, ds.Gz) * k;
                n = Normalize(n / Math.Max(n.Y, 0.2f) - d); nc = Normalize(nc / Math.Max(nc.Y, 0.2f) - d);
            }
            var vtx = new ReliefVertex
            {
                Position = pos, Normal = n, Ng = ng, NormalCoarse = nc, FineOffset = fineOffset,
                StrataAmp = D0.StrataAmp, Sigma = spacing, Fine = D0.Fine,
            };
            if (m.Tan != null)
            {
                var ta = Sum(b0, m.Tan[v0], b1, m.Tan[v1], b2, m.Tan[v2]);
                var t3 = new Vector3(ta.X, ta.Y, ta.Z);
                t3 = Normalize(t3 - n * Vector3.Dot(n, t3));
                vtx.Tangent = new Vector4(t3, m.Tan[v0].W < 0 ? -1 : 1);
            }
            else vtx.Tangent = new Vector4(1, 0, 0, 1);
            vtx.Uv0 = Sum(b0, m.Uv0[v0], b1, m.Uv0[v1], b2, m.Uv0[v2]);
            if (m.Uv1 != null) vtx.Uv1 = Sum(b0, m.Uv1[v0], b1, m.Uv1[v1], b2, m.Uv1[v2]);
            vtx.Color = m.Col != null ? Lerp(m.Col[v0], b0, m.Col[v1], b1, m.Col[v2], b2) : 0xFFFFFFFFu;
            float mask = s.ampSteep > 0 ? Math.Min(1f, Math.Max(amp, swell) / s.ampSteep) : 0;
            vtx.Uv3 = new Vector3(offset, mask, focus);
            return vtx;
        }

        /// <summary>
        /// Only the position of <see cref="Evaluate(ReliefMesh,int,float,float,float,float,ReliefSettings,out float,out bool)"/>
        /// (the same arithmetic, no normal): ground queries (<see cref="ReliefGround.SurfaceAt"/>).
        /// </summary>
        public static Vector3 SurfacePoint(ReliefMesh m, int t, float b0, float b1, float b2, float spacing, ReliefSettings s)
        {
            int w0 = m.Weld[m.TriV[t * 3]], w1 = m.Weld[m.TriV[t * 3 + 1]], w2 = m.Weld[m.TriV[t * 3 + 2]];
            var p = Sum(b0, m.WPos[w0], b1, m.WPos[w1], b2, m.WPos[w2]);
            float amp = Sum(b0, m.WAmp[w0], b1, m.WAmp[w1], b2, m.WAmp[w2]);
            float swell = m.WSwell != null ? Sum(b0, m.WSwell[w0], b1, m.WSwell[w1], b2, m.WSwell[w2]) : 0;
            var T01 = m.EdgeTerm[EdgeOf(m, t, 0, 1)]; var T12 = m.EdgeTerm[EdgeOf(m, t, 1, 2)]; var T20 = m.EdgeTerm[EdgeOf(m, t, 2, 0)];
            var sp = p + (((b0 * b1) * T01 + (b1 * b2) * T12) + (b2 * b0) * T20);
            float dg = DuneAt(m, w0, w1, w2, b0, b1, b2, sp, s, out var ds, ref amp, ref swell);
            if (amp <= 0.0005f && swell <= 0.0005f && dg <= 0) return sp;
            var ng = Normalize(Sum(b0, m.WNrm[w0], b1, m.WNrm[w1], b2, m.WNrm[w2]));
            var D = default(ReliefDetail);
            bool fine = spacing < s.farSpacing;
            if (amp > 0.0005f || swell > 0.0005f) Detail(sp, ng, amp, swell, spacing, s, ref D, fine);
            float duneUp = DuneAdd(dg, ds, spacing, fine, s, ref D);
            var pBase = sp + D.Coarse * ng + duneUp * Vector3.UnitY;
            float strata = D.StrataAmp > 0 ? D.StrataAmp * ReliefNoise.Strata(pBase, s.strataScale, spacing) : 0;
            return pBase + (D.FineBatch + strata) * ng;
        }

        /// <summary>
        /// dune-1: the dune gate at a surface point (barycentric over the triangle's welded vertices, in the caller's slot order)
        /// and the field's sample at the Phong point <paramref name="sp"/>; fades <paramref name="amp"/> / <paramref name="swell"/>
        /// (the noise relief) by the dune weight. Returns the gate (0 = no dune here).
        /// </summary>
        static float DuneAt(ReliefMesh m, int w0, int w1, int w2, float b0, float b1, float b2, Vector3 sp, ReliefSettings s,
                            out DuneSample ds, ref float amp, ref float swell)
        {
            ds = default;
            var field = m.WDuneGate != null ? s.DuneFieldOn : null;
            if (field == null) return 0;
            float dg = Sum(b0, m.WDuneGate[w0], b1, m.WDuneGate[w1], b2, m.WDuneGate[w2]);
            if (dg <= 0 || !field.Sample(sp.X, sp.Z, out ds)) return 0;
            float cut = 1 - s.duneNoiseCut * dg * ds.W;
            amp *= cut; swell *= cut;
            return dg;
        }

        /// <summary>
        /// dune-1: the dune change at a point: returns the coarse part (m, vertical, the same in every mesh); the rest (crest kink,
        /// megaripples) goes into the ~4 m fine slot, so the shader fades it by the live camera distance like the other fine octaves.
        /// </summary>
        static float DuneAdd(float dg, in DuneSample ds, float spacing, bool fine, ReliefSettings s, ref ReliefDetail r)
        {
            if (dg <= 0) return 0;
            float k = dg * s.duneScale;
            if (fine && ds.B != 0)
            {
                int slot = DuneSlot(s);
                AddFine(ref r, slot, slot >= 0 ? FineLambda(slot, s) : 4f, spacing, k * ds.B);
            }
            return k * ds.C;
        }

        /// <summary>dune-1: the fine slot of wavelength 3..6 m (the lumps' 4 m octave with the shipped values); -1 = none.</summary>
        public static int DuneSlot(ReliefSettings s)
        {
            int first = -1;
            for (int i = 0; i < FineOctaves.Count; i++)
            {
                float l = FineLambda(i, s);
                if (l < 3f || l > 6f) continue;
                if (first < 0) first = i;
                if (i == FineSlot(2, 0, s)) return i;   // prefer the lumps' slot (lumps fade on dunes: sand has no clods)
            }
            return first;
        }

        /// <summary>
        /// The detail height (m) along the smooth normal <paramref name="n"/> at <paramref name="p"/>, everything at the
        /// sampling <paramref name="spacing"/> (strata included, evaluated at <paramref name="p"/>): <paramref name="amp"/> scales
        /// gullies / strata / lumps, <paramref name="swell"/> the broad walkable swells. (Ground queries, tests.)
        /// </summary>
        public static float Detail(Vector3 p, Vector3 n, float amp, float swell, float spacing, ReliefSettings s)
        {
            var D = default(ReliefDetail);
            Detail(p, n, amp, swell, spacing, s, ref D, spacing < s.farSpacing);
            float strata = D.StrataAmp > 0 ? D.StrataAmp * ReliefNoise.Strata(p, s.strataScale, spacing) : 0;
            return D.Coarse + D.FineBatch + strata;
        }

        /// <summary>
        /// fix-171: the detail split for the live distance fade. Octaves of at least <see cref="ReliefSettings.macroLambda"/>
        /// get the band of the far spacing everywhere (the same in the far mesh and in every near mesh: the macro shape never
        /// changes with the camera); the shorter ones go into the fine slots (<see cref="FineSlot"/>) at full weight and, at
        /// <paramref name="spacing"/>, into <see cref="ReliefDetail.FineBatch"/>; the strata only as an amplitude (the shader and
        /// Evaluate take them at the coarse point). <paramref name="fine"/> false: the fine octaves are not evaluated (far mesh).
        /// </summary>
        public static void Detail(Vector3 p, Vector3 n, float amp, float swell, float spacing, ReliefSettings s, ref ReliefDetail r, bool fine)
        {
            r = default;
            var raw = _detailRaw ?? (_detailRaw = new float[16]);
            float macro = s.macroLambda, far = s.farSpacing, minLambda = fine ? 0 : macro;
            if (swell > 0.0005f && s.swell > 0 && s.swellOctaves > 0)
            {
                ReliefNoise.SwellsRaw(new Vector2(p.X, p.Z), s.swellScale, s.swellOctaves, raw, minLambda);
                float macroSum = 0, k = swell * s.swell, lambda = s.swellScale;
                for (int o = 0; o < s.swellOctaves && o < raw.Length; o++, lambda *= 0.5f)
                {
                    if (lambda >= macro) { macroSum += ReliefNoise.Band(lambda, far) * raw[o]; continue; }
                    if (fine) AddFine(ref r, FineSlot(1, o, s), lambda, spacing, k * raw[o]);
                }
                r.Coarse += k * Math.Max(-1f, Math.Min(1f, macroSum));
            }
            if (amp <= 0.0005f) return;
            float steep = Steep(n.Y, s);
            float slope = 1 - Math.Max(n.Y, -1f);
            if (s.gully > 0 && s.gullyOctaves > 0)
            {
                float ny = Math.Max(Math.Abs(n.Y), 0.25f);
                var g = new Vector2(n.X / ny, n.Z / ny);       // downhill gradient of the height (dh/dx, dh/dz up to sign)
                float onSlope = ReliefNoise.SmoothStep(0.02f, 0.2f, slope);
                if (onSlope > 0)
                {
                    ReliefNoise.GulliesRaw(new Vector2(p.X, p.Z), g, s.gullyScale, s.gullyOctaves, raw, minLambda);
                    float k = amp * s.gully * onSlope, lambda = s.gullyScale;
                    for (int o = 0; o < s.gullyOctaves && o < raw.Length; o++, lambda *= 0.5f)
                    {
                        if (lambda >= macro) { r.Coarse += k * ReliefNoise.Band(lambda, far) * raw[o]; continue; }
                        if (fine) AddFine(ref r, FineSlot(0, o, s), lambda, spacing, k * raw[o]);
                    }
                }
            }
            if (s.strata > 0 && steep > 0) r.StrataAmp = amp * s.strata * steep;
            if (s.lumps > 0 && s.lumpOctaves > 0)
            {
                ReliefNoise.LumpsRaw(p, s.lumpScale, s.lumpOctaves, raw, minLambda);
                float k = amp * s.lumps, lambda = s.lumpScale;
                for (int o = 0; o < s.lumpOctaves && o < raw.Length; o++, lambda *= 0.5f)
                {
                    if (lambda >= macro) { r.Coarse += k * ReliefNoise.Band(lambda, far) * raw[o]; continue; }
                    if (fine) AddFine(ref r, FineSlot(2, o, s), lambda, spacing, k * raw[o]);
                }
            }
        }

        [ThreadStatic] static float[] _detailRaw;

        static void AddFine(ref ReliefDetail r, int slot, float lambda, float spacing, float c)
        {
            float w = ReliefNoise.Band(lambda, spacing);
            if (slot < 0) { r.Coarse += w * c; return; }   // no slot left (more fine octaves than slots): fixed at this spacing
            r.Fine[slot] = r.Fine[slot] + c;
            r.FineBatch += w * c;
        }

        /// <summary>
        /// fix-171: the fine slot (0..6) of octave <paramref name="octave"/> of term <paramref name="term"/> (0 gullies, 1 swells,
        /// 2 lumps), -1 for none: the octaves shorter than <see cref="ReliefSettings.macroLambda"/> and longer than 2 near
        /// spacings (shorter ones are never sampled finely enough), gullies first, then swells, then lumps.
        /// </summary>
        public static int FineSlot(int term, int octave, ReliefSettings s)
        {
            int start = 0;
            for (int t = 0; t <= term; t++)
            {
                TermFine(t, s, out int first, out int count);
                if (t == term)
                {
                    int i = octave - first;
                    if (i < 0 || i >= count) return -1;
                    int slot = start + i;
                    return slot < FineOctaves.Count ? slot : -1;
                }
                start += count;
            }
            return -1;
        }

        /// <summary>The wavelength (m) of fine slot <paramref name="slot"/>, 0 for an unused slot (the shader's band is then 0).</summary>
        public static float FineLambda(int slot, ReliefSettings s)
        {
            int start = 0;
            for (int t = 0; t < 3; t++)
            {
                TermFine(t, s, out int first, out int count);
                if (slot < start + count) return TermLargest(t, s) * (float)Math.Pow(0.5, first + slot - start);
                start += count;
            }
            return 0;
        }

        static float TermLargest(int t, ReliefSettings s) => t == 0 ? s.gullyScale : t == 1 ? s.swellScale : s.lumpScale;

        static int TermOctaves(int t, ReliefSettings s) =>
            t == 0 ? (s.gully > 0 ? s.gullyOctaves : 0) : t == 1 ? (s.swell > 0 ? s.swellOctaves : 0) : (s.lumps > 0 ? s.lumpOctaves : 0);

        static void TermFine(int t, ReliefSettings s, out int first, out int count)
        {
            int octaves = TermOctaves(t, s);
            first = octaves; count = 0;
            float lambda = TermLargest(t, s), lo = 2f * s.nearSpacing;
            for (int o = 0; o < octaves; o++, lambda *= 0.5f)
            {
                if (lambda >= s.macroLambda || lambda <= lo) continue;
                if (first == octaves) first = o;
                count++;
            }
        }

        /// <summary>The edge id between two vertex slots of triangle <paramref name="t"/> (edge k joins slot k and k + 1).</summary>
        static int EdgeOf(ReliefMesh m, int t, int x, int y) => m.TriEdge[t * 3 + (y == (x + 1) % 3 ? x : y)];

        static Vector3 Sum(float a, Vector3 x, float b, Vector3 y, float c, Vector3 z) => (a * x + b * y) + c * z;
        static Vector4 Sum(float a, Vector4 x, float b, Vector4 y, float c, Vector4 z) => (a * x + b * y) + c * z;
        static Vector2 Sum(float a, Vector2 x, float b, Vector2 y, float c, Vector2 z) => (a * x + b * y) + c * z;
        static float Sum(float a, float x, float b, float y, float c, float z) => (a * x + b * y) + c * z;

        static Vector3 Normalize(Vector3 v)
        {
            float l = v.Length();
            return l > 1e-8f ? v / l : Vector3.UnitY;
        }

        static uint Lerp(uint c0, float b0, uint c1, float b1, uint c2, float b2)
        {
            uint r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                float v = ((c0 >> sh) & 0xFF) * b0 + ((c1 >> sh) & 0xFF) * b1 + ((c2 >> sh) & 0xFF) * b2;
                int iv = (int)(v + 0.5f);
                r |= (uint)(iv < 0 ? 0 : iv > 255 ? 255 : iv) << sh;
            }
            return r;
        }

        // ------------------------------------------------------------------ tessellation of one source triangle

        /// <summary>A leaf is a uniform grid of at most this level; larger needs are split into four (adaptive).</summary>
        public const int LeafLevel = 8;
        const int MaxDepth = 8;

        /// <summary>Points of one (sub-)edge in order: vertex ids <c>a[s]</c> .. <c>a[e]</c> (either direction).</summary>
        struct Span
        {
            public int[] a; public int s, e;
            public Span(int[] arr, int start, int end) { a = arr; s = start; e = end; }
            public int Count => Math.Abs(e - s);
            public int this[int i] => a[e >= s ? s + i : s - i];
            public Span Reversed => new Span(a, e, s);
        }

        [ThreadStatic] static int[] _gridIds;
        [ThreadStatic] static List<Vector3> _bary;

        /// <summary>
        /// Emits source triangle <paramref name="t"/> with source edge splits <paramref name="n0"/>.. (edge k = vertex k → k+1).
        /// Far (<paramref name="points"/> null): one uniform grid of the largest edge split. Near: adaptive: a sub-triangle whose
        /// wanted level (size / spacing at its nearest point) or edge counts exceed <see cref="LeafLevel"/> is split into four;
        /// every (sub-)edge has one fixed point array (source edges: the canonical points; inner edges: created once by
        /// whichever side comes first), children take index ranges of it, and a leaf grid snaps onto its edges' arrays,
        /// so the triangulation is watertight inside and on the source edges.
        /// </summary>
        static void EmitTriangle(ReliefMesh m, int t, int n0, int n1, int n2, ReliefPoint[] points, int count,
                                 ReliefSettings s, ReliefOutput o)
        {
            var list = o.Indices[m.TriSub[t]];
            int start = list.Count;
            int baseId = o.VertexCount;
            var bary = _bary ?? (_bary = new List<Vector3>(256));
            bary.Clear();
            int wv0 = m.Weld[m.TriV[t * 3]], wv1 = m.Weld[m.TriV[t * 3 + 1]], wv2 = m.Weld[m.TriV[t * 3 + 2]];
            Vector3 Q0 = m.WPos[wv0], Q1 = m.WPos[wv1], Q2 = m.WPos[wv2];
            // canonical edge direction: true when the edge's canonical A is the triangle's vertex k
            bool f0 = m.EdgeA[m.TriEdge[t * 3]] == wv0, f1 = m.EdgeA[m.TriEdge[t * 3 + 1]] == wv1, f2 = m.EdgeA[m.TriEdge[t * 3 + 2]] == wv2;

            int PointP(int s0, int s1, int s2, float x0, float x1, float x2)
            {
                int q0 = m.Weld[m.TriV[t * 3 + s0]], q1 = m.Weld[m.TriV[t * 3 + s1]], q2 = m.Weld[m.TriV[t * 3 + s2]];
                var pos = Sum(x0, m.WPos[q0], x1, m.WPos[q1], x2, m.WPos[q2]);
                float sp = points == null ? s.farSpacing : SpacingAt(pos, points, count, s.farSpacing, true);
                float focus = points == null ? 0 : FocusSpacingAt(pos, points, count, s.farSpacing);
                var v = Evaluate(m, t, s0, s1, s2, x0, x1, x2, sp, focus, s, out float off, out bool walk);
                float a = Math.Abs(off);
                if (a > o.MaxOffsetAll) o.MaxOffsetAll = a;
                if (walk)
                {
                    if (a > o.MaxOffsetWalk) { o.MaxOffsetWalk = a; o.MaxWalkAt = v.Position; }
                    o.WalkHistogram[Math.Min(63, (int)(a * 100))]++;
                    o.SumOffsetWalk += a; o.CountWalk++;
                }
                var b = new Vector3();
                if (s0 == 0) b.X = x0; else if (s0 == 1) b.Y = x0; else b.Z = x0;
                if (s1 == 0) b.X = x1; else if (s1 == 1) b.Y = x1; else b.Z = x1;
                if (s2 == 0) b.X = x2; else if (s2 == 1) b.Y = x2; else b.Z = x2;
                bary.Add(b);
                return o.Add(v);
            }
            int Point(Vector3 b) => PointP(0, 1, 2, b.X, b.Y, b.Z);
            Vector3 BaryOf(int id) => bary[id - baseId];
            Vector3 Flat(Vector3 b) => Sum(b.X, Q0, b.Y, Q1, b.Z, Q2);

            int c0 = Point(new Vector3(1, 0, 0)), c1 = Point(new Vector3(0, 1, 0)), c2 = Point(new Vector3(0, 0, 1));

            // a source edge's array, local order vertex k -> k+1; points evaluated from the canonical endpoint
            int[] SourceEdge(int k, int nk)
            {
                bool fwd = k == 0 ? f0 : k == 1 ? f1 : f2;
                var arr = new int[nk + 1];
                arr[0] = k == 0 ? c0 : k == 1 ? c1 : c2;
                arr[nk] = k == 0 ? c1 : k == 1 ? c2 : c0;
                int sa = fwd ? k : (k + 1) % 3, sb = fwd ? (k + 1) % 3 : k;   // triangle slots of the canonical A and B
                for (int i = 1; i < nk; i++)
                {
                    int j = fwd ? i : nk - i;                                     // canonical index from A
                    arr[i] = PointP(sa, sb, 3 - sa - sb, (float)(nk - j) / nk, (float)j / nk, 0);
                }
                return arr;
            }
            var E0 = SourceEdge(0, n0); var E1 = SourceEdge(1, n1); var E2 = SourceEdge(2, n2);

            // fix-171: a skirt under each source edge on the cell's outline: the edge's own points and copies lowered by the
            // skirt depth (+ the drawn surface's height above the collision), both windings (seen from either cell)
            int Lowered(int id)
            {
                var v = o.Vertices[id];
                float depth = s.skirt + Math.Min(2f, Math.Abs(v.Uv3.X));
                v.Position.Y -= depth;
                v.Uv3 = new Vector3(v.Uv3.X, v.Uv3.Y, 0);   // the gap of its top point (snow/rain terms); no snow prints on a skirt
                bary.Add(BaryOf(id));
                return o.Add(v);
            }
            void Skirt(int[] arr)
            {
                int pt = -1, pb = -1;
                for (int i = 0; i < arr.Length; i++)
                {
                    int top = arr[i], bot = Lowered(top);
                    if (i > 0)
                    {
                        Tri(list, pt, top, bot); Tri(list, pt, bot, pb);
                        Tri(list, top, pt, bot); Tri(list, bot, pt, pb);
                    }
                    pt = top; pb = bot;
                }
            }
            if (s.skirt > 0 && m.EdgeBorder != null)
            {
                if (m.EdgeBorder[m.TriEdge[t * 3]]) Skirt(E0);
                if (m.EdgeBorder[m.TriEdge[t * 3 + 1]]) Skirt(E1);
                if (m.EdgeBorder[m.TriEdge[t * 3 + 2]]) Skirt(E2);
            }

            // an inner edge between two existing vertices (created once; the sibling across it reuses it reversed)
            Span Inner(int x, int y, float target)
            {
                Vector3 bx = BaryOf(x), by = BaryOf(y);
                Vector3 px = Flat(bx), py = Flat(by);
                float len = Vector3.Distance(px, py);
                float sp = points == null ? s.farSpacing : SegmentSpacing(px, py, points, count, s.farSpacing);
                int n = Math.Max(1, Math.Min(MaxEdgeSplit, (int)Math.Ceiling(len / Math.Max(sp, 0.01f))));
                if (target >= 2 * LeafLevel) n = Math.Max(n, 2);
                var arr = new int[n + 1];
                arr[0] = x; arr[n] = y;
                for (int i = 1; i < n; i++) arr[i] = Point(Vector3.Lerp(bx, by, (float)i / n));
                return new Span(arr, 0, n);
            }

            float Target(Vector3 ba, Vector3 bb, Vector3 bc)
            {
                if (points == null) return 1;
                Vector3 pa = Flat(ba), pb = Flat(bb), pc = Flat(bc);
                float size = Math.Max(Vector3.Distance(pa, pb), Math.Max(Vector3.Distance(pb, pc), Vector3.Distance(pc, pa)));
                var c = (pa + pb + pc) / 3;
                float r = Math.Max(Vector3.Distance(c, pa), Math.Max(Vector3.Distance(c, pb), Vector3.Distance(c, pc)));
                float sp = s.farSpacing;
                for (int i = 0; i < count; i++)
                {
                    float d = Math.Max(0, Vector3.Distance(c, points[i].Position) - r);
                    if (d >= points[i].Radius(s.farSpacing)) continue;
                    sp = Math.Min(sp, points[i].SpacingAt(d));
                }
                return (float)Math.Ceiling(size / Math.Max(sp, 0.01f));
            }

            void Leaf(Span ab, Span bc, Span ca, int lv)
            {
                int A = ab[0], B = bc[0], C = ca[0];
                Vector3 ba = BaryOf(A), bb = BaryOf(B), bcy = BaryOf(C);
                int size = (lv + 1) * (lv + 2) / 2;
                var ids = _gridIds;
                if (ids == null || ids.Length < size) _gridIds = ids = new int[Math.Max(size, 64)];
                int Idx(int a, int b) => (a + b) * (a + b + 1) / 2 + b;
                int nab = ab.Count, nbc = bc.Count, nca = ca.Count;
                for (int a = 0; a <= lv; a++)
                for (int b = 0; a + b <= lv; b++)
                {
                    int c = lv - a - b;
                    int id;
                    if (b == 0) id = ab[(int)Math.Floor((double)a * nab / lv + 0.5)];              // A -> B, param a
                    else if (c == 0) id = bc[(int)Math.Floor((double)b * nbc / lv + 0.5)];         // B -> C, param b
                    else if (a == 0) id = ca[(int)Math.Floor((double)(lv - b) * nca / lv + 0.5)];  // C -> A, param lv - b
                    else id = Point(((float)c / lv) * ba + ((float)a / lv) * bb + ((float)b / lv) * bcy);
                    ids[Idx(a, b)] = id;
                }
                for (int a = 0; a < lv; a++)
                for (int b = 0; a + b < lv; b++)
                {
                    Tri(list, ids[Idx(a, b)], ids[Idx(a + 1, b)], ids[Idx(a, b + 1)]);
                    if (a + b < lv - 1) Tri(list, ids[Idx(a + 1, b)], ids[Idx(a + 1, b + 1)], ids[Idx(a, b + 1)]);
                }
            }

            void Emit(Span ab, Span bc, Span ca, int depth)
            {
                float target = Target(BaryOf(ab[0]), BaryOf(bc[0]), BaryOf(ca[0]));
                int maxC = Math.Max(ab.Count, Math.Max(bc.Count, ca.Count));
                bool canSplit = points != null && depth < MaxDepth && ab.Count >= 2 && bc.Count >= 2 && ca.Count >= 2;
                if (Math.Max(target, maxC) <= LeafLevel || !canSplit)
                {
                    int lv = Math.Max(maxC, (int)Math.Min(target, Math.Max(maxC, s.nearMaxSplit)));
                    Leaf(ab, bc, ca, Math.Max(1, lv));
                    return;
                }
                // four children: halves of the three edges (at an existing array point) + three inner edges
                int hab = ab.Count / 2, hbc = bc.Count / 2, hca = ca.Count / 2;
                int Mab = ab[hab], Mbc = bc[hbc], Mca = ca[hca];
                var abL = new Span(ab.a, ab.s, ab.e >= ab.s ? ab.s + hab : ab.s - hab); var abR = new Span(ab.a, abL.e, ab.e);
                var bcL = new Span(bc.a, bc.s, bc.e >= bc.s ? bc.s + hbc : bc.s - hbc); var bcR = new Span(bc.a, bcL.e, bc.e);
                var caL = new Span(ca.a, ca.s, ca.e >= ca.s ? ca.s + hca : ca.s - hca); var caR = new Span(ca.a, caL.e, ca.e);
                float half = target * 0.5f;
                var i1 = Inner(Mab, Mbc, half);   // Mab -> Mbc
                var i2 = Inner(Mbc, Mca, half);   // Mbc -> Mca
                var i3 = Inner(Mca, Mab, half);   // Mca -> Mab
                Emit(abL, i3.Reversed, caR, depth + 1);    // A, Mab, Mca
                Emit(abR, bcL, i1.Reversed, depth + 1);    // Mab, B, Mbc
                Emit(i2.Reversed, bcR, caL, depth + 1);    // Mca, Mbc, C
                Emit(i1, i2, i3, depth + 1);               // Mab, Mbc, Mca
            }

            Emit(new Span(E0, 0, n0), new Span(E1, 0, n1), new Span(E2, 0, n2), 0);
            if (o.TriStart != null) { o.TriStart[t] = start; o.TriCount[t] = list.Count - start; }
        }

        static void Tri(List<int> list, int i0, int i1, int i2)
        {
            if (i0 == i1 || i1 == i2 || i2 == i0) return;   // collapsed by the edge snapping
            list.Add(i0); list.Add(i1); list.Add(i2);
        }

        // ------------------------------------------------------------------ whole meshes

        /// <summary>The far mesh: every triangle at its far splits, camera-independent. <paramref name="o"/> gets TriStart/TriCount.</summary>
        public static void BuildFar(ReliefMesh m, ReliefSettings s, ReliefOutput o)
        {
            o.Reset(m.Sub.Length);
            o.TriStart = new int[m.TriCount]; o.TriCount = new int[m.TriCount];
            o.Near = null;
            for (int t = 0; t < m.TriCount; t++)
            {
                int n0 = m.EdgeFar[m.TriEdge[t * 3]], n1 = m.EdgeFar[m.TriEdge[t * 3 + 1]], n2 = m.EdgeFar[m.TriEdge[t * 3 + 2]];
                EmitTriangle(m, t, n0, n1, n2, null, 0, s, o);
            }
        }

        /// <summary>
        /// The near part for the refine points: the triangles within reach (<see cref="IsNear"/>) at their near splits with
        /// the fine octaves; <see cref="ReliefOutput.Near"/> marks them (the far mesh must drop them). Returns the count.
        /// </summary>
        public static int BuildNear(ReliefMesh m, ReliefPoint[] points, int count, ReliefSettings s, ReliefOutput o) =>
            BuildNear(m, points, count, s, o, 0, m.TriCount);

        /// <summary>
        /// fix-171: <see cref="BuildNear(ReliefMesh,ReliefPoint[],int,ReliefSettings,ReliefOutput)"/> for the source triangles
        /// <paramref name="from"/> .. <paramref name="to"/> - 1 only (one chunk of a parallel build; <see cref="Merge"/> joins
        /// the chunks in order: the result is the same as one build).
        /// </summary>
        public static int BuildNear(ReliefMesh m, ReliefPoint[] points, int count, ReliefSettings s, ReliefOutput o, int from, int to)
        {
            o.Reset(m.Sub.Length);
            o.TriStart = null; o.TriCount = null;
            if (o.Near == null || o.Near.Length != m.TriCount) o.Near = new bool[m.TriCount];
            else Array.Clear(o.Near, 0, o.Near.Length);
            if (count == 0) return 0;
            if (!Reaches(m, points, count, s)) return 0;
            int near = 0;
            for (int t = Math.Max(0, from); t < Math.Min(to, m.TriCount); t++)
            {
                if (!IsNear(m, t, points, count, s.farSpacing)) continue;
                o.Near[t] = true; near++;
                int n0 = EdgeSplit(m, m.TriEdge[t * 3], points, count, s);
                int n1 = EdgeSplit(m, m.TriEdge[t * 3 + 1], points, count, s);
                int n2 = EdgeSplit(m, m.TriEdge[t * 3 + 2], points, count, s);
                EmitTriangle(m, t, n0, n1, n2, points, count, s, o);
            }
            o.NearTriangles = near;
            return near;
        }

        /// <summary>Quick reject of a whole mesh: does any point reach its bounds?</summary>
        public static bool Reaches(ReliefMesh m, ReliefPoint[] points, int count, ReliefSettings s)
        {
            for (int i = 0; i < count; i++)
            {
                float r = points[i].Radius(s.farSpacing);
                var c = Vector3.Clamp(points[i].Position, m.BoundsMin, m.BoundsMax);
                if (r > 0 && Vector3.Distance(c, points[i].Position) < r) return true;
            }
            return false;
        }

        /// <summary>fix-171: the chunks of a parallel near build (in triangle order) joined into one output.</summary>
        public static ReliefOutput Merge(ReliefMesh m, IReadOnlyList<ReliefOutput> parts) =>
            parts.Count == 1 ? parts[0] : Merge(m, parts, new ReliefOutput());

        /// <summary>perf-5: <see cref="Merge(ReliefMesh,IReadOnlyList{ReliefOutput})"/> into <paramref name="o"/> (its buffers are
        /// reused; the parts are copied even when there is one, so they can be reused too).</summary>
        public static ReliefOutput Merge(ReliefMesh m, IReadOnlyList<ReliefOutput> parts, ReliefOutput o)
        {
            o.Cleared(m);
            int verts = 0;
            foreach (var p in parts) verts += p.VertexCount;
            if (o.Vertices.Length < verts) o.Vertices = new ReliefVertex[verts];
            foreach (var p in parts)
            {
                int off = o.VertexCount;
                Array.Copy(p.Vertices, 0, o.Vertices, off, p.VertexCount);
                o.VertexCount += p.VertexCount;
                for (int sm = 0; sm < o.Indices.Length && sm < p.Indices.Length; sm++)
                {
                    var src = p.Indices[sm]; var dst = o.Indices[sm];
                    for (int i = 0; i < src.Count; i++) dst.Add(src[i] + off);
                }
                if (p.Near != null) for (int t = 0; t < m.TriCount; t++) o.Near[t] |= p.Near[t];
                o.NearTriangles += p.NearTriangles;
                if (p.MaxOffsetWalk > o.MaxOffsetWalk) { o.MaxOffsetWalk = p.MaxOffsetWalk; o.MaxWalkAt = p.MaxWalkAt; }
                o.MaxOffsetAll = Math.Max(o.MaxOffsetAll, p.MaxOffsetAll);
                o.SumOffsetWalk += p.SumOffsetWalk; o.CountWalk += p.CountWalk;
                for (int i = 0; i < o.WalkHistogram.Length; i++) o.WalkHistogram[i] += p.WalkHistogram[i];
            }
            return o;
        }

        /// <summary>
        /// Index lists of the combined mesh: the far triangles that are not near (ranges of <paramref name="far"/>), then the
        /// near ones offset by the far vertex count. <paramref name="into"/>[sub] is cleared and filled.
        /// </summary>
        public static void Combine(ReliefMesh m, ReliefOutput far, ReliefOutput near, List<int>[] into)
        {
            for (int sm = 0; sm < into.Length; sm++) into[sm].Clear();
            bool[] skip = near != null && near.NearTriangles > 0 ? near.Near : null;
            for (int t = 0; t < m.TriCount; t++)
            {
                if (skip != null && skip[t]) continue;
                var src = far.Indices[m.TriSub[t]];
                var dst = into[m.TriSub[t]];
                int a = far.TriStart[t], n = far.TriCount[t];
                for (int i = 0; i < n; i++) dst.Add(src[a + i]);
            }
            if (skip == null) return;
            int off = far.VertexCount;
            for (int sm = 0; sm < into.Length && sm < near.Indices.Length; sm++)
            {
                var src = near.Indices[sm]; var dst = into[sm];
                for (int i = 0; i < src.Count; i++) dst.Add(src[i] + off);
            }
        }
    }
}
