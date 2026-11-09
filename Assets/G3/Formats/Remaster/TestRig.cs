using System;
using System.Collections.Generic;
using System.Linq;

namespace G3.Formats.Remaster
{
    /// <summary>
    /// A generated stand-in for an exported G3 character (no game data): a G3-style skeleton (Hero_* bones with
    /// rotated, biped-like local frames, face bones, Slot_* nodes), a skinned low-poly tube body with two material slots
    /// and a static hair box on Slot_Hair. Built in Unity space and mirrored like the real exporter, so it exercises the
    /// same conversions. Used by the tests and the Blender pipeline checks (<c>G3Cli remaster testrig</c>).
    /// </summary>
    public static class TestRig
    {
        public const string BodyMaterial = "TestRig_Body", SkinMaterial = "TestRig_Skin", HairMaterial = "TestRig_Hair";

        public static RigModel Build(int ringSegments = 8, int rings = 4)
        {
            var m = new RigModel { Generator = "G3World TestRig" };
            var world = new List<double[]>();
            int Bone(string name, int parent, double x, double y, double z, double ax, double ay, double az, double deg)
            {
                var w = M4.Trs(new[] { x, y, z }, AxisAngle(ax, ay, az, deg), null);
                var local = parent >= 0 ? M4.Mul(M4.Inverse(world[parent]), w) : w;
                M4.Decompose(local, out var t, out var q, out _);
                world.Add(w);
                return m.AddNode(name, parent, t, q);
            }

            int root = Bone("G3_TestRig", -1, 0, 0, 0, 0, 1, 0, 0);
            int hroot = Bone("Hero_ROOT", root, 0, 0, 0, 0, 1, 0, 90);
            int pelvis = Bone("Hero_Pelvis", hroot, 0, 1.0, 0, 1, 0.2, 0, 90);
            int sp1 = Bone("Hero_Spine_Spine_1", pelvis, 0, 1.15, 0, 0.1, 0, 1, 90);
            int sp2 = Bone("Hero_Spine_Spine_2", sp1, 0, 1.35, 0, 0.1, 0, 1, 85);
            int neck = Bone("Hero_Neck_Neck", sp2, 0, 1.55, 0, 0, 0, 1, 95);
            int head = Bone("Hero_Head_Head", neck, 0, 1.65, 0.01, 0, 0.3, 1, 90);
            int jaw = Bone("Hero_Head_Jaw_B", head, 0, 1.62, 0.05, 1, 0, 0, -30);
            Bone("Hero_Head_Lip_Upper_B", head, 0, 1.66, 0.09, 0, 1, 0, 180);
            Bone("Hero_Head_Lip_Lower_B", jaw, 0, 1.6, 0.09, 0, 1, 0, 170);
            int hairSlot = Bone("Slot_Hair", head, 0, 1.75, 0, 0, 0, 1, 90);
            Bone("Slot_Beard", head, 0, 1.58, 0.07, 0, 0, 1, 90);
            Bone("Slot_Helmet", head, 0, 1.76, 0, 0, 0, 1, 90);
            Bone("Slot_Back_Weapon", sp2, 0, 1.4, -0.15, 0, 0, 1, 90);
            var segments = new List<(int a, int b, double r, string mat)>
            {
                (pelvis, sp1, 0.16, BodyMaterial), (sp1, sp2, 0.17, BodyMaterial), (sp2, neck, 0.16, BodyMaterial),
                (neck, head, 0.06, SkinMaterial), (head, hairSlot, 0.1, SkinMaterial),
            };
            foreach (var (side, sx) in new[] { ("R", 1.0), ("L", -1.0) })
            {
                int armRoot = Bone($"Hero_{side}_Arm_ROOT", sp2, 0.12 * sx, 1.5, 0, 0, 0, 1, 180 * (sx < 0 ? 1 : 0));
                int upper = Bone($"Hero_{side}_Arm_UpperArm", armRoot, 0.2 * sx, 1.5, 0, 0, 1, 0.2, 10 * sx);
                int fore = Bone($"Hero_{side}_Arm_Forearm", upper, 0.45 * sx, 1.5, 0.02, 0, 1, 0, 25 * sx);
                int hand = Bone($"Hero_{side}_Arm_Hand", fore, 0.7 * sx, 1.5, 0.05, 1, 0, 0, 90);
                Bone($"Hero_{side}_Arm_END", hand, 0.8 * sx, 1.5, 0.05, 1, 0, 0, 90);
                int slot = Bone(side == "R" ? "Slot_RightHand_Weapon" : "Slot_LeftHand_Weapon", hand, 0.75 * sx, 1.48, 0.05, 0, 0, 1, -90);
                int legRoot = Bone($"Hero_{side}_Leg_ROOT", pelvis, 0.1 * sx, 0.97, 0, 0, 0, 1, -90);
                int thigh = Bone($"Hero_{side}_Leg_Thigh", legRoot, 0.1 * sx, 0.95, 0, 0, 0, 1, -92);
                int calf = Bone($"Hero_{side}_Leg_Calf", thigh, 0.1 * sx, 0.5, 0.02, 0, 0, 1, -88);
                int foot = Bone($"Hero_{side}_Leg_Foot", calf, 0.1 * sx, 0.08, 0, 1, 0, 0, 45);
                Bone($"Hero_{side}_Leg_END", foot, 0.1 * sx, 0.02, 0.15, 1, 0, 0, 45);
                segments.Add((upper, fore, 0.06, SkinMaterial));
                segments.Add((fore, hand, 0.05, SkinMaterial));
                segments.Add((hand, slot, 0.045, SkinMaterial));
                segments.Add((thigh, calf, 0.08, BodyMaterial));
                segments.Add((calf, foot, 0.06, BodyMaterial));
            }

            // Skinned tube body: one ring set per segment, weights blend a → b along it (slot ends stay on a).
            var body = new RigMesh { Name = "G3_TestRig_Body" };
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var ji = new List<int>(); var wt = new List<float>();
            var tris = new Dictionary<string, List<int>> { [BodyMaterial] = new List<int>(), [SkinMaterial] = new List<int>() };
            var joints = new List<int>();
            int J(int node) { int i = joints.IndexOf(node); if (i < 0) { joints.Add(node); i = joints.Count - 1; } return i; }
            for (int s = 0; s < segments.Count; s++)
            {
                var (a, b, r, mat) = segments[s];
                double[] pa = { world[a][3], world[a][7], world[a][11] }, pb = { world[b][3], world[b][7], world[b][11] };
                double[] axis = Norm(Sub(pb, pa));
                double[] u = Norm(Cross(axis, Math.Abs(axis[1]) < 0.9 ? new double[] { 0, 1, 0 } : new double[] { 1, 0, 0 }));
                double[] v = Cross(axis, u);
                bool slotEnd = m.Nodes[b].Name.StartsWith("Slot_", StringComparison.Ordinal);
                int baseV = pos.Count / 3;
                for (int ring = 0; ring <= rings; ring++)
                {
                    double t = ring / (double)rings;
                    for (int k = 0; k <= ringSegments; k++)
                    {
                        double ang = 2 * Math.PI * k / ringSegments;
                        double[] dir = Add(Mul(u, Math.Cos(ang)), Mul(v, Math.Sin(ang)));
                        double[] p = Add(Add(pa, Mul(Sub(pb, pa), t)), Mul(dir, r));
                        pos.AddRange(new[] { (float)p[0], (float)p[1], (float)p[2] });
                        nrm.AddRange(new[] { (float)dir[0], (float)dir[1], (float)dir[2] });
                        uv.AddRange(new[] { (float)((s % 4 + k / (double)ringSegments) / 4), (float)((s / 4 + t) / 4) });
                        float wb = slotEnd ? 0 : (float)t;
                        ji.AddRange(new[] { J(a), J(b), 0, 0 });
                        wt.AddRange(new[] { 1 - wb, wb, 0f, 0f });
                    }
                }
                var list = tris[mat];
                int row = ringSegments + 1;
                for (int ring = 0; ring < rings; ring++)
                    for (int k = 0; k < ringSegments; k++)
                    {
                        int i0 = baseV + ring * row + k, i1 = i0 + 1, i2 = i0 + row, i3 = i2 + 1;
                        AddOutward(list, pos, nrm, i0, i2, i1);
                        AddOutward(list, pos, nrm, i1, i2, i3);
                    }
            }
            body.Positions = pos.ToArray(); body.Normals = nrm.ToArray(); body.Uv0 = uv.ToArray();
            body.JointIndices = ji.ToArray(); body.Weights = wt.ToArray(); body.Joints = joints.ToArray();
            body.InverseBind = joints.Select(j => M4.Inverse(world[j])).ToArray();
            body.SubMeshes.Add(new RigSubMesh { Material = BodyMaterial, Indices = tris[BodyMaterial].ToArray() });
            body.SubMeshes.Add(new RigSubMesh { Material = SkinMaterial, Indices = tris[SkinMaterial].ToArray() });
            m.Meshes.Add(body);

            // Static hair box in the Slot_Hair frame.
            var hair = new RigMesh { Name = "G3_TestRig_Hair", Node = hairSlot };
            BuildBox(hair, 0.11, 0.05, 0.12);
            m.Meshes.Add(hair);
            m.Materials.Add(new RigMaterial { Name = BodyMaterial });
            m.Materials.Add(new RigMaterial { Name = SkinMaterial });
            m.Materials.Add(new RigMaterial { Name = HairMaterial });

            m.MirrorX(); // Unity space → glTF space, as the exporter does
            return m;
        }

        /// <summary>Triangle with cross(p1−p0, p2−p0) along the vertex normals (Unity's stored order for front faces).</summary>
        static void AddOutward(List<int> list, List<float> pos, List<float> nrm, int a, int b, int c)
        {
            double[] P(int i) => new double[] { pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2] };
            var g = Cross(Sub(P(b), P(a)), Sub(P(c), P(a)));
            double d = g[0] * nrm[a * 3] + g[1] * nrm[a * 3 + 1] + g[2] * nrm[a * 3 + 2];
            if (d >= 0) list.AddRange(new[] { a, b, c }); else list.AddRange(new[] { a, c, b });
        }

        static void BuildBox(RigMesh mesh, double hx, double hy, double hz)
        {
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
            var faces = new[] { new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 } };
            foreach (var f in faces)
            {
                double[] n = { f[0], f[1], f[2] };
                double[] u = Math.Abs(n[1]) > 0.5 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
                double[] v = Cross(n, u);
                int b = pos.Count / 3;
                foreach (var (su, sv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    double[] p = Add(Add(Mul(n, 1), Mul(u, su)), Mul(v, sv));
                    pos.AddRange(new[] { (float)(p[0] * hx), (float)(p[1] * hy), (float)(p[2] * hz) });
                    nrm.AddRange(new[] { (float)n[0], (float)n[1], (float)n[2] });
                    uv.AddRange(new[] { (su + 1) / 2f, (sv + 1) / 2f });
                }
                AddOutward(idx, pos, nrm, b, b + 1, b + 2);
                AddOutward(idx, pos, nrm, b, b + 2, b + 3);
            }
            mesh.Positions = pos.ToArray(); mesh.Normals = nrm.ToArray(); mesh.Uv0 = uv.ToArray();
            mesh.SubMeshes.Add(new RigSubMesh { Material = HairMaterial, Indices = idx.ToArray() });
        }

        static double[] AxisAngle(double x, double y, double z, double deg)
        {
            double l = Math.Sqrt(x * x + y * y + z * z), h = deg * Math.PI / 360, s = Math.Sin(h) / l;
            return new[] { x * s, y * s, z * s, Math.Cos(h) };
        }

        static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        static double[] Mul(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double[] Norm(double[] a) { double l = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]); return Mul(a, 1 / l); }
    }
}
