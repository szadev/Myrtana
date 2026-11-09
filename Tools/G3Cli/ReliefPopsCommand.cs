using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using G3.Formats.Pak;
using G3.Unity;
using V3 = System.Numerics.Vector3;

static partial class Program
{
    /// <summary>
    /// fix-171 <c>relief pops --path x0 z0 x1 z1 [--step 1] [--eye 1.8] [--radius 90]</c>: the camera walks the path; whenever the
    /// near batch is re-centred, every surface probe (source triangle centres with detail within the radius) is evaluated as drawn
    /// before and after the swap at the same camera position; reports the largest jump (m) and its size on screen (px at 1080p,
    /// 60 deg). Old = build 21a (every octave band-limited by the batch spacing, 6 m grid, no margin, CPU positions); new =
    /// fix-171 (macro octaves fixed, fine + strata by the live distance, never finer than the batch spacing, hysteresis + margin).
    /// </summary>
    static int ReliefPops(string data, string[] args)
    {
        float x0 = 600, z0 = 560, x1 = 660, z1 = 600, step = 1f, eye = 1.8f, radius = 90f, lag = 0, lead = 0, margin = -1, recenter = -1;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--path") { x0 = F(args[++i]); z0 = F(args[++i]); x1 = F(args[++i]); z1 = F(args[++i]); }
            else if (args[i] == "--step") step = F(args[++i]);
            else if (args[i] == "--eye") eye = F(args[++i]);
            else if (args[i] == "--radius") radius = F(args[++i]);
            else if (args[i] == "--lag") lag = F(args[++i]);   // m the camera walks while a batch is built
            else if (args[i] == "--lead") lead = F(args[++i]); // m the new code puts the centre ahead of the camera (velocity x lead time)
            else if (args[i] == "--margin") margin = F(args[++i]);
            else if (args[i] == "--recenter") recenter = F(args[++i]);
        }
        static float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);
        var s = WithDunes(ReliefSettings.Default.Clamped(), args);
        if (margin >= 0) s.nearMargin = margin;
        if (recenter > 0) s.recenter = recenter;
        using var fs = new G3FileSystem(data, "Projects_compiled", "_compiledMesh", "_compiledMaterial");
        float bx = (x0 + x1) / 2, bz = (z0 + z1) / 2, bh = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0)) / 2 + radius + 20;
        var cells = LoadLandscapeCells(fs).Select(c => (c.name, ToRelief(c.mesh))).ToList();
        var facts = ReliefWorldFacts(cells, s, ReliefOpenField(fs, cells));
        var used = new List<ReliefMesh>();
        for (int i = 0; i < cells.Count; i++)
        {
            var m = cells[i].Item2;
            var mn = new V3(float.MaxValue); var mx = new V3(float.MinValue);
            foreach (var p in m.Pos) { mn = V3.Min(mn, p); mx = V3.Max(mx, p); }
            if (mx.X < bx - bh || mn.X > bx + bh || mx.Z < bz - bh || mn.Z > bz + bh) continue;
            TerrainRelief.Prepare(m, s, facts[i].facts);
            used.Add(m);
        }
        // probes: centres of source triangles with detail (amp or swell) near the path
        var probes = new List<(ReliefMesh m, int t)>();
        foreach (var m in used)
            for (int t = 0; t < m.TriCount; t++)
            {
                var sph = m.TriSphere[t];
                if (SegDist(sph.X, sph.Z, x0, z0, x1, z1) > radius) continue;
                int w0 = m.Weld[m.TriV[t * 3]], w1 = m.Weld[m.TriV[t * 3 + 1]], w2 = m.Weld[m.TriV[t * 3 + 2]];
                if (m.WAmp[w0] + m.WAmp[w1] + m.WAmp[w2] + m.WSwell[w0] + m.WSwell[w1] + m.WSwell[w2] < 0.05f) continue;
                probes.Add((m, t));
            }
        Console.WriteLine($"{used.Count} cells, {probes.Count} probes within {radius:0} m of the path ({x0},{z0})-({x1},{z1})");
        float Ground(float x, float z)
        {
            float best = float.MaxValue, y = 0;
            foreach (var m in used)
            {
                if (x < m.BoundsMin.X || x > m.BoundsMax.X || z < m.BoundsMin.Z || z > m.BoundsMax.Z) continue;
                foreach (var p in m.WPos) { float d = (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z); if (d < best) { best = d; y = p.Y; } }
            }
            return y;
        }
        var oldS = s.Clamped(); oldS.macroLambda = 1e6f; oldS.nearMargin = 0;
        foreach (var mode in new[] { "old (21a)", "new (fix-171)" })
        {
            bool isNew = mode.StartsWith("new");
            var st = isNew ? s : oldS;
            float len = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            V3? centre = null;
            int swaps = 0; double worst = 0, worstPx = 0; var pops = new List<double>(); var pxs = new List<double>(); string worstAt = "";
            Dictionary<(ReliefMesh, int), V3> drawn = null;
            float lastY = float.NaN;
            var camAt = new Dictionary<int, float>();
            for (float d = 0; d <= len; d += step)
            {
                float x = x0 + (x1 - x0) * d / len, z = z0 + (z1 - z0) * d / len;
                // a walker: the eye follows the ground, at most 1.5 m up or down per step (no jumps onto cliff tops)
                float gy = Ground(x, z) + eye;
                lastY = float.IsNaN(lastY) ? gy : lastY + Math.Max(-1.5f * step, Math.Min(1.5f * step, gy - lastY));
                var cam = new V3(x, lastY, z);
                V3 want;
                // the batch asked for `lag` m ago is the one swapped in now (it was built while the camera walked on)
                float da = Math.Max(0, d - lag);
                float xa = x0 + (x1 - x0) * da / len, za = z0 + (z1 - z0) * da / len;
                var asked = lag > 0 ? new V3(xa, camAt.TryGetValue((int)Math.Round(da / step), out float ya) ? ya : cam.Y, za) : cam;
                camAt[(int)Math.Round(d / step)] = cam.Y;
                if (!isNew) { float g = 6f; want = new V3(MathF.Round(asked.X / g) * g, MathF.Round(asked.Y / g) * g, MathF.Round(asked.Z / g) * g); }
                else
                {
                    var ahead = asked;
                    if (lead > 0) { float dl = Math.Min(len, da + lead); ahead = new V3(x0 + (x1 - x0) * dl / len, asked.Y, z0 + (z1 - z0) * dl / len); }
                    want = centre == null || V3.Distance(asked, centre.Value) > st.recenter ? ahead : centre.Value;
                }
                if (centre != null && want == centre.Value && drawn != null) continue;
                var pt = new ReliefPoint(want, st.nearSpacing, st.nearSlope, true, float.MaxValue, st.nearMargin);
                var now = new Dictionary<(ReliefMesh, int), V3>(probes.Count);
                foreach (var (m, t) in probes) now[(m, t)] = Drawn(m, t, pt, cam, st, isNew);
                if (drawn != null)
                {
                    swaps++;
                    // the old frame at this camera: the previous batch, drawn for this camera
                    var prevPt = new ReliefPoint(centre.Value, st.nearSpacing, st.nearSlope, true, float.MaxValue, st.nearMargin);
                    foreach (var (m, t) in probes)
                    {
                        var a = Drawn(m, t, prevPt, cam, st, isNew); var b = now[(m, t)];
                        double jump = V3.Distance(a, b), dist = Math.Max(1, V3.Distance(cam, b));
                        double px = jump / dist / (Math.Tan(30 * Math.PI / 180) * 2 / 1080);
                        pops.Add(jump); pxs.Add(px);
                        if (px > worstPx)
                        {
                            worstPx = px; worst = jump; worstAt = $"({b.X:F0},{b.Y:F0},{b.Z:F0}) {dist:F0} m away";
                            if (Environment.GetEnvironmentVariable("POPS_DEBUG") == "1")
                            {
                                const float bb = 1f / 3f;
                                var flat = (m.WPos[m.Weld[m.TriV[t * 3]]] + m.WPos[m.Weld[m.TriV[t * 3 + 1]]] + m.WPos[m.Weld[m.TriV[t * 3 + 2]]]) / 3f;
                                float sa = TerrainRelief.SpacingAt(flat, new[] { prevPt }, 1, st.farSpacing, true), sb = TerrainRelief.SpacingAt(flat, new[] { pt }, 1, st.farSpacing, true);
                                var va = TerrainRelief.Evaluate(m, t, bb, bb, 1 - 2 * bb, sa, st, out _, out _);
                                var vb = TerrainRelief.Evaluate(m, t, bb, bb, 1 - 2 * bb, sb, st, out _, out _);
                                worstAt += $" | sigma {sa:F2}->{sb:F2} p0 {va.Position - va.FineOffset * va.Ng}->{vb.Position - vb.FineOffset * vb.Ng} fineOff {va.FineOffset:F2}->{vb.FineOffset:F2} strataAmp {va.StrataAmp:F2}/{vb.StrataAmp:F2} a {a} b {b}";
                            }
                        }
                    }
                }
                centre = want; drawn = now;
            }
            pops.Sort(); pxs.Sort();
            double P(List<double> l, double q) => l.Count == 0 ? 0 : l[Math.Min(l.Count - 1, (int)(q * l.Count))];
            Console.WriteLine($"{mode}{(lag > 0 ? $", batch lag {lag:0.#} m" : "")}{(lead > 0 && isNew ? $", lead {lead:0.#} m" : "")}: {swaps} re-centres; jump at a swap p99 {P(pops, 0.99) * 100:F1} cm, max {pops.LastOrDefault() * 100:F1} cm; " +
                              $"on screen p99 {P(pxs, 0.99):F1} px, p99.9 {P(pxs, 0.999):F1} px, max {worstPx:F1} px ({worst * 100:F0} cm at {worstAt}); " +
                              $"probes moving > 1 px per swap {pxs.Count(v => v > 1) * 100.0 / Math.Max(1, pxs.Count):F2} %");
        }
        return 0;
    }

    /// <summary>A probe (source triangle centre) as drawn: old = the CPU position at the batch spacing; new = the shader's position.</summary>
    static V3 Drawn(ReliefMesh m, int t, ReliefPoint batch, V3 cam, ReliefSettings s, bool isNew)
    {
        const float b = 1f / 3f;
        var flat = (m.WPos[m.Weld[m.TriV[t * 3]]] + m.WPos[m.Weld[m.TriV[t * 3 + 1]]] + m.WPos[m.Weld[m.TriV[t * 3 + 2]]]) / 3f;
        var pts = new[] { batch };
        float sigma = TerrainRelief.SpacingAt(flat, pts, 1, s.farSpacing, true);
        var v = TerrainRelief.Evaluate(m, t, b, b, 1 - 2 * b, sigma, s, out _, out _);
        if (!isNew) return v.Position;
        var p0 = v.Position - v.FineOffset * v.Ng;
        if (v.Sigma >= s.farSpacing - 1e-3f) return p0;
        float d = V3.Distance(p0, cam);
        float sg = Math.Max(Math.Min(Math.Max(s.nearSpacing + s.nearSlope * d, s.nearSpacing), s.farSpacing), v.Sigma);
        float fine = 0;
        for (int k = 0; k < FineOctaves.Count; k++) fine += ReliefNoise.Band(TerrainRelief.FineLambda(k, s), sg) * v.Fine[k];
        float strata = v.StrataAmp > 0 ? v.StrataAmp * ReliefNoise.Band(s.strataScale, sg) * ReliefNoise.Strata(p0, s.strataScale, 1e-3f) : 0;
        return p0 + (fine + strata) * v.Ng;
    }

    static float SegDist(float px, float pz, float ax, float az, float bx, float bz)
    {
        float vx = bx - ax, vz = bz - az, l2 = vx * vx + vz * vz;
        float t = l2 > 0 ? Math.Max(0, Math.Min(1, ((px - ax) * vx + (pz - az) * vz) / l2)) : 0;
        float dx = px - (ax + t * vx), dz = pz - (az + t * vz);
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }
}
