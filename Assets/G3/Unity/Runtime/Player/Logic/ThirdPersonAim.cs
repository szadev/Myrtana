using System;
using System.Numerics;

namespace G3.Player
{
    /// <summary>
    /// Pure geometry of the third-person camera's screen centre (no engine types; tested in Tools/G3Tests). The camera of
    /// G3PlayerCamera hangs on a boom: pivot (above the feet) → <c>shoulder</c> to the right → <c>distance</c> back along
    /// the player's look Euler(pitch, yaw); it then looks at a point <c>lift</c> above the shoulder point (G3 TargetOffsetY,
    /// 0.5…1.2 m in CamModeRanged). So the screen centre is not the player's look: with the lift it points
    /// atan(lift / distance) higher (≈ 10° at 3 m), and with the shoulder offset it runs beside the hero, not through him.
    /// <see cref="Solve"/> gives the yaw and pitch that put the screen centre on a point, as a player moves the mouse until
    /// the crosshair sits on the target (fix-56: the dev runner set the look straight at the target and the crosshair sat
    /// metres above it).
    /// Unity conventions: y up, yaw about +y from +z towards +x, pitch positive looking down, angles in degrees.
    /// </summary>
    public static class ThirdPersonAim
    {
        const float Deg = (float)(Math.PI / 180.0);

        /// <summary>The player's look direction for Euler(pitch, yaw, 0).</summary>
        public static Vector3 Forward(float yaw, float pitch)
        {
            float y = yaw * Deg, p = pitch * Deg;
            return new Vector3((float)(Math.Sin(y) * Math.Cos(p)), (float)-Math.Sin(p), (float)(Math.Cos(y) * Math.Cos(p)));
        }

        /// <summary>The camera's right for this yaw (horizontal).</summary>
        public static Vector3 Right(float yaw)
        {
            float y = yaw * Deg;
            return new Vector3((float)Math.Cos(y), 0f, (float)-Math.Sin(y));
        }

        /// <summary>
        /// Camera position and screen-centre direction (unit) for a look (yaw, pitch): G3PlayerCamera.PlaceThirdPerson with
        /// the boom lengths it has now (<paramref name="shoulder"/>, <paramref name="distance"/>; the collision pull-in is
        /// already in them) and its look-at lift (0 = along the boom).
        /// </summary>
        public static void Pose(Vector3 pivot, float yaw, float pitch, float shoulder, float distance, float lift,
                                out Vector3 position, out Vector3 forward)
        {
            var f = Forward(yaw, pitch);
            var shoulderPoint = pivot + Right(yaw) * shoulder;
            position = shoulderPoint - f * distance;
            forward = f;
            if (lift <= 0f) return;
            var look = shoulderPoint + Vector3.UnitY * lift - position;
            if (look.LengthSquared() > 1e-8f) forward = Vector3.Normalize(look);
        }

        /// <summary>
        /// fix-253 (owner: "after the bow is drawn the aim kicks upward" on a small mouse-up): the screen-centre direction
        /// of the <b>unobstructed</b> boom — <see cref="Pose"/> with the framing distance (the wished one, eased, no
        /// collision pull-in) and its lift. A pure function of the look: one degree of mouse pitch turns it by about one
        /// degree whatever the obstacles do to the camera's position. Looking at the lift point from the collided end
        /// instead (<see cref="LookAtForward"/>) turns the view steeply up when the boom end, dropped towards the ground by
        /// a look up, is lifted off the drawn relief (the view stops following the mouse) and then pulled in (it leaps up).
        /// G3PlayerCamera uses this while a ranged weapon is drawn.
        /// </summary>
        public static Vector3 SteadyForward(float yaw, float pitch, float framingDistance, float lift)
        {
            Pose(Vector3.Zero, yaw, pitch, 0f, Math.Max(framingDistance, 0.01f), lift, out _, out var f);
            return f;
        }

        /// <summary>Elevation (deg, up positive) of a direction; 0 for a zero one.</summary>
        public static float Elevation(Vector3 dir)
        {
            float len = dir.Length();
            if (len < 1e-6f) return 0f;
            return (float)Math.Asin(Math.Max(-1f, Math.Min(1f, dir.Y / len))) / Deg;
        }

        /// <summary>
        /// The screen-centre direction from a camera at <paramref name="cameraAt"/> looking at the lift point above
        /// <paramref name="shoulderPoint"/> (G3PlayerCamera's look-at framing without the steady aim);
        /// <paramref name="boomForward"/> without a lift.
        /// </summary>
        public static Vector3 LookAtForward(Vector3 shoulderPoint, Vector3 cameraAt, float lift, Vector3 boomForward)
        {
            if (lift <= 0f) return boomForward;
            var look = shoulderPoint + Vector3.UnitY * lift - cameraAt;
            return look.LengthSquared() > 1e-8f ? Vector3.Normalize(look) : boomForward;
        }

        /// <summary>Ease-in / ease-out time of the steady aim weight (G3PlayerCamera.steadyAim), s.</summary>
        public const float SteadyEaseSeconds = 0.25f;

        /// <summary>
        /// The look (yaw, pitch) that puts the screen centre on <paramref name="target"/>, pitch within
        /// <paramref name="minPitch"/>…<paramref name="maxPitch"/>. False (best effort kept) when the target is inside the
        /// boom's reach or no pitch in range reaches it.
        /// </summary>
        public static bool Solve(Vector3 pivot, Vector3 target, float shoulder, float distance, float lift,
                                 float minPitch, float maxPitch, out float yaw, out float pitch)
        {
            var t = target - pivot;
            float r = (float)Math.Sqrt(t.X * t.X + t.Z * t.Z);
            float bearing = (float)Math.Atan2(t.X, t.Z) / Deg;
            yaw = bearing;
            pitch = 0f;
            if (r < 1e-3f) return false;
            // Plan view: the camera sits `shoulder` to the right of the pivot and looks along the yaw, so the target must lie
            // `shoulder` to the right of the line through the pivot: r·sin(bearing − yaw) = shoulder.
            bool ok = true;
            if (Math.Abs(shoulder) >= r) ok = false;
            else yaw = bearing - (float)Math.Asin(shoulder / r) / Deg;

            // Vertical plane along the yaw: h ahead of the pivot, v above it.
            var f0 = Forward(yaw, 0f);
            float h = f0.X * t.X + f0.Z * t.Z, v = t.Y;
            float Miss(float p)
            {
                float c = (float)Math.Cos(p * Deg), s = (float)Math.Sin(p * Deg);
                float camH = -distance * c, camV = distance * s;           // camera relative to the shoulder point
                float lookH = distance * c, lookV = lift > 0f ? lift - distance * s : -distance * s;
                if (lift <= 0f) { lookH = c; lookV = -s; }
                float toH = h - camH, toV = v - camV;
                return (float)(Math.Atan2(lookV, lookH) - Math.Atan2(toV, toH)); // > 0: the centre is above the target
            }

            // The centre goes down as the pitch grows: scan for the sign change, then bisect.
            const float step = 1f;
            float lo = minPitch, mLo = Miss(lo);
            float best = lo, bestMiss = Math.Abs(mLo);
            for (float p = minPitch + step; p <= maxPitch + 1e-3f; p += step)
            {
                float hi = Math.Min(p, maxPitch), mHi = Miss(hi);
                if (Math.Abs(mHi) < bestMiss) { best = hi; bestMiss = Math.Abs(mHi); }
                if (mLo > 0f && mHi <= 0f)
                {
                    for (int i = 0; i < 40; i++)
                    {
                        float mid = 0.5f * (lo + hi), mMid = Miss(mid);
                        if (mMid > 0f) lo = mid;
                        else hi = mid;
                    }
                    pitch = 0.5f * (lo + hi);
                    return ok;
                }
                lo = hi;
                mLo = mHi;
            }
            pitch = best;
            return false;
        }
    }
}
