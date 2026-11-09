using System;
using System.Collections.Generic;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Mesh;
using G3.Formats.Pak;
using G3.Unity.Characters;
using NV3 = System.Numerics.Vector3;

/// <summary>
/// `G3Cli anim beard <facial actor> <beard mesh>` (fix-246): the beard hung on the facial actor's Slot_Beard, skinned to
/// the head by <see cref="FacialHairSkin"/> (closest skin point weights), posed in every viseme of `<actor>_LipSync`.
/// Prints the bones the beard takes, and per viseme how far the chin and the beard at the chin move (cm, actor space):
/// the beard must move with the chin (G3 itself keeps it rigid on the head bone, i.e. 0 cm).
/// </summary>
static partial class Program
{
    static int AnimBeard(string data, G3FileSystem fs, string actorName, string beardName, int smooth = 2)
    {
        var act = Xact.Read(AnimFind(fs, actorName, ".xact").Read(), actorName).Actor;
        var mot = Xmot.Read(AnimFind(fs, actorName + "_LipSync", ".xmot").Read(), actorName + "_LipSync");
        using var meshFs = new G3FileSystem(data, "_compiledMesh");
        if (!beardName.EndsWith(".xcmsh", StringComparison.OrdinalIgnoreCase)) beardName += ".xcmsh";
        var be = meshFs.GetByName(beardName) ?? throw new Exception($"no {beardName}");
        var beard = Xcmsh.Read(be.Read(), beardName);
        int slot = act.FindNode(FacialHairSkin.BeardSlot);
        if (slot < 0) throw new Exception($"{actorName}: no {FacialHairSkin.BeardSlot}");

        var map = ActorPose.MapTracks(act, mot);
        var held = ActorPose.HeldHelpers(act, map);
        var bind = ActorPose.Bind(act);

        // head skin (actor space, bind) with the skin submeshes only, and its weights by actor node
        var skinPos = new List<NV3>();
        var skinTris = new List<int>();
        var skinW = new List<BoneWeights4>();
        var skinRef = new List<(ActorMesh m, int v)>();
        var nodeIndex = new Dictionary<int, int>(); // actor node -> dense bone index
        var nodes = new List<int>();
        int Bone(int node)
        {
            if (!nodeIndex.TryGetValue(node, out int b)) { b = nodes.Count; nodes.Add(node); nodeIndex[node] = b; }
            return b;
        }
        foreach (var m in act.Meshes.Where(m => !m.IsCollisionMesh && m.Positions.Length > 0))
        {
            int baseV = skinPos.Count;
            for (int v = 0; v < m.Positions.Length; v++)
            {
                var p = ActorPose.SkinVertex(m, v, bind, bind);
                skinPos.Add(new NV3(p.X, p.Y, p.Z));
                skinRef.Add((m, v));
                var inf = m.Skin?[m.OrgVertex[v]];
                if (inf == null || inf.Length == 0) inf = new[] { new SkinInfluence((ushort)m.Node, 1) };
                var top = inf.OrderByDescending(s => s.Weight).Take(4).ToArray();
                float sum = top.Sum(s => s.Weight);
                var w = new BoneWeights4();
                for (int k = 0; k < top.Length; k++)
                {
                    int b = Bone(top[k].Node);
                    float x = top[k].Weight / sum;
                    if (k == 0) { w.B0 = b; w.W0 = x; }
                    else if (k == 1) { w.B1 = b; w.W1 = x; }
                    else if (k == 2) { w.B2 = b; w.W2 = x; }
                    else { w.B3 = b; w.W3 = x; }
                }
                skinW.Add(w);
            }
            foreach (var sm in m.SubMeshes)
            {
                if (!FacialHairSkin.IsSkinMaterial(act.MaterialName(sm.MaterialId))) continue;
                for (int i = 0; i < sm.IndexCount; i++) skinTris.Add(baseV + m.Indices[sm.IndexStart + i]);
            }
        }

        // the beard in actor space (slot frame -> actor)
        var s0 = bind[slot];
        var hair = new List<NV3>();
        var hairTris = new List<int>();
        foreach (var el in beard.Elements)
        {
            int baseV = hair.Count;
            for (int v = 0; v < el.VertexCount; v++)
            {
                var p = s0.Pos + s0.Rot.Rotate(new V3(el.Positions[v * 3], el.Positions[v * 3 + 1], el.Positions[v * 3 + 2]));
                hair.Add(new NV3(p.X, p.Y, p.Z));
            }
            foreach (var i in el.Indices) hairTris.Add(baseV + (int)i);
        }
        var hairArr = hair.ToArray();
        var pts = FacialHairSkin.Map(skinPos.ToArray(), skinTris.ToArray(), hairArr);
        var weights = FacialHairSkin.TransferWeights(pts, skinW.ToArray(), nodes.Count,
                                                     FacialHairSkin.Neighbours(hairArr, hairTris.ToArray(), 0.01f, 1f), Bone(act.Nodes[slot].Parent), smooth,
                                                     FacialHairSkin.Subtree(nodes.Select(n => act.Nodes[n].Parent >= 0 && nodeIndex.TryGetValue(act.Nodes[n].Parent, out int pb) ? pb : -1).ToArray(), Bone(act.Nodes[slot].Parent)));
        Console.WriteLine($"{actorName} + {beardName}: {skinPos.Count} skin verts, {skinTris.Count / 3} skin tris, {hair.Count} beard verts, " +
                          $"distance to skin median {Median(pts.Select(p => p.Distance))} cm max {pts.Max(p => p.Distance):0.0} cm");
        var share = new double[nodes.Count];
        foreach (var w in weights) { share[w.B0] += w.W0; share[w.B1] += w.W1; share[w.B2] += w.W2; share[w.B3] += w.W3; }
        Console.WriteLine("  bones: " + string.Join(", ", Enumerable.Range(0, nodes.Count).Where(b => share[b] > 0.005 * hair.Count)
            .OrderByDescending(b => share[b]).Select(b => $"{act.Nodes[nodes[b]].Name} {100 * share[b] / hair.Count:0}%")));

        // per viseme: how far the skin under the beard moves (a rigid beard, as in G3, is off by that much: the chin pokes
        // through) against how far the skinned beard is off the skin's motion
        int frames = (int)Math.Round(mot.Duration * 25f) + 1;
        for (int f = 0; f < frames; f++)
        {
            var pose = ActorPose.Sample(act, map, held, Math.Min(mot.Duration, f / 25f));
            var posed = new NV3[skinPos.Count];
            for (int i = 0; i < posed.Length; i++)
            {
                var q = ActorPose.SkinVertex(skinRef[i].m, skinRef[i].v, bind, pose);
                posed[i] = new NV3(q.X, q.Y, q.Z);
            }
            float rigid = 0, skinned = 0, beardMax = 0;
            for (int i = 0; i < hair.Count; i++)
            {
                var sp = pts[i];
                if (sp.A < 0) continue;
                var under = (posed[sp.A] - skinPos[sp.A]) * sp.WA + (posed[sp.B] - skinPos[sp.B]) * sp.WB + (posed[sp.C] - skinPos[sp.C]) * sp.WC;
                var moved = Skin(hair[i], weights[i], nodes, bind, pose) - hair[i];
                rigid = Math.Max(rigid, under.Length());
                skinned = Math.Max(skinned, (moved - under).Length());
                beardMax = Math.Max(beardMax, moved.Length());
            }
            Console.WriteLine($"  viseme {f,2}: skin under the beard moves up to {rigid:0.00} cm (rigid beard off by that), skinned beard moves " +
                              $"{beardMax:0.00} cm and is off the skin by {skinned:0.00} cm");
        }
        return 0;
    }

    static NV3 Skin(NV3 point, BoneWeights4 w, List<int> nodes, ActorPose.Xf[] bind, ActorPose.Xf[] pose)
    {
        var p = new V3(point.X, point.Y, point.Z);
        V3 acc = default;
        foreach (var (b, x) in new[] { (w.B0, w.W0), (w.B1, w.W1), (w.B2, w.W2), (w.B3, w.W3) })
        {
            if (x <= 0) continue;
            var bb = bind[nodes[b]];
            var q = pose[nodes[b]];
            acc = acc + (q.Pos + q.Rot.Rotate(bb.Rot.Conjugate.Rotate(p - bb.Pos))) * x;
        }
        return new NV3(acc.X, acc.Y, acc.Z);
    }

    static string Median(IEnumerable<float> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        return a.Length == 0 ? "-" : a[a.Length / 2].ToString("0.0");
    }
}
