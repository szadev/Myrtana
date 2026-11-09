using System;

namespace G3.Player
{
    /// <summary>
    /// fix-181 (owner, build 21a Nordmar: snow wolves "spinning in circles" while idle): watches one body's heading and
    /// tells when it keeps turning the same way on the spot — <see cref="SpinTurn"/> degrees in one direction while it
    /// walked less than <see cref="SpinPath"/> m — so the caller can log it and stop what turns it. A turn the other way,
    /// <see cref="WindowSeconds"/> without reaching the limit, or a walk farther than the limit starts the count afresh.
    /// A body circling a target in a fight walks metres per turn and never counts. Pure (tested in Tools/G3Tests).
    /// </summary>
    public sealed class SpinMonitor
    {
        /// <summary>Turn in one direction (deg) that counts as spinning: two full turns.</summary>
        public const float SpinTurn = 720f;
        /// <summary>Longest walk (m) during those turns that is still "on the spot".</summary>
        public const float SpinPath = 1.5f;
        /// <summary>The count starts afresh after this long (s).</summary>
        public const float WindowSeconds = 8f;
        /// <summary>A heading change smaller than this (deg) per step is not counted as a turn's direction change (noise).</summary>
        const float Noise = 0.5f;

        bool _has;
        float _yaw, _x, _z;
        float _turn, _path, _seconds;

        /// <summary>The turn (deg, signed) and the time (s) of the last detected spin.</summary>
        public float LastTurn { get; private set; }
        public float LastSeconds { get; private set; }

        public void Reset()
        {
            _has = false;
            _turn = _path = _seconds = 0f;
        }

        /// <summary>
        /// One step: the body's heading <paramref name="yawDeg"/> and position (x, z) after <paramref name="dt"/> s. True once
        /// when it has spun (the count then starts afresh).
        /// </summary>
        public bool Step(float yawDeg, float x, float z, float dt)
        {
            if (!_has)
            {
                _has = true;
                _yaw = yawDeg; _x = x; _z = z;
                return false;
            }
            float d = DeltaAngle(_yaw, yawDeg);
            float moved = (float)Math.Sqrt((x - _x) * (x - _x) + (z - _z) * (z - _z));
            _yaw = yawDeg; _x = x; _z = z;
            if (moved > 5f) { Restart(); return false; }   // teleported
            if (Math.Abs(d) > Noise && _turn != 0f && Math.Sign(d) != Math.Sign(_turn)) Restart();
            _turn += d;
            _path += moved;
            _seconds += Math.Max(0f, dt);
            if (_path > SpinPath || _seconds > WindowSeconds) { Restart(); return false; }
            if (Math.Abs(_turn) < SpinTurn) return false;
            LastTurn = _turn;
            LastSeconds = _seconds;
            Restart();
            return true;
        }

        void Restart() => _turn = _path = _seconds = 0f;

        /// <summary>Shortest signed difference b − a in degrees (−180, 180].</summary>
        public static float DeltaAngle(float a, float b)
        {
            float d = (b - a) % 360f;
            if (d > 180f) d -= 360f;
            else if (d <= -180f) d += 360f;
            return d;
        }
    }
}
