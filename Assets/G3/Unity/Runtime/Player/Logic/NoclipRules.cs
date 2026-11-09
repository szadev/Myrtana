using System;

namespace G3.Player
{
    /// <summary>
    /// fix-131 (owner: a noclip in the demo's dev console): pure rules of the hero's noclip (G3Player.SetNoclip; console
    /// <c>noclip</c> / <c>fly</c>). The hero himself flies through geometry (no collision, no gravity, no ground check,
    /// world catch or fall damage): W/S along the look (pitch included), A/D strafe, Space up, Ctrl down, Shift faster.
    /// Leaving it puts him on a floor below on the same side, never inside geometry [port: G3 has no noclip].
    /// </summary>
    public static class NoclipRules
    {
        /// <summary>Base noclip speed, m/s.</summary>
        public const float Speed = 10f;
        /// <summary>Shift multiplies the speed by this.</summary>
        public const float FastFactor = 5f;
        public const float MinSpeed = 1f, MaxSpeed = 200f;
        /// <summary>Leaving noclip looks for a floor this far below the feet, m.</summary>
        public const float LandProbe = 600f;
        /// <summary>At most this many surfaces below are tried (rock layers, a ceiling over a cave), each must leave room.</summary>
        public const int LandMaxHits = 8;

        /// <summary>The next state of a toggle: an explicit on/off wins, none flips.</summary>
        public static bool Next(bool current, bool? requested) => requested ?? !current;

        /// <summary>The console argument: on/1/true, off/0/false, empty = toggle (null); anything else is an error (null + false).</summary>
        public static bool TryParse(string arg, out bool? requested)
        {
            requested = null;
            if (string.IsNullOrWhiteSpace(arg) || arg.Equals("toggle", StringComparison.OrdinalIgnoreCase)) return true;
            switch (arg.Trim().ToLowerInvariant())
            {
                case "on": case "1": case "true": case "yes": requested = true; return true;
                case "off": case "0": case "false": case "no": requested = false; return true;
                default: return false;
            }
        }

        /// <summary>
        /// World velocity (x, y, z; m/s) for the keys: <paramref name="forward"/>/<paramref name="strafe"/> in -1..1 along the
        /// look (yaw, pitch in degrees, pitch down positive as Unity's x rotation) and its right, up/down keys straight up/down.
        /// </summary>
        public static (float x, float y, float z) Velocity(float forward, float strafe, bool up, bool down, bool fast,
            float yawDeg, float pitchDeg, float speed = Speed)
        {
            double yaw = yawDeg * Math.PI / 180.0, pitch = pitchDeg * Math.PI / 180.0;
            double cp = Math.Cos(pitch);
            // forward of Quaternion.Euler(pitch, yaw, 0): (sin yaw cos pitch, -sin pitch, cos yaw cos pitch); right: (cos yaw, 0, -sin yaw)
            double fx = Math.Sin(yaw) * cp, fy = -Math.Sin(pitch), fz = Math.Cos(yaw) * cp;
            double rx = Math.Cos(yaw), rz = -Math.Sin(yaw);
            double vx = fx * forward + rx * strafe, vy = fy * forward + (up ? 1 : 0) - (down ? 1 : 0), vz = fz * forward + rz * strafe;
            double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (len < 1e-6) return (0f, 0f, 0f);
            double s = Clamp(speed) * (fast ? FastFactor : 1f) / Math.Max(1.0, len);
            return ((float)(vx * s), (float)(vy * s), (float)(vz * s));
        }

        public static float Clamp(float speed) => speed < MinSpeed ? MinSpeed : speed > MaxSpeed ? MaxSpeed : speed;

        public enum Landing { NavFloorBelow, NearestFloor, FreeFloorBelow, LastSafe, Stay }

        /// <summary>
        /// Where leaving noclip puts the hero: the first free floor straight below with NavMesh (the walkable side of a
        /// cave floor or the land), else the nearest free floor round the feet (<see cref="GroundGuard.Best"/>: same level,
        /// then down), else the first free floor below without NavMesh (open land with no NavMesh), else his last valid
        /// standing place from before the noclip, else he stays (the world catch then takes a fall with no ground below).
        /// </summary>
        public static Landing Choose(bool navFloorBelow, bool nearestFloor, bool freeFloorBelow, bool lastSafe) =>
            navFloorBelow ? Landing.NavFloorBelow : nearestFloor ? Landing.NearestFloor : freeFloorBelow ? Landing.FreeFloorBelow
            : lastSafe ? Landing.LastSafe : Landing.Stay;
    }
}
