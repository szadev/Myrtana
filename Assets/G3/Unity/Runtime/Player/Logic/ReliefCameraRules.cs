using System;
using System.Numerics;

namespace G3.Player
{
    /// <summary>
    /// fix-240: the third-person camera against the drawn terrain relief (Docs/remaster-world.md sections 13-14). The relief
    /// is render-only: G3's MeshCollider stays under it, so the camera's sphere casts and the wall push (fix-94) keep the
    /// camera off the <b>collision</b> ground, which can lie up to ~1 m (swells) and more (steep strata) below what is drawn;
    /// close to raised ground the camera ended inside the relief (the owner's build 25d: the screen filled by stretched ground
    /// from inside). These rules query the drawn height field (the relief's ground maps, <c>G3TerrainRelief.TryGround</c>)
    /// on the CPU:
    /// <list type="bullet">
    /// <item>the camera end is lifted to <see cref="Clearance"/> above the drawn ground (along its normal; G3's own rule against
    /// a slope is to ride up it, fix-94), at most <see cref="MaxLift"/>;</item>
    /// <item>where the line from the pivot to the (lifted) end runs through drawn ground (a swell between the hero and the
    /// camera, or a lift too large), the camera is pulled in in front of it, as a physics hit would;</item>
    /// <item>lifts come in at once and go out eased, pull-ins likewise (<see cref="Follow"/>).</item>
    /// </list>
    /// Only where the drawn ground is above the collision ground and the point is not under it (an overhang, a cave roof):
    /// everywhere else the physics casts already decide. Engine-free (G3Tests: ReliefCameraRulesTests).
    /// </summary>
    public static class ReliefCameraRules
    {
        /// <summary>The drawn ground at (x, z) for a point at height y: collision and visual height. False = no relief there.</summary>
        public delegate bool Ground(float x, float y, float z, out float collisionY, out float visualY);

        /// <summary>Distance kept off the drawn ground along its normal, m: G3's 17.5 cm wall distance plus the near plane's
        /// corner (0.05 m near plane at 60-75 deg: ~0.09 m) and the ground map's bilinear error (a few cm).</summary>
        public const float Clearance = 0.25f;
        /// <summary>Largest lift of the camera end, m; above this the camera is pulled in instead.</summary>
        public const float MaxLift = 2.5f;
        /// <summary>Sample step along the line of sight, m.</summary>
        public const float Step = 0.2f;
        /// <summary>The first metres of the line from the pivot (the hero's head) are not tested, m.</summary>
        public const float SkipNear = 0.3f;
        /// <summary>A pulled-in camera stays this far in front of where the line enters the drawn ground, m.</summary>
        public const float PullMargin = 0.25f;
        /// <summary>A point this far below the collision ground is under it (overhang, cave roof): the relief there is ignored, m.</summary>
        public const float UnderCollision = 0.3f;
        /// <summary>Half the finite-difference baseline of the slope estimate, m.</summary>
        public const float SlopeProbe = 0.35f;
        /// <summary>Normal.y floor of the slope estimate (the vertical clearance is at most Clearance / this).</summary>
        public const float MinNormalY = 0.35f;
        /// <summary>Ease-out time of a lift or a pull-in once the relief no longer asks for it, s.</summary>
        public const float EaseSeconds = 0.2f;

        /// <summary>Per-camera state of <see cref="Follow"/>.</summary>
        public struct State
        {
            public float Lift;
            public float Reach;
            public bool Started;
        }

        /// <summary>
        /// The lowest camera height at <paramref name="p"/>'s XZ the drawn relief allows (its height plus
        /// <paramref name="clearance"/> along the local normal). False: no relief there, relief not above the collision, or
        /// <paramref name="p"/> under the collision ground.
        /// </summary>
        public static bool Floor(Ground ground, Vector3 p, float clearance, out float floorY)
        {
            floorY = float.NegativeInfinity;
            if (!Drawn(ground, p, out float vis)) return false;
            float gx = Gradient(ground, p, vis, 1, 0), gz = Gradient(ground, p, vis, 0, 1);
            float ny = Math.Max(MinNormalY, 1f / (float)Math.Sqrt(1f + gx * gx + gz * gz));
            floorY = vis + clearance / ny;
            return true;
        }

        /// <summary>The drawn height under <paramref name="p"/> when it counts for the camera (see <see cref="Floor"/>).</summary>
        static bool Drawn(Ground ground, Vector3 p, out float vis)
        {
            vis = 0f;
            if (ground == null || !ground(p.X, p.Y, p.Z, out float col, out vis)) return false;
            if (float.IsNaN(col) || float.IsNaN(vis)) return false;
            if (p.Y < col - UnderCollision) return false;   // under the collision ground: the physics casts' domain
            return vis > col;                              // drawn ground at or below the collision: the casts keep the camera off it
        }

        static float Gradient(Ground ground, Vector3 p, float vis, int dx, int dz)
        {
            var a = new Vector3(p.X + dx * SlopeProbe, p.Y, p.Z + dz * SlopeProbe);
            var b = new Vector3(p.X - dx * SlopeProbe, p.Y, p.Z - dz * SlopeProbe);
            bool ha = ground(a.X, a.Y, a.Z, out _, out float va) && !float.IsNaN(va);
            bool hb = ground(b.X, b.Y, b.Z, out _, out float vb) && !float.IsNaN(vb);
            if (ha && hb) return (va - vb) / (2 * SlopeProbe);
            if (ha) return (va - vis) / SlopeProbe;
            if (hb) return (vis - vb) / SlopeProbe;
            return 0f;
        }

        /// <summary>
        /// The share (0..1) of the line <paramref name="from"/> → <paramref name="to"/> in front of the first point where it
        /// runs into the drawn ground (less <see cref="PullMargin"/>); 1 when it stays out.
        /// </summary>
        public static float Reach(Ground ground, Vector3 from, Vector3 to)
        {
            var d = to - from;
            float len = d.Length();
            if (len <= SkipNear) return 1f;
            var dir = d / len;
            for (float t = SkipNear; ; t += Step)
            {
                bool last = t >= len;
                if (last) t = len;
                var q = from + dir * t;
                if (Drawn(ground, q, out float vis) && q.Y < vis)
                    return Math.Max(0f, t - PullMargin) / len;
                if (last) return 1f;
            }
        }

        /// <summary>
        /// The camera end for one frame: <paramref name="at"/> (after the physics casts) lifted off and pulled in front of
        /// the drawn relief seen from <paramref name="from"/> (the boom's shoulder point). Lift and pull-in follow
        /// <see cref="State"/>: in at once, out over <see cref="EaseSeconds"/>; the end is never left below the floor.
        /// </summary>
        public static Vector3 Follow(Ground ground, Vector3 from, Vector3 at, ref State s, float dt)
        {
            if (!s.Started) { s.Started = true; s.Lift = 0f; s.Reach = 1f; }
            float lift = 0f;
            if (Floor(ground, at, Clearance, out float floor) && floor > at.Y)
                lift = floor - at.Y;
            float reach;
            if (lift > MaxLift)
            {
                lift = 0f;
                reach = Reach(ground, from, at);   // too deep to ride up: in front of where the line enters the ground
            }
            else reach = Reach(ground, from, at + Vector3.UnitY * lift);

            float k = dt > 0f ? (float)Math.Exp(-dt / EaseSeconds) : 1f;
            s.Lift = lift >= s.Lift ? lift : lift + (s.Lift - lift) * k;
            s.Reach = reach <= s.Reach ? reach : reach + (s.Reach - reach) * k;

            var lifted = s.Lift > 0f ? at + Vector3.UnitY * s.Lift : at;
            var end = s.Reach >= 1f ? lifted : from + (lifted - from) * s.Reach;
            if (Floor(ground, end, Clearance, out float f) && f > end.Y && f - end.Y <= MaxLift) end.Y = f;
            return end;
        }

        /// <summary>Forget the eased lift and pull-in (a cut: teleport, load, mode switch).</summary>
        public static void Reset(ref State s) => s = default;
    }
}
