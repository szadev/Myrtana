using System;
using System.Collections.Generic;

namespace Myrtana.Rules
{
    /// <summary>
    /// How a dead body comes to rest on the ground (fix-80). G3 keeps a corpse lying on the surface: a dead NPC gets
    /// <c>ForceGroundAlignment</c> and the lying ground pose (AniState 0x13) when it leaves and re-enters the processing
    /// range [OnExitProcessingRange, AIMode Dead → MovementMode 4, ForceGroundAlignment = true, ForceNextPose], a ragdoll
    /// lies where physics left it (ZS_RagDollDead_Loop: no velocity → collision group 13, the pose stays). Ours, once the
    /// body is still (the ragdoll frozen or the LieDead pose reached):
    /// <list type="bullet">
    /// <item>a <b>pose</b> corpse (the dying motion, LiePiercedDead, a corpse met again) is tilted onto the ground plane under
    /// it (least-squares fit of the ground samples, at most <see cref="MaxTiltDegrees"/>) and moved up or down until its
    /// lowest flesh (a low percentile of the skin, so a hand in a dip does not lift it) touches the ground — the authored
    /// LieDead pose has the back of the body 6–11 cm below the origin, so on flat ground it sank in, on a slope the
    /// head or the legs vanished in the hill;</item>
    /// <item>a <b>ragdoll</b> keeps its pose and is only lifted out when one of its collision parts (pelvis, chest, head,
    /// arms, legs) is in the ground, by the deepest part (fix-221, <see cref="RagdollLift"/>; not by the skin: uncollided feet
    /// in an uphill slope propped it up) — never lowered: it may lie on a rock or a step the samples miss.</item>
    /// </list>
    /// The body's origin then moves to the middle of the lying body (the focus name and the loot reach follow the corpse,
    /// not the spot it stood on).
    /// </summary>
    public static class CorpseRestRules
    {
        /// <summary>Steepest ground a pose corpse is tilted onto, degrees (G3's walkable slopes end near 45°) [interp].</summary>
        public const float MaxTiltDegrees = 35f;
        /// <summary>The skin rests this far above the sampled ground, m (no z-fighting with grass and decals).</summary>
        public const float Contact = 0.01f;
        /// <summary>A ragdoll's skin may dip this deep into the ground before it is lifted, m (cloth, collider radius).</summary>
        public const float RagdollTolerance = 0.03f;
        /// <summary>Share of the skin samples allowed under the ground (outliers: fingers, cloth tips, a dip in the grid).</summary>
        public const float LowPercentile = 0.02f;
        /// <summary>Largest correction, m: farther is a bad ground sample, not a body to move.</summary>
        public const float MaxLift = 2f, MaxDrop = 0.6f;

        /// <summary>
        /// Least-squares plane y = a·x + b·z + c through the ground samples; its upward unit normal. False with fewer than three
        /// samples or when they lie on one line.
        /// </summary>
        public static bool FitPlane(IReadOnlyList<(float x, float y, float z)> ground, out (float x, float y, float z) normal, out float a,
                                    out float b, out float c)
        {
            normal = (0f, 1f, 0f);
            a = b = c = 0f;
            int n = ground?.Count ?? 0;
            if (n < 3) return false;
            double mx = 0, my = 0, mz = 0;
            foreach (var p in ground) { mx += p.x; my += p.y; mz += p.z; }
            mx /= n; my /= n; mz /= n;
            double sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
            foreach (var p in ground)
            {
                double dx = p.x - mx, dz = p.z - mz, dy = p.y - my;
                sxx += dx * dx; sxz += dx * dz; szz += dz * dz; sxy += dx * dy; szy += dz * dy;
            }
            double det = sxx * szz - sxz * sxz;
            if (Math.Abs(det) < 1e-6) return false;
            a = (float)((sxy * szz - szy * sxz) / det);
            b = (float)((szy * sxx - sxy * sxz) / det);
            c = (float)(my - a * mx - b * mz);
            float len = (float)Math.Sqrt(a * a + 1f + b * b);
            normal = (-a / len, 1f / len, -b / len);
            return true;
        }

        /// <summary>The ground normal turned back towards up so that it leans at most <paramref name="maxDegrees"/>.</summary>
        public static (float x, float y, float z) LimitTilt((float x, float y, float z) normal, float maxDegrees = MaxTiltDegrees)
        {
            float len = (float)Math.Sqrt(normal.x * normal.x + normal.y * normal.y + normal.z * normal.z);
            if (len < 1e-6f) return (0f, 1f, 0f);
            var n = (x: normal.x / len, y: normal.y / len, z: normal.z / len);
            if (n.y < 0) n = (-n.x, -n.y, -n.z);
            float max = maxDegrees * (float)Math.PI / 180f;
            float tilt = (float)Math.Acos(Math.Min(1f, n.y));
            if (tilt <= max) return n;
            float h = (float)Math.Sqrt(n.x * n.x + n.z * n.z);
            if (h < 1e-6f) return (0f, 1f, 0f);
            float s = (float)Math.Sin(max) / h;
            return (n.x * s, (float)Math.Cos(max), n.z * s);
        }

        /// <summary>
        /// The vertical move that lets the body rest on the ground, from the heights of its skin samples above the ground
        /// under each (<paramref name="gaps"/>, m, negative = under the ground). A pose corpse is moved so its low
        /// percentile touches (up or down); a ragdoll only up, and only when it is deeper than the tolerance. 0 without samples.
        /// </summary>
        /// <summary>
        /// fix-202: whether the settling puts the body on its spot at once (<c>true</c>: a corpse shown again, a loaded game,
        /// the view streaming in, nobody saw it fall) or blends there (a fresh corpse, the ragdoll's lift out of the ground
        /// included: it jumped up to 0.4 m in one frame after lying still).
        /// </summary>
        public static bool SettlesAtOnce(bool restored) => restored;

        /// <summary>
        /// fix-221: how far a ragdoll's collision part (a sphere: a capsule's end, the head) is clear of the ground, m,
        /// measured along the ground normal (negative = in the ground): <paramref name="centreGap"/> is the sphere centre's
        /// height over the ground under it, <paramref name="normalY"/> the up component of the ground normal (cos of the slope).
        /// </summary>
        public static float PartClearance(float centreGap, float radius, float normalY) =>
            centreGap * Math.Max(0.1f, Math.Min(1f, normalY)) - radius;

        /// <summary>
        /// fix-221: the lift of a resting ragdoll, from its parts' clearances (<see cref="PartClearance"/>): only real
        /// penetration of the bodies physics laid down (the drawn relief above the collision, a part pushed in) — by the deepest
        /// part, turned into a vertical move on the slope; 0 when every part is within <see cref="RagdollTolerance"/>. The skin
        /// is not used: feet, toes, fingers and back slots have no collider and dip into an uphill slope, and lifting the body
        /// by them propped it 0.35-0.58 m over an 18-33° slope (pelvis 0.6-0.7 m up, fix-184/fix-202). Never lowers.
        /// </summary>
        public static float RagdollLift(IReadOnlyList<float> clearances, float normalY)
        {
            float worst = LowestClearance(clearances);
            if (float.IsNaN(worst) || worst >= -RagdollTolerance) return 0f;
            float lift = -worst / Math.Max(0.5f, Math.Min(1f, normalY));
            return Math.Min(MaxLift, lift);
        }

        /// <summary>fix-221: the lowest clearance (m) of the parts, NaN without any.</summary>
        public static float LowestClearance(IReadOnlyList<float> clearances)
        {
            float worst = float.NaN;
            if (clearances != null)
                foreach (float c in clearances) if (!float.IsNaN(c) && (float.IsNaN(worst) || c < worst)) worst = c;
            return worst;
        }

        public static float RestOffset(IReadOnlyList<float> gaps, bool ragdoll)
        {
            if (gaps == null || gaps.Count == 0) return 0f;
            float low = Percentile(gaps, LowPercentile);
            float offset;
            if (ragdoll) offset = low < -RagdollTolerance ? -low : 0f;
            else offset = Contact - low;
            return Math.Max(-MaxDrop, Math.Min(MaxLift, offset));
        }

        /// <summary>
        /// fix-166: a ground sample this far above the plane through the others is not ground under the body, m: the top of a
        /// barrel, crate or wall it lies against (b1019d fix163: Orc_Raider_Scout_02 next to G3_Object_Barrel_02 and a ruin
        /// wall in the Vengard yard — the rays that start above the body hit their tops 1.0-1.6 m over the dirt, the fitted
        /// plane leaned 17° and the body was shown 0.3-0.6 m in the ground after a load). A slope or a stair step (~0.3 m)
        /// stays.
        /// </summary>
        public const float RaisedOutlier = 0.5f;

        /// <summary>
        /// fix-166: drops (sets to NaN) the grid heights that stand more than <paramref name="maxAbove"/> above the plane
        /// fitted through the remaining ones, the highest first, one at a time (NaN = no ground; <see cref="GridHeight"/>
        /// falls back to the plane there). <paramref name="heights"/> is the survey grid, row by row along x. Returns how
        /// many were dropped; never leaves fewer than 3.
        /// </summary>
        public static int DropRaisedOutliers(float[] heights, int nx, int nz, float minX, float minZ, float step,
                                             float maxAbove = RaisedOutlier)
        {
            if (heights == null || nx <= 0 || nz <= 0) return 0;
            int dropped = 0;
            var kept = new List<(float x, float y, float z)>(heights.Length);
            while (true)
            {
                kept.Clear();
                for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++)
                {
                    float h = heights[iz * nx + ix];
                    if (!float.IsNaN(h)) kept.Add((minX + ix * step, h, minZ + iz * step));
                }
                if (kept.Count <= 3 || !FitPlane(kept, out _, out float a, out float b, out float c)) return dropped;
                int worst = -1;
                float worstAbove = maxAbove;
                for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++)
                {
                    float h = heights[iz * nx + ix];
                    if (float.IsNaN(h)) continue;
                    float above = h - (a * (minX + ix * step) + b * (minZ + iz * step) + c);
                    if (above > worstAbove)
                    {
                        worstAbove = above;
                        worst = iz * nx + ix;
                    }
                }
                if (worst < 0) return dropped;
                heights[worst] = float.NaN;
                dropped++;
            }
        }

        /// <summary>fix-232: ground steeper than this from the body's own floor is a wall beside it, not ground under it, degrees
        /// (G3's walkable slopes end near 45°) [interp].</summary>
        public const float WallSlopeDegrees = 45f;
        /// <summary>fix-232: rise allowed over the wall slope (a stone, a root, the grid's bilinear error), m.</summary>
        public const float WallMargin = 0.3f;

        /// <summary>
        /// fix-232: drops (NaN) the grid heights that rise from the body's floor (<paramref name="floorX"/>,
        /// <paramref name="floorY"/>, <paramref name="floorZ"/>: a pose corpse's origin, the spot it died standing on or the
        /// ground it was laid on) more steeply than <paramref name="maxSlopeDegrees"/> plus <paramref name="margin"/>: the face
        /// of a rock wall the body lies against. fix180 Nordmar: an ice wolf killed 0.2-0.7 m from a 10 m rock face had 4-8 of
        /// its 20 samples on the face, the plane through them leaned 68°, the body was tilted 35° and lifted 0.6 m, and the rest
        /// check (rays from higher up: more of the face) found it 0.76 m "in the ground" and lifted it again: pelvis 1.07 m over
        /// the slope. Never leaves fewer than 3 heights (then nothing is dropped). Returns how many were dropped.
        /// </summary>
        public static int DropWallSamples(float[] heights, int nx, int nz, float minX, float minZ, float step,
                                          float floorX, float floorY, float floorZ,
                                          float maxSlopeDegrees = WallSlopeDegrees, float margin = WallMargin)
        {
            if (heights == null || nx <= 0 || nz <= 0) return 0;
            float tan = (float)Math.Tan(maxSlopeDegrees * Math.PI / 180.0);
            int keep = 0, drop = 0;
            var wall = new bool[heights.Length];
            for (int iz = 0; iz < nz; iz++)
            for (int ix = 0; ix < nx; ix++)
            {
                int i = iz * nx + ix;
                float h = heights[i];
                if (float.IsNaN(h)) continue;
                float dx = minX + ix * step - floorX, dz = minZ + iz * step - floorZ;
                float d = (float)Math.Sqrt(dx * dx + dz * dz);
                if (h - floorY > margin + d * tan) { wall[i] = true; drop++; }
                else keep++;
            }
            if (drop == 0 || keep < 3) return 0;
            for (int i = 0; i < heights.Length; i++) if (wall[i]) heights[i] = float.NaN;
            return drop;
        }

        /// <summary>The value below which <paramref name="share"/> of the samples lie (nearest rank).</summary>
        public static float Percentile(IReadOnlyList<float> values, float share)
        {
            var sorted = new List<float>(values);
            sorted.Sort();
            int i = (int)Math.Floor(Math.Max(0f, Math.Min(1f, share)) * (sorted.Count - 1));
            return sorted[i];
        }

        /// <summary>
        /// Bilinear ground height inside a regular sample grid (<paramref name="heights"/> row-major [iz * nx + ix], NaN =
        /// no ground found there: the plane value is used). Outside the grid the border is extended.
        /// </summary>
        public static float GridHeight(float[] heights, int nx, int nz, float minX, float minZ, float step, float x, float z,
                                       float a, float b, float c)
        {
            float fx = step > 0 ? (x - minX) / step : 0f, fz = step > 0 ? (z - minZ) / step : 0f;
            fx = Math.Max(0f, Math.Min(nx - 1, fx));
            fz = Math.Max(0f, Math.Min(nz - 1, fz));
            int ix = Math.Min(nx - 2, (int)fx), iz = Math.Min(nz - 2, (int)fz);
            if (nx < 2) ix = 0;
            if (nz < 2) iz = 0;
            float tx = nx < 2 ? 0f : fx - ix, tz = nz < 2 ? 0f : fz - iz;
            float H(int gx, int gz)
            {
                gx = Math.Min(nx - 1, gx);
                gz = Math.Min(nz - 1, gz);
                float h = heights[gz * nx + gx];
                return float.IsNaN(h) ? a * (minX + gx * step) + b * (minZ + gz * step) + c : h;
            }
            float h0 = H(ix, iz) * (1 - tx) + H(ix + 1, iz) * tx;
            float h1 = H(ix, iz + 1) * (1 - tx) + H(ix + 1, iz + 1) * tx;
            return h0 * (1 - tz) + h1 * tz;
        }
    }
}
