using System;
using System.Numerics;

namespace Myrtana.Rules
{
    /// <summary>
    /// fix-228 (owner shot: a hornet focused near the screen centre, the hero on a slope, the bow drawn but tilted far below
    /// it). The aim pose of a drawn bow or crossbow, engine-free:
    /// <list type="bullet">
    /// <item>What the bow points at: G3 OnGetAimAngle (PS_Ranged, Script 0x10083300) takes the direction "to the screen-centre
    /// point or the target centre". Ours: the focus's body centre (chest; a flying creature's body) when it sits under the
    /// crosshair (<see cref="FocusConeDegrees"/>) and nothing nearer is in the way, else the screen-centre pick point. A
    /// screen-centre ray that slipped past a small flyer used to hit the slope far behind and below it.</item>
    /// <item>How far the body pitches: the Up/Fwd/Down aim motions are steps (±25° split, G3CombatAnimator); the Down one
    /// points much lower than a target just below the split. The pose is corrected after the animation: the arrow line
    /// (draw hand → bow hand) is turned by the spine onto the line to the aim point, within ±<see cref="MaxCorrection"/>, the
    /// aim pitch limited to <see cref="MinPitch"/>..<see cref="MaxPitch"/> and eased (<see cref="Ease"/>).</item>
    /// </list>
    /// The arrow itself still leaves along the screen-centre ray (no snap, G3): the pose only shows the aim.
    /// </summary>
    public static class BowAimPose
    {
        /// <summary>The body pitches at most this far up / down (deg) whatever the camera looks at.</summary>
        public const float MaxPitch = 60f, MinPitch = -60f;
        /// <summary>The spine corrects the animated arrow line by at most this much (deg).</summary>
        public const float MaxCorrection = 40f;
        /// <summary>A focus within this angle (deg, at the camera) of the screen centre is what the bow points at.</summary>
        public const float FocusConeDegrees = 12f;
        /// <summary>Something under the crosshair this much (m) nearer than the focus centre blocks it (a wall, a tree).</summary>
        public const float BlockMargin = 1f;
        /// <summary>Easing rate (1/s) of the correction and of its weight (fade in on the held aim, out after it).</summary>
        public const float CorrectionRate = 12f, WeightRate = 8f;

        const float Rad2Deg = 180f / (float)Math.PI;

        /// <summary>Pitch (deg, up positive) of a direction; 0 for a zero one.</summary>
        public static float PitchOf(Vector3 dir)
        {
            float len = dir.Length();
            if (len < 1e-5f) return 0f;
            return (float)Math.Asin(Math.Clamp(dir.Y / len, -1f, 1f)) * Rad2Deg;
        }

        /// <summary>The pitch limited to the natural range of the aim.</summary>
        public static float ClampPitch(float pitch) => Math.Clamp(pitch, MinPitch, MaxPitch);

        /// <summary>
        /// What the bow points at: the focus centre when it is under the crosshair (within <see cref="FocusConeDegrees"/> of
        /// the camera ray) and the ray did not hit something more than <see cref="BlockMargin"/> nearer; else the pick point.
        /// </summary>
        /// <param name="camera">Camera position.</param>
        /// <param name="cameraForward">Camera forward (any length).</param>
        /// <param name="pick">The screen-centre pick point (ray hit, or far along the ray).</param>
        /// <param name="pickHit">Whether the ray hit something (false: <paramref name="pick"/> is just far away).</param>
        /// <param name="focus">The focus's body centre, null without a focus.</param>
        public static Vector3 AimPoint(Vector3 camera, Vector3 cameraForward, Vector3 pick, bool pickHit, Vector3? focus)
        {
            if (focus is not Vector3 f) return pick;
            var toFocus = f - camera;
            float focusDistance = toFocus.Length();
            float fwdLen = cameraForward.Length();
            if (focusDistance < 1e-3f || fwdLen < 1e-5f) return pick;
            float cos = Vector3.Dot(toFocus, cameraForward) / (focusDistance * fwdLen);
            if (cos < (float)Math.Cos(FocusConeDegrees / Rad2Deg)) return pick;
            if (pickHit && Vector3.Distance(camera, pick) < focusDistance - BlockMargin) return pick;
            return f;
        }

        /// <summary>The drawn ground at (x, z) for a point at height y: collision and drawn height. False = no relief there.</summary>
        public delegate bool Ground(float x, float y, float z, out float collisionY, out float visualY);

        /// <summary>Sample step (m) of <see cref="ReliefHit"/> near the camera; it grows with the distance (<see cref="StepGrowth"/>).</summary>
        public const float ReliefStep = 0.4f, StepGrowth = 0.02f;
        /// <summary>The relief is searched this far (m) along the crosshair ray.</summary>
        public const float ReliefReach = 150f;
        /// <summary>A point this far (m) below the collision ground is under it (overhang, cave roof): no relief there.</summary>
        public const float UnderCollision = 0.3f;

        static bool InsideRelief(Ground ground, Vector3 p)
        {
            if (!ground(p.X, p.Y, p.Z, out float col, out float vis) || float.IsNaN(col) || float.IsNaN(vis)) return false;
            return vis > col && p.Y < vis && p.Y >= col - UnderCollision;
        }

        /// <summary>
        /// fix-257 (owner shot in Varant: the camera low behind the hero in a dune hollow, the bow pointing steeply up): the
        /// crosshair ray passed through the drawn dunes (the relief has no colliders) and found nothing, so the aim point was
        /// far along the ray — the camera's steep look — instead of the dune or the foe in front. The first point along the
        /// ray (from <paramref name="start"/> to <paramref name="maxDistance"/>) where it enters the drawn ground; a ray that
        /// starts inside it (the camera's near plane) counts only once it has left it.
        /// </summary>
        public static bool ReliefHit(Ground ground, Vector3 origin, Vector3 dir, float start, float maxDistance, out float distance)
        {
            distance = maxDistance;
            float len = dir.Length();
            if (ground == null || len < 1e-6f || maxDistance <= start) return false;
            dir /= len;
            float t = Math.Max(0f, start), prev = t;
            bool outside = !InsideRelief(ground, origin + dir * t);
            while (t < maxDistance)
            {
                prev = t;
                t = Math.Min(maxDistance, t + ReliefStep + t * StepGrowth);
                bool inside = InsideRelief(ground, origin + dir * t);
                if (!inside) { outside = true; continue; }
                if (!outside) continue;
                float lo = prev, hi = t;          // lo outside, hi inside
                for (int i = 0; i < 10; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (InsideRelief(ground, origin + dir * mid)) hi = mid; else lo = mid;
                }
                distance = hi;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The point under the crosshair: the nearer of the physics hit and the drawn relief, else <paramref name="far"/> m
        /// along the ray (the sky: the aim follows the ray).
        /// </summary>
        public static Vector3 CrosshairPoint(Vector3 camera, Vector3 forward, float? physicsHit, float? reliefHit, float far,
                                             out bool hit)
        {
            float len = forward.Length();
            var f = len > 1e-6f ? forward / len : Vector3.UnitZ;
            float d = far;
            hit = false;
            if (physicsHit is float ph && ph < d) { d = ph; hit = true; }
            if (reliefHit is float rh && rh < d) { d = rh; hit = true; }
            return camera + f * d;
        }

        /// <summary>The pitch the arrow should have from <paramref name="from"/> (the bow) to the aim point, limited.</summary>
        public static float TargetPitch(Vector3 from, Vector3 aimPoint) => ClampPitch(PitchOf(aimPoint - from));

        /// <summary>
        /// How far (deg, up positive) the spine turns the animated arrow line (pitch <paramref name="arrowPitch"/>) onto the
        /// target pitch: the difference, limited to ±<see cref="MaxCorrection"/>.
        /// </summary>
        public static float Correction(float targetPitch, float arrowPitch) =>
            Math.Clamp(ClampPitch(targetPitch) - arrowPitch, -MaxCorrection, MaxCorrection);

        /// <summary>Exponential approach of <paramref name="current"/> to <paramref name="target"/> (frame-rate independent).</summary>
        public static float Ease(float current, float target, float rate, float dt)
        {
            if (dt <= 0f) return current;
            float k = 1f - (float)Math.Exp(-rate * dt);
            return current + (target - current) * k;
        }
    }
}
