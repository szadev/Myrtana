using System.Collections.Generic;
using UnityEngine;

namespace G3.Unity.Characters
{
    /// <summary>
    /// Builds the G3 locomotion motion names of one stance (<c>…_Move_{Stand|Turn90|Walk|Run|Sprint}_N_{Fwd|Back|Left|Right}_00</c>)
    /// and turns a local velocity + turn rate into blend weights for <see cref="G3MotionPlayer.PlayBlend"/>.
    /// The gait speeds are the root speeds encoded in the motion names (Hero: walk 1.6, run 4.0 fwd/back and 3.0 sideways,
    /// sprint 6.0 m/s), so the clip whose speed matches the actual velocity gets the weight. Rig/stance/items are
    /// parameters, so NPC rigs (Wolf, Orc, …) and other stances (Sneak, Wade, weapon drawn) reuse it.
    /// </summary>
    public sealed class G3LocomotionSelector
    {
        static readonly string[] Dirs = { "Fwd", "Back", "Left", "Right" };
        static readonly string[] Gaits = { "Walk", "Run", "Sprint" };

        public readonly G3MotionKey Base;
        /// <summary>Motion keys of the set in weight order: Stand, Turn90 Left, Turn90 Right, then per direction the gaits.</summary>
        public readonly List<string> Keys = new List<string>();
        /// <summary>Name of the set for <see cref="G3MotionPlayer.PlayBlend"/>.</summary>
        public string SetKey => _setKey ??= $"{Base.Rig}_{Base.Stance}_{Base.LeftItem}_{Base.RightItem}_{Base.Pose}_Move";
        string _setKey; // Base is fixed: built once instead of per call (perf-7)
        /// <summary>Playback rate for the last evaluation (above the fastest clip the cycle is sped up, at most 1.5×).</summary>
        public float PlaybackRate { get; private set; } = 1;
        /// <summary>Turn rate of the Turn90 clips, degrees per second (90° per clip length).</summary>
        public float TurnClipRate { get; } = 112.5f;

        readonly float[] _weights;
        readonly bool _hasTurn; // the Swim stance has no Turn90 clips: turning keeps the stand clip
        readonly List<(int index, float speed)>[] _gaits = new List<(int, float)>[4]; // per direction, ascending speed
        const int Stand = 0, TurnLeft = 1, TurnRight = 2;

        /// <param name="fallbackPose">fix-58: a pose whose Move motions fill the gaits the base pose lacks (the shield
        /// guard P1 has no Sprint: the P0 Sprint motions are used, <see cref="G3.Player.StancePose.MoveFallbackPose"/>); null = none.</param>
        public G3LocomotionSelector(G3MotionLibrary library, G3MotionKey baseKey, string fallbackPose = null)
        {
            Base = baseKey;
            Keys.Add(baseKey.With("Move", "Stand").ToString());
            Keys.Add(baseKey.With("Move", "Turn90", "Left").ToString());
            Keys.Add(baseKey.With("Move", "Turn90", "Right").ToString());
            var turn = library != null ? library.Find(Keys[TurnLeft]) : null;
            if (turn != null) TurnClipRate = 90f / turn.Length;
            _hasTurn = turn != null && library.Find(Keys[TurnRight]) != null;
            for (int d = 0; d < Dirs.Length; d++)
            {
                _gaits[d] = new List<(int, float)>();
                foreach (var g in Gaits)
                {
                    string k = baseKey.With("Move", g, Dirs[d]).ToString();
                    var e = library != null ? library.Find(k) : null;
                    if ((e == null || e.speed <= 0) && fallbackPose != null && library != null)
                    {
                        var fb = baseKey.With("Move", g, Dirs[d]);
                        fb.Pose = fallbackPose;
                        string fk = fb.ToString();
                        var fe = library.Find(fk);
                        if (fe != null && fe.speed > 0) { e = fe; k = fk; _fallbackKeys[(g, Dirs[d])] = fk; }
                    }
                    if (e == null || e.speed <= 0) continue;
                    _gaits[d].Add((Keys.Count, e.speed));
                    Keys.Add(k);
                }
                _gaits[d].Sort((a, b) => a.speed.CompareTo(b.speed));
            }
            _weights = new float[Keys.Count];
        }

        /// <summary>Root speed of a gait in a direction ("Run", "Left"), 0 when the clip is missing.</summary>
        public float Speed(string gait, string dir)
        {
            // Asked every frame per walking NPC (G3NpcAnimator.WalkSpeed/GaitSpeed); the key string is built once per
            // (gait, dir) and the answer cached, Base never changes (perf-7).
            if (_speeds.TryGetValue((gait, dir), out float cached)) return cached;
            float speed = 0;
            int d = System.Array.IndexOf(Dirs, dir);
            if (d >= 0)
            {
                string k = _fallbackKeys.TryGetValue((gait, dir), out var fk) ? fk : Base.With("Move", gait, dir).ToString();
                foreach (var (i, s) in _gaits[d]) if (Keys[i] == k) { speed = s; break; }
            }
            if (gait != null && dir != null) _speeds[(gait, dir)] = speed;
            return speed;
        }

        readonly Dictionary<(string gait, string dir), string> _fallbackKeys = new Dictionary<(string, string), string>();
        readonly Dictionary<(string gait, string dir), float> _speeds = new Dictionary<(string, string), float>();

        /// <param name="local">velocity in the character frame, m/s (x = right, y = forward)</param>
        /// <param name="turnRate">yaw rate, degrees per second (positive = clockwise from above = right)</param>
        public IReadOnlyList<float> Evaluate(Vector2 local, float turnRate)
        {
            System.Array.Clear(_weights, 0, _weights.Length);
            PlaybackRate = 1;
            float speed = local.magnitude;
            float standShare = 1;
            if (speed > 0.05f)
            {
                float ax = Mathf.Abs(local.x), az = Mathf.Abs(local.y), sum = ax + az;
                standShare = 0;
                float rate = 0;
                standShare += Direction(local.y >= 0 ? 0 : 1, az / sum, speed, ref rate);
                standShare += Direction(local.x < 0 ? 2 : 3, ax / sum, speed, ref rate);
                PlaybackRate = Mathf.Max(1, rate);
            }
            // The standing share turns in place when the character rotates.
            float t = _hasTurn ? Mathf.Clamp(turnRate / TurnClipRate, -1, 1) : 0;
            _weights[Stand] += standShare * (1 - Mathf.Abs(t));
            _weights[TurnLeft] += standShare * Mathf.Max(0, -t);
            _weights[TurnRight] += standShare * Mathf.Max(0, t);
            return _weights;
        }

        /// <summary>Distributes <paramref name="w"/> over the gaits of a direction; returns the share left for standing.</summary>
        float Direction(int d, float w, float speed, ref float rate)
        {
            if (w <= 0) return 0;
            var g = _gaits[d];
            if (g.Count == 0) return w;
            if (speed <= g[0].speed)
            {
                float k = speed / g[0].speed;
                _weights[g[0].index] += w * k;
                return w * (1 - k);
            }
            for (int i = 1; i < g.Count; i++)
            {
                if (speed > g[i].speed) continue;
                float k = (speed - g[i - 1].speed) / (g[i].speed - g[i - 1].speed);
                _weights[g[i - 1].index] += w * (1 - k);
                _weights[g[i].index] += w * k;
                return 0;
            }
            var top = g[g.Count - 1];
            _weights[top.index] += w;
            rate = Mathf.Max(rate, Mathf.Min(1.5f, speed / top.speed));
            return 0;
        }
    }
}
