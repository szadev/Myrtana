using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using G3.Formats.Animation;
using G3.Formats.Pak;

/// <summary>
/// `G3Cli anim visemes <facial actor> <out.json>`: the facial head skinned in every viseme pose of its
/// `<actor>_LipSync` motion (frame i = viseme i, 25 fps; Docs/dialog-presentation.md §4), plus the bind pose.
/// Input of the viseme → ARKit fit for the MetaHuman hero head (Docs/remaster-metahuman.md §5.2,
/// Tools/Remaster/metahuman/mh_visemes.py). Actor space, centimetres, all non-collision meshes concatenated.
/// </summary>
static partial class Program
{
    static int AnimVisemes(G3FileSystem fs, string actorName, string outPath)
    {
        var ea = AnimFind(fs, actorName, ".xact");
        var em = AnimFind(fs, actorName + "_LipSync", ".xmot");
        var act = Xact.Read(ea.Read(), ea.Path).Actor;
        var mot = Xmot.Read(em.Read(), em.Path);
        var map = ActorPose.MapTracks(act, mot);
        var held = ActorPose.HeldHelpers(act, map);
        var bind = ActorPose.Bind(act);
        int frames = (int)Math.Round(mot.Duration * 25f) + 1;
        var meshes = act.Meshes.Where(m => !m.IsCollisionMesh && m.Positions.Length > 0).ToList();
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\"actor\":\"").Append(actorName).Append("\",\"fps\":25,\"frames\":").Append(frames);
        sb.Append(",\"tracked\":[").Append(string.Join(",", Enumerable.Range(0, act.Nodes.Count).Where(i => map[i] != null)
            .Select(i => "\"" + act.Nodes[i].Name + "\""))).Append(']');
        // triangles (for landmark search) in the concatenated vertex order
        sb.Append(",\"triangles\":[");
        int baseV = 0; bool first = true;
        foreach (var m in meshes)
        {
            for (int k = 0; k + 2 < m.Indices.Length; k += 3)
            {
                if (!first) sb.Append(','); first = false;
                sb.Append(m.Indices[k] + baseV).Append(',').Append(m.Indices[k + 1] + baseV).Append(',').Append(m.Indices[k + 2] + baseV);
            }
            baseV += m.Positions.Length;
        }
        sb.Append("],\"uv\":[");
        first = true;
        foreach (var m in meshes)
            for (int v = 0; v < m.Positions.Length; v++)
            {
                if (!first) sb.Append(','); first = false;
                float u = m.Uv0 != null ? m.Uv0[v * 2] : 0, w = m.Uv0 != null ? m.Uv0[v * 2 + 1] : 0;
                sb.Append(u.ToString("0.#####", ci)).Append(',').Append(w.ToString("0.#####", ci));
            }
        sb.Append("],\"poses\":[");
        for (int f = -1; f < frames; f++)
        {
            var pose = f < 0 ? bind : ActorPose.Sample(act, map, held, Math.Min(mot.Duration, f / 25f));
            if (f >= 0) sb.Append(',');
            sb.Append('[');
            first = true;
            foreach (var m in meshes)
                for (int v = 0; v < m.Positions.Length; v++)
                {
                    var p = ActorPose.SkinVertex(m, v, bind, pose);
                    if (!first) sb.Append(','); first = false;
                    sb.Append(p.X.ToString("0.###", ci)).Append(',').Append(p.Y.ToString("0.###", ci)).Append(',').Append(p.Z.ToString("0.###", ci));
                }
            sb.Append(']');
        }
        sb.Append("]}");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"{ea.Path} x {em.Path}: {frames} frames, {baseV} vertices, {map.Count(x => x != null)} tracked nodes -> {outPath} (poses[0] = bind)");
        return 0;
    }
}
