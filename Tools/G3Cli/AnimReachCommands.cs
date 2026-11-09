using System;
using System.Linq;
using G3.Formats.Animation;
using G3.Formats.Pak;

/// <summary>
/// `G3Cli anim reach <actor> <motion> <mesh> [slot] [frames]`: per frame of a motion, in actor space (cm): the pelvis
/// (Spine_Spine_1), the weapon slot and the weapon mesh's far end (the blade tip). Used to place a finishing move
/// (Docs/combat.md §5.5): where the tip is at the stab frame relative to the actor's root.
/// `G3Cli anim bone <actor> <motion> <boneSuffix> [frames]`: one bone's actor-space position over a motion.
/// </summary>
static partial class Program
{
    static int AnimReach(string data, G3FileSystem fs, string actorName, string motionName, string meshName, string slot, int frames)
    {
        var act = Xact.Read(AnimFind(fs, actorName, ".xact").Read()).Actor;
        var mot = Xmot.Read(AnimFind(fs, motionName, ".xmot").Read());
        var map = ActorPose.MapTracks(act, mot);
        var held = ActorPose.HeldHelpers(act, map);
        int si = act.FindNode(slot);
        int pelvis = Enumerable.Range(0, act.Nodes.Count).FirstOrDefault(i => act.Nodes[i].Name.EndsWith("Spine_Spine_1", StringComparison.OrdinalIgnoreCase));
        using var mfs = new G3FileSystem(data, "_compiledMesh");
        var mesh = G3.Formats.Mesh.Xcmsh.Read(AnimFind(mfs, meshName, ".xcmsh").Read());
        V3 tip = default; float far = -1;
        foreach (var el in mesh.Elements)
            for (int i = 0; i < el.Positions.Length; i += 3)
            {
                var v = new V3(el.Positions[i], el.Positions[i + 1], el.Positions[i + 2]);
                if (v.Length > far) { far = v.Length; tip = v; }
            }
        V3 tail = default; float farT = -1;
        foreach (var el in mesh.Elements)
            for (int i = 0; i < el.Positions.Length; i += 3)
            {
                var v = new V3(el.Positions[i], el.Positions[i + 1], el.Positions[i + 2]);
                if ((v - tip).Length > farT) { farT = (v - tip).Length; tail = v; }
            }
        Console.WriteLine($"{motionName}: {mot.Duration:F2}s, tip local {tip} ({far:F0} cm), other end {tail}");
        float lowest = float.MaxValue, lowT = 0;
        for (int f = 0; f <= frames; f++)
        {
            float t = mot.Duration * f / Math.Max(1, frames);
            var pose = ActorPose.Sample(act, map, held, t);
            var w = pose[si];
            var tw = w.Pos + w.Rot.Rotate(tip);
            if (tw.Y < lowest) { lowest = tw.Y; lowT = t; }
            Console.WriteLine($"  t {t:F2} pelvis {pose[pelvis].Pos} slot {w.Pos} tip {tw} end {w.Pos + w.Rot.Rotate(tail)}");
        }
        Console.WriteLine($"  lowest tip at t {lowT:F2} (y {lowest:F0})");
        return 0;
    }

    static int AnimBone(G3FileSystem fs, string actorName, string motionName, string bone, int frames)
    {
        var act = Xact.Read(AnimFind(fs, actorName, ".xact").Read()).Actor;
        var mot = Xmot.Read(AnimFind(fs, motionName, ".xmot").Read());
        var map = ActorPose.MapTracks(act, mot);
        var held = ActorPose.HeldHelpers(act, map);
        var ids = Enumerable.Range(0, act.Nodes.Count).Where(i => act.Nodes[i].Name.EndsWith(bone, StringComparison.OrdinalIgnoreCase)).ToArray();
        for (int f = 0; f <= frames; f++)
        {
            float t = mot.Duration * f / Math.Max(1, frames);
            var pose = ActorPose.Sample(act, map, held, t);
            Console.WriteLine($"  t {t:F2} " + string.Join(" ", ids.Select(i => $"{act.Nodes[i].Name} {pose[i].Pos}")));
        }
        return 0;
    }
}
