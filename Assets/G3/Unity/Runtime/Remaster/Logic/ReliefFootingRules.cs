using System;
using System.Numerics;

namespace G3.Unity
{
    /// <summary>
    /// Engine-free rules of standing on the drawn relief (Docs/remaster-world.md section 14, <c>G3ReliefFooting</c>): the
    /// collision (G3's ground) carries the character, the model is moved onto the visual ground and the legs reach it.
    /// <list type="bullet">
    /// <item>Body: lowered to the lower foot's ground (the other leg bends, as in every engine's leg IK), never more than
    /// <see cref="MaxCrouch"/> below the ground at the root, never above it by more than <see cref="MaxLift"/>.</item>
    /// <item>Legs: analytic two-bone IK (law of cosines) of hip, knee and ankle towards the animated ankle raised by the ground
    /// under it minus the body offset; the knee stays on its side of the hip-target line (its bend plane).</item>
    /// </list>
    /// </summary>
    public static class ReliefFootingRules
    {
        /// <summary>The body goes at most this far below the ground at its root to let the lower foot reach its ground, m.</summary>
        public const float MaxCrouch = 0.3f;
        /// <summary>... and at most this far above it, m.</summary>
        public const float MaxLift = 0.05f;
        /// <summary>A foot raises at most this much by IK (a step onto a hummock), m.</summary>
        public const float MaxFootRaise = 0.45f;
        /// <summary>Largest visual offset of a model at all (a hollow or a swell), m.</summary>
        public const float MaxOffset = 1.5f;

        /// <summary>fix-181: deepest collision feet under the ground layer that the model is still lifted out of (m).</summary>
        public const float BuriedMax = 4f;
        /// <summary>fix-181: feet this far under the collision ground are inside it (the relief's on-terrain tolerance).</summary>
        public const float BuriedTolerance = 0.35f;

        /// <summary>
        /// fix-181: the body offset for feet at <paramref name="feetY"/> whose ground layer is <paramref name="colY"/> (collision)
        /// / <paramref name="visY"/> (drawn): feet inside the ground (more than <see cref="BuriedTolerance"/>, at most
        /// <see cref="BuriedMax"/> under it) lift the model onto the drawn ground; otherwise <paramref name="offset"/> as is.
        /// Never lowers a model.
        /// </summary>
        public static float BuriedLift(float feetY, float colY, float visY, float offset)
        {
            float depth = colY - feetY;
            if (depth <= BuriedTolerance || depth > BuriedMax) return offset;
            return Math.Max(offset, visY - feetY);
        }

        /// <summary>
        /// The body offset (m) for ground deltas (drawn ground minus the plane the animation stands on) at the root
        /// <paramref name="d0"/> and under the feet <paramref name="dLeft"/>, <paramref name="dRight"/> (NaN: no foot / no ground).
        /// </summary>
        public static float BodyOffset(float d0, float dLeft, float dRight)
        {
            float low = float.IsNaN(dLeft) ? dRight : float.IsNaN(dRight) ? dLeft : Math.Min(dLeft, dRight);
            if (float.IsNaN(low)) return Clamp(d0, -MaxOffset, MaxOffset);
            float o = Math.Max(d0 - MaxCrouch, Math.Min(d0 + MaxLift, low));
            return Clamp(o, -MaxOffset, MaxOffset);
        }

        /// <summary>How far a foot is raised by IK: its ground delta minus the body offset, limited.</summary>
        public static float FootRaise(float dFoot, float body) => float.IsNaN(dFoot) ? 0 : Clamp(dFoot - body, -0.2f, MaxFootRaise);

        // ---------------------------------------------------------------- fix-173: gait-aware footing
        // As in current engines' foot placement: the legs reach the ground only while the gait is slow (stand, walk) and only
        // the foot that is down; a running / sprinting / lunging body keeps the animation's legs and is only moved as a whole
        // (G3 itself, relief off, shows the run cycle unchanged on slopes: that is the reference).

        /// <summary>Full leg IK up to this horizontal speed (G3 walk 1.6 m/s), m/s.</summary>
        public const float IkFullSpeed = 1.9f;
        /// <summary>No leg IK from this speed on (G3 run 4, strafe run 3), m/s.</summary>
        public const float IkNoneSpeed = 3.0f;
        /// <summary>A foot within this height of its rest (lowest seen) is fully down, m.</summary>
        public const float ContactLow = 0.03f;
        /// <summary>... and from this height on it swings (no ground reach), m.</summary>
        public const float ContactHigh = 0.12f;

        /// <summary>Leg IK weight of a body moving at <paramref name="speed"/> m/s (1 standing / walking, 0 running).</summary>
        public static float LegWeight(float speed) => 1f - SmoothStep(IkFullSpeed, IkNoneSpeed, speed);

        /// <summary>How much a foot is down (1) or swinging (0): its animated height above the root plane against its rest height.</summary>
        public static float Contact(float height, float rest) => float.IsNaN(height) ? 0f : 1f - SmoothStep(ContactLow, ContactHigh, height - rest);

        /// <summary>
        /// The raise of a foot with contact <paramref name="contact"/> (0..1) whose animated sole is <paramref name="lift"/> m above
        /// its rest height: a planted foot reaches its ground (<see cref="FootRaise(float,float)"/>); a swinging foot keeps the
        /// animation and is only lifted out of the ground it would pass through (never higher, never lowered).
        /// </summary>
        public static float FootRaise(float dFoot, float body, float contact, float lift)
        {
            if (float.IsNaN(dFoot)) return 0;
            contact = Clamp(contact, 0, 1);
            float planted = FootRaise(dFoot, body);
            float swing = Clamp(dFoot - body - Math.Max(0, lift), 0, MaxFootRaise);
            return contact * planted + (1 - contact) * swing;
        }

        /// <summary>
        /// The body offset with the gait: <paramref name="weight"/> (<see cref="LegWeight"/>) blends between the root's own ground
        /// delta (no leg IK: the body follows the ground as a whole) and the lower planted foot's ground; a swinging foot
        /// (contact 0) does not pull the body down.
        /// </summary>
        public static float BodyOffset(float d0, float dLeft, float dRight, float contactLeft, float contactRight, float weight)
        {
            float root = Clamp(d0, -MaxOffset, MaxOffset);
            weight = Clamp(weight, 0, 1);
            if (weight <= 0) return root;
            float l = float.IsNaN(dLeft) ? float.NaN : d0 + (dLeft - d0) * Clamp(contactLeft, 0, 1);
            float r = float.IsNaN(dRight) ? float.NaN : d0 + (dRight - d0) * Clamp(contactRight, 0, 1);
            return root + (BodyOffset(d0, l, r) - root) * weight;
        }

        static float SmoothStep(float a, float b, float x)
        {
            float t = Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>Exponential approach of <paramref name="current"/> to <paramref name="target"/> with time constant <paramref name="seconds"/>.</summary>
        public static float Follow(float current, float target, float dt, float seconds)
        {
            if (float.IsNaN(current)) return target;
            if (seconds <= 0 || dt <= 0) return dt <= 0 ? current : target;
            return current + (target - current) * (1f - (float)Math.Exp(-dt / seconds));
        }

        /// <summary>
        /// Two-bone IK: hip <paramref name="a"/>, knee <paramref name="b"/>, ankle <paramref name="c"/> (world), target
        /// <paramref name="t"/>. Returns world-space rotation deltas: hip' = <paramref name="hip"/> * hip, then the knee's world
        /// rotation knee' = <paramref name="knee"/> * knee (its value before the hip changed). <paramref name="bendAxis"/>: used
        /// when the leg is straight (the character's right axis). False when the chain is degenerate (nothing to do).
        /// </summary>
        public static bool TwoBone(Vector3 a, Vector3 b, Vector3 c, Vector3 t, Vector3 bendAxis, out Quaternion hip, out Quaternion knee)
        {
            hip = knee = Quaternion.Identity;
            float lab = Vector3.Distance(a, b), lcb = Vector3.Distance(c, b);
            if (lab < 1e-4f || lcb < 1e-4f) return false;
            var toT = t - a;
            float lat = Clamp(toT.Length(), Math.Abs(lab - lcb) + 1e-3f, (lab + lcb) * 0.999f);
            var dir = Norm(toT);
            // the knee stays in its bend plane: the side of the hip-target line it is on now
            var side = (b - a) - dir * Vector3.Dot(b - a, dir);
            if (side.LengthSquared() < 1e-8f) side = Vector3.Cross(Norm(bendAxis), dir);
            side = Norm(side);
            float cosA = Clamp((lab * lab + lat * lat - lcb * lcb) / (2 * lab * lat), -1, 1);
            float sinA = (float)Math.Sqrt(Math.Max(0, 1 - cosA * cosA));
            var kneeAt = a + dir * (cosA * lab) + side * (sinA * lab);
            hip = FromTo(b - a, kneeAt - a);
            var c1 = kneeAt + Vector3.Transform(c - b, hip);
            var target = a + dir * lat;
            knee = Quaternion.Concatenate(hip, FromTo(c1 - kneeAt, target - kneeAt));   // hip first, then the knee turn
            return true;
        }

        /// <summary>The smallest rotation taking direction <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static Quaternion FromTo(Vector3 from, Vector3 to)
        {
            var f = Norm(from); var t = Norm(to);
            float d = Vector3.Dot(f, t);
            if (d > 0.999999f) return Quaternion.Identity;
            if (d < -0.999999f)
            {
                var ax = Vector3.Cross(Vector3.UnitX, f);
                if (ax.LengthSquared() < 1e-6f) ax = Vector3.Cross(Vector3.UnitY, f);
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), (float)Math.PI);
            }
            var axis = Vector3.Cross(f, t);
            var q = new Quaternion(axis.X, axis.Y, axis.Z, 1 + d);
            return Quaternion.Normalize(q);
        }

        static Vector3 Norm(Vector3 v) { float l = v.Length(); return l > 1e-8f ? v / l : Vector3.UnitY; }
        static float Acos(float x) => (float)Math.Acos(Clamp(x, -1f, 1f));
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
