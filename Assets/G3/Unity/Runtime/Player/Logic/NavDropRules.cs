using System;
using System.Collections.Generic;

namespace G3.Player
{
    /// <summary>
    /// Pure rules of the NPC drop-down links (no engine types; tested in Tools/G3Tests). Gothic 3 characters have no
    /// climb: a floor within <see cref="SlopeRules.StepHeight"/> above the feet is stepped onto, anything higher is a wall
    /// (Docs/hero.md "Slopes, steps, sliding"); but with no floor within StepHeight below the feet the body simply falls
    /// (movement mode "fall") and lands. So a 2–4 m terrace step is one-way: NPCs walk off it downwards and never up. Our
    /// NavMesh (agent climb = StepHeight, max slope = SteepGroundAngleMax) has no way down such a step, which made the
    /// terraces NavMesh islands (editor-runner-20: Xardas' plateau, Geldern_Slave_01, Harek in Kap Dun froze). One-way
    /// off-mesh links over the edge model the G3 fall; <see cref="MaxDrop"/> keeps them to heights an NPC route takes.
    /// </summary>
    public static class NavDropRules
    {
        /// <summary>Highest drop a walker takes over a link (m).</summary>
        public const float MaxDrop = 4f;
        /// <summary>A drop must be higher than the step height (lower ones are on the NavMesh) plus this margin (m).</summary>
        public const float MinDropMargin = 0.15f;
        /// <summary>Distances past the NavMesh edge probed for the landing (m): the face below may lean out a little.</summary>
        public static readonly float[] LandingProbes = { 0.6f, 1.1f, 1.7f, 2.5f };
        /// <summary>Directions probed around the way to the target (degrees off it).</summary>
        public static readonly float[] FanDegrees = { 0f, -25f, 25f, -50f, 50f, -80f, 80f };

        /// <summary>Whether a fall from <paramref name="edgeY"/> onto a floor at <paramref name="landingY"/> is a drop link.</summary>
        public static bool IsDrop(float edgeY, float landingY, float stepHeight = SlopeRules.StepHeight)
        {
            float drop = edgeY - landingY;
            return drop > stepHeight + MinDropMargin && drop <= MaxDrop;
        }

        /// <summary>
        /// Height of a body crossing a drop link at progress <paramref name="t"/> (0 = edge, 1 = landing; horizontal):
        /// it keeps the edge height over the first third (still on the ledge), then falls with gravity's square curve.
        /// Never below the landing, never above the edge.
        /// </summary>
        public static float FallY(float edgeY, float landingY, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            const float Ledge = 0.33f;
            if (t <= Ledge) return edgeY;
            float u = (t - Ledge) / (1f - Ledge);
            return edgeY + (landingY - edgeY) * u * u;
        }

        /// <summary>
        /// Whether a landing improves the way: it reaches the target (complete), or it ends closer to it than the path
        /// without the link by at least <paramref name="minGain"/> m.
        /// </summary>
        public static bool Improves(bool landingComplete, float landingLeft, float currentLeft, float minGain = 2f) =>
            landingComplete || landingLeft < currentLeft - minGain;
    }

    /// <summary>
    /// Connected groups ("islands") of points, from pairwise "a path joins them" answers (union–find). Used by the
    /// NavMesh island check (<c>navislands</c>): routine points and guide targets of one cell should form one island.
    /// </summary>
    public sealed class Islands
    {
        readonly int[] _parent;

        public Islands(int count)
        {
            _parent = new int[count];
            for (int i = 0; i < count; i++) _parent[i] = i;
        }

        public int Count => _parent.Length;

        public int Find(int i)
        {
            while (_parent[i] != i) i = _parent[i] = _parent[_parent[i]];
            return i;
        }

        public void Join(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) _parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        /// <summary>Members per island, largest first.</summary>
        public List<List<int>> Groups()
        {
            var map = new Dictionary<int, List<int>>();
            for (int i = 0; i < _parent.Length; i++)
            {
                int r = Find(i);
                if (!map.TryGetValue(r, out var list)) map[r] = list = new List<int>();
                list.Add(i);
            }
            var groups = new List<List<int>>(map.Values);
            groups.Sort((x, y) => y.Count != x.Count ? y.Count.CompareTo(x.Count) : x[0].CompareTo(y[0]));
            return groups;
        }
    }
}
