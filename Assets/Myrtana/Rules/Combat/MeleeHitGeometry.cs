using System;

namespace Myrtana.Rules
{
    /// <summary>A point in a fighter's frame (metres; y up from the feet, z forward).</summary>
    public readonly struct HitPoint
    {
        public readonly float X, Y, Z;
        public HitPoint(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static HitPoint Lerp(HitPoint a, HitPoint b, float t) => new HitPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        public float DistanceTo(HitPoint o) => (float)Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
        public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
    }

    /// <summary>
    /// A fighter's weapon trigger in its own frame: an upright box (animals, goblins) or an upright capsule (humans) from
    /// <see cref="Bottom"/> to <see cref="Top"/> above the feet, centred on x = 0, z = <see cref="CenterZ"/>.
    /// </summary>
    public readonly struct WeaponTrigger
    {
        public readonly bool IsBox;
        /// <summary>Box half extents across (x) and along (z) the body; a capsule's radius is <see cref="HalfX"/>.</summary>
        public readonly float HalfX, HalfZ;
        public readonly float Bottom, Top, CenterZ;

        public WeaponTrigger(bool box, float halfX, float halfZ, float bottom, float top, float centerZ)
        {
            IsBox = box; HalfX = halfX; HalfZ = box ? halfZ : halfX; Bottom = bottom; Top = top; CenterZ = centerZ;
        }

        public float Radius => HalfX;
        public float Height => Top - Bottom;
        public float CenterY => (Top + Bottom) * 0.5f;

        /// <summary>Distance from a point to the shape (0 inside).</summary>
        public float Distance(HitPoint p)
        {
            if (IsBox)
            {
                float dx = Math.Max(Math.Abs(p.X) - HalfX, 0f);
                float dz = Math.Max(Math.Abs(p.Z - CenterZ) - HalfZ, 0f);
                float dy = p.Y < Bottom ? Bottom - p.Y : p.Y > Top ? p.Y - Top : 0f;
                return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            float lo = Math.Min(Bottom + HalfX, CenterY), hi = Math.Max(Top - HalfX, CenterY);
            float y = p.Y < lo ? lo : p.Y > hi ? hi : p.Y;
            float d = new HitPoint(0, y, CenterZ).DistanceTo(p) - HalfX;
            return Math.Max(d, 0f);
        }

        public override string ToString() =>
            (IsBox ? $"box +-{HalfX:0.00} x +-{HalfZ:0.00}" : $"capsule r {HalfX:0.00}") + $" y {Bottom:0.00}..{Top:0.00} z {CenterZ:0.00}";
    }

    /// <summary>
    /// Melee hit geometry (Docs/combat.md §9.8).
    /// <para>
    /// G3 [OnTouchDamage, Script_Game 0x100d7ac0]: a melee touch deals damage only when the touched shape of the victim
    /// is in shape group 7 (WeaponTrigger); the group-6 movement capsule (the character controller) only counts for
    /// projectiles. Every NPC template carries such a trigger, and it is far taller than the creature (template
    /// eCCollisionShape_PS, cm, `G3Cli items tpl &lt;name&gt;`): goblin box y −102..220, x ±43, z −29..32; meatbug box
    /// y −127..200, ±25; wolf box y −42..188, ±38, z −142..115; boar box y −69..192, ±33, z −81..143; scavenger box
    /// y −54..208, ±48; snapper y −91..250; humans and the hero capsule r 66–71, y −52..242; skeleton capsule r 65,
    /// y −33..216. So a swing at the hero's chest height reaches a meatbug, a goblin 0.7 m downhill or a wolf 1 m below;
    /// there is no aiming or bending of the swing toward low targets.
    /// </para>
    /// <para>
    /// We fit the trigger from the skeleton (no template data at runtime) [interp: fit of the table above]: from
    /// <see cref="Below"/> under the feet to max(<see cref="MinTop"/>, top + <see cref="AboveHead"/>); long bodies get a box
    /// over their footprint, upright ones a capsule of <see cref="UprightRadiusPerHeight"/> × their height.
    /// </para>
    /// </summary>
    public static class MeleeHitGeometry
    {
        public const float Below = 0.5f, MinTop = 2.0f, AboveHead = 0.5f, UprightRadiusPerHeight = 0.33f;
        /// <summary>Smallest half width of a trigger box, m (G3 meatbug ±25 cm).</summary>
        public const float MinHalf = 0.25f;
        /// <summary>Most capsule sub-steps of one frame's sweep.</summary>
        public const int MaxSteps = 32;

        /// <summary>
        /// The weapon trigger for joint extents (<paramref name="width"/> x, <paramref name="height"/> = top above the feet,
        /// <paramref name="length"/> z, <paramref name="centerZ"/> = middle along z), flesh included.
        /// </summary>
        public static WeaponTrigger Trigger(float width, float height, float length, float centerZ)
        {
            height = Clamp(height, 0.2f, 8f);
            float top = Math.Max(MinTop, height + AboveHead);
            if (CombatBodyShape.IsLong(height, length))
                return new WeaponTrigger(true, Math.Max(width * 0.6f, MinHalf), Math.Max(length * 0.6f, MinHalf), -Below, top, centerZ);
            float r = Clamp(height * UprightRadiusPerHeight, 0.3f, 2f);
            return new WeaponTrigger(false, r, r, -Below, Math.Max(top, -Below + 2 * r), 0f);
        }

        /// <summary>Sub-steps of a sweep that moved the weapon <paramref name="travel"/> m: consecutive capsules overlap (no tunnelling).</summary>
        public static int Steps(float travel, float radius)
        {
            float step = Math.Max(radius, 0.03f);
            int n = (int)Math.Ceiling(travel / step);
            return n < 1 ? 1 : n > MaxSteps ? MaxSteps : n;
        }

        /// <summary>Gap between a weapon capsule (segment a–b, <paramref name="radius"/>) and the trigger, m (≤ 0: touching).</summary>
        public static float Gap(WeaponTrigger trigger, HitPoint a, HitPoint b, float radius)
        {
            // The distance from a convex shape along a segment is convex in t: ternary search.
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 48; i++)
            {
                float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
                if (trigger.Distance(HitPoint.Lerp(a, b, m1)) <= trigger.Distance(HitPoint.Lerp(a, b, m2))) hi = m2;
                else lo = m1;
            }
            float best = Math.Min(trigger.Distance(HitPoint.Lerp(a, b, (lo + hi) * 0.5f)), Math.Min(trigger.Distance(a), trigger.Distance(b)));
            return best - radius;
        }

        /// <summary>
        /// One frame of a swing: the weapon capsule moved from (prevA, prevB) to (a, b). It is swept in <see cref="Steps"/>
        /// interpolated poses, exactly as <c>MeleeSwing</c> overlaps them, and touches when any pose touches.
        /// </summary>
        public static bool SweepTouches(WeaponTrigger trigger, HitPoint prevA, HitPoint prevB, HitPoint a, HitPoint b, float radius)
        {
            float travel = Math.Max(prevA.DistanceTo(a), prevB.DistanceTo(b));
            int steps = Steps(travel, radius);
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                if (Gap(trigger, HitPoint.Lerp(prevA, a, t), HitPoint.Lerp(prevB, b, t), radius) <= 0f) return true;
            }
            return false;
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
