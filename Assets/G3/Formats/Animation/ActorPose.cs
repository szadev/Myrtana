using System;
using System.Collections.Generic;

namespace G3.Formats.Animation
{
    /// <summary>
    /// Reference posing of an actor skeleton by a motion, in engine space (Docs/formats-animation.md §4).
    /// Rules: a motion track's key values are the node's absolute local transform relative to the nearest
    /// ancestor that is also tracked; untracked ancestors in between (the *_ROOT / *_END helpers) are folded into
    /// the key, so while a clip plays they are held at identity. Untracked nodes without tracked descendants keep
    /// their bind local transform.
    /// </summary>
    public static class ActorPose
    {
        public struct Xf { public V3 Pos; public Q4 Rot; }

        public static Xf[] Bind(EmfxActor a)
        {
            var w = new Xf[a.Nodes.Count];
            var done = new bool[w.Length];
            for (int i = 0; i < w.Length; i++) Resolve(a, i, w, done, (n) => (a.Nodes[n].Parent, a.Nodes[n].Position, a.Nodes[n].Rotation.Normalized));
            return w;
        }

        /// <summary>Track per actor node (null when the node is not animated by this motion).</summary>
        public static MotionTrack[] MapTracks(EmfxActor a, MotionResource m)
        {
            var map = new MotionTrack[a.Nodes.Count];
            var byName = new Dictionary<string, MotionTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in m.Tracks) byName[t.Node] = t;
            for (int i = 0; i < map.Length; i++) byName.TryGetValue(a.Nodes[i].Name, out map[i]);
            return map;
        }

        /// <summary>
        /// Nodes that lie between a tracked node and its nearest tracked ancestor (held at identity during the clip).
        /// Only the <c>*_ROOT</c> / <c>*_END</c> helpers are folded, never an untracked bone (fix-89): a partial motion (an
        /// overlay, variant <c>_O_</c>, e.g. a draw that tracks only the arms and <c>Hero_ROOT</c>) keeps its keys relative
        /// to the nearest real bone (the arm's to <c>Spine_5</c>); holding the untracked spine at identity folded the torso
        /// onto the pelvis. In full-body motions every bone is tracked, so nothing changes there.
        /// </summary>
        public static bool[] HeldHelpers(EmfxActor a, MotionTrack[] map)
        {
            var held = new bool[map.Length];
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] == null) continue;
                for (int p = a.Nodes[i].Parent; p >= 0 && map[p] == null && IsHelper(a.Nodes[p].Name); p = a.Nodes[p].Parent)
                    held[p] = true;
            }
            return held;
        }

        /// <summary>A Max biped helper node (<c>*_ROOT</c>, <c>*_END</c>): never tracked, folded into the next tracked bone's keys.</summary>
        public static bool IsHelper(string node) =>
            node != null && (node.EndsWith("_ROOT", StringComparison.OrdinalIgnoreCase) || node.EndsWith("_END", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// fix-89: the nodes a motion drives when it plays as an overlay over another motion (G3/EMotionFX mixing: a motion
        /// only sets the nodes it has tracks for): its tracked nodes and the helpers folded into them. Every other node keeps
        /// the motion underneath.
        /// </summary>
        public static bool[] Drives(MotionTrack[] map, bool[] held)
        {
            var d = new bool[map.Length];
            for (int i = 0; i < d.Length; i++) d[i] = map[i] != null || held[i];
            return d;
        }

        /// <summary>
        /// Pose of the base motion at <paramref name="tBase"/> with the overlay at <paramref name="tOver"/> layered on top at
        /// full weight on the nodes <paramref name="overDrives"/> marks (local transforms; G3 overlay mixing).
        /// </summary>
        public static Xf[] SampleLayered(EmfxActor a, MotionTrack[] baseMap, bool[] baseHeld, float tBase,
                                         MotionTrack[] overMap, bool[] overHeld, bool[] overDrives, float tOver)
        {
            var w = new Xf[a.Nodes.Count];
            var done = new bool[w.Length];
            for (int i = 0; i < w.Length; i++)
                Resolve(a, i, w, done, n =>
                {
                    bool over = overDrives[n];
                    var k = over ? overMap[n] : baseMap[n];
                    float t = over ? tOver : tBase;
                    if (k != null) return (a.Nodes[n].Parent, k.SamplePosition(t), k.SampleRotation(t));
                    if (over ? overHeld[n] : baseHeld[n]) return (a.Nodes[n].Parent, new V3(0, 0, 0), Q4.Identity);
                    return (a.Nodes[n].Parent, a.Nodes[n].Position, a.Nodes[n].Rotation.Normalized);
                });
            return w;
        }

        public static Xf[] Sample(EmfxActor a, MotionTrack[] map, bool[] held, float t)
        {
            var w = new Xf[a.Nodes.Count];
            var done = new bool[w.Length];
            for (int i = 0; i < w.Length; i++)
                Resolve(a, i, w, done, n =>
                {
                    var k = map[n];
                    if (k != null) return (a.Nodes[n].Parent, k.SamplePosition(t), k.SampleRotation(t));
                    if (held[n]) return (a.Nodes[n].Parent, new V3(0, 0, 0), Q4.Identity);
                    return (a.Nodes[n].Parent, a.Nodes[n].Position, a.Nodes[n].Rotation.Normalized);
                });
            return w;
        }

        static Xf Resolve(EmfxActor a, int i, Xf[] w, bool[] done, Func<int, (int parent, V3 pos, Q4 rot)> local)
        {
            if (done[i]) return w[i];
            var (p, pos, rot) = local(i);
            Xf r;
            if (p < 0) r = new Xf { Pos = pos, Rot = rot };
            else
            {
                var pw = Resolve(a, p, w, done, local);
                r = new Xf { Pos = pw.Pos + pw.Rot.Rotate(pos), Rot = (pw.Rot * rot).Normalized };
            }
            w[i] = r; done[i] = true;
            return r;
        }

        /// <summary>Linear-blend skinning of one mesh vertex: Σ w · pose ∘ bind⁻¹ (p).</summary>
        public static V3 SkinVertex(ActorMesh m, int vertex, Xf[] bind, Xf[] pose)
        {
            var p = m.Positions[vertex];
            var inf = m.Skin?[m.OrgVertex[vertex]];
            if (inf == null || inf.Length == 0) inf = new[] { new SkinInfluence((ushort)m.Node, 1) };
            V3 acc = default; float sum = 0;
            foreach (var s in inf)
            {
                var b = bind[s.Node];
                var local = b.Rot.Conjugate.Rotate(p - b.Pos);
                var q = pose[s.Node];
                acc = acc + (q.Pos + q.Rot.Rotate(local)) * s.Weight;
                sum += s.Weight;
            }
            return sum > 1e-6f ? acc * (1 / sum) : p;
        }
    }
}
