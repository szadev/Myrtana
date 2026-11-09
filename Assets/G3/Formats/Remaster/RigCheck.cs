using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// Checks that a remastered character keeps the contract of its original (Docs/remaster-characters.md §1): same node
    /// names and hierarchy (bones, Slot_* nodes, face bones), bind pose within <see cref="Options.MaxBindMillimetres"/>,
    /// skin weights summing to 1 with at most 4 influences, joints only from the original rig, UVs present; and reports
    /// vertex/UV/triangle counts and material slots. Comparisons are by name in scene space, so armature objects or
    /// extra helper nodes a tool adds (Blender's "Armature") do not matter.
    /// </summary>
    public static class RigCheck
    {
        public sealed class Options
        {
            /// <summary>Largest allowed bind-pose translation difference of any original node (and joint·IBM error).</summary>
            public double MaxBindMillimetres = 0.1;
            /// <summary>Largest allowed difference of a node's bone axes. Clips key local rotations: changed axes break them.</summary>
            public double MaxAxisDegrees = 0.05;
            public double WeightSumTolerance = 1e-3;
            public int MaxInfluences = 4;
            /// <summary>Bounding box growth (cm) above which the silhouette is reported as changed (warning only).</summary>
            public double BoundsWarnCentimetres = 5;
        }

        public enum Level { Info, Pass, Warn, Fail }

        public sealed class Line
        {
            public Level Level;
            public string Check, Detail;
            public override string ToString() => $"{Level.ToString().ToUpperInvariant(),-4} {Check}: {Detail}";
        }

        public sealed class Report
        {
            public readonly List<Line> Lines = new List<Line>();
            public bool Passed => Lines.All(l => l.Level != Level.Fail);
            public int Failures => Lines.Count(l => l.Level == Level.Fail);
            public int Warnings => Lines.Count(l => l.Level == Level.Warn);
            internal void Add(Level level, string check, string detail) => Lines.Add(new Line { Level = level, Check = check, Detail = detail });
            public string ToText()
            {
                var sb = new StringBuilder();
                foreach (var l in Lines) sb.AppendLine(l.ToString());
                sb.Append(Passed ? "RESULT PASS" : "RESULT FAIL").Append($" ({Failures} failed, {Warnings} warnings)");
                return sb.ToString();
            }
        }

        static string F(double v, string fmt = "0.####") => v.ToString(fmt, CultureInfo.InvariantCulture);

        /// <summary>Checks <paramref name="candidate"/> against <paramref name="original"/>.</summary>
        public static Report Compare(RigModel original, RigModel candidate, Options o = null)
        {
            o ??= new Options();
            var r = new Report();
            // Rig nodes: everything except the nodes that only carry a mesh.
            var rig = original.Nodes.Select((n, i) => (n, i)).Where(x => !x.n.CarriesMesh).ToList();
            var origNames = new HashSet<string>(rig.Select(x => x.n.Name), StringComparer.OrdinalIgnoreCase);

            // Nodes present.
            var missing = rig.Where(x => candidate.FindNode(x.n.Name) < 0).Select(x => x.n.Name).ToList();
            var extra = candidate.Nodes.Where(n => !origNames.Contains(n.Name) && !n.CarriesMesh).Select(n => n.Name).ToList();
            r.Add(missing.Count == 0 ? Level.Pass : Level.Fail, "nodes",
                missing.Count == 0 ? $"all {rig.Count} original nodes present" : $"{missing.Count} missing: {Short(missing)}");
            if (extra.Count > 0) r.Add(Level.Info, "extra nodes", $"{extra.Count}: {Short(extra)}");

            // Hierarchy: the nearest original-named ancestor must be the same parent.
            var wrongParent = new List<string>();
            foreach (var (n, _) in rig)
            {
                int c = candidate.FindNode(n.Name);
                if (c < 0) continue;
                string want = n.Parent >= 0 ? original.Nodes[n.Parent].Name : null;
                string got = null;
                for (int p = candidate.Nodes[c].Parent; p >= 0; p = candidate.Nodes[p].Parent)
                    if (origNames.Contains(candidate.Nodes[p].Name)) { got = candidate.Nodes[p].Name; break; }
                if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) wrongParent.Add($"{n.Name} ({got ?? "root"} instead of {want ?? "root"})");
            }
            r.Add(wrongParent.Count == 0 ? Level.Pass : Level.Fail, "hierarchy",
                wrongParent.Count == 0 ? "same parents" : $"{wrongParent.Count} differ: {Short(wrongParent)}");

            // Bind pose of every node (scene space).
            var ow = original.Worlds();
            var cw = candidate.Worlds();
            double worstMm = 0, worstDeg = 0; string worstMmNode = "", worstDegNode = "";
            foreach (var (n, i) in rig)
            {
                int c = candidate.FindNode(n.Name);
                if (c < 0) continue;
                double mm = M4.TranslationDelta(ow[i], cw[c]) * 1000, deg = M4.AxisDeltaDegrees(ow[i], cw[c]);
                if (mm > worstMm) { worstMm = mm; worstMmNode = n.Name; }
                if (deg > worstDeg) { worstDeg = deg; worstDegNode = n.Name; }
            }
            r.Add(worstMm <= o.MaxBindMillimetres ? Level.Pass : Level.Fail, "bind pose position",
                $"max {F(worstMm)} mm{(worstMm > 0 ? " at " + worstMmNode : "")} (limit {F(o.MaxBindMillimetres)} mm)");
            r.Add(worstDeg <= o.MaxAxisDegrees ? Level.Pass : Level.Fail, "bind pose axes",
                $"max {F(worstDeg)}°{(worstDeg > 0 ? " at " + worstDegNode : "")} (limit {F(o.MaxAxisDegrees)}°)");

            // Slots.
            var slots = original.Nodes.Where(n => n.Name.StartsWith("Slot_", StringComparison.OrdinalIgnoreCase)).Select(n => n.Name).ToList();
            var badSlots = slots.Where(s => candidate.FindNode(s) < 0 ||
                                            M4.TranslationDelta(ow[original.FindNode(s)], cw[candidate.FindNode(s)]) * 1000 > o.MaxBindMillimetres).ToList();
            if (slots.Count > 0)
                r.Add(badSlots.Count == 0 ? Level.Pass : Level.Fail, "slots",
                    badSlots.Count == 0 ? $"{slots.Count} Slot_* nodes in place" : $"missing or moved: {Short(badSlots)}");

            // Meshes.
            for (int mi = 0; mi < candidate.Meshes.Count; mi++)
            {
                var cm = candidate.Meshes[mi];
                var om = original.Meshes.FirstOrDefault(m => string.Equals(m.Name, cm.Name, StringComparison.OrdinalIgnoreCase))
                         ?? (mi < original.Meshes.Count ? original.Meshes[mi] : null);
                CheckMesh(r, o, original, candidate, om, cm, origNames);
            }
            if (candidate.Meshes.Count == 0) r.Add(Level.Fail, "meshes", "no mesh");
            return r;
        }

        static void CheckMesh(Report r, Options o, RigModel original, RigModel candidate, RigMesh om, RigMesh cm, HashSet<string> origNames)
        {
            string tag = $"mesh {cm.Name}";
            r.Add(Level.Info, tag, $"{cm.VertexCount} vertices, {cm.TriangleCount} triangles, UV0 {(cm.Uv0 != null ? cm.VertexCount : 0)}, " +
                                   $"UV1 {(cm.Uv1 != null ? cm.VertexCount : 0)}, {(cm.IsSkinned ? cm.Joints.Length + " joints" : "static on " + NodeName(candidate, cm.Node))}" +
                                   (om != null ? $" | original {om.VertexCount} vertices, {om.TriangleCount} triangles ({F(cm.TriangleCount / (double)Math.Max(1, om.TriangleCount), "0.##")}×)" : ""));
            r.Add(cm.Uv0 != null ? Level.Pass : Level.Fail, tag + " uv", cm.Uv0 != null ? "UV0 present" : "no UV0");
            if (om != null && om.IsSkinned != cm.IsSkinned)
                r.Add(Level.Fail, tag + " skinning", om.IsSkinned ? "original is skinned, remaster is static" : "original is static, remaster is skinned");

            if (cm.IsSkinned)
            {
                // Joints must be original nodes; joint·IBM must be the bind pose.
                var foreign = cm.Joints.Select(j => candidate.Nodes[j].Name).Where(n => !origNames.Contains(n)).ToList();
                r.Add(foreign.Count == 0 ? Level.Pass : Level.Fail, tag + " joints",
                    foreign.Count == 0 ? $"{cm.Joints.Length} joints, all original" : $"not in the original rig: {Short(foreign)}");
                var cw = candidate.Worlds();
                double worst = 0; string at = "";
                for (int j = 0; j < cm.Joints.Length; j++)
                {
                    var m = M4.Mul(cw[cm.Joints[j]], cm.InverseBind[j]);
                    double e = Math.Max(M4.TranslationDelta(m, M4.Identity()) * 1000, 0);
                    double deg = M4.AxisDeltaDegrees(m, M4.Identity());
                    double score = Math.Max(e, deg * 10); // 0.01° ≈ 0.1 mm at 0.6 m
                    if (score > worst) { worst = score; at = candidate.Nodes[cm.Joints[j]].Name; }
                }
                r.Add(worst <= o.MaxBindMillimetres ? Level.Pass : Level.Fail, tag + " inverse bind",
                    $"joint·IBM off identity by {F(worst)} (mm, or 0.1° ≙ 1){(worst > 0 ? " at " + at : "")}");

                int badSum = 0, tooMany = 0, unweighted = 0, maxInf = 0;
                double worstSum = 0;
                for (int v = 0; v < cm.VertexCount; v++)
                {
                    double sum = 0; int nz = 0;
                    for (int k = 0; k < cm.Stride; k++)
                    {
                        float w = cm.Weights[v * cm.Stride + k];
                        if (w > 1e-6f) { sum += w; nz++; }
                    }
                    if (nz == 0) unweighted++;
                    if (Math.Abs(sum - 1) > o.WeightSumTolerance) { badSum++; worstSum = Math.Max(worstSum, Math.Abs(sum - 1)); }
                    if (nz > o.MaxInfluences) tooMany++;
                    maxInf = Math.Max(maxInf, nz);
                }
                r.Add(badSum == 0 ? Level.Pass : Level.Fail, tag + " weight sums",
                    badSum == 0 ? "all = 1" : $"{badSum} vertices off by up to {F(worstSum)}{(unweighted > 0 ? $", {unweighted} unweighted" : "")}");
                r.Add(tooMany == 0 ? Level.Pass : Level.Fail, tag + " influences",
                    tooMany == 0 ? $"max {maxInf} per vertex" : $"{tooMany} vertices with more than {o.MaxInfluences} (max {maxInf})");
            }

            // Materials: slots should be the original's (new ones are allowed, but need a material on import).
            var origMats = new HashSet<string>(original.Meshes.SelectMany(m => m.SubMeshes).Select(s => s.Material ?? ""), StringComparer.OrdinalIgnoreCase);
            var newMats = cm.SubMeshes.Select(s => s.Material ?? "").Where(m => !origMats.Contains(m)).Distinct().ToList();
            r.Add(newMats.Count == 0 ? Level.Pass : Level.Warn, tag + " materials",
                $"{cm.SubMeshes.Count} slots ({string.Join(", ", cm.SubMeshes.Select(s => s.Material))})" + (newMats.Count > 0 ? $"; not in the original: {Short(newMats)}" : ""));

            // Winding vs normals and silhouette (scene space).
            candidate.RestWorld(cm, out var pos, out var nrm);
            if (nrm != null)
            {
                int agree = 0, total = 0;
                foreach (var sm in cm.SubMeshes)
                    for (int t = 0; t + 2 < sm.Indices.Length; t += 3)
                    {
                        int a = sm.Indices[t], b = sm.Indices[t + 1], c = sm.Indices[t + 2];
                        double ux = pos[b * 3] - pos[a * 3], uy = pos[b * 3 + 1] - pos[a * 3 + 1], uz = pos[b * 3 + 2] - pos[a * 3 + 2];
                        double vx = pos[c * 3] - pos[a * 3], vy = pos[c * 3 + 1] - pos[a * 3 + 1], vz = pos[c * 3 + 2] - pos[a * 3 + 2];
                        double gx = uy * vz - uz * vy, gy = uz * vx - ux * vz, gz = ux * vy - uy * vx;
                        double nx = nrm[a * 3] + nrm[b * 3] + nrm[c * 3], ny = nrm[a * 3 + 1] + nrm[b * 3 + 1] + nrm[c * 3 + 1], nz = nrm[a * 3 + 2] + nrm[b * 3 + 2] + nrm[c * 3 + 2];
                        double d = gx * nx + gy * ny + gz * nz;
                        if (Math.Abs(d) < 1e-12) continue;
                        total++;
                        if (d > 0) agree++;
                    }
                double share = total > 0 ? agree / (double)total : 1;
                r.Add(share >= 0.9 ? Level.Pass : Level.Warn, tag + " winding", $"{F(share * 100, "0.#")}% of triangles face along their normals (CCW)");
            }
            // FACES heads (fix-52): eyeball/cornea facing out, no skin inside an eyeball (RemasterEyes; the importer repairs
            // older files, so a Fail here means the Blender side still builds them wrong)
            var eyes = RemasterEyes.Check(cm);
            if (eyes.HasEyes) r.Add(eyes.Passed ? Level.Pass : Level.Fail, tag + " eyes", eyes.ToString());
            // v2 heads (hero-v2, runner-31 HV1): no G3 / R1 face material next to the v2 skin (different UV layout)
            var v2Foreign = RemasterGenerated.V2HeadForeignMaterials(cm.SubMeshes.Where(s => s.Indices.Length > 0).Select(s => s.Material));
            if (cm.SubMeshes.Any(s => RemasterGenerated.Classify(s.Material) == RemasterGeneratedKind.SkinV2))
                r.Add(v2Foreign.Count == 0 ? Level.Pass : Level.Fail, tag + " v2 materials",
                      v2Foreign.Count == 0 ? "only generated materials next to the v2 skin" : "G3/R1 materials on a v2 head: " + string.Join(", ", v2Foreign));
            if (om != null)
            {
                original.RestWorld(om, out var opos, out _);
                var (cmin, cmax) = Bounds(pos);
                var (omin, omax) = Bounds(opos);
                double d = 0;
                for (int k = 0; k < 3; k++) d = Math.Max(d, Math.Max(Math.Abs(cmin[k] - omin[k]), Math.Abs(cmax[k] - omax[k])));
                r.Add(d * 100 <= o.BoundsWarnCentimetres ? Level.Pass : Level.Warn, tag + " bounds", $"rest bounds differ by up to {F(d * 100, "0.##")} cm");
            }
        }

        static (double[] min, double[] max) Bounds(float[] p)
        {
            var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            var max = new[] { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i + 2 < p.Length; i += 3)
                for (int k = 0; k < 3; k++) { min[k] = Math.Min(min[k], p[i + k]); max[k] = Math.Max(max[k], p[i + k]); }
            if (p.Length == 0) { min = new double[3]; max = new double[3]; }
            return (min, max);
        }

        static string NodeName(RigModel m, int node) => node >= 0 && node < m.Nodes.Count ? m.Nodes[node].Name : "root";

        static string Short(List<string> items) => items.Count <= 8 ? string.Join(", ", items) : string.Join(", ", items.Take(8)) + $", … (+{items.Count - 8})";

        /// <summary>Summary of a single file (no reference): nodes, slots, meshes, weights.</summary>
        public static string Describe(RigModel m)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"generator: {m.Generator}");
            sb.AppendLine($"nodes: {m.Nodes.Count} ({m.Nodes.Count(n => n.Name.StartsWith("Slot_", StringComparison.OrdinalIgnoreCase))} Slot_*)");
            foreach (var mesh in m.Meshes)
                sb.AppendLine($"mesh {mesh.Name}: {mesh.VertexCount} v, {mesh.TriangleCount} tris, {(mesh.IsSkinned ? mesh.Joints.Length + " joints, stride " + mesh.Stride : "static on " + NodeName(m, mesh.Node))}, " +
                              $"materials {string.Join(", ", mesh.SubMeshes.Select(s => s.Material))}");
            foreach (var mat in m.Materials)
                sb.AppendLine($"material {mat.Name}: {(mat.BaseColorImage != null ? "base colour " + mat.BaseColorImage.Length + " B" : "no base colour")}" +
                              $"{(mat.NormalImage != null ? ", normal " + mat.NormalImage.Length + " B" : "")}");
            return sb.ToString().TrimEnd();
        }
    }
}
